using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Download;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Fingerprint;
using Octo.Services.Local;
using Octo.Services.Metadata;
using Octo.Services.Notifications;
using Octo.Services.Subsonic;

namespace Octo.Services.Lidarr;

public interface ILidarrHeartAcquisitionService
{
    Task<bool> TryAcquireTrackAsync(
        string provider, string externalId, bool notifyFailure = true,
        string? requestedBy = null);
    Task<bool> TryAcquireAlbumAsync(
        string provider, string externalId, bool notifyFailure = true,
        string? requestedBy = null);
}

/// <summary>
/// Submits Lidarr album searches without occupying Octo's serialized direct-download
/// worker, then reconciles imported files independently.
/// </summary>
public sealed class LidarrHeartAcquisitionService : ILidarrHeartAcquisitionService
{
    private readonly LidarrClient _client;
    private readonly IMusicMetadataService _metadata;
    private readonly DeezerMetadataService _deezer;
    private readonly IOptionsMonitor<LidarrSettings> _settings;
    private readonly IOptionsMonitor<SubsonicSettings> _subsonicSettings;
    private readonly IConfiguration _configuration;
    private readonly NavidromeIdentityService _navIdentity;
    private readonly ILocalLibraryService _library;
    private readonly DownloadHistoryService _history;
    private readonly NotificationService _notifications;
    private readonly ILogger<LidarrHeartAcquisitionService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _albumJobs = new();
    private readonly ConcurrentDictionary<string, byte> _recordedPaths = new(StringComparer.OrdinalIgnoreCase);

    // Who asked for each album, by Lidarr foreign id. Separate from _albumJobs because the
    // same dedup applies: a second user starring an album Lidarr is already working on joins
    // that job, and the import can land minutes later, so the set is read when the file is
    // recorded rather than captured when the job started.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _albumRequesters = new();

    /// <summary>The live progress list. Lidarr reports no bytes, so it only ever hears
    /// accepted, landed, done and failed from here.</summary>
    private readonly AcquisitionTracker? _tracker;
    private readonly MusicBrainzClient? _musicBrainz;
    private readonly LidarrAlbumClaims? _claims;
    // Resolved when used, like the download service does: the library action pieces sit on top
    // of the acquisition queue that this class feeds.
    private readonly IServiceProvider? _services;

    internal TimeSpan ClaimPoll { get; set; } = TimeSpan.FromSeconds(10);

    public LidarrHeartAcquisitionService(
        LidarrClient client,
        IMusicMetadataService metadata,
        DeezerMetadataService deezer,
        IOptionsMonitor<LidarrSettings> settings,
        IOptionsMonitor<SubsonicSettings> subsonicSettings,
        IConfiguration configuration,
        NavidromeIdentityService navIdentity,
        ILocalLibraryService library,
        DownloadHistoryService history,
        NotificationService notifications,
        ILogger<LidarrHeartAcquisitionService> logger,
        AcquisitionTracker? tracker = null,
        MusicBrainzClient? musicBrainz = null,
        LidarrAlbumClaims? claims = null,
        IServiceProvider? services = null)
    {
        _claims = claims;
        _services = services;
        _tracker = tracker;
        _musicBrainz = musicBrainz;
        _client = client;
        _metadata = metadata;
        _deezer = deezer;
        _settings = settings;
        _subsonicSettings = subsonicSettings;
        _configuration = configuration;
        _navIdentity = navIdentity;
        _library = library;
        _history = history;
        _notifications = notifications;
        _logger = logger;
        foreach (var entry in history.GetRecent(int.MaxValue))
            if (!string.IsNullOrWhiteSpace(entry.Path)) _recordedPaths.TryAdd(entry.Path, 0);
    }

