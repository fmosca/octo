namespace Octo.Services.Tagging;

/// <summary>
/// Weighs every candidate release against the evidence, ranks them, and says how sure the
/// winner is. Strong may overwrite what the file and the catalog said; Medium may when the
/// fingerprint service or the music database backs it; anything less only fills blanks. Two
/// pressings of different albums too close to call leave the recording certain and the album
/// in doubt.
/// </summary>
public static class ReleaseChooser
{
    internal const double StrongThreshold = 0.05;
    internal const double MediumThreshold = 0.25;
    internal const double AmbiguityMargin = 0.02;
    internal const int MaxCandidates = 200;

    public static TagPlan Choose(TagEvidence evidence, IReadOnlyList<ReleaseCandidate> candidates, MatchingSettings settings,
        int? thisYear = null)
    {
        var pool = candidates.Take(MaxCandidates).ToList();
        if (pool.Count == 0) return TagPlan.Empty(evidence, settings);

        // The earliest first release among each recording's candidates, so a later group of the
        // same recording reads as the reissue it is.
        var earliest = pool.Where(c => c.OriginalYear is not null)
            .GroupBy(c => c.RecordingId ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(c => c.OriginalYear!.Value), StringComparer.OrdinalIgnoreCase);

        var ranked = pool
            .Select(c => ReleaseDistance.Measure(evidence, c, settings,
                earliest.TryGetValue(c.RecordingId ?? "", out var year) ? year : null, thisYear))
            .OrderBy(s => s.Distance)
            .ThenBy(s => s.Candidate.GroupFirstReleaseDate ?? "9999", StringComparer.Ordinal)
            .ThenBy(s => s.Candidate.ReleaseDate ?? "9999", StringComparer.Ordinal)
            .ThenByDescending(s => s.Candidate.Sources)
            .ThenBy(s => s.Candidate.ReleaseId ?? "", StringComparer.Ordinal)
            .ToList();

        var best = ranked[0];
        var confidence = best.Distance <= StrongThreshold ? TagConfidence.Strong
            : best.Distance <= MediumThreshold ? TagConfidence.Medium
            : TagConfidence.Low;

        if (confidence != TagConfidence.Low && ranked.Count > 1 && IsAmbiguous(best, ranked[1]))
            confidence = TagConfidence.Ambiguous;

        var plan = new TagPlan { Confidence = confidence, Chosen = best, Ranked = ranked, Evidence = evidence, Settings = settings };
        if (confidence == TagConfidence.Ambiguous)
            plan.Notes.Add($"two releases are too close to call: {TagPlan.Describe(best.Candidate)} and {TagPlan.Describe(ranked[1].Candidate)}; the album was left as it was");
        return plan;
    }

    /// <summary>Two best candidates of different release groups, the same kind, within the margin.</summary>
    internal static bool IsAmbiguous(ScoredCandidate first, ScoredCandidate second)
    {
        if (second.Distance - first.Distance > AmbiguityMargin) return false;
        var a = first.Candidate.ReleaseGroupId;
        var b = second.Candidate.ReleaseGroupId;
        var differentGroup = !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            ? !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            : !string.Equals(Common.SongIdentity.Key(first.Candidate.AlbumTitle), Common.SongIdentity.Key(second.Candidate.AlbumTitle), StringComparison.Ordinal);
        if (!differentGroup) return false;
        return Nullable.Equals(first.PenaltyOf("type"), second.PenaltyOf("type"));
    }
}
