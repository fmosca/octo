using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Fingerprint;
using Octo.Services.Local;
using Octo.Services.Notifications;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;
using Octo.Services.YouTube;

namespace Octo.Tests;

/// <summary>
/// An album heart used to search, pick a peer and queue song by song. These pin down choosing one
/// peer's folder of the album, and the walk that takes it in one batch and searches only the gaps.
/// </summary>
public sealed class AlbumFolderTests : IDisposable
{
    // ---- The picker ----------------------------------------------------------------------------

    private static AlbumTrack T(int number, string title, int seconds) => new($"id-{number}", title, seconds, number);

    private static SoulseekFileHit F(string user, string path, int seconds, string ext = "flac", bool? free = null,
        int queue = 0, int speed = 1000) =>
        new()
        {
            Username = user, Filename = path, Size = 30_000_000, Length = seconds, Extension = ext,
            HasFreeUploadSlot = free, QueueLength = queue, UploadSpeed = speed,
        };

    private static readonly Func<SoulseekFileHit, bool> FlacOnly = hit => hit.Extension == "flac";

    private static readonly AlbumTrack[] Album =
    [
        T(1, "Intro", 90), T(2, "Hold On", 200), T(3, "Hold On, We're Going Home", 228), T(4, "Started", 180),
    ];

    private static IEnumerable<SoulseekFileHit> Folder(string user, string folder, string ext = "flac", bool? free = null,
        params int[] skip) =>
        Album.Where(t => !skip.Contains(t.Number!.Value)).Select(t =>
            F(user, $@"{folder}\{t.Number:00} - {t.Title}.{ext}", t.Duration!.Value, ext, free));

    [Fact]
    public void OneFlacFolderBeatsAFullerMp3Folder()
    {
        var hits = Folder("mp3peer", @"Music\Drake\Album", "mp3")
            .Concat(Folder("flacpeer", @"Music\Drake\Album", "flac", null, 4)).ToList();
        var choice = AlbumFolderPicker.Choose(hits, Album, FlacOnly);
        Assert.NotNull(choice);
        Assert.Equal("flacpeer", choice.Username);
        Assert.Equal(3, choice.Files.Count);
    }

    [Fact]
    public void AFolderWithMostOfTheAlbumBeatsTheAlbumSpreadOverPeers()
    {
        var hits = Folder("whole", @"a\Album", skip: 4)
            .Concat(Folder("p1", @"b\Album", skip: [2, 3, 4]))
            .Concat(Folder("p2", @"c\Album", skip: [1, 3, 4]))
            .Concat(Folder("p3", @"d\Album", skip: [1, 2, 4])).ToList();
        Assert.Equal("whole", AlbumFolderPicker.Choose(hits, Album, FlacOnly)!.Username);
    }

    [Fact]
    public void HoldOnNeverTakesHoldOnWereGoingHome()
    {
        var choice = AlbumFolderPicker.Choose(Folder("peer", @"x\Album").ToList(), Album, FlacOnly)!;
        var holdOn = choice.Files.Single(pair => pair.Track.Title == "Hold On").File;
        Assert.EndsWith("02 - Hold On.flac", holdOn.Filename);
        var home = choice.Files.Single(pair => pair.Track.Title.StartsWith("Hold On, ")).File;
        Assert.EndsWith("03 - Hold On, We're Going Home.flac", home.Filename);
        Assert.Equal(4, choice.Files.Select(pair => pair.File.Filename).Distinct().Count());
    }

    [Fact]
    public void AFileOfTheWrongLengthIsNotMatched()
    {
        var hits = Folder("peer", @"x\Album").ToList();
        hits[3].Length = 180 + 11;
        var choice = AlbumFolderPicker.Choose(hits, Album, FlacOnly)!;
        Assert.Equal(3, choice.Files.Count);
        Assert.DoesNotContain(choice.Files, pair => pair.Track.Title == "Started");
    }

    [Fact]
    public void UnderHalfTheAlbumIsNoChoice() =>
        Assert.Null(AlbumFolderPicker.Choose(Folder("peer", @"x\Album", skip: [2, 3, 4]).ToList(), Album, FlacOnly));

