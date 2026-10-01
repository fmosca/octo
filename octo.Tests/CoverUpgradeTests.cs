using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.CoverArt;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Octo.Tests;

/// <summary>
/// "Upgrade cover art" rewrites the owner's files, so what it touches, what it leaves and what
/// Undo puts back are pinned here, on real MP3s with real tags.
/// </summary>
public class CoverUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-cover-upgrade-" + Guid.NewGuid().ToString("N"));
    private readonly string _config;

    public CoverUpgradeTests()
    {
        _config = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(_root, "music"));
        Directory.CreateDirectory(_config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static byte[] Jpeg(int side, byte shade)
    {
        using var image = new Image<Rgba32>(side, side, new Rgba32(shade, shade, shade));
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        return stream.ToArray();
    }

    private sealed class FixedFinder(FoundCover? cover) : IAlbumCoverFinder
    {
        public List<AlbumCoverQuery> Asked { get; } = [];

        public Task<FoundCover?> FindAsync(AlbumCoverQuery query, CancellationToken ct)
        {
            Asked.Add(query);
            return Task.FromResult(cover);
        }
    }

    private string Song(string album, string title, byte[]? front, byte[]? back = null)
    {
        var folder = Path.Combine(_root, "music", "Daft Punk", album);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, title + ".mp3");
        File.WriteAllBytes(path, AudioFixtures.Mp3());
        using var file = TagLib.File.Create(path);
        file.Tag.Performers = ["Daft Punk"];
        file.Tag.AlbumArtists = ["Daft Punk"];
        file.Tag.Album = album;
        file.Tag.Title = title;
        var pictures = new List<TagLib.IPicture>();
        if (front is not null)
            pictures.Add(new TagLib.Picture { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg", Data = new TagLib.ByteVector(front) });
        if (back is not null)
            pictures.Add(new TagLib.Picture { Type = TagLib.PictureType.BackCover, MimeType = "image/jpeg", Data = new TagLib.ByteVector(back) });
        file.Tag.Pictures = pictures.ToArray();
        file.Save();
        return path;
    }

    private static TagLib.IPicture[] Pictures(string path)
    {
        using var file = TagLib.File.Create(path);
        return file.Tag.Pictures;
    }

    private static int FrontSide(string path) =>
        CoverImage.Measure(Pictures(path).First(p => p.Type == TagLib.PictureType.FrontCover).Data.Data) is { } size
            ? size.Width : 0;

    private (CoverUpgradeWorker Worker, CoverUpgradeStore Store) Worker(IAlbumCoverFinder finder, bool fullSize = false)
    {
        var store = new CoverUpgradeStore();
        var journal = new CoverUpgradeJournal(Path.Combine(_config, "cover-upgrade-journal.jsonl"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = Path.Combine(_root, "music") })
            .Build();
        var worker = new CoverUpgradeWorker(store, journal, finder,
            TestOptions.Monitor(new MetadataSettings { EmbedFullSizeCovers = fullSize }), config,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CoverUpgradeWorker>.Instance) { PauseBetweenAlbums = TimeSpan.Zero };
        return (worker, store);
    }

    private static async Task Run(CoverUpgradeWorker worker, CoverUpgradeStore store, CoverUpgradeRequest request)
    {
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(worker.TryEnqueue(request));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && (worker.IsBusy || store.Current.Status == CoverUpgradeStatus.Running))
                await Task.Delay(20);
            Assert.NotEqual(CoverUpgradeStatus.Running, store.Current.Status);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task APreviewLooksEachAlbumUpOnceAndWritesNothing()
    {
        var small = Jpeg(200, 10);
        var one = Song("Discovery", "One More Time", small);
        var two = Song("Discovery", "Aerodynamic", small);
        var before = File.ReadAllBytes(one);
        var finder = new FixedFinder(new FoundCover(Jpeg(3000, 200), "iTunes", 3000));
        var (worker, store) = Worker(finder);

        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Preview, FolderCovers: true));

        var run = store.Current;
        Assert.Equal(CoverUpgradeStatus.Completed, run.Status);
        Assert.Equal(1, run.Upgraded);
        Assert.Equal(2, run.Files);
        var change = Assert.Single(run.Preview);
        Assert.Equal((200, 3000, "iTunes"), (change.FromSide, change.ToSide, change.Source));
        Assert.Equal(before, File.ReadAllBytes(one));
        Assert.Equal(200, FrontSide(two));
        var asked = Assert.Single(finder.Asked);
        Assert.Equal(("Daft Punk", "Discovery"), (asked.Artist, asked.Album));
        Assert.False(worker.CanUndo);
    }

    [Fact]
    public async Task AnUpgradeEmbedsAt1500KeepsTheBackCoverAndUndoPutsTheOldOneBack()
    {
        var small = Jpeg(200, 10);
        var back = Jpeg(300, 90);
        var path = Song("Discovery", "One More Time", small, back);
        var (worker, store) = Worker(new FixedFinder(new FoundCover(Jpeg(3000, 200), "iTunes", 3000)));

        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Apply, FolderCovers: true));

        Assert.Equal(1500, FrontSide(path));
        var backAfter = Assert.Single(Pictures(path), p => p.Type == TagLib.PictureType.BackCover);
        Assert.Equal(back, backAfter.Data.Data);
        Assert.True(worker.CanUndo);

        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Apply, FolderCovers: true, Undo: true));

        Assert.Equal(CoverUpgradeStatus.Completed, store.Current.Status);
        var front = Assert.Single(Pictures(path), p => p.Type == TagLib.PictureType.FrontCover);
        Assert.Equal(small, front.Data.Data);
        Assert.Single(Pictures(path), p => p.Type == TagLib.PictureType.BackCover);
        Assert.False(worker.CanUndo);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_config, "cover-backups")));
    }

    [Fact]
    public async Task FullSizeEmbedsTheMasterAsFound()
    {
        var path = Song("Discovery", "One More Time", Jpeg(200, 10));
        var (worker, store) = Worker(new FixedFinder(new FoundCover(Jpeg(2400, 200), "iTunes", 2400)), fullSize: true);

        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Apply, FolderCovers: true));

        Assert.Equal(2400, FrontSide(path));
    }

    [Fact]
    public async Task ACoverAlreadyAsSharpIsLeftAndASongWithNoneGetsOne()
    {
        var sharp = Song("Homework", "Da Funk", Jpeg(1000, 10));
        var bare = Song("Alive 1997", "Rollin and Scratchin", null);
        var sharpBefore = File.ReadAllBytes(sharp);
        var (worker, store) = Worker(new FixedFinder(new FoundCover(Jpeg(1100, 200), "Deezer", 1100)));

        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Apply, FolderCovers: true));

        Assert.Equal(sharpBefore, File.ReadAllBytes(sharp));
        Assert.Equal(1100, FrontSide(bare));
        Assert.Equal((1, 1), (store.Current.Upgraded, store.Current.Kept));
    }

    [Fact]
    public async Task ASoftFolderJpegIsUpgradedOnlyWhenAsked()
    {
        var path = Song("Discovery", "One More Time", Jpeg(1200, 10));
        var folderFile = Path.Combine(Path.GetDirectoryName(path)!, "folder.jpg");
        var soft = Jpeg(300, 50);
        File.WriteAllBytes(folderFile, soft);
        var found = new FoundCover(Jpeg(3000, 200), "iTunes", 3000);

        var (worker, store) = Worker(new FixedFinder(found));
        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Apply, FolderCovers: false));
        Assert.Equal(soft, File.ReadAllBytes(folderFile));

        (worker, store) = Worker(new FixedFinder(found));
        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Apply, FolderCovers: true));
        Assert.Equal((3000, 3000), CoverImage.Measure(File.ReadAllBytes(folderFile)));
        Assert.True(Assert.Single(store.Current.Preview).FolderCover);

        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Apply, FolderCovers: true, Undo: true));
        Assert.Equal(soft, File.ReadAllBytes(folderFile));
    }

    [Fact]
    public async Task AScanListsOnlySoftAlbumsAndLooksNothingUp()
    {
        Song("Discovery", "One More Time", Jpeg(300, 10));
        Song("Homework", "Da Funk", Jpeg(1200, 10));
        Song("Alive 1997", "Rollin and Scratchin", null);
        var finder = new FixedFinder(new FoundCover(Jpeg(3000, 200), "iTunes", 3000));
        var (worker, store) = Worker(finder);

        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Scan, FolderCovers: true));

        var run = store.Current;
        Assert.Equal(CoverUpgradeStatus.Completed, run.Status);
        Assert.Empty(finder.Asked);
        Assert.Equal((2, 1), (run.Soft, run.Kept));
        Assert.Equal(["Alive 1997", "Discovery"], run.Preview.Select(r => r.Album!).Order().ToArray());
        Assert.All(run.Preview, row => Assert.Equal("soft", row.Result));
        Assert.Equal(0, run.Preview.Single(r => r.Album == "Alive 1997").FromSide);
        Assert.NotNull(worker.Thumbnail(run.Preview.Single(r => r.Album == "Discovery").Id));
        Assert.Null(worker.Thumbnail(run.Preview.Single(r => r.Album == "Alive 1997").Id));
    }

    [Fact]
    public async Task OnlyThePickedAlbumsAreLookedUpAndUpgraded()
    {
        var discovery = Song("Discovery", "One More Time", Jpeg(300, 10));
        var homework = Song("Homework", "Da Funk", Jpeg(300, 10));
        var homeworkBefore = File.ReadAllBytes(homework);
        var finder = new FixedFinder(new FoundCover(Jpeg(3000, 200), "iTunes", 3000));
        var (worker, store) = Worker(finder);
        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Scan, FolderCovers: true));
        var pick = store.Current.Preview.Single(r => r.Album == "Discovery").Id;

        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Apply,
            FolderCovers: true, Albums: [pick]));

        Assert.Equal(1500, FrontSide(discovery));
        Assert.Equal(homeworkBefore, File.ReadAllBytes(homework));
        Assert.Equal(["Discovery"], finder.Asked.Select(q => q.Album!).ToArray());
        var row = Assert.Single(store.Current.Preview);
        Assert.Equal((pick, "upgraded", 300, 3000), (row.Id, row.Result, row.FromSide, row.ToSide));
    }

    [Fact]
    public async Task APickedAlbumWithNothingLargerStaysOnTheListAsNone()
    {
        Song("Discovery", "One More Time", Jpeg(300, 10));
        var (worker, store) = Worker(new FixedFinder(null));
        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Scan, FolderCovers: true));
        var pick = Assert.Single(store.Current.Preview).Id;

        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Preview,
            FolderCovers: true, Albums: [pick]));

        var row = Assert.Single(store.Current.Preview);
        Assert.Equal(("none", 0), (row.Result, row.Files));
        Assert.Equal(1, store.Current.Kept);
    }

    [Fact]
    public async Task APreviewKeepsASmallCopyOfEachCoverItFoundAndANewScanForgetsThem()
    {
        Song("Discovery", "One More Time", Jpeg(300, 10));
        var (worker, store) = Worker(new FixedFinder(new FoundCover(Jpeg(3000, 200), "iTunes", 3000)));
        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Scan, FolderCovers: true));
        var id = Assert.Single(store.Current.Preview).Id;
        Assert.Null(worker.FoundThumbnail(id));

        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Preview,
            FolderCovers: true, Albums: [id]));
        var found = worker.FoundThumbnail(id);
        Assert.NotNull(found);
        Assert.Equal((CoverUpgradeWorker.ThumbSide, CoverUpgradeWorker.ThumbSide), CoverImage.Measure(found!));

        await Run(worker, store, new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, CoverUpgradeMode.Scan, FolderCovers: true));
        Assert.Null(worker.FoundThumbnail(id));
    }
}
