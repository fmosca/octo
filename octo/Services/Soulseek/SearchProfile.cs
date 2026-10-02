using Octo.Models.Settings;

namespace Octo.Services.Soulseek;

/// <summary>
/// How long and how wide one Soulseek search looks. slskd ends a search at whichever comes first:
/// SearchTimeoutMs with no new answer, ResponseLimit peers, or FileLimit files. The file limit
/// counts every format. Octo asked for 150 files, so a popular song's first wave of MP3s ended
/// the search before the FLAC answers arrived (#70). CeilingSeconds is Octo's own limit: a search
/// still running then is cancelled, and slskd still hands over what it gathered.
/// </summary>
public sealed record SearchProfile(string Name, int CeilingSeconds, int SearchTimeoutMs, int ResponseLimit, int FileLimit)
{
    /// <summary>A star or a play. Quality beats speed, but somebody may be waiting, so the
    /// configured ceiling holds. 500 files lets a popular song run past its MP3s; it usually
    /// ends at the ceiling now instead of after 5 to 20 s.</summary>
    public static SearchProfile Interactive(SoulseekSettings s) =>
        new("interactive", s.SearchWaitSeconds, 15_000, 250, 500);

    /// <summary>Better quality and the weekly upgrade. Nobody is waiting, and the quick search
    /// is the one that already came back without a lossless copy.</summary>
    public static SearchProfile Upgrade(SoulseekSettings s) =>
        new("upgrade", s.EffectiveUpgradeSearchWaitSeconds, 30_000, 500, 2_000);
}
