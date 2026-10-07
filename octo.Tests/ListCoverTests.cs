using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Services.CoverArt;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using Xunit.Abstractions;

namespace Octo.Tests;

/// <summary>
/// The colour rules the covers share with the Octo apps, and which painted background a
/// list's music gets.
/// </summary>
public class CoverColourTests
{
    private static CoverBook Book => CoverBook.Default;

    /// <summary>FNV-1a 64 over UTF-8, shifted right once, as the apps hash a list.</summary>
    [Theory]
    [InlineData("", 0xcbf29ce484222325UL >> 1)]
    [InlineData("a", 0xaf63dc4c8601ec8cUL >> 1)]
    public void CoverHash_IsFnv1aShiftedRight(string text, ulong expected) =>
        Assert.Equal((long)expected, CoverColours.CoverHash(text));

    /// <summary>The design's check value for the pick hash, and the pick and turn it gives.</summary>
    [Fact]
    public void CoverPick_IsFnv1aThenFmix64ShiftedRight()
    {
        Assert.Equal(0x098f28ee76f647ceL, CoverColours.CoverPick("pl-1"));
        Assert.Equal(15, CoverBackgrounds.Choose(Book, null, "pl-1"));
        Assert.Equal(0, CoverBackgrounds.Orientation(Book, "pl-1"));
    }

    /// <summary>
    /// Numbered lists ("1" to "12", "p1" to "p12"), whose ids differ only in their last letters,
    /// still look apart: with no music every one differs, the turns vary, and twelve lists of one
    /// warm colour repeat a look at most twice.
    /// </summary>
    [Fact]
    public void NumberedLists_LookApart()
    {
        var warm = CoverMusic.FromCovers([new[] { new Swatch(unchecked((int)0xFFE0701F), 1f) }]);
        foreach (var ids in new[] { Enumerable.Range(1, 12).Select(i => $"{i}").ToList(), Enumerable.Range(1, 12).Select(i => $"p{i}").ToList() })
        {
            var none = ids.Select(id => (CoverBackgrounds.Choose(Book, null, id), CoverBackgrounds.Orientation(Book, id))).ToList();
            Assert.Equal(ids.Count, none.Distinct().Count());
            Assert.True(ids.Select(id => CoverBackgrounds.Orientation(Book, id)).Distinct().Count() >= 5);
            var picks = ids.Select(id => (CoverBackgrounds.Choose(Book, warm, id), CoverBackgrounds.Orientation(Book, id))).ToList();
            Assert.True(picks.Distinct().Count() >= ids.Count - 2, $"{ids[0]}: {picks.Distinct().Count()} looks for warm music");
        }
    }

    [Theory]
    [InlineData("#808080", 0.0)]
    [InlineData("#ff0000", 29.2)]
    [InlineData("#0000ff", 264.1)]
    public void Lch_HasTheOklabHueOfAColour(string hex, double hue)
    {
        var lch = CoverColours.ToLch(CoverColours.Hex(hex));
        if (lch.C > 0.01) Assert.InRange(lch.H, hue - 0.5, hue + 0.5);
        else Assert.True(lch.C < 0.001);
    }

    [Fact]
    public void Swatches_FindAPicturesColoursByShare()
    {
        var pixels = Enumerable.Range(0, 64 * 64).Select(i => i % 4 == 0 ? CoverColours.Hex("#1d3f8c") : CoverColours.Hex("#d9552b")).ToArray();

        var swatches = CoverColours.Swatches(pixels, step: 1);

        Assert.Equal(2, swatches.Count);
        Assert.Equal(CoverColours.Hex("#d9552b"), swatches[0].Argb);
        Assert.InRange(swatches[0].Share, 0.74f, 0.76f);
    }

    [Fact]
    public void Music_FromColourfulCovers_IsTheirStrongestColour_Rounded()
    {
        var warm = new List<IReadOnlyList<Swatch>>
        {
            new[] { new Swatch(CoverColours.Hex("#D9552B"), 0.6f), new Swatch(CoverColours.Hex("#2B1A12"), 0.3f) },
            new[] { new Swatch(CoverColours.Hex("#1D3F8C"), 0.5f) },
        };

        var music = CoverMusic.FromCovers(warm)!;
        var orange = CoverColours.ToLch(CoverColours.Hex("#D9552B"));

        Assert.Equal((int)Math.Floor(orange.H + 0.5) % 360, music.Hue);
        Assert.Equal(Math.Floor(orange.C * 1000 + 0.5) / 1000, music.Chroma);
        Assert.Equal(Math.Floor(orange.L * 1000 + 0.5) / 1000, music.Lightness);
    }

    [Fact]
    public void Music_FromGreyCoversOrNone_IsNothing()
    {
        Assert.Null(CoverMusic.FromCovers([new[] { new Swatch(CoverColours.Hex("#808080"), 0.9f) }]));
        Assert.Null(CoverMusic.FromCovers([]));
    }

    /// <summary>
    /// The picks and turns the design's rule gives, worked out apart from this code (by a short script
    /// following cover-design.json "background" word for word), so the server and the apps give
    /// a list the same background.
    /// </summary>
    [Theory]
    [InlineData("Daft Punk Radio", 97, 0.143, 0.861, "tangerine.webp", 7)]
    [InlineData("Rock Mix", 26, 0.14, 0.62, "afterglow.webp", 3)]
    [InlineData("Your Mix", 262, 0.2, 0.5, "night-swim.webp", 4)]
    [InlineData("1990s Mix", 134, 0.14, 0.62, "amber-night.webp", 0)]
    [InlineData("Polka Mix", -1, 0, 0, "bubblegum.webp", 0)]
    [InlineData("pl-1", 30, 0.1, 0.4, "coral.webp", 0)]
    [InlineData("Daft Punk Radio", 261, 0.043, 0.722, "peach.webp", 7)]
    [InlineData("Metal Mix", 40, 0.163, 0.601, "firewave.webp", 6)]
    public void Background_IsTheDesignsPick(string id, int hue, double chroma, double lightness, string file, int orientation)
    {
        var music = hue < 0 ? null : CoverMusic.Of(hue, chroma, lightness);

        Assert.Equal(file, Book.Backgrounds[CoverBackgrounds.Choose(Book, music, id)].File);
        Assert.Equal(orientation, CoverBackgrounds.Orientation(Book, id));
    }

