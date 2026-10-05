using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.CoverArt;
using Octo.Services.LastFm;
using Octo.Services.Metadata;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;
using Octo.Services.YouTube;
using System.Collections.Concurrent;
using System.Net;

namespace Octo.Tests;

public class SoulseekMetadataServiceTests
{
    private readonly ExternalIdRegistry _registry = new();

    /// <summary>Every url the fake catalog was asked for.</summary>
    private readonly ConcurrentQueue<string> _calls = new();

    /// <summary>Builds the service with a Deezer layer answering from a url-substring map.
    /// YouTube is never reached by the album paths under test. Given a gate, the catalog
    /// answers nothing until it opens, so callers started together are all in flight before
    /// any hears back, however slow the machine.</summary>
    private SoulseekMetadataService BuildService(Dictionary<string, string> routes, Task? gate = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri!.ToString();
                _calls.Enqueue(url);
                if (gate is not null) await gate;
                foreach (var (needle, body) in routes)
                {
                    if (url.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler.Object));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var youtube = new YouTubeResolver(factory.Object, config, new Mock<ILogger<YouTubeResolver>>().Object);
        var deezer = new DeezerMetadataService(factory.Object,
            TestOptions.Monitor(new Octo.Models.Settings.MetadataSettings()),
            new Mock<ILogger<DeezerMetadataService>>().Object);

        var coverArt = new CoverArtAggregator(
            Array.Empty<ICoverArtSource>(), new Mock<ILogger<CoverArtAggregator>>().Object);

        return new SoulseekMetadataService(
            youtube, _registry, deezer, coverArt, new Mock<ILogger<SoulseekMetadataService>>().Object);
    }

    private const string AlbumSearchJson = @"{""data"":[
        {""id"":1,""title"":""Test Album"",""record_type"":""album"",""nb_tracks"":2,
         ""cover_xl"":""https://cdn/a.jpg"",""artist"":{""name"":""Test Artist""}}]}";

    private const string AlbumDetailJson = @"{""id"":1,""title"":""Test Album"",
        ""cover_xl"":""https://cdn/a.jpg"",""release_date"":""1997-05-21"",
        ""artist"":{""name"":""Test Artist""},""genres"":{""data"":[{""name"":""Rock""}]}}";