    [Fact]
    public void ADeniedFileIsPassedOver()
    {
        var hits = Folder("peer", @"x\Album").ToList();
        var choice = AlbumFolderPicker.Choose(hits, Album, hit => FlacOnly(hit) && !hit.Filename.Contains("Intro"))!;
        Assert.DoesNotContain(choice.Files, pair => pair.Track.Title == "Intro");
    }

    [Fact]
    public void ATwoDiscRipIsOneFolder()
    {
        var hits = new[]
        {
            F("peer", @"Music\Album\CD1\01 - Intro.flac", 90), F("peer", @"Music\Album\CD1\02 - Hold On.flac", 200),
            F("peer", @"Music\Album\Disc 2\03 - Hold On, We're Going Home.flac", 228), F("peer", @"Music\Album\Disc 2\04 - Started.flac", 180),
        };
        var choice = AlbumFolderPicker.Choose(hits, Album, FlacOnly)!;
        Assert.Equal(4, choice.Files.Count);
        Assert.Equal("Music/Album", choice.Folder);
    }

    [Fact]
    public void TooFewKnownLengthsIsNoAlbumMode()
    {
        var unknown = Album.Select((t, i) => i < 2 ? t with { Duration = null } : t).ToList();
        Assert.Null(AlbumFolderPicker.Choose(Folder("peer", @"x\Album").ToList(), unknown, FlacOnly));
    }

    [Fact]
    public void APeerWithAFreeSlotWinsWhenItCoversEnough()
    {
        var hits = Folder("queued", @"a\Album", free: false)
            .Concat(Folder("free", @"b\Album", free: true, skip: 4)).ToList();
        Assert.Equal("free", AlbumFolderPicker.Choose(hits, Album, FlacOnly)!.Username);

        var thin = Folder("queued", @"a\Album", free: false)
            .Concat(Folder("free", @"b\Album", free: true, skip: [3, 4])).ToList();
        Assert.Equal("queued", AlbumFolderPicker.Choose(thin, Album, FlacOnly)!.Username);
    }

    [Fact]
    public void FilesComeBackInAlbumOrder()
    {
        var hits = Folder("peer", @"x\Album").Reverse().ToList();
        var choice = AlbumFolderPicker.Choose(hits, Album, FlacOnly)!;
        Assert.Equal(["Intro", "Hold On", "Hold On, We're Going Home", "Started"], choice.Files.Select(p => p.Track.Title));
    }

    [Theory]
    [InlineData("Drake", "HABIBTI (FOMO)", "Drake HABIBTI")]
    [InlineData("Artist", "Song - Single", "Artist Song")]
    [InlineData("Artist", "Deluxe [Remastered]", "Artist Deluxe")]
    [InlineData("Artist", "Unknown Album", null)]
    [InlineData("", "Album", null)]
    public void TheAlbumSearchWords(string artist, string album, string? expected) =>
        Assert.Equal(expected, SoulseekDownloadService.AlbumSearchText(artist, album));

    // ---- The walk, through the real download service against a fake slskd --------------------

    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-albumwalk-" + Guid.NewGuid().ToString("N"));

