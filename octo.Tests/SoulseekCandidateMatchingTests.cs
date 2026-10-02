using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// Which peer file gets accepted decides what audio ends up in the library, and a wrong
/// choice is invisible: the tagger stamps the correct title, track number and cover art
/// onto whatever arrived, so the library looks right and plays wrong.
///
/// Every filename below is real, taken from a live Soulseek walk of Massive Attack's
/// Mezzanine that silently pulled four tracks off a Mad Professor remix album.
/// </summary>
public class SoulseekCandidateMatchingTests
{
    // ---- filename matching ----------------------------------------------------

    /// <summary>
    /// A folder name must not satisfy a track title. Matching used to run against the
    /// whole path, so any file inside a "Mezzanine (1998)" directory answered a search
    /// for the track "Mezzanine".
    /// </summary>
    [Fact]
    public void FolderNameDoesNotSatisfyATrackTitle()
    {
        const string inMezzanineFolder = @"music\Massive Attack\Mezzanine (1998)\03 - Teardrop.flac";

        Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(inMezzanineFolder, "Mezzanine"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(inMezzanineFolder, "Teardrop"));
    }

    /// <summary>
    /// The filename check alone CANNOT catch every case, and pretending otherwise would
    /// be the wrong lesson. This peer encodes artist, album and title into one flat
    /// filename, so the album name "Mezzanine" sits in the leaf and the name check passes
    /// a file that is actually a different song. Duration and the variant marker are what
    /// reject it, which is why all three layers exist.
    /// </summary>
    [Fact]
    public void FlatFilenamesNeedTheOtherTwoSignals()
    {
        const string wrong =
            @"Massive Attack_Massive Attack V Mad Professor Part II (Mezzanine Remix Tapes '98)_08_Group Four (Security Forces Dub).flac";

        // The name check cannot tell: "mezzanine" really is in the leaf.
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(wrong, "Mezzanine"));

