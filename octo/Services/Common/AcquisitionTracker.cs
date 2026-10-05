using System.Text.RegularExpressions;

namespace Octo.Services.Common;

/// <summary>Where one acquisition has got to. Sent to clients in lower case.</summary>
public enum AcquisitionState
{
    /// <summary>Accepted, waiting for the worker.</summary>
    Queued,
    /// <summary>Looking for a source or a peer.</summary>
    Searching,
    /// <summary>Bytes are flowing.</summary>
    Downloading,
    /// <summary>The duration check and the fingerprint.</summary>
    Verifying,
    /// <summary>Placed, tagged and registered; waiting for Navidrome to show it.</summary>
    Importing,
    Done,
    Failed,
}

/// <summary>One acquisition as a reader sees it. A copy, so it never changes under them.</summary>
public sealed record AcquisitionSnapshot(
    string Id,
    string Provider,
    string ExternalId,
    string? Artist,
    string? Title,
    string? Album,
    IReadOnlyList<string> RequestedBy,
    string? Source,
    AcquisitionState State,
    double? Progress,
    long? BytesDone,
    long? BytesTotal,
    DateTime StartedAt,
    DateTime UpdatedAt,
    string? Error,
    string? LibraryId,
    int? Ahead = null,
    string? Note = null);

/// <summary>How a row ended. LibraryId is set only for a song Navidrome showed; AlbumKeys are
/// the hearted albums the song was fetched for.</summary>
public sealed record AcquisitionEnd(string Key, string? Artist, string? Title, bool Done,
    string? LibraryId, IReadOnlyList<string> AlbumKeys);

/// <summary>
/// Live progress of every hearted download, from the moment the star is accepted until a while
/// after it ends, so a client can draw a ring on the button it was tapped on.
///
/// Observation only. Nothing in the download path reads it back, and no method here throws: a
/// bookkeeping mistake must never cost anyone a song. Held in memory and gone on a restart,
/// which is fine, because the fetched-songs log still says what finished.
///
/// Keyed by provider and external id, the two things every stage of the pipeline already has.
/// The id the client starred rides along so the client can find its own row again.
/// </summary>
public sealed class AcquisitionTracker
{
    /// <summary>How long a finished or failed entry stays visible.</summary>
    internal static readonly TimeSpan FinishedRetention = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long an entry that is still running may go without any news before it is dropped.
    /// Generous on purpose: a star can wait behind a whole album for hours. This only exists so
    /// a run that ended somewhere nothing reported it cannot sit in the list forever.
    /// </summary>
    internal static readonly TimeSpan StalledRetention = TimeSpan.FromHours(24);

    internal const int Capacity = 500;

    private const int MaxErrorLength = 160;