    public AlbumFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A FLAC whose STREAMINFO says it lasts this long, so the length check passes.</summary>
    private static byte[] Flac(int seconds)
    {
        using var stream = new MemoryStream();
        stream.Write("fLaC"u8);
        stream.Write([0x80, 0x00, 0x00, 0x22]);
        stream.Write([0x10, 0x00, 0x10, 0x00]);
        stream.Write([0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        const ulong sampleRate = 44100, channelsMinusOne = 1, bitsMinusOne = 15;
        var totalSamples = 44100UL * (ulong)seconds;
        var packed = (sampleRate << 44) | (channelsMinusOne << 41) | (bitsMinusOne << 36) | totalSamples;
        for (var shift = 56; shift >= 0; shift -= 8) stream.WriteByte((byte)(packed >> shift));
        stream.Write(new byte[16]);
        return stream.ToArray();
    }

    /// <summary>
    /// slskd as far as one walk needs: logged in, searches answered from a script, and every batch
    /// file written straight into its destination folder and reported finished.
    /// </summary>
    private sealed class FakeSlskd(string root, Func<string, object[]> responsesFor) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Searches = new();
        public readonly ConcurrentQueue<(string User, string[] Files, string Destination)> Batches = new();
        private readonly ConcurrentDictionary<string, string> _searchText = new();
        private readonly ConcurrentDictionary<string, List<object>> _transfers = new();
        private readonly ConcurrentDictionary<string, int> _lengths = new();
        private int _ids;

        public void Length(string remote, int seconds) => _lengths[remote] = seconds;

        /// <summary>What a peer lists for one folder of its share, by user and folder; null lists nothing.</summary>
        public Func<string, string, object?>? Folders { get; set; }
        public readonly ConcurrentQueue<(string User, string Folder)> Browses = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (path == "/api/v0/session") return Json("""{"token":"jwt","expires":4102444800}""");
            if (path == "/api/v0/options")
                return Json(JsonSerializer.Serialize(new { directories = new { downloads = root, incomplete = "/app/incomplete" } }));
            if (path == "/api/v0/searches" && request.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(body);
                var text = doc.RootElement.GetProperty("searchText").GetString()!;
                _searchText[doc.RootElement.GetProperty("id").GetString()!] = text;
                Searches.Enqueue(text);
                return Json("{}");
            }
            if (path.StartsWith("/api/v0/searches/") && path.EndsWith("/responses"))
            {
                var id = path.Split('/')[4];
                return Json(JsonSerializer.Serialize(responsesFor(_searchText.GetValueOrDefault(id, ""))));
            }
            if (path.StartsWith("/api/v0/searches/"))
                return Json("""{"state":"Completed, ResponseLimitReached","endedAt":"2026-10-03T12:00:00Z","responseCount":1}""");
            if (path.StartsWith("/api/v0/users/") && path.EndsWith("/directory") && request.Method == HttpMethod.Post)
            {
                var user = Uri.UnescapeDataString(path.Split('/')[4]);
                using var doc = JsonDocument.Parse(body);
                var folder = doc.RootElement.GetProperty("directory").GetString()!;
                Browses.Enqueue((user, folder));
                return Folders?.Invoke(user, folder) is { } listing
                    ? Json(JsonSerializer.Serialize(listing))
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (path == "/api/v0/transfers/downloads/batches")
            {
                using var doc = JsonDocument.Parse(body);
                var user = doc.RootElement.GetProperty("username").GetString()!;
                var destination = doc.RootElement.GetProperty("options").GetProperty("destination").GetString()!;
                var files = doc.RootElement.GetProperty("files").EnumerateArray()
                    .Select(f => f.GetProperty("filename").GetString()!).ToArray();
                Batches.Enqueue((user, files, destination));
                var queued = new List<object>();
                foreach (var file in files)
                {
                    var leaf = file.Replace('\\', '/').Split('/')[^1];
                    var local = Path.Combine(root, destination, leaf);
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    var bytes = Flac(_lengths.GetValueOrDefault(file, 200));
                    File.WriteAllBytes(local, bytes);
                    var transfer = new
                    {
                        id = $"t-{Interlocked.Increment(ref _ids)}", filename = file, state = "Completed, Succeeded",
                        size = (long)bytes.Length, bytesTransferred = (long)bytes.Length, percentComplete = 100.0,
                    };
                    _transfers.AddOrUpdate(user, _ => [transfer], (_, list) => { lock (list) list.Add(transfer); return list; });
                    queued.Add(transfer);
                }
                return Json(JsonSerializer.Serialize(new { batch = new { username = user, transfers = queued }, failures = Array.Empty<object>() }),
                    HttpStatusCode.Created);
            }
            if (path.StartsWith("/api/v0/transfers/downloads/") && request.Method == HttpMethod.Get)
            {
                var user = Uri.UnescapeDataString(path.Split('/')[5]);
                var files = _transfers.TryGetValue(user, out var list) ? list.ToArray() : [];
                return Json(JsonSerializer.Serialize(new { username = user, directories = new[] { new { directory = "x", files } } }));
            }
            return Json("{}");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Walk
    {
        public required SoulseekDownloadService Service { get; init; }
        public required FakeSlskd Slskd { get; init; }
        public required List<Song> Songs { get; init; }
        public required string AlbumId { get; init; }
    }

    private Walk Build(bool albumFolders, Func<IReadOnlyList<Song>, Func<string, object[]>> answers)
    {
        var registry = new ExternalIdRegistry();
        var titles = new[] { ("Intro", 90), ("Hold On", 200), ("Hold On, We're Going Home", 228), ("Started", 180) };
        var songs = titles.Select((t, i) =>
        {
            var routing = new SoulseekRouting
            {
                Kind = RoutingKind.Song, Artist = "Drake", Title = t.Item1, Album = "Nothing Was the Same",
                Duration = t.Item2, Track = i + 1,
            };
            var id = registry.Register(routing);
            return new Song
            {
                Id = $"ext-soulseek-{id}", ExternalProvider = "soulseek", ExternalId = id, Title = t.Item1,
                Artist = "Drake", Album = "Nothing Was the Same", Duration = t.Item2, Track = i + 1,
            };
        }).ToList();

        var slskd = new FakeSlskd(_root, answers(songs));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(slskd, disposeHandler: false));
        var soulseekSettings = new SoulseekSettings
        {
            BaseUrl = "http://slskd.test", Username = "u", Password = "p", MinFileSizeBytes = 0,
            AlbumFolders = albumFolders, ParallelDownloads = 3, DetectTranscodes = false, VerifyDownloads = false,
            DownloadTimeoutSeconds = 30,
        };
        var client = new SoulseekClient(factory.Object, Options.Create(soulseekSettings), NullLogger<SoulseekClient>.Instance)
        {
            SearchPollInterval = TimeSpan.FromMilliseconds(5),
            PollInterval = TimeSpan.FromMilliseconds(5),
            MinSearchSpacing = TimeSpan.Zero,
        };

        var metadata = new Mock<IMusicMetadataService>();
        var album = new Album { Id = "album-1", Title = "Nothing Was the Same", Artist = "Drake", Songs = songs };
        metadata.Setup(m => m.GetAlbumAsync("soulseek", "album-1")).ReturnsAsync(album);
        metadata.Setup(m => m.GetSongAsync("soulseek", It.IsAny<string>()))
            .ReturnsAsync((string _, string id) => songs.Single(s => s.ExternalId == id));

        var monitor = TestOptions.Monitor(soulseekSettings);
        var services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<SoulseekSettings>>(monitor)
            .AddSingleton(new DownloadConcurrency(monitor))
            .BuildServiceProvider();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = _root }).Build();
        var subsonic = TestOptions.Monitor(new SubsonicSettings { AutoDetectDownloadPath = false, DownloadSource = DownloadSource.Soulseek });
        var service = new SoulseekDownloadService(config, Mock.Of<ILocalLibraryService>(), metadata.Object, subsonic,
            TestOptions.Monitor(new GenreSettings()), Options.Create(soulseekSettings), client,
            new YouTubeResolver(factory.Object, config, NullLogger<YouTubeResolver>.Instance), registry, factory.Object,
            new NavidromeIdentityService(subsonic, factory.Object, NullLogger<NavidromeIdentityService>.Instance),
            new DownloadHistoryService(Path.Combine(_root, "history.json"), NullLogger<DownloadHistoryService>.Instance),
            new NotificationService([], TestOptions.Monitor(new NotificationSettings()), NullLogger<NotificationService>.Instance),
            new RejectedPeerRegistry(),
            new DownloadVerificationService(new AudioFingerprinter(NullLogger<AudioFingerprinter>.Instance),
                new AcoustIdClient(factory.Object, NullLogger<AcoustIdClient>.Instance), monitor,
                NullLogger<DownloadVerificationService>.Instance),
            services, NullLogger<SoulseekDownloadService>.Instance);
        return new Walk { Service = service, Slskd = slskd, Songs = songs, AlbumId = "album-1" };
    }

