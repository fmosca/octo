using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Lidarr;

namespace Octo.Tests;

public class HeartAcquisitionCoordinatorTests
{
    private static ILogger<HeartAcquisitionCoordinator> CoordinatorLogger =>
        new Mock<ILogger<HeartAcquisitionCoordinator>>().Object;

    [Fact]
    public async Task LidarrSourceRoutesTrackAndAlbumWithoutCallingDirectDownloader()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var direct = new Mock<IDownloadService>();
        lidarr.Setup(x => x.TryAcquireTrackAsync("soulseek", "track-id", true, It.IsAny<string?>()))
            .ReturnsAsync(true);
        lidarr.Setup(x => x.TryAcquireAlbumAsync("soulseek", "album-id", true, It.IsAny<string?>()))
            .ReturnsAsync(true);
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var coordinator = new HeartAcquisitionCoordinator(
            TestOptions.Monitor(new SubsonicSettings { DownloadSource = DownloadSource.Lidarr }),
            queue, direct.Object, lidarr.Object, CoordinatorLogger);

        await coordinator.AcquireTrackAsync("soulseek", "track-id");
        await coordinator.AcquireAlbumAsync("soulseek", "album-id");

        lidarr.Verify(x => x.TryAcquireTrackAsync("soulseek", "track-id", true, It.IsAny<string?>()), Times.Once);
        lidarr.Verify(x => x.TryAcquireAlbumAsync("soulseek", "album-id", true, It.IsAny<string?>()), Times.Once);
        direct.Verify(x => x.DownloadRemainingAlbumTracksInBackground(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SoulseekSourceKeepsAlbumOnExistingDirectPath()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var direct = new Mock<IDownloadService>();
        direct.Setup(x => x.DownloadAlbumWithSourceAsync(
                "soulseek", "album-id", DownloadSource.Soulseek, false, It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(true);
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var coordinator = new HeartAcquisitionCoordinator(
            TestOptions.Monitor(new SubsonicSettings { DownloadSource = DownloadSource.Soulseek }),
            queue, direct.Object, lidarr.Object, CoordinatorLogger);

        await coordinator.AcquireAlbumAsync("soulseek", "album-id");

        direct.Verify(x => x.DownloadAlbumWithSourceAsync(
            "soulseek", "album-id", DownloadSource.Soulseek, false,
            It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()), Times.Once);
        lidarr.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DownloadSourceChangeTakesEffectWithoutRebuildingCoordinator()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var direct = new Mock<IDownloadService>();
        direct.Setup(x => x.DownloadAlbumWithSourceAsync(
                "soulseek", "first-album", DownloadSource.Soulseek, false,
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(true);
        lidarr.Setup(x => x.TryAcquireAlbumAsync("soulseek", "second-album", true, It.IsAny<string?>()))
            .ReturnsAsync(true);
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var settings = TestOptions.Monitor(
            new SubsonicSettings { DownloadSource = DownloadSource.Soulseek });
        var coordinator = new HeartAcquisitionCoordinator(settings, queue, direct.Object, lidarr.Object, CoordinatorLogger);

        await coordinator.AcquireAlbumAsync("soulseek", "first-album");
        settings.Set(new SubsonicSettings { DownloadSource = DownloadSource.Lidarr });
        await coordinator.AcquireAlbumAsync("soulseek", "second-album");

        direct.Verify(x => x.DownloadAlbumWithSourceAsync(
            "soulseek", "first-album", DownloadSource.Soulseek, false,
            It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()), Times.Once);
        lidarr.Verify(x => x.TryAcquireAlbumAsync("soulseek", "second-album", true, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task AlbumPriorityStopsAtFirstSuccessfulSource()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var direct = new Mock<IDownloadService>();
        direct.Setup(x => x.DownloadAlbumWithSourceAsync(
                "soulseek", "album-id", DownloadSource.Soulseek, true,
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(true);
        var settings = TestOptions.Monitor(new SubsonicSettings
        {
            HeartDownloadSources =
            [
                new() { Source = HeartDownloadSource.YouTube, Enabled = false },
                new() { Source = HeartDownloadSource.Soulseek, Enabled = true },
                new() { Source = HeartDownloadSource.Lidarr, Enabled = true },
            ],
        });
        var coordinator = new HeartAcquisitionCoordinator(settings,
            new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object),
            direct.Object, lidarr.Object, CoordinatorLogger);

        await coordinator.AcquireAlbumAsync("soulseek", "album-id");

        lidarr.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AlbumPriorityFallsThroughToLidarrAfterDirectFailure()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var direct = new Mock<IDownloadService>();
        direct.Setup(x => x.DownloadAlbumWithSourceAsync(
                "soulseek", "album-id", DownloadSource.Soulseek, true,
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(false);
        lidarr.Setup(x => x.TryAcquireAlbumAsync("soulseek", "album-id", true, It.IsAny<string?>()))
            .ReturnsAsync(true);
        var settings = TestOptions.Monitor(new SubsonicSettings
        {
            HeartDownloadSources =
            [
                new() { Source = HeartDownloadSource.Soulseek, Enabled = true },
                new() { Source = HeartDownloadSource.YouTube, Enabled = false },
                new() { Source = HeartDownloadSource.Lidarr, Enabled = true },
            ],
        });
        var coordinator = new HeartAcquisitionCoordinator(settings,
            new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object),
            direct.Object, lidarr.Object, CoordinatorLogger);

        await coordinator.AcquireAlbumAsync("soulseek", "album-id");

        lidarr.Verify(x => x.TryAcquireAlbumAsync("soulseek", "album-id", true, It.IsAny<string?>()), Times.Once);
        direct.Verify(x => x.DownloadAlbumWithSourceAsync(
            It.IsAny<string>(), It.IsAny<string>(), DownloadSource.YouTube,
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()), Times.Never);
    }

    [Fact]
    public async Task AlbumPriorityTriesDirectSourcesInOrderAndStopsOnSuccess()
    {
        var attempts = new List<DownloadSource>();
        var direct = new Mock<IDownloadService>();
        direct.Setup(x => x.DownloadAlbumWithSourceAsync(
                "soulseek", "album-id", It.IsAny<DownloadSource>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync((string _, string _, DownloadSource source, bool _, CancellationToken _,
                IReadOnlyList<string>? _) =>
            {
                attempts.Add(source);
                return source == DownloadSource.YouTube;
            });
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var settings = TestOptions.Monitor(new SubsonicSettings
        {
            HeartDownloadSources =
            [
                new() { Source = HeartDownloadSource.Soulseek, Enabled = true },
                new() { Source = HeartDownloadSource.YouTube, Enabled = true },
                new() { Source = HeartDownloadSource.Lidarr, Enabled = true },
            ],
        });
        var coordinator = new HeartAcquisitionCoordinator(settings,
            new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object),
            direct.Object, lidarr.Object, CoordinatorLogger);

        await coordinator.AcquireAlbumAsync("soulseek", "album-id");

        Assert.Equal([DownloadSource.Soulseek, DownloadSource.YouTube], attempts);
        lidarr.VerifyNoOtherCalls();
    }

    [Fact]
    public void LegacyFallbackMapsToOrderedSourcesWithLidarrLast()
    {
        var settings = new SubsonicSettings { DownloadSource = DownloadSource.SoulseekThenYouTube };

        var steps = settings.EffectiveHeartDownloadSources();

        Assert.Collection(steps,
            step => { Assert.Equal(HeartDownloadSource.Soulseek, step.Source); Assert.True(step.SongEnabled); Assert.True(step.AlbumEnabled); },
            step => { Assert.Equal(HeartDownloadSource.YouTube, step.Source); Assert.True(step.SongEnabled); Assert.True(step.AlbumEnabled); },
            step => { Assert.Equal(HeartDownloadSource.Lidarr, step.Source); Assert.False(step.SongEnabled); Assert.False(step.AlbumEnabled); });
    }

    [Fact]
    public async Task TrackPriorityFallsThroughFromSoulseekToLidarr()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        lidarr.Setup(x => x.TryAcquireTrackAsync("soulseek", "track-id", true, It.IsAny<string?>()))
            .ReturnsAsync(true);
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var settings = TestOptions.Monitor(new SubsonicSettings
        {
            HeartDownloadSources =
            [
                new() { Source = HeartDownloadSource.Soulseek, Enabled = true },
                new() { Source = HeartDownloadSource.YouTube, Enabled = false },
                new() { Source = HeartDownloadSource.Lidarr, Enabled = true },
            ],
        });
        var coordinator = new HeartAcquisitionCoordinator(
            settings, queue, new Mock<IDownloadService>().Object, lidarr.Object, CoordinatorLogger);

        var acquisition = coordinator.AcquireTrackAsync("soulseek", "track-id");
        var request = await queue.DequeueAsync(CancellationToken.None);
        Assert.NotNull(request);
        Assert.Equal(DownloadSource.Soulseek, request.SourceOverride);
        Assert.False(request.NotifyOnFailure);
        queue.Release(request);
        request.Completion.TrySetException(new InvalidOperationException("no peer"));
        await acquisition;

        lidarr.Verify(x => x.TryAcquireTrackAsync("soulseek", "track-id", true, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public void ConfiguredOrderIsPreservedAndMissingSourcesAreAppendedDisabled()
    {
        var settings = new SubsonicSettings
        {
            HeartDownloadSources =
            [
                new() { Source = HeartDownloadSource.Lidarr, Enabled = true },
                new() { Source = HeartDownloadSource.YouTube, Enabled = true },
            ],
        };

        var steps = settings.EffectiveHeartDownloadSources();

        Assert.Collection(steps,
            step => Assert.Equal(HeartDownloadSource.Lidarr, step.Source),
            step => Assert.Equal(HeartDownloadSource.YouTube, step.Source),
            step => { Assert.Equal(HeartDownloadSource.Soulseek, step.Source); Assert.False(step.SongEnabled); Assert.False(step.AlbumEnabled); });
    }

    [Fact]
    public async Task SongAndAlbumHeartsUseTheirOwnPerSourceSwitches()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        lidarr.Setup(x => x.TryAcquireAlbumAsync("soulseek", "album-id", true, It.IsAny<string?>()))
            .ReturnsAsync(true);
        var direct = new Mock<IDownloadService>();
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var settings = TestOptions.Monitor(new SubsonicSettings
        {
            HeartDownloadSources =
            [
                new() { Source = HeartDownloadSource.Soulseek, SongEnabled = true, AlbumEnabled = false },
                new() { Source = HeartDownloadSource.Lidarr, SongEnabled = false, AlbumEnabled = true },
                new() { Source = HeartDownloadSource.YouTube, SongEnabled = false, AlbumEnabled = false },
            ],
        });
        var coordinator = new HeartAcquisitionCoordinator(settings, queue, direct.Object, lidarr.Object, CoordinatorLogger);

        var trackAcquisition = coordinator.AcquireTrackAsync("soulseek", "track-id");
        var request = await queue.DequeueAsync(CancellationToken.None);
        Assert.NotNull(request);
        Assert.Equal(DownloadSource.Soulseek, request.SourceOverride);
        request.Completion.TrySetResult("/music/track.flac");
        queue.Release(request);
        await trackAcquisition;
        await coordinator.AcquireAlbumAsync("soulseek", "album-id");

        lidarr.Verify(x => x.TryAcquireTrackAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()), Times.Never);
        lidarr.Verify(x => x.TryAcquireAlbumAsync("soulseek", "album-id", true, It.IsAny<string?>()), Times.Once);
    }

    private static SubsonicSettings PlaySettings(bool downloadOnPlay, bool lidarrAlbumOnPlay,
        params HeartDownloadStep[] steps) => new()
    {
        DownloadOnPlay = downloadOnPlay,
        LidarrAlbumOnPlay = lidarrAlbumOnPlay,
        HeartDownloadSources = steps.Length > 0
            ? [.. steps]
            :
            [
                new HeartDownloadStep { Source = HeartDownloadSource.Lidarr, SongEnabled = true, AlbumEnabled = true },
                new HeartDownloadStep { Source = HeartDownloadSource.YouTube, SongEnabled = true, AlbumEnabled = true },
                new HeartDownloadStep { Source = HeartDownloadSource.Soulseek, SongEnabled = false, AlbumEnabled = false },
            ],
    };

    private HeartAcquisitionCoordinator Coordinator(SubsonicSettings s, TrackAcquisitionQueue q,
        ILidarrHeartAcquisitionService? lidarr = null, AcquisitionTracker? tracker = null) =>
        new(TestOptions.Monitor(s), q, new Mock<IDownloadService>().Object,
            lidarr ?? new Mock<ILidarrHeartAcquisitionService>().Object, CoordinatorLogger, tracker);

    private static TrackAcquisitionQueue NewQueue() => new(new Mock<ILogger<TrackAcquisitionQueue>>().Object);

    private static async Task<AcquisitionRequest?> NextQueued(TrackAcquisitionQueue queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            return await queue.DequeueAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task PlayStartsNothingByDefault()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var coordinator = new HeartAcquisitionCoordinator(TestOptions.Monitor(PlaySettings(false, false)),
            queue, new Mock<IDownloadService>().Object, lidarr.Object, CoordinatorLogger);

        coordinator.QueuePlay("soulseek", "track-id");

        Assert.Null(await NextQueued(queue));
        lidarr.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DownloadOnPlayQueuesTheFirstDirectSongSourceAndSkipsLidarr()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var coordinator = new HeartAcquisitionCoordinator(TestOptions.Monitor(PlaySettings(true, false)),
            queue, new Mock<IDownloadService>().Object, lidarr.Object, CoordinatorLogger);

        coordinator.QueuePlay("soulseek", "track-id", "felix");

        var request = await NextQueued(queue);
        Assert.NotNull(request);
        Assert.Equal("track-id", request.ExternalId);
        Assert.False(request.IsStar);
        Assert.True(request.ForcePermanent);
        Assert.Equal(DownloadSource.YouTube, request.SourceOverride);
        Assert.Equal(new[] { "felix" }, request.RequestedBy);
        lidarr.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LidarrAlbumOnPlayHandsEachTrackToLidarrOnce()
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        lidarr.Setup(x => x.TryAcquireTrackAsync("soulseek", "track-id", false, It.IsAny<string?>()))
            .ReturnsAsync(true);
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var coordinator = new HeartAcquisitionCoordinator(TestOptions.Monitor(PlaySettings(false, true)),
            queue, new Mock<IDownloadService>().Object, lidarr.Object, CoordinatorLogger);

        coordinator.QueuePlay("soulseek", "track-id");
        coordinator.QueuePlay("soulseek", "track-id");

        Assert.Null(await NextQueued(queue));
        lidarr.Verify(x => x.TryAcquireTrackAsync("soulseek", "track-id", false, It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public void DownloadOnPlayLeavesTheTrackToWaitForLosslessOnPlay()
    {
        var settings = PlaySettings(true, false);
        settings.WaitForLosslessOnPlay = true;
        var queue = NewQueue();
        Coordinator(settings, queue).QueuePlay("soulseek", "track-id");
        Assert.Equal(0, queue.WaitingPlays);
    }

    [Theory]
    [InlineData(false, DownloadSource.Soulseek)]
    [InlineData(true, DownloadSource.SoulseekThenYouTube)]
    public async Task SoulseekFirstPlaysFallBackToYouTubeOnlyWhenItIsOn(bool youTube, DownloadSource expected)
    {
        var queue = NewQueue();
        Coordinator(PlaySettings(true, false,
            new HeartDownloadStep { Source = HeartDownloadSource.Soulseek, SongEnabled = true, AlbumEnabled = false },
            new HeartDownloadStep { Source = HeartDownloadSource.YouTube, SongEnabled = youTube, AlbumEnabled = false }), queue)
            .QueuePlay("soulseek", "track-id");
        Assert.Equal(expected, (await NextQueued(queue))!.SourceOverride);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LidarrAlbumOnPlayTriesAgainAfterAHandOffThatDidNotTake(bool throws)
    {
        var lidarr = new Mock<ILidarrHeartAcquisitionService>();
        var calls = lidarr.SetupSequence(x => x.TryAcquireTrackAsync("soulseek", "track-id", false, It.IsAny<string?>()));
        if (throws) calls.ThrowsAsync(new HttpRequestException("down")).ReturnsAsync(true);
        else calls.ReturnsAsync(false).ReturnsAsync(true);
        var coordinator = Coordinator(PlaySettings(false, true), NewQueue(), lidarr.Object);

        for (var i = 0; i < 3; i++) coordinator.QueuePlay("soulseek", "track-id");

        lidarr.Verify(x => x.TryAcquireTrackAsync("soulseek", "track-id", false, It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task OnlyOnePlayedSongWaitsAndASkippedOneCanBeAskedForAgain()
    {
        var queue = NewQueue();
        var coordinator = Coordinator(PlaySettings(true, false), queue);
        coordinator.QueuePlay("soulseek", "first");
        coordinator.QueuePlay("soulseek", "second");
        Assert.Equal(1, queue.WaitingPlays);
        Assert.Equal("first", (await NextQueued(queue))!.ExternalId);

        coordinator.QueuePlay("soulseek", "second");
        Assert.Equal("second", (await NextQueued(queue))!.ExternalId);
    }

    [Fact]
    public async Task AHeartGoesBeforeAWaitingPlayAndUpgradesOneItJoins()
    {
        var queue = NewQueue();
        Coordinator(PlaySettings(true, false), queue).QueuePlay("soulseek", "played");
        _ = queue.Enqueue("soulseek", "hearted", isStar: true, triggerAlbumDownload: false, forcePermanent: true);
        Assert.Equal("hearted", (await NextQueued(queue))!.ExternalId);

        _ = queue.Enqueue("soulseek", "played", isStar: true, triggerAlbumDownload: false,
            forcePermanent: true, notifyOnFailure: true, requestedBy: "felix");
        var joined = (await NextQueued(queue))!;
        Assert.True(joined.HeartJoined);
        Assert.True(joined.NotifiesOnFailure);
        Assert.Contains("felix", joined.RequestedBy);
    }

    [Fact]
    public async Task APlayedDownloadHasARowThatFailsWithIt()
    {
        var queue = NewQueue();
        var tracker = new AcquisitionTracker(NullLogger<AcquisitionTracker>.Instance);
        var coordinator = Coordinator(PlaySettings(true, false), queue, tracker: tracker);
        coordinator.QueuePlay("soulseek", "track-id", clientId: "ext-1", owner: "felix");
        coordinator.QueuePlay("soulseek", "skipped", clientId: "ext-2", owner: "felix");
        Assert.Equal(AcquisitionState.Queued, Assert.Single(tracker.ForUser("felix")).State);

        var request = (await NextQueued(queue))!;
        queue.Release(request);
        request.Completion.TrySetException(new Exception("no peers"));
        for (var i = 0; i < 200 && tracker.ForUser("felix")[0].State != AcquisitionState.Failed; i++)
            await Task.Delay(10);
        Assert.Equal(AcquisitionState.Failed, tracker.ForUser("felix")[0].State);
    }

    [Fact]
    public async Task ADroppedPlayNeverStrandsAHeart()
    {
        // The heart for B arrives at the worst moment: while the play for B is being dropped.
        TrackAcquisitionQueue queue = null!;
        Task<string>? heart = null;
        queue = new TrackAcquisitionQueue(new OnSkippedPlay(() =>
            heart = queue.Enqueue("soulseek", "B", isStar: true, triggerAlbumDownload: false, forcePermanent: true)));
        Assert.NotNull(queue.TryEnqueuePlay("soulseek", "A", DownloadSource.YouTube, null));

        Assert.Null(queue.TryEnqueuePlay("soulseek", "B", DownloadSource.YouTube, null));
        Assert.NotNull(heart);

        // The heart has its own request, queued and taken first, not a released play's.
        var next = (await NextQueued(queue))!;
        if (next.ExternalId == "B")
        {
            queue.Release(next);
            next.Completion.TrySetResult("/music/b.flac");
        }
        Assert.Equal("/music/b.flac", await heart.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(next.IsStar);
    }

    /// <summary>Runs <paramref name="onSkip"/> when the queue logs that it skipped a play.</summary>
    private sealed class OnSkippedPlay(Action onSkip) : ILogger<TrackAcquisitionQueue>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Skipped play acquisition", StringComparison.Ordinal)) onSkip();
        }
    }
}