    private sealed class Entry
    {
        public required string Provider { get; init; }
        public required string ExternalId { get; init; }
        public string? ClientId { get; set; }
        public string? Artist { get; set; }
        public string? Title { get; set; }
        public string? Album { get; set; }
        public HashSet<string> Owners { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Source { get; set; }
        public AcquisitionState State { get; set; }
        public double? Progress { get; set; }
        public long? BytesDone { get; set; }
        public long? BytesTotal { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? Error { get; set; }
        public string? LibraryId { get; set; }

        /// <summary>A short line for the listener, such as which source is being tried now.</summary>
        public string? Note { get; set; }

        /// <summary>Order of arrival. Tracks an album walk lists together share a start time,
        /// so this, not the time, is what says which of them is ahead.</summary>
        public long Seq { get; set; }

        /// <summary>Bumped on every restart, so a watcher left over from an earlier run of the
        /// same song can never finish the new one.</summary>
        public int Run { get; set; }

        public bool Finished => State is AcquisitionState.Done or AcquisitionState.Failed;
    }

    /// <summary>Who hearted an album, so its tracks are theirs when the walk lists them.</summary>
    private sealed class AlbumClaim
    {
        public HashSet<string> Owners { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TrackKeys { get; } = new(StringComparer.Ordinal);
        public DateTime UpdatedAt { get; set; }
    }

    private readonly object _lock = new();
    private long _seq;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AlbumClaim> _albums = new(StringComparer.Ordinal);
    private readonly ILogger<AcquisitionTracker> _logger;
    private readonly IServiceProvider? _services;
    private readonly TimeProvider _time;

    /// <summary>
    /// How an imported song is looked for in Navidrome: every <see cref="VisibilityPoll"/> for
    /// <see cref="VisibilityAttempts"/> tries, then every <see cref="SlowVisibilityPoll"/> for
    /// <see cref="SlowVisibilityAttempts"/> more. About ten minutes in all, because a song the
    /// app is waiting on should arrive with its id, not after the next full sync.
    /// </summary>
    internal TimeSpan VisibilityPoll { get; set; } = TimeSpan.FromSeconds(5);
    internal int VisibilityAttempts { get; set; } = 36;
    internal TimeSpan SlowVisibilityPoll { get; set; } = TimeSpan.FromSeconds(15);
    internal int SlowVisibilityAttempts { get; set; } = 28;

    /// <summary>
    /// After this many tries without the song, Navidrome is asked to scan once, past the
    /// debounce. A scan takes seconds, so a song still missing after a minute usually means the
    /// scan that should have found it was swallowed by the debounce behind another one.
    /// </summary>
    internal int RescanAfterAttempts { get; set; } = 12;

    /// <summary>Asks Navidrome to scan. Tests set it; otherwise the library service does.</summary>
    internal Func<Task>? Rescan { get; set; }

    /// <summary>
    /// Finds the Navidrome id of a placed file. Null means nothing can look, and an imported song
    /// is then done at once. Tests set it; otherwise the song path resolver answers.
    /// </summary>
    internal Func<string, string, string, CancellationToken, Task<string?>>? LibraryLookup { get; set; }

    public AcquisitionTracker(ILogger<AcquisitionTracker> logger, IServiceProvider? services = null,
        TimeProvider? time = null)
    {
        _logger = logger;
        _services = services;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Told when a row ends, outside the lock. A listener that throws is logged and
    /// skipped, so it can never cost anyone a song.</summary>
    public event Action<AcquisitionEnd>? Ended;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    internal static string KeyOf(string provider, string externalId) =>
        $"{provider.Trim().ToLowerInvariant()}:{externalId}";

    // ---------------------------------------------------------------------------------------
    // Starting
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A star on a song was accepted. Starts an entry, or restarts one that already finished,
    /// or adds this user to one that is still running (a second heart joins the same download).
    /// </summary>
    public void Begin(string provider, string externalId, string? clientId, string? requestedBy,
        string? artist = null, string? title = null, string? album = null)
    {
        Guard(nameof(Begin), () =>
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(externalId)) return;
            lock (_lock)
            {
                var entry = Open(provider, externalId, restartFinished: true);
                if (!string.IsNullOrWhiteSpace(clientId)) entry.ClientId = clientId;
                AddOwner(entry.Owners, requestedBy);
                Name(entry, artist, title, album);
                Prune();
            }
        });
    }

    /// <summary>A star on an album was accepted. Its tracks are listed as the walk reaches them.</summary>
    public void BeginAlbum(string provider, string albumId, string? requestedBy)
    {
        Guard(nameof(BeginAlbum), () =>
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(albumId)) return;
            lock (_lock)
            {
                var key = KeyOf(provider, albumId);
                if (!_albums.TryGetValue(key, out var claim)) _albums[key] = claim = new AlbumClaim();
                AddOwner(claim.Owners, requestedBy);
                claim.UpdatedAt = Now;
                Prune();
            }
        });
    }

