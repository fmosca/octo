using Microsoft.Extensions.Caching.Memory;
using Octo.Services.Common;
using Octo.Services.Tagging;
using System.Text.Json;

namespace Octo.Services.Fingerprint;

/// <summary>
/// One question for MusicBrainz, asked only when a person keeps a track AcoustID had never heard
/// of: which recording is this? That is the MusicBrainz id a confirmed fingerprint needs before
/// AcoustID will take it (#47). And one more, asked only while verifying a download whose
/// fingerprint named a recording that reads differently from the request: which ISRCs does
/// that recording carry?
///
/// MusicBrainz allows about one request a second and wants a User-Agent naming the application,
/// and it holds near-duplicate recordings (two "Teardrop" by Massive Attack, 27 ms apart), so an
/// answer is only returned when exactly one recording fits. Anything else is a guess, and a
/// guess is never submitted with someone's name on it.
/// </summary>
public sealed class MusicBrainzClient
{
    public const string ClientName = "musicbrainz";
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(1100);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<MusicBrainzClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCallUtc = DateTime.MinValue;

    /// <summary>Release lookups are remembered a day, since an album's tracks ask for the same
    /// release one by one; searches six hours. Every entry counts as one, so the limit is a count.</summary>
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 2000 });
    private static readonly TimeSpan ReleaseTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan SearchTtl = TimeSpan.FromHours(6);

    public MusicBrainzClient(IHttpClientFactory http, ILogger<MusicBrainzClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// One release in full: its label and catalogue number, barcode, status, country, date, its
    /// group's kind and first release date, every track's position and id, and the genres people
    /// voted on. Remembered a day, since an album's tracks ask one by one.
    /// </summary>
    public async Task<ReleaseDetails?> LookupReleaseAsync(string releaseId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(releaseId)) return null;
        var key = "release|" + releaseId.Trim().ToLowerInvariant();
        if (_cache.TryGetValue(key, out ReleaseDetails? cached)) return cached;

        using var doc = await GetAsync(
            $"release/{Uri.EscapeDataString(releaseId.Trim())}?inc=labels+release-groups+artist-credits+recordings+isrcs+genres&fmt=json",
            "release lookup", ct);
        if (doc is null) return null;
        var details = ReleaseDetails.Parse(doc.RootElement);
        if (details is not null)
            _cache.Set(key, details, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ReleaseTtl });
        return details;
    }

    /// <summary>
    /// Recordings by name and length, for a download the fingerprint service could not name. Up
    /// to 25, each with its releases and their groups. The answer is the caller's to dispose.
    /// </summary>
    public async Task<JsonDocument?> SearchRecordingsAsync(string artist, string title, int durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title)) return null;
        var key = "search|" + SongIdentity.MatchKey(artist, title) + "|" + durationSeconds;
        if (_cache.TryGetValue(key, out string? cachedJson) && cachedJson is not null) return JsonDocument.Parse(cachedJson);

        using var doc = await GetAsync(BuildRecordingSearchUrl(artist, title, durationSeconds), "recording search", ct);
        if (doc is null) return null;
        var json = doc.RootElement.GetRawText();
        _cache.Set(key, json, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = SearchTtl });
        return JsonDocument.Parse(json);
    }

    /// <summary>The recordings that carry one code, with their releases. The answer is the caller's to dispose.</summary>
    public async Task<JsonDocument?> LookupIsrcAsync(string isrc, CancellationToken ct)
    {
        if (SongIdentity.NormalizeIsrc(isrc) is not { } code) return null;
        var key = "isrc|" + code;
        if (_cache.TryGetValue(key, out string? cachedJson) && cachedJson is not null) return JsonDocument.Parse(cachedJson);

        using var doc = await GetAsync($"isrc/{code}?inc=artist-credits+releases+release-groups+media&fmt=json", "code lookup", ct);
        if (doc is null) return null;
        var json = doc.RootElement.GetRawText();
        _cache.Set(key, json, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = SearchTtl });
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// The release-group id of the pressing a barcode names. A barcode is shared by every
    /// edition of one release-group that kept it, so this is how two differently spelled
    /// listings of the same record learn they are the same record. "Not Found" and an empty
    /// hits list mean the database has nothing for it, which is remembered a few hours; a
    /// request that never completed an answer is not cached at all, so one throttled call
    /// cannot turn into six hours of "no such barcode".
    /// </summary>
    public async Task<string?> FindReleaseGroupByBarcodeAsync(string? barcode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return null;
        var code = barcode.Trim();
        if (code.Length is < 8 or > 14 || code.Any(c => !char.IsDigit(c))) return null;
        var key = "barcode|" + code;
        if (_cache.TryGetValue(key, out string? cached) && cached is not null) return cached;
        // Remembering the miss for this session too: a barcode with no group will not
        // grow one in an hour, and re-asking would spend the one-a-second budget.
        if (_cache.TryGetValue("nobc|" + code, out _)) return null;

        // Null here is "could not be asked" - down, throttled, unreadable - and is left
        // uncached. Only a parsed answer says anything about the barcode.
        using var doc = await GetAsync(
            $"release/?query=barcode:{Uri.EscapeDataString(code)}&fmt=json&limit=1", "barcode lookup", ct);
        if (doc is null) return null;
        string? groupId = null;
        if (doc.RootElement.TryGetProperty("releases", out var releases)
            && releases.ValueKind == JsonValueKind.Array && releases.GetArrayLength() > 0
            && releases[0].TryGetProperty("release-group", out var group))
            groupId = group.TryGetProperty("id", out var gid) ? gid.GetString() : null;
        if (groupId is null)
        {
            _cache.Set("nobc|" + code, "1", new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = SearchTtl });
            return null;
        }
        _cache.Set(key, groupId, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ReleaseTtl });
        return groupId;
    }

    /// <summary>
    /// The search for a recording by name, with a length window of ten seconds either way when
    /// the length is known. Every name goes through <see cref="EscapeQuery"/>, since a quote, a
    /// colon or a slash in a title would otherwise change what the query means and the failure
    /// would read as "no candidate".
    /// </summary>
    internal static string BuildRecordingSearchUrl(string artist, string title, int durationSeconds)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(title)) parts.Add($"recording:\"{EscapeQuery(title.Trim())}\"");
        if (!string.IsNullOrWhiteSpace(artist)) parts.Add($"artist:\"{EscapeQuery(artist.Trim())}\"");
        if (durationSeconds > 0)
        {
            var low = Math.Max(0, durationSeconds - 10) * 1000;
            var high = (durationSeconds + 10) * 1000;
            parts.Add($"dur:[{low} TO {high}]");
        }
        return $"recording/?query={Uri.EscapeDataString(string.Join(" AND ", parts))}&fmt=json&limit=25";
    }

    /// <summary>Every character the query language reads as an operator, made literal.</summary>
    internal static string EscapeQuery(string value)
    {
        const string special = "+-&|!(){}[]^\"~*?:\\/";
        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (special.Contains(ch)) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    public async Task<string?> FindRecordingAsync(string artist, string title, int durationSeconds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title) || durationSeconds <= 0) return null;

        var query = $"recording:\"{Escape(title)}\" AND artist:\"{Escape(artist)}\"";
        using var doc = await GetAsync($"recording/?query={Uri.EscapeDataString(query)}&fmt=json&limit=25", "recording search", ct);
        return doc is null ? null : Pick(doc.RootElement, artist, title, durationSeconds);
    }

    /// <summary>
    /// The ISRCs MusicBrainz lists for one recording: empty when it lists none, null when it
    /// could not be asked. One lookup by id with inc=isrcs, inside the same one-a-second budget
    /// as the search above.
    /// </summary>
    public async Task<IReadOnlyList<string>?> FetchIsrcsAsync(string recordingId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recordingId)) return null;
        using var doc = await GetAsync($"recording/{Uri.EscapeDataString(recordingId)}?inc=isrcs&fmt=json", "ISRC lookup", ct);
        return doc is null ? null : ParseIsrcs(doc.RootElement);
    }

    /// <summary>
    /// The release group of the oldest official studio album a song appears on, or of a
    /// soundtrack when no studio album has it. Null when MusicBrainz knows neither.
    /// </summary>
    public async Task<string?> FindStudioAlbumAsync(string artist, string title, CancellationToken ct)
    {
        var plainTitle = SongIdentity.StripFeatures(title);
        // Words, not a phrase: "They Dont Care About Us" has to find "They Don't Care About Us".
        var words = new string(plainTitle.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(artist) || words.Length == 0) return null;
        var query = $"recording:({words}) AND artist:\"{Escape(artist)}\" AND primarytype:album AND status:official";
        using var doc = await GetAsync($"recording/?query={Uri.EscapeDataString(query)}&fmt=json&limit=50", "studio album search", ct);
        return doc is null ? null : PickStudioAlbum(doc.RootElement, plainTitle);
    }

    internal static string? PickStudioAlbum(JsonElement root, string title)
    {
        if (!root.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
            return null;
        var wanted = SongIdentity.Key(title);
        string? groupId = null, groupDate = null;
        var groupRank = int.MaxValue;
        foreach (var recording in recordings.EnumerateArray())
        {
            if (!recording.TryGetProperty("title", out var recordingTitle)
                || SongIdentity.Key(recordingTitle.GetString()) != wanted
                || !recording.TryGetProperty("releases", out var releases)) continue;
            foreach (var release in releases.EnumerateArray())
            {
                if (!release.TryGetProperty("release-group", out var group)
                    || !group.TryGetProperty("primary-type", out var type) || type.GetString() != "Album") continue;
                var secondary = group.TryGetProperty("secondary-types", out var s) && s.ValueKind == JsonValueKind.Array
                    ? s.EnumerateArray().Select(x => x.GetString()).ToList() : [];
                var rank = secondary.Count == 0 ? 0 : secondary is ["Soundtrack"] ? 1 : -1;
                if (rank < 0 || rank > groupRank) continue;

                var date = release.TryGetProperty("date", out var d) && !string.IsNullOrEmpty(d.GetString())
                    ? d.GetString() : null;
                if (rank < groupRank || groupId is null
                    || (date is not null && (groupDate is null || string.CompareOrdinal(date, groupDate) < 0)))
                {
                    groupId = group.GetProperty("id").GetString();
                    groupDate = date;
                    groupRank = rank;
                }
            }
        }
        return groupId;
    }

    /// <summary>The "isrcs" list of a recording lookup, each one normalised; invalid ones dropped.</summary>
    internal static IReadOnlyList<string> ParseIsrcs(JsonElement root) =>
        root.TryGetProperty("isrcs", out var isrcs) && isrcs.ValueKind == JsonValueKind.Array
            ? isrcs.EnumerateArray()
                .Select(isrc => isrc.ValueKind == JsonValueKind.String ? SongIdentity.NormalizeIsrc(isrc.GetString()) : null)
                .OfType<string>().Distinct(StringComparer.Ordinal).ToList()
            : [];

    /// <summary>One request, spaced at least <see cref="MinimumGap"/> after the last. Null on any
    /// failure, which every caller reads as "MusicBrainz had nothing to say".</summary>
    private async Task<JsonDocument?> GetAsync(string relativeUrl, string what, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var gap = _lastCallUtc + MinimumGap - DateTime.UtcNow;
            if (gap > TimeSpan.Zero) await Task.Delay(gap, ct);
            _lastCallUtc = DateTime.UtcNow;

            using var response = await _http.CreateClient(ClientName).GetAsync(relativeUrl, ct);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("musicbrainz {What} failed: {M}", what, ex.Message);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The one recording that is this song, this version, by this artist, at this length.</summary>
    internal static string? Pick(JsonElement root, string artist, string title, int durationSeconds)
    {
        if (!root.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
            return null;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recording in recordings.EnumerateArray())
        {
            if (recording.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number
                && score.GetInt32() < 90) continue;
            if (recording.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.True) continue;
            if (!recording.TryGetProperty("length", out var length) || length.ValueKind != JsonValueKind.Number
                || Math.Abs(length.GetDouble() / 1000 - durationSeconds) > 3) continue;

            var name = recording.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var disambiguation = recording.TryGetProperty("disambiguation", out var d) ? d.GetString() ?? "" : "";
            // A live take or a remix says so in its disambiguation, not always in its title.
            var described = disambiguation.Length > 0 ? $"{name} ({disambiguation})" : name;
            if (!SongIdentity.SameTitle(title, described, SongIdentity.StrictTitles).IsSame) continue;

            var credits = recording.TryGetProperty("artist-credit", out var credit) && credit.ValueKind == JsonValueKind.Array
                ? credit.EnumerateArray().Select(entry => entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                    .Where(entry => entry.Length > 0).ToList()
                : [];
            if (!TrackMatchComparer.ArtistMatches(artist, string.Join(" & ", credits), credits)) continue;

            if (recording.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } recordingId)
                ids.Add(recordingId);
        }
        return ids.Count == 1 ? ids.First() : null;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
