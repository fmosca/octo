using System.Collections.Concurrent;
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
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// A heart through Lidarr keeps the promises a Soulseek heart does. What Lidarr imports goes
/// through the download pipeline song by song, so it meets the same checks; a song already in the
/// library is taken back out (an owned MP3 is queued for Better quality instead), a song removed
/// with a library action stays removed, and the album's monitoring goes back to how it was. The
/// heart learns whether its songs landed, so the next source can try what Lidarr could not get.
/// </summary>
public sealed class LidarrHeartProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-lidarr-heart-" + Guid.NewGuid().ToString("N"));
    private readonly LidarrTrackFetcherTests.FakeLidarr _lidarr;
    private readonly LidarrAlbumClaims _claims = new();
    private readonly LibraryActionJournal _journal = new();
    private readonly UpgradeQueue _upgrades = new();
    private readonly LidarrImportHandoff _imports = new();
    private readonly ExternalIdRegistry _ids = new();
    private readonly Mock<IDownloadService> _downloads = new();
    private readonly ConcurrentQueue<(string Id, string Title, bool Quiet)> _taken = new();
    private readonly HashSet<string> _refused = [];
    private IReadOnlyList<LibraryOwnership.Candidate> _owned = [];

    public LidarrHeartProtectionTests()
    {
        Directory.CreateDirectory(_root);
        _lidarr = new LidarrTrackFetcherTests.FakeLidarr(_root);
        // The pipeline as far as Lidarr's files go: take the offered file, or refuse it the way
        // AcoustID refuses a wrong recording, which leaves it where Lidarr put it.
        _downloads.Setup(d => d.ExecuteAcquisitionAsync("soulseek", It.IsAny<string>(), false, true, DownloadSource.Lidarr,
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>(), false, null))
            .Returns((string _, string id, bool _, bool _, DownloadSource? _, CancellationToken _, IReadOnlyList<string>? _, bool _,
                ReplacementHandoff? _) =>
            {
                var title = _ids.Lookup(id)?.Title ?? id;
                if (_refused.Contains(title)) throw new FileNotFoundException("AcoustID identified Lidarr's file as a live recording");
                var import = _imports.Take(id) ?? throw new InvalidOperationException("nothing offered for " + id);
                var placed = Path.Combine(_root, $"Massive Attack - {title}.flac");
                File.Move(import.Path, placed);
                _taken.Enqueue((id, title, import.Quiet));
                return Task.FromResult(placed);
            });
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

    private string Id(string title) => _ids.Register(new SoulseekRouting
    {
        Kind = RoutingKind.Song, Artist = "Massive Attack", Title = title, Album = "Mezzanine",
    });

    private LidarrHeartAcquisitionService Service(LibraryActionSettings? actions = null, int timeoutSeconds = 30)
    {
        var factory = new ReviewFixtures.OneClientFactory(_lidarr);
        var lidarr = TestOptions.Monitor(new LidarrSettings
        {
            BaseUrl = "http://lidarr:8686", ApiKey = "k", RootFolderPath = "/data/music",
            QualityProfileId = 1, MetadataProfileId = 1, ImportTimeoutSeconds = timeoutSeconds,
        });
        var subsonic = TestOptions.Monitor(new SubsonicSettings
        {
            Url = "http://navidrome.test", AutoDetectDownloadPath = false, SkipOwnedSongs = true,
        });
        var identity = new NavidromeIdentityService(subsonic, factory, NullLogger<NavidromeIdentityService>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = _root }).Build();
        var metadata = new Mock<IMusicMetadataService>();
        Song Song(string title, int track) => new()
        {
            Title = title, Artist = "Massive Attack", Album = "Mezzanine", Track = track,
            ExternalProvider = "soulseek", ExternalId = Id(title),
        };
        metadata.Setup(m => m.GetAlbumAsync("soulseek", "mezzanine")).ReturnsAsync(() => new Album
        {
            Title = "Mezzanine", Artist = "Massive Attack", Songs = [Song("Angel", 1), Song("Teardrop", 3)],
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
            .AddSingleton(_downloads.Object)
            .AddSingleton<IOptionsMonitor<LibraryActionSettings>>(TestOptions.Monitor(actions ?? new LibraryActionSettings()))
            .BuildServiceProvider();
        return new LidarrHeartAcquisitionService(new LidarrClient(factory, lidarr), metadata.Object, null!, lidarr, subsonic,
            config, identity,
            new NotificationService([], TestOptions.Monitor(new NotificationSettings()), NullLogger<NotificationService>.Instance),
            NullLogger<LidarrHeartAcquisitionService>.Instance, claims: _claims, services: services, imports: _imports, ids: _ids)
        {
            PollOverride = TimeSpan.FromMilliseconds(20),
        };
    }

    private Task<bool> HeartTheAlbumAsync(LibraryActionSettings? actions = null)
    {
        _lidarr.OnSearch = () => { _lidarr.Import(1, "Angel", "FLAC"); _lidarr.Import(3, "Teardrop", "FLAC"); };
        return Service(actions).TryAcquireAlbumAsync("soulseek", "mezzanine", requestedBy: "alice").WaitAsync(TimeSpan.FromSeconds(20));
    }

    private IEnumerable<string> Taken() => _taken.Select(t => t.Title).Order();

    [Fact]
    public async Task EverySongGoesThroughThePipeline()
    {
        Assert.True(await HeartTheAlbumAsync());

        Assert.Equal(["Angel", "Teardrop"], Taken());
        Assert.Empty(_lidarr.Deleted);
        // An album heart gets one notice for the album, so its songs land quietly.
        Assert.All(_taken, t => Assert.True(t.Quiet));
        Assert.Contains(false, _lidarr.MonitorChanges);
        Assert.False(_claims.HeartBusy(LidarrTrackFetcherTests.AlbumId));
    }

    [Fact]
    public async Task ASongAlreadyInTheLibraryIsTakenBackOut()
    {
        _owned = [new("nd-9", "Massive Attack", "Teardrop", "Mezzanine", 330, "flac", 1000)];

        Assert.True(await HeartTheAlbumAsync());

        Assert.Equal(["Angel"], Taken());
        Assert.Single(_lidarr.Deleted);
        Assert.Empty(_upgrades.Snapshot());
    }

    [Fact]
    public async Task AnOwnedMp3IsQueuedForBetterQualityInstead()
    {
        _owned = [new("nd-9", "Massive Attack", "Teardrop", "Mezzanine", 330, "mp3", 320)];

        Assert.True(await HeartTheAlbumAsync(UpgradesOn));

        Assert.Equal(["Angel"], Taken());
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

        // Removed means not wanted: the album is not short of anything the next source should fetch.
        Assert.False(await HeartTheAlbumAsync());

        Assert.Equal(["Teardrop"], Taken());
        Assert.Single(_lidarr.Deleted);
    }

    [Fact]
    public async Task ASongThePipelineRefusesIsDeletedAndLeftToTheNextSource()
    {
        _refused.Add("Teardrop");

        Assert.False(await HeartTheAlbumAsync());

        Assert.Equal(["Angel"], Taken());
        // Refused before it was taken, so it is still where Lidarr put it, and goes through Lidarr.
        Assert.Single(_lidarr.Deleted);
    }

    [Fact]
    public async Task NothingArrivingInTimeIsAMissForTheNextSource()
    {
        _lidarr.OnSearch = null;

        var landed = await Service(timeoutSeconds: 1).TryAcquireAlbumAsync("soulseek", "mezzanine", requestedBy: "alice")
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(landed);
        Assert.Empty(_taken);
        Assert.False(_claims.HeartBusy(LidarrTrackFetcherTests.AlbumId));
    }

    [Fact]
    public async Task ASecondHeartOnTheSameAlbumJoinsAndIsAnsweredToo()
    {
        var release = new TaskCompletionSource();
        _lidarr.OnSearch = () => _ = release.Task.ContinueWith(_ => { _lidarr.Import(1, "Angel", "FLAC"); _lidarr.Import(3, "Teardrop", "FLAC"); });
        var service = Service();

        var first = service.TryAcquireAlbumAsync("soulseek", "mezzanine", requestedBy: "alice");
        await LastFmScrobbleServiceTests.Until(() => _lidarr.Searches == 1);
        var second = service.TryAcquireAlbumAsync("soulseek", "mezzanine", requestedBy: "bob");
        release.SetResult();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, _lidarr.Searches);
        Assert.Equal(["Angel", "Teardrop"], Taken());
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
