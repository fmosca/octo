using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Octo.Services.Fingerprint;
using Octo.Services.Tagging;

namespace Octo.Tests;

/// <summary>
/// The music database's release answer, in the shape read on 2026-10-01: label-info with a
/// catalogue number and a label, the release's status, date, country and barcode, its group's
/// first release date and kinds, each medium's tracks with their ids, and voted genres. A
/// missing block is null, never a throw.
/// </summary>
public class MusicBrainzReleaseDetailsTests
{
    private const string NightAtTheOpera = """
    {
      "id": "r-nato", "title": "A Night at the Opera", "status": "Official", "date": "1975-11-21", "country": "GB",
      "barcode": "077778949224", "quality": "normal",
      "label-info": [{"catalog-number": "EMTC 103", "label": {"id": "l-emi", "name": "EMI"}}],
      "release-group": {"id": "g-nato", "title": "A Night at the Opera", "primary-type": "Album",
        "secondary-types": [], "first-release-date": "1975-11-21"},
      "artist-credit": [{"name": "Queen", "joinphrase": "", "artist": {"id": "a-queen", "name": "Queen"}}],
      "media": [{"position": 1, "format": "12\" Vinyl", "track-count": 12,
        "tracks": [
          {"id": "t-1", "position": 1, "number": "A1", "title": "Death on Two Legs", "length": 223000,
           "recording": {"id": "rec-dotl", "title": "Death on Two Legs (Dedicated to...)", "length": 223000, "isrcs": ["GBUM71029604"]}},
          {"id": "t-11", "position": 11, "number": "B5", "title": "Bohemian Rhapsody", "length": 355000,
           "recording": {"id": "rec-br", "title": "Bohemian Rhapsody", "length": 355000, "isrcs": ["GBUM71029604", "GBUM71029605"]}}
        ]}],
      "genres": [{"name": "rock", "count": 12}, {"name": "progressive rock", "count": 5}, {"name": "glam rock", "count": 1}]
    }
    """;

    [Fact]
    public void Parse_ReadsEveryField()
    {
        using var doc = JsonDocument.Parse(NightAtTheOpera);
        var details = ReleaseDetails.Parse(doc.RootElement)!;

        Assert.Equal("r-nato", details.ReleaseId);
        Assert.Equal("A Night at the Opera", details.Title);
        Assert.Equal("Official", details.Status);
        Assert.Equal("1975-11-21", details.Date);
        Assert.Equal("GB", details.Country);
        Assert.Equal("077778949224", details.Barcode);
        Assert.Equal("EMI", details.Label);
        Assert.Equal("EMTC 103", details.CatalogNumber);
        Assert.Equal("g-nato", details.GroupId);
        Assert.Equal("1975-11-21", details.GroupFirstReleaseDate);
        Assert.Equal("Album", details.PrimaryType);
        Assert.Empty(details.SecondaryTypes);
        Assert.Equal("Queen", details.AlbumArtist);
        Assert.Equal(["a-queen"], details.AlbumArtistIds);
        Assert.Equal(1, details.DiscCount);
        Assert.False(details.IsCompilation);

        var track = details.TrackFor("rec-br")!;
        Assert.Equal("t-11", track.ReleaseTrackId);
        Assert.Equal(11, track.Position);
        Assert.Equal("B5", track.Number);
        Assert.Equal(1, track.DiscNumber);
        Assert.Equal(12, track.TrackCount);
        Assert.Equal(355, track.LengthSeconds);
        Assert.Equal(["GBUM71029604", "GBUM71029605"], track.Isrcs);
        Assert.Null(details.TrackFor("rec-other"));

        Assert.Equal(["rock", "progressive rock"], details.TopGenres(minVotes: 2));
    }

    [Fact]
    public void Parse_MissingBlocks_AreNullNotAThrow()
    {
        using var doc = JsonDocument.Parse("""{"id": "r-bare", "title": "Bare"}""");
        var details = ReleaseDetails.Parse(doc.RootElement)!;

        Assert.Equal("r-bare", details.ReleaseId);
        Assert.Null(details.Label);
        Assert.Null(details.CatalogNumber);
        Assert.Null(details.Barcode);
        Assert.Null(details.GroupId);
        Assert.Null(details.Status);
        Assert.Empty(details.Tracks);
        Assert.Empty(details.Genres);
        Assert.Equal(0, details.DiscCount);
        Assert.Empty(details.TopGenres());
    }

