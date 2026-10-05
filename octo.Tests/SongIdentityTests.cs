using System.Text.Json;
using Octo.Services.Common;
using Octo.Services.Fingerprint;
using Octo.Services.Lyrics;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// The shared reading of songs where it decides something: the query variants a search tries,
/// and the three places that must never take a different version for the song asked for (a
/// Soulseek peer's file, the recording AcoustID identified, and a lyrics entry).
/// </summary>
public class SongIdentityTests
{
    // ---- query variants ---------------------------------------------------------------------

    [Fact]
    public void QueryVariants_StylizedArtist_AddsTheSpelledOutQuery()
    {
        var queries = SongIdentity.QueryVariants("$UICIDE", "$uicideboy$");

        Assert.Equal(["$uicideboy$ $UICIDE", "suicideboys SUICIDE", "$UICIDE"], queries.Select(query => query.Text));
        Assert.Equal("", queries[^1].Artist);
    }

    [Fact]
    public void QueryVariants_OrderIsOriginalCleanStylizedPrimaryTitleOnly()
    {
        var queries = SongIdentity.QueryVariants("Ca$h (feat. Gue$t) [Official Video]", "A$AP Rocky feat. Gue$t");

        Assert.Equal(
        [
            new SongQuery("Ca$h (feat. Gue$t) [Official Video]", "A$AP Rocky feat. Gue$t"),
            new SongQuery("Ca$h", "A$AP Rocky feat. Gue$t"),
            new SongQuery("Cash", "ASAP Rocky feat. Guest"),
            new SongQuery("Ca$h", "A$AP Rocky"),
            new SongQuery("Ca$h", ""),
        ], queries);
    }

    [Fact]
    public void QueryVariants_NothingToClean_IsTheOriginalAndTheTitle()
        => Assert.Equal(["Drake Landed", "Landed"], SongIdentity.QueryVariants("Landed", "Drake").Select(query => query.Text));

    [Fact]
    public void QueryVariants_KeepTheVersionOutOfTheCleanQuery_ButTheMatchStillNeedsIt()
    {
        // A looser query never means a looser match: the studio hit the clean query finds is
        // still not the live song asked for.
        var queries = SongIdentity.QueryVariants("Creep (Live)", "Radiohead");
        Assert.Contains(queries, query => query.Title == "Creep");
        Assert.Equal(SongVerdict.SameSongDifferentVersion, SongIdentity.Same("Creep (Live)", "Radiohead", "Creep", "Radiohead").Verdict);
    }

    [Fact]
    public void QueryVariants_ArtistPrefixedTitle_LosesThePrefix()
        => Assert.Equal(["Adele Adele - Hello", "Adele Hello", "Hello"],
            SongIdentity.QueryVariants("Adele - Hello", "Adele").Select(query => query.Text));

    [Fact]
    public void SoulseekQueries_SkipBracketsAndCapTheSearches()
    {
        Assert.Equal(["Fishmans Long Season", "Long Season"],
            SoulseekDownloadService.SearchQueries("Long Season [LIVE][4K]", "Fishmans").Select(query => query.Text));
        Assert.Equal(["$uicideboy$ $UICIDE", "suicideboys SUICIDE", "$UICIDE"],
            SoulseekDownloadService.SearchQueries("$UICIDE", "$uicideboy$").Select(query => query.Text));
        Assert.Equal(["Massive Attack Exchange", "Exchange"],
            SoulseekDownloadService.SearchQueries("(Exchange)", "Massive Attack").Select(query => query.Text));
        // Two with the artist at most, then the title alone.
        Assert.Equal(3, SoulseekDownloadService.SearchQueries("Ca$h (feat. Gue$t)", "A$AP Rocky feat. Gue$t").Count);
    }

    // ---- Soulseek: never a different version -------------------------------------------------

    [Theory]
    [InlineData(@"music\Radiohead\OK Computer\03 - Creep (Live).flac", "Creep")]
    [InlineData("Glass Animals - Heat Waves (Sped Up).flac", "Heat Waves")]
    [InlineData("Heat Waves (Slowed + Reverb).flac", "Heat Waves")]
    [InlineData("08 - Group Four (Security Forces Dub).flac", "Group Four")]
    [InlineData("Song - Radio Edit.flac", "Song")]
    [InlineData("Song (Instrumental).flac", "Song")]
    [InlineData("Song (Live).flac", "Song (Remix)")]
    public void Soulseek_AFileOfAnotherVersion_IsRejected(string filename, string title)
        => Assert.True(SoulseekDownloadService.AddsVersion(filename, title));