    private static object Response(string user, IEnumerable<(string File, int Seconds)> files) => new
    {
        username = user, uploadSpeed = 1_000_000, queueLength = 0, hasFreeUploadSlot = true,
        files = files.Select(f => new { filename = f.File, size = (long)Flac(f.Seconds).Length, length = f.Seconds, extension = "flac" }).ToArray(),
    };

    private static string Remote(string folder, Song song) => $@"{folder}\{song.Track:00} - {song.Title}.flac";

    [Fact]
    public async Task AWholeFolderIsOneSearchAndOneBatch_AndEverySongLands()
    {
        Walk? walk = null;
        walk = Build(albumFolders: true, songs => text =>
            text == "Drake Nothing Was the Same"
                ? [Response("albumpeer", songs.Select(s => (Remote(@"Music\Drake\NWTS", s), s.Duration!.Value)))]
                : []);
        foreach (var song in walk.Songs) walk.Slskd.Length(Remote(@"Music\Drake\NWTS", song), song.Duration!.Value);

        var ok = await walk.Service.DownloadAlbumWithSourceAsync("soulseek", walk.AlbumId, DownloadSource.Soulseek,
            suppressSummary: false).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(ok);
        Assert.Equal(["Drake Nothing Was the Same"], walk.Slskd.Searches);
        var batch = Assert.Single(walk.Slskd.Batches);
        Assert.Equal("albumpeer", batch.User);
        Assert.Equal(4, batch.Files.Length);
        Assert.StartsWith(".octo-incoming/slskd/", batch.Destination);
        Assert.Equal(4, Directory.EnumerateFiles(_root, "*.flac", SearchOption.AllDirectories)
            .Count(f => !f.Contains(".octo-incoming")));
    }

