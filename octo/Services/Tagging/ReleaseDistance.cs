using Octo.Services.Common;
using Octo.Services.Fingerprint;

namespace Octo.Services.Tagging;

/// <summary>
/// How far one candidate is from what was asked for and what landed, as a number from 0 (the
/// same) to 1 (nothing agrees). Each key adds a penalty times its weight; the result is the
/// weighted sum over the weights of the keys that could be judged, so a question with no
/// answer on either side neither helps nor hurts.
/// </summary>
public sealed class Distance
{
    private readonly List<(string Key, double Penalty, double Weight)> _parts = [];

    public void Add(string key, double penalty, double weight) =>
        _parts.Add((key, Math.Clamp(penalty, 0, 1), weight));

    public double Value => _parts.Count == 0 ? 1 : _parts.Sum(p => p.Penalty * p.Weight) / _parts.Sum(p => p.Weight);

    public IReadOnlyList<(string Key, double Penalty, double Weight)> Breakdown => _parts;

    /// <summary>The penalty one key added, or null when that key could not be judged.</summary>
    public double? PenaltyOf(string key) =>
        _parts.Where(p => p.Key == key).Select(p => (double?)p.Penalty).FirstOrDefault();
}

/// <summary>
/// The keys a candidate is judged on, their weights, and each key's penalty function. The
/// weights follow the taggers that solved this before Octo where the key exists there; the
/// identifiers (fingerprint, code, barcode) weigh most because they name a recording outright.
/// </summary>
public static class ReleaseDistance
{
    internal const double TitleWeight = 3, ArtistWeight = 2, LengthWeight = 2, AlbumWeight = 3,
        FileAlbumWeight = 1, FileAlbumWeightWhenKept = 3, TrackWeight = 1, FingerprintWeight = 5,
        IsrcWeight = 5, BarcodeWeight = 5, TypeWeight = 2, OriginalWeight = 1, StatusWeight = 0.5,
        SourceWeight = 2, YearWeight = 1, CountryWeight = 0.5;

    internal const int LengthGraceSeconds = 5, LengthMaxSeconds = 30;

    /// <summary>How many years of later first release count as "a whole generation later".</summary>
    internal const double OriginalSpanYears = 25;

    /// <summary>The shortest title key allowed to match as a prefix or substring of a longer one.</summary>
    private const int MinPrefixCore = 6;

    public static ScoredCandidate Measure(TagEvidence evidence, ReleaseCandidate candidate,
        MatchingSettings settings, int? earliestGroupYearForRecording, int? thisYear = null)
    {
        var request = evidence.Request;
        var file = evidence.File;
        var distance = new Distance();

        distance.Add("title", TitlePenalty(request.Title, candidate.RecordingTitle, request.VersionMarkers, candidate.SecondaryTypes), TitleWeight);
        distance.Add("artist", ArtistPenalty(request.Artist, candidate.ArtistCredit, candidate.Artists), ArtistWeight);

        if (file.DurationSeconds > 0 && candidate.LengthSeconds is > 0)
            distance.Add("length", LengthPenalty(file.DurationSeconds, candidate.LengthSeconds.Value), LengthWeight);

        var candidateAlbum = candidate.AlbumTitle;
        if (request.OwnsAlbum && !string.IsNullOrWhiteSpace(candidateAlbum))
            distance.Add("album", AlbumPenalty(request.Album!, candidateAlbum), AlbumWeight);
        else if (!request.OwnsAlbum && file.TagsAreEvidence && !string.IsNullOrWhiteSpace(file.Album)
                 && !string.IsNullOrWhiteSpace(candidateAlbum))
            distance.Add("file_album", AlbumPenalty(file.Album, candidateAlbum),
                settings.PreferOriginalAlbum ? FileAlbumWeight : FileAlbumWeightWhenKept);

        if (request.Track is > 0 && candidate.TrackNumber is > 0)
            distance.Add("track", request.Track == candidate.TrackNumber ? 0 : 1, TrackWeight);

        if (evidence.FingerprintedRecordingIds.Count > 0)
            distance.Add("fingerprint",
                candidate.RecordingId is { Length: > 0 } id && evidence.FingerprintedRecordingIds.Contains(id) ? 0 : 1,
                FingerprintWeight);

        var knownIsrcs = SongIdentity.Isrcs(file.Isrcs.Append(request.Isrc));
        if (knownIsrcs.Count > 0 && candidate.Isrcs.Count > 0)
            distance.Add("isrc", SongIdentity.SharesIsrc(knownIsrcs, candidate.Isrcs) ? 0 : 0.5, IsrcWeight);

        if (file.TagsAreEvidence && BarcodePenalty(file.Barcode, candidate.Barcode) is { } barcode)
            distance.Add("barcode", barcode, BarcodeWeight);

        if (!request.OwnsAlbum)
        {
            distance.Add("type", TypePenalty(candidate.PrimaryType, candidate.SecondaryTypes, request.VersionMarkers), TypeWeight);
            if (candidate.OriginalYear is { } groupYear && earliestGroupYearForRecording is { } earliest)
                distance.Add("original", OriginalPenalty(groupYear, earliest), OriginalWeight);
        }

        if (!string.IsNullOrWhiteSpace(candidate.Status))
            distance.Add("status", StatusPenalty(candidate.Status), StatusWeight);

        distance.Add("source", SourcePenalty(candidate.Source), SourceWeight);

        if (file.TagsAreEvidence && file.Year is > 0 && candidate.Year is > 0)
            distance.Add("year", YearPenalty(file.Year.Value, candidate.Year, candidate.OriginalYear, thisYear ?? DateTime.UtcNow.Year), YearWeight);

        if (settings.PreferredCountries.Count > 0 && !string.IsNullOrWhiteSpace(candidate.Country))
            distance.Add("country", CountryPenalty(candidate.Country, settings.PreferredCountries), CountryWeight);

        return new ScoredCandidate(candidate, distance.Value, distance.Breakdown);
    }

