using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.CoverArt;
using Octo.Services.Soulseek;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Octo.Tests;

/// <summary>
/// The download-time cover chain (#51): the right release's cover when a fingerprint named it,
/// the catalog and the aggregator next, and a cover that is not square never passes for one.
/// </summary>
public class CoverChainTests
{
    private static byte[] Jpeg(int width, int height, Rgba32? centre = null)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0));
        if (centre is { } colour)
        {
            var side = Math.Min(width, height);
            image.Mutate(ctx => ctx.Fill(Color.FromPixel(colour),
                new RectangleF((width - side) / 2f, (height - side) / 2f, side, side)));
        }
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        return stream.ToArray();
    }

    private static readonly byte[] Square = Jpeg(600, 600, new Rgba32(200, 40, 40));
    private static readonly byte[] Sharp = Jpeg(1200, 1200, new Rgba32(40, 40, 200));
    private static readonly byte[] Catalog = Jpeg(1000, 1000, new Rgba32(200, 200, 40));
    private static readonly byte[] Thumbnail = Jpeg(200, 200, new Rgba32(90, 90, 90));
    private static readonly byte[] VideoFrame = Jpeg(1280, 720, new Rgba32(40, 200, 40));

    // ---- CoverImage -----------------------------------------------------------------------

    [Fact]
    public void IsUsable_SixteenByNine_IsNotASquareCover()
    {
        Assert.False(CoverImage.IsUsable(VideoFrame, requireSquare: true));
        Assert.True(CoverImage.IsUsable(VideoFrame, requireSquare: false));
    }

    [Fact]
    public void IsUsable_NearlySquare_Passes() =>
        Assert.True(CoverImage.IsUsable(Jpeg(600, 590), requireSquare: true));

    [Fact]
    public void IsUsable_TooSmallOrNotAnImage_Fails()
    {
        Assert.False(CoverImage.IsUsable(Jpeg(100, 100), requireSquare: true));
        Assert.False(CoverImage.IsUsable("not an image"u8.ToArray(), requireSquare: false));
        Assert.False(CoverImage.IsUsable(null, requireSquare: false));
    }

    /// <summary>A YouTube "Topic" frame letterboxes the real cover; its centre square is that cover.</summary>
    [Fact]
    public void CropToSquare_Letterbox_ReturnsTheCentre()
    {
        var cropped = CoverImage.CropToSquare(VideoFrame);

        Assert.NotNull(cropped);
        using var image = Image.Load<Rgba32>(cropped);
        Assert.Equal(720, image.Width);
        Assert.Equal(720, image.Height);
        var middle = image[360, 360];
        Assert.True(middle.G > 150 && middle.R < 100, $"centre pixel was {middle}");
    }

    // ---- DownloadCoverResolver ------------------------------------------------------------

    private sealed class FixedSource(byte[]? bytes) : ICoverArtSource
    {
        public string Name => "fixed";
        public int Calls { get; private set; }

        public Task<byte[]?> TryFetchAsync(SoulseekRouting routing, bool background = false, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(bytes);
        }
    }

    private static IHttpClientFactory Http(Func<HttpRequestMessage, HttpResponseMessage> answer, List<string>? calls = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                calls?.Add(request.RequestUri!.ToString());
                return answer(request);
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() =>
            new HttpClient(handler.Object) { BaseAddress = new Uri("https://coverartarchive.org/") });
        return factory.Object;
    }

    private static HttpResponseMessage Picture(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static DownloadCoverResolver Resolver(IHttpClientFactory http, ICoverArtSource aggregated, MetadataSettings? settings = null) =>
        new(new CoverArtArchiveLookup(http, NullLogger<CoverArtArchiveLookup>.Instance),
            new CoverArtAggregator([aggregated], NullLogger<CoverArtAggregator>.Instance),
            http, TestOptions.Monitor(settings ?? new MetadataSettings()), NullLogger<DownloadCoverResolver>.Instance);

    [Fact]
    public async Task Resolve_ArchiveFirst_WhenTheAlbumIsTheMatchedRelease()
    {
        var calls = new List<string>();
        var http = Http(request => request.RequestUri!.ToString().Contains("coverartarchive")
            ? Picture(Sharp) : new HttpResponseMessage(HttpStatusCode.NotFound), calls);
        var song = new Song
        {
            Artist = "M83", Title = "Lower Your Eyelids", Album = "Before the Dawn Heals Us",
            MusicBrainzReleaseId = "rel-1", MusicBrainzAlbumTitle = "Before the Dawn Heals Us",
            CoverArtUrlLarge = "https://deezer.example/cover.jpg",
        };

        var choice = await Resolver(http, new FixedSource(null)).ResolveAsync(song, null, CancellationToken.None);

        Assert.Equal("Cover Art Archive", choice!.Source);
        Assert.Contains(calls, url => url.Contains("release/rel-1/front-1200"));
        Assert.DoesNotContain(calls, url => url.Contains("deezer.example"));
    }

    private static Song MatchedRelease() => new()
    {
        Artist = "M83", Title = "Lower Your Eyelids", Album = "Before the Dawn Heals Us",
        MusicBrainzReleaseId = "rel-1", MusicBrainzAlbumTitle = "Before the Dawn Heals Us",
        CoverArtUrlLarge = "https://deezer.example/cover.jpg",
    };

    /// <summary>The soft cover Brandon got: the archive's 500 px scan beat the catalog's 1000.</summary>
    [Fact]
    public async Task Resolve_ASmallArchiveScanLosesToTheCatalogsLargerCover()
    {
        var http = Http(request => request.RequestUri!.ToString() switch
        {
            var url when url.Contains("front-1200") => new HttpResponseMessage(HttpStatusCode.NotFound),
            var url when url.Contains("front-500") => Picture(Jpeg(500, 500)),
            var url when url.Contains("deezer.example") => Picture(Catalog),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var choice = await Resolver(http, new FixedSource(null)).ResolveAsync(MatchedRelease(), null, CancellationToken.None);

        Assert.Equal("the catalog", choice!.Source);
        Assert.Equal((1000, 1000), CoverImage.Measure(choice.Bytes));
    }

    [Fact]
    public async Task Resolve_TheArchivesSmallerThumbnailIsUsedWhenItHasNoLargeOne()
    {
        var calls = new List<string>();
        var http = Http(request => request.RequestUri!.ToString().Contains("front-500")
            ? Picture(Square) : new HttpResponseMessage(HttpStatusCode.NotFound), calls);

        var choice = await Resolver(http, new FixedSource(null)).ResolveAsync(MatchedRelease(), null, CancellationToken.None);

        Assert.Equal("Cover Art Archive", choice!.Source);
        Assert.Equal(["release/rel-1/front-1200", "release/rel-1/front-500"],
            calls.Where(url => url.Contains("coverartarchive")).Select(url => new Uri(url).AbsolutePath.TrimStart('/')).ToList());
    }

    [Fact]
    public async Task Resolve_ASmallCatalogCoverKeepsLookingAndTheSearchsLargerOneWins()
    {
        var http = Http(_ => Picture(Square));
        var search = new FixedSource(Catalog);
        var song = new Song { Artist = "A", Title = "T", CoverArtUrlLarge = "https://deezer.example/cover.jpg" };

        var choice = await Resolver(http, search).ResolveAsync(song, Thumbnail, CancellationToken.None);

        Assert.Equal("a cover search", choice!.Source);
        Assert.False(choice.KeepsExisting);
    }

    [Fact]
    public async Task Resolve_APeersTinyThumbnailNeverBeatsARealCover()
    {
        var http = Http(_ => Picture(Square));
        var song = new Song { Artist = "A", Title = "T", CoverArtUrlLarge = "https://deezer.example/cover.jpg" };

        var choice = await Resolver(http, new FixedSource(null)).ResolveAsync(song, Thumbnail, CancellationToken.None);

        Assert.Equal("the catalog", choice!.Source);
        Assert.False(choice.KeepsExisting);
    }

    [Fact]
    public async Task Resolve_TheFilesOwnArtStaysWhenItIsTheLargest()
    {
        var http = Http(_ => Picture(Square));
        var song = new Song { Artist = "A", Title = "T", CoverArtUrlLarge = "https://deezer.example/cover.jpg" };

        var choice = await Resolver(http, new FixedSource(null)).ResolveAsync(song, Sharp, CancellationToken.None);

        Assert.True(choice!.KeepsExisting);
        Assert.Equal("the file itself", choice.Source);
    }

    [Fact]
    public async Task Resolve_ASharpCatalogCoverStopsTheSearch()
    {
        var http = Http(_ => Picture(Catalog));
        var search = new FixedSource(Sharp);
        var song = new Song { Artist = "A", Title = "T", CoverArtUrlLarge = "https://deezer.example/cover.jpg" };

        var choice = await Resolver(http, search).ResolveAsync(song, null, CancellationToken.None);

        Assert.Equal("the catalog", choice!.Source);
        Assert.Equal(0, search.Calls);
    }

    /// <summary>A download tagged with a compilation's name must not get the original album's cover.</summary>
    [Fact]
    public async Task Resolve_ArchiveSkipped_WhenTheAlbumIsAnotherRelease()
    {
        var calls = new List<string>();
        var http = Http(request => Picture(Square), calls);
        var song = new Song
        {
            Artist = "A", Title = "T", Album = "Now 42",
            MusicBrainzReleaseId = "rel-1", MusicBrainzAlbumTitle = "The Real Album",
            CoverArtUrlLarge = "https://deezer.example/cover.jpg",
        };

        var choice = await Resolver(http, new FixedSource(null)).ResolveAsync(song, null, CancellationToken.None);

        Assert.Equal("the catalog", choice!.Source);
        Assert.DoesNotContain(calls, url => url.Contains("coverartarchive"));
    }

    [Fact]
    public async Task Resolve_NonSquareCatalogCover_FallsThroughToTheSearch()
    {
        var http = Http(_ => Picture(VideoFrame));
        var search = new FixedSource(Square);
        var song = new Song { Artist = "A", Title = "T", CoverArtUrlLarge = "https://i.ytimg.example/maxres.jpg" };

        var choice = await Resolver(http, search).ResolveAsync(song, null, CancellationToken.None);

        Assert.Equal("a cover search", choice!.Source);
        Assert.Equal(1, search.Calls);
    }

    [Fact]
    public async Task Resolve_NothingFound_UsesTheCentreOfTheVideoFrame()
    {
        var http = Http(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var song = new Song { Artist = "A", Title = "T" };

        var choice = await Resolver(http, new FixedSource(null)).ResolveAsync(song, VideoFrame, CancellationToken.None);

        Assert.NotNull(choice);
        Assert.False(choice.KeepsExisting);
        Assert.True(CoverImage.IsUsable(choice.Bytes, requireSquare: true));
    }

    [Fact]
    public async Task Resolve_ReplaceVideoCoversOff_KeepsTheFrame()
    {
        var http = Http(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var song = new Song { Artist = "A", Title = "T" };

        var choice = await Resolver(http, new FixedSource(null), new MetadataSettings { ReplaceVideoCovers = false })
            .ResolveAsync(song, VideoFrame, CancellationToken.None);

        Assert.True(choice!.KeepsExisting);
    }

    [Fact]
    public async Task Resolve_SquareCoverAlreadyOnTheFile_IsKeptWithoutARewrite()
    {
        var http = Http(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var choice = await Resolver(http, new FixedSource(null))
            .ResolveAsync(new Song { Artist = "A", Title = "T" }, Square, CancellationToken.None);

        Assert.True(choice!.KeepsExisting);
        Assert.Equal("the file itself", choice.Source);
    }

    [Fact]
    public async Task Resolve_NothingAnywhere_IsNull()
    {
        var http = Http(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await Resolver(http, new FixedSource(null))
            .ResolveAsync(new Song { Artist = "A", Title = "T" }, null, CancellationToken.None));
    }
}
