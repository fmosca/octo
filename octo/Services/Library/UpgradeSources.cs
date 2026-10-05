using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Soulseek;

namespace Octo.Services.Library;

/// <summary>
/// Where Better quality looks for a lossless copy, decided in one place so the dashboard, the
/// apps, the queue and the weekly worker never disagree. Only lossless sources count: YouTube can
/// only produce an MP3, which a Better quality check refuses anyway.
/// </summary>
public sealed class UpgradeSources(
    IOptionsMonitor<LibraryActionSettings> actions,
    IOptionsMonitor<SoulseekSettings> soulseek,
    IOptionsMonitor<LidarrSettings> lidarr,
    ISoulseekLink? soulseekLink = null)
{
    /// <summary>slskd's address and sign-in are set.</summary>
    public static bool SoulseekSetUp(SoulseekSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.BaseUrl)
        && !string.IsNullOrWhiteSpace(settings.Username) && !string.IsNullOrWhiteSpace(settings.Password);

    /// <summary>Lidarr's address, key and the choices it needs to add an album are set.</summary>
    public static bool LidarrSetUp(LidarrSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.BaseUrl) && !string.IsNullOrWhiteSpace(settings.ApiKey)
        && !string.IsNullOrWhiteSpace(settings.RootFolderPath)
        && settings.QualityProfileId > 0 && settings.MetadataProfileId > 0;

    /// <summary>The sources the setting allows, in the order they are tried, set up or not.</summary>
    public IReadOnlyList<DownloadSource> Wanted() => actions.CurrentValue.UpgradeSource switch
    {
        UpgradeSourceChoice.Soulseek => [DownloadSource.Soulseek],
        UpgradeSourceChoice.Lidarr => [DownloadSource.Lidarr],
        _ => [DownloadSource.Soulseek, DownloadSource.Lidarr],
    };

    /// <summary>The allowed sources that are set up, in the order they are tried.</summary>
    public IReadOnlyList<DownloadSource> Plan() => Wanted().Where(SetUp).ToList();

    /// <summary>Whether any allowed source is set up, so Better quality can be offered at all.</summary>
    public bool Ready => Plan().Count > 0;

    /// <summary>
    /// The sources in words, for "Looking for a higher quality copy on X": the ones that are set up,
    /// or, when none is, the ones the setting asks for.
    /// </summary>
    public string Name
    {
        get
        {
            var plan = Plan();
            return Words(plan.Count > 0 ? plan : Wanted());
        }
    }

    /// <summary>
    /// The sources that can take a search right now. Soulseek drops out while slskd is not logged
    /// in; empty means every source is out, and the request should wait rather than fail.
    /// </summary>
    public async Task<IReadOnlyList<DownloadSource>> AvailableAsync(CancellationToken ct = default)
    {
        var plan = Plan();
        if (!plan.Contains(DownloadSource.Soulseek) || soulseekLink is null) return plan;
        var link = (await soulseekLink.ReadAsync(fresh: false, ct))?.Link;
        return link == SoulseekLinkState.NotLoggedIn ? plan.Where(s => s != DownloadSource.Soulseek).ToList() : plan;
    }

    /// <summary>Whether the only thing between this request and a search is a Soulseek outage.</summary>
    public async Task<bool> WaitingForSoulseekAsync(CancellationToken ct = default) =>
        Plan().Contains(DownloadSource.Soulseek) && (await AvailableAsync(ct)).Count == 0;

    public static string Words(IEnumerable<DownloadSource> sources) =>
        string.Join(" or ", sources.Select(Word).Distinct());

    public static string Word(DownloadSource source) => source switch
    {
        DownloadSource.Lidarr => "Lidarr",
        DownloadSource.YouTube => "YouTube",
        _ => "Soulseek",
    };

    private bool SetUp(DownloadSource source) => source switch
    {
        DownloadSource.Soulseek => SoulseekSetUp(soulseek.CurrentValue),
        DownloadSource.Lidarr => LidarrSetUp(lidarr.CurrentValue),
        _ => false,
    };
}