    /// <summary>
    /// An album walk is about to fetch these tracks. They belong to whoever hearted the album,
    /// or whoever hearted the song that started the walk. A walk nobody hearted (a play in Album
    /// mode) lists nothing. A track already running or already done is left as it is; one that
    /// failed before is queued again, because this is another go at it.
    /// </summary>
    public void Announce(string provider, string? albumId, string? parentExternalId,
        IEnumerable<(string ExternalId, string? Artist, string? Title, string? Album)> tracks)
    {
        Guard(nameof(Announce), () =>
        {
            if (string.IsNullOrWhiteSpace(provider)) return;
            lock (_lock)
            {
                var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AlbumClaim? claim = null;
                if (!string.IsNullOrWhiteSpace(albumId)
                    && _albums.TryGetValue(KeyOf(provider, albumId), out claim))
                    owners.UnionWith(claim.Owners);
                if (!string.IsNullOrWhiteSpace(parentExternalId)
                    && _entries.TryGetValue(KeyOf(provider, parentExternalId), out var parent))
                    owners.UnionWith(parent.Owners);
                if (owners.Count == 0) return;

                foreach (var track in tracks)
                {
                    if (string.IsNullOrWhiteSpace(track.ExternalId)) continue;
                    var key = KeyOf(provider, track.ExternalId);
                    claim?.TrackKeys.Add(key);
                    var restart = !_entries.TryGetValue(key, out var existing)
                        || existing.State == AcquisitionState.Failed;
                    var entry = restart
                        ? Open(provider, track.ExternalId, restartFinished: true)
                        : existing!;
                    entry.Owners.UnionWith(owners);
                    Name(entry, track.Artist, track.Title, track.Album);
                }
                if (claim is not null) claim.UpdatedAt = Now;
                Prune();
            }
        });
    }

    // ---------------------------------------------------------------------------------------
    // Moving along. Every one of these touches only an entry that is still running, so a stage
    // reported by a play of a song that was hearted an hour ago cannot reopen a finished row.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Move to a stage, and name the source when it is known. A note replaces the last one; a
    /// stage without one keeps it, so "trying YouTube" stays up while YouTube searches.
    /// </summary>
    public void Stage(string provider, string externalId, AcquisitionState state, string? source = null,
        string? note = null)
    {
        Guard(nameof(Stage), () =>
        {
            if (state is AcquisitionState.Done or AcquisitionState.Failed)
            {
                if (state == AcquisitionState.Done) Complete(provider, externalId);
                else Fail(provider, externalId, null);
                return;
            }
            Update(provider, externalId, entry =>
            {
                // Back to looking means the last transfer is gone, whatever it had reached.
                if (state is AcquisitionState.Queued or AcquisitionState.Searching)
                {
                    entry.BytesDone = null;
                    entry.BytesTotal = null;
                }
                entry.State = state;
                entry.Progress = null;
                if (!string.IsNullOrWhiteSpace(source)) entry.Source = source;
                if (!string.IsNullOrWhiteSpace(note)) entry.Note = note.Trim();
            });
        });
    }

    /// <summary>Fill in the names once the pipeline has them. Empty values never overwrite.</summary>
    public void Describe(string provider, string externalId, string? artist, string? title, string? album)
    {
        Guard(nameof(Describe), () => Update(provider, externalId, entry => Name(entry, artist, title, album),
            touch: false));
    }

    /// <summary>
    /// A transfer started or moved on. Any figure may be missing; progress comes from the bytes
    /// when both are known, otherwise from the percentage. Nothing moved yet reads as unknown
    /// rather than zero: slskd reports a transfer in progress before its first byte, and a
    /// ring drawn at 0% looks like a download that died.
    /// </summary>
    public void Transfer(string provider, string externalId, long? bytesDone, long? bytesTotal,
        double? percentComplete = null, string? source = null)
    {
        Guard(nameof(Transfer), () => Update(provider, externalId, entry =>
        {
            entry.State = AcquisitionState.Downloading;
            if (!string.IsNullOrWhiteSpace(source)) entry.Source = source;
            entry.BytesDone = bytesDone is >= 0 ? bytesDone : null;
            entry.BytesTotal = bytesTotal is > 0 ? bytesTotal : null;
            var fraction = FractionOf(bytesDone, bytesTotal, percentComplete);
            entry.Progress = fraction is > 0d ? fraction : null;
        }));
    }

