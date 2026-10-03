using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Library;
using Octo.Services.Notifications;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// Better quality looks for a lossless copy on Soulseek only, and searches the slow, wide way, and
/// the weekly upgrade trickles lossy songs through it while nothing else is downloading (#70).
/// </summary>
public class QualityUpgradeTests
{
    [Fact]
    public void BetterQualityIsSoulseekOnlyAndSearchesTheSlowWay()
    {
        Assert.Equal((DownloadSource.Soulseek, true), LibraryActionExecutor.ReplacementPlan(LibraryAction.BetterQuality));
        Assert.Equal(((DownloadSource?)null, false), LibraryActionExecutor.ReplacementPlan(LibraryAction.WrongSong));
    }

    [Fact]
    public async Task TheWorkerHandsTheSourceAndUpgradeSearchToTheDownload()
    {
        var queue = new TrackAcquisitionQueue(NullLogger<TrackAcquisitionQueue>.Instance);
        var downloads = new Mock<IDownloadService>();
        downloads.Setup(d => d.ExecuteAcquisitionAsync("soulseek", "id-1", false, true, DownloadSource.Soulseek,
            It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>(), true, It.IsAny<ReplacementHandoff?>())).ReturnsAsync("/music/a.flac");
        var worker = new AcquisitionWorker(queue, downloads.Object, new ExternalIdRegistry(),
            new NotificationService([], TestOptions.Monitor(new NotificationSettings()), NullLogger<NotificationService>.Instance),
            NullLogger<AcquisitionWorker>.Instance);
        await worker.StartAsync(default);
        var path = await queue.Enqueue("soulseek", "id-1", isStar: true, triggerAlbumDownload: false, forcePermanent: true,
            sourceOverride: DownloadSource.Soulseek, upgradeSearch: true);
        await worker.StopAsync(default);
        Assert.Equal("/music/a.flac", path);
    }

    static LibrarySongRow Row(string id, string path, string suffix = "mp3", long size = 5_000_000) =>
        new(id, path, "/music", size, suffix, 320, "T", "A", 200);

    [Fact]
    public void NeverTriedFirstThenOldestAndLosslessIsIgnored()
    {
        var now = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
        var state = new QualityUpgradeState();
        state.Attempts[QualityUpgradeWorker.KeyOf(Row("b", "B/b.mp3"))] = new(now.AddDays(-60), "Failed", null);
        state.Attempts[QualityUpgradeWorker.KeyOf(Row("c", "C/c.mp3"))] = new(now.AddDays(-40), "Failed", null);
        state.Attempts[QualityUpgradeWorker.KeyOf(Row("e", "E/e.mp3"))] = new(now.AddDays(-2), "Failed", null);
        var songs = new[] { Row("f", "F/f.flac", "flac"), Row("c", "C/c.mp3"), Row("b", "B/b.mp3"), Row("e", "E/e.mp3"), Row("a", "A/a.mp3") };
        Assert.Equal("a", QualityUpgradeWorker.Pick(songs, state, now, dryRun: false)!.Id);
        state.Attempts[QualityUpgradeWorker.KeyOf(Row("a", "A/a.mp3"))] = new(now, "Failed", null);
        Assert.Equal("b", QualityUpgradeWorker.Pick(songs, state, now, false)!.Id);
    }

    [Fact]
    public void TheKeySurvivesANavidromeIdChange()
    {
        Assert.Equal(QualityUpgradeWorker.KeyOf(Row("old-id", "A/x.mp3")), QualityUpgradeWorker.KeyOf(Row("new-id", "A/x.mp3")));
        Assert.Equal(QualityUpgradeWorker.KeyOf(Row("1", "A/x.mp3")), QualityUpgradeWorker.KeyOf(Row("2", "/music/A/x.mp3")));
        var now = DateTime.UtcNow;
        var state = new QualityUpgradeState();
        state.Attempts[QualityUpgradeWorker.KeyOf(Row("old-id", "A/x.mp3"))] = new(now.AddDays(-30), "Failed", null);
        Assert.Equal("fresh", QualityUpgradeWorker.Pick([Row("new-id", "A/x.mp3"), Row("fresh", "Z/z.mp3")], state, now, false)!.Id);
    }

    private sealed class Calls { public int List; public int Apply; }

