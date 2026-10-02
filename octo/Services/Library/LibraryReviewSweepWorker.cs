using System.Text.Json;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.CoverArt;
using Octo.Services.Fingerprint;
using Octo.Services.Local;

namespace Octo.Services.Library;

/// <summary>The one question the sweep asks of a file, behind a seam so tests need no fpcalc.</summary>
public interface IReviewSweepVerifier
{
    bool IsReady { get; }
    Task<VerificationResult> VerifyAsync(string path, string? artist, string? title);
}

/// <summary>The real check, in AcoustID's background lane. Resolved late, because hosted
/// services are built before the download service is first used.</summary>
public sealed class FingerprintSweepVerifier(IServiceProvider services) : IReviewSweepVerifier
{
    private DownloadVerificationService? Service => services.GetService<DownloadVerificationService>();
    public bool IsReady => Service is { IsFingerprintingEnabled: true };

    // No ISRC: the file's own tag held against itself proves nothing, and WithTaggedIsrc would
    // turn a lookup AcoustID never answered into a confirmation.
    public Task<VerificationResult> VerifyAsync(string path, string? artist, string? title) =>
        Service is { } service
            ? AcoustIdRateLimiter.InBackgroundAsync(() => service.VerifyAsync(path, artist, title))
            : Task.FromResult(VerificationResult.Inconclusive);
}

public sealed class ReviewSweepState
{
    public bool Paused { get; set; }
    /// <summary>Library-relative path of the last file dealt with this pass; empty at its start.</summary>
    public string Cursor { get; set; } = "";
    /// <summary>Library-relative path to the size and modified time it had when checked. Relative,
    /// so a mount moving does not forget everything; stamped, so a replaced file is checked again.</summary>
    public Dictionary<string, string> Checked { get; set; } = new(StringComparer.Ordinal);
    public int Pass { get; set; } = 1;
    public int Total { get; set; }
    public int Found { get; set; }
    public int Fine { get; set; }
    public int Undecodable { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? PassFinishedUtc { get; set; }
    public DateTime NextPassUtc { get; set; }
}

/// <summary>review-sweep.json. Written whole on each change; changes are a file a minute or so.</summary>
public sealed class ReviewSweepStore
{
    private readonly string? _path;
    private readonly ILogger<ReviewSweepStore>? _logger;
    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private ReviewSweepState _state = new();

    public ReviewSweepStore(string? path = null, ILogger<ReviewSweepStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _logger = logger;
        if (_path is null || !File.Exists(_path)) return;
        try { _state = JsonSerializer.Deserialize<ReviewSweepState>(File.ReadAllText(_path)) ?? new(); }
        catch (Exception ex)
        {
            // Kept aside: losing it only means checking the library again, but it should be seen.
            _logger?.LogWarning("review sweep state could not be read ({M}); starting over", ex.Message);
            try { File.Move(_path, $"{_path}.corrupt-{DateTime.UtcNow.Ticks}"); } catch { /* best effort */ }
        }
    }

    public T Read<T>(Func<ReviewSweepState, T> read) { lock (_lock) return read(_state); }

    public void Update(Action<ReviewSweepState> change)
    {
        string json;
        lock (_lock) { change(_state); json = JsonSerializer.Serialize(_state); }
        if (_path is null) return;
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path + ".tmp", json);
                File.Move(_path + ".tmp", _path, overwrite: true);
            }
            catch (Exception ex) { _logger?.LogWarning("review sweep state could not be written: {M}", ex.Message); }
        }
    }
}

public sealed record ReviewSweepStatus(string State, string? Reason, bool Paused, int Position, int Total,
    int Pass, int Found, int Open, int Fine, int Undecodable, string? Keeper, int PerHour,
    DateTime? LastCheckedUtc, DateTime? PassFinishedUtc);