    [Fact]
    public void Parse_LabelInfoWithoutALabel_StillReadsTheCatalogNumber()
    {
        using var doc = JsonDocument.Parse("""{"id": "r", "label-info": [{"catalog-number": "CAT-1"}, {"label": {"name": "Later"}}], "barcode": ""}""");
        var details = ReleaseDetails.Parse(doc.RootElement)!;
        Assert.Equal("CAT-1", details.CatalogNumber);
        Assert.Equal("Later", details.Label);
        Assert.Null(details.Barcode);
    }

    [Fact]
    public void Parse_NotARelease_IsNull()
    {
        using var doc = JsonDocument.Parse("""{"error": "Not Found"}""");
        Assert.Null(ReleaseDetails.Parse(doc.RootElement));
    }

    [Fact]
    public void Parse_VariousArtistsGroup_IsACompilation()
    {
        using var doc = JsonDocument.Parse("""
        {"id": "r-va", "artist-credit": [{"name": "Various Artists", "artist": {"id": "va", "name": "Various Artists"}}],
         "release-group": {"id": "g", "primary-type": "Album", "secondary-types": ["Compilation"]}}
        """);
        var details = ReleaseDetails.Parse(doc.RootElement)!;
        Assert.True(details.IsCompilation);
        Assert.Equal(["Compilation"], details.SecondaryTypes);
    }

    /// <summary>The code lookup's shape: recordings with a first-release-date and their releases.</summary>
    [Fact]
    public void FromIsrcLookup_ReadsRecordingsAndTheirReleases()
    {
        using var doc = JsonDocument.Parse("""
        {"isrc": "GBDUW0000059", "recordings": [{
          "id": "rec-omt", "title": "One More Time", "length": 320000, "first-release-date": "2000-11-13",
          "artist-credit": [{"name": "Daft Punk", "artist": {"id": "a-dp", "name": "Daft Punk"}}],
          "releases": [
            {"id": "r-disc", "title": "Discovery", "status": "Official", "date": "2001-03-12", "country": "FR", "barcode": "724384960629",
             "release-group": {"id": "g-disc", "title": "Discovery", "primary-type": "Album", "first-release-date": "2001-02-26"},
             "artist-credit": [{"name": "Daft Punk", "artist": {"id": "a-dp", "name": "Daft Punk"}}],
             "media": [{"position": 1, "track-count": 14, "track-offset": 0, "track": [{"id": "t-omt", "number": "1", "position": 1, "title": "One More Time"}]}]},
            {"id": "r-single", "title": "One More Time", "status": "Official", "date": "2000-11-13", "country": "FR",
             "release-group": {"id": "g-single", "title": "One More Time", "primary-type": "Single", "first-release-date": "2000-11-13"}}
          ]}]}
        """);

        var candidates = CandidateSources.FromIsrcLookup(doc.RootElement);

        Assert.Equal(2, candidates.Count);
        var album = candidates[0];
        Assert.Equal(TagSource.Database, album.Source);
        Assert.Equal("rec-omt", album.RecordingId);
        Assert.Equal(["GBDUW0000059"], album.Isrcs);
        Assert.Equal("Discovery", album.AlbumTitle);
        Assert.Equal("g-disc", album.ReleaseGroupId);
        Assert.Equal("2001-02-26", album.GroupFirstReleaseDate);
        Assert.Equal("724384960629", album.Barcode);
        Assert.Equal(1, album.TrackNumber);
        Assert.Equal(14, album.TrackCount);
        Assert.Equal("t-omt", album.ReleaseTrackId);
        Assert.Equal(["a-dp"], album.AlbumArtistIds);
        Assert.Equal("Single", candidates[1].PrimaryType);
        Assert.Equal(2000, candidates[1].Year);
    }

    [Fact]
    public void FromDatabaseSearch_SkipsVideos_AndReadsARecordingWithNoReleases()
    {
        using var doc = JsonDocument.Parse("""
        {"recordings": [
          {"id": "v", "title": "Video", "video": true, "artist-credit": [{"name": "A"}]},
          {"id": "r", "title": "Song", "length": 200000, "first-release-date": "1999-01-01", "artist-credit": [{"name": "A", "artist": {"id": "a1", "name": "A"}}]}
        ]}
        """);
        var candidates = CandidateSources.FromDatabaseSearch(doc.RootElement);
        var only = Assert.Single(candidates);
        Assert.Equal("r", only.RecordingId);
        Assert.Null(only.ReleaseId);
        Assert.Equal(1999, only.OriginalYear);
        Assert.Equal(200, only.LengthSeconds);
    }

    // ---- the client: cache and escaping --------------------------------------------------

