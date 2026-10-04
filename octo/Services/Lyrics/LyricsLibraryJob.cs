using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Local;

namespace Octo.Services.Lyrics;

public enum LyricsLibraryStatus { Idle, Running, Completed, Cancelled, Interrupted, Failed }

/// <summary>A lyric the job wrote but is not sure of, for someone to look at.</summary>
public sealed record LyricsReviewEntry(
    string Path, string Artist, string Title, string? Album, int? DurationSeconds,
    string Source, string Kind, string? CandidateId, string Reason, DateTime AtUtc);

public sealed class LyricsLibraryRun
{
    public string RunId { get; set; } = "";
    public LyricsLibraryStatus Status { get; set; } = LyricsLibraryStatus.Idle;

    /// <summary>OctoDownloads, or WholeLibrary when "Write lyrics files beside all library
    /// songs" was on when the run started.</summary>
    public string Scope { get; set; } = "OctoDownloads";

    /// <summary>Also look again where a song's lyrics are weaker than the sources would choose
    /// now: plain, or line-timed while word timing is preferred.</summary>
    public bool Upgrade { get; set; }

    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Written { get; set; }
    public int WordTimed { get; set; }
    public int Upgraded { get; set; }
    public int AlreadyHad { get; set; }
    public int NotFound { get; set; }
    public int Instrumental { get; set; }
    public int Busy { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Cursor { get; set; }
    public string? LastPath { get; set; }
    public string? Reason { get; set; }
    public List<string> Errors { get; set; } = [];

    /// <summary>The files this run walks, kept so a resume carries on in the same order.</summary>
    public List<string> Queue { get; set; } = [];

    public List<LyricsReviewEntry> Review { get; set; } = [];

    public bool CanResume => Status is LyricsLibraryStatus.Cancelled or LyricsLibraryStatus.Interrupted
        && Cursor < Queue.Count;
}

/// <summary>
/// The run's progress, on disk, with the genre backfill's idiom: a dirty bit and a coalesced
/// flush through a temporary file, so a long walk is not one write per song and a torn write
/// never loses the cursor a resume depends on.
/// </summary>
public sealed class LyricsLibraryStore : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    public const int MaxReview = 500;
    private const int MaxErrors = 20;

    private readonly string? _path;
    private readonly ILogger<LyricsLibraryStore>? _logger;
    private readonly Timer? _flushTimer;
    private readonly object _lock = new();
    private int _dirty;
    private LyricsLibraryRun _run = new();

    public LyricsLibraryStore(string? path = null, ILogger<LyricsLibraryStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _logger = logger;
        if (_path is null) return;
        Load();
        _flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public LyricsLibraryRun Current
    {
        get { lock (_lock) return _run; }
    }

    public void Update(Action<LyricsLibraryRun> mutate)
    {
        lock (_lock)
        {
            mutate(_run);
            if (_run.Errors.Count > MaxErrors) _run.Errors.RemoveRange(0, _run.Errors.Count - MaxErrors);
            if (_run.Review.Count > MaxReview) _run.Review.RemoveRange(0, _run.Review.Count - MaxReview);
        }
        Interlocked.Exchange(ref _dirty, 1);
    }

    public void Replace(LyricsLibraryRun run)
    {
        lock (_lock) _run = run;
        Interlocked.Exchange(ref _dirty, 1);
        Flush();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var run = JsonSerializer.Deserialize<LyricsLibraryRun>(File.ReadAllText(_path!));
            if (run is null) return;
            // Never resumed by itself: a restart may be how someone stopped it.
            if (run.Status == LyricsLibraryStatus.Running)
            {
                run.Status = LyricsLibraryStatus.Interrupted;
                run.Reason = "Octo restarted while this run was in progress.";
            }
            _run = run;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("library lyrics state could not be read: {M}", ex.Message);
        }
    }

    public void Flush()
    {
        if (_path is null) return;
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_run);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _dirty, 1);
            _logger?.LogWarning("library lyrics state could not be written: {M}", ex.Message);
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        Flush();
    }
}

public sealed record LyricsLibraryRequest(bool Upgrade, bool Resume = false);

/// <summary>
/// "Find lyrics for the library": walks the songs that have no lyrics file and no lyrics in
/// their tags, and saves lyrics for each one the sources have (LYRICS_SAVE_TO says where). With
/// the upgrade box ticked, songs whose lyrics are weaker than the sources would choose are looked
/// up again too. One song at a time
/// with a pause between, so a library of thousands never floods a lyrics service; stoppable
/// from the dashboard, and resumable after a stop or a restart from where it was.
///
/// Where it writes follows the rule the downloads follow: beside the songs Octo downloaded, and
/// beside every library song only when "Write lyrics files beside all library songs" is on.
/// </summary>
public sealed class LyricsLibraryWorker : BackgroundService
{
    /// <summary>The pause after each song that needed a lookup.</summary>
    internal static TimeSpan Gap = TimeSpan.FromSeconds(1.5);

