using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;

namespace Octo.Services.Soulseek;

/// <summary>
/// Thin HTTP client for slskd's REST API. Handles auth and the small set of
/// endpoints Octo needs: search, browse responses, enqueue download, poll status.
/// </summary>
public class SoulseekClient
{
    private readonly HttpClient _http;
    private readonly SoulseekSettings _settings;
    private readonly ILogger<SoulseekClient> _logger;

    private string? _jwt;
    private DateTime _jwtExpiresUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    public SoulseekClient(
        IHttpClientFactory httpClientFactory,
        IOptions<SoulseekSettings> settings,
        ILogger<SoulseekClient> logger)
    {
        _http = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    private string Base => (_settings.BaseUrl ?? "http://localhost:5030").TrimEnd('/');

    /// <summary>
    /// Fetches and caches a JWT from slskd's session endpoint. Re-authenticates
    /// when the cached token is missing or near expiry.
    /// </summary>
    private async Task<string?> GetJwtAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_jwt) && DateTime.UtcNow < _jwtExpiresUtc.AddMinutes(-1))
            return _jwt;

        await _authLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_jwt) && DateTime.UtcNow < _jwtExpiresUtc.AddMinutes(-1))
                return _jwt;

            if (string.IsNullOrWhiteSpace(_settings.Username) || string.IsNullOrWhiteSpace(_settings.Password))
            {
                _logger.LogWarning("Soulseek__Username/Password not set; cannot authenticate to slskd");
                return null;
            }

            var body = JsonSerializer.Serialize(new { username = _settings.Username, password = _settings.Password });
            using var resp = await _http.PostAsync(
                $"{Base}/api/v0/session",
                new StringContent(body, Encoding.UTF8, "application/json"),
                ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("slskd auth failed: HTTP {Code}", (int)resp.StatusCode);
                _jwt = null;
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            _jwt = doc.RootElement.GetProperty("token").GetString();
            _jwtExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(
                doc.RootElement.GetProperty("expires").GetInt64()).UtcDateTime;
            return _jwt;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<HttpRequestMessage> AuthedRequestAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        var jwt = await GetJwtAsync(ct);
        if (!string.IsNullOrEmpty(jwt))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return req;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        var req = await AuthedRequestAsync(method, url, ct);
        if (content != null) req.Content = content;
        var resp = await _http.SendAsync(req, ct);

        // If the JWT was rejected (e.g. rotated key), refresh once and retry.
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _jwt = null;
            using var retryReq = await AuthedRequestAsync(method, url, ct);
            if (content != null) retryReq.Content = content;
            resp.Dispose();
            return await _http.SendAsync(retryReq, ct);
        }
        return resp;
    }

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, $"{Base}/api/v0/application", null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("slskd not reachable at {Base}: {Msg}", Base, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Reads slskd's resolved downloads directory from /api/v0/options. Purely a
    /// diagnostic: a null (endpoint missing, redacted, or unexpected shape) must
    /// never gate anything.
    /// </summary>
    public async Task<string?> GetDownloadsDirectoryAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, $"{Base}/api/v0/options", null, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!TryGetPropertyIgnoreCase(doc.RootElement, "directories", out var dirs)) return null;
            if (!TryGetPropertyIgnoreCase(dirs, "downloads", out var downloads)) return null;
            return downloads.ValueKind == JsonValueKind.String ? downloads.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not read slskd options: {Msg}", ex.Message);
            return null;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (element.TryGetProperty(name, out value)) return true;
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Initiates a search and returns as soon as there is an answer, rather than
    /// always sitting out a fixed wait.
    ///
    /// Three things can end the wait, whichever comes first:
    ///   1. <paramref name="enough"/> says the hits so far are already worth acting on,
    ///   2. slskd reports the search finished, so nothing more is coming,
    ///   3. SearchWaitSeconds elapses, which is a ceiling rather than a duration.
    ///
    /// This matters in both directions. Peer responses arrive in a burst around the
    /// 20s mark, so a fixed wait either cuts the search off before its results exist
    /// or idles long after they have arrived; and a search that comes back empty
    /// should fall through to the fallback source immediately instead of making the
    /// user wait out a timer for an answer that is already known.
    /// </summary>
    /// <param name="enough">
    /// Decides whether the hits gathered so far are worth committing to. The client
    /// cannot judge this itself: "usable" means the right format, size and title
    /// match, which only the caller's ranking knows. Null means wait for completion
    /// or the ceiling.
    /// </param>
    public async Task<List<SoulseekFileHit>> SearchAsync(
        string query,
        int limit,
        CancellationToken ct = default,
        Func<IReadOnlyList<SoulseekFileHit>, bool>? enough = null)
    {
        var searchId = Guid.NewGuid().ToString();
        var payload = JsonSerializer.Serialize(new
        {
            id = searchId,
            searchText = query,
            fileLimit = Math.Max(limit * 5, 50),
            filterResponses = true
        });

        try
        {
            using var startResp = await SendAsync(
                HttpMethod.Post,
                $"{Base}/api/v0/searches",
                new StringContent(payload, Encoding.UTF8, "application/json"),
                ct);
            startResp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Soulseek search start failed: {Msg}", ex.Message);
            return new List<SoulseekFileHit>();
        }

        var hits = new List<SoulseekFileHit>();
        var ceiling = DateTime.UtcNow.AddSeconds(_settings.SearchWaitSeconds);
        var started = DateTime.UtcNow;
        var pollIntervalMs = 1000;
        string stopReason = "ceiling";

        while (DateTime.UtcNow < ceiling && !ct.IsCancellationRequested)
        {
            await Task.Delay(pollIntervalMs, ct);
            try
            {
                using var resp = await SendAsync(
                    HttpMethod.Get,
                    $"{Base}/api/v0/searches/{searchId}/responses",
                    null,
                    ct);
                if (!resp.IsSuccessStatusCode) continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                hits = ParseResponses(json);

                if (hits.Count >= limit) { stopReason = "hit limit"; break; }

                // Good enough to act on: stop waiting and go download it.
                if (enough != null && enough(hits)) { stopReason = "found what we needed"; break; }

                // Nothing more is coming. Returning now means an empty search falls
                // through to the fallback source immediately instead of idling out
                // the ceiling for an answer that is already settled.
                if (await IsSearchFinishedAsync(searchId, ct))
                {
                    stopReason = "search finished";
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Poll failed (transient): {Msg}", ex.Message);
            }
        }

        _logger.LogInformation(
            "Soulseek search '{Query}': {Count} hits after {Elapsed:F1}s ({Reason})",
            query, hits.Count, (DateTime.UtcNow - started).TotalSeconds, stopReason);

        // Fire-and-forget cleanup so we don't accumulate completed searches
        _ = Task.Run(async () =>
        {
            try { using var _ = await SendAsync(HttpMethod.Delete, $"{Base}/api/v0/searches/{searchId}", null, CancellationToken.None); }
            catch { /* best effort */ }
        });

        return hits;
    }

    private List<SoulseekFileHit> ParseResponses(string json)
    {
        var hits = new List<SoulseekFileHit>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return hits;

            foreach (var resp in doc.RootElement.EnumerateArray())
            {
                if (!resp.TryGetProperty("username", out var unameEl)) continue;
                var username = unameEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(username)) continue;

                if (!resp.TryGetProperty("files", out var filesEl) ||
                    filesEl.ValueKind != JsonValueKind.Array) continue;

                int? uploadSpeed = resp.TryGetProperty("uploadSpeed", out var spEl) && spEl.ValueKind == JsonValueKind.Number
                    ? spEl.GetInt32()
                    : null;
                int? queueLength = resp.TryGetProperty("queueLength", out var qlEl) && qlEl.ValueKind == JsonValueKind.Number
                    ? qlEl.GetInt32()
                    : null;

                foreach (var file in filesEl.EnumerateArray())
                {
                    var filename = file.TryGetProperty("filename", out var fnEl) ? fnEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(filename)) continue;

                    long size = file.TryGetProperty("size", out var szEl) && szEl.ValueKind == JsonValueKind.Number
                        ? szEl.GetInt64()
                        : 0;
                    int? bitRate = file.TryGetProperty("bitRate", out var brEl) && brEl.ValueKind == JsonValueKind.Number
                        ? brEl.GetInt32()
                        : null;
                    int? sampleRate = file.TryGetProperty("sampleRate", out var srEl) && srEl.ValueKind == JsonValueKind.Number
                        ? srEl.GetInt32()
                        : null;
                    int? bitDepth = file.TryGetProperty("bitDepth", out var bdEl) && bdEl.ValueKind == JsonValueKind.Number
                        ? bdEl.GetInt32()
                        : null;
                    int? length = file.TryGetProperty("length", out var lenEl) && lenEl.ValueKind == JsonValueKind.Number
                        ? lenEl.GetInt32()
                        : null;
                    var ext = file.TryGetProperty("extension", out var exEl) ? exEl.GetString() : null;

                    hits.Add(new SoulseekFileHit
                    {
                        Username = username,
                        Filename = filename,
                        Size = size,
                        BitRate = bitRate,
                        SampleRate = sampleRate,
                        BitDepth = bitDepth,
                        Length = length,
                        Extension = NormalizeExtension(ext, filename),
                        UploadSpeed = uploadSpeed,
                        QueueLength = queueLength
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse Soulseek responses: {Msg}", ex.Message);
        }
        return hits;
    }

    /// <summary>
    /// <summary>
    /// True once slskd says the search has stopped gathering responses.
    ///
    /// Deliberately reads the STATUS object rather than inferring completion from
    /// the responses endpoint, and deliberately is not used to decide whether
    /// results exist: status reports a responseCount well before /responses will
    /// return the files, so trusting it for anything except "is it over" makes a
    /// too-short wait look perfectly healthy.
    ///
    /// Any failure returns false, so an unreadable status simply means the caller
    /// keeps polling until the ceiling rather than giving up early.
    /// </summary>
    private async Task<bool> IsSearchFinishedAsync(string searchId, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, $"{Base}/api/v0/searches/{searchId}", null, ct);
            if (!resp.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("state", out var stateEl)) return false;

            // slskd reports compound states such as "Completed, TimedOut" or
            // "Completed, ResponseLimitReached"; all of them mean it is done.
            var state = stateEl.GetString() ?? "";
            return state.Contains("Completed", StringComparison.OrdinalIgnoreCase)
                || state.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)
                || state.Contains("Errored", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reduce a file extension to the bare lowercase form ("flac").
    ///
    /// Candidate ranking accepts a hit by comparing this against the configured
    /// PreferredExtension, so the two have to agree on shape. slskd does not
    /// guarantee one: some builds report "flac", some report ".flac", and some
    /// omit the field entirely and leave only the filename to go on. An
    /// unnormalized leading dot compared against a bare "flac" matches nothing,
    /// which reads downstream as "this track is not on Soulseek" rather than as
    /// a parsing mismatch. Both sides of the comparison run through here.
    /// </summary>
    internal static string NormalizeExtension(string? extension, string filename)
    {
        var raw = string.IsNullOrWhiteSpace(extension) ? Path.GetExtension(filename) : extension;
        return (raw ?? "").Trim().TrimStart('.').ToLowerInvariant();
    }

    /// <summary>
    /// Enqueues a download from a specific peer. Returns when the request is accepted by slskd
    /// (not when the file is fully transferred — caller polls for that).
    /// </summary>
    public async Task EnqueueDownloadAsync(string username, string filename, long size, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new[]
        {
            new { filename, size }
        });

        using var resp = await SendAsync(
            HttpMethod.Post,
            $"{Base}/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"slskd download enqueue failed: HTTP {(int)resp.StatusCode} {err}");
        }
    }

    /// <summary>
    /// Polls a download to completion. Returns Succeeded on success or Errored
    /// on any kind of slskd-side failure (peer rejected, timed out, cancelled,
    /// or the transfer silently disappeared from slskd's active list — which
    /// happens after rejection on some slskd versions and would otherwise hang
    /// us forever). Caller decides whether to retry or escalate.
    ///
    /// <paramref name="onProgress"/> hears the transfer's byte counts on every poll that finds
    /// it. It only listens: the cadence, the deadline and the outcome are the same without it,
    /// and anything it throws is swallowed here.
    /// </summary>
    public async Task<SoulseekTransferState> WaitForCompletionAsync(string username, string filename, int? perAttemptTimeoutSeconds = null, CancellationToken ct = default,
        Action<SoulseekTransferProgress>? onProgress = null)
    {
        var timeoutSec = perAttemptTimeoutSeconds ?? _settings.DownloadTimeoutSeconds;
        var watch = new TransferWatch(DateTime.UtcNow, TimeSpan.FromSeconds(timeoutSec), MaxTransferTime);
        var seenAtLeastOnce = false;
        var consecutiveMisses = 0;
        // After we've seen the transfer at least once, missing it for this many
        // consecutive polls means slskd dropped it and we should give up. Some
        // slskd versions remove rejected transfers from the active-list endpoint
        // immediately, so without this we'd poll forever.
        const int MaxConsecutiveMissesAfterSeen = 6;  // ~9s at 1500ms cadence

        while (!watch.Expired(DateTime.UtcNow) && !ct.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, ct);

            try
            {
                using var resp = await SendAsync(
                    HttpMethod.Get,
                    $"{Base}/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}",
                    null,
                    ct);
                if (!resp.IsSuccessStatusCode)
                {
                    if (seenAtLeastOnce) consecutiveMisses++;
                    if (consecutiveMisses >= MaxConsecutiveMissesAfterSeen)
                    {
                        _logger.LogWarning("slskd transfer disappeared after rejection (no longer queryable): {File}", filename);
                        return SoulseekTransferState.Errored;
                    }
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var transfer = FindTransfer(doc.RootElement, filename);
                var state = transfer is { } found ? StateOf(found) : null;
                bool foundThisPoll = state is not null;
                if (foundThisPoll)
                {
                    seenAtLeastOnce = true;
                    consecutiveMisses = 0;

                    var progress = ReadTransferProgress(transfer!.Value);
                    watch.Saw(progress.BytesTransferred, DateTime.UtcNow);
                    if (onProgress is not null)
                    {
                        try { onProgress(progress); }
                        catch (Exception ex) { _logger.LogDebug("Transfer progress listener failed: {Msg}", ex.Message); }
                    }

                    if (state!.Contains("Completed", StringComparison.OrdinalIgnoreCase) &&
                        state.Contains("Succeeded", StringComparison.OrdinalIgnoreCase))
                    {
                        return SoulseekTransferState.Succeeded;
                    }
                    if (state.Contains("Errored", StringComparison.OrdinalIgnoreCase) ||
                        state.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                        state.Contains("Rejected", StringComparison.OrdinalIgnoreCase) ||
                        state.Contains("TimedOut", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("slskd transfer ended in state: {State}", state);
                        return SoulseekTransferState.Errored;
                    }
                }

                if (!foundThisPoll && seenAtLeastOnce)
                {
                    consecutiveMisses++;
                    if (consecutiveMisses >= MaxConsecutiveMissesAfterSeen)
                    {
                        _logger.LogWarning("slskd transfer disappeared from active list (rejection or cleanup): {File}", filename);
                        return SoulseekTransferState.Errored;
                    }
                }
            }
            catch (Exception ex) when (ex is not TaskCanceledException)
            {
                _logger.LogDebug("Transfer poll transient: {Msg}", ex.Message);
            }
        }

        ct.ThrowIfCancellationRequested();

        // Giving up on this peer. Without a cancel slskd keeps the transfer going, and
        // a file that lands after the next peer's copy is a second copy in the library.
        if (await CancelTransferAsync(username, filename) == SoulseekTransferState.Succeeded)
        {
            _logger.LogInformation("slskd transfer finished just as it was given up: {File}", filename);
            return SoulseekTransferState.Succeeded;
        }
        if (watch.HitCeiling(DateTime.UtcNow))
            _logger.LogWarning("slskd transfer still not done after {Min} minutes; cancelled: {File}",
                (int)MaxTransferTime.TotalMinutes, filename);
        else
            _logger.LogWarning("slskd transfer timed out: nothing new for {Sec}s; cancelled: {File}", timeoutSec, filename);
        return SoulseekTransferState.Errored;
    }

    /// <summary>
    /// The longest a transfer that keeps moving is waited for. A slow peer with the right
    /// file is worth waiting on; one that trickles for an hour is not.
    /// </summary>
    internal static readonly TimeSpan MaxTransferTime = TimeSpan.FromMinutes(60);

    /// <summary>How often a transfer is polled. Only tests shorten it.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Cancels a download in slskd and removes it from its list, so it can never land.
    /// Answers Succeeded instead when the transfer turns out to have just finished, and
    /// Errored otherwise, including when slskd cannot be asked.
    /// </summary>
    public async Task<SoulseekTransferState> CancelTransferAsync(string username, string filename)
    {
        var user = Uri.EscapeDataString(username);
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, $"{Base}/api/v0/transfers/downloads/{user}", null, CancellationToken.None);
            if (!resp.IsSuccessStatusCode) return SoulseekTransferState.Errored;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (FindTransfer(doc.RootElement, filename) is not { } file) return SoulseekTransferState.Errored;
            var state = StateOf(file);
            if (state.Contains("Completed", StringComparison.OrdinalIgnoreCase) &&
                state.Contains("Succeeded", StringComparison.OrdinalIgnoreCase))
                return SoulseekTransferState.Succeeded;
            if (TransferId(file) is not { } id) return SoulseekTransferState.Errored;
            using var cancel = await SendAsync(HttpMethod.Delete,
                $"{Base}/api/v0/transfers/downloads/{user}/{Uri.EscapeDataString(id)}?remove=true", null, CancellationToken.None);
            if (!cancel.IsSuccessStatusCode)
                _logger.LogWarning("slskd refused to cancel {File}: HTTP {Code}", filename, (int)cancel.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not cancel slskd transfer {File}: {Msg}", filename, ex.Message);
        }
        return SoulseekTransferState.Errored;
    }

    /// <summary>The id slskd gives a transfer, for cancelling it.</summary>
    internal static string? TransferId(JsonElement file) =>
        file.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    /// <summary>
    /// Finds a transfer's state in an slskd downloads response, or null when the
    /// file is not present. The per-user endpoint returns a single
    /// {username, directories} object, while the all-users endpoint returns an
    /// array of them; both shapes are accepted. Reading the wrong shape is what
    /// made every completed transfer look like a timeout: the poll loop rejected
    /// the object response wholesale and rode the per-attempt timer to the end.
    /// </summary>
    internal static string? FindTransferState(JsonElement root, string filename) =>
        FindTransfer(root, filename) is { } file ? StateOf(file) : null;

    private static string StateOf(JsonElement file) =>
        file.TryGetProperty("state", out var stEl) ? stEl.GetString() ?? "" : "";

    /// <summary>
    /// What slskd says a transfer has moved so far. Any field it leaves out, or sends as
    /// something other than a number, is null rather than zero, so a missing size never reads
    /// as a finished file.
    /// </summary>
    internal static SoulseekTransferProgress ReadTransferProgress(JsonElement file)
    {
        static long? Long(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
                ? n : null;
        static double? Double(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)
                ? n : null;
        return new SoulseekTransferProgress(StateOf(file),
            Long(file, "bytesTransferred"), Long(file, "size"), Double(file, "percentComplete"));
    }

    /// <summary>The file object for a transfer, in either response shape, or null.</summary>
    internal static JsonElement? FindTransfer(JsonElement root, string filename)
    {
        IEnumerable<JsonElement> userGroups = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray(),
            JsonValueKind.Object => new[] { root },
            _ => Array.Empty<JsonElement>(),
        };

        foreach (var userGroup in userGroups)
        {
            if (userGroup.ValueKind != JsonValueKind.Object) continue;
            if (!userGroup.TryGetProperty("directories", out var dirs)) continue;
            if (dirs.ValueKind != JsonValueKind.Array) continue;
            foreach (var dir in dirs.EnumerateArray())
            {
                if (!dir.TryGetProperty("files", out var files)) continue;
                if (files.ValueKind != JsonValueKind.Array) continue;
                foreach (var file in files.EnumerateArray())
                {
                    var fn = file.TryGetProperty("filename", out var fnEl) ? fnEl.GetString() : null;
                    if (fn != filename) continue;
                    return file;
                }
            }
        }
        return null;
    }
}

