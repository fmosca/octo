using Microsoft.AspNetCore.Mvc;
using Octo.Models.Domain;
using Octo.Services.Subsonic;
using System.Text.Json;
using System.Xml.Linq;

namespace Octo.Tests;

public class SubsonicResponseBuilderTests
{
    private readonly SubsonicResponseBuilder _builder;

    public SubsonicResponseBuilderTests()
    {
        _builder = new SubsonicResponseBuilder(new Octo.Services.Soulseek.ExternalIdRegistry(), Microsoft.Extensions.Options.Options.Create(new Octo.Models.Settings.SubsonicSettings()));
    }

    [Fact]
    public void CreateResponse_JsonFormat_ReturnsJsonWithOkStatus()
    {
        // Act
        var result = _builder.CreateResponse("json", "testElement", new { });

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        Assert.NotNull(jsonResult.Value);
        
        // Serialize and deserialize to check structure
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString());
        Assert.Equal("1.16.1", doc.RootElement.GetProperty("subsonic-response").GetProperty("version").GetString());
    }

    [Fact]
    public void CreateResponse_XmlFormat_ReturnsXmlWithOkStatus()
    {
        // Act
        var result = _builder.CreateResponse("xml", "testElement", new { });

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/xml", contentResult.ContentType);
        
        var doc = XDocument.Parse(contentResult.Content!);
        var root = doc.Root!;
        Assert.Equal("subsonic-response", root.Name.LocalName);
        Assert.Equal("ok", root.Attribute("status")?.Value);
        Assert.Equal("1.16.1", root.Attribute("version")?.Value);
    }

    [Fact]
    public void CreateError_JsonFormat_ReturnsJsonWithError()
    {
        // Act
        var result = _builder.CreateError("json", 70, "Test error message");

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var response = doc.RootElement.GetProperty("subsonic-response");
        
        Assert.Equal("failed", response.GetProperty("status").GetString());
        Assert.Equal(70, response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("Test error message", response.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void CreateError_XmlFormat_ReturnsXmlWithError()
    {
        // Act
        var result = _builder.CreateError("xml", 70, "Test error message");

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/xml", contentResult.ContentType);
        
        var doc = XDocument.Parse(contentResult.Content!);
        var root = doc.Root!;
        Assert.Equal("failed", root.Attribute("status")?.Value);
        
        var ns = root.GetDefaultNamespace();
        var errorElement = root.Element(ns + "error");
        Assert.NotNull(errorElement);
        Assert.Equal("70", errorElement.Attribute("code")?.Value);
        Assert.Equal("Test error message", errorElement.Attribute("message")?.Value);
    }

    [Fact]
    public void CreateSongResponse_JsonFormat_ReturnsSongData()
    {
        // Arrange
        var song = new Song
        {
            Id = "song123",
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "Test Album",
            Duration = 180,
            Track = 5,
            Year = 2023,
            Genre = "Rock",
            LocalPath = "/music/test.mp3"
        };

        // Act
        var result = _builder.CreateSongResponse("json", song);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var songData = doc.RootElement.GetProperty("subsonic-response").GetProperty("song");
        
        Assert.Equal("song123", songData.GetProperty("id").GetString());
        Assert.Equal("Test Song", songData.GetProperty("title").GetString());
        Assert.Equal("Test Artist", songData.GetProperty("artist").GetString());
        Assert.Equal("Test Album", songData.GetProperty("album").GetString());
    }

    [Fact]
    public void CreateSongResponse_XmlFormat_ReturnsSongData()
    {
        // Arrange
        var song = new Song
        {
            Id = "song123",
            Title = "Test Song",
            Artist = "Test Artist",
            Album = "Test Album",
            Duration = 180
        };

        // Act
        var result = _builder.CreateSongResponse("xml", song);

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/xml", contentResult.ContentType);
        
        var doc = XDocument.Parse(contentResult.Content!);
        var ns = doc.Root!.GetDefaultNamespace();
        var songElement = doc.Root!.Element(ns + "song");
        Assert.NotNull(songElement);
        Assert.Equal("song123", songElement.Attribute("id")?.Value);
        Assert.Equal("Test Song", songElement.Attribute("title")?.Value);
    }

    [Fact]
    public void CreateAlbumResponse_JsonFormat_ReturnsAlbumWithSongs()
    {
        // Arrange
        var album = new Album
        {
            Id = "album123",
            Title = "Test Album",
            Artist = "Test Artist",
            Year = 2023,
            Songs = new List<Song>
            {
                new Song { Id = "song1", Title = "Song 1", Duration = 180 },
                new Song { Id = "song2", Title = "Song 2", Duration = 200 }
            }
        };

        // Act
        var result = _builder.CreateAlbumResponse("json", album);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var albumData = doc.RootElement.GetProperty("subsonic-response").GetProperty("album");
        
        Assert.Equal("album123", albumData.GetProperty("id").GetString());
        Assert.Equal("Test Album", albumData.GetProperty("name").GetString());
        Assert.Equal(2, albumData.GetProperty("songCount").GetInt32());
        Assert.Equal(380, albumData.GetProperty("duration").GetInt32());
    }

    [Fact]
    public void CreateAlbumResponse_XmlFormat_ReturnsAlbumWithSongs()
    {
        // Arrange
        var album = new Album
        {
            Id = "album123",
            Title = "Test Album",
            Artist = "Test Artist",
            SongCount = 2,
            Songs = new List<Song>
            {
                new Song { Id = "song1", Title = "Song 1" },
                new Song { Id = "song2", Title = "Song 2" }
            }
        };

        // Act
        var result = _builder.CreateAlbumResponse("xml", album);

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/xml", contentResult.ContentType);
        
        var doc = XDocument.Parse(contentResult.Content!);
        var ns = doc.Root!.GetDefaultNamespace();
        var albumElement = doc.Root!.Element(ns + "album");
        Assert.NotNull(albumElement);
        Assert.Equal("album123", albumElement.Attribute("id")?.Value);
        Assert.Equal("2", albumElement.Attribute("songCount")?.Value);
    }

    [Fact]
    public void CreateAlbumResponse_XmlFormat_DerivesSongCountFromTracklist()
    {
        // An external album knows its count from the tracklist, not SongCount. The XML
        // branch used to report 0 in that case while the JSON branch reported the real
        // number, so an XML client saw an empty-looking album.
        var album = new Album
        {
            Id = "album123",
            Title = "Test Album",
            Artist = "Test Artist",
            Songs = new List<Song>
            {
                new Song { Id = "song1", Title = "Song 1" },
                new Song { Id = "song2", Title = "Song 2" },
                new Song { Id = "song3", Title = "Song 3" }
            }
        };

        // Act
        var result = _builder.CreateAlbumResponse("xml", album);

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        var doc = XDocument.Parse(contentResult.Content!);
        var ns = doc.Root!.GetDefaultNamespace();
        var albumElement = doc.Root!.Element(ns + "album");
        Assert.NotNull(albumElement);
        Assert.Equal("3", albumElement!.Attribute("songCount")?.Value);
    }

    [Fact]
    public void CreateArtistResponse_JsonFormat_ReturnsArtistData()
    {
        // Arrange
        var artist = new Artist
        {
            Id = "artist123",
            Name = "Test Artist"
        };
        var albums = new List<Album>
        {
            new Album { Id = "album1", Title = "Album 1" },
            new Album { Id = "album2", Title = "Album 2" }
        };

        // Act
        var result = _builder.CreateArtistResponse("json", artist, albums);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var artistData = doc.RootElement.GetProperty("subsonic-response").GetProperty("artist");
        
        Assert.Equal("artist123", artistData.GetProperty("id").GetString());
        Assert.Equal("Test Artist", artistData.GetProperty("name").GetString());
        Assert.Equal(2, artistData.GetProperty("albumCount").GetInt32());
    }

    [Fact]
    public void CreateArtistResponse_XmlFormat_ReturnsArtistData()
    {
        // Arrange
        var artist = new Artist
        {
            Id = "artist123",
            Name = "Test Artist"
        };
        var albums = new List<Album>
        {
            new Album { Id = "album1", Title = "Album 1" },
            new Album { Id = "album2", Title = "Album 2" }
        };

        // Act
        var result = _builder.CreateArtistResponse("xml", artist, albums);

        // Assert
        var contentResult = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/xml", contentResult.ContentType);
        
        var doc = XDocument.Parse(contentResult.Content!);
        var ns = doc.Root!.GetDefaultNamespace();
        var artistElement = doc.Root!.Element(ns + "artist");
        Assert.NotNull(artistElement);
        Assert.Equal("artist123", artistElement.Attribute("id")?.Value);
        Assert.Equal("Test Artist", artistElement.Attribute("name")?.Value);
        Assert.Equal("2", artistElement.Attribute("albumCount")?.Value);
    }

    [Fact]
    public void CreateSongResponse_SongWithNullValues_HandlesGracefully()
    {
        // Arrange
        var song = new Song
        {
            Id = "song123",
            Title = "Test Song"
            // Other fields are null
        };

        // Act
        var result = _builder.CreateSongResponse("json", song);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var songData = doc.RootElement.GetProperty("subsonic-response").GetProperty("song");
        
        Assert.Equal("song123", songData.GetProperty("id").GetString());
        Assert.Equal("Test Song", songData.GetProperty("title").GetString());
    }

    [Fact]
    public void CreateAlbumResponse_EmptySongList_ReturnsZeroCounts()
    {
        // Arrange
        var album = new Album
        {
            Id = "album123",
            Title = "Empty Album",
            Artist = "Test Artist",
            Songs = new List<Song>()
        };

        // Act
        var result = _builder.CreateAlbumResponse("json", album);

        // Assert
        var jsonResult = Assert.IsType<JsonResult>(result);
        var json = JsonSerializer.Serialize(jsonResult.Value);
        var doc = JsonDocument.Parse(json);
        var albumData = doc.RootElement.GetProperty("subsonic-response").GetProperty("album");
        
        Assert.Equal(0, albumData.GetProperty("songCount").GetInt32());
        Assert.Equal(0, albumData.GetProperty("duration").GetInt32());
    }

    // ---- Declared format must match the bytes that will arrive -----------------
    // A Subsonic client picks its decoder from suffix/contentType, so declaring one
    // thing and serving another makes playback fail silently rather than error. The
    // comment above ConvertSongToJson records that this was already shipped wrong once.

    private static SubsonicResponseBuilder BuilderWith(bool waitForLossless) =>
        new(new Octo.Services.Soulseek.ExternalIdRegistry(),
            Microsoft.Extensions.Options.Options.Create(
                new Octo.Models.Settings.SubsonicSettings { WaitForLosslessOnPlay = waitForLossless }));

    private static Song ExternalSong() => new()
    {
        Id = "abc123", Title = "Teardrop", Artist = "Massive Attack",
        Duration = 330, IsLocal = false, ExternalProvider = "soulseek", ExternalId = "abc123",
    };

    [Fact]
    public void ExternalSong_DeclaresLossy_WhenNotWaitingForLossless()
    {
        var row = BuilderWith(false).ConvertSongToJson(ExternalSong());

        Assert.Equal("m4a", row["suffix"]);
        Assert.Equal("audio/mp4", row["contentType"]);
        Assert.Equal(128, row["bitRate"]);
    }

    /// <summary>
    /// With the wait enabled, /rest/stream serves the fetched FLAC under this same id,
    /// so the row has to say so. Declaring m4a here is the exact mismatch that leaves
    /// players stuck on "loading".
    /// </summary>
    [Fact]
    public void ExternalSong_DeclaresLossless_WhenWaitingForLossless()
    {
        var row = BuilderWith(true).ConvertSongToJson(ExternalSong());

        Assert.Equal("flac", row["suffix"]);
        Assert.Equal("audio/flac", row["contentType"]);
        // A FLAC's rate is unknown until it is fetched, so none is claimed.
        Assert.False(row.ContainsKey("bitRate"));
    }

    [Fact]
    public void LocalSong_AlwaysDeclaresFlac_RegardlessOfSetting()
    {
        var local = ExternalSong();
        local.IsLocal = true;

        foreach (var waiting in new[] { false, true })
        {
            var row = BuilderWith(waiting).ConvertSongToJson(local);
            Assert.Equal("flac", row["suffix"]);
            Assert.Equal(1411, row["bitRate"]);
        }
    }

    // ---- ISRCs: OpenSubsonic's isrc is a list, in both formats ---------------------------

    [Fact]
    public void ExternalSong_WithAnIsrc_ListsIt()
    {
        var song = ExternalSong();
        song.Isrc = "gb-a1b-98-00001";

        Assert.Equal(new[] { "GBA1B9800001" }, BuilderWith(false).ConvertSongToJson(song)["isrc"]);
    }

    [Fact]
    public void ExternalSong_WithNoValidIsrc_ListsNone()
    {
        var song = ExternalSong();
        Assert.Equal(Array.Empty<string>(), BuilderWith(false).ConvertSongToJson(song)["isrc"]);
        song.Isrc = "not an isrc";
        Assert.Equal(Array.Empty<string>(), BuilderWith(false).ConvertSongToJson(song)["isrc"]);
    }

    /// <summary>A library song Octo rebuilt from Navidrome's answer goes back out with the
    /// codes it came in with, untouched, not normalised and not dropped.</summary>
    [Fact]
    public void LibrarySong_KeepsNavidromesIsrcsUntouched()
    {
        var song = ExternalSong();
        song.IsLocal = true;
        song.Isrc = "USRC17600001";
        song.Isrcs = ["GBA1B9800001", "us-rc1-76-07839"];

        Assert.Equal(new[] { "GBA1B9800001", "us-rc1-76-07839" }, BuilderWith(false).ConvertSongToJson(song)["isrc"]);
    }

    [Fact]
    public void XmlSong_ListsEachIsrcAsAChildElement()
    {
        var song = ExternalSong();
        song.IsLocal = true;
        song.Isrcs = ["GBA1B9800001", "USRC17607839"];
        XNamespace ns = "http://subsonic.org/restapi";

        var xml = BuilderWith(false).ConvertSongToXml(song, ns);

        Assert.Equal(["GBA1B9800001", "USRC17607839"], xml.Elements(ns + "isrc").Select(element => element.Value));
        Assert.Null(xml.Attribute("isrc"));
    }

    // ---- Issue #35: the album DETAIL shape was missing `created` -----------------
    // Strict OpenSubsonic clients validate before playing: Music Assistant rejected every
    // external album with "Field created of type str is missing in AlbumID3WithSongs".
    // The album ROW shape (BuildAlbumFields) always sent it, so the two disagreed and only
    // the detail call broke.

    [Fact]
    public void CreateAlbumResponse_JsonFormat_CarriesCreated()
    {
        var album = new Album { Id = "album123", Title = "Test Album", Artist = "Test Artist" };

        var result = _builder.CreateAlbumResponse("json", album);

        var json = JsonSerializer.Serialize(Assert.IsType<JsonResult>(result).Value);
        var albumData = JsonDocument.Parse(json).RootElement
            .GetProperty("subsonic-response").GetProperty("album");

        Assert.True(albumData.TryGetProperty("created", out var created));
        Assert.True(DateTime.TryParse(created.GetString(), out _));
    }

    [Fact]
    public void CreateAlbumResponse_XmlFormat_CarriesCreated()
    {
        var album = new Album { Id = "album123", Title = "Test Album", Artist = "Test Artist" };

        var result = _builder.CreateAlbumResponse("xml", album);

        var xml = Assert.IsType<ContentResult>(result).Content!;
        var albumElement = XDocument.Parse(xml).Descendants()
            .First(e => e.Name.LocalName == "album");

        Assert.NotNull(albumElement.Attribute("created"));
        Assert.True(DateTime.TryParse(albumElement.Attribute("created")!.Value, out _));
    }

    [Fact]
    public void ConvertAlbumToJson_CarriesDuration()
    {
        var album = new Album
        {
            Id = "album123", Title = "Test Album", Artist = "Test Artist",
            Songs = [new Song { Duration = 200 }, new Song { Duration = null }, new Song { Duration = 100 }],
        };

        var fields = Assert.IsType<Dictionary<string, object>>(_builder.ConvertAlbumToJson(album));

        Assert.Equal(300, fields["duration"]);
    }

    [Fact]
    public void ConvertAlbumToXml_CarriesDuration_EvenWithoutKnownSongs()
    {
        var album = new Album { Id = "album123", Title = "Test Album", Artist = "Test Artist" };

        var element = _builder.ConvertAlbumToXml(album, XNamespace.Get("http://subsonic.org/restapi"));

        Assert.Equal("0", element.Attribute("duration")?.Value);
    }

    // ---- OpenSubsonic releaseTypes ------------------------------------------------------
    // What lets a client group an artist's page into albums, EPs and singles.

    [Fact]
    public void AlbumRow_CarriesItsReleaseTypesInBothFormats()
    {
        var album = new Album { Id = "al1", Title = "Live Set", Artist = "A", ReleaseTypes = ["ep"] };

        var json = JsonSerializer.Serialize(_builder.ConvertAlbumToJson(album));
        Assert.Equal(["ep"], JsonDocument.Parse(json).RootElement.GetProperty("releaseTypes")
            .EnumerateArray().Select(t => t.GetString()));

        // A list of text is one element per value in XML, the way the upstream server writes it.
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        var xml = _builder.ConvertAlbumToXml(album, ns);
        Assert.Equal(["ep"], xml.Elements(ns + "releaseTypes").Select(e => e.Value));
        Assert.Null(xml.Attribute("releaseTypes"));
    }

    [Fact]
    public void AlbumRow_WithNoKnownType_SendsAnEmptyList()
    {
        // OpenSubsonic asks for the field even when empty, so a client knows it is supported.
        var album = new Album { Id = "al1", Title = "Mystery", Artist = "A" };

        var json = JsonSerializer.Serialize(_builder.ConvertAlbumToJson(album));
        Assert.Equal(0, JsonDocument.Parse(json).RootElement.GetProperty("releaseTypes").GetArrayLength());
        var ns = XNamespace.Get("http://subsonic.org/restapi");
        Assert.Empty(_builder.ConvertAlbumToXml(album, ns).Elements(ns + "releaseTypes"));
    }

    [Fact]
    public void CreateAlbumResponse_CarriesItsReleaseTypesInBothFormats()
    {
        var album = new Album { Id = "al1", Title = "Hit", Artist = "A", ReleaseTypes = ["single"] };

        var json = JsonSerializer.Serialize(Assert.IsType<JsonResult>(_builder.CreateAlbumResponse("json", album)).Value);
        Assert.Equal(["single"], JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response")
            .GetProperty("album").GetProperty("releaseTypes").EnumerateArray().Select(t => t.GetString()));

        var doc = XDocument.Parse(Assert.IsType<ContentResult>(_builder.CreateAlbumResponse("xml", album)).Content!);
        var ns = doc.Root!.GetDefaultNamespace();
        Assert.Equal(["single"], doc.Root.Element(ns + "album")!.Elements(ns + "releaseTypes").Select(e => e.Value));
    }

    // ---- The announced length must be the length of the audio --------------------------
    // An outside song whose video runs five to eight minutes was announced as 180 s,
    // because the only length on the Song is the download-verification one and nothing
    // else was ever read. Third-party clients took 180 for a real three minutes, so the
    // scrub bar ran past the end of the track.

    private static SubsonicResponseBuilder BuilderWithRegistry(
        Octo.Services.Soulseek.ExternalIdRegistry registry) =>
        new(registry, Microsoft.Extensions.Options.Options.Create(
            new Octo.Models.Settings.SubsonicSettings()));

    private static string RegisterExternalSong(Octo.Services.Soulseek.ExternalIdRegistry registry,
        string artist, string title) =>
        registry.Register(new Octo.Services.Soulseek.SoulseekRouting
        {
            Kind = Octo.Services.Soulseek.RoutingKind.Song,
            Artist = artist,
            Title = title,
        });

    private static Song ExternalSongFor(string id, string artist, string title) => new()
    {
        Id = id, Title = title, Artist = artist,
        IsLocal = false, ExternalProvider = "soulseek", ExternalId = id,
    };

    [Fact]
    public void ExternalSong_WithoutAVerificationLength_AnnouncesTheRegistrysDisplayLength()
    {
        var registry = new Octo.Services.Soulseek.ExternalIdRegistry();
        var id = RegisterExternalSong(registry, "Earth, Wind & Fire", "Boogie Wonderland");
        Assert.True(registry.RememberLength(id, 288, Octo.Services.Soulseek.LengthSource.Deezer));

        var row = BuilderWithRegistry(registry)
            .ConvertSongToJson(ExternalSongFor(id, "Earth, Wind & Fire", "Boogie Wonderland"));

        Assert.Equal(288, row["duration"]);
    }

    [Fact]
    public void ExternalSong_WithAPlayedLength_PrefersTheSongOverTheRegistry()
    {
        var registry = new Octo.Services.Soulseek.ExternalIdRegistry();
        var id = RegisterExternalSong(registry, "Oz Noy", "Come Dance With Me");
        Assert.True(registry.RememberLength(id, 250, Octo.Services.Soulseek.LengthSource.Deezer));

        var song = ExternalSongFor(id, "Oz Noy", "Come Dance With Me");
        song.Duration = 455;

        var row = BuilderWithRegistry(registry).ConvertSongToJson(song);

        Assert.Equal(455, row["duration"]);
    }

    [Fact]
    public void ExternalSong_WithNoKnownLength_KeepsThePlaceholder()
    {
        var row = _builder.ConvertSongToJson(ExternalSongFor("unknown-id", "Bill Laurance", "The Good Things"));

        // The placeholder stays only where nothing at all is known: Octo's own app reads
        // 180 as "no length", and there is no better guess for a third-party client.
        Assert.Equal(180, row["duration"]);
    }
}