    /// <summary>When this many songs in a row find every service busy, the run stops rather
    /// than walk the rest of the library into the same wall; it can be resumed later.</summary>
    internal const int BusyInARowLimit = 10;

    private static readonly string[] AudioExtensions =
        [".mp3", ".flac", ".m4a", ".mp4", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma", ".aiff", ".aif", ".ape", ".wv", ".dsf"];

    private readonly Channel<LyricsLibraryRequest> _queue =
        Channel.CreateBounded<LyricsLibraryRequest>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly LyricsLibraryStore _store;
    private readonly LyricsSidecarWriter _writer;
    private readonly IOptionsMonitor<MetadataSettings> _settings;
    private readonly IServiceScopeFactory _scopes;
    private readonly Func<string> _musicRoot;
    private readonly ILogger<LyricsLibraryWorker> _logger;
    private volatile bool _cancelRequested;
    private int _pending;

    public LyricsLibraryWorker(LyricsLibraryStore store, LyricsSidecarWriter writer, IOptionsMonitor<MetadataSettings> settings,
        IServiceScopeFactory scopes, Octo.Services.Library.NavidromeSongPathResolver paths, ILogger<LyricsLibraryWorker> logger)
        : this(store, writer, settings, scopes, paths.MusicRoot, logger)
    {
    }

    internal LyricsLibraryWorker(LyricsLibraryStore store, LyricsSidecarWriter writer, IOptionsMonitor<MetadataSettings> settings,
        IServiceScopeFactory scopes, Func<string> musicRoot, ILogger<LyricsLibraryWorker> logger)
    {
        _store = store;
        _writer = writer;
        _settings = settings;
        _scopes = scopes;
        _musicRoot = musicRoot;
        _logger = logger;
    }

    public LyricsLibraryRun Current => _store.Current;

    public bool IsRunning => _store.Current.Status == LyricsLibraryStatus.Running || Volatile.Read(ref _pending) == 1;

    /// <summary>False when a run is going or about to, which the dashboard shows as "already running".</summary>
    public bool TryEnqueue(LyricsLibraryRequest request)
    {
        if (_store.Current.Status == LyricsLibraryStatus.Running || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return false;
        if (_queue.Writer.TryWrite(request)) return true;
        Interlocked.Exchange(ref _pending, 0);
        return false;
    }

    public void RequestCancel() => _cancelRequested = true;

    public void DismissReview(string path) =>
        _store.Update(run => run.Review.RemoveAll(entry => string.Equals(entry.Path, path, StringComparison.Ordinal)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _store.Update(run => run.Status = LyricsLibraryStatus.Interrupted);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Finding lyrics for the library failed");
                _store.Update(run =>
                {
                    run.Status = LyricsLibraryStatus.Failed;
                    run.Reason = ex.Message;
                    run.FinishedUtc = DateTime.UtcNow;
                });
            }
            finally
            {
                Interlocked.Exchange(ref _pending, 0);
                _store.Flush();
            }
        }
    }

    internal async Task RunAsync(LyricsLibraryRequest request, CancellationToken stoppingToken)
    {
        _cancelRequested = false;
        var current = _store.Current;
        List<string> queue;
        if (request.Resume && current.CanResume)
        {
            queue = current.Queue;
            _store.Update(run => { run.Status = LyricsLibraryStatus.Running; run.Reason = null; });
            _logger.LogInformation("Finding lyrics for the library, resuming at {Cursor}/{Total}", current.Cursor, queue.Count);
        }
        else
        {
            var whole = _settings.CurrentValue.WriteLyricsBesideAllSongs;
            queue = (await EnumerateAsync(whole)).ToList();
            _store.Replace(new LyricsLibraryRun
            {
                RunId = Guid.NewGuid().ToString("N")[..12],
                Status = LyricsLibraryStatus.Running,
                Scope = whole ? "WholeLibrary" : "OctoDownloads",
                Upgrade = request.Upgrade,
                StartedUtc = DateTime.UtcNow,
                Total = queue.Count,
                Queue = queue,
                // A new run keeps what an earlier one left for review.
                Review = current.Review,
            });
            _logger.LogInformation("Finding lyrics for the library: {Count} song(s), {Scope}", queue.Count, _store.Current.Scope);
        }

        var upgrade = _store.Current.Upgrade;
        var busyInARow = 0;
        for (var index = _store.Current.Cursor; index < queue.Count; index++)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                _store.Update(run => run.Status = LyricsLibraryStatus.Interrupted);
                return;
            }
            if (_cancelRequested)
            {
                _store.Update(run =>
                {
                    run.Status = LyricsLibraryStatus.Cancelled;
                    run.FinishedUtc = DateTime.UtcNow;
                    run.Reason = "Stopped from the dashboard.";
                });
                return;
            }

            var path = queue[index];
            var (write, looked, error) = await ProcessAsync(path, upgrade, stoppingToken);
            var busy = write?.Outcome is LyricsWriteOutcome.GaveUp or LyricsWriteOutcome.Retrying;
            busyInARow = busy ? busyInARow + 1 : 0;
            if (busyInARow >= BusyInARowLimit)
            {
                // The songs that met the wall are not counted as done, so a resume asks again.
                var from = index - BusyInARowLimit + 1;
                _store.Update(run =>
                {
                    run.Status = LyricsLibraryStatus.Interrupted;
                    run.Cursor = from;
                    run.Busy -= BusyInARowLimit - 1;
                    run.Processed -= BusyInARowLimit - 1;
                    run.Reason = "The lyrics services stopped answering. Resume later to carry on from here.";
                });
                _logger.LogWarning("Finding lyrics for the library paused at {Cursor}/{Total}: the services are not answering",
                    from, queue.Count);
                return;
            }

            _store.Update(run =>
            {
                run.Cursor = index + 1;
                run.Processed++;
                run.LastPath = path;
                if (error is not null)
                {
                    run.Failed++;
                    run.Errors.Add($"{path}: {error}");
                    return;
                }
                switch (write?.Outcome)
                {
                    case LyricsWriteOutcome.Written:
                        run.Written++;
                        if (write.Result?.Timing == LyricsTiming.Word) run.WordTimed++;
                        break;
                    case LyricsWriteOutcome.Upgraded:
                        run.Upgraded++;
                        if (write.Result?.Timing == LyricsTiming.Word) run.WordTimed++;
                        break;
                    case LyricsWriteOutcome.AlreadyThere: run.AlreadyHad++; break;
                    case LyricsWriteOutcome.NotFound: run.NotFound++; break;
                    case LyricsWriteOutcome.Instrumental: run.Instrumental++; break;
                    case LyricsWriteOutcome.GaveUp or LyricsWriteOutcome.Retrying: run.Busy++; break;
                    default: run.Skipped++; break;
                }
                if (write?.Result is { Doubt: { } doubt } result
                    && write.Outcome is LyricsWriteOutcome.Written or LyricsWriteOutcome.Upgraded)
                {
                    var tags = looked!;
                    run.Review.RemoveAll(entry => string.Equals(entry.Path, path, StringComparison.Ordinal));
                    run.Review.Add(new LyricsReviewEntry(path, tags.Artist, tags.Title, tags.Album, tags.DurationSeconds,
                        result.Source, LyricsChoiceService.KindOf(result), result.CandidateId, doubt, DateTime.UtcNow));
                }
            });

            if (looked is not null && write?.Outcome is not LyricsWriteOutcome.AlreadyThere && Gap > TimeSpan.Zero)
                await Task.Delay(Gap, stoppingToken);
        }