public class SoulseekFileHit
{
    public string Username { get; set; } = "";
    public string Filename { get; set; } = "";
    public long Size { get; set; }
    public int? BitRate { get; set; }
    public int? SampleRate { get; set; }
    public int? BitDepth { get; set; }
    public int? Length { get; set; }
    public string Extension { get; set; } = "";
    public int? UploadSpeed { get; set; }
    public int? QueueLength { get; set; }
}

/// <summary>
/// How long to keep waiting on one transfer. Each time more bytes have arrived, the
/// quiet window starts again, so a slow peer that keeps sending is waited for. A
/// transfer with nothing new for the whole window, or still unfinished at the ceiling,
/// is given up on.
/// </summary>
internal sealed class TransferWatch(DateTime started, TimeSpan quiet, TimeSpan ceiling)
{
    private DateTime _quietSince = started;
    private long _bytes;

    public void Saw(long? bytes, DateTime now)
    {
        if (bytes is not { } b || b <= _bytes) return;
        _bytes = b;
        _quietSince = now;
    }

    public bool HitCeiling(DateTime now) => now - started >= ceiling;

    public bool Expired(DateTime now) => now - _quietSince >= quiet || HitCeiling(now);
}

/// <summary>One poll's view of a transfer. PercentComplete is slskd's own, from 0 to 100.</summary>
public sealed record SoulseekTransferProgress(
    string State, long? BytesTransferred, long? Size, double? PercentComplete)
{
    /// <summary>
    /// Bytes are flowing, or have. A transfer waiting in the peer's queue reads "Queued,
    /// Remotely" with nothing moved, and that is still a wait, not a download.
    /// </summary>
    public bool IsMoving =>
        BytesTransferred is > 0
        || State.Contains("InProgress", StringComparison.OrdinalIgnoreCase)
        || State.Contains("Succeeded", StringComparison.OrdinalIgnoreCase);
}

public enum SoulseekTransferState
{
    Succeeded,
    Errored
}