    private static (QualityUpgradeWorker Worker, Calls Calls) Worker(int perWeek = 7, bool idle = true,
        QualityUpgradeStore? store = null)
    {
        var settings = new LibraryActionSettings
        {
            Enabled = true,
            AllowedUsers = ["alice"],
            Actions = [new LibraryActionDefinition { Action = LibraryAction.BetterQuality, Enabled = true }],
            DryRun = false,
            UpgradePerWeek = perWeek,
        };
        var calls = new Calls();
        var worker = new QualityUpgradeWorker(store ?? new QualityUpgradeStore(), null!, null!, null!, null!,
            TestOptions.Monitor(settings), NullLogger<QualityUpgradeWorker>.Instance)
        {
            ListSongs = _ =>
            {
                calls.List++;
                return Task.FromResult<(IReadOnlyList<LibrarySongRow>, bool)>(([Row("a", "A/a.mp3")], true));
            },
            Apply = (_, _) =>
            {
                calls.Apply++;
                return Task.FromResult(new LibraryActionOutcome(LibraryActionState.Failed, "nothing better"));
            },
            HasAdminIdentity = () => true,
            AcquisitionsIdle = () => idle,
        };
        return (worker, calls);
    }

    [Fact]
    public async Task ZeroIsOff()
    {
        Assert.Null(QualityUpgradeWorker.Interval(0));
        var (w, calls) = Worker(perWeek: 0);
        Assert.Equal(QualityUpgradeWorker.Tick.Off, await w.TickAsync(default));
        Assert.Equal(0, calls.List);
    }

    [Fact]
    public async Task ItWaitsWhileAHeartIsQueuedOrRunning()
    {
        var (w, calls) = Worker(idle: false);
        Assert.Equal(QualityUpgradeWorker.Tick.Busy, await w.TickAsync(default));
        Assert.Equal(0, calls.Apply);
    }

    [Fact]
    public async Task DuringASoulseekOutage_NoSongIsTriedAndTheRunIsNotSpent()
    {
        var store = new QualityUpgradeStore();
        var (w, calls) = Worker(store: store);
        w.SoulseekOffline = _ => Task.FromResult(true);

        Assert.Equal(QualityUpgradeWorker.Tick.Offline, await w.TickAsync(default));
        Assert.Equal(0, calls.Apply);
        Assert.Null(store.Snapshot().LastRunUtc);

        // Back online, the same tick runs as normal.
        w.SoulseekOffline = _ => Task.FromResult(false);
        Assert.Equal(QualityUpgradeWorker.Tick.Ran, await w.TickAsync(default));
        Assert.Equal(1, calls.Apply);
    }

    [Fact]
    public async Task WhenNavidromeCannotListTheLibrary_TheWeeksRunIsNotSpent()
    {
        var store = new QualityUpgradeStore();
        var (w, calls) = Worker(store: store);
        var answering = false;
        w.ListSongs = _ =>
        {
            calls.List++;
            return Task.FromResult<(IReadOnlyList<LibrarySongRow>, bool)>(
                answering ? ([Row("a", "A/a.mp3")], true) : ([], false));
        };

        Assert.Equal(QualityUpgradeWorker.Tick.Unreachable, await w.TickAsync(default));
        Assert.Null(store.Snapshot().LastRunUtc);
        Assert.Null(store.Snapshot().LastOutcome);

        answering = true;
        Assert.Equal(QualityUpgradeWorker.Tick.Ran, await w.TickAsync(default));
        Assert.Equal(1, calls.Apply);
    }

    [Fact]
    public async Task StateSurvivesARestartAndTheNextRunWaitsItsTurn()
    {
        var path = Path.Combine(Path.GetTempPath(), "octo-qu-" + Guid.NewGuid() + ".json");
        try
        {
            var (first, _) = Worker(store: new QualityUpgradeStore(path));
            Assert.Equal(QualityUpgradeWorker.Tick.Ran, await first.TickAsync(default));
            var reopened = new QualityUpgradeStore(path);
            Assert.Single(reopened.Snapshot().Attempts);
            var (second, calls) = Worker(store: reopened);
            Assert.Equal(QualityUpgradeWorker.Tick.NotDue, await second.TickAsync(default));
            Assert.Equal(0, calls.Apply);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AQueuedStar_CountsAsADownloadInFlight()
    {
        var queue = new TrackAcquisitionQueue(NullLogger<TrackAcquisitionQueue>.Instance);
        var activity = new AcquisitionActivity(new ServiceCollection().AddSingleton(queue).BuildServiceProvider());
        Assert.False(activity.IsBusy);
        _ = queue.Enqueue("deezer", "1", isStar: true, triggerAlbumDownload: false, forcePermanent: false);
        Assert.True(activity.IsBusy);
    }

    [Fact]
    public void ARunningTransfer_CountsAsADownloadInFlight()
    {
        var downloads = new Mock<IDownloadService>();
        downloads.SetupGet(d => d.HasActiveDownloads).Returns(true);
        var activity = new AcquisitionActivity(new ServiceCollection()
            .AddSingleton(new TrackAcquisitionQueue(NullLogger<TrackAcquisitionQueue>.Instance))
            .AddSingleton(downloads.Object).BuildServiceProvider());
        Assert.True(activity.IsBusy);
    }
}