    private const string AlbumTracksJson = @"{""total"":2,""data"":[
        {""title"":""Track One"",""duration"":180,""track_position"":1,""disk_number"":1,""artist"":{""name"":""Test Artist""}},
        {""title"":""Track Two"",""duration"":240,""track_position"":2,""disk_number"":1,""artist"":{""name"":""Test Artist""}}]}";

    [Fact]
    public async Task SearchAlbumsAsync_ReturnsAlbumsWithRegistryIds()
    {
        // Arrange
        var svc = BuildService(new() { ["/search/album"] = AlbumSearchJson });

        // Act
        var albums = await svc.SearchAlbumsAsync("test", 10);

        // Assert
        var album = Assert.Single(albums);
        Assert.Equal("Test Album", album.Title);
        Assert.Equal("Test Artist", album.Artist);
        Assert.False(album.IsLocal);
        // The external id must be the REGISTRY id, not the Deezer id: every consumer
        // round-trips through the registry.
        Assert.Equal(album.Id, album.ExternalId);
        var routing = _registry.Lookup(album.Id);
        Assert.NotNull(routing);
        Assert.Equal(RoutingKind.Album, routing!.Kind);
        Assert.Equal("1", routing.ExternalAlbumId);
    }

    [Fact]
    public async Task GetAlbumAsync_PopulatesSongsWithAlbumIdAndRegistryIds()
    {
        // Arrange
        var svc = BuildService(new()
        {
            ["/search/album"] = AlbumSearchJson,
            ["/album/1/tracks"] = AlbumTracksJson,
            ["/album/1"] = AlbumDetailJson,
        });
        var albumId = (await svc.SearchAlbumsAsync("test", 10)).Single().Id;

        // Act
        var album = await svc.GetAlbumAsync(SoulseekMetadataService.ProviderName, albumId);

        // Assert
        Assert.NotNull(album);
        Assert.Equal(2, album!.Songs.Count);
        Assert.Equal(new[] { "Track One", "Track Two" }, album.Songs.Select(s => s.Title));
        Assert.Equal(1997, album.Year);

        foreach (var song in album.Songs)
        {
            // AlbumId is what makes the album clickable in native mode and what
            // DownloadMode.Album reads.
            Assert.Equal(albumId, song.AlbumId);
            Assert.Equal("Test Album", song.Album);
            Assert.Equal(song.Id, song.ExternalId);

            // The routing must carry the album too, or the download path re-derives it
            // from artist+title and can land on a compilation instead.
            var routing = _registry.Lookup(song.Id);
            Assert.NotNull(routing);
            Assert.Equal(RoutingKind.Song, routing!.Kind);
            Assert.Equal("Test Album", routing.Album);
        }

        Assert.Equal(180, album.Songs[0].Duration);
        Assert.Equal(1, album.Songs[0].Track);
    }

    [Fact]
    public async Task GetSongAsync_ReturnsAlbumFromRouting()
    {
        // Guards the tagging fix: the download path rebuilds a song from its id alone.
        var svc = BuildService(new()
        {
            ["/search/album"] = AlbumSearchJson,
            ["/album/1/tracks"] = AlbumTracksJson,
            ["/album/1"] = AlbumDetailJson,
        });
        var albumId = (await svc.SearchAlbumsAsync("test", 10)).Single().Id;
        var trackId = (await svc.GetAlbumAsync(SoulseekMetadataService.ProviderName, albumId))!.Songs[0].Id;

        var song = await svc.GetSongAsync(SoulseekMetadataService.ProviderName, trackId);

        Assert.NotNull(song);
        Assert.Equal("Test Album", song!.Album);
        Assert.Equal("Track One", song.Title);
    }

    [Fact]
    public async Task GetAlbumAsync_TracklistUnresolvable_StillReturnsAlbum()
    {
        // An album with no resolvable tracklist must still render rather than 404.
        var svc = BuildService(new() { ["/search/album"] = AlbumSearchJson });
        var albumId = (await svc.SearchAlbumsAsync("test", 10)).Single().Id;

        var album = await svc.GetAlbumAsync(SoulseekMetadataService.ProviderName, albumId);

        Assert.NotNull(album);
        Assert.Equal("Test Album", album!.Title);
        Assert.Empty(album.Songs);
    }

    [Fact]
    public async Task GetAlbumAsync_DeezerKnowsTheAlbumButFailsToAnswer_ListsNoFiledSongs()
    {
        // A partial list during a Deezer outage would be cached as the whole album by a
        // client that syncs, so the songs filed under it are only a stand-in when Deezer
        // has no such album at all.
        var svc = BuildService(new() { ["/search/album"] = AlbumSearchJson });
        var albumId = (await svc.SearchAlbumsAsync("test", 10)).Single().Id;
        _registry.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Song, Artist = "Test Artist", Title = "Track One", Album = "Test Album", Duration = 200,
        });

        var album = await svc.GetAlbumAsync(SoulseekMetadataService.ProviderName, albumId);

        Assert.NotNull(album);
        Assert.Empty(album!.Songs);
    }

    private const string DeezerNoData =
        @"{""error"":{""type"":""DataException"",""message"":""no data"",""code"":800}}";

    private const string DeezerQuota =
        @"{""error"":{""type"":""Exception"",""message"":""Quota limit exceeded"",""code"":4}}";

    /// <summary>An album row opened with one song filed under it, the catalog answering the
    /// album and its tracklist with these.</summary>
    private async Task<Album> OpenAlbumWithAFiledSong(string albumJson, string tracksJson)
    {
        var svc = BuildService(new()
        {
            ["/search/album"] = AlbumSearchJson,
            ["/album/1/tracks"] = tracksJson,
            ["/album/1"] = albumJson,
        });
        var albumId = (await svc.SearchAlbumsAsync("test", 10)).Single().Id;
        _registry.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Song, Artist = "Test Artist", Title = "Track One", Album = "Test Album", Duration = 200,
        });
        return (await svc.GetAlbumAsync(SoulseekMetadataService.ProviderName, albumId))!;
    }

    [Fact]
    public async Task GetAlbumAsync_DeezerHasNoSuchAlbum_ListsTheFiledSongs()
    {
        // Deezer answering that the album does not exist used to read the same as an outage,
        // so the album opened empty though Octo had shown a song under it (#59).
        var album = await OpenAlbumWithAFiledSong(DeezerNoData, AlbumTracksJson);

        Assert.Equal(["Track One"], album.Songs.Select(s => s.Title));
        Assert.Equal(1, album.SongCount);
    }

    [Fact]
    public async Task GetAlbumAsync_DeezerListsNoTracksForTheAlbum_ListsTheFiledSongs()
    {
        var album = await OpenAlbumWithAFiledSong(AlbumDetailJson, @"{""data"":[]}");

        Assert.Equal(["Track One"], album.Songs.Select(s => s.Title));
    }

    [Fact]
    public async Task GetAlbumAsync_DeezerSaysTheAlbumHasNoTracks_ListsTheFiledSongs()
    {
        var album = await OpenAlbumWithAFiledSong(
            @"{""id"":1,""title"":""Test Album"",""nb_tracks"":0,""artist"":{""name"":""Test Artist""}}",
            @"{""total"":0,""data"":[]}");

        Assert.Equal(["Track One"], album.Songs.Select(s => s.Title));
        Assert.Equal("Test Album", album.Title);
    }

    [Fact]
    public async Task GetAlbumAsync_TracklistThrottled_ListsNoFiledSongs()
    {
        // Deezer knows the album and only failed to answer: a partial list would stick.
        var album = await OpenAlbumWithAFiledSong(AlbumDetailJson, DeezerQuota);

        Assert.Empty(album.Songs);
    }

    [Fact]
    public async Task GetAlbumAsync_UnknownId_ReturnsNull()
    {
        var svc = BuildService(new());

        Assert.Null(await svc.GetAlbumAsync(SoulseekMetadataService.ProviderName, "nope"));
    }

    [Fact]
    public async Task GetAlbumAsync_WrongProvider_ReturnsNull()
    {
        var svc = BuildService(new() { ["/search/album"] = AlbumSearchJson });
        var albumId = (await svc.SearchAlbumsAsync("test", 10)).Single().Id;

        Assert.Null(await svc.GetAlbumAsync("deezer", albumId));
    }

    /// <summary>The album a song row names for a song Deezer cannot place: the row's own
    /// title, minted by the response builder exactly as getSong and search3 mint it.</summary>
    private string AlbumIdFromSongRow(string songId, string artist, string title)
    {
        var builder = new SubsonicResponseBuilder(_registry, Options.Create(new SubsonicSettings()));
        var row = builder.ConvertSongToJson(new Song
        {
            Id = songId, Artist = artist, Title = title, Album = "", Duration = 151, IsLocal = false,
        });
        return (string)row["parent"];
    }

    [Fact]
    public async Task GetAlbumAsync_SongRowAlbumDeezerCannotName_ListsTheSongThatNamedIt()
    {
        // Issue #59: an upload Deezer does not know gets a single-style album named after
        // itself. getAlbum answered it with no songs, and Tempo crashed opening the player.
        var svc = BuildService(new());
        var songId = _registry.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Song, YouTubeId = "yt-raya", Artist = "Phonk", Title = "Zericxxn - Raya", Duration = 151,
        });
        var albumId = AlbumIdFromSongRow(songId, "Phonk", "Zericxxn - Raya");

        var album = await svc.GetAlbumAsync(SoulseekMetadataService.ProviderName, albumId);

        Assert.NotNull(album);
        var song = Assert.Single(album!.Songs);
        Assert.Equal(songId, song.Id);
        Assert.Equal(albumId, song.AlbumId);
        Assert.Equal(album.Title, song.Album);
        Assert.Equal(1, album.SongCount);
    }

    [Fact]
    public async Task GetAlbumAsync_SongRowAlbum_ListsEachRecordingOnceUnderItsNewestId()
    {
        // The same upload minted twice (a lookup found its video, so its id changed) is one song.
        var svc = BuildService(new());
        var older = _registry.Register(new SoulseekRouting
            { Kind = RoutingKind.Song, Artist = "Phonk", Title = "Zericxxn - Raya", Duration = 180 });
        var newer = _registry.Register(new SoulseekRouting
            { Kind = RoutingKind.Song, YouTubeId = "yt-raya", Artist = "Phonk", Title = "Zericxxn - Raya", Duration = 151 });
        _registry.Register(new SoulseekRouting
            { Kind = RoutingKind.Song, Artist = "Someone Else", Title = "Zericxxn - Raya", Duration = 151 });
        var albumId = AlbumIdFromSongRow(newer, "Phonk", "Zericxxn - Raya");

        var album = await svc.GetAlbumAsync(SoulseekMetadataService.ProviderName, albumId);

        var song = Assert.Single(album!.Songs);
        Assert.Equal(newer, song.Id);
        Assert.NotEqual(older, song.Id);
    }

    // ---- Which catalog artist an artist page lists ----------------------------------
    // The page used to trust the first search hit, and two artists can share a name.

    private string OutsideArtist(string name, string? deezerId = null) => _registry.Register(new SoulseekRouting
    {
        Kind = RoutingKind.Artist, Artist = name, ExternalArtistId = deezerId,
    });

    private static string Releases(params string[] titles) =>
        @"{""data"":[" + string.Join(",", titles.Select((title, i) =>
            $@"{{""id"":{900 + i},""title"":""{title}"",""record_type"":""album"",""release_date"":""200{i}-01-01"",""nb_tracks"":10}}")) + "]}";

    [Fact]
    public async Task GetArtistAlbums_SkipsABiggerActWhoseNameContainsThisOne()
    {
        var svc = BuildService(new()
        {
            ["/search/artist"] = @"{""data"":[
                {""id"":111,""name"":""Test Artist Orchestra"",""nb_fan"":90000},
                {""id"":222,""name"":""Test Artist"",""nb_fan"":10}]}",
            ["/artist/111/albums"] = Releases("Wrong Record"),
            ["/artist/222/albums"] = Releases("Right Record"),
        });

        var albums = await svc.GetArtistAlbumsAsync(SoulseekMetadataService.ProviderName, OutsideArtist("Test Artist"));

        Assert.Equal(["Right Record"], albums.Select(a => a.Title));
    }

    [Fact]
    public async Task GetArtistAlbums_OfTwoArtistsOfOneName_TheMoreFollowedAndRemembersIt()
    {
        var svc = BuildService(new()
        {
            ["/search/artist"] = @"{""data"":[
                {""id"":111,""name"":""Nirvana"",""nb_fan"":40},
                {""id"":222,""name"":""Nirvana"",""nb_fan"":9000000}]}",
            ["/artist/111/albums"] = Releases("Local Anaesthetic"),
            ["/artist/222/albums"] = Releases("Nevermind"),
        });
        var id = OutsideArtist("Nirvana");

        var albums = await svc.GetArtistAlbumsAsync(SoulseekMetadataService.ProviderName, id);

        Assert.Equal(["Nevermind"], albums.Select(a => a.Title));
        // Kept on the artist, so the next visit asks for no name search.
        Assert.Equal("222", _registry.Lookup(id)!.ExternalArtistId);
    }

    [Fact]
    public async Task GetArtistAlbums_AnIdAlreadyKnown_WinsOverANameSearch()
    {
        // The artist the user tapped is the less followed one of the name.
        var svc = BuildService(new()
        {
            ["/search/artist"] = @"{""data"":[
                {""id"":222,""name"":""Nirvana"",""nb_fan"":9000000},
                {""id"":111,""name"":""Nirvana"",""nb_fan"":40}]}",
            ["/artist/111/albums"] = Releases("Local Anaesthetic"),
            ["/artist/222/albums"] = Releases("Nevermind"),
        });

        var albums = await svc.GetArtistAlbumsAsync(SoulseekMetadataService.ProviderName, OutsideArtist("Nirvana", "111"));

        Assert.Equal(["Local Anaesthetic"], albums.Select(a => a.Title));
    }

    [Fact]
    public async Task GetArtistAlbums_ALibraryArtist_IsTheOneSharingItsAlbums()
    {
        // The library holds the less followed Nirvana. Its albums say so, even over an id a
        // search remembered for the name.
        var svc = BuildService(new()
        {
            ["/search/artist"] = @"{""data"":[
                {""id"":222,""name"":""Nirvana"",""nb_fan"":9000000},
                {""id"":111,""name"":""Nirvana"",""nb_fan"":40}]}",
            ["/artist/111/albums"] = Releases("Local Anaesthetic", "Dedicated to Markos III"),
            ["/artist/222/albums"] = Releases("Nevermind", "In Utero"),
        });
        var id = OutsideArtist("Nirvana", "222");

        var albums = await svc.GetArtistAlbumsAsync(SoulseekMetadataService.ProviderName, id,
            ["Local Anaesthetic"]);

        Assert.Contains("Dedicated to Markos III", albums.Select(a => a.Title));
        Assert.DoesNotContain("Nevermind", albums.Select(a => a.Title));
        // The library page's choice is its own. The artist's routing is everyone's.
        Assert.Equal("222", _registry.Lookup(id)!.ExternalArtistId);

        // Kept for the page's next visit all the same: no name search this time.
        var searches = _calls.Count(c => c.Contains("/search/artist"));
        var again = await svc.GetArtistAlbumsAsync(SoulseekMetadataService.ProviderName, id,
            ["Local Anaesthetic"]);
        Assert.Equal(albums.Select(a => a.Title), again.Select(a => a.Title));
        Assert.Equal(searches, _calls.Count(c => c.Contains("/search/artist")));
    }

    [Fact]
    public async Task GetArtistAlbums_ALibraryPagesNamesake_StaysOnThatPage()
    {
        // One listener's library holds the less followed Nirvana. Their library page picking
        // that artist used to write it onto the name's shared routing, and every listener's
        // outside page and search row for "Nirvana" then showed the library's artist.
        var svc = BuildService(new()
        {
            ["/search/artist"] = @"{""data"":[
                {""id"":222,""name"":""Nirvana"",""nb_fan"":9000000,""picture_xl"":""https://cdn/us.jpg""},
                {""id"":111,""name"":""Nirvana"",""nb_fan"":40,""picture_xl"":""https://cdn/uk.jpg""}]}",
            ["/artist/111/albums"] = Releases("Local Anaesthetic"),
            ["/artist/222/albums"] = Releases("Nevermind"),
        });
        // The library page: a name search, then the albums with the library's titles.
        var id = (await svc.SearchArtistsAsync("Nirvana", 5)).Single().Id;
        var library = await svc.GetArtistAlbumsAsync(SoulseekMetadataService.ProviderName, id, ["Local Anaesthetic"]);
        Assert.Equal(["Local Anaesthetic"], library.Select(a => a.Title));

        // Another listener's outside page for the name, and their search row.
        var outside = await svc.GetArtistAlbumsAsync(SoulseekMetadataService.ProviderName, id);
        var row = (await svc.SearchArtistsAsync("Nirvana", 5)).Single();

        Assert.Equal(["Nevermind"], outside.Select(a => a.Title));
        Assert.Equal("https://cdn/us.jpg", row.ImageUrl);
        Assert.Equal("222", _registry.Lookup(id)!.ExternalArtistId);
    }

    [Fact]
    public async Task SearchAlbums_ALessFollowedNamesakesAlbum_DoesNotDecideTheName()
    {
        // Two artists share a name. An album row and the album it opens name the catalog
        // artist who made it, and writing that onto the name's shared artist entry let the
        // first search to show the obscure one's album decide "Nirvana" for every listener,
        // across restarts.
        var svc = BuildService(new()
        {
            ["/search/album"] = @"{""data"":[
                {""id"":5,""title"":""Local Anaesthetic"",""record_type"":""album"",""nb_tracks"":1,
                 ""artist"":{""id"":111,""name"":""Nirvana""}}]}",
            ["/album/5/tracks"] = @"{""total"":1,""data"":[
                {""title"":""Modus Vivendi"",""duration"":200,""track_position"":1,""disk_number"":1,""artist"":{""name"":""Nirvana""}}]}",
            ["/album/5"] = @"{""id"":5,""title"":""Local Anaesthetic"",""release_date"":""1971-01-01"",
                ""artist"":{""id"":111,""name"":""Nirvana""}}",
            ["/search/artist"] = @"{""data"":[
                {""id"":222,""name"":""Nirvana"",""nb_fan"":9000000,""picture_xl"":""https://cdn/us.jpg""},
                {""id"":111,""name"":""Nirvana"",""nb_fan"":40,""picture_xl"":""https://cdn/uk.jpg""}]}",
            ["/artist/111/albums"] = Releases("Local Anaesthetic"),
            ["/artist/222/albums"] = Releases("Nevermind"),
        });

        var album = Assert.Single(await svc.SearchAlbumsAsync("local anaesthetic", 10));
        await svc.GetAlbumAsync(SoulseekMetadataService.ProviderName, album.Id);

        var row = (await svc.SearchArtistsAsync("Nirvana", 5)).Single();
        var outside = await svc.GetArtistAlbumsAsync(SoulseekMetadataService.ProviderName, album.ArtistId!);

        Assert.Equal(album.ArtistId, row.Id);
        Assert.Equal("https://cdn/us.jpg", row.ImageUrl);
        Assert.Equal(["Nevermind"], outside.Select(a => a.Title));
        Assert.Equal("222", _registry.Lookup(album.ArtistId!)!.ExternalArtistId);
    }

    [Fact]
    public async Task SearchAlbums_AnArtistAlreadySettled_KeepsItsChoice()
    {
        var svc = BuildService(new()
        {
            ["/search/album"] = @"{""data"":[
                {""id"":5,""title"":""Local Anaesthetic"",""record_type"":""album"",""nb_tracks"":6,
                 ""artist"":{""id"":111,""name"":""Nirvana""}}]}",
        });
        var id = OutsideArtist("Nirvana", "222");

        var album = Assert.Single(await svc.SearchAlbumsAsync("local anaesthetic", 10));

        Assert.Equal(id, album.ArtistId);
        Assert.Equal("222", _registry.Lookup(id)!.ExternalArtistId);
    }

    [Fact]
    public async Task ArtistPage_OpenedWithTwoRequestsAtOnce_WalksTheCatalogOnce()
    {
        // Feishin opens an artist page with the artist and its album list at the same moment,
        // and a second client may open it too. Each request walked the catalog on its own:
        // 44 calls for one page, against a limit of 30 every 5 seconds.
        var titles = Enumerable.Range(0, 30).Select(i => $"Record {i}").ToArray();
        var catalogAnswers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var svc = BuildService(new()
        {
            ["/search/artist"] = @"{""data"":[{""id"":222,""name"":""Busy"",""nb_fan"":9}]}",
            ["/artist/222/albums"] = @"{""data"":[" + string.Join(",", titles.Select((t, i) =>
                $@"{{""id"":{1000 + i},""title"":""{t}"",""record_type"":""album"",""release_date"":""2001-01-01""}}")) + "]}",
            ["/album/1"] = @"{""nb_tracks"":10}",
        }, catalogAnswers.Task);
        var id = OutsideArtist("Busy");

        var pages = new[]
        {
            svc.GetArtistAsync(SoulseekMetadataService.ProviderName, id).ContinueWith(_ =>
                svc.GetArtistAlbumsKnownCountsAsync(SoulseekMetadataService.ProviderName, id)).Unwrap(),
            svc.GetArtistAlbumsAsync(SoulseekMetadataService.ProviderName, id),
            svc.GetArtistAlbumsAsync(SoulseekMetadataService.ProviderName, id),
        };
        // All three are asking before the catalog answers any of them.
        catalogAnswers.SetResult();
        var lists = await Task.WhenAll(pages);

        // One walk: the name search, the artist's listing, and the own records of the first
        // 20 albums without a count. 22 calls, where each request used to make its own.
        Assert.Equal(1, _calls.Count(c => c.Contains("/search/artist")));
        Assert.Equal(1, _calls.Count(c => c.Contains("/artist/222/albums")));
        Assert.Equal(20, _calls.Count(c => c.Contains("/album/1")));
        Assert.Equal(22, _calls.Count);
        Assert.All(lists, list => Assert.Equal(30, list.Count));
        // Both page lists show the counts that came back, and each has albums of its own.
        Assert.Equal(20, lists[1].Count(a => a.SongCount == 10));
        Assert.NotSame(lists[1][0], lists[2][0]);
    }

    [Fact]
    public async Task SearchArtists_TwoArtistsOfOneName_AreOneRowForTheMoreFollowed()
    {
        // They would get one id, and two rows opening one page only confuse.
        var svc = BuildService(new()
        {
            ["/search/artist"] = @"{""data"":[
                {""id"":111,""name"":""Nirvana"",""nb_fan"":40,""picture_xl"":""https://cdn/uk.jpg""},
                {""id"":222,""name"":""Nirvana"",""nb_fan"":9000000,""picture_xl"":""https://cdn/us.jpg""},
                {""id"":333,""name"":""Nirvana Tribute"",""nb_fan"":5}]}",
        });

        var artists = await svc.SearchArtistsAsync("nirvana", 10);

        Assert.Equal(["Nirvana", "Nirvana Tribute"], artists.Select(a => a.Name));
        Assert.Equal("https://cdn/us.jpg", artists[0].ImageUrl);
        Assert.Equal("222", _registry.Lookup(artists[0].Id)!.ExternalArtistId);
    }

    [Fact]
    public async Task OutsideAlbums_SayWhatKindOfReleaseTheyAre()
    {
        var svc = BuildService(new()
        {
            ["/search/album"] = AlbumSearchJson,
            ["/album/1/tracks"] = AlbumTracksJson,
            // The album's own record calls it an EP, and opening it says so.
            ["/album/1"] = AlbumDetailJson.Replace(@"""id"":1,", @"""id"":1,""record_type"":""ep"","),
            ["/search/artist"] = @"{""data"":[{""id"":444,""name"":""Test Artist""}]}",
            ["/artist/444/albums"] = @"{""data"":[
                {""id"":5,""title"":""A Single"",""record_type"":""single"",""release_date"":""2020-01-01"",""nb_tracks"":1},
                {""id"":6,""title"":""Odd One"",""record_type"":""mixtape"",""release_date"":""2019-01-01"",""nb_tracks"":9},
                {""id"":7,""title"":""Best Of"",""record_type"":""compile"",""release_date"":""2018-01-01"",""nb_tracks"":20}]}",
        });

        var found = Assert.Single(await svc.SearchAlbumsAsync("test", 10));
        Assert.Equal(["album"], found.ReleaseTypes);

        var opened = await svc.GetAlbumAsync(SoulseekMetadataService.ProviderName, found.Id);
        Assert.Equal(["ep"], opened!.ReleaseTypes);

        var page = await svc.GetArtistAlbumsAsync(SoulseekMetadataService.ProviderName, OutsideArtist("Test Artist"));
        Assert.Equal(["single"], page.Single(a => a.Title == "A Single").ReleaseTypes);
        // The catalog's "compile", as MusicBrainz and so Navidrome file one: an album that is
        // a compilation. Still listed after the singles.
        Assert.Equal(["album", "compilation"], page.Single(a => a.Title == "Best Of").ReleaseTypes);
        Assert.Equal(["A Single", "Best Of", "Odd One"], page.Select(a => a.Title));
        // A type OpenSubsonic has no name for is left unsaid rather than guessed.
        Assert.Empty(page.Single(a => a.Title == "Odd One").ReleaseTypes);
    }

    // ---- Artist page data: related, top, biography ----------------------------------

    private const string RelatedJson = @"{""data"":[
        {""id"":31,""name"":""Metronomy, The Others"",""picture_medium"":""https://cdn/m.jpg"",""nb_fan"":5},
        {""id"":33,""name"":""Foals""}]}";

    private const string TopJson = @"{""data"":[
        {""id"":601,""title"":""Lisztomania"",""duration"":260,""album"":{""title"":""Wolfgang Amadeus Phoenix""}},
        {""id"":602,""title"":""1901""}]}";

    private const string ArtistSearchJson = @"{""data"":[
        {""id"":15,""name"":""Phoenix"",""nb_fan"":100000}]}";

    [Fact]
    public async Task RelatedArtists_ResolvesTheIdByNameAndRegistersRows()
    {
        var svc = BuildService(new()
        {
            ["/search/artist"] = ArtistSearchJson,
            ["/artist/15/related"] = RelatedJson,
        });
        var id = OutsideArtist("Phoenix");

        var related = await svc.RelatedArtistsAsync(SoulseekMetadataService.ProviderName, id);

        Assert.NotNull(related);
        Assert.Equal(["Metronomy, The Others", "Foals"], related!.Select(a => a.Name));
        // Tapping a row opens that artist's page: a real catalog id on the routing.
        var row = _registry.Lookup(related[0].Id)!;
        Assert.Equal(RoutingKind.Artist, row.Kind);
        Assert.Equal("31", row.ExternalArtistId);
        // Name search done once; the next visit asks for no search again.
        Assert.Equal("15", _registry.Lookup(id)!.ExternalArtistId);
        var searches = _calls.Count(c => c.Contains("/search/artist"));
        await svc.RelatedArtistsAsync(SoulseekMetadataService.ProviderName, id);
        Assert.Equal(searches, _calls.Count(c => c.Contains("/search/artist")));
    }

    [Fact]
    public async Task RelatedArtists_TopTracksOrAlienProvider_AnswerNull()
    {
        var svc = BuildService(new());
        Assert.Null(await svc.RelatedArtistsAsync("deezer", "whatever"));
        // A song id is not an artist page.
        var songId = _registry.Register(new SoulseekRouting { Artist = "Phoenix", Title = "Lisztomania" });
        Assert.Null(await svc.RelatedArtistsAsync(SoulseekMetadataService.ProviderName, songId));
    }

    [Fact]
    public async Task TopTracks_AnUnresolvedId_SearchesOnceAndCarriesDurations()
    {
        var svc = BuildService(new()
        {
            ["/search/artist"] = ArtistSearchJson,
            ["/artist/15/top"] = TopJson,
        });
        var id = OutsideArtist("Phoenix");

        var top = await svc.TopTracksAsync(SoulseekMetadataService.ProviderName, id, 2);

        Assert.NotNull(top);
        Assert.Equal(["Lisztomania", "1901"], top!.Select(s => s.Title));
        Assert.Equal(["Wolfgang Amadeus Phoenix", ""], top.Select(s => s.Album));
        // Catalog length is carried and remembered as the display length, so the scrub bar
        // is right from the first render.
        Assert.Equal(260, top[0].Duration);
        Assert.Equal(260, _registry.Lookup(top[0].Id) is { } r ? SongLength.Shown(r).Seconds : null);
        // Rows play like every outside song: a song routing resolved at play time.
        Assert.Equal(RoutingKind.Song, _registry.Lookup(top[0].Id)!.Kind);
        // The remembered id skips the name search on the next visit.
        Assert.Equal("15", _registry.Lookup(id)!.ExternalArtistId);
    }

    [Fact]
    public async Task TopTracks_ANonArtistId_AnswersNull()
    {
        var svc = BuildService(new());
        Assert.Null(await svc.TopTracksAsync("spotify", "whatever"));
    }

    [Fact]
    public async Task Biography_ResolvesThroughTheCatalogNameAndReadsLastFm()
    {
        var svc = BuildService(new()
        {
            ["/search/artist"] = ArtistSearchJson,
            ["/artist/15/related"] = RelatedJson,
        });
        var id = OutsideArtist("Phoenix");
        var lastFm = BuildLastFm(new()
        {
            ["method=artist.getinfo"] = @"{""artist"":{""name"":""Phoenix"",
                ""bio"":{""content"":""A long biography."",""summary"":""A short one.""}}}",
        });

        var bio = await svc.BiographyAsync(SoulseekMetadataService.ProviderName, id, lastFm);

        Assert.NotNull(bio);
        Assert.Equal("A long biography.", bio!.Value.Biography);
        Assert.Equal("A short one.", bio.Value.Summary);
    }

    [Fact]
    public async Task Biography_ANameOfAnotherSpelling_UsesTheCatalogsName()
    {
        // The routing carries the stylized display form; the catalog's row is "Phoen!x".
        // The bio is asked for the catalog's spelling, never the display form's near miss.
        var svc = BuildService(new()
        {
            ["/search/artist"] = @"{""data"":[{""id"":44,""name"":""Phoen!x"",""nb_fan"":10}]}",
        });
        var id = OutsideArtist("Phoen!x");
        string? askedFor = null;
        var lastFm = BuildLastFm(new() { ["method=artist.getinfo"] = @"{""artist"":{""name"":""Phoen!x"",""bio"":{""content"":""Bio.""}}}" });

        var bio = await svc.BiographyAsync(SoulseekMetadataService.ProviderName, id, lastFm);

        Assert.NotNull(bio);
        Assert.Equal("Bio.", bio!.Value.Biography);
        // The last.fm call went out with the catalog's own name for the id.
        askedFor = _calls.First(c => c.Contains("artist.getinfo"));
        Assert.Contains(Uri.EscapeDataString("Phoen!x"), askedFor);
        Assert.DoesNotContain(Uri.EscapeDataString("Phoenix"), askedFor);
    }

    [Fact]
    public async Task Biography_MissingKeyOrMissingBio_AnswersEmpty()
    {
        var svc = BuildService(new());
        var noKey = BuildLastFm(new());
        Assert.Equal(("", ""), await svc.BiographyAsync(
            SoulseekMetadataService.ProviderName, OutsideArtist("Phoenix"), noKey) ?? ("", ""));
        var withKey = BuildLastFm(new()
        {
            ["method=artist.getinfo"] = @"{""artist"":{""name"":""Phoenix"",""bio"":{""content"":""""}}}",
        });
        Assert.Equal(("", ""), await svc.BiographyAsync(
            SoulseekMetadataService.ProviderName, OutsideArtist("Phoenix"), withKey) ?? ("", ""));
    }

    /// <summary>A Last.fm service answering biography calls from a url map. Its key is set,
    /// so the callers under test take the biography branch.</summary>
    private LastFmService BuildLastFm(Dictionary<string, string> routes)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage req, CancellationToken _) =>
            {
                var url = req.RequestUri!.ToString();
                _calls.Enqueue(url);
                foreach (var (needle, body) in routes)
                    if (url.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                            { Content = new StringContent(body) });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(@"{""error"":6,""message"":""not found""}") });
            });

        var settings = TestOptions.Monitor(new LastFmSettings { ApiKey = "test-key" });
        var meta = Microsoft.Extensions.Options.Options.Create(new Octo.Models.Settings.MetadataSettings());
        return new LastFmService(new HttpClient(handler.Object), settings, meta,
            new Mock<ILogger<LastFmService>>().Object);
    }
}
