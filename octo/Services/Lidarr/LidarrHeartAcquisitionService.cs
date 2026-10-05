using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Fingerprint;
using Octo.Services.Metadata;
using Octo.Services.Notifications;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Services.Lidarr;

public interface ILidarrHeartAcquisitionService
{
    /// <summary>
    /// True once the song is in the library: Lidarr brought it and it passed Octo's checks, or it
    /// was there already. False when Lidarr could not get it, so the heart's next source tries.
    /// Waits for Lidarr's import, up to Lidarr's import timeout.
    /// </summary>
    Task<bool> TryAcquireTrackAsync(
        string provider, string externalId, bool notifyFailure = true,
        string? requestedBy = null);

    /// <summary>True once every song of the album is in the library; false leaves the rest to
    /// the next source, which skips the songs that did land.</summary>
    Task<bool> TryAcquireAlbumAsync(
        string provider, string externalId, bool notifyFailure = true,
        string? requestedBy = null);
}

/// <summary>
/// Hearts through Lidarr. Lidarr only fetches whole albums, so a heart becomes an album search;
/// what Lidarr imports is then handed, song by song, to the download pipeline a Soulseek file
/// goes through (AcoustID, the spectrum, release matching, tags, placing, history), with Lidarr
/// as the place the file came from (LidarrImportHandoff). A song already in the library or
/// removed with a library action is deleted from what Lidarr brought instead.
///
/// One job per album: a second heart on an album Lidarr is already fetching joins it, and its
/// songs are followed too. The job ends when every track is dealt with, or at the timeout, and
/// tells each heart whether its songs are in the library.
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
    private readonly NotificationService _notifications;
    private readonly ILogger<LidarrHeartAcquisitionService> _logger;

    /// <summary>The live progress list. Lidarr reports no bytes, so it only hears accepted from
    /// here; the pipeline moves each song's row on from there.</summary>
    private readonly AcquisitionTracker? _tracker;
    private readonly MusicBrainzClient? _musicBrainz;
    private readonly LidarrAlbumClaims? _claims;
    private readonly LidarrImportHandoff _imports;
    private readonly ExternalIdRegistry? _ids;
    // Resolved when used: the download pipeline and the library action pieces sit on top of the
    // acquisition queue that the heart coordinator, and so this class, feeds.
    private readonly IServiceProvider? _services;

    private readonly object _jobsLock = new();
    private readonly Dictionary<string, AlbumJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

    internal TimeSpan ClaimPoll { get; set; } = TimeSpan.FromSeconds(10);
    internal TimeSpan? PollOverride { get; set; }

    public LidarrHeartAcquisitionService(
        LidarrClient client,
        IMusicMetadataService metadata,
        DeezerMetadataService deezer,
        IOptionsMonitor<LidarrSettings> settings,
        IOptionsMonitor<SubsonicSettings> subsonicSettings,
        IConfiguration configuration,
        NavidromeIdentityService navIdentity,
        NotificationService notifications,
        ILogger<LidarrHeartAcquisitionService> logger,
        AcquisitionTracker? tracker = null,
        MusicBrainzClient? musicBrainz = null,
        LidarrAlbumClaims? claims = null,
        IServiceProvider? services = null,
        LidarrImportHandoff? imports = null,
        ExternalIdRegistry? ids = null)
    {
        _client = client;
        _metadata = metadata;
        _deezer = deezer;
        _settings = settings;
        _subsonicSettings = subsonicSettings;
        _configuration = configuration;
        _navIdentity = navIdentity;
        _notifications = notifications;
        _logger = logger;
        _tracker = tracker;
        _musicBrainz = musicBrainz;
        _claims = claims;
        _services = services;
        _imports = imports ?? new LidarrImportHandoff();
        _ids = ids;
    }

    /// <summary>One album Lidarr is fetching for one or more hearts.</summary>
    private sealed class AlbumJob(LidarrAlbumCandidate candidate, Album album)
    {
        private readonly object _lock = new();
        private readonly List<Song> _songs = [];

        public LidarrAlbumCandidate Candidate { get; } = candidate;
        public Album Album { get; } = album;
        public ConcurrentDictionary<string, byte> Requesters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task Submitted { get; set; } = Task.CompletedTask;
        public bool MatchByNumber { get; private set; }
        public bool AlbumHeart { get; private set; }

        /// <summary>The keys of the songs in the library when the job ended: a hearted song's
        /// own id, and every song's artist and title, so a heart that joined late still finds
        /// its song among the album's other tracks.</summary>
        public TaskCompletionSource<IReadOnlySet<string>> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Why a hearted song did not land, by its key, for the progress list.</summary>
        public ConcurrentDictionary<string, string> Missed { get; } = new(StringComparer.Ordinal);

        public void Join(IEnumerable<Song> songs, bool matchByNumber, bool albumHeart, string? requestedBy)
        {
            lock (_lock)
            {
                foreach (var song in songs)
                    if (!_songs.Any(known => KeyOf(known) == KeyOf(song))) _songs.Add(song);
                MatchByNumber |= matchByNumber;
                AlbumHeart |= albumHeart;
            }
            if (!string.IsNullOrWhiteSpace(requestedBy)) Requesters.TryAdd(requestedBy.Trim(), 0);
        }

        public Album View()
        {
            lock (_lock) return new Album { Title = Album.Title, Artist = Album.Artist, Year = Album.Year, Songs = [.. _songs] };
        }

        public IReadOnlyList<string>? AskedBy() =>
            Requesters.IsEmpty ? null : Requesters.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string KeyOf(Song song) =>
        !string.IsNullOrWhiteSpace(song.ExternalId) ? $"{song.ExternalProvider}:{song.ExternalId}" : TitleKey(song.Artist, song.Title);

    private static string TitleKey(string? artist, string? title) => "t:" + SongIdentity.MatchKey(artist, title);

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
            Album album;
            if (studio is not null)
            {
                song.Album = studio.Title;
                album = new Album { Title = studio.Title, Artist = studio.Artist, Year = studio.Year, Songs = [song] };
            }
            else
            {
                var enriched = await _deezer.EnrichTrackAsync(song.Artist, song.Title, includeYear: true);
                var albumTitle = enriched?.AlbumTitle;
                if (string.IsNullOrWhiteSpace(albumTitle)) albumTitle = song.Album;
                if (string.IsNullOrWhiteSpace(albumTitle))
                    throw new InvalidOperationException($"Could not resolve an album for '{song.Artist} - {song.Title}'.");
                song.Album = albumTitle;
                song.CoverArtUrl ??= enriched?.AlbumCoverUrl;
                song.Year ??= enriched?.Year;
                album = new Album
                {
                    Title = albumTitle, Artist = enriched?.ArtistName ?? song.Artist, Year = enriched?.Year,
                    CoverArtUrl = enriched?.AlbumCoverUrl, Songs = [song],
                };
            }
            var job = await QueueResolvedAlbumAsync(album, requestedBy, studio, matchByNumber: false, albumHeart: false);
            var landed = await job.Ended.Task;
            if (landed.Contains(KeyOf(song)) || landed.Contains(TitleKey(song.Artist, song.Title))) return true;
            if (notifyFailure)
                _tracker?.Fail(provider, externalId, job.Missed.GetValueOrDefault(KeyOf(song)) ?? "Lidarr did not bring this song.");
            return false;
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
            var job = await QueueResolvedAlbumAsync(album, requestedBy, albumHeart: true);
            var landed = await job.Ended.Task;
            var missing = album.Songs
                .Where(song => !landed.Contains(KeyOf(song)) && !landed.Contains(TitleKey(song.Artist, song.Title)))
                .ToList();
            if (notifyFailure)
                foreach (var song in missing.Where(s => !string.IsNullOrEmpty(s.ExternalProvider) && !string.IsNullOrEmpty(s.ExternalId)))
                    _tracker?.Fail(song.ExternalProvider!, song.ExternalId!,
                        job.Missed.GetValueOrDefault(KeyOf(song)) ?? "Lidarr finished the album without this song.");
            return missing.Count == 0;
        }, "album", provider, externalId, notifyFailure);

    /// <param name="matchByNumber">Whether an imported file may be matched to a song by its track
    /// number. Not for a track heart: its one song carries the number from whatever release it was
    /// found on, often the single, where it is track 1, which on the album is another song.</param>
    private async Task<AlbumJob> QueueResolvedAlbumAsync(Album album, string? requestedBy = null,
        LidarrAlbumCandidate? resolved = null, bool matchByNumber = true, bool albumHeart = false)
    {
        if (string.IsNullOrWhiteSpace(album.Artist) || string.IsNullOrWhiteSpace(album.Title))
            throw new InvalidOperationException("Lidarr requires an album artist and title.");

        var candidate = resolved ?? await _client.ResolveAlbumAsync(album.Artist, album.Title, album.Year);
        AlbumJob job;
        bool created;
        lock (_jobsLock)
        {
            created = !_jobs.TryGetValue(candidate.ForeignAlbumId, out job!);
            if (created) _jobs[candidate.ForeignAlbumId] = job = new AlbumJob(candidate, album);
            job.Join(album.Songs, matchByNumber, albumHeart, requestedBy);
            if (created) job.Submitted = Task.Run(() => SubmitAsync(job));
        }
        if (!created)
        {
            // Joined a search already running: its songs are followed from here on.
            foreach (var (provider, id) in TrackedKeys(album))
                _tracker?.Transfer(provider, id, null, null, null, "Lidarr");
        }
        try
        {
            await job.Submitted;
        }
        catch
        {
            lock (_jobsLock)
                if (_jobs.TryGetValue(candidate.ForeignAlbumId, out var current) && ReferenceEquals(current, job))
                    _jobs.Remove(candidate.ForeignAlbumId);
            throw;
        }
        return job;
    }

    private static IEnumerable<(string Provider, string Id)> TrackedKeys(Album album) =>
        album.Songs
            .Where(s => !string.IsNullOrWhiteSpace(s.ExternalProvider) && !string.IsNullOrWhiteSpace(s.ExternalId))
            .Select(s => (s.ExternalProvider!, s.ExternalId!));

    private async Task SubmitAsync(AlbumJob job)
    {
        var snapshot = _settings.CurrentValue;
        var key = job.Candidate.ForeignAlbumId;
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
            started = await _client.StartAlbumSearchAsync(job.Candidate);
        }
        catch
        {
            _claims?.HeartEnded(key);
            throw;
        }

        var album = job.Album;
        _logger.LogInformation("Lidarr accepted AlbumSearch for '{Artist} - {Album}' ({ForeignId}, local id {Id})",
            album.Artist, album.Title, key, started.AlbumId);
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
        // Accepted. Lidarr says nothing about bytes, so this is a download with no figure on it.
        foreach (var (provider, id) in TrackedKeys(job.View()))
            _tracker?.Transfer(provider, id, null, null, null, "Lidarr");

        _ = Task.Run(async () =>
        {
            var landed = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                await ReconcileImportsAsync(job, started.AlbumId, snapshot, before, landed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lidarr import reconciliation failed for '{Artist} - {Album}'", album.Artist, album.Title);
                if (snapshot.CompletionMode == LidarrCompletionMode.Imported)
                    _notifications.Notify(new NotificationEvent
                    {
                        Type = NotificationEventType.DownloadFailed,
                        Artist = album.Artist, Title = album.Title, Album = album.Title,
                        Source = "Lidarr", CoverArtUrl = album.CoverArtUrl, Detail = ex.Message,
                    });
            }
            finally
            {
                lock (_jobsLock)
                    if (_jobs.TryGetValue(key, out var current) && ReferenceEquals(current, job)) _jobs.Remove(key);
                job.Ended.TrySetResult(landed);
                // Octo moves every import into its own layout, so Lidarr's next rescan finds the
                // files gone. An album Octo switched monitoring on for would then be fetched again
                // and again, so it goes back to how it was.
                if (!started.WasMonitored)
                {
                    try { await _client.SetAlbumsMonitoredAsync([started.AlbumId], false); }
                    catch (Exception ex) { _logger.LogWarning("Could not stop monitoring Lidarr album {Id} again: {Message}", started.AlbumId, ex.Message); }
                }
                _claims?.HeartEnded(key);
            }
        });
    }

    private async Task ReconcileImportsAsync(AlbumJob job, int albumId, LidarrSettings settings,
        IReadOnlySet<int> before, HashSet<string> landed)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, settings.ImportTimeoutSeconds));
        var poll = PollOverride ?? TimeSpan.FromSeconds(Math.Clamp(settings.ImportTimeoutSeconds / 30, 1, 10));
        var deadline = DateTime.UtcNow + timeout;
        var octoRoot = _navIdentity.EffectiveDownloadPath(_configuration["Library:DownloadPath"] ?? "/music");
        // Lidarr tracks dealt with: handed to the pipeline, or dropped. A dropped file no longer
        // counts as Lidarr's, so the album is done once every track is in one of the two.
        var handled = new HashSet<int>();
        var handed = new List<Task>();
        var expected = 0;
        var timedOut = false;

        while (true)
        {
            var state = await _client.GetAlbumImportStateAsync(albumId);
            expected = state.TrackCount;
            var visible = state.Tracks
                .Where(t => t.HasFile && !string.IsNullOrWhiteSpace(t.Path) && !handled.Contains(t.Id))
                .GroupBy(t => t.Path!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            foreach (var track in visible)
            {
                var importedPath = TranslateImportedPath(track.Path!, settings.RootFolderPath, octoRoot);
                if (!File.Exists(importedPath)) continue;
                handled.Add(track.Id);
                var album = job.View();
                var song = MatchSong(album, track, job.MatchByNumber);
                if (await ShouldDropAsync(album, track, song, importedPath, job) is { } drop)
                {
                    await DropAsync(track, importedPath, before, drop.Reason);
                    // Already in the library: in, as far as the heart is concerned, and its row
                    // closes on the library's own copy.
                    if (drop.Owned is { } owned)
                    {
                        lock (landed) AddKeys(landed, song, track, album);
                        if (song is { ExternalProvider: { Length: > 0 } provider, ExternalId: { Length: > 0 } id })
                            _tracker?.Imported(provider, id, song.Artist, song.Title, owned.AbsolutePath);
                    }
                    else if (song is not null) job.Missed[KeyOf(song)] = $"Not taken: {drop.Reason}.";
                    continue;
                }
                handed.Add(HandToPipelineAsync(job, track, song, album, importedPath, before, landed));
            }

            if (expected > 0 && handled.Count >= expected) break;
            if (DateTime.UtcNow >= deadline) { timedOut = true; break; }
            await Task.Delay(poll);
        }

        // The pipeline runs each song's checks and tagging; the job ends when the last is done.
        await Task.WhenAll(handed);

        var album2 = job.View();
        if (timedOut)
        {
            var detail = $"Lidarr import timed out after {(int)timeout.TotalMinutes} minute(s)"
                         + (expected > 0 ? $" ({handled.Count}/{expected} tracks arrived)" : "");
            foreach (var song in album2.Songs) job.Missed.TryAdd(KeyOf(song), detail + ".");
            _logger.LogWarning("{Detail} for '{Artist} - {Album}'", detail, album2.Artist, album2.Title);
            if (settings.CompletionMode == LidarrCompletionMode.Imported)
                _notifications.Notify(new NotificationEvent
                {
                    Type = NotificationEventType.DownloadFailed,
                    Artist = album2.Artist, Title = album2.Title, Album = album2.Title,
                    Source = "Lidarr", CoverArtUrl = job.Album.CoverArtUrl, Detail = detail,
                });
            return;
        }
        int inLibrary;
        lock (landed) inLibrary = landed.Count(key => key.StartsWith("t:", StringComparison.Ordinal));
        if (job.AlbumHeart || settings.CompletionMode == LidarrCompletionMode.Imported)
            _notifications.Notify(new NotificationEvent
            {
                Type = NotificationEventType.AlbumCompleted,
                Artist = album2.Artist,
                Title = album2.Title,
                CoverArtUrl = job.Album.CoverArtUrl,
                TrackCount = inLibrary,
                FailedCount = Math.Max(0, expected - inLibrary),
                Source = "Lidarr",
            });
    }

    /// <summary>
    /// One imported file through the download pipeline, as the song it is: a hearted song by its
    /// own id, so its progress row moves on, and any other track of the album by an id minted for
    /// it. Whatever the pipeline did not take is deleted through Lidarr, so nothing is left behind
    /// in the library unchecked.
    /// </summary>
    private async Task HandToPipelineAsync(AlbumJob job, LidarrImportedTrack track, Song? song, Album album,
        string importedPath, IReadOnlySet<int> before, HashSet<string> landed)
    {
        var provider = SoulseekMetadataService.ProviderName;
        string? id = song is { ExternalProvider: var p, ExternalId: { Length: > 0 } e }
                     && string.Equals(p, provider, StringComparison.OrdinalIgnoreCase) ? e : null;
        id ??= _ids?.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Song,
            Artist = track.Artist ?? album.Artist,
            Title = string.IsNullOrWhiteSpace(track.Title) ? song?.Title : track.Title,
            Album = album.Title,
            Duration = track.DurationSeconds,
            Track = track.TrackNumber,
        });
        if (id is null || _services?.GetService(typeof(IDownloadService)) is not IDownloadService downloads)
        {
            _logger.LogWarning("Lidarr brought '{Artist} - {Title}', but there is no download pipeline to take it", track.Artist, track.Title);
            return;
        }

        // A song nobody hearted lands quietly; an album heart gets one notice for the album.
        _imports.Offer(id, importedPath, quiet: song is null || job.AlbumHeart);
        try
        {
            await downloads.ExecuteAcquisitionAsync(provider, id, triggerAlbumDownload: false, forcePermanent: true,
                sourceOverride: DownloadSource.Lidarr, CancellationToken.None, requestedBy: job.AskedBy());
            if (_imports.Withdraw(id))
                // The pipeline found the song in the library and never took this file.
                await DropAsync(track, importedPath, before, "Octo already had this song");
            lock (landed) AddKeys(landed, song, track, album);
        }
        catch (Exception ex)
        {
            _imports.Withdraw(id);
            _logger.LogInformation("Lidarr's '{Artist} - {Title}' was not taken: {Message}", track.Artist, track.Title, ex.Message);
            if (song is not null) job.Missed[KeyOf(song)] = ex.Message;
            if (File.Exists(importedPath)) await DropAsync(track, importedPath, before, $"Octo did not take it ({ex.Message})");
        }
    }

    private static void AddKeys(HashSet<string> landed, Song? song, LidarrImportedTrack track, Album album)
    {
        if (song is not null) landed.Add(KeyOf(song));
        landed.Add(TitleKey(track.Artist ?? album.Artist, track.Title));
        if (song is not null) landed.Add(TitleKey(song.Artist, song.Title));
    }

    private sealed record Drop(string Reason, Octo.Services.Library.OwnedCopy? Owned);

    /// <summary>
    /// Why an imported file should not join the library, or null when it should. A song removed
    /// with a library action stays removed, and a song already in the library is not added twice,
    /// the same rules as a Soulseek download. An owned lossy copy is queued for Better quality
    /// instead, which swaps it in place and keeps its plays (W8).
    /// </summary>
    private async Task<Drop?> ShouldDropAsync(Album album, LidarrImportedTrack track, Song? song, string importedPath, AlbumJob job)
    {
        var artist = song?.Artist ?? track.Artist ?? album.Artist;
        var title = string.IsNullOrWhiteSpace(track.Title) ? song?.Title : track.Title;
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;

        if (_services?.GetService(typeof(Octo.Services.Library.LibraryActionJournal)) is Octo.Services.Library.LibraryActionJournal journal
            && journal.IsNeverRequested(artist, title))
            return new Drop("it was removed with a library action", null);

        if (!_subsonicSettings.CurrentValue.SkipOwnedSongs
            || _services?.GetService(typeof(Octo.Services.Library.LibraryOwnership)) is not Octo.Services.Library.LibraryOwnership ownership)
            return null;
        var owned = await ownership.FindAsync(artist, title, track.DurationSeconds ?? song?.Duration, album.Title);
        // The import itself, once Navidrome has scanned it, is not a second copy.
        if (owned is null || SamePath(owned.AbsolutePath, importedPath)) return null;
        if (!owned.Lossless && LidarrTrackFetcher.IsLossless(track) && QueueUpgrade(owned, artist, title, album.Title, job))
            return new Drop($"you have it as {owned.Suffix.ToUpperInvariant()}, which is queued for a higher quality copy", owned);
        return new Drop($"it is already in your library ({owned.Suffix.ToUpperInvariant()})", owned);
    }

    /// <summary>Queue Better quality for an owned lossy copy, when every gate of the action is open
    /// for the person who hearted the album.</summary>
    private bool QueueUpgrade(Octo.Services.Library.OwnedCopy owned, string artist, string title, string album, AlbumJob job)
    {
        if (owned.NavidromeId is null || job.AskedBy() is not { Count: > 0 } askers) return false;
        if (_services?.GetService(typeof(Octo.Services.Library.UpgradeQueue)) is not Octo.Services.Library.UpgradeQueue queue
            || _services.GetService(typeof(IOptionsMonitor<LibraryActionSettings>)) is not IOptionsMonitor<LibraryActionSettings> monitor
            || !Octo.Services.Library.LibraryOwnership.UpgradeAllowed(monitor.CurrentValue, askers[0]))
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

    private async Task<bool> TryAcquireAsync(
        Func<Task<bool>> work, string kind, string provider, string externalId, bool notifyFailure)
    {
        try
        {
            return await work();
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
                _notifications.Notify(new NotificationEvent
                {
                    Type = NotificationEventType.DownloadFailed,
                    Source = "Lidarr",
                    Detail = ex.Message,
                });
            }
            return false;
        }
    }
}
