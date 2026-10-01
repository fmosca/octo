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
    string? Source, int Files, bool FolderCover, string Result, string? FirstFile = null);

/// <summary>One folder to go through. Files is null for "every song in it".</summary>
public sealed record CoverUpgradeItem(string Folder, List<string>? Files);

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

    /// <summary>Apple asks for about 20 searches a minute; one album is one search.</summary>
    internal TimeSpan PauseBetweenAlbums { get; set; } = TimeSpan.FromSeconds(3);

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
            var queue = selected is null ? await EnumerateAsync(request.Scope) : QueueOf(previous, selected);
            if (request.Mode != CoverUpgradeMode.Apply) _store.ClearFoundThumbs();
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
                Total = queue.Count,
                Queue = queue,
            });
        }
        var current = _store.Current;
        _logger.LogInformation("Cover upgrade {Mode}: {Count} folder(s) from {Cursor}, scope {Scope}, {Picked}",
            current.Mode, current.Total, current.Cursor, current.Scope,
            current.Selected is null ? "every album" : $"{current.Selected.Count} picked album(s)");

        for (var index = current.Cursor; index < current.Queue.Count; index++)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                _store.Update(run => run.Status = CoverUpgradeStatus.Interrupted);
                return;
            }
            if (_cancel)
            {
                _store.Update(run =>
                {
                    run.Status = CoverUpgradeStatus.Cancelled;
                    run.Reason = "Stopped from the dashboard.";
                    run.FinishedUtc = DateTime.UtcNow;
                });
                return;
            }

            var item = current.Queue[index];
            var looked = false;
            try
            {
                looked = await ProcessFolderAsync(item, current, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _store.Update(run =>
                {
                    run.Failed++;
                    run.Errors.Add($"{item.Folder}: {ex.Message}");
                });
            }
            _store.Update(run =>
            {
                run.Cursor = index + 1;
                run.Processed++;
                run.LastFolder = item.Folder;
            });
            if (looked && PauseBetweenAlbums > TimeSpan.Zero) await Task.Delay(PauseBetweenAlbums, stoppingToken);
        }

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

    private sealed record SongFile(string Path, string Artist, string? Album, string? Title,
        string? ReleaseId, string? ReleaseGroupId, int Side);

    /// <summary>The folders behind the picked albums, from the list they were picked from, keeping
    /// each folder's own file list (Octo's downloads only touch Octo's files).</summary>
    private static List<CoverUpgradeItem> QueueOf(CoverUpgradeRun previous, IReadOnlyCollection<string> ids)
    {
        var picked = ids.ToHashSet(StringComparer.Ordinal);
        var files = new Dictionary<string, List<string>?>(StringComparer.Ordinal);
        foreach (var item in previous.Queue) files.TryAdd(item.Folder, item.Files);
        return previous.Preview
            .Where(row => picked.Contains(row.Id))
            .Select(row => row.Folder)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(folder => folder, StringComparer.Ordinal)
            .Select(folder => new CoverUpgradeItem(folder, files.GetValueOrDefault(folder)))
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
            using var file = TagLib.File.Create(path);
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
        foreach (var path in paths.Where(File.Exists))
        {
            var song = ReadSong(path);
            if (song is null) _store.Update(r => r.Kept++);
            else songs.Add(song);
        }

        var looked = false;
        var picked = run.Selected?.ToHashSet(StringComparer.Ordinal);
        // An album without a name is matched song by song, as a single.
        foreach (var album in songs.GroupBy(song => string.IsNullOrWhiteSpace(song.Album)
                     ? "\u0001" + song.Path
                     : $"{SongIdentity.Key(song.Artist)}|{SongIdentity.Key(song.Album)}"))
        {
            if (ct.IsCancellationRequested || _cancel) break;
            var id = AlbumId(item.Folder, album.Key);
            if (picked is not null && !picked.Contains(id)) continue;
            var first = album.First();
            var have = album.Min(song => song.Side);
            var folderCover = run.FolderCovers && album.Count() == songs.Count ? FolderCover(item.Folder) : null;
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
                    album.Count(), folderCover is not null, "soft", first.Path);
                _store.Update(r =>
                {
                    r.Soft++;
                    if (r.Preview.Count < CoverUpgradeStore.MaxPreviewRows) r.Preview.Add(soft);
                });
                continue;
            }

            if (looked && PauseBetweenAlbums > TimeSpan.Zero) await Task.Delay(PauseBetweenAlbums, ct);
            looked = true;

            var found = await _finder.FindAsync(new AlbumCoverQuery(first.Artist, first.Album, first.Title,
                album.Select(s => s.ReleaseId).FirstOrDefault(rid => !string.IsNullOrEmpty(rid)),
                album.Select(s => s.ReleaseGroupId).FirstOrDefault(rid => !string.IsNullOrEmpty(rid))), ct);

            var upgradeFiles = found is not null
                ? album.Where(song => found.Side >= song.Side * MinimumGain && found.Side > song.Side).ToList()
                : [];
            var upgradeFolder = found is not null && folderCover is { } fc
                && found.Side >= fc.Side * MinimumGain && found.Side > fc.Side;
            if (found is null || (upgradeFiles.Count == 0 && !upgradeFolder))
            {
                // On a picked list, an album that stays as it is still says so.
                var none = new CoverUpgradeChange(id, item.Folder, first.Artist, first.Album, shown, found?.Side ?? 0,
                    found?.Source, 0, false, "none", first.Path);
                _store.Update(r =>
                {
                    r.Kept++;
                    if (picked is not null && r.Preview.Count < CoverUpgradeStore.MaxPreviewRows) r.Preview.Add(none);
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
            else written = upgradeFiles.Count;

            var change = new CoverUpgradeChange(id, item.Folder, first.Artist, first.Album, shown,
                found.Side, found.Source, written, upgradeFolder, run.DryRun ? "found" : "upgraded", first.Path);
            _store.Update(r =>
            {
                r.Upgraded++;
                r.Files += written;
                if (r.Preview.Count < CoverUpgradeStore.MaxPreviewRows) r.Preview.Add(change);
            });
        }
        return looked;
    }

    private SongFile? ReadSong(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var artist = TagWriterExtras.IsCompilation(file) ? "Various Artists"
                : !string.IsNullOrWhiteSpace(tag.FirstAlbumArtist) ? tag.FirstAlbumArtist : tag.FirstPerformer;
            if (string.IsNullOrWhiteSpace(artist) || (string.IsNullOrWhiteSpace(tag.Album) && string.IsNullOrWhiteSpace(tag.Title)))
                return null;
            var front = FrontOf(tag.Pictures);
            var side = front?.Data?.Data is { Length: > 0 } bytes && CoverImage.Measure(bytes) is { } size
                ? Math.Min(size.Width, size.Height) : 0;
            return new SongFile(path, artist, tag.Album, tag.Title, tag.MusicBrainzReleaseId,
                tag.MusicBrainzReleaseGroupId, side);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Cover upgrade could not read {Path}: {M}", path, ex.Message);
            return null;
        }
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
        if (scope == CoverUpgradeScope.OctoDownloads)
        {
            var library = handle.ServiceProvider.GetRequiredService<ILocalLibraryService>();
            return (await library.GetMappingsAsync())
                .Select(mapping => mapping.LocalPath)
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .Distinct(StringComparer.Ordinal)
                .GroupBy(path => Path.GetDirectoryName(path) ?? "")
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new CoverUpgradeItem(group.Key, group.OrderBy(p => p, StringComparer.Ordinal).ToList()))
                .ToList();
        }

        var fallback = _configuration["Library:DownloadPath"] ?? "/music";
        var root = handle.ServiceProvider.GetService<NavidromeIdentityService>()?.EffectiveDownloadPath(fallback) ?? fallback;
        if (!Directory.Exists(root))
        {
            _logger.LogWarning("Cover upgrade found no music folder at {Root}", root);
            return [];
        }
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => AudioExtensions.Contains(Path.GetExtension(path)))
            .Select(path => Path.GetDirectoryName(path) ?? "")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(dir => dir, StringComparer.Ordinal)
            .Select(dir => new CoverUpgradeItem(dir, null))
            .ToList();
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
