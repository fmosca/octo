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
/// Better quality looks for a lossless copy on Soulseek only, and searches the slow, wide way (#70).
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
            It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>(), true)).ReturnsAsync("/music/a.flac");
        var worker = new AcquisitionWorker(queue, downloads.Object, new ExternalIdRegistry(),
            new NotificationService([], TestOptions.Monitor(new NotificationSettings()), NullLogger<NotificationService>.Instance),
            NullLogger<AcquisitionWorker>.Instance);
        await worker.StartAsync(default);
        var path = await queue.Enqueue("soulseek", "id-1", isStar: true, triggerAlbumDownload: false, forcePermanent: true,
            sourceOverride: DownloadSource.Soulseek, upgradeSearch: true);
        await worker.StopAsync(default);
        Assert.Equal("/music/a.flac", path);
    }
}