    /// <summary>Music too dull to say much (chroma under lowChromaAsGrey) picks as a list with no covers does.</summary>
    [Fact]
    public void Background_ForDullMusic_IsTheNamesPick()
    {
        var dull = CoverMusic.Of(261, Book.BackgroundChoice.LowChromaAsGrey - 0.001, 0.72);

        Assert.Equal(CoverBackgrounds.Choose(Book, null, "Daft Punk Radio"), CoverBackgrounds.Choose(Book, dull, "Daft Punk Radio"));
    }

    /// <summary>Music of a colour gets one of a few backgrounds of that colour, the same one for the same list.</summary>
    [Theory]
    [InlineData(200, 30, 40)]
    [InlineData(30, 60, 200)]
    [InlineData(40, 160, 70)]
    [InlineData(240, 200, 30)]
    [InlineData(150, 40, 190)]
    public void Background_MatchesTheMusicsColour(int r, int g, int b)
    {
        var lch = CoverColours.ToLch(unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b);
        var music = CoverMusic.Of(lch.H, lch.C, lch.L);

        var picks = Enumerable.Range(0, 60).Select(i => CoverBackgrounds.Choose(Book, music, $"pl-{i}")).Distinct().ToList();

        Assert.InRange(picks.Count, 2, Book.BackgroundChoice.Nearest);
        foreach (var pick in picks)
        {
            var background = Book.Backgrounds[pick];
            var nearest = background.Hues.Min(h => CoverColours.HueDistance(h.H, music.Hue));
            Assert.True(nearest < 50, $"{background.Name} for hue {music.Hue}: {nearest:F0}");
        }
        Assert.Equal(CoverBackgrounds.Choose(Book, music, "pl-1"), CoverBackgrounds.Choose(Book, music, "pl-1"));
    }

    [Fact]
    public void Background_WithoutMusic_IsAnyOne_AlwaysTheSame()
    {
        var picks = Enumerable.Range(0, 400).Select(i => CoverBackgrounds.Choose(Book, null, $"pl-{i}")).ToList();

        Assert.Equal(picks, Enumerable.Range(0, 400).Select(i => CoverBackgrounds.Choose(Book, null, $"pl-{i}")));
        var used = picks.GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
        Assert.True(used.Count >= Book.Backgrounds.Count - 2);
        Assert.All(used.Values, count => Assert.True(count < 400 / Book.Backgrounds.Count * 4));
    }

    /// <summary>The music colours the contact sheet's 24 lists get from its seed covers, or their genre's; -1 for none.</summary>
    private static readonly (string Name, int Hue, double Chroma, double Lightness)[] SheetLists =
    [
        ("Daft Punk Radio", 261, 0.043, 0.722), ("Billie Eilish Radio", 63, 0.061, 0.575),
        ("Tame Impala Radio", 318, 0.041, 0.453), ("Radiohead Radio", 47, 0.159, 0.657),
        ("Kendrick Lamar Radio", 4, 0.068, 0.41), ("Your Mix", 241, 0.039, 0.732),
        ("Discovery Mix", 30, 0.225, 0.581), ("Bad Bunny Radio", 30, 0.225, 0.581),
        ("Jazz & Blues Mix", 225, 0.14, 0.62), ("Metal Mix", 40, 0.163, 0.601),
        ("1970s Mix", 75, 0.14, 0.62), ("Rock Mix", 26, 0.14, 0.62),
        ("Hip-Hop Mix", 61, 0.14, 0.62), ("1990s Mix", 134, 0.14, 0.62),
        ("2020s Mix", 168, 0.14, 0.62), ("Electronic Radio", 250, 0.14, 0.62),
        ("Polka Mix", -1, 0, 0), ("Red Hot Chili Peppers Radio", -1, 0, 0),
        ("The Most Unreasonably Long Playlist Name Anyone Ever Typed Into A Music Server Radio", -1, 0, 0),
        ("宇多田ヒカル Radio", -1, 0, 0), ("블랙핑크 BLACKPINK Radio", 5, 0.041, 0.336),
        ("فيروز Radio", 69, 0.065, 0.682), ("Late Night 🌙 Chill Mix", 206, 0.14, 0.62),
        ("Ünïcödé Café Mix", -1, 0, 0),
    ];

    /// <summary>The contact sheet's 24 lists look apart: no background turned the same way twice, and none used more than three times.</summary>
    [Fact]
    public void SheetLists_LookApart()
    {
        var looks = SheetLists.Select(list => (
            Background: CoverBackgrounds.Choose(Book, list.Hue < 0 ? null : CoverMusic.Of(list.Hue, list.Chroma, list.Lightness), list.Name),
            Orientation: CoverBackgrounds.Orientation(Book, list.Name))).ToList();

        Assert.Equal(looks.Count, looks.Distinct().Count());
        var most = looks.GroupBy(look => look.Background).MaxBy(group => group.Count())!;
        Assert.True(most.Count() <= 3, $"{Book.Backgrounds[most.Key].Name} is used {most.Count()} times");
    }

    /// <summary>A turned background is the same pixels, quarter turned clockwise and then mirrored.</summary>
    [Fact]
    public void Turn_QuarterTurnsClockwise_ThenMirrors()
    {
        using var plain = CoverBackgrounds.Load(Book, 0, 600);
        foreach (var v in Enumerable.Range(0, 8))
        {
            using var turned = CoverBackgrounds.Load(Book, 0, 600);
            CoverBackgrounds.Turn(turned, v);
            foreach (var (x, y) in new[] { (0, 0), (17, 250), (599, 3), (321, 598) })
            {
                // Where (x, y) of the turned picture came from.
                var (sx, sy) = (v >= 4 ? 599 - x : x, y);
                for (var t = 0; t < v % 4; t++) (sx, sy) = (sy, 599 - sx);
                Assert.Equal(plain[sx, sy], turned[x, y]);
            }
        }
        Assert.Equal(Enumerable.Range(0, 8), Enumerable.Range(0, 400).Select(i => CoverBackgrounds.Orientation(Book, $"pl-{i}")).Distinct().Order());
    }

