using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;

namespace Octo.Services.Updates;

/// <summary>One dated server release, with the notes a person reads before updating.</summary>
public sealed record ReleaseNote(string Tag, string Name, string Notes, string Url, DateTime? PublishedUtc);

/// <summary>What the last check found, kept on disk so a restart answers without the network.</summary>
public sealed class ReleaseCheckState
{
    public string? Repo { get; set; }
    public DateTime? CheckedUtc { get; set; }
    public DateTime? AttemptedUtc { get; set; }
    public string? Error { get; set; }
    public string? ETag { get; set; }

    /// <summary>The server's releases, newest first.</summary>
    public List<ReleaseNote> Releases { get; set; } = [];
}

/// <summary>
/// What the dashboard shows about new releases. Standing is how the running build compares with
/// the newest release: "behind", "current", "ahead" (a build cut after it), or "unknown" (a build
/// that names no release, or no release known yet).
/// </summary>
public sealed record ReleaseCheckView(
    bool Enabled, string Repo, string Running, ReleaseNote? Latest, IReadOnlyList<ReleaseNote> Newer,
    bool UpdateAvailable, string Standing, DateTime? CheckedUtc, string? Error);

/// <summary>
/// Asks GitHub whether a newer Octo release is out: every 6 hours, and when someone presses
/// Check now. Reads the release list rather than "latest", because the repo also publishes the
/// apps' releases and "latest" is a flag a person sets by hand; only dated tags count. A failed
/// check keeps the last good answer and says why, and never throws.
/// </summary>
public sealed partial class ReleaseCheck : BackgroundService
{
    public const string ClientName = "github-releases";

    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Check now is honoured at most this often, so a busy button cannot spend GitHub's limit.</summary>
    public static readonly TimeSpan ManualSpacing = TimeSpan.FromMinutes(1);

    private const int KeptReleases = 10;
    private const int ShownNewer = 5;
    private const int MaxNotesLength = 20_000;

    private readonly string _path;
    private readonly IHttpClientFactory _http;
    private readonly IOptionsMonitor<UpdateSettings> _settings;
    private readonly ILogger<ReleaseCheck> _logger;
    private readonly Func<DateTime> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private ReleaseCheckState _state;

    public ReleaseCheck(string path, IHttpClientFactory http, IOptionsMonitor<UpdateSettings> settings,
        ILogger<ReleaseCheck> logger, Func<DateTime>? clock = null, string? running = null)
    {
        _path = path;
        _http = http;
        _settings = settings;
        _logger = logger;
        _clock = clock ?? (() => DateTime.UtcNow);
        Running = running ?? ReleaseVersion.Running;
        _state = Load();
    }

    /// <summary>The release this server runs, e.g. "2026.10.02".</summary>
    public string Running { get; }

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoPattern();

    public ReleaseCheckView View()
    {
        var settings = _settings.CurrentValue;
        ReleaseCheckState state;
        lock (_lock) state = _state;
        var repo = Repo(settings);
        // An answer about another repo says nothing about this one.
        var releases = string.Equals(state.Repo, repo, StringComparison.OrdinalIgnoreCase) ? state.Releases : [];
        var latest = releases.FirstOrDefault();
        var comparable = ReleaseVersion.TryParse(Running, out var running);
        var newer = comparable
            ? releases.Where(r => ReleaseVersion.TryParse(r.Tag, out var v) && v > running).Take(ShownNewer).ToList()
            // A local build that names no release cannot be compared, so it is never told it is behind.
            : [];
        var standing = !comparable || latest is null || !ReleaseVersion.TryParse(latest.Tag, out var newest) ? "unknown"
            : newer.Count > 0 ? "behind"
            : running > newest ? "ahead"
            : "current";
        return new ReleaseCheckView(settings.Check, repo, Running, latest, newer, newer.Count > 0, standing,
            state.CheckedUtc, state.Error);
    }

