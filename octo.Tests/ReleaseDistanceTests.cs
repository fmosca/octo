using Octo.Services.Tagging;

namespace Octo.Tests;

/// <summary>
/// Each key's penalty function and the accumulator behind the release chooser. The numbers here
/// are the contract the calibration fixtures in ReleaseChooserTests are computed from.
/// </summary>
public class ReleaseDistanceTests
{
    private static readonly IReadOnlySet<string> NoMarkers = new HashSet<string>();
    private static IReadOnlySet<string> Markers(params string[] markers) => new HashSet<string>(markers);

    // ---- title ---------------------------------------------------------------------------

    [Theory]
    [InlineData("Teardrop", "Teardrop", 0)]
    [InlineData("No Surprises (Official Video)", "No Surprises", 0)]
    [InlineData("$UICIDE", "Suicide", 0.1)]
    [InlineData("Bzrp Music Sessions #56", "Rauw Alejandro: Bzrp Music Sessions, Vol. 56", 0.3)]
    [InlineData("Teardrop", "Angel", 1)]
    [InlineData("Teardrop", "Teardrop (Mad Professor mix)", 1)]
    public void TitlePenalty_PlainRequest(string requested, string candidate, double expected) =>
        Assert.Equal(expected, ReleaseDistance.TitlePenalty(requested, candidate, NoMarkers, []), 3);

    /// <summary>A live take asked for and a title that does not say so is 0.8, unless the release's
    /// kind says it is a live album, which is where the music database writes it.</summary>
    [Fact]
    public void TitlePenalty_RequestedMarkerMissing_IsCoveredByTheReleaseKind()
    {
        Assert.Equal(0.8, ReleaseDistance.TitlePenalty("Glory Box (Live)", "Glory Box", Markers("live"), []), 3);
        Assert.Equal(0, ReleaseDistance.TitlePenalty("Glory Box (Live)", "Glory Box", Markers("live"), ["Live"]), 3);
        Assert.Equal(0.8, ReleaseDistance.TitlePenalty("Song (Remix)", "Song", Markers("remix"), []), 3);
        Assert.Equal(0, ReleaseDistance.TitlePenalty("Song (Remix)", "Song", Markers("remix"), ["Remix"]), 3);
    }

    [Fact]
    public void TitlePenalty_EmptyTitle_IsTheWholePenalty() =>
        Assert.Equal(1, ReleaseDistance.TitlePenalty("Teardrop", "", NoMarkers, []), 3);

    // ---- artist --------------------------------------------------------------------------

    [Theory]
    [InlineData("Massive Attack", "Massive Attack feat. Elizabeth Fraser", 0)]
    [InlineData("Bjork", "Björk", 0)]
    [InlineData("Bizarrap, Rauw Alejandro", "Bizarrap & Rauw Alejandro", 0)]
    [InlineData("Bizarrap, Duki", "Bizarrap & Rauw Alejandro", 1)]
    [InlineData("", "Massive Attack", 0.5)]
    [InlineData("Nirvana", "Foo Fighters", 1)]
    public void ArtistPenalty_FourVerdicts(string requested, string credit, double expected) =>
        Assert.Equal(expected, ReleaseDistance.ArtistPenalty(requested, credit, []), 3);

    [Fact]
    public void ArtistPenalty_ListedCredits_AreUsedInsteadOfSplittingTheJoinedCredit() =>
        Assert.Equal(0, ReleaseDistance.ArtistPenalty("Earth, Wind & Fire", "Earth, Wind & Fire", ["Earth, Wind & Fire"]), 3);

    // ---- length --------------------------------------------------------------------------

    [Theory]
    [InlineData(330, 330, 0)]
    [InlineData(330, 335, 0)]
    [InlineData(330, 350, 0.5)]
    [InlineData(330, 370, 1)]
    [InlineData(330, 290, 1)]
    public void LengthPenalty_FiveSecondsGraceThirtyToTheWhole(int file, int candidate, double expected) =>
        Assert.Equal(expected, ReleaseDistance.LengthPenalty(file, candidate), 3);

    // ---- album ---------------------------------------------------------------------------

    [Theory]
    [InlineData("Mezzanine", "Mezzanine", 0)]
    [InlineData("Discovery", "Discovery (Deluxe Edition)", 0.3)]
    [InlineData("Nevermind", "Nevermind - 20th Anniversary", 0.3)]
    [InlineData("Discovery", "Musique Vol. 1 (1993-2005)", 1)]
    public void AlbumPenalty_EditionsTheRequestNeverNamed_CostALittle(string requested, string candidate, double expected) =>
        Assert.Equal(expected, ReleaseDistance.AlbumPenalty(requested, candidate), 3);

    // ---- type ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Album", new string[0], 0)]
    [InlineData("Single", new string[0], 0.2)]
    [InlineData("EP", new string[0], 0.2)]
    [InlineData("Album", new[] { "Soundtrack" }, 0.5)]
    [InlineData("Album", new[] { "DJ-mix" }, 0.8)]
    [InlineData("Album", new[] { "Mixtape/Street" }, 0.8)]
    [InlineData("Album", new[] { "Compilation" }, 1)]
    [InlineData("Album", new[] { "Live" }, 1)]
    [InlineData("Album", new[] { "Remix" }, 1)]
    [InlineData(null, new string[0], 0.5)]
    [InlineData("Other", new string[0], 0.5)]
    public void TypePenalty_PlainRequest(string? primary, string[] secondary, double expected) =>
        Assert.Equal(expected, ReleaseDistance.TypePenalty(primary, secondary, NoMarkers), 3);