    /// <summary>Sizes other than the file's: halved while that leaves enough, then each pixel the mean of the area it covers.</summary>
    [Fact]
    public void Background_AtOtherSizes_IsTheAreaMean()
    {
        using var at600 = CoverBackgrounds.Load(Book, 0, 600);
        using var at800 = CoverBackgrounds.Load(Book, 0, 800);
        using var at1200 = CoverBackgrounds.Load(Book, 0, 1200);

        // 800 from 1200: output pixel 1 covers source pixels 1.5 to 3, half of 1 and all of 2.
        var (a, b) = (at1200[1, 0], at1200[2, 0]);
        var (c, d) = (at1200[1, 1], at1200[2, 1]);
        var want = (a.R * 0.5 / 1.5 + b.R / 1.5) * (1 / 1.5) + (c.R * 0.5 / 1.5 + d.R / 1.5) * (0.5 / 1.5);
        Assert.InRange(at800[1, 0].R - want, -0.51, 0.51);
        var q = new[] { at1200[0, 0], at1200[1, 0], at1200[0, 1], at1200[1, 1] };
        Assert.Equal((q.Sum(p => p.R) + 2) / 4, at600[0, 0].R);
    }

    [Fact]
    public void Library_HasEveryBackgroundItNames_AndTheyDecode()
    {
        Assert.Equal(48, Book.Backgrounds.Count);
        foreach (var index in Enumerable.Range(0, Book.Backgrounds.Count))
        {
            using var image = CoverBackgrounds.Load(Book, index, 600);
            Assert.Equal(600, image.Width);
        }
    }
}

/// <summary>
/// The golden covers from the design's reference (tools/cover-art/reference.py in the Octo
/// app's repo, copied as CoverGolden/samples.json): the words' sizes and boxes, and the veiled
/// background before any words, sampled across each cover.
/// </summary>
public class CoverGoldenTests(ITestOutputHelper output)
{
    private static CoverBook Book => CoverBook.Default;

    public static TheoryData<int> Cases() => new() { 0, 1, 2, 3, 4, 5 };

    private static System.Text.Json.JsonElement Golden(int index)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CoverGolden", "samples.json"));
        return System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("covers")[index];
    }

    private static (CoverArt Art, CoverSpec Spec) Compose(System.Text.Json.JsonElement golden)
    {
        var side = golden.GetProperty("side").GetInt32();
        string? Text(string key) => golden.GetProperty(key).ValueKind == System.Text.Json.JsonValueKind.Null ? null : golden.GetProperty(key).GetString();
        var spec = new CoverSpec(golden.GetProperty("name").GetString()!, golden.GetProperty("name").GetString()!, Text("line"), Text("footer"), null);
        var file = golden.GetProperty("background").GetString();
        var index = Book.Backgrounds.Select((b, i) => (b, i)).Single(pair => pair.b.File == file).i;
        return (new CoverArt(side, index, 0, CoverLayout.Words(spec, side, new CoverTypesetter(), Book)), spec);
    }

    /// <summary>
    /// Sizes, lines, left edges, tops and heights match the reference to the pixel. Right edges
    /// come from each engine's own widths: the reference's Pillow sets Inter without its kerning,
    /// SixLabors with it, so a right edge may sit a few pixels short of the reference's.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Words_MatchTheReference(int index)
    {
        var golden = Golden(index);
        var (art, _) = Compose(golden);
        var expected = golden.GetProperty("words").EnumerateArray().ToList();
        var actual = art.Words.OrderBy(w => w.Role).ToList();
        Assert.Equal(expected.Count, actual.Count);
        foreach (var want in expected)
        {
            var role = want.GetProperty("role").GetString() switch { "title" => WordsRole.Title, "line" => WordsRole.Line, _ => WordsRole.Footer };
            var got = actual.Single(w => w.Role == role);
            var (lines, _) = new CoverTypesetter().Lines(got.Text, got.Type, got.Width);
            Assert.Equal(want.GetProperty("size").GetInt32(), (int)got.Type.SizePx);
            Assert.Equal(want.GetProperty("lines").EnumerateArray().Select(l => l.GetString()), lines.Select(l => l.Text));
            Assert.Equal(want.GetProperty("x").GetDouble(), got.Inked[0], 0.01);
            Assert.Equal(want.GetProperty("top").GetDouble(), got.Top, 0.01);
            Assert.Equal(want.GetProperty("height").GetDouble(), got.Measured.Height, 0.01);
            // Kerning moves a right edge by up to about 2% of the line.
            var right = want.GetProperty("right").GetDouble();
            var slack = Math.Max(2, 0.02 * (right - want.GetProperty("x").GetDouble()));
            output.WriteLine($"{golden.GetProperty("file").GetString()} {role}: right {got.Inked[2]:F1}, reference {right}");
            Assert.InRange(got.Inked[2], right - slack, right + slack);
        }
    }

    /// <summary>
    /// The veiled background matches the reference within 2 levels a channel at every sampled
    /// point, over the reference's own word boxes, so the veil's maths is checked apart from the
    /// few pixels kerning moves a right edge.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Veil_MatchesTheReference(int index)
    {
        var golden = Golden(index);
        var (art, _) = Compose(golden);
        var side = art.Side;
        var boxes = golden.GetProperty("words").EnumerateArray().Select(w =>
        {
            var role = w.GetProperty("role").GetString() switch { "title" => WordsRole.Title, "line" => WordsRole.Line, _ => WordsRole.Footer };
            var (x, top, height, right) = (w.GetProperty("x").GetSingle(), w.GetProperty("top").GetSingle(),
                w.GetProperty("height").GetSingle(), w.GetProperty("right").GetSingle());
            var type = new CoverType(w.GetProperty("size").GetSingle(), 400, 0, 1, 1);
            return new CoverWords("", type, x, top, side, CoverAlign.Left, CoverColours.White, new Measured(1, right - x, height, false), role);
        }).ToList();
        using var veiled = CoverBackgrounds.Load(Book, art.Background, side);
        CoverVeil.Apply(veiled, CoverVeil.Regions(Book, boxes, side), Book.Veil);
        var worst = 0;
        foreach (var sample in golden.GetProperty("samples").EnumerateArray())
        {
            var (x, y) = (sample.GetProperty("x").GetInt32(), sample.GetProperty("y").GetInt32());
            var want = sample.GetProperty("rgb").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var got = veiled[x, y];
            var off = Math.Max(Math.Abs(got.R - want[0]), Math.Max(Math.Abs(got.G - want[1]), Math.Abs(got.B - want[2])));
            worst = Math.Max(worst, off);
            Assert.True(off <= 2, $"{golden.GetProperty("file").GetString()} at {x},{y}: {got} against [{string.Join(",", want)}]");
        }
        output.WriteLine($"{golden.GetProperty("file").GetString()}: worst channel difference {worst}");
    }

    /// <summary>
    /// The whole cover as the server lays it out: with its own (kerned) widths the veil's edge
    /// moves by a few pixels, which moves a sample by a few levels at most.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Veil_WithTheServersOwnWidths_StaysClose(int index)
    {
        var golden = Golden(index);
        var (art, _) = Compose(golden);
        using var veiled = CoverPainter.Paint(Book, art, new CoverTypesetter(), drawWords: false);
        foreach (var sample in golden.GetProperty("samples").EnumerateArray())
        {
            var (x, y) = (sample.GetProperty("x").GetInt32(), sample.GetProperty("y").GetInt32());
            var want = sample.GetProperty("rgb").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var got = veiled[x, y];
            var off = Math.Max(Math.Abs(got.R - want[0]), Math.Max(Math.Abs(got.G - want[1]), Math.Abs(got.B - want[2])));
            Assert.True(off <= 4, $"{golden.GetProperty("file").GetString()} at {x},{y}: {got} against [{string.Join(",", want)}]");
        }
    }
}

