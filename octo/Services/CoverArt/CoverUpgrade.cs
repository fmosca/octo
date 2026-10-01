using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Local;
using Octo.Services.Subsonic;

namespace Octo.Services.CoverArt;

public enum CoverUpgradeScope
{
    /// <summary>Only the files Octo downloaded.</summary>
    OctoDownloads,
    /// <summary>Every song under the music folder.</summary>
    WholeLibrary,
}

public enum CoverUpgradeStatus { Idle, Running, Completed, Cancelled, Interrupted, Failed }

public enum CoverUpgradeMode
{
    /// <summary>Reads the songs only, and lists every album whose cover is smaller than asked.
    /// No lookups and no writes, so it is quick even over a whole library.</summary>
    Scan,
    /// <summary>Looks the albums up and lists what a larger cover would replace. Writes nothing.</summary>
    Preview,
    /// <summary>Looks up and writes.</summary>
    Apply,
}

/// <summary>
/// One run. <paramref name="Albums"/> is the albums picked from the last run's list, by id;
/// null means every album in scope whose cover is smaller than <paramref name="SmallerThan"/>.
/// </summary>
public sealed record CoverUpgradeRequest(CoverUpgradeScope Scope, CoverUpgradeMode Mode, bool FolderCovers,
    int SmallerThan = CoverUpgradeWorker.DefaultSmallerThan, IReadOnlyList<string>? Albums = null, bool Undo = false);

/// <summary>
/// One album on the list. Result says what became of it: <c>soft</c> (a scan found its cover
/// small), <c>found</c> (a preview found a larger one), <c>upgraded</c> (written), or
/// <c>none</c> (looked up, nothing clearly larger).
/// </summary>
public sealed record CoverUpgradeChange(string Id, string Folder, string Artist, string? Album, int FromSide, int ToSide,
    string? Source, int Files, bool FolderCover, string Result, string? FirstFile = null,
    IReadOnlyList<string>? Paths = null, string? NavidromeAlbumId = null, string? Barcode = null);

/// <summary>
/// One piece of work: a folder, or one Navidrome album when Navidrome could say which songs
/// make each album. Files is null for "every song in the folder".
/// </summary>
public sealed record CoverUpgradeItem(string Folder, List<string>? Files, string? NavidromeAlbumId = null);

public sealed class CoverUpgradeRun
{
    public string RunId { get; set; } = "";
    public CoverUpgradeStatus Status { get; set; } = CoverUpgradeStatus.Idle;
    public CoverUpgradeScope Scope { get; set; }
    public CoverUpgradeMode Mode { get; set; } = CoverUpgradeMode.Scan;
    public bool FolderCovers { get; set; } = true;
    public int SmallerThan { get; set; } = CoverUpgradeWorker.DefaultSmallerThan;
    /// <summary>The album ids this run was limited to, or null for every album in scope.</summary>
    public List<string>? Selected { get; set; }
    public bool FullSize { get; set; }
    public bool Undo { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    /// <summary>Folders in the queue.</summary>
    public int Total { get; set; }
    public int Processed { get; set; }
    /// <summary>Songs in the queue and songs read so far: the progress a person sees, because a
    /// flat library is one folder of thousands of songs.</summary>
    public int SongsTotal { get; set; }
    public int SongsRead { get; set; }
    /// <summary>Albums to go through and gone through, when they are known up front: every
    /// album of a scan Navidrome grouped, or the picked ones. Lookups take seconds each, so
    /// this is the progress that moves during them.</summary>
    public int AlbumsTotal { get; set; }
    public int AlbumsDone { get; set; }
    /// <summary>Albums a scan found with a cover smaller than asked.</summary>
    public int Soft { get; set; }
    /// <summary>Albums whose cover got (or would get) larger.</summary>
    public int Upgraded { get; set; }
    /// <summary>Albums already as sharp as anything found, or with nothing found.</summary>
    public int Kept { get; set; }
    /// <summary>Songs rewritten (or that would be).</summary>
    public int Files { get; set; }
    public int Failed { get; set; }
    public int Cursor { get; set; }
    public string? LastFolder { get; set; }
    public string? Reason { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<CoverUpgradeChange> Preview { get; set; } = [];
    public List<CoverUpgradeItem> Queue { get; set; } = [];

    [JsonIgnore]
    public bool DryRun => Mode != CoverUpgradeMode.Apply;

    [JsonIgnore]
    public bool CanResume => Status is CoverUpgradeStatus.Cancelled or CoverUpgradeStatus.Interrupted
        && !Undo && Cursor < Queue.Count;
}

/// <summary>The run, kept in the config folder so it survives a restart; the genre backfill's
/// idiom (coalesced writes every ten seconds, through a temporary file).</summary>
public sealed class CoverUpgradeStore : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    /// <summary>Enough for a scan of a large library to list every soft album, so any of them can
    /// be picked; about 250 bytes a row.</summary>
    public const int MaxPreviewRows = 5000;
    private const int MaxErrors = 20;

    private readonly string? _path;
    private readonly ILogger<CoverUpgradeStore>? _logger;
    private readonly Timer? _timer;
    private readonly object _lock = new();
    private int _dirty;
    private CoverUpgradeRun _run = new();

    /// <summary>Small copies of the covers a preview found, by album id, so the dashboard can
    /// show the new cover before anything is written. On disk beside the run, or in memory
    /// when the store has no file (tests).</summary>
    private readonly string? _thumbs;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _memoryThumbs = new();

    public CoverUpgradeStore(string? path = null, ILogger<CoverUpgradeStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _thumbs = _path is null ? null : Path.Combine(Path.GetDirectoryName(_path)!, "cover-upgrade-found");
        _logger = logger;
        if (_path is null) return;
        Load();
        _timer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public CoverUpgradeRun Current { get { lock (_lock) return _run; } }

    public void Update(Action<CoverUpgradeRun> change)
    {
        lock (_lock)
        {
            change(_run);
            if (_run.Errors.Count > MaxErrors) _run.Errors.RemoveRange(0, _run.Errors.Count - MaxErrors);
            if (_run.Preview.Count > MaxPreviewRows) _run.Preview.RemoveRange(MaxPreviewRows, _run.Preview.Count - MaxPreviewRows);
        }
        Interlocked.Exchange(ref _dirty, 1);
    }

    public void SaveFoundThumb(string id, byte[] bytes)
    {
        if (_thumbs is null) { _memoryThumbs[id] = bytes; return; }
        try
        {
            Directory.CreateDirectory(_thumbs);
            File.WriteAllBytes(Path.Combine(_thumbs, id + ".jpg"), bytes);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("cover upgrade could not keep a found cover: {M}", ex.Message);
        }
    }

    public byte[]? FoundThumb(string id)
    {
        if (_thumbs is null) return _memoryThumbs.TryGetValue(id, out var bytes) ? bytes : null;
        var path = Path.Combine(_thumbs, id + ".jpg");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void ClearFoundThumbs()
    {
        _memoryThumbs.Clear();
        try
        {
            if (_thumbs is not null && Directory.Exists(_thumbs)) Directory.Delete(_thumbs, recursive: true);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("cover upgrade could not clear found covers: {M}", ex.Message);
        }
    }

    public void Replace(CoverUpgradeRun run)
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
            var run = JsonSerializer.Deserialize<CoverUpgradeRun>(File.ReadAllText(_path!));
            if (run is null) return;
            // Never resumed by itself: the restart may have been how it was stopped.
            if (run.Status == CoverUpgradeStatus.Running)
            {
                run.Status = CoverUpgradeStatus.Interrupted;
                run.Reason = "Octo restarted while this run was going.";
            }
            _run = run;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("cover upgrade state could not be read: {M}", ex.Message);
        }
    }

    private void Flush()
    {
        if (_path is null || Interlocked.Exchange(ref _dirty, 0) == 0) return;
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_run);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", json);
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _dirty, 1);
            _logger?.LogWarning("cover upgrade state could not be written: {M}", ex.Message);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        Flush();
    }
}