    /// <summary>
    /// The same title is 0. A title that reads the same once stylised characters are letters, or
    /// agrees by the looser readings, is 0.1; one that holds the other is 0.3; another title is 1.
    /// A version the request never asked for (a remix, a live take) is 1 whatever the core says. A
    /// version the request asked for and the candidate's title lacks is 0.8, unless the release's
    /// kind carries it (a live album, a remix release): the music database writes "live" in a
    /// recording's disambiguation and the release's kind, not always in its title.
    /// </summary>
    internal static double TitlePenalty(string requested, string candidate, IReadOnlySet<string> requestMarkers,
        IReadOnlyList<string> secondaryTypes)
    {
        var want = SongIdentity.ParseTitle(requested);
        var got = SongIdentity.ParseTitle(candidate);
        if (want.Key.Length == 0 || got.Key.Length == 0) return 1;

        double core;
        if (want.Key == got.Key) core = 0;
        else if (want.LooseKey == got.LooseKey) core = 0.1;
        else if (SongIdentity.SameTitle(requested, candidate).Verdict != SongVerdict.Different) core = 0.1;
        else if (Contains(want.Key, got.Key) || Contains(LettersOf(want.Key), LettersOf(got.Key))) core = 0.3;
        else return 1;

        var wanted = SongIdentity.DistinctVersions(want);
        var offered = SongIdentity.DistinctVersions(got);
        if (offered.Any(version => !wanted.Contains(version))) return 1;

        var missing = wanted.Where(version => !offered.Contains(version)).ToList();
        if (missing.Count == 0) return core;
        return missing.All(version => KindCarries(version, secondaryTypes)) ? core : Math.Max(core, 0.8);
    }

    /// <summary>Whether a release's kind says what a title marker says: a live album for "live", a
    /// remix release for "remix".</summary>
    private static bool KindCarries(string marker, IReadOnlyList<string> secondaryTypes) => marker switch
    {
        "live" or "unplugged" => Has(secondaryTypes, "Live"),
        "remix" or "mix" or "dub" or "edit" or "vip" or "rework" => Has(secondaryTypes, "Remix"),
        "demo" => Has(secondaryTypes, "Demo"),
        _ => false,
    };

