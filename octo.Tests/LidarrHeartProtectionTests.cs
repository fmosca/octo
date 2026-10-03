using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Library;
using Octo.Services.Lidarr;
using Octo.Services.Local;
using Octo.Services.Notifications;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// A heart through Lidarr keeps the promises a Soulseek heart does. Lidarr brings the whole album,
/// so a song already in the library is taken back out (an owned MP3 is queued for Better quality
/// instead), a song removed with a library action stays removed, and the album's monitoring goes
/// back to how it was, so Lidarr does not fetch the songs Octo moved again.
/// </summary>
public sealed class LidarrHeartProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-lidarr-heart-" + Guid.NewGuid().ToString("N"));
    private readonly LidarrTrackFetcherTests.FakeLidarr _lidarr;
    private readonly LidarrAlbumClaims _claims = new();
    private readonly LibraryActionJournal _journal = new();
    private readonly UpgradeQueue _upgrades = new();
    private readonly DownloadHistoryService _history;
    private IReadOnlyList<LibraryOwnership.Candidate> _owned = [];

    public LidarrHeartProtectionTests()
    {
        Directory.CreateDirectory(_root);
        _lidarr = new LidarrTrackFetcherTests.FakeLidarr(_root);
        _history = new DownloadHistoryService(Path.Combine(_root, "history.json"), NullLogger<DownloadHistoryService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static readonly LibraryActionSettings UpgradesOn = new()
    {
        Enabled = true, DryRun = false, AllowedUsers = ["alice"],
        Actions = [new LibraryActionDefinition { Action = LibraryAction.BetterQuality, Enabled = true }],
    };

    private LidarrHeartAcquisitionService Service(LibraryActionSettings? actions = null)
    {
        var factory = new ReviewFixtures.OneClientFactory(_lidarr);
        var lidarr = TestOptions.Monitor(new LidarrSettings
        {
            BaseUrl = "http://lidarr:8686", ApiKey = "k", RootFolderPath = "/data/music",
            QualityProfileId = 1, MetadataProfileId = 1, ImportTimeoutSeconds = 30,
        });
        var subsonic = TestOptions.Monitor(new SubsonicSettings
        {
            Url = "http://navidrome.test", AutoDetectDownloadPath = false, SkipOwnedSongs = true,
        });
        var identity = new NavidromeIdentityService(subsonic, factory, NullLogger<NavidromeIdentityService>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = _root }).Build();
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(m => m.GetAlbumAsync("deezer", "mezzanine")).ReturnsAsync(() => new Album
        {
            Title = "Mezzanine", Artist = "Massive Attack",
            Songs =
            [
                new Song { Title = "Angel", Artist = "Massive Attack", Album = "Mezzanine", Track = 1, ExternalProvider = "deezer", ExternalId = "angel" },
                new Song { Title = "Teardrop", Artist = "Massive Attack", Album = "Mezzanine", Track = 3, ExternalProvider = "deezer", ExternalId = "teardrop" },
            ],
        });
        var ownership = new LibraryOwnership(subsonic, null!, null!, null!, new Mock<ILocalLibraryService>().Object,
            NullLogger<LibraryOwnership>.Instance)
        {
            Search = (_, _) => Task.FromResult<IReadOnlyList<LibraryOwnership.Candidate>?>(_owned),
            Resolve = (id, _) =>
            {
                var path = Path.Combine(_root, "owned", $"{id}.file");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [1]);
                return Task.FromResult<string?>(path);
            },
        };
        var services = new ServiceCollection()
            .AddSingleton(_journal)
            .AddSingleton(ownership)
            .AddSingleton(_upgrades)
            .AddSingleton<IOptionsMonitor<LibraryActionSettings>>(TestOptions.Monitor(actions ?? new LibraryActionSettings()))
            .BuildServiceProvider();
        return new LidarrHeartAcquisitionService(new LidarrClient(factory, lidarr), metadata.Object, null!, lidarr, subsonic,
            config, identity, new Mock<ILocalLibraryService>().Object, _history,
            new NotificationService([], TestOptions.Monitor(new NotificationSettings()), NullLogger<NotificationService>.Instance),
            NullLogger<LidarrHeartAcquisitionService>.Instance, claims: _claims, services: services);
    }

    private async Task HeartTheAlbumAsync(LibraryActionSettings? actions = null)
    {
        _lidarr.OnSearch = () => { _lidarr.Import(1, "Angel", "FLAC"); _lidarr.Import(3, "Teardrop", "FLAC"); };
        Assert.True(await Service(actions).TryAcquireAlbumAsync("deezer", "mezzanine", requestedBy: "alice"));
        // Done once the heart lets go of the album, after its monitoring went back.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !(_lidarr.Searches == 1 && !_claims.HeartBusy(LidarrTrackFetcherTests.AlbumId)))
            await Task.Delay(50);
        Assert.False(_claims.HeartBusy(LidarrTrackFetcherTests.AlbumId));
    }

    private IEnumerable<string?> Recorded() => _history.GetRecent().Select(entry => entry.Title);

    [Fact]
    public async Task ASongAlreadyInTheLibraryIsTakenBackOut()
    {
        _owned = [new("nd-9", "Massive Attack", "Teardrop", "Mezzanine", 330, "flac", 1000)];

        await HeartTheAlbumAsync();

        Assert.Equal(["Angel"], Recorded());
        Assert.Single(_lidarr.Deleted);
        Assert.Contains(false, _lidarr.MonitorChanges);
        Assert.Empty(_upgrades.Snapshot());
    }

    [Fact]
    public async Task AnOwnedMp3IsQueuedForBetterQualityInstead()
    {
        _owned = [new("nd-9", "Massive Attack", "Teardrop", "Mezzanine", 330, "mp3", 320)];

        await HeartTheAlbumAsync(UpgradesOn);

        Assert.Equal(["Angel"], Recorded());
        Assert.Single(_lidarr.Deleted);
        var job = Assert.Single(_upgrades.Snapshot());
        Assert.Equal("nd-9", job.NavidromeId);
        Assert.Equal("alice", job.RequestedBy);
    }

    [Fact]
    public async Task ARemovedSongStaysRemoved()
    {
        _journal.Record(new LibraryActionEntry("delete:angel", LibraryAction.Delete, "nd-1", "alice", "Angel", "Massive Attack",
            "Mezzanine", null, null, null, LibraryActionState.Applied, "Removed.", DryRun: false, DateTime.UtcNow));

        await HeartTheAlbumAsync();

        Assert.Equal(["Teardrop"], Recorded());
        Assert.Single(_lidarr.Deleted);
    }

    [Fact]
    public async Task NothingOwnedMeansEverySongJoins()
    {
        await HeartTheAlbumAsync();

        Assert.Equal(["Angel", "Teardrop"], Recorded().Order());
        Assert.Empty(_lidarr.Deleted);
    }

    [Fact]
    public void ATrackHeartNeverMatchesByTrackNumber()
    {
        // Deezer found the song on its single, where it is track 1. On the album, 1 is Angel.
        var heart = new Album { Title = "Mezzanine", Artist = "Massive Attack", Songs = [new Song { Title = "Teardrop", Track = 1 }] };
        var angel = new LidarrImportedTrack(1, "Angel", 1, 380, true, "/data/music/a.flac", 1, "Massive Attack", 101);

        Assert.Null(LidarrHeartAcquisitionService.MatchSong(heart, angel, matchByNumber: false));
        Assert.Same(heart.Songs[0], LidarrHeartAcquisitionService.MatchSong(heart, angel));
    }
}