/// <summary>
/// What the last real run replaced, so Undo can put it back: one line per file, and the old
/// pictures themselves in a folder beside it, stored once per distinct picture (an album's
/// tracks share one), named by their hash.
/// </summary>
public sealed class CoverUpgradeJournal
{
    public sealed record Entry(
        [property: JsonPropertyName("p")] string Path,
        [property: JsonPropertyName("k")] string Kind,
        [property: JsonPropertyName("h")] string? Hash,
        [property: JsonPropertyName("r")] string RunId);

    public const string Embedded = "embedded";
    public const string FolderFile = "file";

    private readonly string? _path;
    private readonly string? _backups;
    private readonly ILogger<CoverUpgradeJournal>? _logger;
    private readonly object _lock = new();

    public CoverUpgradeJournal(string? path = null, ILogger<CoverUpgradeJournal>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _backups = _path is null ? null : Path.Combine(Path.GetDirectoryName(_path)!, "cover-backups");
        _logger = logger;
    }

    public bool HasEntries => _path is not null && File.Exists(_path) && new FileInfo(_path).Length > 0;

    /// <summary>Keeps the picture and records the file. False when the picture could not be
    /// kept, and then the file must not be changed: its undo would be gone.</summary>
    public bool Record(string path, string kind, byte[]? before, string runId)
    {
        if (_path is null) return true;
        try
        {
            string? hash = null;
            if (before is { Length: > 0 })
            {
                hash = Convert.ToHexString(SHA256.HashData(before))[..32].ToLowerInvariant();
                var backup = Path.Combine(_backups!, hash);
                Directory.CreateDirectory(_backups!);
                if (!File.Exists(backup))
                {
                    File.WriteAllBytes(backup + ".tmp", before);
                    File.Move(backup + ".tmp", backup, overwrite: true);
                }
            }
            lock (_lock)
                File.AppendAllText(_path, JsonSerializer.Serialize(new Entry(path, kind, hash, runId)) + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("cover upgrade could not keep the old cover of {Path}: {M}", path, ex.Message);
            return false;
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<Entry> ReadAll()
    {
        if (_path is null || !File.Exists(_path)) return [];
        var entries = new List<Entry>();
        lock (_lock)
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<Entry>(line) is { Path.Length: > 0 } entry) entries.Add(entry);
                }
                catch (JsonException) { /* a run killed mid-line costs that one file's undo */ }
            }
        entries.Reverse();
        return entries;
    }