    [Theory]
    [InlineData("03 - Creep.flac", "Creep")]
    [InlineData("Creep (Remastered 2009).flac", "Creep")]
    [InlineData("Strobe (Original Mix).flac", "Strobe")]
    [InlineData("Song (Explicit).flac", "Song")]
    [InlineData("03 - Creep (Live).flac", "Creep (Live)")]
    [InlineData("Live Forever.flac", "Live Forever")]
    [InlineData("Hole - Live Through This - 03 - Doll Parts.flac", "Doll Parts")]
    public void Soulseek_TheSameVersion_IsKept(string filename, string title)
        => Assert.False(SoulseekDownloadService.AddsVersion(filename, title));

    [Theory]
    [InlineData("01 - Suicide.flac", "$UICIDE")]
    [InlineData("$uicideboy$ - $UICIDE.flac", "Suicide")]
    [InlineData("05 - Huntin' Wabbitz.flac", "Huntin’ Wabbitz")]
    [InlineData("Sigur Ros - Hoppipolla.flac", "Hoppípolla")]
    public void Soulseek_AFileNamedAnotherWay_StillMatchesTheTitle(string filename, string title)
    {
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(filename, title));
        Assert.True(SoulseekDownloadService.FilenamePlausiblyMatchesTitle(filename, title, requirePhrase: true));
    }

    [Fact]
    public void Soulseek_UltimateSuicide_IsNotSuicideOnTheTitleOnlySearch()
        => Assert.False(SoulseekDownloadService.FilenamePlausiblyMatchesTitle("Ultimate $uicide.flac", "$UICIDE Pt. 2", requirePhrase: true));

    // ---- AcoustID: never a different version -------------------------------------------------

    [Theory]
    [InlineData("Heat Waves", "Heat Waves (Sped Up)")]
    [InlineData("Heat Waves", "Heat Waves (Slowed + Reverb)")]
    [InlineData("Creep", "Creep - Live at Glastonbury")]
    [InlineData("Song", "Song (Instrumental)")]
    [InlineData("Song", "Song (Karaoke Version)")]
    [InlineData("Song (Skrillex Remix)", "Song (Skrillex Remix) (Live)")]
    [InlineData("Mask Off", "Mask Off Remix")]
    public void AcoustId_AMatchOfAnotherVersion_IsAMismatch(string requested, string matched)
        => Assert.False(TrackMatchComparer.TitleMatches(requested, matched));

    [Theory]
    [InlineData("$UICIDE", "Suicide")]
    [InlineData("Strobe", "Strobe (Original Mix)")]
    [InlineData("Song", "Song (Album Version)")]
    [InlineData("Huntin’ Wabbitz", "Huntin' Wabbitz")]
    [InlineData("Real Friends (Explicit)", "Real Friends")]
    public void AcoustId_TheSameRecordingWrittenOtherwise_Matches(string requested, string matched)
        => Assert.True(TrackMatchComparer.TitleMatches(requested, matched));

    [Theory]
    [InlineData("$uicideboy$", "Suicideboys")]
    [InlineData("Kanye West", "Ye")]
    [InlineData("Tyler, The Creator", "Tyler, The Creator")]
    [InlineData("Lil Peep/iLoveMakonnen", "Lil Peep & iLoveMakonnen")]
    public void AcoustId_TheSameArtistWrittenOtherwise_Matches(string requested, string credited)
        => Assert.True(TrackMatchComparer.ArtistMatches(requested, credited, null));

    [Fact]
    public void AcoustId_AgreeingCandidate_SkipsAnotherVersion()
    {
        var lookup = new AcoustIdLookup(true, null, [new AcoustIdResult(0.95, [
            new AcoustIdRecording("live", "Creep (Live)", ["Radiohead"], null, null) { DurationSeconds = 238 },
            new AcoustIdRecording("studio", "Creep", ["Radiohead"], null, null) { DurationSeconds = 238 },
        ])]);

        Assert.Equal("studio", DownloadVerificationService.AgreeingCandidate(lookup, "Radiohead", "Creep", 238));
    }

    // ---- lyrics: never a different version --------------------------------------------------

    public static IEnumerable<object[]> OtherVersions() =>
    [
        ["Creep", "Creep (Live)"],
        ["Heat Waves", "Heat Waves (Sped Up)"],
        ["Heat Waves", "Heat Waves Slowed"],
        ["Mask Off", "Mask Off Remix"],
        ["Song", "Song (Instrumental)"],
        ["Song (Skrillex Remix)", "Song (Diplo Remix)"],
        ["Song", "Song - Radio Edit"],
    ];

    [Theory]
    [MemberData(nameof(OtherVersions))]
    public void Lyrics_KuGou_NeverTakesAnotherVersion(string want, string got)
    {
        var query = new LyricsQuery("Artist", want, null, 200);
        Assert.False(KugouLyricsSource.IsThisSong(new LyricsCandidate("kugou", "1.a", got, "Artist", null, 200), query));
        Assert.False(KugouLyricsSource.IsThisSong(new LyricsCandidate("kugou", "1.a", want, "Artist", null, 200),
            query with { Title = got }));
    }

    [Theory]
    [MemberData(nameof(OtherVersions))]
    public void Lyrics_Lrclib_NeverTakesAnotherVersion(string want, string got)
    {
        var row = JsonDocument.Parse(JsonSerializer.Serialize(new { trackName = got, artistName = "Artist", duration = 200 })).RootElement;
        Assert.False(LrclibLyricsSource.IsThisSong(row, new LyricsQuery("Artist", want, null, 200)));
    }

    [Theory]
    [MemberData(nameof(OtherVersions))]
    public void Lyrics_NetEase_NeverTakesAnotherVersion(string want, string got)
        => Assert.False(NeteaseLyricsSource.IsThisSong(new LyricsCandidate("netease", "1", got, "Artist", null, 200),
            new LyricsQuery("Artist", want, null, 200)));

    [Theory]
    [InlineData("$UICIDE", "$uicideboy$", "Suicide", "Suicideboys")]
    [InlineData("Huntin’ Wabbitz", "$uicideboy$", "Huntin' Wabbitz", "$UICIDEBOY$")]
    [InlineData("Can't Tell Me Nothing", "Kanye West", "Can't Tell Me Nothing (Explicit)", "Ye (侃爷)")]
    [InlineData("Sunlight On Your Skin", "Lil Peep feat. iLoveMakonnen", "Sunlight On Your Skin", "Lil Peep、iLoveMakonnen")]
    public void Lyrics_TheSameSongWrittenOtherwise_IsFound(string want, string wantArtist, string got, string gotArtist)
        => Assert.True(KugouLyricsSource.IsThisSong(new LyricsCandidate("kugou", "1.a", got, gotArtist, null, 200),
            new LyricsQuery(wantArtist, want, null, 201)));

    [Theory]
    [InlineData("Movie Star", "Movie Star (Clean)")]
    [InlineData("Movie Star", "Movie Star (Clean Version)")]
    [InlineData("Movie Star (Explicit)", "Movie Star (Censored)")]
    public void Lyrics_ACleanEditsWordsFitTheSong(string want, string got)
    {
        // Same words at the same times, a few bleeped: good lyrics for the explicit recording.
        Assert.True(KugouLyricsSource.IsThisSong(new LyricsCandidate("kugou", "1.a", got, "Artist", null, 200),
            new LyricsQuery("Artist", want, null, 200)));
        // A download still never takes the clean edit for the song asked for.
        Assert.NotEqual(SongVerdict.Same, SongIdentity.Same(want, "Artist", got, "Artist").Verdict);
    }

    // ---- the comparison itself --------------------------------------------------------------

    [Fact]
    public void Same_SaysWhyAndHowSure()
    {
        var loose = SongIdentity.Same("$UICIDE", "$uicideboy$", "Suicide", "Suicideboys");
        Assert.Equal(SongVerdict.Same, loose.Verdict);
        Assert.True(loose.Confidence < 1);
        Assert.Contains("stylized", loose.Reason);

        var live = SongIdentity.Same("Creep", "Radiohead", "Creep (Live)", "Radiohead");
        Assert.Equal(SongVerdict.SameSongDifferentVersion, live.Verdict);
        Assert.Contains("live", live.Reason);

        var exact = SongIdentity.Same(new SongRef("Creep", "Radiohead", 238), new SongRef("Creep", "Radiohead", 239));
        Assert.Equal(1.0, exact.Confidence);
    }

    [Theory]
    [InlineData("Beyoncé", "Beyonce", true)]
    [InlineData("The Weeknd", "Weeknd", true)]
    [InlineData("$uicideboy$", "Suicideboys", true)]
    [InlineData("Kanye West", "Ye", true)]
    [InlineData("Phoen!x", "Phoenix", true)]
    [InlineData("Bob Marley & The Wailers", "Bob Marley", false)]
    [InlineData("Phoenix II", "Phoenix", false)]
    [InlineData("Drake feat. Rihanna", "Rihanna", false)]
    public void SameArtistName_IsTheWholeNameNeverAPart(string a, string b, bool same)
        => Assert.Equal(same, SongIdentity.SameArtistName(a, b));

    [Fact]
    public void TitleKey_IgnoresGuestsButKeepsTheVersion()
    {
        Assert.Equal(SongIdentity.TitleKey("Too Good"), SongIdentity.TitleKey("Too Good (feat. Rihanna)"));
        Assert.NotEqual(SongIdentity.TitleKey("Too Good"), SongIdentity.TitleKey("Too Good (Live)"));
    }

    [Fact]
    public void MatchKey_OneSongOneKey_OneVersionOneKey()
    {
        Assert.Equal(SongIdentity.MatchKey("Drake feat. Rihanna", "Too Good"), SongIdentity.MatchKey("Drake", "Too Good (feat. Rihanna)"));
        Assert.Equal(SongIdentity.MatchKey("Kanye West", "Stronger"), SongIdentity.MatchKey("Ye (侃爷)", "Stronger (Explicit)"));
        Assert.NotEqual(SongIdentity.MatchKey("Radiohead", "Creep"), SongIdentity.MatchKey("Radiohead", "Creep (Live)"));
    }

    // ---- ISRCs ------------------------------------------------------------------------------

    [Theory]
    [InlineData("USRC17607839", "USRC17607839")]
    [InlineData("us-rc1-76-07839", "USRC17607839")]
    [InlineData(" US RC1 76 07839 ", "USRC17607839")]
    [InlineData("US.RC1.76.07839", "USRC17607839")]
    [InlineData("ＵＳＲＣ１７６０７８３９", "USRC17607839")]
    [InlineData("GBAHT1600302", "GBAHT1600302")]
    [InlineData("USRC1760783", null)]
    [InlineData("USRC176078390", null)]
    [InlineData("1SRC17607839", null)]
    [InlineData("USRC1760783X", null)]
    [InlineData("US_RC17607839", null)]
    [InlineData("ISRC: USRC17607839", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeIsrc_OneSpellingOrAbsent(string? value, string? expected) =>
        Assert.Equal(expected, SongIdentity.NormalizeIsrc(value));

    [Fact]
    public void SharesIsrc_NeedsAValidCodeOnBothSides()
    {
        Assert.True(SongIdentity.SharesIsrc(["USRC17607839"], ["us-rc1-76-07839"]));
        Assert.True(SongIdentity.SharesIsrc(["GBAHT1600302", "USRC17607839"], ["USRC17607839"]));
        Assert.False(SongIdentity.SharesIsrc(["USRC17607839"], ["GBAHT1600302"]));
        Assert.False(SongIdentity.SharesIsrc(["USRC17607839"], []));
        Assert.False(SongIdentity.SharesIsrc(null, ["USRC17607839"]));
        Assert.False(SongIdentity.SharesIsrc(["junk"], ["junk"]));
    }

    [Fact]
    public void Same_OneIsrc_IsTheSameRecordingAtFullConfidence()
    {
        var match = SongIdentity.Same(
            new SongRef("紅蓮華", "LiSA") { Isrcs = ["JPU901901234"] },
            new SongRef("Gurenge", "LiSA") { Isrcs = ["JP-U90-19-01234"] });

        Assert.Equal(SongVerdict.Same, match.Verdict);
        Assert.Equal(1.0, match.Confidence);
        Assert.Equal("same ISRC", match.Reason);
    }

    [Fact]
    public void Same_DifferentIsrcs_FallBackToTheTextUnchanged()
    {
        var withCodes = SongIdentity.Same(
            new SongRef("Song (Remastered 2011)", "Artist", 200) { Isrcs = ["GBAAA0100001"] },
            new SongRef("Song", "Artist", 201) { Isrcs = ["GBAAA1100002"] });
        var without = SongIdentity.Same(new SongRef("Song (Remastered 2011)", "Artist", 200), new SongRef("Song", "Artist", 201));

        Assert.Equal(without, withCodes);
    }

    [Theory]
    [InlineData("Ye", "Kanye West")]
    [InlineData("Kanye", "Kanye West")]
    [InlineData("ye", "Kanye West")]
    [InlineData("Tupac Shakur", "2Pac")]
    [InlineData("Puff Daddy", "Diddy")]
    [InlineData("Snoop Lion", "Snoop Dogg")]
    [InlineData("Yasiin Bey", "Mos Def")]
    [InlineData("Biggie Smalls", "The Notorious B.I.G.")]
    [InlineData("Donald Glover", "Childish Gambino")]
    [InlineData("The Artist Formerly Known as Prince", "Prince")]
    public void KnownName_AnAliasLeadsToTheNameTheArtistIsKnownBy(string alias, string known)
    {
        Assert.Equal(known, SongIdentity.KnownName(alias));
        // The name found is the same artist by the alias table's own rule.
        Assert.True(SongIdentity.SameArtistName(alias, known));
    }

    [Theory]
    [InlineData("Kanye West")]
    [InlineData("2Pac")]
    [InlineData("Diddy")]
    [InlineData("Radiohead")]
    [InlineData("")]
    [InlineData(null)]
    public void KnownName_NoneForANameThatIsNotAnAlias(string? artist)
    {
        Assert.Null(SongIdentity.KnownName(artist));
    }

    // ---- AlbumCoreKey: same record re-issued folds, a different record does not ----------

    [Theory]
    [InlineData("Coltrane")]
    [InlineData("Coltrane (Expanded Edition)")]
    [InlineData("Coltrane (Deluxe Edition - Rudy Van Gelder Remaster)")]
    [InlineData("Coltrane [Rudy Van Gelder Remaster]")]
    [InlineData("Coltrane (Remastered 2026)")]
    [InlineData("Coltrane (2009)")]
    [InlineData("Coltrane [24 bit Hi-Res]")]
    public void AlbumCoreKey_EditionsOfOneRecord_Fold(string title)
    {
        var key = SongIdentity.AlbumCoreKey("John Coltrane", title);
        Assert.Equal(SongIdentity.AlbumCoreKey("John Coltrane", "Coltrane"), key);
    }

    [Theory]
    [InlineData("Giant Steps (226 bpm – Live from The Tiberi Tapes)")]
    [InlineData("Coltrane (Crooked Man Remix)")]
    [InlineData("Coltrane '58: The Prestige Recordings")]
    [InlineData("Coltrane For Lovers")]
    [InlineData("Duke Ellington & John Coltrane")]
    [InlineData("Coltrane Jazz")]
    public void AlbumCoreKey_DifferentRecords_StandApart(string title)
    {
        var key = SongIdentity.AlbumCoreKey("John Coltrane", title);
        Assert.NotEqual(SongIdentity.AlbumCoreKey("John Coltrane", "Coltrane"), key);
    }

    [Fact]
    public void AlbumCoreKey_ArtistInKey_SameTitleDifferentArtistsStayApart()
        => Assert.NotEqual(
            SongIdentity.AlbumCoreKey("John Coltrane", "Coltrane"),
            SongIdentity.AlbumCoreKey("John Coltrane Quartet", "Coltrane"));

    [Fact]
    public void AlbumCoreKey_BothBracketsNameDifferentRecords_TheyStayApart()
    {
        // Two compilation volumes of the same series: the [Remastered] pressing word folds,
        // the volume's album list does not.
        var a = SongIdentity.AlbumCoreKey("John Coltrane",
            "Four Classic Albums (Blue Train / Africa Brass / Plays the Blues / Ole) [Remastered]");
        var b = SongIdentity.AlbumCoreKey("John Coltrane",
            "Four Classic Albums (Coltrane Jazz / My Favorite Things / Bags & Trane / Giant Steps) [Remastered]");
        Assert.NotEqual(a, b);
        // Bracket order does not matter either — the same record listed the other way round.
        var c = SongIdentity.AlbumCoreKey("John Coltrane",
            "Four Classic Albums (Ole/ Plays the Blues/ Africa Brass / Blue Train) [Remastered]");
        Assert.Equal(a, c);
    }

    [Fact]
    public void AlbumCoreKey_WholeTitleIsAnEditionWord_KeysAsItCame()
        => Assert.Equal(
            SongIdentity.AlbumCoreKey("Various", "2011 Remaster"),
            SongIdentity.AlbumCoreKey("Various", "2011 Remaster"));
}