    /// <summary>
    /// The file is in the library folder and registered. The entry reads importing until
    /// Navidrome can see the song, then done with its id; if nothing can look, or Navidrome has
    /// not shown it within a few minutes, done without one. The file is there either way.
    /// </summary>
    public void Imported(string provider, string externalId, string? artist, string? title, string? path)
    {
        Guard(nameof(Imported), () =>
        {
            int run;
            lock (_lock)
            {
                if (!_entries.TryGetValue(KeyOf(provider, externalId), out var entry) || entry.Finished) return;
                entry.State = AcquisitionState.Importing;
                entry.Progress = null;
                entry.UpdatedAt = Now;
                run = entry.Run;
                // A song found already in the library arrives here before anything named it.
                if (string.IsNullOrWhiteSpace(artist)) artist = entry.Artist;
                if (string.IsNullOrWhiteSpace(title)) title = entry.Title;
            }

            var lookup = ResolveLookup();
            if (lookup is null || string.IsNullOrWhiteSpace(path)
                || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            {
                Finish(KeyOf(provider, externalId), run, null);
                return;
            }
            _ = Task.Run(() => WatchUntilVisibleAsync(KeyOf(provider, externalId), run, lookup, artist, title, path));
        });
    }

    /// <summary>In the library.</summary>
    public void Complete(string provider, string externalId, string? libraryId = null)
    {
        Guard(nameof(Complete), () =>
        {
            int run;
            lock (_lock)
            {
                if (!_entries.TryGetValue(KeyOf(provider, externalId), out var entry) || entry.Finished) return;
                run = entry.Run;
            }
            Finish(KeyOf(provider, externalId), run, libraryId);
        });
    }

    /// <summary>
    /// The last source gave up. Call this only at the end of the chain: a source that fails
    /// while another is still to try is not a failure the listener should see.
    /// </summary>
    public void Fail(string provider, string externalId, string? error)
    {
        Guard(nameof(Fail), () =>
        {
            AcquisitionEnd? ended = null;
            Update(provider, externalId, entry =>
            {
                entry.State = AcquisitionState.Failed;
                entry.Progress = null;
                entry.Error = UserSafe(error) ?? "The download failed.";
                ended = EndOf(KeyOf(provider, externalId), entry);
            });
            Raise(ended);
        });
    }

    /// <summary>Fail every track of a hearted album that is still running.</summary>
    public void FailAlbum(string provider, string albumId, string? error)
    {
        Guard(nameof(FailAlbum), () =>
        {
            List<string> keys;
            lock (_lock)
            {
                if (!_albums.TryGetValue(KeyOf(provider, albumId), out var claim)) return;
                keys = claim.TrackKeys.ToList();
            }
            foreach (var key in keys)
            {
                var split = key.IndexOf(':');
                Fail(key[..split], key[(split + 1)..], error);
            }
        });
    }

    // ---------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------

    /// <summary>Everything, newest first. For the dashboard.</summary>
    public IReadOnlyList<AcquisitionSnapshot> All() => Read(null);

    /// <summary>Only the entries this user asked for, newest first.</summary>
    public IReadOnlyList<AcquisitionSnapshot> ForUser(string username) =>
        string.IsNullOrWhiteSpace(username) ? [] : Read(username.Trim());

    private IReadOnlyList<AcquisitionSnapshot> Read(string? username)
    {
        try
        {
            lock (_lock)
            {
                Prune();
                var running = _entries.Values.Where(entry => !entry.Finished).ToList();
                return _entries.Values
                    .Where(entry => username is null || entry.Owners.Contains(username))
                    .OrderByDescending(entry => entry.StartedAt)
                    .Select(entry => Snapshot(entry, AheadOf(entry, running)))
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Acquisition tracker read failed: {Message}", ex.Message);
            return [];
        }
    }

    private static AcquisitionSnapshot Snapshot(Entry entry, int? ahead) => new(
        entry.ClientId ?? entry.ExternalId, entry.Provider, entry.ExternalId,
        entry.Artist, entry.Title, entry.Album,
        entry.Owners.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
        entry.Source, entry.State, entry.Progress, entry.BytesDone, entry.BytesTotal,
        entry.StartedAt, entry.UpdatedAt, entry.Error, entry.LibraryId, ahead, entry.Note);

    /// <summary>
    /// How many downloads, anyone's, are ahead of a queued one. Octo fetches one song at a
    /// time, so every running entry that arrived first is in front of it. Only a count leaves
    /// here, never whose they are. Null once it is past the queue.
    /// </summary>
    private static int? AheadOf(Entry entry, IReadOnlyList<Entry> running) =>
        entry.State == AcquisitionState.Queued
            ? running.Count(other => !ReferenceEquals(other, entry) && other.Seq < entry.Seq)
            : null;

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Progress from 0 to 1. The bytes win when both are known, because slskd's percentage is
    /// rounded; the percentage (0 to 100) is the fallback. Null when neither says anything.
    /// </summary>
    internal static double? FractionOf(long? bytesDone, long? bytesTotal, double? percentComplete)
    {
        double? fraction = bytesDone is >= 0 && bytesTotal is > 0
            ? (double)bytesDone.Value / bytesTotal.Value
            : percentComplete is { } percent && !double.IsNaN(percent)
                ? percent / 100d
                : null;
        return fraction is { } value ? Math.Round(Math.Clamp(value, 0d, 1d), 4) : null;
    }

    // A sentence ends at a full stop after a word of four or more characters and before a
    // capital, so "Mr. Brightside" and "St. Vincent" stay whole.
    private static readonly Regex SentenceEnd = new(@"(?<=[\p{L}\p{N}'""\)]{4})\.\s+(?=\p{Lu})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A path on the server is nobody's business away from home. "AC/DC" is not a path: a
    // letter or digit before the slash rules it out.
    private static readonly Regex ServerPath = new(@"(?<![\p{L}\p{N}])(?:[A-Za-z]:\\|/)[^\s'""(),;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The first sentence of an error, with server paths taken out and the length capped. The
    /// messages behind a failed download are written for the log, and the rest of them (which
    /// peer, which slskd setting) means nothing on a phone.
    /// </summary>
    internal static string? UserSafe(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var text = message.Split('\n')[0].Trim();
        var end = SentenceEnd.Match(text);
        if (end.Success) text = text[..end.Index];
        text = ServerPath.Replace(text, "a path").Trim().TrimEnd('.');
        if (text.Length > MaxErrorLength) text = text[..(MaxErrorLength - 3)].TrimEnd() + "...";
        return text.Length == 0 ? null : text;
    }

    /// <summary>The entry for this song, made or restarted as needed. Caller holds the lock.</summary>
    private Entry Open(string provider, string externalId, bool restartFinished)
    {
        var key = KeyOf(provider, externalId);
        var now = Now;
        if (_entries.TryGetValue(key, out var entry))
        {
            if (!(restartFinished && entry.Finished)) return entry;
            entry.Run++;
            entry.Owners.Clear();
            entry.Source = null;
            entry.Progress = null;
            entry.BytesDone = null;
            entry.BytesTotal = null;
            entry.Error = null;
            entry.LibraryId = null;
            entry.Note = null;
        }
        else
        {
            entry = new Entry { Provider = provider.Trim().ToLowerInvariant(), ExternalId = externalId };
            _entries[key] = entry;
        }
        entry.State = AcquisitionState.Queued;
        entry.Seq = ++_seq;
        entry.StartedAt = now;
        entry.UpdatedAt = now;
        return entry;
    }

    private void Update(string provider, string externalId, Action<Entry> change, bool touch = true)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(externalId)) return;
        lock (_lock)
        {
            if (!_entries.TryGetValue(KeyOf(provider, externalId), out var entry) || entry.Finished) return;
            change(entry);
            if (touch) entry.UpdatedAt = Now;
        }
    }

    private void Finish(string key, int run, string? libraryId)
    {
        AcquisitionEnd ended;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Finished || entry.Run != run) return;
            entry.State = AcquisitionState.Done;
            entry.Progress = null;
            entry.Error = null;
            entry.Note = null;
            if (!string.IsNullOrWhiteSpace(libraryId)) entry.LibraryId = libraryId;
            entry.UpdatedAt = Now;
            ended = EndOf(key, entry);
        }
        Raise(ended);
    }