    private static bool Has(IReadOnlyList<string> types, string type) =>
        types.Any(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= MinPrefixCore && longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static string LettersOf(string key) => new(key.Where(char.IsLetter).ToArray());

    /// <summary>The same artist is 0, the same once stylised characters are letters 0.1, one side
    /// unknown 0.5, a different guest list 1. A credit that only holds the other's name is 0.2.</summary>
    internal static double ArtistPenalty(string requested, string credit, IReadOnlyList<string> credits)
    {
        switch (SongIdentity.CompareArtists(requested, credit, credits.Count > 0 ? credits : null))
        {
            case ArtistAgreement.Agree: return 0;
            case ArtistAgreement.Loose: return 0.1;
            case ArtistAgreement.Unknown: return 0.5;
            case ArtistAgreement.Conflict: return 1;
        }
        return TrackMatchComparer.ArtistMatches(requested, credit, credits) ? 0.2 : 1;
    }

    /// <summary>Within five seconds is 0; thirty seconds past that is 1.</summary>
    internal static double LengthPenalty(int file, int candidate) =>
        Math.Clamp(Math.Abs(file - candidate) - LengthGraceSeconds, 0, LengthMaxSeconds) / (double)LengthMaxSeconds;

    /// <summary>Album titles by the same reading as song titles, except that an edition the
    /// request never named ("Deluxe", "Remastered", "Anniversary") costs 0.3 rather than nothing.</summary>
    internal static double AlbumPenalty(string requested, string candidate)
    {
        var want = SongIdentity.ParseTitle(requested);
        var got = SongIdentity.ParseTitle(candidate);
        if (want.Key.Length == 0 || got.Key.Length == 0) return 1;

        double core;
        if (want.Key == got.Key) core = 0;
        else if (want.LooseKey == got.LooseKey) core = 0.1;
        else if (Contains(want.Key, got.Key)) core = 0.3;
        else return 1;

        return HasEdition(candidate) && !HasEdition(requested) ? Math.Max(core, 0.3) : core;
    }

    private static bool HasEdition(string title) =>
        title.Contains('(') || title.Contains('[') || title.Contains(" - ", StringComparison.Ordinal);

    /// <summary>
    /// What kind of release a song without a requested album should be filed under. A plain
    /// request wants the studio album; a single or an EP is close; a compilation, a live album or
    /// a remix release is the wrong place. A request that asked for a live take wants a live
    /// release; one that asked for a remix wants a remix release or a single.
    /// </summary>
    internal static double TypePenalty(string? primary, IReadOnlyList<string> secondary, IReadOnlySet<string> requestMarkers)
    {
        var live = requestMarkers.Contains("live") || requestMarkers.Contains("unplugged");
        var remix = requestMarkers.Overlaps(["remix", "mix", "dub", "vip", "rework", "edit"]);
        var isLive = Has(secondary, "Live");
        var isRemix = Has(secondary, "Remix");
        var kind = primary?.Trim().ToLowerInvariant();

        if (live) return isLive ? 0 : 0.6;
        if (remix) return isRemix ? 0 : kind == "single" ? 0.2 : 0.5;

        if (Has(secondary, "Compilation") || isLive || isRemix) return 1;
        if (Has(secondary, "DJ-mix") || Has(secondary, "Mixtape/Street")) return 0.8;
        if (Has(secondary, "Soundtrack")) return 0.5;
        return kind switch
        {
            "album" => 0,
            "single" or "ep" => 0.2,
            null or "" => 0.5,
            _ => 0.5,
        };
    }

    /// <summary>A release group first issued long after the recording's first release is a reissue,
    /// a compilation or a later pressing: a generation later is the whole penalty.</summary>
    internal static double OriginalPenalty(int groupFirstYear, int earliestGroupFirstYear) =>
        Math.Clamp((groupFirstYear - earliestGroupFirstYear) / OriginalSpanYears, 0, 1);

    internal static double StatusPenalty(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "official" => 0,
        "promotion" => 0.5,
        "bootleg" or "pseudo-release" or "withdrawn" or "cancelled" => 1,
        _ => 0.25,
    };

    /// <summary>What each source is worth before anything is compared.</summary>
    internal static double SourcePenalty(TagSource source) => source switch
    {
        TagSource.Fingerprint => 0,
        TagSource.Database => 0.1,
        TagSource.Catalog => 0.25,
        TagSource.FileTags => 0.5,
        _ => 1,
    };

    /// <summary>The known year against the candidate's: the same, or the same as the recording's
    /// first release, is 0. Otherwise the gap as a share of the years the candidate has existed.</summary>
    internal static double YearPenalty(int known, int? candidateYear, int? originalYear, int thisYear)
    {
        if (candidateYear is not { } year) return 0;
        if (known == year || (originalYear is { } original && known == original)) return 0;
        var span = Math.Max(1, Math.Abs(thisYear - year));
        return Math.Clamp(Math.Abs(known - year) / (double)span, 0, 1);
    }

    /// <summary>The first preferred country is free, each one down the list costs a share, and a
    /// country not on the list costs everything.</summary>
    internal static double CountryPenalty(string? country, IReadOnlyList<string> preferred)
    {
        if (preferred.Count == 0 || string.IsNullOrWhiteSpace(country)) return 0;
        for (var i = 0; i < preferred.Count; i++)
            if (string.Equals(preferred[i], country.Trim(), StringComparison.OrdinalIgnoreCase))
                return i / (double)preferred.Count;
        return 1;
    }

    /// <summary>The same barcode in its 12 or 13 digit form is 0, another one is 1, and a side that
    /// is not a barcode at all cannot be judged.</summary>
    internal static double? BarcodePenalty(string? file, string? candidate)
    {
        var mine = Octo.Services.CoverArt.ITunesCoverArtLookup.BarcodeForms(file).ToHashSet(StringComparer.Ordinal);
        var theirs = Octo.Services.CoverArt.ITunesCoverArtLookup.BarcodeForms(candidate).ToList();
        if (mine.Count == 0 || theirs.Count == 0) return null;
        return theirs.Any(mine.Contains) ? 0 : 1;
    }
}
