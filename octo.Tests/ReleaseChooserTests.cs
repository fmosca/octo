using System.Text.Json;
using Octo.Models.Domain;
using Octo.Services.Fingerprint;
using Octo.Services.Metadata;
using Octo.Services.Tagging;

namespace Octo.Tests;

/// <summary>
/// The calibration table: ten downloads as they arrive, each with the candidates its sources
/// would offer, and the release, confidence and album-level outcome each must land on. The
/// fixtures pin outcomes, not numbers, so a constant may be tuned until every row holds.
/// </summary>
public class ReleaseChooserTests
{
    private const int ThisYear = 2026;

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static TagRequest Request(string artist, string title, string? album = null, int? track = null,
        string? isrc = null, int? duration = null) =>
        new(artist, title, album, track, null, duration, isrc, null, null,
            Octo.Services.Common.SongIdentity.DistinctVersions(Octo.Services.Common.SongIdentity.ParseTitle(title)));

    private static FileFacts Peer(int seconds, string? title, string? artist, string? album = null, int? year = null,
        string? barcode = null, bool compilation = false, params string[] isrcs) =>
        new(seconds, ".flac", 44100, title, artist, album, null, year, null, null, isrcs, barcode, null, null, null, null,
            compilation, TagsAreEvidence: true);

    private static FileFacts Upload(int seconds) =>
        new(seconds, ".mp3", 44100, "Uploader Name - Song", "Some Channel", "Some Channel", null, null, null, null, [],
            null, null, null, null, null, false, TagsAreEvidence: false);

    private static TagEvidence Evidence(TagRequest request, FileFacts file, params string[] fingerprinted) =>
        new(request, file, 0.85, new HashSet<string>(fingerprinted, StringComparer.OrdinalIgnoreCase));

    private static ReleaseCandidate Fingerprinted(string recordingId, string title, string credit, string album,
        string groupId, string primaryType, string date, string? groupFirst = null, int length = 330,
        string? status = null, params string[] secondary) =>
        new(TagSource.Fingerprint, title, credit)
        {
            RecordingId = recordingId,
            PrimaryArtist = Octo.Services.Common.SongIdentity.PrimaryArtist(credit),
            LengthSeconds = length,
            ReleaseId = $"rel-{album}-{date}",
            ReleaseGroupId = groupId,
            ReleaseTitle = album,
            GroupTitle = album,
            PrimaryType = primaryType,
            SecondaryTypes = secondary,
            Status = status,
            ReleaseDate = date,
            GroupFirstReleaseDate = groupFirst ?? date,
            FingerprintId = "acoustid-1",
            Sources = 10,
        };

    private static Song SongFor(TagRequest request) => new()
    {
        Artist = request.Artist, Title = request.Title, Album = request.Album ?? "", Track = request.Track, Isrc = request.Isrc,
    };

    // ---- A: a lone star finds its studio album -------------------------------------------

    private static (TagEvidence Evidence, List<ReleaseCandidate> Candidates) FixtureA()
    {
        var request = Request("Massive Attack", "Teardrop", duration: 330);
        var evidence = Evidence(request, Peer(330, "Teardrop", "Massive Attack"), "rec-teardrop");
        var candidates = new List<ReleaseCandidate>
        {
            Fingerprinted("rec-teardrop", "Teardrop", "Massive Attack feat. Elizabeth Fraser", "Collected", "g-collected", "Album", "2006-03-27", secondary: "Compilation"),
            Fingerprinted("rec-teardrop", "Teardrop", "Massive Attack feat. Elizabeth Fraser", "Teardrop", "g-single", "Single", "1998-04-27"),
            Fingerprinted("rec-teardrop", "Teardrop", "Massive Attack feat. Elizabeth Fraser", "Mezzanine", "g-mezzanine", "Album", "1998-04-20")
                with { Label = "Virgin", CatalogNumber = "CDV 2851", Barcode = "724384559922", TrackNumber = 3, TrackCount = 11, DiscNumber = 1 },
        };
        return (evidence, candidates);
    }

    [Fact]
    public void A_LoneStar_FilesUnderTheStudioAlbum_TheSingleSecondAndNotAmbiguous()
    {
        var (evidence, candidates) = FixtureA();

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);