/// <summary>
/// List covers as the server serves them: the covers folder wins, a station is a plain
/// playlist cover with no badge, the words always read, long and foreign names fit, the same
/// list always gets the same bytes, and a cover is quick to draw.
/// </summary>
public class ListCoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-covers-" + Guid.NewGuid());
    private readonly ITestOutputHelper _output;

    public ListCoverTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(CoversDirectory);
    }

    private string CoversDirectory => Path.Combine(_root, "config", "covers");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private CoverArtService Service() => new(NullLogger<CoverArtService>.Instance, CoversDirectory);

    private static Rgb24 Pixel(byte[] jpeg, int x, int y)
    {
        using var image = Image.Load<Rgb24>(jpeg);
        return image[x, y];
    }

    private static bool Near(Rgb24 pixel, string hex, int tolerance = 24)
    {
        var expected = Color.ParseHex(hex).ToPixel<Rgb24>();
        return Math.Abs(pixel.R - expected.R) <= tolerance && Math.Abs(pixel.G - expected.G) <= tolerance
            && Math.Abs(pixel.B - expected.B) <= tolerance;
    }

    /// <summary>Pixels in a square where two covers differ clearly, not just by JPEG noise.</summary>
    private static int Changed(byte[] a, byte[] b, int x0, int y0, int x1, int y1)
    {
        using var left = Image.Load<Rgb24>(a);
        using var right = Image.Load<Rgb24>(b);
        var changed = 0;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var p = left[x, y];
            var q = right[x, y];
            if (Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B))) > 40) changed++;
        }
        return changed;
    }

    private static byte[] Picture(string hex, string? second = null)
    {
        using var image = new Image<Rgb24>(120, 120, Color.ParseHex(hex).ToPixel<Rgb24>());
        if (second is not null)
            image.Mutate(ctx => ctx.Fill(Color.ParseHex(second), new SixLabors.ImageSharp.Drawing.RectangularPolygon(0, 80, 120, 40)));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static Func<CancellationToken, Task<IReadOnlyList<CoverSeed>>> Seeds(params byte[][] pictures) =>
        _ => Task.FromResult<IReadOnlyList<CoverSeed>>(pictures
            .Select((bytes, i) => new CoverSeed($"seed{i}-{bytes.Length}-{bytes[^5]}", _ => Task.FromResult<byte[]?>(bytes)))
            .ToList());

    // ------------------------------------------------------------ covers folder

    [Fact]
    public void Override_IsUsedAsItIs_AndAReplacementShowsWithoutARestart()
    {
        var service = Service();
        var path = Path.Combine(CoversDirectory, "Rock Mix.png");
        using (var green = new Image<Rgb24>(120, 60, new Rgb24(0, 255, 0))) green.SaveAsPng(path);

        var first = service.GetNamedCover("Rock Mix", "Rock");
        using (var picture = Image.Load<Rgb24>(first)) Assert.Equal(600, picture.Width);
        Assert.True(Near(Pixel(first, 300, 300), "#00FF00"));

        using (var blue = new Image<Rgb24>(60, 60, new Rgb24(0, 0, 255))) blue.SaveAsPng(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.True(Near(Pixel(service.GetNamedCover("Rock Mix", "Rock"), 300, 300), "#0000FF"));
    }

    /// <summary>A picture in the covers folder beats colours from the music too.</summary>
    [Fact]
    public async Task Override_BeatsSeedColours_AndIsSizedAsAsked()
    {
        using (var green = new Image<Rgb24>(60, 60, new Rgb24(0, 255, 0)))
            green.SaveAsPng(Path.Combine(CoversDirectory, "Daft Punk Radio.png"));

        var bytes = await Service().GetListCoverAsync(
            new ListCover("Daft Punk Radio", null, ListKinds.Radio, Seeds(Picture("#C08020"))), 900);

        using var image = Image.Load<Rgb24>(bytes);
        Assert.Equal(900, image.Width);
        Assert.True(Near(image[450, 450], "#00FF00"));
    }

    [Fact]
    public void Override_ByGenre_CoversEveryListOfIt()
    {
        using (var green = new Image<Rgb24>(60, 60, new Rgb24(0, 255, 0)))
            green.SaveAsPng(Path.Combine(CoversDirectory, "Rock.png"));

        Assert.True(Near(Pixel(Service().GetNamedCover("Rock Mix", "Rock"), 300, 300), "#00FF00"));
    }

    /// <summary>Whatever a playlist is called, a cover is only ever read from the covers folder.</summary>
    [Fact]
    public void Override_NeverReachesOutsideTheCoversFolder()
    {
        var outside = Path.Combine(Path.GetDirectoryName(CoversDirectory)!, "escape.png");
        using (var green = new Image<Rgb24>(60, 60, new Rgb24(0, 255, 0))) green.SaveAsPng(outside);

        Assert.False(Near(Pixel(Service().GetNamedCover("../escape"), 300, 300), "#00FF00"));
    }

    // ------------------------------------------------------------ no badge

    /// <summary>
    /// A station is a playlist like a mix: its cover is exactly the design, with nothing added.
    /// The old badge sat top left at 28% of the cover; drawn on, it would show there.
    /// </summary>
    [Fact]
    public void StationCover_IsTheDesignAlone_WithNoBadge()
    {
        var service = Service();
        var station = service.GetRadioStationCover("Rock Radio");
        var design = service.Render(CoverArtService.Spec("Rock Radio", ListKinds.Radio, null,
            service.FallbackMusic("Rock Radio", "Rock Radio")), 600);

        Assert.Equal(design, station);
        var badged = service.AddOctoBadge(station);
        Assert.True(Changed(station, badged, 18, 18, 186, 186) > 1_000, "the badge should be visible when applied");
    }

    [Fact]
    public void RadioStationCovers_StayPlainAcrossConcurrentFirstRequests()
    {
        var service = Service();
        var covers = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(8)
            .Select(index => (Name: $"Station {index} Radio", Bytes: service.GetRadioStationCover($"Station {index} Radio")))
            .ToList();

        var fresh = Service();
        Assert.All(covers, cover => Assert.Equal(fresh.GetRadioStationCover(cover.Name), cover.Bytes));
    }

    /// <summary>A picture someone chose for a station is theirs, and is not stamped.</summary>
    [Fact]
    public void StationOverride_IsNotBadged()
    {
        var service = Service();
        using (var green = new Image<Rgb24>(60, 60, new Rgb24(0, 255, 0)))
            green.SaveAsPng(Path.Combine(CoversDirectory, "Rock Radio.png"));

        Assert.Equal(service.GetNamedCover("Rock Radio", null, null, ListKinds.Radio), service.GetRadioStationCover("Rock Radio"));
        Assert.True(Near(Pixel(service.GetRadioStationCover("Rock Radio"), 40, 40), "#00FF00", 8));
    }

    // ------------------------------------------------------------ palette sources

    [Fact]
    public async Task SeedCovers_ColourTheCover_AndANewSeedRedrawsIt()
    {
        var service = Service();
        var plain = await service.GetListCoverAsync(new ListCover("Daft Punk Radio", null, ListKinds.Radio));
        var gold = await service.GetListCoverAsync(new ListCover("Daft Punk Radio", null, ListKinds.Radio, Seeds(Picture("#C08020", "#101010"))));
        var goldAgain = await service.GetListCoverAsync(new ListCover("Daft Punk Radio", null, ListKinds.Radio, Seeds(Picture("#C08020", "#101010"))));
        var teal = await service.GetListCoverAsync(new ListCover("Daft Punk Radio", null, ListKinds.Radio, Seeds(Picture("#1C8C8C", "#101010"))));

        Assert.Equal(gold, goldAgain);
        Assert.True(Changed(plain, gold, 0, 0, 600, 600) > 10_000, "seed colours should change the cover");
        Assert.True(Changed(gold, teal, 0, 0, 600, 600) > 10_000, "a new seed cover should redraw it");
    }

    /// <summary>Grey seeds, or seeds that cannot be fetched, leave the genre's colour.</summary>
    [Fact]
    public async Task GreyOrMissingSeeds_FallBack()
    {
        var service = Service();
        var bare = await service.GetListCoverAsync(new ListCover("Rock Mix", "Rock"));
        var failing = await service.GetListCoverAsync(new ListCover("Rock Mix", "Rock", ListKinds.Mix,
            _ => Task.FromResult<IReadOnlyList<CoverSeed>>([new CoverSeed("gone", _ => throw new HttpRequestException("down"))])));
        var grey = await service.GetListCoverAsync(new ListCover("Rock Mix", "Rock", ListKinds.Mix, Seeds(Picture("#808080"))));

        Assert.Equal(bare, failing);
        Assert.Equal(bare, grey);
    }

    [Fact]
    public void GenreAndDecade_GiveTheirHue_WhenTheSongsGiveNone()
    {
        var service = Service();
        var rock = service.FallbackMusic("Rock Mix", "Rock")!;
        var decade = service.FallbackMusic("1990s Mix", "1990s")!;

        Assert.Equal(26, rock.Hue);
        Assert.Equal(134, decade.Hue);
        Assert.Null(service.FallbackMusic("Polka Mix", "Polka"));
        Assert.Equal(CoverBook.Default.ListHue("Soul Radio"), CoverBook.Default.ListHue("R&B & Soul"));
    }

    // ------------------------------------------------------------ words

    [Theory]
    [InlineData("Daft Punk Radio", ListKinds.Radio, "Daft Punk", "Station")]
    [InlineData("Rock Radio", ListKinds.Radio, "Rock", "Station")]
    [InlineData("Late Night Jazz", ListKinds.Radio, "Late Night Jazz", "Station")]
    [InlineData("Rock Mix", ListKinds.Mix, "Rock", "Mix")]
    [InlineData("1990s Mix", ListKinds.Mix, "1990s", "Mix")]
    [InlineData("Late Night Jazz", ListKinds.Mix, "Late Night Jazz", "Mix")]
    public void Spec_NamesTheListAndSaysWhatItIs(string name, string kind, string title, string line)
    {
        var spec = CoverArtService.Spec(name, kind, 50, null);

        Assert.Equal(title, spec.Name);
        Assert.Equal(line, spec.Line);
        Assert.Equal("50 songs", spec.Footer);
        Assert.Equal(name, spec.Id);
    }

    /// <summary>A name that already ends in what it is gets no second line saying it again.</summary>
    [Theory]
    [InlineData("Your Mix", ListKinds.Radio)]
    [InlineData("Discovery Mix", ListKinds.Radio)]
    [InlineData("Your Mix", ListKinds.Mix)]
    [InlineData("Late Night Radio Station", ListKinds.Radio)]
    [InlineData("Road Trip Playlist", ListKinds.Mix)]
    [InlineData("Summer mixes", ListKinds.Mix)]
    [InlineData("Pirate Radios", ListKinds.Radio)]
    [InlineData("Other Stations", ListKinds.Radio)]
    [InlineData("Old Playlists", ListKinds.Mix)]
    public void Spec_NameThatSaysWhatItIs_HasNoSecondLine(string name, string kind)
    {
        var spec = CoverArtService.Spec(name, kind, 50, null);

        Assert.Equal(name, spec.Name);
        Assert.Null(spec.Line);
        var art = new CoverArtService(NullLogger<CoverArtService>.Instance).Compose(spec, 600);
        Assert.Equal(2, art.Words.Count);
        Assert.DoesNotContain(art.Words, w => w.Text is "Station" or "Mix");
    }

    /// <summary>Only the last word counts, and only a whole word.</summary>
    [Theory]
    [InlineData("Mixtape Classics", false)]
    [InlineData("Radiohead", false)]
    [InlineData("Mix Masters", false)]
    [InlineData("Your Mix", true)]
    [InlineData("Discovery MIX", true)]
    public void SaysWhatItIs_ReadsTheLastWholeWord(string name, bool expected) =>
        Assert.Equal(expected, CoverLayout.SaysWhatItIs(name));

    public static TheoryData<string> Names => new()
    {
        "Rock",
        "Red Hot Chili Peppers",
        "The Most Unreasonably Long Playlist Name Anyone Ever Typed Into A Music Server",
        "Supercalifragilisticexpialidociousness",
        "宇多田ヒカル",
        "블랙핑크 BLACKPINK",
        "فيروز",
        "שירים ישנים",
        "Late Night 🌙 Chill",
        "Ünïcödé Café",
    };

    /// <summary>Every word fits inside the margins, below the top, above the foot line.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Words_FitInsideTheMargins(string name)
    {
        var service = Service();
        foreach (var side in new[] { 600, 1200 })
        {
            var art = service.Compose(CoverArtService.Spec(name + " Radio", ListKinds.Radio, 120, null), side);
            var margin = MathF.Round(side * CoverBook.Default.Layout.Margin);
            Assert.Equal(3, art.Words.Count);
            foreach (var words in art.Words)
            {
                var box = words.Inked;
                Assert.True(box[0] >= margin - 0.5f && box[2] <= side - margin + 0.5f, $"{name} at {side}: {words.Text} runs {box[0]}..{box[2]}");
                Assert.True(box[1] >= 0 && box[3] <= side, $"{name} at {side}: {words.Text} rows {box[1]}..{box[3]}");
                Assert.True(words.Measured.Lines <= CoverBook.Default.Layout.Title.MaxLines);
            }
            // The foot line comes first in the list; the name, then the line under it, then the foot line, top to bottom.
            Assert.True(art.Words[2].Top >= art.Words[1].Inked[3] - 0.5f && art.Words[0].Top >= art.Words[2].Inked[3]);
        }
    }

    [Fact]
    public void LongNames_WrapOntoThreeLinesAtMost_AndTheLongestIsCut()
    {
        var service = Service();
        var wraps = service.Compose(CoverArtService.Spec("Red Hot Chili Peppers And Friends Radio", ListKinds.Radio, null, null), 600).Words[0];
        var cut = service.Compose(CoverArtService.Spec(string.Join(" ", Enumerable.Repeat("Unreasonably", 12)), ListKinds.Mix, null, null), 600).Words[0];

        Assert.InRange(wraps.Measured.Lines, 2, 3);
        Assert.False(wraps.Measured.Cut);
        Assert.Equal(3, cut.Measured.Lines);
        Assert.True(cut.Measured.Cut);
    }

    [Fact]
    public void RightToLeftNames_AreSetFromTheRight()
    {
        var words = Service().Compose(CoverArtService.Spec("فيروز Radio", ListKinds.Radio, null, null), 600).Words;

        Assert.All(words, w => Assert.Equal(CoverAlign.Right, w.Align));
        Assert.True(words[0].Inked[2] > 500);
    }

    /// <summary>
    /// Chinese, Japanese, Korean, Arabic, Hebrew and emoji names draw real letters, from a font
    /// that has them: the name's box holds plenty of white, and each letter differs from the
    /// empty box a missing glyph would leave.
    /// </summary>
    [Theory]
    [InlineData("宇多田ヒカル")]
    [InlineData("블랙핑크")]
    [InlineData("فيروز")]
    [InlineData("שירים")]
    [InlineData("🌙🎧")]
    public void UnicodeNames_DrawTheirLetters(string name)
    {
        var fonts = CoverFonts.Fallbacks.Count;
        if (fonts == 0) return;
        var (font, behind) = CoverFonts.For(name, 600, 96);
        var hasLetters = name.EnumerateRunes().Any(System.Text.Rune.IsLetter);
        if (hasLetters) Assert.False(font.Family.Equals(CoverFonts.ForWeight(600)), $"{name} is set in Inter");
        foreach (var rune in name.EnumerateRunes())
        {
            var cp = new SixLabors.Fonts.Unicode.CodePoint(rune.Value);
            var found = CoverFonts.Has(font, cp) || behind.Any(f => CoverFonts.Has(f.CreateFont(96), cp));
            Assert.True(found, $"no installed font draws U+{rune.Value:X4}");
        }

        var service = Service();
        var spec = CoverArtService.Spec(name, ListKinds.Mix, null, null);
        using var image = service.Paint(spec, 600);
        var box = service.Compose(spec, 600).Words[0].Inked;
        var white = 0;
        for (var y = (int)box[1]; y < (int)box[3]; y++)
        for (var x = (int)box[0]; x < (int)box[2]; x++)
            if (image[x, y] is { R: > 235, G: > 235, B: > 235 }) white++;
        Assert.True(white > 1500, $"{name}: only {white} white pixels in the name");
    }

    /// <summary>
    /// A name Inter draws whole attaches nothing behind it: an attached family is parsed in full
    /// the first time its line is measured, and a Latin name used to carry every installed family
    /// (~120 MB for the boot warmup alone). A symbol takes the one family that holds it and no
    /// others.
    /// </summary>
    [Fact]
    public void NamesInterDraws_AttachNoOtherFont()
    {
        var (latin, behind) = CoverFonts.For("Warm Radio", 600, 96);
        Assert.True(latin.Family.Equals(CoverFonts.ForWeight(600)), "a Latin name is not set in Inter");
        Assert.Empty(behind);

        if (CoverFonts.Fallbacks.Count == 0) return;
        var (_, forEmoji) = CoverFonts.For("Your Mix \U0001F534", 600, 96);
        Assert.True(forEmoji.Count <= 1, $"{forEmoji.Count} families attached for one emoji");
        Assert.All(forEmoji, family => Assert.True(
            CoverFonts.Has(family.CreateFont(96), new SixLabors.Fonts.Unicode.CodePoint(0x1F534)),
            $"{family.Name} is attached but does not hold the emoji"));
    }

    // ------------------------------------------------------------ contrast

    public static TheoryData<int> Backgrounds() => new(Enumerable.Range(0, CoverBook.Default.Backgrounds.Count));

    /// <summary>
    /// On every one of the 48 backgrounds, every pixel behind the words, as painted, reaches the
    /// design's contrast against the words' own white: 3:1 at least for the large name and its
    /// line (the veil aims for 4.5 and may stop at 3 to keep the colour), and 4.25:1 for the foot
    /// line. The design sets the foot line's limit for 85% white over grey; over a vivid colour
    /// that white blends a little darker, so on the most saturated backgrounds the foot line
    /// lands between 4.29 and 4.5 rather than at 4.5.
    /// </summary>
    [Theory]
    [MemberData(nameof(Backgrounds))]
    public void Words_ReachTheirContrast_OnEveryPixelBehindThem(int background)
    {
        var service = Service();
        var spec = CoverArtService.Spec("Everything I Have Ever Loved Radio", ListKinds.Radio, 1234, null);
        foreach (var side in new[] { 600, 1200 })
        {
            var art = service.Compose(spec, side) with { Background = background, Orientation = background % 8 };
            using var backdrop = CoverPainter.Paint(CoverBook.Default, art, new CoverTypesetter(), drawWords: false);
            foreach (var words in art.Words)
            {
                var need = words.Role == WordsRole.Footer ? 4.25 : 3.0;
                var box = words.Inked;
                var worst = double.MaxValue;
                for (var y = Math.Max(0, (int)box[1]); y < Math.Min(side, (int)MathF.Ceiling(box[3])); y++)
                for (var x = Math.Max(0, (int)box[0]); x < Math.Min(side, (int)MathF.Ceiling(box[2])); x++)
                {
                    var p = backdrop[x, y];
                    var under = unchecked((int)0xFF000000) | (p.R << 16) | (p.G << 8) | p.B;
                    worst = Math.Min(worst, CoverColours.ContrastRatio(CoverColours.Over(words.Ink, under), under));
                }
                Assert.True(worst >= need, $"{CoverBook.Default.Backgrounds[background].Name} at {side}: '{words.Text}' reaches only {worst:F2}");
            }
        }
    }

    /// <summary>After JPEG, which moves a level or two, the words still read behind them.</summary>
    [Fact]
    public void Words_StillReadAfterJpeg()
    {
        var service = Service();
        foreach (var background in new[] { "lemonade.webp", "chiffon.webp", "peach.webp", "opal.webp" })
        {
            var index = CoverBook.Default.Backgrounds.Select((b, i) => (b, i)).Single(pair => pair.b.File == background).i;
            var spec = CoverArtService.Spec("Sunday Morning Radio", ListKinds.Radio, 99, null);
            var art = service.Compose(spec, 600) with { Background = index, Orientation = 0 };
            using var painted = CoverPainter.Paint(CoverBook.Default, art, new CoverTypesetter(), drawWords: false);
            using var ms = new MemoryStream();
            painted.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 92, ColorType = SixLabors.ImageSharp.Formats.Jpeg.JpegEncodingColor.YCbCrRatio444 });
            using var decoded = Image.Load<Rgb24>(ms.ToArray());
            foreach (var words in art.Words)
            {
                var need = words.Role == WordsRole.Footer ? 4.15 : 2.9;
                var box = words.Inked;
                for (var y = (int)box[1]; y < (int)box[3]; y++)
                for (var x = (int)box[0]; x < (int)box[2]; x++)
                {
                    var p = decoded[x, y];
                    var under = unchecked((int)0xFF000000) | (p.R << 16) | (p.G << 8) | p.B;
                    Assert.True(CoverColours.ContrastRatio(CoverColours.Over(words.Ink, under), under) >= need, $"{background} at {x},{y}");
                }
            }
        }
    }

    // ------------------------------------------------------------ determinism and speed

    [Fact]
    public async Task SameList_SameBytes_AcrossInstancesAndSizes()
    {
        var seeds = Seeds(Picture("#1D3F8C", "#D9552B"));
        var a = await Service().GetListCoverAsync(new ListCover("Tame Impala Radio", null, ListKinds.Radio, seeds, 50), 800);
        var b = await Service().GetListCoverAsync(new ListCover("Tame Impala Radio", null, ListKinds.Radio, seeds, 50), 800);

        Assert.Equal(a, b);
        using var image = Image.Load<Rgb24>(a);
        Assert.Equal(800, image.Width);
        Assert.Equal(600, Image.Identify(Service().GetNamedCover("x", null, 64)).Width);
        Assert.Equal(1200, Image.Identify(Service().GetNamedCover("x", null, 5000)).Width);
    }

    // ------------------------------------------------------------ contact sheet

    /// <summary>
    /// A sheet of covers for looking at the design, only when asked:
    /// OCTO_SHOTS_DIR=&lt;folder&gt; (and OCTO_SHOTS_SEEDS=&lt;folder of album covers&gt;) dotnet test --filter ContactSheet
    /// </summary>
    [Fact]
    public async Task ContactSheet()
    {
        var output = Environment.GetEnvironmentVariable("OCTO_SHOTS_DIR");
        if (string.IsNullOrEmpty(output)) return;
        var seedDir = Environment.GetEnvironmentVariable("OCTO_SHOTS_SEEDS") ?? "";
        byte[]? Seed(string stem)
        {
            var path = Path.Combine(seedDir, stem + ".jpg");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        var lists = new (string Name, string? Label, string Kind, string[] Seeds, int? Songs)[]
        {
            ("Daft Punk Radio", null, ListKinds.Radio, ["Daft_Punk_Random_Access_Memories"], 50),
            ("Billie Eilish Radio", null, ListKinds.Radio, ["Billie_Eilish_Happier_Than_Ever"], 50),
            ("Tame Impala Radio", null, ListKinds.Radio, ["Tame_Impala_Currents"], 50),
            ("Radiohead Radio", null, ListKinds.Radio, ["Radiohead_In_Rainbows"], 50),
            ("Kendrick Lamar Radio", null, ListKinds.Radio, ["Kendrick_Lamar_DAMN"], 50),
            ("Your Mix", null, ListKinds.Radio, ["Taylor_Swift_1989", "Frank_Ocean_Blonde"], 50),
            ("Discovery Mix", null, ListKinds.Radio, ["Arctic_Monkeys_AM", "Bad_Bunny_Un_Verano_Sin_Ti"], 50),
            ("Bad Bunny Radio", null, ListKinds.Radio, ["Bad_Bunny_Un_Verano_Sin_Ti"], 50),
            ("Jazz & Blues Mix", "Jazz & Blues", ListKinds.Mix, ["Miles_Davis_Kind_of_Blue"], 100),
            ("Metal Mix", "Metal", ListKinds.Mix, ["Metallica_Master_of_Puppets"], 100),
            ("1970s Mix", "1970s", ListKinds.Mix, ["Fleetwood_Mac_Rumours"], 100),
            ("Rock Mix", "Rock", ListKinds.Mix, [], null),
            ("Hip-Hop Mix", "Hip-Hop", ListKinds.Mix, [], 100),
            ("1990s Mix", "1990s", ListKinds.Mix, [], 100),
            ("2020s Mix", "2020s", ListKinds.Mix, [], 100),
            ("Electronic Radio", "electronic", ListKinds.Radio, [], 50),
            ("Polka Mix", "Polka", ListKinds.Mix, [], 37),
            ("Red Hot Chili Peppers Radio", null, ListKinds.Radio, [], 50),
            ("The Most Unreasonably Long Playlist Name Anyone Ever Typed Into A Music Server Radio", null, ListKinds.Radio, [], 50),
            ("宇多田ヒカル Radio", null, ListKinds.Radio, ["Hikaru_Utada_Fantome"], 50),
            ("블랙핑크 BLACKPINK Radio", null, ListKinds.Radio, ["BLACKPINK_The_Album"], 50),
            ("فيروز Radio", null, ListKinds.Radio, ["Fairuz"], 50),
            ("Late Night 🌙 Chill Mix", "Lo-fi & Chill", ListKinds.Mix, [], 100),
            ("Ünïcödé Café Mix", null, ListKinds.Mix, [], 1),
        };
        var service = Service();
        const int side = 600, gap = 40, columns = 6;
        var rows = (lists.Length + columns - 1) / columns;
        using var sheet = new Image<Rgba32>(columns * (side + gap) + gap, rows * (side + gap) + gap, new Rgba32(12, 12, 13));
        for (var i = 0; i < lists.Length; i++)
        {
            var list = lists[i];
            var pictures = list.Seeds.Select(Seed).Where(b => b is not null).Cast<byte[]>().ToArray();
            var watch = Stopwatch.StartNew();
            var bytes = await service.GetListCoverAsync(new ListCover(list.Name, list.Label, list.Kind,
                pictures.Length > 0 ? Seeds(pictures) : null, list.Songs), side);
            _output.WriteLine($"{list.Name}: {watch.Elapsed.TotalMilliseconds:F0} ms");
            using var cover = Image.Load<Rgba32>(bytes);
            var (x, y) = (gap + i % columns * (side + gap), gap + i / columns * (side + gap));
            sheet.Mutate(ctx => ctx.DrawImage(cover, new Point(x, y), 1f));
        }
        Directory.CreateDirectory(output);
        await sheet.SaveAsPngAsync(Path.Combine(output, "list-covers.png"));
    }
}