    public Task<bool> TryAcquireTrackAsync(
        string provider, string externalId, bool notifyFailure = true,
        string? requestedBy = null) =>
        TryAcquireAsync(async () =>
        {
            var song = await _metadata.GetSongAsync(provider, externalId)
                ?? throw new InvalidOperationException("The starred external track is no longer available.");

            // Deezer names the release a hit came out on first, usually the single, which Lidarr
            // then cannot match. MusicBrainz knows which studio album the song belongs to.
            var studioAlbumId = _musicBrainz is null ? null
                : await _musicBrainz.FindStudioAlbumAsync(song.Artist ?? "", song.Title ?? "", CancellationToken.None);
            var studio = studioAlbumId is null ? null : await _client.ResolveAlbumByForeignIdAsync(studioAlbumId);
            if (studio is not null)
            {
                song.Album = studio.Title;
                await QueueResolvedAlbumAsync(new Album
                {
                    Title = studio.Title,
                    Artist = studio.Artist,
                    Year = studio.Year,
                    Songs = new List<Song> { song },
                }, requestedBy, studio, matchByNumber: false);
                return;
            }

            var enriched = await _deezer.EnrichTrackAsync(song.Artist, song.Title, includeYear: true);
            var albumTitle = enriched?.AlbumTitle;
            if (string.IsNullOrWhiteSpace(albumTitle)) albumTitle = song.Album;
            if (string.IsNullOrWhiteSpace(albumTitle))
                throw new InvalidOperationException($"Could not resolve an album for '{song.Artist} - {song.Title}'.");

            song.Album = albumTitle;
            song.CoverArtUrl ??= enriched?.AlbumCoverUrl;
            song.Year ??= enriched?.Year;
            var album = new Album
            {
                Title = albumTitle,
                Artist = enriched?.ArtistName ?? song.Artist,
                Year = enriched?.Year,
                CoverArtUrl = enriched?.AlbumCoverUrl,
                Songs = new List<Song> { song },
            };
            await QueueResolvedAlbumAsync(album, requestedBy, matchByNumber: false);
        }, "track", provider, externalId, notifyFailure);

    public Task<bool> TryAcquireAlbumAsync(
        string provider, string externalId, bool notifyFailure = true,
        string? requestedBy = null) =>
        TryAcquireAsync(async () =>
        {
            var album = await _metadata.GetAlbumAsync(provider, externalId)
                ?? throw new InvalidOperationException("The starred external album is no longer available.");
            // No walk runs on this path, so the track list is announced here instead.
            _tracker?.Announce(provider, externalId, null, album.Songs
                .Where(s => !string.IsNullOrEmpty(s.ExternalId))
                .Select(s => (s.ExternalId!, (string?)s.Artist, (string?)s.Title, (string?)album.Title)));
            await QueueResolvedAlbumAsync(album, requestedBy);
        }, "album", provider, externalId, notifyFailure);