    /// <summary>Caller holds the lock.</summary>
    private AcquisitionEnd EndOf(string key, Entry entry) => new(key, entry.Artist, entry.Title,
        entry.State == AcquisitionState.Done, entry.State == AcquisitionState.Done ? entry.LibraryId : null,
        _albums.Where(pair => pair.Value.TrackKeys.Contains(key)).Select(pair => pair.Key).ToList());

    private void Raise(AcquisitionEnd? ended)
    {
        if (ended is null || Ended is not { } listeners) return;
        foreach (var listener in listeners.GetInvocationList().Cast<Action<AcquisitionEnd>>())
        {
            try { listener(ended); }
            catch (Exception ex) { _logger.LogDebug("Acquisition end listener failed: {Message}", ex.Message); }
        }
    }

    private async Task WatchUntilVisibleAsync(string key, int run,
        Func<string, string, string, CancellationToken, Task<string?>> lookup,
        string artist, string title, string path)
    {
        try
        {
            var fast = Math.Max(1, VisibilityAttempts);
            var total = fast + Math.Max(0, SlowVisibilityAttempts);
            for (var attempt = 0; attempt < total; attempt++)
            {
                if (attempt > 0) await Task.Delay(attempt < fast ? VisibilityPoll : SlowVisibilityPoll);
                if (attempt > 0 && attempt == RescanAfterAttempts) await RescanOnceAsync(path);
                lock (_lock)
                {
                    // Restarted or finished by something else while this waited: not ours now.
                    if (!_entries.TryGetValue(key, out var entry) || entry.Run != run || entry.Finished) return;
                }
                string? id = null;
                try { id = await lookup(artist, title, path, CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug("Library lookup for {Path} failed: {Message}", path, ex.Message); }
                if (!string.IsNullOrWhiteSpace(id))
                {
                    Finish(key, run, id);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Watching {Path} for Navidrome failed: {Message}", path, ex.Message);
        }
        // Registered and in the folder; Navidrome is just slow to say so. Done is still the
        // truth, and the app finds the song on its own from here.
        Finish(key, run, null);
    }

    private async Task RescanOnceAsync(string path)
    {
        try
        {
            var rescan = Rescan ?? ResolveRescan();
            if (rescan is null) return;
            _logger.LogInformation("{Path} is not in Navidrome yet; asking it to scan again", path);
            await rescan();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Rescan for {Path} failed: {Message}", path, ex.Message);
        }
    }

    private Func<Task>? ResolveRescan()
    {
        var library = _services?.GetService<Octo.Services.Local.ILocalLibraryService>();
        return library is null ? null : () => library.TriggerLibraryScanAsync(force: true);
    }

    private Func<string, string, string, CancellationToken, Task<string?>>? ResolveLookup()
    {
        if (LibraryLookup is not null) return LibraryLookup;
        if (_services is null) return null;
        var identity = _services.GetService<Octo.Services.Subsonic.NavidromeIdentityService>();
        var resolver = _services.GetService<Octo.Services.Library.NavidromeSongPathResolver>();
        // Without an admin identity the resolver can never answer, so waiting would only hold
        // the ring on "importing" for minutes for nothing.
        if (identity?.GetScanAuth() is null || resolver is null) return null;
        return resolver.FindIdByPathAsync;
    }

    /// <summary>Drop what has expired, then the oldest past the cap. Caller holds the lock.</summary>
    private void Prune()
    {
        var now = Now;
        foreach (var (key, entry) in _entries.ToList())
        {
            var age = now - entry.UpdatedAt;
            if (entry.Finished ? age >= FinishedRetention : age >= StalledRetention) _entries.Remove(key);
        }
        foreach (var (key, claim) in _albums.ToList())
            if (now - claim.UpdatedAt >= StalledRetention) _albums.Remove(key);

        if (_entries.Count <= Capacity) return;
        // Finished rows go first: a running download is the one somebody is watching.
        foreach (var (key, _) in _entries
                     .OrderBy(pair => pair.Value.Finished ? 0 : 1)
                     .ThenBy(pair => pair.Value.UpdatedAt)
                     .Take(_entries.Count - Capacity)
                     .ToList())
            _entries.Remove(key);
    }

    private static void AddOwner(HashSet<string> owners, string? username)
    {
        if (!string.IsNullOrWhiteSpace(username)) owners.Add(username.Trim());
    }

    private static void Name(Entry entry, string? artist, string? title, string? album)
    {
        if (!string.IsNullOrWhiteSpace(artist)) entry.Artist = artist;
        if (!string.IsNullOrWhiteSpace(title)) entry.Title = title;
        if (!string.IsNullOrWhiteSpace(album)) entry.Album = album;
    }

    private void Guard(string operation, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            _logger.LogDebug("Acquisition tracker {Operation} failed: {Message}", operation, ex.Message);
        }
    }
}