    private static MusicBrainzClient Client(Func<string, HttpResponseMessage> answer, List<string> calls)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                calls.Add(req.RequestUri!.ToString());
                return answer(req.RequestUri!.ToString());
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler.Object) { BaseAddress = new Uri("https://musicbrainz.test/ws/2/") });
        return new MusicBrainzClient(factory.Object, NullLogger<MusicBrainzClient>.Instance);
    }

    [Fact]
    public async Task LookupRelease_AsksOnce_ThenAnswersFromMemory()
    {
        var calls = new List<string>();
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(NightAtTheOpera) }, calls);

        var first = await client.LookupReleaseAsync("r-nato", CancellationToken.None);
        var second = await client.LookupReleaseAsync("r-nato", CancellationToken.None);

        Assert.Same(first, second);
        var url = Assert.Single(calls);
        Assert.Contains("release/r-nato?inc=labels+release-groups+artist-credits+recordings+isrcs+genres&fmt=json", url);
    }

    [Fact]
    public async Task LookupRelease_ServerError_IsNull_AndNotRemembered()
    {
        var calls = new List<string>();
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), calls);

        Assert.Null(await client.LookupReleaseAsync("r-nato", CancellationToken.None));
        Assert.Null(await client.LookupReleaseAsync("r-nato", CancellationToken.None));
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task SearchRecordings_IsRememberedForTheSameSong()
    {
        var calls = new List<string>();
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"recordings": []}""") }, calls);

        using var first = await client.SearchRecordingsAsync("Portishead", "Glory Box (Live)", 300, CancellationToken.None);
        using var second = await client.SearchRecordingsAsync("Portishead", "Glory Box (Live)", 300, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Single(calls);
    }
}

/// <summary>
/// Titles and names with quotes, colons, slashes or brackets must reach the music database as
/// the words they are, not as operators, or the failure reads as "no candidate".
/// </summary>
public class MusicBrainzQueryTests
{
    private static string Query(string url)
    {
        var start = url.IndexOf("query=", StringComparison.Ordinal) + "query=".Length;
        var end = url.IndexOf("&fmt=json", StringComparison.Ordinal);
        return Uri.UnescapeDataString(url[start..end]);
    }

    [Theory]
    [InlineData("AC/DC", "Thunderstruck", 292, "recording:\"Thunderstruck\" AND artist:\"AC\\/DC\" AND dur:[282000 TO 302000]")]
    [InlineData("Bizarrap", "Bzrp Music Sessions, Vol. 56", 0, "recording:\"Bzrp Music Sessions, Vol. 56\" AND artist:\"Bizarrap\"")]
    [InlineData("Adele", "Hello?", 295, "recording:\"Hello\\?\" AND artist:\"Adele\" AND dur:[285000 TO 305000]")]
    [InlineData("Shawn Mendes", "Señorita: Remix", 0, "recording:\"Señorita\\: Remix\" AND artist:\"Shawn Mendes\"")]
    [InlineData("\"Weird Al\" Yankovic", "Amish Paradise", 0, "recording:\"Amish Paradise\" AND artist:\"\\\"Weird Al\\\" Yankovic\"")]
    public void BuildRecordingSearchUrl_EscapesEveryOperator(string artist, string title, int seconds, string expectedQuery) =>
        Assert.Equal(expectedQuery, Query(MusicBrainzClient.BuildRecordingSearchUrl(artist, title, seconds)));

    [Fact]
    public void BuildRecordingSearchUrl_ExactEncodedUrl() =>
        Assert.Equal(
            "recording/?query=recording%3A%22Thunderstruck%22%20AND%20artist%3A%22AC%5C%2FDC%22%20AND%20dur%3A%5B282000%20TO%20302000%5D&fmt=json&limit=25",
            MusicBrainzClient.BuildRecordingSearchUrl("AC/DC", "Thunderstruck", 292));

    [Fact]
    public void BuildRecordingSearchUrl_ShortLengthNeverGoesNegative() =>
        Assert.EndsWith("AND dur:[0 TO 15000]", Query(MusicBrainzClient.BuildRecordingSearchUrl("A", "B", 5)));

    [Theory]
    [InlineData("plain words", "plain words")]
    [InlineData("a+b-c&&d||e!f(g)h{i}j[k]l^m\"n~o*p?q:r\\s/t", "a\\+b\\-c\\&\\&d\\|\\|e\\!f\\(g\\)h\\{i\\}j\\[k\\]l\\^m\\\"n\\~o\\*p\\?q\\:r\\\\s\\/t")]
    public void EscapeQuery(string input, string expected) =>
        Assert.Equal(expected, MusicBrainzClient.EscapeQuery(input));
}