        // These do. "Mezzanine" is 354s and this file is 494s.
        Assert.False(SoulseekDownloadService.DurationPlausible(494, 354));
        Assert.True(SoulseekDownloadService.VariantPenalty(wrong, "Mezzanine") > 0);
    }

    [Fact]
    public void RealTrackFilenamesStillMatch()
    {
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            @"music\Massive Attack\Mezzanine (1998)\09 - Mezzanine.flac", "Mezzanine"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            @"Massive Attack_Collected_01-05_Inertia Creeps.flac", "Inertia Creeps"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            @"Massive-Attack-Mezzanine-06-Dissolved-Girl.flac", "Dissolved Girl"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            @"07 Massive Attack - Man Next Door.flac", "Man Next Door"));
    }

    /// <summary>Any-one-token matching let "Group Four" be satisfied by unrelated files.</summary>
    [Fact]
    public void EverySignificantTokenMustBePresent()
    {
        Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "Frankie Valli - The Four Seasons.flac", "Group Four"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "10 - Group Four.flac", "Group Four"));
    }

    /// <summary>Short titles must not be filtered away by the >=3 char token rule.</summary>
    [Fact]
    public void ShortTitlesAreNotOverFiltered()
    {
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "Kendrick Lamar - DNA..flac", "DNA."));
    }

    // ---- title-only fallback strictness ---------------------------------------

    /// <summary>
    /// The real file that answered a star for Jason Aldean's "The Truth" once the
    /// artist dropped out of the query: every token of the title sits somewhere in the
    /// name, so scattered-token matching passed a 136 MB dungeon-synth track. The
    /// phrase rule is what rejects it, and the loose assertion documents why the phrase
    /// rule exists rather than being a bug in the token rule.
    /// </summary>
    [Fact]
    public void ScatteredTitleTokensDoNotSatisfyTheTitleOnlyFallback()
    {
        const string wrong = "UNSHEATHED GLORY - Finale - The Greataxe of Shining Truth.flac";

        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(wrong, "The Truth"));
        Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(wrong, "The Truth", requirePhrase: true));
    }

    [Fact]
    public void RealFilenameShapesStillPassThePhraseRule()
    {
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "Jason Aldean - The Truth.flac", "The Truth", requirePhrase: true));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "03 - The Truth.flac", "The Truth", requirePhrase: true));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            @"music\Massive Attack\Mezzanine (1998)\09 - Mezzanine.flac", "Mezzanine", requirePhrase: true));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "Massive-Attack-Mezzanine-06-Dissolved-Girl.flac", "Dissolved Girl", requirePhrase: true));
    }

    /// <summary>Filenames routinely drop a leading article, and that is not a mismatch.</summary>
    [Fact]
    public void ALeadingArticleMayDropFromTheFilename()
    {
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "Jason Aldean - Truth.flac", "The Truth", requirePhrase: true));
    }

    /// <summary>
    /// Dotted acronyms space-normalize into single letters no filename spells out, so
    /// the compact form is accepted — but the anything-goes pass that zero significant
    /// tokens used to grant is exactly what the fallback cannot afford.
    /// </summary>
    [Fact]
    public void DottedAcronymsMatchCompactButNoLongerMatchAnything()
    {
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "MIA.flac", "M.I.A.", requirePhrase: true));
        Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "anything at all.flac", "M.I.A.", requirePhrase: true));
    }

    /// <summary>
    /// The incident's second gate: the wrong file advertised no length, and unknown
    /// used to pass unconditionally. On the fallback, a known catalog length makes an
    /// unadvertised one disqualifying — rejection after the download still costs the
    /// full transfer, and this candidate class is where the 136 MB one came from.
    /// </summary>
    [Fact]
    public void UnknownLengthLosesItsFreePassOnTheTitleOnlyFallback()
    {
        Assert.False(SoulseekDownloadService.DurationPlausible(null, 245, requireKnownLength: true));
        Assert.False(SoulseekDownloadService.DurationPlausible(0, 245, requireKnownLength: true));

        Assert.True(SoulseekDownloadService.DurationPlausible(null, 245));
        Assert.True(SoulseekDownloadService.DurationPlausible(243, 245, requireKnownLength: true));
        Assert.True(SoulseekDownloadService.DurationPlausible(null, null, requireKnownLength: true));
        Assert.True(SoulseekDownloadService.DurationPlausible(245, null, requireKnownLength: true));
    }

    // ---- duration -------------------------------------------------------------

    [Fact]
    public void WildlyWrongLengthIsRejected()
    {
        // "Mezzanine" is 354s; the file that arrived was 494s.
        Assert.False(SoulseekDownloadService.DurationPlausible(494, 354));
        // "Exchange" is 251s; a dub mix of 344s arrived.
        Assert.False(SoulseekDownloadService.DurationPlausible(344, 251));
    }

    [Fact]
    public void MasteringDriftIsAccepted()
    {
        // Real spread measured across the album's correctly-matched tracks.
        Assert.True(SoulseekDownloadService.DurationPlausible(380, 378));
        Assert.True(SoulseekDownloadService.DurationPlausible(331, 327));
        Assert.True(SoulseekDownloadService.DurationPlausible(299, 299));
    }

    /// <summary>
    /// The near misses that a looser tolerance let through: an "(Angel Dust)" dub 12s off
    /// and an "(Floating on Dubwise)" dub 11s off, against correct tracks that never
    /// drifted past 4s.
    /// </summary>
    [Fact]
    public void NearMissDubMixesAreRejected()
    {
        Assert.False(SoulseekDownloadService.DurationPlausible(366, 378));
        Assert.False(SoulseekDownloadService.DurationPlausible(367, 356));
    }

    /// <summary>
    /// Neither of these contains a keyword a marker list would catch. What gives them away
    /// is that they are bracketed additions the requested title never asked for.
    /// </summary>
    [Fact]
    public void UnrequestedBracketedAdditionsArePenalised()
    {
        Assert.True(SoulseekDownloadService.VariantPenalty(
            "2-02 Massive Attack & Mad Professor - Angel (Angel Dust).flac", "Angel") > 0);
        Assert.True(SoulseekDownloadService.VariantPenalty(
            "Massive Attack - Mezzanine - 04 - Inertia Creeps (Floating on Dubwise).flac",
            "Inertia Creeps") > 0);
    }

    /// <summary>Year and format tags are how peers label a good rip, not a different take.</summary>
    [Fact]
    public void YearAndFormatTagsAreNotTreatedAsVariants()
    {
        Assert.Equal(0, SoulseekDownloadService.VariantPenalty("Angel (1998).flac", "Angel"));
        Assert.Equal(0, SoulseekDownloadService.VariantPenalty("Angel [FLAC].flac", "Angel"));
    }

    [Fact]
    public void UnknownLengthIsNotTreatedAsEvidence()
    {
        Assert.True(SoulseekDownloadService.DurationPlausible(null, 354));
        Assert.True(SoulseekDownloadService.DurationPlausible(354, null));
        Assert.True(SoulseekDownloadService.DurationPlausible(0, 354));
    }

    // ---- variant markers ------------------------------------------------------

    /// <summary>
    /// The case duration cannot catch: "Group Four (Security Forces dub)" runs 495s
    /// against the album version's 493s, so only the name gives it away.
    /// </summary>
    [Fact]
    public void UnrequestedVariantsSortBelowPlainMatches()
    {
        var dub = SoulseekDownloadService.VariantPenalty(
            "2-08 Massive Attack & Mad Professor - Group Four (Security Forces dub).flac", "Group Four");
        var plain = SoulseekDownloadService.VariantPenalty(
            "10 Massive Attack - Group Four.flac", "Group Four");

        Assert.True(dub > plain, "a dub mix must rank below the plain album version");
        Assert.Equal(0, plain);
    }

    /// <summary>A remix that was actually asked for must not be penalised.</summary>
    [Fact]
    public void RequestedVariantsAreNotPenalised()
    {
        Assert.Equal(0, SoulseekDownloadService.VariantPenalty(
            "05 - Teardrop (Mazaruni Dub One).flac", "Teardrop (Mazaruni Dub One)"));
    }

    /// <summary>Word boundaries, so "Oliver" or "delivery" is not read as "live".</summary>
    [Fact]
    public void MarkersMatchWholeWordsOnly()
    {
        Assert.Equal(0, SoulseekDownloadService.VariantPenalty(
            "Oliver Nelson - Stolen Moments.flac", "Stolen Moments"));
    }

    // ---- roman numerals -------------------------------------------------------

    /// <summary>
    /// The >=3 char token rule deleted the only thing separating these two titles, so
    /// either file satisfied a request for the other and a two-part suite arrived as two
    /// copies of the same part.
    /// </summary>
    [Fact]
    public void RomanNumeralPartsAreNotInterchangeable()
    {
        Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "02 - Trilogy II.flac", "Trilogy I"));
        Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "01 - Trilogy I.flac", "Trilogy II"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "01 - Trilogy I.flac", "Trilogy I"));
    }

    /// <summary>
    /// Word boundaries in both directions: "I" must not find itself inside "II", and "V"
    /// must not find itself inside "IV".
    /// </summary>
    [Fact]
    public void RomanNumeralsMatchWholeWordsOnly()
    {
        Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "Trilogy IV.flac", "Trilogy V"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "Trilogy IV.flac", "Trilogy IV"));
    }

    /// <summary>
    /// The guard is anchored to the end of the title, so ordinary titles carrying a
    /// stray "I" or a trailing letter are untouched by it.
    /// </summary>
    [Fact]
    public void OrdinaryTitlesAreUnaffectedByTheRomanGuard()
    {
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "Kendrick Lamar - DNA..flac", "DNA."));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            @"music\Massive Attack\Mezzanine (1998)\09 - Mezzanine.flac", "Mezzanine"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(
            "10 - Group Four.flac", "Group Four"));
    }

    [Fact]
    public void AnAlbumQueryIsAddedLastOnlyWhenTheAlbumSaysSomething()
    {
        var planned = SoulseekDownloadService.PlannedQueries("Teardrop", "Massive Attack", "Mezzanine", 330);
        Assert.Equal(("Massive Attack Mezzanine", true), (planned[^1].Query.Text, planned[^1].Strict));
        Assert.False(planned[0].Strict);
        Assert.DoesNotContain(planned, p => p.Query.Text.Contains("flac", StringComparison.OrdinalIgnoreCase));
        Assert.Null(SoulseekDownloadService.AlbumQuery("Teardrop", "Massive Attack", "Mezzanine", null));
        Assert.Null(SoulseekDownloadService.AlbumQuery("Teardrop", "Massive Attack", "Teardrop", 330));
        Assert.Null(SoulseekDownloadService.AlbumQuery("Hello", "Adele", "Hello - Single", 295));
        Assert.Null(SoulseekDownloadService.AlbumQuery("Song", "Artist", "[Unknown Album]", 200));
        Assert.Null(SoulseekDownloadService.AlbumQuery("Song", "Artist", "Single", 200));
        Assert.Equal(planned.Count - 1, SoulseekDownloadService.PlannedQueries("Teardrop", "Massive Attack", "Mezzanine", null).Count);
    }

    [Fact]
    public void AShortTitleMustAppearAsWordsOfItsOwn()
    {
        Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle("anything at all.flac", "M.I.A."));
        Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle("Supper Club.flac", "Up"));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle("MIA.flac", "M.I.A."));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle("Peter Gabriel - 05 - Up.flac", "Up"));
    }

    // ---- quality ranking ------------------------------------------------------

    private static SoulseekFileHit Hit(int? bitDepth, int? sampleRate) =>
        new() { BitDepth = bitDepth, SampleRate = sampleRate };

    /// <summary>
    /// A 24/96 transfer of a CD-era master carries no more music than the 16/44.1 one,
    /// at several times the bytes and the transfer time. Ranked down, never rejected.
    /// </summary>
    [Fact]
    public void CdQualityOutranksHiRes()
    {
        var cd = SoulseekDownloadService.QualityPenalty(Hit(16, 44100));
        var cd48 = SoulseekDownloadService.QualityPenalty(Hit(16, 48000));
        var hiRes = SoulseekDownloadService.QualityPenalty(Hit(24, 96000));

        Assert.True(cd < cd48, "16/44.1 is the target, 16/48 is the runner-up");
        Assert.True(cd48 < hiRes, "hi-res sorts last");
    }

    /// <summary>
    /// Most peers report neither field. Treating unknown as hi-res would bury the
    /// majority of a normal search; treating it as CD would let an unlabelled 24/96
    /// outrank a labelled 16/44.1. It sits between the two on purpose.
    /// </summary>
    [Fact]
    public void UnknownQualitySitsBetweenCdAndHiRes()
    {
        var unknown = SoulseekDownloadService.QualityPenalty(Hit(null, null));

        Assert.True(SoulseekDownloadService.QualityPenalty(Hit(16, 48000)) < unknown);
        Assert.True(unknown < SoulseekDownloadService.QualityPenalty(Hit(24, 96000)));
    }

    /// <summary>
    /// Size is the last signal left and it ties often, because slskd reports queue length
    /// and upload speed per response rather than per file. Pointing it the wrong way for
    /// a lossy library walks every track down to the worst copy on the shelf.
    /// </summary>
    [Fact]
    public void SizeTiebreakPointsTowardsCdRipsButAwayFromLowBitrates()
    {
        Assert.True(
            SoulseekDownloadService.SizeSortKey(30_000_000, "flac")
            > SoulseekDownloadService.SizeSortKey(25_000_000, "flac"),
            "chasing lossless, the smaller of two equals is the CD rip");

        Assert.True(
            SoulseekDownloadService.SizeSortKey(10_000_000, "mp3")
            < SoulseekDownloadService.SizeSortKey(4_000_000, "mp3"),
            "chasing lossy, the bigger file is simply the higher bitrate");
    }

    /// <summary>A configured extension is normalized the same way a hit's is.</summary>
    [Fact]
    public void SizeTiebreakReadsAConfiguredExtensionInAnyShape()
    {
        var smallerFirst = SoulseekDownloadService.SizeSortKey(9, "flac");
        Assert.Equal(smallerFirst, SoulseekDownloadService.SizeSortKey(9, ".flac"));
        Assert.Equal(smallerFirst, SoulseekDownloadService.SizeSortKey(9, "FLAC"));
    }

    // ---- extension normalization ----------------------------------------------

    /// <summary>
    /// Ranking accepts a hit by comparing its extension against the configured one, and
    /// slskd does not report a consistent shape. An unnormalized ".flac" matched no
    /// configured "flac", which surfaced as "this track is not on Soulseek" rather than
    /// as a parsing mismatch, and took every FLAC on the network with it.
    /// </summary>
    [Fact]
    public void EveryShapeSlskdReportsReducesToTheSameExtension()
    {
        Assert.Equal("flac", SoulseekClient.NormalizeExtension(".flac", "x.flac"));
        Assert.Equal("flac", SoulseekClient.NormalizeExtension("flac", "x.flac"));
        Assert.Equal("flac", SoulseekClient.NormalizeExtension("FLAC", "x.flac"));
        Assert.Equal("flac", SoulseekClient.NormalizeExtension("  .FLAC ", "x.flac"));
    }

    /// <summary>Field absent or blank: fall back to the filename it came with.</summary>
    [Fact]
    public void AMissingExtensionFallsBackToTheFilename()
    {
        Assert.Equal("flac", SoulseekClient.NormalizeExtension(null, @"share\Artist\01 - Track.flac"));
        Assert.Equal("flac", SoulseekClient.NormalizeExtension("", @"share\Artist\01 - Track.flac"));
        Assert.Equal("", SoulseekClient.NormalizeExtension(null, "no extension at all"));
    }
}