/// <summary>
/// Asks about music that was already in the library (#72), the way Review asks about a download
/// (#47): a few files an hour, only while nothing is downloading, only of whoever keeps the
/// library, and never more than <see cref="MaxOpenQuestions"/> open at once. It never acts on a
/// file. A confident "this is something else" is a question here, not a deletion.
/// </summary>
public sealed class LibraryReviewSweepWorker : BackgroundService
{
    internal const int MaxOpenQuestions = 50;
    internal const int MaxLookupFailuresPerFile = 3;
    internal const int MaxUnfingerprintedInARow = 5;
    internal static readonly TimeSpan IdleCheck = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan BusyCheck = TimeSpan.FromSeconds(30);
    /// <summary>After a full pass, how long before the library is walked again for new or changed files.</summary>
    internal static readonly TimeSpan PassInterval = TimeSpan.FromHours(6);

    internal enum Outcome { Fine, Ask, Undecodable, NotFingerprinted, LookupFailed }

    private readonly ReviewSweepStore _store;
    private readonly NoticeQueue _notices;
    private readonly IReviewSweepVerifier _verifier;
    private readonly IAcquisitionActivity _activity;
    private readonly ILocalLibraryService _library;
    private readonly IOptionsMonitor<LibraryActionSettings> _settings;
    private readonly IOptionsMonitor<SubsonicSettings> _subsonic;
    private readonly Func<string> _musicRoot;
    private readonly ILogger<LibraryReviewSweepWorker> _logger;
    private readonly TimeProvider _time;

    private volatile List<string>? _files;
    private HashSet<string> _octoFiles = new(StringComparer.Ordinal);
    private int _generation, _lookupFailures, _unfingerprinted;
    private volatile string _state = "Off";
    private volatile string? _reason;

