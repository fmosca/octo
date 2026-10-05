using System.Text.Json;
using System.Text.Json.Serialization;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Fingerprint;

namespace Octo.Services.Library;

public enum NoticeState { Waiting, Queued, Kept, Acted, Dismissed, Expired }

/// <summary>What raised a question. Download is 0 so every entry written before this loads as one.</summary>
public enum NoticeOrigin { Download, LibrarySweep }

/// <summary>One question Octo is asking one person about one track.</summary>
public sealed record NoticeEntry
{
    public string Key { get; init; } = "";
    public NoticeKind Kind { get; init; }
    public string Username { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Album { get; init; }
    public string? NavidromeId { get; init; }
    public string? GroupKey { get; init; }
    public int Order { get; init; }
    public NoticeState State { get; init; }

    /// <summary>What the dashboard shows as the reason Octo is asking.</summary>
    public string Reason { get; init; } = "";

    /// <summary>Why verification could not decide, which is what makes an answer submittable.</summary>
    public InconclusiveReason Cause { get; init; }

    /// <summary>A library sweep question is never sent to AcoustID: keeping a file says it is
    /// fine to keep, not that its tags name the right recording.</summary>
    public NoticeOrigin Origin { get; init; }

    public string? Fingerprint { get; init; }
    public int DurationSeconds { get; init; }
    public string? CandidateRecordingId { get; init; }
    public string? FileFormat { get; init; }
    public bool Submitted { get; init; }
    public int LookupAttempts { get; init; }
    public DateTime NextLookupUtc { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? QueuedUtc { get; init; }
    public DateTime? ResolvedUtc { get; init; }

    [JsonIgnore] public bool IsOpen => State is NoticeState.Waiting or NoticeState.Queued;
}

/// <summary>
/// What Octo has asked each person about, and what they answered (#47, #53).
///
/// Resolved entries are kept, bounded, so a rescan never asks again about something a person
/// already settled: "a track resolved once should not come back". A fingerprint is dropped the
/// moment its entry resolves any way but Keep, and once it has been sent or refused, so the file
/// stays small.
/// </summary>
public sealed class NoticeQueue : IDisposable
{
    public const int MaxEntries = 5000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A person who removes a track from Review and then drops it into Delete answered with
    /// Delete, even though the sweep saw the removal first.
    /// </summary>
    private static readonly TimeSpan ActedAfterDismissWindow = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string? _path;
    private readonly ILogger<NoticeQueue>? _logger;
    private readonly Timer? _flushTimer;
    private readonly object _lock = new();
    private readonly object _flushLock = new();
    private readonly Dictionary<string, NoticeEntry> _entries = new(StringComparer.Ordinal);
    private int _dirty;

    public NoticeQueue(string? path = null, ILogger<NoticeQueue>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _logger = logger;
        if (_path is null) return;

        Load();
        _flushTimer = new Timer(_ => Flush(), null, FlushInterval, FlushInterval);
    }

    public static string ReviewKey(string username, string localPath) =>
        $"review|{username.Trim().ToLowerInvariant()}|{localPath}";

    /// <summary>
    /// Ask <paramref name="username"/> about a download verification could not settle. A file
    /// already asked about is never asked about again, in any state, which is what stops a
    /// dismissal from being undone by the next download of the same file.
    /// </summary>
    public bool AddReview(string username, string localPath, Song song, VerificationResult verdict,
        NoticeOrigin origin = NoticeOrigin.Download)
    {
        var key = ReviewKey(username, localPath);
        lock (_lock)
        {
            if (_entries.ContainsKey(key)) return false;
            _entries[key] = new NoticeEntry
            {
                Key = key,
                Kind = NoticeKind.Review,
                Username = username.Trim(),
                LocalPath = localPath,
                Artist = song.Artist,
                Title = song.Title,
                Album = song.Album,
                State = NoticeState.Waiting,
                Reason = verdict.Reason switch
                {
                    InconclusiveReason.NoEntry => "AcoustID has never heard this recording",
                    InconclusiveReason.BelowThreshold => "AcoustID was not sure what this is",
                    InconclusiveReason.SourceDisagreed => $"AcoustID thinks this is {verdict.Describe()}",
                    InconclusiveReason.SoundsLikeAnother => $"Sounds like {verdict.Describe()}, not what its tags say",
                    InconclusiveReason.LengthOff =>
                        $"It runs {Clock(verdict.DurationSeconds)}, but the recording it matched runs {Clock(verdict.Match?.DurationSeconds ?? 0)}",
                    _ => "Octo could not check this download",
                },
                Cause = verdict.Reason,
                Origin = origin,
                // Dropped for a library question, so a Keep on one can never be submitted.
                Fingerprint = origin == NoticeOrigin.LibrarySweep ? null : verdict.Fingerprint,
                DurationSeconds = verdict.DurationSeconds,
                CandidateRecordingId = origin == NoticeOrigin.LibrarySweep ? null : verdict.CandidateRecordingId,
                FileFormat = Path.GetExtension(localPath).TrimStart('.').ToLowerInvariant(),
                NextLookupUtc = DateTime.UtcNow,
            };
            Trim();
        }
        MarkDirty();
        return true;
    }

    private static string Clock(int seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    /// <summary>Every file a Review question was ever about, open or answered, for anyone.</summary>
    public IReadOnlySet<string> ReviewedPaths()
    {
        lock (_lock)
            return _entries.Values.Where(entry => entry.Kind == NoticeKind.Review)
                .Select(entry => entry.LocalPath).ToHashSet(StringComparer.Ordinal);
    }

    public int OpenCount(NoticeOrigin origin)
    {
        lock (_lock) return _entries.Values.Count(entry => entry.IsOpen && entry.Origin == origin);
    }

    public IReadOnlyList<NoticeEntry> ForUser(string username, NoticeKind kind)
    {
        lock (_lock)
            return _entries.Values
                .Where(entry => entry.Kind == kind && entry.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    /// <summary>Review entries still waiting for Navidrome to scan the file, whose next lookup is due.</summary>
    public IReadOnlyList<NoticeEntry> DueForLookup(DateTime nowUtc, int limit)
    {
        lock (_lock)
            return _entries.Values
                .Where(entry => entry.Kind == NoticeKind.Review && entry.State == NoticeState.Waiting
                    && entry.NavidromeId is null && entry.NextLookupUtc <= nowUtc)
                .OrderBy(entry => entry.CreatedUtc)
                .Take(limit)
                .ToList();
    }

    public bool IsQueued(string username, string navidromeId)
    {
        lock (_lock)
            return _entries.Values.Any(entry => entry.State == NoticeState.Queued
                && entry.NavidromeId == navidromeId
                && entry.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public void SetNavidromeId(string key, string navidromeId) =>
        Update(key, entry => entry with { NavidromeId = navidromeId });

    /// <summary>Not scanned yet: look again after 1, 2, 4, 8, then 16 minutes.</summary>
    public void DeferLookup(string key, DateTime nowUtc) =>
        Update(key, entry => entry with
        {
            LookupAttempts = entry.LookupAttempts + 1,
            NextLookupUtc = nowUtc.AddMinutes(Math.Pow(2, Math.Min(entry.LookupAttempts, 4))),
        });

    public void Resolve(string key, NoticeState state) =>
        Update(key, entry => entry with
        {
            State = state,
            ResolvedUtc = DateTime.UtcNow,
            Fingerprint = state == NoticeState.Kept ? entry.Fingerprint : null,
        });

    public void MarkQueued(IEnumerable<string> keys)
    {
        foreach (var key in keys)
            Update(key, entry => entry with { State = NoticeState.Queued, QueuedUtc = DateTime.UtcNow });
    }

    /// <summary>
    /// A person kept this track, which answers every question Octo had open for them about it. For
    /// Review the question was about the file, so it is answered for everyone asked. For
    /// Duplicates, keeping one copy says the copies are on purpose, so that person's whole group
    /// is settled. A rating or the Keep playlist cannot say which playlist it came from, so a
    /// track in both is answered in both. Returns the entry Octo had open for this person, Review
    /// first, or null when Octo never asked them about this track.
    /// </summary>
    public NoticeEntry? MarkKept(string username, string navidromeId)
    {
        List<NoticeEntry> mine;
        lock (_lock)
        {
            mine = _entries.Values.Where(entry => entry.IsOpen && entry.NavidromeId == navidromeId
                    && entry.Username.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Kind)
                .ToList();
            if (mine.Count == 0) return null;

            var now = DateTime.UtcNow;
            foreach (var asked in mine)
            foreach (var entry in _entries.Values.Where(entry => entry.IsOpen && entry.Kind == asked.Kind).ToList())
            {
                var answered = asked.Kind == NoticeKind.Review
                    ? entry.NavidromeId == navidromeId || entry.LocalPath == asked.LocalPath
                    : entry.GroupKey == asked.GroupKey
                      && entry.Username.Equals(asked.Username, StringComparison.OrdinalIgnoreCase);
                if (!answered) continue;
                _entries[entry.Key] = entry with
                {
                    State = asked.Kind == NoticeKind.Review ? NoticeState.Kept : NoticeState.Dismissed,
                    ResolvedUtc = now,
                    // One answer is one submission. A download nobody requested is asked of every
                    // allowed user, and each of their entries carries the same fingerprint.
                    Fingerprint = entry.Key == asked.Key ? entry.Fingerprint : null,
                };
            }
        }
        MarkDirty();
        return mine[0];
    }

    /// <summary>
    /// An action ran on this track, so every open question about it is answered, for everyone:
    /// the file it was about has gone or changed. The rest of a duplicate group it belonged to
    /// is no longer the group Octo asked about, so those questions expire, and the next scan asks
    /// again if copies remain.
    /// </summary>
    public void MarkActed(string navidromeId)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in _entries.Values.Where(entry => entry.NavidromeId == navidromeId).ToList())
            {
                var recentlyDismissed = entry.State == NoticeState.Dismissed
                    && entry.ResolvedUtc is { } at && now - at < ActedAfterDismissWindow;
                if (!entry.IsOpen && !recentlyDismissed) continue;
                _entries[entry.Key] = entry with { State = NoticeState.Acted, ResolvedUtc = now, Fingerprint = null };
                if (entry.GroupKey is { } group) groups.Add(group);
            }
            foreach (var entry in _entries.Values
                         .Where(entry => entry.IsOpen && entry.GroupKey is { } group && groups.Contains(group)).ToList())
                _entries[entry.Key] = entry with { State = NoticeState.Expired, ResolvedUtc = now };
        }
        MarkDirty();
    }

    public static string DuplicateKey(string username, string groupKey, string trackId) =>
        $"dup|{username.Trim().ToLowerInvariant()}|{groupKey}|{trackId}";

    /// <summary>
    /// Bring the Duplicates questions in line with a library walk (#53): one entry per allowed
    /// user per copy, the copy worth keeping first. Only a scan settles a duplicate by itself: a
    /// group no longer found means a copy went, so its open questions expire. A group someone
    /// settled stays settled for them, and a group that expired and is found again is asked again.
    /// A walk that did not finish adds what it found and expires nothing. Returns how many
    /// questions it opened.
    /// </summary>
    public int SyncDuplicates(IReadOnlyList<DuplicateGroup> groups, IEnumerable<string> users, bool complete)
    {
        var now = DateTime.UtcNow;
        var owners = users.Select(user => user.Trim()).Where(user => user.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var live = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var opened = 0;
        lock (_lock)
        {
            if (complete)
                foreach (var entry in _entries.Values.Where(entry => entry.Kind == NoticeKind.Duplicates && entry.IsOpen
                             && !live.Contains(entry.GroupKey ?? "")).ToList())
                    _entries[entry.Key] = entry with { State = NoticeState.Expired, ResolvedUtc = now };

            var settled = _entries.Values
                .Where(entry => entry.Kind == NoticeKind.Duplicates && entry.GroupKey is not null
                    && entry.State is NoticeState.Dismissed or NoticeState.Kept or NoticeState.Acted)
                .Select(entry => $"{entry.Username.ToLowerInvariant()}|{entry.GroupKey}")
                .ToHashSet(StringComparer.Ordinal);

            foreach (var group in groups)
            foreach (var user in owners)
            {
                if (settled.Contains($"{user.ToLowerInvariant()}|{group.Key}")) continue;

                for (var i = 0; i < group.Tracks.Count; i++)
                {
                    var track = group.Tracks[i];
                    var key = DuplicateKey(user, group.Key, track.Id);
                    var reason = i == 0
                        ? $"The best of {group.Tracks.Count} copies: {Describe(track)}"
                        : $"{Describe(track)}, also in the library as {Describe(group.Tracks[0])}";
                    if (_entries.TryGetValue(key, out var existing))
                    {
                        if (existing.State != NoticeState.Expired) continue;
                        _entries[key] = existing with
                        {
                            State = NoticeState.Waiting, Order = i, Reason = reason,
                            CreatedUtc = now, QueuedUtc = null, ResolvedUtc = null,
                        };
                        opened++;
                        continue;
                    }
                    _entries[key] = new NoticeEntry
                    {
                        Key = key,
                        Kind = NoticeKind.Duplicates,
                        Username = user,
                        Artist = track.Artist,
                        Title = track.Title,
                        Album = track.Album,
                        NavidromeId = track.Id,
                        GroupKey = group.Key,
                        Order = i,
                        State = NoticeState.Waiting,
                        Reason = reason,
                        CreatedUtc = now,
                        NextLookupUtc = now,
                    };
                    opened++;
                }
            }
            Trim();
        }
        MarkDirty();
        return opened;
    }

    private static string Describe(LibraryTrack track) =>
        (track.BitRate > 0 ? $"{track.Suffix.ToUpperInvariant()}, {track.BitRate} kbps" : track.Suffix.ToUpperInvariant())
        + (track.TranscodedFrom is { } source ? $", likely transcoded from {source}" : "");

    /// <summary>Kept entries with a fingerprint that has not been sent or refused yet.</summary>
    public IReadOnlyList<NoticeEntry> AwaitingSubmission()
    {
        lock (_lock)
            return _entries.Values
                .Where(entry => entry.State == NoticeState.Kept && !entry.Submitted && entry.Fingerprint is not null)
                .ToList();
    }

    /// <summary>Sent, or deliberately not sent: either way the fingerprint is not needed again.</summary>
    public void MarkSubmitted(IEnumerable<string> keys, bool sent)
    {
        foreach (var key in keys)
            Update(key, entry => entry with { Submitted = sent, Fingerprint = null });
    }

    public IReadOnlyList<NoticeEntry> Recent(int limit = 200)
    {
        lock (_lock)
            return _entries.Values
                .OrderByDescending(entry => entry.ResolvedUtc ?? entry.QueuedUtc ?? entry.CreatedUtc)
                .Take(limit)
                .ToList();
    }

    private void Update(string key, Func<NoticeEntry, NoticeEntry> change)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;
            _entries[key] = change(entry);
        }
        MarkDirty();
    }

    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>Oldest resolved entries go first. An open question is never forgotten.</summary>
    private void Trim()
    {
        if (_entries.Count <= MaxEntries) return;
        var resolved = _entries.Values.Where(entry => !entry.IsOpen)
            .OrderBy(entry => entry.ResolvedUtc ?? entry.CreatedUtc)
            .Take(_entries.Count - MaxEntries)
            .Select(entry => entry.Key)
            .ToList();
        foreach (var key in resolved) _entries.Remove(key);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var entries = JsonSerializer.Deserialize<List<NoticeEntry>>(File.ReadAllText(_path!), Json);
            if (entries is null) return;
            lock (_lock)
            {
                foreach (var entry in entries.Where(entry => !string.IsNullOrEmpty(entry.Key)))
                    _entries[entry.Key] = entry;
                Trim();
            }
        }
        catch (Exception ex)
        {
            // Kept aside rather than overwritten: it holds what people already answered.
            _logger?.LogWarning("notice queue could not be read ({M}); starting empty", ex.Message);
            try { File.Move(_path!, $"{_path}.corrupt-{DateTime.UtcNow.Ticks}"); } catch { /* best effort */ }
        }
    }

    public bool Flush()
    {
        if (_path is null) return true;
        lock (_flushLock)
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return true;
            try
            {
                List<NoticeEntry> entries;
                lock (_lock) entries = _entries.Values.ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(entries, Json));
                File.Move(tmp, _path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _dirty, 1);
                _logger?.LogWarning("notice queue could not be written: {M}", ex.Message);
                return false;
            }
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        Flush();
    }
}