    public byte[]? Backup(string? hash)
    {
        if (hash is null || _backups is null) return null;
        var path = Path.Combine(_backups, hash);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Keeps only these entries (oldest first), and the backups they still need.</summary>
    public void Rewrite(IReadOnlyList<Entry> oldestFirst)
    {
        if (_path is null) return;
        lock (_lock)
        {
            if (oldestFirst.Count == 0) File.Delete(_path);
            else
            {
                File.WriteAllLines(_path + ".tmp", oldestFirst.Select(entry => JsonSerializer.Serialize(entry)));
                File.Move(_path + ".tmp", _path, overwrite: true);
            }
            if (_backups is null || !Directory.Exists(_backups)) return;
            var needed = oldestFirst.Select(entry => entry.Hash).OfType<string>().ToHashSet();
            foreach (var file in Directory.EnumerateFiles(_backups))
                if (!needed.Contains(Path.GetFileName(file))) File.Delete(file);
        }
    }
}

/// <summary>
/// Goes through a library's songs and gives each album the largest cover any source has for
/// it. Brandon's ask, 2026-10-01: "go through your artwork and grab the highest quality for
/// each one to embed all of your songs".
///
/// Three steps, Brandon's shape for it (2026-10-01): a scan reads the songs only and lists every
/// album whose cover is smaller than asked; he picks all of them or some; a preview of the
/// picked ones looks them up, and an upgrade writes. One folder at a time, its songs grouped by
/// album. An album is changed only when the cover found is clearly larger than the one its
/// songs carry, and only the front cover is replaced; other pictures stay. A real run keeps
/// every replaced picture first, so Undo can put it all back. cover.jpg and folder.jpg beside
/// an album are what Navidrome shows before the art inside the files, so they are upgraded too
/// when asked (JPEG only; a PNG or WebP one is left and reported).
/// </summary>
public sealed class CoverUpgradeWorker : BackgroundService
{
    /// <summary>A found cover must be this much larger to be worth a rewrite.</summary>
    internal const double MinimumGain = 1.2;

    /// <summary>What counts as a soft cover unless the dashboard says otherwise: under the
    /// catalog's own 1000 px.</summary>
    public const int DefaultSmallerThan = 1000;

    /// <summary>Albums a scan reads at once.</summary>
    internal const int ScanParallelism = 6;

    /// <summary>Albums looked up at once. Apple's searches wait their turn inside the iTunes
    /// lookup (about 20 a minute), so this only overlaps the downloads and the other sources.</summary>
    internal const int LookupParallelism = 4;

    internal static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".mp4", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff", ".dsf", ".wv", ".ape",
    };

    private readonly Channel<CoverUpgradeRequest> _queue = Channel.CreateBounded<CoverUpgradeRequest>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CoverUpgradeStore _store;
    private readonly CoverUpgradeJournal _journal;
    private readonly IAlbumCoverFinder _finder;
    private readonly IOptionsMonitor<MetadataSettings> _settings;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<CoverUpgradeWorker> _logger;
    private volatile bool _cancel;
    private int _pending;

    public CoverUpgradeWorker(CoverUpgradeStore store, CoverUpgradeJournal journal, IAlbumCoverFinder finder,
        IOptionsMonitor<MetadataSettings> settings, IConfiguration configuration, IServiceScopeFactory scopes,
        ILogger<CoverUpgradeWorker> logger)
    {
        _store = store;
        _journal = journal;
        _finder = finder;
        _settings = settings;
        _configuration = configuration;
        _scopes = scopes;
        _logger = logger;
    }

    public CoverUpgradeRun Current => _store.Current;
    public bool CanUndo => _journal.HasEntries;
    public bool IsBusy => _store.Current.Status == CoverUpgradeStatus.Running || Volatile.Read(ref _pending) != 0;