/// <summary>Timing runs alone, after the other tests, so it measures the cover and not the suite.</summary>
[CollectionDefinition(nameof(CoverTiming), DisableParallelization = true)]
public class CoverTiming;

[Collection(nameof(CoverTiming))]
public class CoverTimingTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// Drawing a cover: background, veil, words and JPEG. The fastest of several runs is the
    /// cover's own cost; the middle one also carries whatever else the machine is doing.
    /// </summary>
    [Fact]
    public void ACover_DrawsQuickly()
    {
        var service = new CoverArtService(NullLogger<CoverArtService>.Instance);
        var spec = CoverArtService.Spec("Red Hot Chili Peppers Radio", ListKinds.Radio, 100, null);
        service.Render(spec, 600);
        service.Render(spec, 1200);

        (double Fastest, double Middle) Time(int side)
        {
            var times = new List<double>();
            for (var i = 0; i < 9; i++)
            {
                var watch = Stopwatch.StartNew();
                service.Render(spec with { Id = $"t{i}" }, side);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            return (times[0], times[times.Count / 2]);
        }

        var small = Time(600);
        var large = Time(1200);
        _output.WriteLine($"600 px: fastest {small.Fastest:F1} ms, middle {small.Middle:F1} ms; 1200 px: fastest {large.Fastest:F1} ms, middle {large.Middle:F1} ms");
        Assert.True(small.Fastest < 150, $"600 px took {small.Fastest:F1} ms");
        Assert.True(large.Fastest < 450, $"1200 px took {large.Fastest:F1} ms");
    }

}