        Assert.Equal(TagConfidence.Strong, plan.Confidence);
        Assert.Equal("Mezzanine", plan.Chosen!.Candidate.AlbumTitle);
        Assert.Equal(0, plan.Chosen.Distance, 3);
        Assert.Equal("Teardrop", plan.Ranked[1].Candidate.AlbumTitle);
        Assert.Equal("Collected", plan.Ranked[2].Candidate.AlbumTitle);

        var song = SongFor(evidence.Request);
        plan.ApplyTo(song);
        Assert.Equal("Mezzanine", song.Album);
        Assert.Equal(1998, song.Year);
        Assert.Equal("1998-04-20", song.OriginalDate);
        Assert.Equal("Virgin", song.Label);
        Assert.Equal("CDV 2851", song.CatalogNumber);
        Assert.Equal("724384559922", song.Barcode);
        Assert.Equal("album", song.ReleaseType);
        Assert.Equal(3, song.Track);
        Assert.Equal(11, song.TotalTracks);
        Assert.Equal("rec-teardrop", song.MusicBrainzRecordingId);
        Assert.Equal("acoustid-1", song.AcoustId);
        Assert.Equal("g-mezzanine", song.MusicBrainzReleaseGroupId);
        Assert.Equal("Fingerprint", plan.Fields["album"].Source);
    }

    /// <summary>The same answer when the candidates come from a parsed lookup rather than by hand.</summary>
    [Fact]
    public void A_FromAParsedLookup_GivesTheSameAnswer()
    {
        using var doc = JsonDocument.Parse("""
        {"status": "ok", "results": [{"id": "acoustid-1", "score": 0.97, "recordings": [{
          "id": "rec-teardrop", "title": "Teardrop", "duration": 330.2, "sources": 40, "isrcs": ["GBAAA9800001"],
          "artists": [{"id": "a-ma", "name": "Massive Attack", "joinphrase": " feat. "}, {"id": "a-ef", "name": "Elizabeth Fraser"}],
          "releasegroups": [
            {"id": "g-collected", "title": "Collected", "type": "Album", "secondarytypes": ["Compilation"],
             "releases": [{"id": "r-col", "date": {"year": 2006, "month": 3, "day": 27}, "country": "GB",
               "mediums": [{"position": 1, "track_count": 14, "tracks": [{"id": "t-col", "position": 4}]}]}]},
            {"id": "g-single", "title": "Teardrop", "type": "Single",
             "releases": [{"id": "r-single", "date": {"year": 1998, "month": 4, "day": 27}, "country": "GB",
               "mediums": [{"position": 1, "track_count": 4, "tracks": [{"id": "t-s", "position": 1}]}]}]},
            {"id": "g-mezzanine", "title": "Mezzanine", "type": "Album",
             "releases": [
               {"id": "r-mezz-2019", "date": {"year": 2019, "month": 8, "day": 23}, "country": "XE",
                "mediums": [{"position": 1, "track_count": 11, "tracks": [{"id": "t-m19", "position": 3}]}]},
               {"id": "r-mezz", "date": {"year": 1998, "month": 4, "day": 20}, "country": "GB",
                "mediums": [{"position": 1, "track_count": 11, "tracks": [{"id": "t-m", "position": 3}]}]}]}
          ]}]}]}
        """);
        var lookup = AcoustIdClient.ParseLookup(doc.RootElement);
        var candidates = CandidateSources.FromFingerprint(lookup, 0.85);
        var request = Request("Massive Attack", "Teardrop", duration: 330);
        var evidence = Evidence(request, Peer(330, "Teardrop", "Massive Attack"), "rec-teardrop");

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);

        Assert.Equal(4, candidates.Count);
        Assert.Equal(TagConfidence.Strong, plan.Confidence);
        Assert.Equal("r-mezz", plan.Chosen!.Candidate.ReleaseId);
        Assert.Equal("1998-04-20", plan.Chosen.Candidate.GroupFirstReleaseDate);
        Assert.Equal(["GBAAA9800001"], plan.Chosen.Candidate.Isrcs);
        Assert.Equal(40, plan.Chosen.Candidate.Sources);
        Assert.Equal("t-m", plan.Chosen.Candidate.ReleaseTrackId);
        // The 2019 pressing of the same group sits behind the first one, not ahead of it.
        Assert.Equal("r-mezz-2019", plan.Ranked[1].Candidate.ReleaseId);
    }

    // ---- B: a reissue of the same album loses to the first -------------------------------

    [Fact]
    public void B_VideoUpload_PrefersTheOriginalAlbumOverItsReissue_NotAmbiguous()
    {
        var request = Request("Radiohead", "No Surprises (Official Video)", duration: 228);
        var evidence = Evidence(request, Upload(228), "rec-ns");
        var candidates = new List<ReleaseCandidate>
        {
            Fingerprinted("rec-ns", "No Surprises", "Radiohead", "OKNOTOK 1997 2017", "g-oknotok", "Album", "2017-06-23", length: 228),
            Fingerprinted("rec-ns", "No Surprises", "Radiohead", "OK Computer", "g-okc", "Album", "1997-05-21", length: 228),
        };

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);

        Assert.Equal(TagConfidence.Strong, plan.Confidence);
        Assert.Equal("OK Computer", plan.Chosen!.Candidate.AlbumTitle);
        Assert.True(plan.Ranked[1].Distance - plan.Chosen.Distance > ReleaseChooser.AmbiguityMargin);
    }

    // ---- C: an album walk keeps its album and takes the release's facts -------------------

    [Fact]
    public void C_AlbumWalk_RequestAlbumConfirmed_ReleaseFactsTaken()
    {
        var request = Request("Daft Punk", "One More Time", "Discovery", 1, "GBDUW0000059", 320);
        var evidence = Evidence(request, Peer(320, "One More Time", "Daft Punk", "Discovery", 2001, isrcs: "GBDUW0000059"), "rec-omt");
        var candidates = new List<ReleaseCandidate>
        {
            Fingerprinted("rec-omt", "One More Time", "Daft Punk", "Musique Vol. 1 (1993-2005)", "g-musique", "Album", "2006-03-29", secondary: "Compilation")
                with { TrackNumber = 7, Isrcs = ["GBDUW0000059"], Label = "Virgin", CatalogNumber = "COMP-1" },
            Fingerprinted("rec-omt", "One More Time", "Daft Punk", "Discovery", "g-discovery", "Album", "2001-03-12")
                with { TrackNumber = 1, TrackCount = 14, Isrcs = ["GBDUW0000059"], Label = "Virgin", CatalogNumber = "7243 8 49606 2 2", Barcode = "724384960629" },
        };

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);
        var song = SongFor(request);
        song.TotalTracks = 14;
        plan.ApplyTo(song);

        Assert.Equal(TagConfidence.Strong, plan.Confidence);
        Assert.Equal("Discovery", plan.Chosen!.Candidate.AlbumTitle);
        Assert.InRange(plan.Ranked[1].Distance, 0.1, 0.25);
        Assert.Equal("Discovery", song.Album);
        Assert.Equal(1, song.Track);
        Assert.Equal("7243 8 49606 2 2", song.CatalogNumber);
        Assert.Equal("724384960629", song.Barcode);
        Assert.Equal("GBDUW0000059", song.Isrc);
        Assert.Equal("Request", plan.Fields["isrc"].Source);
    }

    [Fact]
    public void C_RequestAlbum_IsNeverOverwritten_EvenByAStrongOtherAlbum()
    {
        var request = Request("Daft Punk", "One More Time", "Discovery", 1, duration: 320);
        var evidence = Evidence(request, Upload(320), "rec-omt");
        var candidates = new List<ReleaseCandidate>
        {
            Fingerprinted("rec-omt", "One More Time", "Daft Punk", "Musique Vol. 1 (1993-2005)", "g-musique", "Album", "2006-03-29", secondary: "Compilation")
                with { TrackNumber = 7, Label = "Virgin", CatalogNumber = "COMP-1" },
        };

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);
        var song = SongFor(request);
        plan.ApplyTo(song);

        Assert.Equal("Discovery", song.Album);
        Assert.Equal(1, song.Track);
        Assert.Null(song.CatalogNumber);
        Assert.Null(song.Label);
    }

    // ---- D: the pressing the file came from, the year the album first came out ----------

    [Fact]
    public void D_PeerTaggedWithTheRemasterYear_TakesThatPressing_ShowsTheOriginalYear()
    {
        var request = Request("Nirvana", "Smells Like Teen Spirit", duration: 301);
        var evidence = Evidence(request, Peer(301, "Smells Like Teen Spirit", "Nirvana", "Nevermind", 2011), "rec-slts");
        var candidates = new List<ReleaseCandidate>
        {
            Fingerprinted("rec-slts", "Smells Like Teen Spirit", "Nirvana", "Nevermind", "g-nevermind", "Album", "1991-09-24", length: 301)
                with { Label = "DGC", CatalogNumber = "DGCD-24425" },
            Fingerprinted("rec-slts", "Smells Like Teen Spirit", "Nirvana", "Nevermind", "g-nevermind", "Album", "2011-09-19", "1991-09-24", length: 301)
                with { Label = "DGC", CatalogNumber = "B0015884-02" },
        };

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);
        var song = SongFor(request);
        plan.ApplyTo(song);

        Assert.Equal(TagConfidence.Strong, plan.Confidence);
        Assert.Equal("2011-09-19", plan.Chosen!.Candidate.ReleaseDate);
        Assert.Equal("B0015884-02", song.CatalogNumber);
        Assert.Equal(1991, song.Year);
        Assert.Equal("1991-09-24", song.OriginalDate);
        Assert.Equal("2011-09-19", plan.ToReport().ReleaseDate);
    }

    [Fact]
    public void D_YearFromOriginalReleaseOff_WritesThePressingsYear()
    {
        var request = Request("Nirvana", "Smells Like Teen Spirit", duration: 301);
        var evidence = Evidence(request, Peer(301, "Smells Like Teen Spirit", "Nirvana", "Nevermind", 2011), "rec-slts");
        var candidates = new List<ReleaseCandidate>
        {
            Fingerprinted("rec-slts", "Smells Like Teen Spirit", "Nirvana", "Nevermind", "g-nevermind", "Album", "2011-09-19", "1991-09-24", length: 301),
        };

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default with { YearFromOriginalRelease = false }, ThisYear);
        var song = SongFor(request);
        plan.ApplyTo(song);

        Assert.Equal(2011, song.Year);
        Assert.Equal("1991-09-24", song.OriginalDate);
    }

    // ---- E: a live request with no fingerprint finds the live release in the database ----

    private static (TagEvidence, List<ReleaseCandidate>) FixtureE()
    {
        var request = Request("Portishead", "Glory Box (Live)", duration: 300);
        var evidence = Evidence(request, Upload(300));
        var candidates = new List<ReleaseCandidate>
        {
            new ReleaseCandidate(TagSource.Database, "Glory Box", "Portishead")
            {
                RecordingId = "rec-studio", LengthSeconds = 300, ReleaseId = "r-dummy", ReleaseGroupId = "g-dummy",
                ReleaseTitle = "Dummy", GroupTitle = "Dummy", PrimaryType = "Album", Status = "Official",
                ReleaseDate = "1994-08-22", GroupFirstReleaseDate = "1994-08-22",
            },
            new ReleaseCandidate(TagSource.Database, "Glory Box (live, 1997-07-24: Roseland Ballroom, New York)", "Portishead")
            {
                RecordingId = "rec-live", LengthSeconds = 300, ReleaseId = "r-roseland", ReleaseGroupId = "g-roseland",
                ReleaseTitle = "Roseland NYC Live", GroupTitle = "Roseland NYC Live", PrimaryType = "Album",
                SecondaryTypes = ["Live"], Status = "Official", ReleaseDate = "1998-11-09", GroupFirstReleaseDate = "1998-11-09",
            },
        };
        return (evidence, candidates);
    }

    [Fact]
    public void E_LiveRequest_DatabaseSearch_PicksTheLiveRelease_StudioNeverReachesMedium()
    {
        var (evidence, candidates) = FixtureE();

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);

        Assert.Equal(TagConfidence.Strong, plan.Confidence);
        Assert.Equal("Roseland NYC Live", plan.Chosen!.Candidate.AlbumTitle);
        Assert.True(plan.Ranked[1].Distance > ReleaseChooser.MediumThreshold, $"studio album at {plan.Ranked[1].Distance}");
    }

    [Fact]
    public void E_FromAParsedDatabaseSearch_GivesTheSameAnswer()
    {
        using var doc = JsonDocument.Parse("""
        {"created": "2026-10-01T00:00:00.000Z", "count": 2, "offset": 0, "recordings": [
          {"id": "rec-studio", "score": 100, "title": "Glory Box", "length": 300000, "video": false,
           "artist-credit": [{"name": "Portishead", "artist": {"id": "a-p", "name": "Portishead"}}],
           "isrcs": ["GBAAA9400001"],
           "releases": [{"id": "r-dummy", "title": "Dummy", "status": "Official", "date": "1994-08-22", "country": "GB",
             "release-group": {"id": "g-dummy", "title": "Dummy", "primary-type": "Album", "first-release-date": "1994-08-22"},
             "media": [{"position": 1, "format": "CD", "track-count": 11, "track-offset": 10,
               "track": [{"id": "t-gb", "number": "11", "title": "Glory Box", "length": 300000}]}]}]},
          {"id": "rec-live", "score": 95, "title": "Glory Box", "length": 300000, "video": false,
           "disambiguation": "live, 1997-07-24: Roseland Ballroom, New York",
           "artist-credit": [{"name": "Portishead", "artist": {"id": "a-p", "name": "Portishead"}}],
           "releases": [{"id": "r-roseland", "title": "Roseland NYC Live", "status": "Official", "date": "1998-11-09", "country": "GB",
             "release-group": {"id": "g-roseland", "title": "Roseland NYC Live", "primary-type": "Album", "secondary-types": ["Live"], "first-release-date": "1998-11-09"},
             "media": [{"position": 1, "format": "CD", "track-count": 11, "track-offset": 7,
               "track": [{"id": "t-gbl", "number": "8", "title": "Glory Box", "length": 300000}]}]}]}
        ]}
        """);
        var candidates = CandidateSources.FromDatabaseSearch(doc.RootElement);
        var (evidence, _) = FixtureE();

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(TagConfidence.Strong, plan.Confidence);
        Assert.Equal("r-roseland", plan.Chosen!.Candidate.ReleaseId);
        Assert.Equal(8, plan.Chosen.Candidate.TrackNumber);
        Assert.Equal("t-gbl", plan.Chosen.Candidate.ReleaseTrackId);
        Assert.Equal(["a-p"], plan.Chosen.Candidate.ArtistIds);
        Assert.Equal(["GBAAA9400001"], plan.Ranked[1].Candidate.Isrcs);
    }

    // ---- F: every credit gets its id ----------------------------------------------------

    [Fact]
    public void F_ListOfCredits_MatchesTheJoinedCredit_WritesEveryArtistId()
    {
        var request = Request("Bizarrap, Rauw Alejandro", "Bzrp Music Sessions, Vol. 56", duration: 200);
        var evidence = Evidence(request, Upload(200), "rec-bzrp");
        var candidates = new List<ReleaseCandidate>
        {
            Fingerprinted("rec-bzrp", "Bzrp Music Sessions, Vol. 56", "Bizarrap & Rauw Alejandro", "Bzrp Music Sessions, Vol. 56", "g-bzrp", "Single", "2023-06-29", length: 200)
                with { Artists = ["Bizarrap", "Rauw Alejandro"], ArtistIds = ["a-bzrp", "a-rauw"], PrimaryArtist = "Bizarrap" },
        };

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);
        var song = SongFor(request);
        plan.ApplyTo(song);

        Assert.Equal(TagConfidence.Strong, plan.Confidence);
        Assert.Equal(["a-bzrp", "a-rauw"], song.MusicBrainzArtistIds);
        Assert.Equal(["Bizarrap", "Rauw Alejandro"], song.Artists);
        Assert.Equal("Bizarrap", song.PrimaryArtist);
        Assert.Equal("Bizarrap, Rauw Alejandro", song.Artist);
    }

    // ---- G: a catalog-only single is Medium and only fills blanks -----------------------

    [Fact]
    public void G_CatalogOnlySingle_IsMedium_AndDoesNotSetTheAlbum()
    {
        var request = Request("Artist", "Song", duration: 200);
        var evidence = Evidence(request, Upload(200));
        var meta = new DeezerMetadataService.FullTrackMeta("Song", "https://cdn/xl.jpg", 2021, 200, "Artist", 1, 1,
            "USAAA2100001", 1, "Pop", "Label", "2021-05-01", ["Artist"], "Artist", "single") { Title = "Song", TrackId = "1", AlbumId = "2" };

        var plan = ReleaseChooser.Choose(evidence, [CandidateSources.FromCatalog(meta)], MatchingSettings.Default, ThisYear);
        var song = SongFor(request);
        plan.ApplyTo(song);

        Assert.Equal(TagConfidence.Medium, plan.Confidence);
        Assert.False(plan.AlbumFromCandidate);
        Assert.Equal("", song.Album);
        Assert.Null(song.Label);
        Assert.Equal("USAAA2100001", song.Isrc);
        Assert.Contains(plan.Notes, note => note.Contains("not taken"));
    }

    // ---- I: a rip from a compilation, filed under the studio album, or not -----------------

    private static (TagEvidence, List<ReleaseCandidate>) FixtureI()
    {
        var request = Request("Massive Attack", "Teardrop", duration: 330);
        var file = Peer(330, "Teardrop", "Massive Attack", "Now That's What I Call Music! 42", compilation: true);
        var evidence = Evidence(request, file, "rec-teardrop");
        var candidates = new List<ReleaseCandidate>
        {
            Fingerprinted("rec-teardrop", "Teardrop", "Massive Attack", "Now That's What I Call Music! 42", "g-now42", "Album", "1999-04-12", secondary: "Compilation")
                with { AlbumArtist = "Various Artists", IsCompilation = true },
            Fingerprinted("rec-teardrop", "Teardrop", "Massive Attack", "Mezzanine", "g-mezzanine", "Album", "1998-04-20"),
        };
        candidates.Add(CandidateSources.FromFileTags(file, request)!);
        return (evidence, candidates);
    }

    [Fact]
    public void I_PreferOriginalAlbumOn_TheStudioAlbumReplacesTheCompilation()
    {
        var (evidence, candidates) = FixtureI();

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);
        var song = SongFor(evidence.Request);
        plan.ApplyTo(song);

        Assert.Equal(TagConfidence.Medium, plan.Confidence);
        Assert.Equal("Mezzanine", plan.Chosen!.Candidate.AlbumTitle);
        Assert.True(plan.AlbumFromCandidate);
        Assert.Equal("Mezzanine", song.Album);
        Assert.False(song.IsCompilation);
    }

    [Fact]
    public void I_PreferOriginalAlbumOff_TheCompilationStays()
    {
        var (evidence, candidates) = FixtureI();

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default with { PreferOriginalAlbum = false }, ThisYear);
        var song = SongFor(evidence.Request);
        plan.ApplyTo(song);

        Assert.Equal(TagConfidence.Medium, plan.Confidence);
        Assert.Equal("Now That's What I Call Music! 42", plan.Chosen!.Candidate.AlbumTitle);
        Assert.Equal(TagSource.Fingerprint, plan.Chosen.Candidate.Source);
        Assert.Equal("Now That's What I Call Music! 42", song.Album);
        Assert.True(song.IsCompilation);
    }

    // ---- J: a version the fingerprint does not name ---------------------------------------

    [Fact]
    public void J_RemixRequested_OriginalRecordingIsMedium_TitleNeverOverwritten()
    {
        var request = Request("Artist", "Song (Remix)", duration: 240);
        var evidence = Evidence(request, Upload(240), "rec-song");
        var candidates = new List<ReleaseCandidate>
        {
            Fingerprinted("rec-song", "Song", "Artist", "The Album", "g-album", "Album", "2015-01-01", length: 240),
        };

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default with { TagFromMatch = true }, ThisYear);
        var song = SongFor(request);
        plan.ApplyTo(song);

        Assert.Equal(TagConfidence.Medium, plan.Confidence);
        Assert.Equal("Song (Remix)", song.Title);
        Assert.Equal("The Album", song.Album);
        Assert.Equal("rec-song", song.MusicBrainzRecordingId);
    }

    // ---- the rules around the table ------------------------------------------------------

    [Fact]
    public void NoCandidates_GivesNone_AndLeavesTheSongUntouched()
    {
        var request = Request("Artist", "Song");
        var plan = ReleaseChooser.Choose(Evidence(request, Upload(0)), [], MatchingSettings.Default, ThisYear);
        var song = SongFor(request);
        song.Album = "Whatever the catalog said";

        plan.ApplyTo(song);

        Assert.Equal(TagConfidence.None, plan.Confidence);
        Assert.Null(plan.Chosen);
        Assert.Equal("Whatever the catalog said", song.Album);
        Assert.Empty(plan.Fields);
    }

    [Fact]
    public void AmbiguousPressings_KeepTheRecordingId_ButNotTheAlbum()
    {
        var request = Request("Artist", "Song", duration: 240);
        var evidence = Evidence(request, Upload(240), "rec-song");
        var candidates = new List<ReleaseCandidate>
        {
            Fingerprinted("rec-song", "Song", "Artist", "Album A", "g-a", "Album", "1998-01-01", length: 240),
            Fingerprinted("rec-song", "Song", "Artist", "Album B", "g-b", "Album", "1998-01-01", length: 240),
        };

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);
        var song = SongFor(request);
        plan.ApplyTo(song);

        Assert.Equal(TagConfidence.Ambiguous, plan.Confidence);
        Assert.Equal("", song.Album);
        Assert.Equal("rec-song", song.MusicBrainzRecordingId);
        Assert.Contains(plan.Notes, note => note.Contains("too close to call"));
    }

    [Fact]
    public void TwoPressingsOfOneGroup_AreNeverAmbiguous()
    {
        var request = Request("Artist", "Song", duration: 240);
        var evidence = Evidence(request, Upload(240), "rec-song");
        var candidates = new List<ReleaseCandidate>
        {
            Fingerprinted("rec-song", "Song", "Artist", "Album A", "g-a", "Album", "1998-01-01", length: 240),
            Fingerprinted("rec-song", "Song", "Artist", "Album A", "g-a", "Album", "1998-01-02", "1998-01-01", length: 240),
        };

        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);

        Assert.Equal(TagConfidence.Strong, plan.Confidence);
        Assert.Equal("1998-01-01", plan.Chosen!.Candidate.ReleaseDate);
    }

    [Fact]
    public void Rehearsal_WritesOnlyTheCodeAndTheConfirmedFingerprintId()
    {
        var (evidence, candidates) = FixtureA();
        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);
        var song = SongFor(evidence.Request);
        song.Verification = new VerificationResult { Verdict = VerificationVerdict.Confirmed, AcoustId = "acoustid-1" };

        plan.ApplyRehearsalTo(song);

        Assert.True(plan.Rehearsed);
        Assert.Equal("", song.Album);
        Assert.Null(song.Label);
        Assert.Null(song.Year);
        Assert.Equal("acoustid-1", song.AcoustId);
        Assert.Equal(TagConfidence.Strong, plan.ToReport().Confidence switch { "Strong" => TagConfidence.Strong, _ => TagConfidence.None });
    }

    [Fact]
    public void Report_CarriesTheTopCandidatesTheirPenaltiesAndTheFields()
    {
        var (evidence, candidates) = FixtureA();
        var plan = ReleaseChooser.Choose(evidence, candidates, MatchingSettings.Default, ThisYear);
        plan.ApplyTo(SongFor(evidence.Request));
        plan.StageSeconds["catalog"] = 1.234;
        plan.Notes.Add("a note");

        var report = plan.ToReport();

        Assert.Equal("Strong", report.Confidence);
        Assert.Equal("Mezzanine", report.ReleaseTitle);
        Assert.Equal(3, report.Candidates.Count);
        Assert.Equal("Mezzanine", report.Candidates[0].Album);
        Assert.Contains(report.Candidates[2].BiggestPenalties, p => p.StartsWith("type"));
        Assert.Equal("Mezzanine", report.Fields["album"].Value);
        Assert.Equal(1.23, report.StageSeconds["catalog"]);
        Assert.Contains("a note", report.Notes);
        var json = JsonSerializer.Serialize(report);
        var back = JsonSerializer.Deserialize<TagReport>(json);
        Assert.Equal("Mezzanine", back!.Fields["album"].Value);
    }
}