    [Fact]
    public async Task ASongTheFolderLacksIsSearchedOnItsOwn()
    {
        var walk = Build(albumFolders: true, songs => text =>
            text == "Drake Nothing Was the Same"
                ? [Response("albumpeer", songs.Take(3).Select(s => (Remote(@"Music\NWTS", s), s.Duration!.Value)))]
                : text.Contains("Started")
                    ? [Response("songpeer", [(Remote(@"Singles", songs[3]), songs[3].Duration!.Value)])]
                    : []);
        foreach (var song in walk.Songs.Take(3)) walk.Slskd.Length(Remote(@"Music\NWTS", song), song.Duration!.Value);
        walk.Slskd.Length(Remote(@"Singles", walk.Songs[3]), walk.Songs[3].Duration!.Value);

        Assert.True(await walk.Service.DownloadAlbumWithSourceAsync("soulseek", walk.AlbumId, DownloadSource.Soulseek,
            suppressSummary: false).WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Equal(2, walk.Slskd.Searches.Count);
        Assert.Contains(walk.Slskd.Searches, s => s.Contains("Started"));
        Assert.Equal(["albumpeer", "songpeer"], walk.Slskd.Batches.Select(b => b.User).Order());
    }

    [Fact]
    public async Task WithAlbumFoldersOff_EverySongIsSearchedOnItsOwn()
    {
        var walk = Build(albumFolders: false, songs => text =>
            songs.FirstOrDefault(s => text.Contains(s.Title!.Split(',')[0]) && !(s.Title == "Hold On" && text.Contains("Home")))
                is { } song ? [Response("peer", [(Remote("x", song), song.Duration!.Value)])] : []);
        foreach (var song in walk.Songs) walk.Slskd.Length(Remote("x", song), song.Duration!.Value);

        await walk.Service.DownloadAlbumWithSourceAsync("soulseek", walk.AlbumId, DownloadSource.Soulseek, suppressSummary: false)
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.DoesNotContain("Drake Nothing Was the Same", walk.Slskd.Searches);
        Assert.True(walk.Slskd.Searches.Count >= 4);
        Assert.All(walk.Slskd.Batches, b => Assert.Single(b.Files));
    }

    private static object LossyResponse(string user, string file, int seconds) => new
    {
        username = user, uploadSpeed = 1_000_000, queueLength = 0, hasFreeUploadSlot = true,
        files = new[] { new { filename = file, size = 8_000_000L, length = seconds, extension = "mp3", bitRate = 320 } },
    };

    /// <summary>
    /// #70: the search finds the song only as an MP3, and the FLAC sits beside it in the same
    /// folder, unanswered. Octo asks the peer for that folder and takes the FLAC from it.
    /// </summary>
    [Fact]
    public async Task AFlacBesideTheOnlyMp3TheSearchFoundIsTaken()
    {
        const string folder = @"Music\Drake\Nothing Was the Same";
        var walk = Build(albumFolders: false, songs => text =>
            text.Contains("Started") ? [LossyResponse("bothpeer", $@"{folder}\04 - Started.mp3", 180)] : []);
        var started = walk.Songs[3];
        var flac = $@"{folder}\04 - Started.flac";
        walk.Slskd.Length(flac, 180);
        walk.Slskd.Folders = (user, dir) => user == "bothpeer" && dir == folder
            ? new
            {
                name = folder,
                files = new object[]
                {
                    new { filename = "04 - Started.mp3", size = 8_000_000L, extension = "mp3", length = 180 },
                    new { filename = "04 - Started.flac", size = (long)Flac(180).Length, extension = "flac", length = 180 },
                    new { filename = "03 - Hold On, We're Going Home.flac", size = (long)Flac(228).Length, extension = "flac", length = 228 },
                },
            }
            : null;

        var path = await walk.Service.ExecuteAcquisitionAsync("soulseek", started.ExternalId!, false, true,
            DownloadSource.Soulseek, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal([("bothpeer", folder)], walk.Slskd.Browses);
        var batch = Assert.Single(walk.Slskd.Batches);
        Assert.Equal([flac], batch.Files);
        Assert.EndsWith(".flac", path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task NoFlacBesideTheMp3MeansNoDownload()
    {
        const string folder = @"Music\Drake\Nothing Was the Same";
        var walk = Build(albumFolders: false, songs => text =>
            text.Contains("Started") ? [LossyResponse("mp3peer", $@"{folder}\04 - Started.mp3", 180)] : []);
        walk.Slskd.Folders = (_, _) => new
        {
            name = folder,
            files = new object[] { new { filename = "04 - Started.mp3", size = 8_000_000L, extension = "mp3", length = 180 } },
        };

        await Assert.ThrowsAnyAsync<Exception>(() => walk.Service.ExecuteAcquisitionAsync("soulseek", walk.Songs[3].ExternalId!,
            false, true, DownloadSource.Soulseek, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)));

        Assert.Single(walk.Slskd.Browses);
        Assert.Empty(walk.Slskd.Batches);
    }

    [Fact]
    public void AFoldersFilesComeBackWithTheirFullRemotePath()
    {
        var from = new SoulseekFileHit { Username = "peer", Filename = @"A\B\x.mp3", QueueLength = 2, UploadSpeed = 900, HasFreeUploadSlot = true };
        var hits = SoulseekClient.ParseDirectory(
            """[{"name":"A\\B","files":[{"filename":"01 - Song.flac","size":30000000,"extension":"flac","length":200,"bitDepth":16,"sampleRate":44100}]}]""",
            from, @"A\B");

        var hit = Assert.Single(hits);
        Assert.Equal(@"A\B\01 - Song.flac", hit.Filename);
        Assert.Equal("flac", hit.Extension);
        Assert.Equal(200, hit.Length);
        Assert.Equal(2, hit.QueueLength);
        Assert.True(hit.HasFreeUploadSlot);
    }

    [Fact]
    public void AFolderObjectAndFullPathsAreReadToo()
    {
        var from = new SoulseekFileHit { Username = "peer", Filename = @"A\B\x.mp3" };
        var hits = SoulseekClient.ParseDirectory(
            """{"files":[{"filename":"A\\B\\02 - Other.flac","size":1}]}""", from, @"A\B");

        Assert.Equal(@"A\B\02 - Other.flac", Assert.Single(hits).Filename);
        Assert.Equal("flac", hits[0].Extension);
    }

    [Theory]
    [InlineData(@"Music\Artist\Album\01 - Song.mp3", @"Music\Artist\Album")]
    [InlineData("Music/Artist/01 - Song.mp3", "Music/Artist")]
    [InlineData("01 - Song.mp3", "")]
    public void TheFolderOfAFileIsWhatThePeerNamesIt(string file, string folder) =>
        Assert.Equal(folder, SoulseekDownloadService.FolderOfFile(file));
}
