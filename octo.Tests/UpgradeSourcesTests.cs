using Octo.Models.Settings;
using Octo.Services.Library;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// Where Better quality looks for a lossless copy: Soulseek, Lidarr, or both in that order. One
/// place decides it, so the dashboard, the apps and the workers never disagree. Only sources that
/// are set up count, and a Soulseek outage leaves Lidarr rather than stopping everything.
/// </summary>
public class UpgradeSourcesTests
{
    private static readonly SoulseekSettings Slskd = new() { BaseUrl = "http://slskd:5030", Username = "u", Password = "p" };
    private static readonly LidarrSettings Lidarr = new()
    {
        BaseUrl = "http://lidarr:8686", ApiKey = "k", RootFolderPath = "/music", QualityProfileId = 1, MetadataProfileId = 1,
    };

    private static UpgradeSources Sources(UpgradeSourceChoice choice, SoulseekSettings? soulseek, LidarrSettings? lidarr,
        SoulseekLinkState link = SoulseekLinkState.LoggedIn) =>
        new(TestOptions.Monitor(new LibraryActionSettings { UpgradeSource = choice }),
            TestOptions.Monitor(soulseek ?? new SoulseekSettings()), TestOptions.Monitor(lidarr ?? new LidarrSettings()),
            new FixedLink(link));

    [Fact]
    public void AutoTriesSoulseekThenLidarr()
    {
        var sources = Sources(UpgradeSourceChoice.Auto, Slskd, Lidarr);

        Assert.Equal([DownloadSource.Soulseek, DownloadSource.Lidarr], sources.Plan());
        Assert.True(sources.Ready);
        Assert.Equal("Soulseek or Lidarr", sources.Name);
    }

    [Fact]
    public void AutoUsesOnlyWhatIsSetUp()
    {
        Assert.Equal([DownloadSource.Lidarr], Sources(UpgradeSourceChoice.Auto, null, Lidarr).Plan());
        Assert.Equal("Lidarr", Sources(UpgradeSourceChoice.Auto, null, Lidarr).Name);
        Assert.Equal([DownloadSource.Soulseek], Sources(UpgradeSourceChoice.Auto, Slskd, null).Plan());
    }

    [Fact]
    public void AChosenSourceIsTheOnlyOne()
    {
        Assert.Equal([DownloadSource.Lidarr], Sources(UpgradeSourceChoice.Lidarr, Slskd, Lidarr).Plan());
        Assert.Equal([DownloadSource.Soulseek], Sources(UpgradeSourceChoice.Soulseek, Slskd, Lidarr).Plan());
    }

    [Fact]
    public void NothingSetUpIsNotReadyAndNamesWhatItWants()
    {
        var sources = Sources(UpgradeSourceChoice.Lidarr, Slskd, null);

        Assert.Empty(sources.Plan());
        Assert.False(sources.Ready);
        Assert.Equal("Lidarr", sources.Name);
    }

    [Fact]
    public void LidarrNeedsItsRootFolderAndProfilesToAddAnAlbum()
    {
        var half = new LidarrSettings { BaseUrl = "http://lidarr:8686", ApiKey = "k" };

        Assert.False(UpgradeSources.LidarrSetUp(half));
        Assert.True(UpgradeSources.LidarrSetUp(Lidarr));
    }

    [Fact]
    public async Task ASoulseekOutageLeavesLidarr()
    {
        var sources = Sources(UpgradeSourceChoice.Auto, Slskd, Lidarr, SoulseekLinkState.NotLoggedIn);

        Assert.Equal([DownloadSource.Lidarr], await sources.AvailableAsync());
        Assert.False(await sources.WaitingForSoulseekAsync());
    }

    [Fact]
    public async Task WithOnlySoulseekAnOutageMeansWait()
    {
        var sources = Sources(UpgradeSourceChoice.Auto, Slskd, null, SoulseekLinkState.NotLoggedIn);

        Assert.Empty(await sources.AvailableAsync());
        Assert.True(await sources.WaitingForSoulseekAsync());
    }

    [Fact]
    public async Task LidarrAloneNeverWaitsForSoulseek()
    {
        var sources = Sources(UpgradeSourceChoice.Lidarr, Slskd, Lidarr, SoulseekLinkState.NotLoggedIn);

        Assert.Equal([DownloadSource.Lidarr], await sources.AvailableAsync());
        Assert.False(await sources.WaitingForSoulseekAsync());
    }

    private sealed class FixedLink(SoulseekLinkState state) : ISoulseekLink
    {
        public Task<SoulseekServerReading?> ReadAsync(bool fresh, CancellationToken ct) =>
            Task.FromResult<SoulseekServerReading?>(new SoulseekServerReading(state, null, null, null));
        public TimeSpan HoldLimit => TimeSpan.Zero;
        public DateTime UtcNow => DateTime.UtcNow;
        public Task<bool> WaitForLoginAsync(DateTime until, CancellationToken ct) => Task.FromResult(true);
    }
}