    [Theory]
    [InlineData("Album", new[] { "Live" }, 0)]
    [InlineData("Album", new string[0], 0.6)]
    [InlineData("Single", new string[0], 0.6)]
    public void TypePenalty_LiveRequest(string primary, string[] secondary, double expected) =>
        Assert.Equal(expected, ReleaseDistance.TypePenalty(primary, secondary, Markers("live")), 3);

    [Theory]
    [InlineData("Album", new[] { "Remix" }, 0)]
    [InlineData("Single", new string[0], 0.2)]
    [InlineData("Album", new string[0], 0.5)]
    public void TypePenalty_RemixRequest(string primary, string[] secondary, double expected) =>
        Assert.Equal(expected, ReleaseDistance.TypePenalty(primary, secondary, Markers("remix")), 3);

    // ---- original, status, source, year, country, barcode --------------------------------

    [Theory]
    [InlineData(1998, 1998, 0)]
    [InlineData(2017, 1997, 0.8)]
    [InlineData(2030, 1990, 1)]
    public void OriginalPenalty_AGenerationLaterIsTheWhole(int groupFirst, int earliest, double expected) =>
        Assert.Equal(expected, ReleaseDistance.OriginalPenalty(groupFirst, earliest), 3);

    [Theory]
    [InlineData("Official", 0)]
    [InlineData("Promotion", 0.5)]
    [InlineData("Bootleg", 1)]
    [InlineData("Pseudo-Release", 1)]
    [InlineData("Something new", 0.25)]
    public void StatusPenalty(string status, double expected) =>
        Assert.Equal(expected, ReleaseDistance.StatusPenalty(status), 3);

    [Theory]
    [InlineData(TagSource.Fingerprint, 0)]
    [InlineData(TagSource.Database, 0.1)]
    [InlineData(TagSource.Catalog, 0.25)]
    [InlineData(TagSource.FileTags, 0.5)]
    public void SourcePenalty_TrustsTheFingerprintMost(TagSource source, double expected) =>
        Assert.Equal(expected, ReleaseDistance.SourcePenalty(source), 3);

    [Theory]
    [InlineData(2011, 1991, 1991, 0.571)]
    [InlineData(2011, 2011, 1991, 0)]
    [InlineData(1991, 2011, 1991, 0)]
    [InlineData(1991, 2011, null, 1)]
    public void YearPenalty_TheGapAsAShareOfTheCandidatesAge(int known, int candidate, int? original, double expected) =>
        Assert.Equal(expected, ReleaseDistance.YearPenalty(known, candidate, original, 2026), 2);

    [Theory]
    [InlineData("US", 0)]
    [InlineData("XW", 0.333)]
    [InlineData("GB", 0.667)]
    [InlineData("DE", 1)]
    public void CountryPenalty_PositionInTheList(string country, double expected) =>
        Assert.Equal(expected, ReleaseDistance.CountryPenalty(country, ["US", "XW", "GB"]), 2);

    [Fact]
    public void BarcodePenalty_TwelveAndThirteenDigitFormsAgree()
    {
        Assert.Equal(0, ReleaseDistance.BarcodePenalty("0602475682233", "602475682233"));
        Assert.Equal(1, ReleaseDistance.BarcodePenalty("0602475682233", "724384960629"));
        Assert.Null(ReleaseDistance.BarcodePenalty("not a code", "724384960629"));
        Assert.Null(ReleaseDistance.BarcodePenalty(null, "724384960629"));
    }

    // ---- the accumulator -----------------------------------------------------------------

    [Fact]
    public void Distance_IsTheWeightedSumOverTheWeightsThatApplied()
    {
        var distance = new Distance();
        distance.Add("title", 1, 3);
        distance.Add("artist", 0, 2);
        Assert.Equal(0.6, distance.Value, 3);
    }

    [Fact]
    public void Distance_AKeyAddedTwiceCountsItsWeightTwice()
    {
        var distance = new Distance();
        distance.Add("a", 0.5, 2);
        distance.Add("a", 0.5, 2);
        Assert.Equal(0.5, distance.Value, 3);
        Assert.Equal(2, distance.Breakdown.Count);
    }

    [Fact]
    public void Distance_AnAbsentKeyChangesNothing_AndNoKeysAtAllIsTheWhole()
    {
        var distance = new Distance();
        distance.Add("b", 0, 1);
        Assert.Equal(0, distance.Value, 3);
        Assert.Null(distance.PenaltyOf("a"));
        Assert.Equal(1, new Distance().Value, 3);
    }

    [Fact]
    public void Distance_ClampsAPenaltyToOne()
    {
        var distance = new Distance();
        distance.Add("a", 7, 1);
        Assert.Equal(1, distance.Value, 3);
    }
}