    public LibraryReviewSweepWorker(ReviewSweepStore store, NoticeQueue notices, IReviewSweepVerifier verifier,
        IAcquisitionActivity activity, ILocalLibraryService library, IOptionsMonitor<LibraryActionSettings> settings,
        IOptionsMonitor<SubsonicSettings> subsonic, Func<string> musicRoot,
        ILogger<LibraryReviewSweepWorker> logger, TimeProvider? time = null)
    {
        _store = store; _notices = notices; _verifier = verifier; _activity = activity; _library = library;
        _settings = settings; _subsonic = subsonic; _musicRoot = musicRoot; _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Never alongside startup, which already works the disk and Navidrome.
        try { await Task.Delay(TimeSpan.FromMinutes(2), _time, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            // Per-tick catch is mandatory: an unhandled exception would stop the host.
            try { wait = await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Library review sweep failed"); wait = IdleCheck; }
            try { await Task.Delay(wait, _time, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    internal async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        var settings = _settings.CurrentValue;
        var perHour = settings.EffectiveReviewSweepPerHour;
        if (!settings.Enabled || !settings.ReviewEnabled || perHour == 0)
            return Hold("Off", "Set how many songs an hour to check, with library actions and Review on.", IdleCheck);
        if (_store.Read(s => s.Paused)) return Hold("Paused", "Paused from the dashboard.", IdleCheck);
        if (!_verifier.IsReady)
            return Hold("Paused", "AcoustID is not set up: turn on download verification and add a key.", IdleCheck);
        if (Keeper(settings, _subsonic.CurrentValue.AdminUsername) is not { } keeper)
            return Hold("Paused", "Nobody is on the library actions allowlist to ask.", IdleCheck);
        var open = _notices.OpenCount(NoticeOrigin.LibrarySweep);
        if (open >= MaxOpenQuestions)
            return Hold("Paused", $"{open} library questions are waiting in Review. Answer some and it carries on.", IdleCheck);
        if (_activity.IsBusy) return Hold("Waiting", "Waiting for a download to finish.", BusyCheck);

        var now = _time.GetUtcNow().UtcDateTime;
        if (_files is null && _store.Read(s => s.Cursor.Length == 0 && s.NextPassUtc > now))
            return Hold("Done", "Every song has been checked. New or changed ones are looked for later.", IdleCheck);

        var generation = Volatile.Read(ref _generation);
        var root = _musicRoot();
        if (_files is null)
        {
            if (!Directory.Exists(root)) return Hold("Paused", "The music folder cannot be read.", IdleCheck);
            _files = Enumerate(root);
            _octoFiles = settings.ReviewSweepOctoDownloads ? new(StringComparer.Ordinal)
                : (await _library.GetMappingsAsync()).Select(mapping => mapping.LocalPath)
                    .Where(path => !string.IsNullOrEmpty(path)).Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
            var total = _files.Count;
            Record(generation, s => s.Total = total);
        }

        var files = _files;
        var reviewed = _notices.ReviewedPaths();
        var (cursor, done) = _store.Read(s => (s.Cursor, new Dictionary<string, string>(s.Checked, StringComparer.Ordinal)));
        string? target = null, stamp = null, passed = cursor;
        for (var i = FirstAfter(files, cursor); i < files.Count && target is null; i++)
        {
            var full = FullPath(root, files[i]);
            var info = new FileInfo(full);
            var fileStamp = info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : null;
            if (fileStamp is null || _octoFiles.Contains(full) || reviewed.Contains(full)
                || (done.TryGetValue(files[i], out var seen) && seen == fileStamp))
            { passed = files[i]; continue; }
            (target, stamp) = (files[i], fileStamp);
        }

        if (target is null)
        {
            var present = files.ToHashSet(StringComparer.Ordinal);
            Record(generation, s =>
            {
                foreach (var gone in s.Checked.Keys.Where(key => !present.Contains(key)).ToList()) s.Checked.Remove(gone);
                s.Cursor = ""; s.Pass++; s.PassFinishedUtc = now; s.NextPassUtc = now + PassInterval;
            });
            _files = null;
            _logger.LogInformation("Library review sweep finished a pass over {Count} file(s)", files.Count);
            return Hold("Done", "Every song has been checked. New or changed ones are looked for later.", IdleCheck);
        }
        if (passed != cursor) Record(generation, s => s.Cursor = passed!);

        var path = FullPath(root, target);
        var song = ReadTags(path);
        var verdict = await _verifier.VerifyAsync(path, song.Artist, song.Title);
        var interval = TimeSpan.FromSeconds(3600.0 / perHour);
        if (generation != Volatile.Read(ref _generation)) return interval;

        var (outcome, question) = Classify(verdict);
        switch (outcome)
        {
            case Outcome.LookupFailed when ++_lookupFailures < MaxLookupFailuresPerFile:
                return Hold("Waiting", "AcoustID did not answer; trying that song again.", interval);
            case Outcome.LookupFailed:
                _lookupFailures = 0;
                Record(generation, s => s.Cursor = target);
                return Hold("Running", null, interval);
            case Outcome.NotFingerprinted:
                Record(generation, s => s.Cursor = target);
                if (++_unfingerprinted < MaxUnfingerprintedInARow) return Hold("Running", null, interval);
                _unfingerprinted = 0;
                return Hold("Paused", "fpcalc could not read five songs in a row. Is the music folder readable?", TimeSpan.FromHours(1));
        }

        _lookupFailures = 0;
        _unfingerprinted = 0;
        var asked = outcome == Outcome.Ask && _notices.AddReview(keeper, path, song, question, NoticeOrigin.LibrarySweep);
        if (asked)
            _logger.LogInformation("Asking {User} about '{Artist} - {Title}' from the library: {Reason}",
                keeper, song.Artist, song.Title, question.Reason);
        Record(generation, s =>
        {
            s.Cursor = target;
            s.Checked[target] = stamp!;
            s.LastCheckedUtc = now;
            if (asked) s.Found++;
            else if (outcome == Outcome.Undecodable) s.Undecodable++;
            else s.Fine++;
        });
        return Hold("Running", null, interval);
    }

    internal static (Outcome, VerificationResult) Classify(VerificationResult verdict) => verdict.Verdict switch
    {
        VerificationVerdict.Confirmed when IsLengthOff(verdict.DurationSeconds, verdict.Match?.DurationSeconds) =>
            (Outcome.Ask, verdict with { Reason = InconclusiveReason.LengthOff }),
        VerificationVerdict.Confirmed => (Outcome.Fine, verdict),
        // No recording id is "no decodable audio". On a network mount that can be a read that
        // failed this once, so it is counted and never asked about.
        VerificationVerdict.Mismatch when string.IsNullOrEmpty(verdict.RecordingId) => (Outcome.Undecodable, verdict),
        VerificationVerdict.Mismatch => (Outcome.Ask, verdict with { Reason = InconclusiveReason.SoundsLikeAnother }),
        _ when verdict.NeedsReview => (Outcome.Ask, verdict),
        _ when verdict.Reason == InconclusiveReason.NotFingerprinted => (Outcome.NotFingerprinted, verdict),
        _ => (Outcome.LookupFailed, verdict),
    };

    /// <summary>
    /// Off by more than 20 seconds or a tenth of the recording, whichever is more. 20 seconds
    /// absorbs fades, pregaps and trailing silence; a tenth still catches a radio edit standing
    /// in for the album cut on a six-minute track, which a wider margin would let through.
    /// </summary>
    internal static bool IsLengthOff(int fileSeconds, int? recordingSeconds) =>
        fileSeconds > 0 && recordingSeconds is int expected && expected > 0
        && Math.Abs(fileSeconds - expected) > Math.Max(20, expected / 10.0);

    /// <summary>The Navidrome admin when they may answer, otherwise the first allowed user. One
    /// person, because a library file has no requester to ask instead.</summary>
    internal static string? Keeper(LibraryActionSettings settings, string? adminUsername) =>
        settings.IsAllowed(adminUsername) ? adminUsername!.Trim()
            : (settings.AllowedUsers ?? []).Select(user => user?.Trim()).FirstOrDefault(user => !string.IsNullOrEmpty(user));

    /// <summary>Header only: the fingerprint reads the audio anyway, and a mount is slow.</summary>
    internal static Song ReadTags(string path)
    {
        var fallback = Path.GetFileNameWithoutExtension(path);
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.None);
            var tag = file.Tag;
            return new Song
            {
                Artist = tag.JoinedPerformers ?? "",
                Title = string.IsNullOrWhiteSpace(tag.Title) ? fallback : tag.Title.Trim(),
                Album = tag.Album ?? "",
            };
        }
        catch { return new Song { Artist = "", Title = fallback, Album = "" }; }
    }

    internal static List<string> Enumerate(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(path => CoverUpgradeWorker.AudioExtensions.Contains(Path.GetExtension(path)))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            // A leading dot is the quarantine and anything else kept out of Navidrome's scan.
            .Where(relative => !relative.Split('/').Any(segment => segment.StartsWith('.')))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

    private static string FullPath(string root, string relative) => Path.GetFullPath(Path.Combine(root, relative));

    private static int FirstAfter(List<string> files, string cursor)
    {
        var index = files.BinarySearch(cursor, StringComparer.Ordinal);
        return index >= 0 ? index + 1 : ~index;
    }

    private void Record(int generation, Action<ReviewSweepState> change) =>
        _store.Update(s => { if (generation == Volatile.Read(ref _generation)) change(s); });

    private TimeSpan Hold(string state, string? reason, TimeSpan wait) { _state = state; _reason = reason; return wait; }

    public void SetPaused(bool paused) => _store.Update(s => s.Paused = paused);

    public void Reset()
    {
        Interlocked.Increment(ref _generation);
        _files = null;
        _store.Update(s =>
        {
            s.Cursor = ""; s.Checked.Clear(); s.Pass = 1; s.Total = 0; s.Found = 0; s.Fine = 0; s.Undecodable = 0;
            s.LastCheckedUtc = null; s.PassFinishedUtc = null; s.NextPassUtc = default;
        });
    }

    public ReviewSweepStatus Status()
    {
        var settings = _settings.CurrentValue;
        var files = _files;
        return _store.Read(s => new ReviewSweepStatus(_state, _reason, s.Paused,
            files is null ? (s.Cursor.Length == 0 ? 0 : s.Checked.Count) : FirstAfter(files, s.Cursor),
            s.Total, s.Pass, s.Found, _notices.OpenCount(NoticeOrigin.LibrarySweep), s.Fine, s.Undecodable,
            Keeper(settings, _subsonic.CurrentValue.AdminUsername), settings.EffectiveReviewSweepPerHour,
            s.LastCheckedUtc, s.PassFinishedUtc));
    }
}