    /// <summary>Asks GitHub now, unless checks are off or a manual check ran a moment ago.</summary>
    public async Task<ReleaseCheckView> CheckAsync(bool manual, CancellationToken ct)
    {
        var settings = _settings.CurrentValue;
        if (!settings.Check) return View();
        await _gate.WaitAsync(ct);
        try
        {
            ReleaseCheckState state;
            lock (_lock) state = Copy(_state);
            var now = _clock();
            if (manual && state.AttemptedUtc is { } last && now - last < ManualSpacing) return View();

            var repo = Repo(settings);
            if (!string.Equals(state.Repo, repo, StringComparison.OrdinalIgnoreCase))
                state = new ReleaseCheckState { Repo = repo };
            state.AttemptedUtc = now;

            if (!RepoPattern().IsMatch(repo))
                state.Error = $"Updates:Repo should read owner/name, like winters27/octo, not \"{repo}\".";
            else
                await AskGitHubAsync(repo, state, ct);

            lock (_lock) _state = state;
            Save(state);
            return View();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AskGitHubAsync(string repo, ReleaseCheckState state, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases?per_page=30");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Octo", Sanitize(Running)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            // A 304 answer does not count against GitHub's limit.
            if (state.ETag is { } etag && state.Releases.Count > 0 && EntityTagHeaderValue.TryParse(etag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);

            using var response = await _http.CreateClient(ClientName).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                state.CheckedUtc = _clock();
                state.Error = null;
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                state.Error = Refusal(response);
                _logger.LogInformation("Release check for {Repo}: {Error}", repo, state.Error);
                return;
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            state.Releases = ServerReleases(document.RootElement);
            state.ETag = response.Headers.ETag?.ToString();
            state.CheckedUtc = _clock();
            state.Error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            state.Error = "Couldn't reach GitHub to look for a new release.";
            _logger.LogInformation("Release check for {Repo} failed: {Message}", repo, ex.Message);
        }
    }

    /// <summary>The dated, published, final releases in GitHub's answer, newest first.</summary>
    internal static List<ReleaseNote> ServerReleases(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("GitHub's answer was not a list of releases.");
        var found = new List<(ReleaseVersion Version, ReleaseNote Note)>();
        foreach (var release in root.EnumerateArray())
        {
            if (Bool(release, "draft") || Bool(release, "prerelease")) continue;
            var tag = Text(release, "tag_name");
            if (!ReleaseVersion.TryParse(tag, out var version) || tag!.Contains('+')) continue;
            var notes = Text(release, "body") ?? "";
            if (notes.Length > MaxNotesLength) notes = notes[..MaxNotesLength];
            var published = release.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String
                            && at.TryGetDateTime(out var time) ? time.ToUniversalTime() : (DateTime?)null;
            found.Add((version, new ReleaseNote(tag, Text(release, "name") is { Length: > 0 } name ? name : tag, notes,
                Text(release, "html_url") ?? "", published)));
        }
        return found.OrderByDescending(r => r.Version).Select(r => r.Note).Take(KeptReleases).ToList();
    }

    private static string Refusal(HttpResponseMessage response)
    {
        var limited = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                      && response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0";
        if (limited) return "GitHub's hourly limit for this address is used up. Octo tries again later.";
        if (response.StatusCode == HttpStatusCode.NotFound) return "GitHub has no such repository. Check Updates:Repo.";
        return $"GitHub answered {(int)response.StatusCode} when asked for releases.";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let the server finish starting before it talks to anyone.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                ReleaseCheckState state;
                lock (_lock) state = _state;
                var due = (state.AttemptedUtc ?? DateTime.MinValue) + Interval;
                if (_settings.CurrentValue.Check && (_clock() >= due
                        || !string.Equals(state.Repo, Repo(_settings.CurrentValue), StringComparison.OrdinalIgnoreCase)))
                    await CheckAsync(manual: false, stoppingToken);
                // Woken every few minutes, so turning checks on or changing the repo is noticed soon.
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static string Repo(UpdateSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Repo) ? "winters27/octo" : settings.Repo.Trim().Trim('/');

    // A product version token may hold only token characters.
    private static string Sanitize(string version) =>
        Regex.Replace(version, @"[^A-Za-z0-9.\-]", "") is { Length: > 0 } clean ? clean : "unknown";

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static ReleaseCheckState Copy(ReleaseCheckState state) => new()
    {
        Repo = state.Repo, CheckedUtc = state.CheckedUtc, AttemptedUtc = state.AttemptedUtc,
        Error = state.Error, ETag = state.ETag, Releases = [.. state.Releases],
    };

    private ReleaseCheckState Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<ReleaseCheckState>(File.ReadAllText(_path)) ?? new ReleaseCheckState();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read {Path}; the next check starts fresh: {Message}", _path, ex.Message);
        }
        return new ReleaseCheckState();
    }

    private void Save(ReleaseCheckState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not save {Path}: {Message}", _path, ex.Message);
        }
    }
}
