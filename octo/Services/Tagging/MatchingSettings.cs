using Octo.Models.Settings;

namespace Octo.Services.Tagging;

/// <summary>The settings the chooser reads, snapshotted once per download.</summary>
public sealed record MatchingSettings(
    bool PreferOriginalAlbum, bool YearFromOriginalRelease, IReadOnlyList<string> PreferredCountries,
    double FingerprintThreshold, bool TagFromMatch)
{
    public static readonly MatchingSettings Default = new(true, true, [], 0.85, false);

    public static MatchingSettings From(MetadataSettings metadata, SoulseekSettings soulseek) => new(
        metadata.PreferOriginalAlbum, metadata.YearFromOriginalRelease, metadata.EffectivePreferredCountries,
        soulseek.EffectiveMinScoreFraction, soulseek.TagFromMusicBrainz || soulseek.NameFromMatch);
}