        _store.Update(run =>
        {
            run.Status = LyricsLibraryStatus.Completed;
            run.FinishedUtc = DateTime.UtcNow;
        });
        var done = _store.Current;
        _logger.LogInformation(
            "Finding lyrics for the library finished: {Written} written ({Word} word-timed), {Upgraded} upgraded, {Had} already had lyrics, {Missing} not found, {Busy} busy, of {Total}",
            done.Written, done.WordTimed, done.Upgraded, done.AlreadyHad, done.NotFound, done.Busy, done.Total);
    }

    /// <summary>One song: its tags, then the writer. Looked is null when the song was never
    /// looked up (no tags to look it up by).</summary>
    private async Task<(LyricsWrite? Write, LyricsJob? Looked, string? Error)> ProcessAsync(string path, bool upgrade,
        CancellationToken ct)
    {
        try
        {
            if (ReadTags(path) is not { } job) return (new LyricsWrite(LyricsWriteOutcome.Gone, null), null, null);
            var write = await _writer.WriteAsync(job with { Attempt = LyricsSidecarWriter.MaxAttempts }, upgrade, ct);
            return (write, job, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null, ex.Message);
        }
    }

    /// <summary>What a song is looked up by, from its own tags. Null without an artist and a
    /// title, since a lookup by file name is a guess.</summary>
    internal static LyricsJob? ReadTags(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var artist = file.Tag.FirstPerformer ?? file.Tag.FirstAlbumArtist;
            var title = file.Tag.Title;
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;
            var seconds = (int)Math.Round(file.Properties.Duration.TotalSeconds);
            return new LyricsJob(path, artist.Trim(), LyricsText.QueryTitle(title.Trim(), artist.Trim()),
                string.IsNullOrWhiteSpace(file.Tag.Album) ? null : file.Tag.Album.Trim(), seconds > 0 ? seconds : null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> EnumerateAsync(bool wholeLibrary)
    {
        if (!wholeLibrary)
        {
            using var scope = _scopes.CreateScope();
            var library = scope.ServiceProvider.GetRequiredService<ILocalLibraryService>();
            return (await library.GetMappingsAsync())
                .Select(mapping => mapping.LocalPath)
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        var root = _musicRoot();
        if (!Directory.Exists(root))
        {
            _logger.LogWarning("Finding lyrics for the library found no music folder at {Root}", root);
            return [];
        }
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Finding lyrics for the library could not list {Root}", root);
            return [];
        }
    }
}
