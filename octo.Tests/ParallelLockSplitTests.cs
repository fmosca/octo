using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Local;
using Octo.Services.Notifications;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// With job folders proven, the transfer runs outside DownloadLock and everything after it runs
/// inside. Twenty downloads with random transfer times: never more transfers than the width, more
/// than one at once (or the split proves nothing), and never two placements at once.
/// </summary>
public sealed class ParallelLockSplitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-split-" + Guid.NewGuid().ToString("N"));

    public ParallelLockSplitTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Counter
    {
        private int _now;
        public int Most;
        public void Enter()
        {
            var now = Interlocked.Increment(ref _now);
            lock (this) Most = Math.Max(Most, now);
        }
        public void Leave() => Interlocked.Decrement(ref _now);
    }

    private sealed class SplitService(string root, ILocalLibraryService library, IMusicMetadataService metadata,
        IServiceProvider services, Counter transfers)
        : BaseDownloadService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = root }).Build(),
            library,
            metadata,
            TestOptions.Monitor(new SubsonicSettings { AutoDetectDownloadPath = false }),
            TestOptions.Monitor(new GenreSettings()),
            new NavidromeIdentityService(
                TestOptions.Monitor(new SubsonicSettings { AutoDetectDownloadPath = false }),
                Mock.Of<IHttpClientFactory>(), NullLogger<NavidromeIdentityService>.Instance),
            new DownloadHistoryService(Path.Combine(root, "history.json"), NullLogger<DownloadHistoryService>.Instance),
            new NotificationService([], TestOptions.Monitor(new NotificationSettings()), NullLogger<NotificationService>.Instance),
            services,
            NullLogger.Instance)
    {
        protected override string ProviderName => "test";
        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);
        protected override string? ExtractExternalIdFromAlbumId(string albumId) => null;

        protected override async Task<string> DownloadTrackAsync(string trackId, Song song, bool suppressNotify,
            DownloadSource? sourceOverride, bool upgradeSearch, CancellationToken cancellationToken)
        {
            transfers.Enter();
            try { await Task.Delay(Random.Shared.Next(2, 25)); }
            finally { transfers.Leave(); }
            var landed = Path.Combine(root, ".octo-incoming", $"{trackId}-{Guid.NewGuid():N}.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(landed)!);
            File.WriteAllBytes(landed, AudioFixtures.Mp3());
            return landed;
        }

        public Task<string> Get(string id) =>
            ExecuteAcquisitionAsync("test", id, false, true, null, CancellationToken.None);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(3, false)]
    public async Task TransfersOverlapUpToTheWidthAndPlacementsNever(int width, bool proven)
    {
        var transfers = new Counter();
        var placements = new Counter();
        var library = new Mock<ILocalLibraryService>();
        library.Setup(l => l.RegisterDownloadedSongAsync(It.IsAny<Song>(), It.IsAny<string>()))
            .Returns(async () =>
            {
                placements.Enter();
                await Task.Delay(3);
                placements.Leave();
            });
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(m => m.GetSongAsync("test", It.IsAny<string>()))
            .ReturnsAsync((string _, string id) => new Song
            {
                Title = $"Song {id}", Artist = "Artist", Album = "Album", ExternalProvider = "test", ExternalId = id,
            });

        var concurrency = new DownloadConcurrency(TestOptions.Monitor(new SoulseekSettings { ParallelDownloads = width }));
        if (proven) concurrency.Prove();
        var services = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<SoulseekSettings>>(
                TestOptions.Monitor(new SoulseekSettings { ParallelDownloads = width }))
            .AddSingleton(concurrency)
            .BuildServiceProvider();
        var service = new SplitService(_root, library.Object, metadata.Object, services, transfers);

        for (var round = 0; round < 3; round++)
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => service.Get($"r{round}-{i}")))
                .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, placements.Most);
        if (proven)
        {
            Assert.True(transfers.Most > 1, "the transfers never overlapped, so the split proves nothing");
            Assert.True(transfers.Most <= width, $"{transfers.Most} transfers at once against a width of {width}");
        }
        else
        {
            Assert.Equal(1, transfers.Most);
        }
        Assert.Equal(0, concurrency.Transfers.InUse);
    }
}
