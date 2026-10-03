using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Library;
using Octo.Services.Local;
using Octo.Services.Notifications;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// A heart, an album walk or a play never adds a second copy of a song already in the library; a
/// lossy copy is upgraded in place instead. These pin down what counts as the same song, the
/// decision, and the download path keeping to it.
/// </summary>
public sealed class LibraryOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-owned-" + Guid.NewGuid().ToString("N"));

    public LibraryOwnershipTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static LibraryOwnership.Candidate C(string id, string artist, string title, int? seconds, string suffix = "flac",
        string? album = null, int bitRate = 900) => new(id, artist, title, album, seconds, suffix, bitRate);

    // ---- The same song ------------------------------------------------------------------------

    [Theory]
    [InlineData(203, true)]
    [InlineData(211, false)]
    public void TheSameSongIsWithinEightSeconds(int seconds, bool same) =>
        Assert.Equal(same, LibraryOwnership.SameSong(C("1", "Drake", "Started From the Bottom", seconds), "Drake", "Started From the Bottom", 200, null));

    [Fact]
    public void ALiveTakeIsNotTheStudioSong() =>
        Assert.False(LibraryOwnership.SameSong(C("1", "Drake", "Started From the Bottom (Live)", 200), "Drake", "Started From the Bottom", 200, null));

    [Fact]
    public void AFeatureWrittenEitherWayIsTheSameSong() =>
        Assert.True(LibraryOwnership.SameSong(C("1", "Drake feat. Rihanna", "Too Good", 263), "Drake", "Too Good (feat. Rihanna)", 263, null));

    [Fact]
    public void WithoutALengthOnlyTheSameAlbumCounts()
    {
        var intro = C("1", "Drake", "Intro", null, album: "Thank Me Later");
        Assert.True(LibraryOwnership.SameSong(intro, "Drake", "Intro", null, "Thank Me Later"));
        Assert.False(LibraryOwnership.SameSong(intro, "Drake", "Intro", null, "Take Care"));
        Assert.False(LibraryOwnership.SameSong(intro, "Drake", "Intro", null, null));
    }

    private LibraryOwnership Ownership(IReadOnlyList<LibraryOwnership.Candidate>? library, Mock<ILocalLibraryService>? local = null)
    {
        var ownership = new LibraryOwnership(TestOptions.Monitor(new SubsonicSettings()), null!, null!, null!,
            (local ?? new Mock<ILocalLibraryService>()).Object, NullLogger<LibraryOwnership>.Instance)
        {
            Search = (_, _) => Task.FromResult(library),
            Resolve = (id, _) =>
            {
                var path = Path.Combine(_root, $"{id}.file");
                File.WriteAllBytes(path, [1]);
                return Task.FromResult<string?>(path);
            },
        };
        return ownership;
    }

    [Fact]
    public async Task TheLosslessCopyIsChosenOverALossyOne()
    {
        var owned = await Ownership([C("mp3", "A", "Song", 200, "mp3", bitRate: 320), C("flac", "A", "Song", 201)])
            .FindAsync("A", "Song", 200, null);
        Assert.Equal("flac", owned!.NavidromeId);
        Assert.True(owned.Lossless);
    }

    [Fact]
    public async Task ACopyWhoseFileCannotBeFoundIsNotOwned()
    {
        var ownership = Ownership([C("gone", "A", "Song", 200)]);
        ownership.Resolve = (_, _) => Task.FromResult<string?>(null);
        Assert.Null(await ownership.FindAsync("A", "Song", 200, null));
    }

    [Fact]
    public async Task WithoutNavidromeOctosOwnDownloadsStillCount()
    {
        var path = Path.Combine(_root, "Song.flac");
        File.WriteAllBytes(path, [1]);
        var local = new Mock<ILocalLibraryService>();
        local.Setup(l => l.FindMappingByTagsAsync("A", "Song", null)).ReturnsAsync(new LocalSongMapping { LocalPath = path });
        var owned = await Ownership(null, local).FindAsync("A", "Song", 200, null);
        Assert.Equal(path, owned!.AbsolutePath);
        Assert.Null(owned.NavidromeId);
    }

    [Fact]
    public async Task ANavidromeThatFailsNeverHoldsADownloadBack()
    {
        var ownership = Ownership([]);
        ownership.Search = (_, _) => throw new HttpRequestException("down");
        Assert.Null(await ownership.FindAsync("A", "Song", 200, null));
    }

    // ---- The decision ------------------------------------------------------------------------

    private static OwnedCopy Copy(bool lossless, string? id = "nd-1") => new(id, "/music/x", lossless ? "flac" : "mp3", 320, lossless);

    [Theory]
    [InlineData(null, true, true, OwnedDecision.Download)]
    [InlineData(true, true, true, OwnedDecision.KeepYours)]
    [InlineData(false, true, true, OwnedDecision.KeepAndUpgrade)]
    [InlineData(false, false, true, OwnedDecision.KeepYours)]
    [InlineData(false, true, false, OwnedDecision.KeepYours)]
    public void TheDecision(bool? lossless, bool sourceCanBeLossless, bool upgradeAllowed, OwnedDecision expected) =>
        Assert.Equal(expected, LibraryOwnership.Decide(lossless is null ? null : Copy(lossless.Value), sourceCanBeLossless, upgradeAllowed));

    [Fact]
    public void ALossyCopyNavidromeDoesNotKnowIsKeptNotUpgraded() =>
        Assert.Equal(OwnedDecision.KeepYours, LibraryOwnership.Decide(Copy(false, id: null), true, true));

    // ---- The download path -------------------------------------------------------------------

    private sealed class Harness(string root, IMusicMetadataService metadata, IServiceProvider services, DownloadSource source)
        : BaseDownloadService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = root }).Build(),
            Mock.Of<ILocalLibraryService>(),
            metadata,
            TestOptions.Monitor(new SubsonicSettings { AutoDetectDownloadPath = false, DownloadSource = source }),
            TestOptions.Monitor(new GenreSettings()),
            new NavidromeIdentityService(TestOptions.Monitor(new SubsonicSettings { AutoDetectDownloadPath = false }),
                Mock.Of<IHttpClientFactory>(), NullLogger<NavidromeIdentityService>.Instance),
            new DownloadHistoryService(Path.Combine(root, "history.json"), NullLogger<DownloadHistoryService>.Instance),
            new NotificationService([], TestOptions.Monitor(new NotificationSettings()), NullLogger<NotificationService>.Instance),
            services,
            NullLogger.Instance)
    {
        public int Transfers;
        protected override string ProviderName => "test";
        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);
        protected override string? ExtractExternalIdFromAlbumId(string albumId) => null;

        protected override Task<string> DownloadTrackAsync(string trackId, Song song, bool suppressNotify,
            DownloadSource? sourceOverride, bool upgradeSearch, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Transfers);
            var landed = Path.Combine(root, ".octo-incoming", $"{trackId}-{Guid.NewGuid():N}.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(landed)!);
            File.WriteAllBytes(landed, AudioFixtures.Mp3());
            return Task.FromResult(landed);
        }

        public Task<string> Get(string id, bool upgradeSearch = false) =>
            ExecuteAcquisitionAsync("test", id, false, true, null, CancellationToken.None, ["alice"], upgradeSearch: upgradeSearch);

        public Task<bool> Album(string id) =>
            DownloadAlbumWithSourceAsync("test", id, DownloadSource.Soulseek, suppressSummary: false, requestedBy: ["alice"]);
    }

    private (Harness Service, UpgradeQueue Queue) Build(IReadOnlyList<LibraryOwnership.Candidate> library,
        DownloadSource source = DownloadSource.Soulseek, bool betterQuality = true, bool skip = true)
    {
        var songs = new[] { ("t1", "Intro", 90), ("t2", "Hold On", 200), ("t3", "Started", 180), ("t4", "Too Much", 260) }
            .Select((t, i) => new Song
            {
                ExternalProvider = "test", ExternalId = t.Item1, Title = t.Item2, Artist = "Drake", Album = "NWTS",
                Duration = t.Item3, Track = i + 1,
            }).ToList();
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(m => m.GetSongAsync("test", It.IsAny<string>())).ReturnsAsync((string _, string id) => songs.Single(s => s.ExternalId == id));
        metadata.Setup(m => m.GetAlbumAsync("test", "album-1")).ReturnsAsync(new Album { Id = "album-1", Title = "NWTS", Artist = "Drake", Songs = songs });

        var queue = new UpgradeQueue();
        var actions = new LibraryActionSettings
        {
            Enabled = true, DryRun = false, AllowedUsers = ["alice"],
            Actions = [new() { Action = LibraryAction.BetterQuality, Enabled = betterQuality }],
        };
        var services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<SoulseekSettings>>(TestOptions.Monitor(new SoulseekSettings()))
            .AddSingleton<IOptionsMonitor<LibraryActionSettings>>(TestOptions.Monitor(actions))
            .AddSingleton(queue)
            .AddSingleton(Ownership(library))
            .BuildServiceProvider();
        var service = new Harness(_root, metadata.Object, services, source);
        if (!skip) throw new NotSupportedException();
        return (service, queue);
    }

    [Fact]
    public async Task AnOwnedFlacIsNeverDownloadedAgain()
    {
        var (service, queue) = Build([C("nd-2", "Drake", "Hold On", 201)]);
        var path = await service.Get("t2");
        Assert.Equal(Path.Combine(_root, "nd-2.file"), path);
        Assert.Equal(0, service.Transfers);
        Assert.Empty(queue.Snapshot());
    }

    [Fact]
    public async Task AnOwnedMp3IsKeptAndQueuedForAHigherQualityCopy()
    {
        var (service, queue) = Build([C("nd-2", "Drake", "Hold On", 200, "mp3", bitRate: 320)]);
        var path = await service.Get("t2");
        Assert.Equal(Path.Combine(_root, "nd-2.file"), path);
        Assert.Equal(0, service.Transfers);
        var job = Assert.Single(queue.Snapshot());
        Assert.Equal(("nd-2", "alice", "heart"), (job.NavidromeId, job.RequestedBy, job.Origin));
    }

    [Fact]
    public async Task WithOnlyYouTubeAnOwnedMp3IsJustKept()
    {
        var (service, queue) = Build([C("nd-2", "Drake", "Hold On", 200, "mp3", bitRate: 320)], DownloadSource.YouTube);
        await service.Get("t2");
        Assert.Equal(0, service.Transfers);
        Assert.Empty(queue.Snapshot());
    }

    [Fact]
    public async Task WithBetterQualityOffAnOwnedMp3IsJustKept()
    {
        var (service, queue) = Build([C("nd-2", "Drake", "Hold On", 200, "mp3", bitRate: 320)], betterQuality: false);
        await service.Get("t2");
        Assert.Equal(0, service.Transfers);
        Assert.Empty(queue.Snapshot());
    }

    [Fact]
    public async Task ABetterQualitySearchStillDownloads()
    {
        var (service, _) = Build([C("nd-2", "Drake", "Hold On", 200, "mp3", bitRate: 320)]);
        await service.Get("t2", upgradeSearch: true);
        Assert.Equal(1, service.Transfers);
    }

    [Fact]
    public async Task ASongYouDoNotHaveDownloads()
    {
        var (service, _) = Build([C("nd-9", "Drake", "Something Else", 200)]);
        await service.Get("t2");
        Assert.Equal(1, service.Transfers);
    }

    [Fact]
    public async Task AnAlbumWalkFetchesOnlyWhatIsMissing()
    {
        var (service, queue) = Build(
        [
            C("nd-1", "Drake", "Intro", 90),
            C("nd-3", "Drake", "Started", 181),
            C("nd-4", "Drake", "Too Much", 260, "mp3", bitRate: 192),
        ]);
        Assert.True(await service.Album("album-1"));
        Assert.Equal(1, service.Transfers);                 // only Hold On
        Assert.Equal("nd-4", Assert.Single(queue.Snapshot()).NavidromeId);
    }

    [Fact]
    public void TheAlbumSummarySaysWhatWasKept()
    {
        var summary = BaseDownloadService.BuildAlbumSummary(new Album { Title = "NWTS", Artist = "Drake" }, 1, 1, 0, kept: 2, upgrading: 1)!;
        Assert.Equal(2, summary.KeptCount);
        Assert.Equal(1, summary.UpgradingCount);
        Assert.NotNull(BaseDownloadService.BuildAlbumSummary(new Album { Title = "NWTS", Artist = "Drake" }, 0, 0, 0, kept: 4));
        Assert.Null(BaseDownloadService.BuildAlbumSummary(new Album { Title = "NWTS", Artist = "Drake" }, 0, 0, 0));
    }
}
