using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Library;
using Octo.Services.Lidarr;
using Octo.Services.Local;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// A heart on a song Octo found outside the library downloads it, then favourites it for whoever
/// hearted it. A heart on a song that is already in the library is a favourite and nothing else:
/// it never downloads, never waits behind other downloads or a Soulseek outage, and never sends
/// Lidarr for a whole album. An owned MP3 is also queued for Better quality.
/// </summary>
public sealed class HeartOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-heart-owned-" + Guid.NewGuid().ToString("N"));
    private readonly TrackAcquisitionQueue _queue = new(NullLogger<TrackAcquisitionQueue>.Instance);
    private readonly Mock<IDownloadService> _downloads = new();
    private readonly Mock<ILidarrHeartAcquisitionService> _lidarr = new();
    private readonly Mock<IMusicMetadataService> _metadata = new();
    private readonly AcquisitionTracker _tracker;
    private readonly StarOnArrival _stars;
    private readonly UpgradeQueue _upgrades = new();
    private readonly ConcurrentQueue<(string Endpoint, Dictionary<string, string> Parameters)> _calls = new();
    private IReadOnlyList<LibraryOwnership.Candidate> _library = [];

    public HeartOwnershipTests()
    {
        Directory.CreateDirectory(_root);
        _tracker = new AcquisitionTracker(NullLogger<AcquisitionTracker>.Instance, null)
        {
            VisibilityPoll = TimeSpan.FromMilliseconds(10),
            LibraryLookup = (_, title, _, _) => Task.FromResult<string?>("nd-" + title.ToLowerInvariant()),
        };
        _stars = new StarOnArrival(_tracker, scopes: null!, TestOptions.Monitor(new SubsonicSettings()),
            NullLogger<StarOnArrival>.Instance)
        {
            Call = (endpoint, parameters) =>
            {
                _calls.Enqueue((endpoint, parameters));
                var body = endpoint == "rest/getSong"
                    ? "{\"subsonic-response\":{\"status\":\"ok\",\"song\":{\"id\":\"" + parameters["id"] + "\",\"albumId\":\"al-1\"}}}"
                    : """{"subsonic-response":{"status":"ok"}}""";
                return Task.FromResult(Encoding.UTF8.GetBytes(body));
            },
        };
        _metadata.Setup(m => m.GetSongAsync("deezer", "teardrop")).ReturnsAsync(() => Song("Teardrop", "teardrop", 330));
        _metadata.Setup(m => m.GetAlbumAsync("deezer", "mezzanine")).ReturnsAsync(() => new Album
        {
            Title = "Mezzanine", Artist = "Massive Attack",
            Songs = [Song("Angel", "angel", 380), Song("Teardrop", "teardrop", 330)],
        });
        _lidarr.Setup(l => l.TryAcquireTrackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()))
            .ReturnsAsync(true);
        _downloads.Setup(d => d.DownloadAlbumWithSourceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DownloadSource>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(true);
    }

    public void Dispose()
    {
        _stars.Dispose();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static Song Song(string title, string id, int seconds) => new()
    {
        Title = title, Artist = "Massive Attack", Album = "Mezzanine", Duration = seconds,
        ExternalProvider = "deezer", ExternalId = id,
    };

    private static LibraryOwnership.Candidate Owned(string id, string title, string suffix, int bitRate = 1000) =>
        new(id, "Massive Attack", title, "Mezzanine", title == "Angel" ? 380 : 330, suffix, bitRate);

    private static readonly LibraryActionSettings UpgradesOn = new()
    {
        Enabled = true, DryRun = false, AllowedUsers = ["alice"],
        Actions = [new LibraryActionDefinition { Action = LibraryAction.BetterQuality, Enabled = true }],
    };

    private HeartAcquisitionCoordinator Coordinator(IEnumerable<HeartDownloadSource>? chain = null, bool skipOwned = true,
        ISoulseekLink? soulseek = null, LibraryActionSettings? actions = null)
    {
        var subsonic = TestOptions.Monitor(new SubsonicSettings
        {
            SkipOwnedSongs = skipOwned,
            HeartDownloadSources = (chain ?? [HeartDownloadSource.Soulseek]).Select(source => new HeartDownloadStep
            {
                Source = source, SongEnabled = true, AlbumEnabled = true,
            }).ToList(),
        });
        var ownership = new LibraryOwnership(subsonic, null!, null!, null!, new Mock<ILocalLibraryService>().Object,
            NullLogger<LibraryOwnership>.Instance)
        {
            Search = (_, _) => Task.FromResult<IReadOnlyList<LibraryOwnership.Candidate>?>(_library),
            Resolve = (id, _) =>
            {
                var path = Path.Combine(_root, $"{id}.file");
                File.WriteAllBytes(path, [1]);
                return Task.FromResult<string?>(path);
            },
        };
        var sources = new UpgradeSources(TestOptions.Monitor(new LibraryActionSettings()),
            TestOptions.Monitor(new SoulseekSettings { BaseUrl = "http://slskd:5030", Username = "u", Password = "p" }),
            TestOptions.Monitor(new LidarrSettings()));
        var hearts = new HeartOwnership(ownership, _metadata.Object, subsonic, NullLogger<HeartOwnership>.Instance,
            _upgrades, TestOptions.Monitor(actions ?? new LibraryActionSettings()), sources);
        return new HeartAcquisitionCoordinator(subsonic, _queue, _downloads.Object, _lidarr.Object,
            NullLogger<HeartAcquisitionCoordinator>.Instance, _tracker, soulseek: soulseek, owned: hearts, stars: _stars);
    }

    private static SubsonicCredential Alice() => SubsonicCredential.From(new Dictionary<string, string>
    {
        ["u"] = "alice", ["t"] = "token-alice", ["s"] = "salt-alice", ["c"] = "Symfonium",
    })!;

    /// <summary>What the star endpoint does before it hands a song heart to the coordinator.</summary>
    private void Heart(string id = "teardrop", bool octoApp = false)
    {
        _tracker.Begin("deezer", id, id, "alice", "Massive Attack", "Teardrop", "Mezzanine");
        if (!octoApp) _stars.HoldSong("deezer", id, Alice(), "alice");
    }

    private List<Dictionary<string, string>> Stars() =>
        _calls.Where(call => call.Endpoint == "rest/star").Select(call => call.Parameters).ToList();

    [Fact]
    public async Task AHeartOnASongYouHave_FavouritesYourCopyAndDownloadsNothing()
    {
        _library = [Owned("nd-9", "Teardrop", "flac")];
        Heart();

        await Coordinator().AcquireTrackAsync("deezer", "teardrop", "alice").WaitAsync(TimeSpan.FromSeconds(5));

        await LastFmScrobbleServiceTests.Until(() => Stars().Count == 1);
        var star = Stars().Single();
        Assert.Equal("nd-9", star["id"]);
        Assert.Equal("alice", star["u"]);
        Assert.True(_queue.IsIdle);
        Assert.Empty(_upgrades.Snapshot());
    }

    [Fact]
    public async Task AHeartOnAnMp3YouHave_FavouritesItAndQueuesBetterQuality()
    {
        _library = [Owned("nd-9", "Teardrop", "mp3", 320)];
        Heart();

        await Coordinator(actions: UpgradesOn).AcquireTrackAsync("deezer", "teardrop", "alice").WaitAsync(TimeSpan.FromSeconds(5));

        await LastFmScrobbleServiceTests.Until(() => Stars().Count == 1);
        Assert.Equal("nd-9", Stars().Single()["id"]);
        Assert.True(_queue.IsIdle);
        var job = Assert.Single(_upgrades.Snapshot());
        Assert.Equal("nd-9", job.NavidromeId);
        Assert.Equal("heart", job.Origin);
    }

    [Fact]
    public async Task AHeartOnASongYouDoNotHave_DownloadsIt()
    {
        Heart();

        var chain = Coordinator().AcquireTrackAsync("deezer", "teardrop", "alice");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var download = await _queue.DequeueAsync(timeout.Token);

        Assert.Equal("teardrop", download!.ExternalId);
        Assert.True(download.IsStar);
        Assert.Empty(Stars());
        _queue.Release(download);
        download.Completion.TrySetResult(Path.Combine(_root, "teardrop.flac"));
        await chain.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>The heart was the download: it is not a favourite when the song lands, unless
    /// StarDownloadsForRequester asks for that (off by default).</summary>
    [Fact]
    public async Task AHeartOnASongYouDoNotHave_IsNotAFavouriteWhenItLands()
    {
        Heart();

        var chain = Coordinator().AcquireTrackAsync("deezer", "teardrop", "alice");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var download = await _queue.DequeueAsync(timeout.Token);
        // What the download does once the file is placed.
        _tracker.Imported("deezer", "teardrop", "Massive Attack", "Teardrop", Path.Combine(_root, "teardrop.flac"));
        _queue.Release(download!);
        download!.Completion.TrySetResult(Path.Combine(_root, "teardrop.flac"));
        await chain.WaitAsync(TimeSpan.FromSeconds(5));

        await LastFmScrobbleServiceTests.Until(() => _tracker.All().Single(row => row.ExternalId == "teardrop").State == AcquisitionState.Done);
        await Task.Delay(100);
        Assert.Empty(Stars());
        Assert.Equal(0, _stars.Held);
    }

    [Fact]
    public async Task AHeartOnASongYouHave_NeverWaitsForSoulseek()
    {
        _library = [Owned("nd-9", "Teardrop", "flac")];
        Heart();

        // slskd is offline and a Soulseek heart would wait up to six hours.
        await Coordinator(soulseek: new OfflineLink()).AcquireTrackAsync("deezer", "teardrop", "alice")
            .WaitAsync(TimeSpan.FromSeconds(5));

        await LastFmScrobbleServiceTests.Until(() => Stars().Count == 1);
        Assert.True(_queue.IsIdle);
    }

    [Fact]
    public async Task AHeartOnASongYouHave_NeverSendsLidarrForTheAlbum()
    {
        _library = [Owned("nd-9", "Teardrop", "flac")];
        Heart();

        await Coordinator([HeartDownloadSource.Lidarr, HeartDownloadSource.Soulseek])
            .AcquireTrackAsync("deezer", "teardrop", "alice").WaitAsync(TimeSpan.FromSeconds(5));

        await LastFmScrobbleServiceTests.Until(() => Stars().Count == 1);
        _lidarr.Verify(l => l.TryAcquireTrackAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task FromOctosOwnApps_ASongYouHaveIsLeftAsItIs()
    {
        // Their heart means Add, and the song is already added: no favourite, no download.
        _library = [Owned("nd-9", "Teardrop", "flac")];
        Heart(octoApp: true);

        await Coordinator().AcquireTrackAsync("deezer", "teardrop", "alice").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_queue.IsIdle);
        Assert.Empty(Stars());
        await LastFmScrobbleServiceTests.Until(() => _tracker.All().Single(row => row.ExternalId == "teardrop").State == AcquisitionState.Done);
        Assert.Equal("nd-9", _tracker.All().Single(row => row.ExternalId == "teardrop").LibraryId);
    }

    [Fact]
    public async Task WithTheCheckOff_EveryHeartDownloads()
    {
        _library = [Owned("nd-9", "Teardrop", "flac")];
        Heart();

        _ = Coordinator(skipOwned: false).AcquireTrackAsync("deezer", "teardrop", "alice");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var download = await _queue.DequeueAsync(timeout.Token);

        Assert.Equal("teardrop", download!.ExternalId);
        _queue.Release(download);
        download.Completion.TrySetResult(Path.Combine(_root, "teardrop.flac"));
    }

    [Fact]
    public async Task AnAlbumYouHaveWhole_IsFavouritedAndNothingDownloads()
    {
        _library = [Owned("nd-1", "Angel", "flac"), Owned("nd-3", "Teardrop", "flac")];
        _tracker.BeginAlbum("deezer", "mezzanine", "alice");
        _stars.HoldAlbum("deezer", "mezzanine", Alice(), "alice");

        await Coordinator().AcquireAlbumAsync("deezer", "mezzanine", "alice").WaitAsync(TimeSpan.FromSeconds(5));

        await LastFmScrobbleServiceTests.Until(() => Stars().Any(call => call.ContainsKey("albumId")));
        Assert.Equal("al-1", Stars().Single(call => call.ContainsKey("albumId"))["albumId"]);
        _downloads.Verify(d => d.DownloadAlbumWithSourceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DownloadSource>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()), Times.Never);
    }

    [Fact]
    public async Task AnAlbumYouHavePartly_GoesToFetchTheRest()
    {
        _library = [Owned("nd-3", "Teardrop", "flac")];

        await Coordinator().AcquireAlbumAsync("deezer", "mezzanine", "alice").WaitAsync(TimeSpan.FromSeconds(5));

        _downloads.Verify(d => d.DownloadAlbumWithSourceAsync("deezer", "mezzanine", It.IsAny<DownloadSource>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()), Times.Once);
    }

    private sealed class OfflineLink : ISoulseekLink
    {
        public Task<SoulseekServerReading?> ReadAsync(bool fresh, CancellationToken ct) =>
            Task.FromResult<SoulseekServerReading?>(new(SoulseekLinkState.NotLoggedIn, "Disconnecting", null, null));
        public TimeSpan HoldLimit => TimeSpan.FromHours(6);
        public DateTime UtcNow => DateTime.UtcNow;
        public Task<bool> WaitForLoginAsync(DateTime deadlineUtc, CancellationToken ct) =>
            Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => false);
    }
}