    public bool TryEnqueue(CoverUpgradeRequest request)
    {
        if (_store.Current.Status == CoverUpgradeStatus.Running
            || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return false;
        if (_queue.Writer.TryWrite(request)) return true;
        Interlocked.Exchange(ref _pending, 0);
        return false;
    }

    public void RequestCancel() => _cancel = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                _cancel = false;
                if (request.Undo) await UndoAsync(stoppingToken);
                else await RunAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cover upgrade failed");
                _store.Update(run =>
                {
                    run.Status = CoverUpgradeStatus.Failed;
                    run.Reason = ex.Message;
                    run.FinishedUtc = DateTime.UtcNow;
                });
            }
            finally
            {
                Interlocked.Exchange(ref _pending, 0);
            }
        }
    }

    private async Task RunAsync(CoverUpgradeRequest request, CancellationToken stoppingToken)
    {
        var previous = _store.Current;
        var selected = request.Albums?.Distinct(StringComparer.Ordinal).ToList();
        var resuming = previous.CanResume && previous.Scope == request.Scope && previous.Mode == request.Mode
            && previous.FolderCovers == request.FolderCovers && previous.SmallerThan == request.SmallerThan
            && (previous.Selected ?? []).SequenceEqual(selected ?? [], StringComparer.Ordinal)
            && (previous.Selected is null) == (selected is null);
        if (resuming)
            _store.Update(run => { run.Status = CoverUpgradeStatus.Running; run.Reason = null; });
        else
        {
            // Running from the first moment, with nothing listed yet: listing the songs and
            // asking Navidrome about them takes seconds, and a dashboard that looked in that
            // time saw the last run and never noticed this one start.
            _store.Replace(new CoverUpgradeRun
            {
                RunId = Guid.NewGuid().ToString("N")[..12],
                Status = CoverUpgradeStatus.Running,
                Scope = request.Scope,
                Mode = request.Mode,
                FolderCovers = request.FolderCovers,
                SmallerThan = request.SmallerThan,
                Selected = selected,
                FullSize = _settings.CurrentValue.EmbedFullSizeCovers,
                StartedUtc = DateTime.UtcNow,
                Reason = "Listing your songs.",
            });
            var queue = selected is null ? await EnumerateAsync(request.Scope) : QueueOf(previous, selected);
            if (request.Mode != CoverUpgradeMode.Apply) _store.ClearFoundThumbs();
            _store.Update(run =>
            {
                run.Reason = null;
                run.Total = queue.Count;
                run.SongsTotal = queue.Sum(item => item.Files?.Count ?? 0);
                run.AlbumsTotal = selected?.Count ?? queue.Count(item => item.NavidromeAlbumId is not null);
                run.Queue = queue;
            });
        }
        var current = _store.Current;
        _logger.LogInformation("Cover upgrade {Mode}: {Count} folder(s) from {Cursor}, scope {Scope}, {Picked}",
            current.Mode, current.Total, current.Cursor, current.Scope,
            current.Selected is null ? "every album" : $"{current.Selected.Count} picked album(s)");

        // Picked albums are matched in bulk (barcodes, then Apple 20 to 40 at a time) WHILE
        // the lookups run: each album waits only for its own batch, so the run takes about as
        // long as finding the barcodes, not that plus everything after it.
        using var primeStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var priming = Task.CompletedTask;
        if (current.Mode != CoverUpgradeMode.Scan && current.Selected is { Count: > 0 } picks)
        {
            var picked = picks.ToHashSet(StringComparer.Ordinal);
            var queries = previous.Preview
                .Where(row => picked.Contains(row.Id) && !string.IsNullOrWhiteSpace(row.Album))
                .Select(row => new AlbumCoverQuery(row.Artist, row.Album, null, Barcode: row.Barcode))
                .ToList();
            priming = Task.Run(async () =>
            {
                try
                {
                    await _finder.PrimeAsync(queries, new Progress<string>(text => _store.Update(run => run.Reason = text)), primeStop.Token);
                }
                catch (Exception ex) when (!primeStop.IsCancellationRequested)
                {
                    _logger.LogInformation("Cover upgrade could not match albums in bulk, so each is searched: {M}", ex.Message);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    _store.Update(run => run.Reason = null);
                }
            });
        }
        try
        {
            await LookUpAlbumsAsync(current, stoppingToken);
        }
        finally
        {
            primeStop.Cancel();
            await priming;
        }
    }

    private async Task LookUpAlbumsAsync(CoverUpgradeRun current, CancellationToken stoppingToken)
    {

        // Over a network mount waiting is most of each read, and most of each lookup is
        // waiting on a server, so several albums are worked on at once.
        var parallel = current.Mode == CoverUpgradeMode.Scan ? ScanParallelism : LookupParallelism;
        for (var index = current.Cursor; index < current.Queue.Count; index += parallel)
        {
            if (Stopped(stoppingToken)) return;

            var batch = current.Queue.Skip(index).Take(parallel).ToList();
            var looked = await Task.WhenAll(batch.Select(async item =>
            {
                try
                {
                    return await ProcessFolderAsync(item, current, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _store.Update(run =>
                    {
                        run.Failed++;
                        run.Errors.Add($"{item.Folder}: {ex.Message}");
                    });
                    return false;
                }
            }));
            // Stopped part way through: this batch is not done, so it runs again on a resume. It
            // used to count as done, and a resume skipped the rest of a half-read folder (which
            // in a flat library was the whole library).
            if (stoppingToken.IsCancellationRequested || _cancel) continue;
            var reached = index + batch.Count;
            _store.Update(run =>
            {
                run.Cursor = reached;
                run.Processed += batch.Count;
                run.LastFolder = batch[^1].Folder;
            });
        }

        // A stop during the last batch ends the loop too; that batch did not finish.
        if (Stopped(stoppingToken)) return;
        _store.Update(run =>
        {
            run.Status = CoverUpgradeStatus.Completed;
            run.FinishedUtc = DateTime.UtcNow;
        });
        var done = _store.Current;
        _logger.LogInformation("Cover upgrade {Mode} finished: {Soft} soft, {Upgraded} larger, {Files} song(s), {Kept} kept, {Failed} failed",
            done.Mode, done.Soft, done.Upgraded, done.Files, done.Kept, done.Failed);
        if (!done.DryRun && done.Files > 0) await RescanAsync();
    }

    /// <summary>Records a shutdown or a stop from the dashboard. True when the run must end.</summary>
    private bool Stopped(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            _store.Update(run => run.Status = CoverUpgradeStatus.Interrupted);
            return true;
        }
        if (!_cancel) return false;
        _store.Update(run =>
        {
            run.Status = CoverUpgradeStatus.Cancelled;
            run.Reason = "Stopped from the dashboard.";
            run.FinishedUtc = DateTime.UtcNow;
        });
        return true;
    }

    private sealed record SongFile(string Path, string Artist, string? Album, string? Title,
        string? ReleaseId, string? ReleaseGroupId, int Side, string? Barcode = null);

    /// <summary>
    /// The songs behind the picked albums, from the list they were picked from. Only those
    /// songs are read again: in a flat library every album shares one folder of thousands of
    /// songs, and reading the folder to find three albums took as long as the scan.
    /// </summary>
    private static List<CoverUpgradeItem> QueueOf(CoverUpgradeRun previous, IReadOnlyCollection<string> ids)
    {
        var picked = ids.ToHashSet(StringComparer.Ordinal);
        var files = new Dictionary<string, List<string>?>(StringComparer.Ordinal);
        foreach (var item in previous.Queue) files.TryAdd(item.Folder, item.Files);
        return previous.Preview
            .Where(row => picked.Contains(row.Id))
            .GroupBy(row => row.Folder, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .SelectMany(group => group.All(row => row.NavidromeAlbumId is not null && row.Paths is { Count: > 0 })
                // Navidrome albums stay one item each, so they keep their ids.
                ? group.Select(row => new CoverUpgradeItem(group.Key, row.Paths!.ToList(), row.NavidromeAlbumId))
                : [new CoverUpgradeItem(group.Key,
                    group.All(row => row.Paths is { Count: > 0 })
                        ? group.SelectMany(row => row.Paths!).Distinct(StringComparer.Ordinal).ToList()
                        : files.GetValueOrDefault(group.Key))])
            .ToList();
    }

    /// <summary>A stable id for one album in one folder, so a pick survives from one run to the next.</summary>
    internal static string AlbumId(string folder, string albumKey) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(folder + "\u0000" + albumKey)))[..16]
            .ToLowerInvariant();

    /// <summary>The side of a dashboard tile's picture: sharp at twice the tile's size.</summary>
    internal const int ThumbSide = 320;

    /// <summary>A small copy of the cover a preview found for an album on the list.</summary>
    public byte[]? FoundThumbnail(string id) => _store.FoundThumb(id);

    /// <summary>A small copy of the cover an album on the list has now, for the dashboard.</summary>
    public byte[]? Thumbnail(string id)
    {
        var row = _store.Current.Preview.FirstOrDefault(r => r.Id == id);
        if (row?.FirstFile is not { } path || !File.Exists(path)) return null;
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.None);
            var bytes = FrontOf(file.Tag.Pictures)?.Data?.Data;
            if (bytes is not { Length: > 0 } && FolderCover(row.Folder, quiet: true) is { } folderFile)
                bytes = File.ReadAllBytes(folderFile.Path);
            return bytes is { Length: > 0 } ? CoverImage.ToJpeg(CoverImage.FitWithin(bytes, ThumbSide)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when it asked the sources anything, so the run should pause after it.</summary>
    private async Task<bool> ProcessFolderAsync(CoverUpgradeItem item, CoverUpgradeRun run, CancellationToken ct)
    {
        if (!Directory.Exists(item.Folder)) return false;
        var paths = item.Files ?? Directory.EnumerateFiles(item.Folder)
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path))).ToList();
        var songs = new List<SongFile>();
        // A Navidrome album is known to be one album, so a scan or a lookup needs only one of
        // its songs read; a replace reads every song, since each one gets its own cover.
        var oneWillDo = item.NavidromeAlbumId is not null && run.Mode != CoverUpgradeMode.Apply;
        foreach (var path in paths)
        {
            if (ct.IsCancellationRequested || _cancel) return false;
            var song = File.Exists(path) ? ReadSong(path) : null;
            if (song is not null) songs.Add(song);
            _store.Update(r => r.SongsRead++);
            if (oneWillDo && song is not null)
            {
                var left = paths.Count - paths.IndexOf(path) - 1;
                if (left > 0) _store.Update(r => r.SongsRead += left);
                break;
            }
        }

        var looked = false;
        var picked = run.Selected?.ToHashSet(StringComparer.Ordinal);
        // An album without a name is matched song by song, as a single.
        foreach (var album in songs.GroupBy(song => item.NavidromeAlbumId is { } ndAlbum
                     ? "nd:" + ndAlbum
                     : string.IsNullOrWhiteSpace(song.Album)
                         ? "\u0001" + song.Path
                         : $"{SongIdentity.Key(song.Artist)}|{SongIdentity.Key(song.Album)}"))
        {
            if (ct.IsCancellationRequested || _cancel) break;
            var id = AlbumId(item.Folder, album.Key);
            _store.Update(r => { if (r.AlbumsTotal > 0) r.AlbumsDone++; });
            // Every path of the album, read or not, so a later run and a replace see them all.
            var albumPaths = item.NavidromeAlbumId is not null ? paths : album.Select(song => song.Path).ToList();
            if (picked is not null && !picked.Contains(id)) continue;
            var first = album.First();
            var have = album.Min(song => song.Side);
            var folderCover = run.FolderCovers && (item.NavidromeAlbumId is not null
                ? FolderHoldsOnly(item.Folder, albumPaths)
                : album.Count() == songs.Count) ? FolderCover(item.Folder) : null;
            var shown = Math.Min(have, folderCover?.Side ?? have);

            // A picked album is looked up whatever its size: it was picked.
            if (picked is null && shown >= run.SmallerThan)
            {
                _store.Update(r => r.Kept++);
                continue;
            }

            if (run.Mode == CoverUpgradeMode.Scan)
            {
                var soft = new CoverUpgradeChange(id, item.Folder, first.Artist, first.Album, shown, 0, null,
                    albumPaths.Count, folderCover is not null, "soft", first.Path, albumPaths, item.NavidromeAlbumId,
                    album.Select(song => song.Barcode).FirstOrDefault(code => !string.IsNullOrEmpty(code)));
                _store.Update(r => { if (AddRow(r, soft)) r.Soft++; });
                continue;
            }

            looked = true;

            var found = await _finder.FindAsync(new AlbumCoverQuery(first.Artist, first.Album, first.Title,
                album.Select(s => s.ReleaseId).FirstOrDefault(rid => !string.IsNullOrEmpty(rid)),
                album.Select(s => s.ReleaseGroupId).FirstOrDefault(rid => !string.IsNullOrEmpty(rid)),
                album.Select(s => s.Barcode).FirstOrDefault(code => !string.IsNullOrEmpty(code))), ct);

            var upgradeFiles = found is not null
                ? album.Where(song => found.Side >= song.Side * MinimumGain && found.Side > song.Side).ToList()
                : [];
            var upgradeFolder = found is not null && folderCover is { } fc
                && found.Side >= fc.Side * MinimumGain && found.Side > fc.Side;
            if (found is null || (upgradeFiles.Count == 0 && !upgradeFolder))
            {
                // On a picked list, an album that stays as it is still says so.
                var none = new CoverUpgradeChange(id, item.Folder, first.Artist, first.Album, shown, found?.Side ?? 0,
                    found?.Source, 0, false, "none", first.Path, albumPaths, item.NavidromeAlbumId);
                _store.Update(r =>
                {
                    r.Kept++;
                    if (picked is not null) AddRow(r, none);
                });
                continue;
            }

            _store.SaveFoundThumb(id, CoverImage.ToJpeg(CoverImage.FitWithin(found.Bytes, ThumbSide)));

            var written = 0;
            if (!run.DryRun)
            {
                var embed = run.FullSize ? found.Bytes : CoverImage.FitWithin(found.Bytes, MetadataSettings.EmbeddedCoverSide);
                foreach (var song in upgradeFiles)
                {
                    if (ct.IsCancellationRequested) break;
                    if (WriteEmbedded(song.Path, embed, run.RunId)) written++;
                }
                if (upgradeFolder && !WriteFolderCover(folderCover!.Value.Path, found.Bytes, run.RunId))
                    upgradeFolder = false;
            }
            else written = item.NavidromeAlbumId is not null ? albumPaths.Count : upgradeFiles.Count;

            var change = new CoverUpgradeChange(id, item.Folder, first.Artist, first.Album, shown,
                found.Side, found.Source, run.DryRun ? albumPaths.Count : written, upgradeFolder,
                run.DryRun ? "found" : "upgraded", first.Path, albumPaths, item.NavidromeAlbumId);
            _store.Update(r =>
            {
                if (AddRow(r, change)) r.Upgraded++;
                r.Files += written;
            });
        }
        return looked;
    }

    /// <summary>
    /// Lists an album once. An album already listed (a resumed batch runs again) takes its new
    /// row's place and is not counted twice. True when it is new.
    /// </summary>
    private static bool AddRow(CoverUpgradeRun run, CoverUpgradeChange row)
    {
        var at = run.Preview.FindIndex(existing => existing.Id == row.Id);
        if (at >= 0)
        {
            run.Preview[at] = row;
            return false;
        }
        if (run.Preview.Count < CoverUpgradeStore.MaxPreviewRows) run.Preview.Add(row);
        return true;
    }

    private SongFile? ReadSong(string path)
    {
        try
        {
            // Tags and pictures only: the audio properties cost extra reads, which over a network
            // mount is most of the time a scan takes.
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.None);
            var tag = file.Tag;
            var artist = TagWriterExtras.IsCompilation(file) ? "Various Artists"
                : !string.IsNullOrWhiteSpace(tag.FirstAlbumArtist) ? tag.FirstAlbumArtist : tag.FirstPerformer;
            if (string.IsNullOrWhiteSpace(artist) || (string.IsNullOrWhiteSpace(tag.Album) && string.IsNullOrWhiteSpace(tag.Title)))
                return null;
            var front = FrontOf(tag.Pictures);
            var side = front?.Data?.Data is { Length: > 0 } bytes && CoverImage.Measure(bytes) is { } size
                ? Math.Min(size.Width, size.Height) : 0;
            return new SongFile(path, artist, tag.Album, tag.Title, tag.MusicBrainzReleaseId,
                tag.MusicBrainzReleaseGroupId, side, BarcodeOf(file));
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Cover upgrade could not read {Path}: {M}", path, ex.Message);
            return null;
        }
    }

    private static readonly string[] BarcodeNames = ["BARCODE", "UPC", "EAN"];

    /// <summary>The album's barcode when the song carries one (about one song in ten of
    /// Brandon's): with it, the album needs no lookup to be matched at Apple.</summary>
    internal static string? BarcodeOf(TagLib.File file)
    {
        try
        {
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                foreach (var name in BarcodeNames)
                    if (xiph.GetFirstField(name) is { Length: > 0 } value) return value.Trim();
            if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                    if (BarcodeNames.Contains(frame.Description, StringComparer.OrdinalIgnoreCase)
                        && frame.Text.FirstOrDefault() is { Length: > 0 } value) return value.Trim();
            if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
                foreach (var name in BarcodeNames)
                    if (apple.GetDashBox("com.apple.iTunes", name) is { Length: > 0 } value) return value.Trim();
        }
        catch
        {
            // A tag TagLib half understands has no barcode worth trusting.
        }
        return null;
    }

    private static TagLib.IPicture? FrontOf(TagLib.IPicture[]? pictures) =>
        pictures?.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures?.FirstOrDefault();

    /// <summary>Replaces the front cover and nothing else, after keeping the old one.</summary>
    private bool WriteEmbedded(string path, byte[] cover, string runId)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var pictures = file.Tag.Pictures ?? [];
            var front = FrontOf(pictures);
            if (!_journal.Record(path, CoverUpgradeJournal.Embedded, front?.Data?.Data, runId)) return false;
            var picture = new TagLib.Picture
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = CoverImage.MimeType(cover),
                Description = "Cover",
                Data = new TagLib.ByteVector(cover),
            };
            file.Tag.Pictures = [picture, .. pictures.Where(p => !ReferenceEquals(p, front))];
            file.Save();
            return true;
        }
        catch (Exception ex)
        {
            _store.Update(r =>
            {
                r.Failed++;
                r.Errors.Add($"{path}: {ex.Message}");
            });
            return false;
        }
    }

    private bool WriteFolderCover(string path, byte[] cover, string runId)
    {
        try
        {
            var before = File.ReadAllBytes(path);
            if (!_journal.Record(path, CoverUpgradeJournal.FolderFile, before, runId)) return false;
            var bytes = CoverImage.ToJpeg(cover);
            if (Path.GetFileName(path).Equals(CoverFiles.FileName, StringComparison.OrdinalIgnoreCase)
                && CoverImage.IsOctoCover(before))
                bytes = CoverImage.MarkAsOcto(bytes);
            File.WriteAllBytes(path + ".octo-tmp", bytes);
            File.Move(path + ".octo-tmp", path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _store.Update(r =>
            {
                r.Failed++;
                r.Errors.Add($"{path}: {ex.Message}");
            });
            return false;
        }
    }

    /// <summary>True when every song in the folder is one of these: a cover.jpg there is this
    /// album's alone.</summary>
    private static bool FolderHoldsOnly(string folder, IReadOnlyCollection<string> albumPaths)
    {
        try
        {
            var mine = albumPaths.ToHashSet(StringComparer.Ordinal);
            return Directory.EnumerateFiles(folder)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
                .All(mine.Contains);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The cover Navidrome shows for an album, at the size the apps ask for, so the wall fills
    /// as fast as the apps do: Navidrome keeps these already made. Null when Navidrome cannot
    /// be asked, and the dashboard then gets a copy read from the song itself.
    /// </summary>
    public async Task<(byte[] Bytes, string Type)?> NavidromeThumbnailAsync(string id, CancellationToken ct)
    {
        var row = _store.Current.Preview.FirstOrDefault(r => r.Id == id);
        if (row?.NavidromeAlbumId is not { } album) return null;
        using var handle = _scopes.CreateScope();
        var identity = handle.ServiceProvider.GetService<NavidromeIdentityService>();
        var baseUrl = handle.ServiceProvider.GetService<IOptionsMonitor<SubsonicSettings>>()?.CurrentValue.Url;
        var http = handle.ServiceProvider.GetService<IHttpClientFactory>();
        if (identity?.GetScanAuth() is not { } auth || string.IsNullOrWhiteSpace(baseUrl) || http is null) return null;
        try
        {
            var url = $"{baseUrl.TrimEnd('/')}/rest/getCoverArt?c=octo&v=1.16.1&size=300"
                + $"&id={Uri.EscapeDataString("al-" + album)}&u={Uri.EscapeDataString(auth.user)}&t={auth.token}&s={auth.salt}";
            using var response = await http.CreateClient().GetAsync(url, ct);
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!response.IsSuccessStatusCode || !type.StartsWith("image/", StringComparison.Ordinal)) return null;
            return (await response.Content.ReadAsByteArrayAsync(ct), type);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Navidrome cover for album {Album} failed: {M}", album, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Which album each file belongs to, from Navidrome's own song list (a few requests for
    /// the whole library), so a scan can go album by album without reading a single file to
    /// find out. Keyed by full path. Empty when Navidrome cannot be asked; the scan then goes
    /// folder by folder as before.
    /// </summary>
    private async Task<Dictionary<string, string>> NavidromeAlbumsAsync(IServiceProvider services, string root,
        CancellationToken ct)
    {
        var albums = new Dictionary<string, string>(StringComparer.Ordinal);
        var identity = services.GetService<NavidromeIdentityService>();
        var baseUrl = services.GetService<IOptionsMonitor<SubsonicSettings>>()?.CurrentValue.Url;
        var http = services.GetService<IHttpClientFactory>();
        if (identity is null || http is null || string.IsNullOrWhiteSpace(baseUrl)) return albums;
        try
        {
            var jwt = await identity.EnsureAdminJwtAsync(ct);
            if (string.IsNullOrEmpty(jwt)) return albums;
            const int page = 1000;
            for (var start = 0; start < 200_000; start += page)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{baseUrl.TrimEnd('/')}/api/song?_start={start}&_end={start + page}&_sort=id&_order=ASC");
                request.Headers.TryAddWithoutValidation("X-Nd-Authorization", $"Bearer {jwt}");
                using var response = await http.CreateClient().SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) break;
                using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) break;
                var count = 0;
                foreach (var song in doc.RootElement.EnumerateArray())
                {
                    count++;
                    var album = Str(song, "albumId");
                    var path = Str(song, "path");
                    if (string.IsNullOrEmpty(album) || string.IsNullOrEmpty(path)) continue;
                    var relative = Path.Combine(path.Replace('\\', '/').TrimStart('/')
                        .Split('/', StringSplitOptions.RemoveEmptyEntries));
                    foreach (var baseDir in new[] { Str(song, "libraryPath"), root })
                    {
                        if (string.IsNullOrEmpty(baseDir)) continue;
                        albums.TryAdd(Path.GetFullPath(Path.Combine(baseDir, relative)), album);
                    }
                    // An older Navidrome reports the full path instead.
                    if (Path.IsPathRooted(path)) albums.TryAdd(Path.GetFullPath(path), album);
                }
                if (count < page) break;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Cover upgrade could not list Navidrome's songs, so it goes folder by folder: {M}", ex.Message);
        }
        return albums;
    }

    private static string? Str(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>Files grouped into one item per Navidrome album where Navidrome named one, and
    /// per folder for the rest.</summary>
    internal static List<CoverUpgradeItem> ByAlbum(IEnumerable<string> files, IReadOnlyDictionary<string, string> albums)
    {
        var items = new List<CoverUpgradeItem>();
        var rest = new List<string>();
        foreach (var group in files.GroupBy(path => albums.GetValueOrDefault(Path.GetFullPath(path))))
        {
            if (group.Key is null) { rest.AddRange(group); continue; }
            var list = group.OrderBy(path => path, StringComparer.Ordinal).ToList();
            items.Add(new CoverUpgradeItem(Path.GetDirectoryName(list[0]) ?? "", list, group.Key));
        }
        items.AddRange(rest
            .GroupBy(path => Path.GetDirectoryName(path) ?? "", StringComparer.Ordinal)
            .Select(group => new CoverUpgradeItem(group.Key, group.OrderBy(path => path, StringComparer.Ordinal).ToList())));
        return items.OrderBy(item => item.Files?.FirstOrDefault() ?? item.Folder, StringComparer.Ordinal).ToList();
    }

    /// <summary>The cover file Navidrome would show for this folder, when it is a JPEG this run
    /// may replace. A PNG or WebP one is reported and left, since a JPEG under its name would
    /// lie about what it is.</summary>
    private (string Path, int Side)? FolderCover(string folder, bool quiet = false)
    {
        foreach (var pattern in new[] { "cover.*", "folder.*", "front.*" })
        {
            var file = Directory.EnumerateFiles(folder, pattern).FirstOrDefault();
            if (file is null) continue;
            var extension = Path.GetExtension(file);
            if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                if (!quiet) _store.Update(r => r.Errors.Add($"{file}: not a JPEG, left as it is"));
                return null;
            }
            var size = CoverImage.Measure(File.ReadAllBytes(file));
            return (file, size is { } s ? Math.Min(s.Width, s.Height) : 0);
        }
        return null;
    }

    private async Task UndoAsync(CancellationToken ct)
    {
        var entries = _journal.ReadAll();
        _store.Replace(new CoverUpgradeRun
        {
            RunId = Guid.NewGuid().ToString("N")[..12],
            Status = CoverUpgradeStatus.Running,
            Mode = CoverUpgradeMode.Apply,
            Undo = true,
            StartedUtc = DateTime.UtcNow,
            Total = entries.Count,
            Reason = "Putting back the covers the last run replaced.",
        });

        // Newest first, so a file changed by two runs ends with the cover the oldest one found.
        var left = new List<CoverUpgradeJournal.Entry>();
        var restored = 0;
        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested || _cancel)
            {
                left.Add(entry);
                continue;
            }
            try
            {
                if (!File.Exists(entry.Path)) { left.Add(entry); continue; }
                var before = _journal.Backup(entry.Hash);
                if (entry.Hash is not null && before is null) throw new IOException("its kept cover is missing");
                if (entry.Kind == CoverUpgradeJournal.FolderFile)
                {
                    File.WriteAllBytes(entry.Path + ".octo-tmp", before!);
                    File.Move(entry.Path + ".octo-tmp", entry.Path, overwrite: true);
                }
                else
                {
                    using var file = TagLib.File.Create(entry.Path);
                    var pictures = file.Tag.Pictures ?? [];
                    var front = FrontOf(pictures);
                    var rest = pictures.Where(p => !ReferenceEquals(p, front));
                    file.Tag.Pictures = before is null ? rest.ToArray()
                        : [new TagLib.Picture
                        {
                            Type = TagLib.PictureType.FrontCover,
                            MimeType = CoverImage.MimeType(before),
                            Description = "Cover",
                            Data = new TagLib.ByteVector(before),
                        }, .. rest];
                    file.Save();
                }
                restored++;
                _store.Update(r => r.Files++);
            }
            catch (Exception ex)
            {
                left.Add(entry);
                _store.Update(r => { r.Failed++; r.Errors.Add($"{entry.Path}: {ex.Message}"); });
            }
            finally
            {
                _store.Update(r => { r.Processed++; r.LastFolder = Path.GetDirectoryName(entry.Path); });
            }
        }

        left.Reverse();
        _journal.Rewrite(left);
        _store.Update(r =>
        {
            r.Status = left.Count == 0 ? CoverUpgradeStatus.Completed : CoverUpgradeStatus.Cancelled;
            r.FinishedUtc = DateTime.UtcNow;
            r.Reason = left.Count == 0
                ? $"Put back the old cover on {restored} file(s)."
                : $"Put back {restored} file(s). {left.Count} are still to do (missing, unreadable or not reached); Undo again once they are back.";
        });
        if (restored > 0) await RescanAsync();
    }

    private async Task<List<CoverUpgradeItem>> EnumerateAsync(CoverUpgradeScope scope)
    {
        using var handle = _scopes.CreateScope();
        var fallback = _configuration["Library:DownloadPath"] ?? "/music";
        var root = handle.ServiceProvider.GetService<NavidromeIdentityService>()?.EffectiveDownloadPath(fallback) ?? fallback;
        List<string> files;
        if (scope == CoverUpgradeScope.OctoDownloads)
        {
            var library = handle.ServiceProvider.GetRequiredService<ILocalLibraryService>();
            files = (await library.GetMappingsAsync())
                .Select(mapping => mapping.LocalPath)
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        else if (!Directory.Exists(root))
        {
            _logger.LogWarning("Cover upgrade found no music folder at {Root}", root);
            return [];
        }
        else
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
                .ToList();
        }
        var albums = await NavidromeAlbumsAsync(handle.ServiceProvider, root, CancellationToken.None);
        _logger.LogInformation("Cover upgrade: Navidrome named the album of {Known} of {Count} song(s)",
            files.Count(path => albums.ContainsKey(Path.GetFullPath(path))), files.Count);
        return ByAlbum(files, albums);
    }

    private async Task RescanAsync()
    {
        try
        {
            using var handle = _scopes.CreateScope();
            await handle.ServiceProvider.GetRequiredService<ILocalLibraryService>().TriggerLibraryScanAsync(force: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cover upgrade could not ask Navidrome to scan: {M}", ex.Message);
        }
    }
}