    private void AddRequester(string albumKey, string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        _albumRequesters
            .GetOrAdd(albumKey, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))
            .TryAdd(username.Trim(), 0);
    }

    private IReadOnlyList<string>? RequestersFor(string albumKey) =>
        _albumRequesters.TryGetValue(albumKey, out var set) && !set.IsEmpty
            ? set.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList()
            : null;

    /// <param name="matchByNumber">Whether an imported file may be matched to a song by its track
    /// number. Not for a track heart: its one song carries the number from whatever release it was
    /// found on, often the single, where it is track 1, which on the album is another song.</param>
    private async Task QueueResolvedAlbumAsync(Album album, string? requestedBy = null,
        LidarrAlbumCandidate? resolved = null, bool matchByNumber = true)
    {
        if (string.IsNullOrWhiteSpace(album.Artist) || string.IsNullOrWhiteSpace(album.Title))
            throw new InvalidOperationException("Lidarr requires an album artist and title.");

        var candidate = resolved ?? await _client.ResolveAlbumAsync(album.Artist, album.Title, album.Year);
        // Before GetOrAdd, so a caller that joins an existing job is still recorded.
        AddRequester(candidate.ForeignAlbumId, requestedBy);
        var lazy = _albumJobs.GetOrAdd(candidate.ForeignAlbumId,
            _ => new Lazy<Task>(() => SubmitAndStartReconciliationAsync(candidate, album, matchByNumber),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            await lazy.Value;
        }
        catch
        {
            _albumJobs.TryRemove(new KeyValuePair<string, Lazy<Task>>(candidate.ForeignAlbumId, lazy));
            throw;
        }
    }

    private static IEnumerable<(string Provider, string Id)> TrackedKeys(Album album) =>
        album.Songs
            .Where(s => !string.IsNullOrWhiteSpace(s.ExternalProvider) && !string.IsNullOrWhiteSpace(s.ExternalId))
            .Select(s => (s.ExternalProvider!, s.ExternalId!));

    private void NoteLanded(Album album, LidarrImportedTrack track, string localPath, Dictionary<Song, string> landed,
        bool matchByNumber)
    {
        if (_tracker is null) return;
        try
        {
            if (MatchSong(album, track, matchByNumber) is not { } song) return;
            landed[song] = localPath;
            if (song is { ExternalProvider: { Length: > 0 } provider, ExternalId: { Length: > 0 } id })
                _tracker.Stage(provider, id, AcquisitionState.Importing);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not match a Lidarr import for progress: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Close the progress entry of every hearted track on this album: the ones Octo can see on
    /// disk are imported, the rest failed with <paramref name="missing"/>. Rows already settled
    /// keep what they said.
    /// </summary>
    private void SettleTracked(Album album, Dictionary<Song, string> landed, string missing)
    {
        if (_tracker is null) return;
        try
        {
            foreach (var song in album.Songs)
            {
                if (string.IsNullOrWhiteSpace(song.ExternalProvider) || string.IsNullOrWhiteSpace(song.ExternalId)) continue;
                if (landed.TryGetValue(song, out var path))
                    _tracker.Imported(song.ExternalProvider, song.ExternalId, song.Artist, song.Title, path);
                else
                    _tracker.Fail(song.ExternalProvider, song.ExternalId, missing);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not settle Lidarr progress for '{Album}': {Message}", album.Title, ex.Message);
        }
    }

    private async Task SubmitAndStartReconciliationAsync(
        LidarrAlbumCandidate candidate, Album album, bool matchByNumber)
    {
        var snapshot = _settings.CurrentValue;
        var key = candidate.ForeignAlbumId;
        // An upgrade borrowing this album deletes what its search brings in when it ends; the heart
        // waits for it rather than lose its songs to that clean up.
        var waitUntil = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(60, snapshot.ImportTimeoutSeconds));
        while (_claims?.UpgradeBusy(key) == true && DateTime.UtcNow < waitUntil) await Task.Delay(ClaimPoll);
        _claims?.HeartStarted(key);
        LidarrSearchStarted started;
        IReadOnlySet<int> before;
        try
        {
            // The files Lidarr had before this search are the owner's; only new ones are ever deleted.
            var existing = await _client.FindAlbumAsync(key);
            before = existing is null ? new HashSet<int>()
                : (await _client.GetAlbumTracksAsync(existing.Id)).Where(t => t.TrackFileId > 0).Select(t => t.TrackFileId).ToHashSet();
            started = await _client.StartAlbumSearchAsync(candidate);
        }
        catch
        {
            _claims?.HeartEnded(key);
            throw;
        }
        var albumId = started.AlbumId;
        _logger.LogInformation("Lidarr accepted AlbumSearch for '{Artist} - {Album}' ({ForeignId}, local id {Id})",
            album.Artist, album.Title, candidate.ForeignAlbumId, albumId);
        _notifications.Notify(new NotificationEvent
        {
            Type = NotificationEventType.DownloadStarted,
            Artist = album.Artist,
            Title = album.Title,
            Album = album.Title,
            Source = "Lidarr",
            CoverArtUrl = album.CoverArtUrl,
            Detail = "Album search accepted",
        });

        // Accepted. Lidarr says nothing about bytes, so this is a download with no figure on
        // it. Before the reconcile starts, so it can never undo what the reconcile reports.
        foreach (var (provider, id) in TrackedKeys(album))
            _tracker?.Transfer(provider, id, null, null, null, "Lidarr");

        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileImportsAsync(albumId, album, snapshot, candidate.ForeignAlbumId, matchByNumber, before);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lidarr import reconciliation failed for '{Artist} - {Album}'",
                    album.Artist, album.Title);
                foreach (var (provider, id) in TrackedKeys(album)) _tracker?.Fail(provider, id, ex.Message);
                if (snapshot.CompletionMode == LidarrCompletionMode.Imported)
                {
                    _notifications.Notify(new NotificationEvent
                    {
                        Type = NotificationEventType.DownloadFailed,
                        Artist = album.Artist,
                        Title = album.Title,
                        Album = album.Title,
                        Source = "Lidarr",
                        CoverArtUrl = album.CoverArtUrl,
                        Detail = ex.Message,
                    });
                }
            }
            finally
            {
                // Octo moves every import into its own layout, so Lidarr's next rescan finds the
                // files gone. An album Octo switched monitoring on for would then be fetched again
                // and again, so it goes back to how it was.
                if (!started.WasMonitored)
                {
                    try { await _client.SetAlbumsMonitoredAsync([albumId], false); }
                    catch (Exception ex) { _logger.LogWarning("Could not stop monitoring Lidarr album {Id} again: {Message}", albumId, ex.Message); }
                }
                _claims?.HeartEnded(key);
                _albumJobs.TryRemove(candidate.ForeignAlbumId, out _);
            }
        });
    }

    private async Task ReconcileImportsAsync(int albumId, Album album, LidarrSettings settings,
        string albumKey, bool matchByNumber = true, IReadOnlySet<int>? before = null)
    {
        before ??= new HashSet<int>();
        // Lidarr tracks dealt with: recorded, or dropped as already yours or removed. The album is
        // done once every one of them is, since a dropped file no longer counts as Lidarr's.
        var settled = new HashSet<int>();
        var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.ImportTimeoutSeconds));
        var poll = TimeSpan.FromSeconds(Math.Clamp(settings.ImportTimeoutSeconds / 30, 1, 10));
        var deadline = DateTime.UtcNow + timeout;
        var imported = 0;
        var expected = 0;
        var visibleToOcto = 0;
        // Which hearted song each visible file is, for the progress list. Matched on every poll,
        // not only when recorded, so a song Lidarr had imported before still counts as here.
        var landed = new Dictionary<Song, string>(ReferenceEqualityComparer.Instance);

        var octoRoot = _navIdentity.EffectiveDownloadPath(_configuration["Library:DownloadPath"] ?? "/music");

        while (DateTime.UtcNow < deadline)
        {
            var state = await _client.GetAlbumImportStateAsync(albumId);
            var tracks = state.Tracks;
            expected = state.TrackCount;
            visibleToOcto = 0;
            var visible = tracks
                .Where(t => t.HasFile && !string.IsNullOrWhiteSpace(t.Path))
                .GroupBy(t => t.Path!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            foreach (var track in visible)
            {
                if (settled.Contains(track.Id)) { visibleToOcto++; continue; }
                var importedPath = TranslateImportedPath(track.Path!, settings.RootFolderPath, octoRoot);
                if (!File.Exists(importedPath)) continue;
                if (await ShouldDropAsync(album, track, importedPath, matchByNumber, albumKey) is { } reason)
                {
                    await DropAsync(track, importedPath, before, reason);
                    settled.Add(track.Id);
                    visibleToOcto++;
                    continue;
                }
                var localPath = NormalizeImportedLayout(importedPath, album, track, octoRoot);
                visibleToOcto++;
                settled.Add(track.Id);
                NoteLanded(album, track, localPath, landed, matchByNumber);
                if (!_recordedPaths.TryAdd(localPath, 0)) continue;
                try
                {
                    await RecordImportAsync(album, track, localPath, RequestersFor(albumKey), matchByNumber);
                    imported++;
                }
                catch
                {
                    _recordedPaths.TryRemove(localPath, out _);
                    throw;
                }
            }

            if ((state.IsComplete && visible.Count > 0 && visibleToOcto == visible.Count)
                || (expected > 0 && settled.Count >= expected))
            {
                if (imported > 0) await _library.TriggerLibraryScanAsync(force: true);
                SettleTracked(album, landed, "Lidarr finished the album without this track.");
                if (settings.CompletionMode == LidarrCompletionMode.Imported && imported > 0)
                {
                    _notifications.Notify(new NotificationEvent
                    {
                        Type = NotificationEventType.AlbumCompleted,
                        Artist = album.Artist,
                        Title = album.Title,
                        CoverArtUrl = album.CoverArtUrl,
                        TrackCount = state.TrackCount,
                        LosslessCount = visible.Count(t =>
                            string.Equals(Path.GetExtension(t.Path), ".flac", StringComparison.OrdinalIgnoreCase)),
                        FailedCount = 0,
                    });
                }
                return;
            }

            await Task.Delay(poll);
        }

        if (imported > 0) await _library.TriggerLibraryScanAsync(force: true);
        var detail = $"Lidarr import timed out after {(int)timeout.TotalMinutes} minute(s)"
                     + (expected > 0 ? $" ({visibleToOcto}/{expected} files visible to Octo)" : "");
        SettleTracked(album, landed, $"Lidarr import timed out after {(int)timeout.TotalMinutes} minute(s).");
        _logger.LogWarning("{Detail} for '{Artist} - {Album}'", detail, album.Artist, album.Title);
        if (settings.CompletionMode == LidarrCompletionMode.Imported)
        {
            _notifications.Notify(new NotificationEvent
            {
                Type = NotificationEventType.DownloadFailed,
                Artist = album.Artist,
                Title = album.Title,
                Album = album.Title,
                Source = "Lidarr",
                CoverArtUrl = album.CoverArtUrl,
                Detail = detail,
            });
        }
    }

    /// <summary>
    /// Why an imported file should not join the library, or null when it should. A song removed
    /// with a library action stays removed, and a song already in the library is not added twice,
    /// the same rules as a Soulseek download. An owned lossy copy is queued for Better quality
    /// instead, which swaps it in place and keeps its plays (W8).
    /// </summary>
    private async Task<string?> ShouldDropAsync(Album album, LidarrImportedTrack track, string importedPath,
        bool matchByNumber, string albumKey)
    {
        var song = MatchSong(album, track, matchByNumber);
        var artist = song?.Artist ?? track.Artist ?? album.Artist;
        var title = string.IsNullOrWhiteSpace(track.Title) ? song?.Title : track.Title;
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;

        if (_services?.GetService(typeof(Octo.Services.Library.LibraryActionJournal)) is Octo.Services.Library.LibraryActionJournal journal
            && journal.IsNeverRequested(artist, title))
            return "it was removed with a library action";

        if (!_subsonicSettings.CurrentValue.SkipOwnedSongs
            || _services?.GetService(typeof(Octo.Services.Library.LibraryOwnership)) is not Octo.Services.Library.LibraryOwnership ownership)
            return null;
        var owned = await ownership.FindAsync(artist, title, track.DurationSeconds ?? song?.Duration, album.Title);
        // The import itself, once Navidrome has scanned it, is not a second copy.
        if (owned is null || SamePath(owned.AbsolutePath, importedPath)) return null;
        if (!owned.Lossless && LidarrTrackFetcher.IsLossless(track) && QueueUpgrade(owned, artist, title, album.Title, albumKey))
            return $"you have it as {owned.Suffix.ToUpperInvariant()}, which is queued for a higher quality copy";
        return $"it is already in your library ({owned.Suffix.ToUpperInvariant()})";
    }

    /// <summary>Queue Better quality for an owned lossy copy, when every gate of the action is open
    /// for the person who hearted the album.</summary>
    private bool QueueUpgrade(Octo.Services.Library.OwnedCopy owned, string artist, string title, string album, string albumKey)
    {
        if (owned.NavidromeId is null || RequestersFor(albumKey) is not { Count: > 0 } askers) return false;
        if (_services?.GetService(typeof(Octo.Services.Library.UpgradeQueue)) is not Octo.Services.Library.UpgradeQueue queue
            || _services.GetService(typeof(IOptionsMonitor<LibraryActionSettings>)) is not IOptionsMonitor<LibraryActionSettings> monitor)
            return false;
        var actions = monitor.CurrentValue;
        if (!actions.Enabled || actions.DryRun || !actions.IsAllowed(askers[0])
            || !actions.EffectiveActions().Any(a => a.Action == LibraryAction.BetterQuality && a.Enabled))
            return false;
        queue.Add([new Octo.Services.Library.UpgradeAsk(owned.NavidromeId, title, artist, album, owned.Suffix)], askers[0], "heart");
        return true;
    }

    /// <summary>
    /// Take an import back out. A file this heart's search brought in is deleted through Lidarr, so
    /// Lidarr's own records stay true; one Lidarr had before is the owner's and is only left alone.
    /// </summary>
    private async Task DropAsync(LidarrImportedTrack track, string importedPath, IReadOnlySet<int> before, string reason)
    {
        if (track.TrackFileId > 0 && !before.Contains(track.TrackFileId))
        {
            try
            {
                await _client.DeleteTrackFileAsync(track.TrackFileId);
                _logger.LogInformation("Lidarr brought '{Artist} - {Title}', but {Reason}; deleted it ({Path})",
                    track.Artist, track.Title, reason, importedPath);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not delete Lidarr's '{Artist} - {Title}' ({Reason}): {Message}",
                    track.Artist, track.Title, reason, ex.Message);
            }
        }
        _logger.LogInformation("Lidarr has '{Artist} - {Title}', but {Reason}; left it out of the library records",
            track.Artist, track.Title, reason);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task RecordImportAsync(Album album, LidarrImportedTrack imported, string localPath,
        IReadOnlyList<string>? requestedBy = null, bool matchByNumber = true)
    {
        var song = MatchSong(album, imported, matchByNumber) ?? new Song
        {
            Artist = imported.Artist ?? album.Artist,
            Title = imported.Title,
            Album = album.Title,
            Track = imported.TrackNumber,
            Duration = imported.DurationSeconds,
            CoverArtUrl = album.CoverArtUrl,
            IsLocal = false,
        };

        if (!string.IsNullOrWhiteSpace(song.ExternalProvider) && !string.IsNullOrWhiteSpace(song.ExternalId))
            await _library.RegisterDownloadedSongAsync(song, localPath);

        var ext = Path.GetExtension(localPath).TrimStart('.').ToUpperInvariant();
        long size = imported.SizeBytes;
        if (size <= 0) try { size = new FileInfo(localPath).Length; } catch { /* best effort */ }
        _history.Record(new DownloadHistoryEntry
        {
            Artist = song.Artist,
            Title = song.Title,
            Album = album.Title,
            Path = localPath,
            Format = string.IsNullOrEmpty(ext) ? "?" : ext,
            Source = "Lidarr",
            CoverArtUrl = song.CoverArtUrlLarge ?? song.CoverArtUrl ?? album.CoverArtUrl,
            SizeBytes = size,
            DownloadedAt = DateTime.UtcNow.ToString("o"),
            RequestedBy = requestedBy is { Count: > 0 } ? [.. requestedBy] : null,
        });
    }

    /// <summary>The album's song Lidarr imported: by title, read by <see cref="SongIdentity"/> so
    /// Deezer's "Song (feat. X)" is MusicBrainz's "Song" but never its "Song (Live)", then by
    /// track number.</summary>
    internal static Song? MatchSong(Album album, LidarrImportedTrack track, bool matchByNumber = true)
    {
        var byTitle = album.Songs.Where(s => SongIdentity.SameTitle(track.Title, s.Title, SongIdentity.StrictTitles).IsSame).ToList();
        if (byTitle.Count == 1) return byTitle[0];
        if (matchByNumber && track.TrackNumber is int number)
        {
            var byNumber = album.Songs.Where(s => s.Track == number).ToList();
            if (byNumber.Count == 1) return byNumber[0];
        }
        return byTitle.FirstOrDefault();
    }

    internal static string TranslateImportedPath(string lidarrPath, string? lidarrRoot, string octoRoot)
    {
        if (string.IsNullOrWhiteSpace(lidarrRoot))
            throw new InvalidOperationException("Lidarr root folder is not configured.");
        var root = Path.GetFullPath(lidarrRoot);
        var source = Path.GetFullPath(lidarrPath);
        var relative = Path.GetRelativePath(root, source);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException($"Lidarr imported a path outside its configured root: {lidarrPath}");
        var targetRoot = Path.GetFullPath(octoRoot);
        var target = Path.GetFullPath(Path.Combine(targetRoot, relative));
        if (Path.GetRelativePath(targetRoot, target).StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Translated Lidarr path escaped Octo's library root.");
        return target;
    }

    /// <summary>
    /// Lidarr does not always rename an import into an Artist/Album layout: a release with
    /// thin MusicBrainz metadata can land under its raw staging folder name (e.g.
    /// "HELLHOUND {mbid:...} {Album}") with the peer's original filename still attached.
    /// Re-home it into the same layout PathHelper.BuildTrackPath gives direct Soulseek
    /// downloads, so Navidrome never sees Lidarr-sourced tracks organized differently.
    /// </summary>
    private string NormalizeImportedLayout(string importedPath, Album album, LidarrImportedTrack track, string octoRoot)
    {
        try
        {
            var ext = Path.GetExtension(importedPath);
            var title = string.IsNullOrWhiteSpace(track.Title) ? album.Title : track.Title;

            // Follow the SAME setting the Soulseek path follows. Building the Artist/Album
            // layout unconditionally would put Lidarr imports in folders while a Flat
            // library keeps everything in one directory, which is the inconsistency this
            // is here to remove, and Flat is the default.
            var canonicalPath = PathHelper.BuildLayoutPath(
                _subsonicSettings.CurrentValue.FolderStructure, octoRoot,
                album.Artist, album.Title, title, track.TrackNumber, ext);
            if (string.Equals(Path.GetFullPath(canonicalPath), Path.GetFullPath(importedPath), StringComparison.OrdinalIgnoreCase))
                return importedPath;

            var targetDir = Path.GetDirectoryName(canonicalPath);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
            canonicalPath = PathHelper.ResolveUniquePath(canonicalPath);
            File.Move(importedPath, canonicalPath);

            var oldDir = Path.GetDirectoryName(importedPath);
            if (!string.IsNullOrEmpty(oldDir) && Directory.Exists(oldDir)
                && !Directory.EnumerateFileSystemEntries(oldDir).Any())
                Directory.Delete(oldDir);

            return canonicalPath;
        }
        catch
        {
            // Best effort: keep the file registered where Lidarr put it rather than losing it.
            return importedPath;
        }
    }

    private async Task<bool> TryAcquireAsync(
        Func<Task> work, string kind, string provider, string externalId, bool notifyFailure)
    {
        try
        {
            await work();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lidarr {Kind} heart failed for {Id}", kind, externalId);
            // notifyFailure is true only for the last source in the chain, which is also the
            // only failure the progress list may show.
            if (notifyFailure)
            {
                if (kind == "album") _tracker?.FailAlbum(provider, externalId, ex.Message);
                else _tracker?.Fail(provider, externalId, ex.Message);
            }
            if (notifyFailure) _notifications.Notify(new NotificationEvent
            {
                Type = NotificationEventType.DownloadFailed,
                Source = "Lidarr",
                Detail = ex.Message,
            });
            return false;
        }
    }
}
