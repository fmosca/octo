using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Octo.Services.CoverArt;

/// <summary>What makes a cover usable (#51): decodable, big enough, and square.</summary>
internal static class CoverImage
{
    /// <summary>JPEG rounding and one-pixel crops leave real covers a few pixels off square.</summary>
    private const double SquareTolerance = 0.03;
    private const int MinSide = 150;

    public static (int Width, int Height)? Measure(byte[] bytes)
    {
        try
        {
            var info = Image.Identify(bytes);
            return (info.Width, info.Height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodable and big enough, and square when <paramref name="requireSquare"/>. A cover that is
    /// not square is a video thumbnail: a 16:9 frame with the artist off-centre, which in a grid
    /// of square covers is the one that looks broken.
    /// </summary>
    public static bool IsUsable(byte[]? bytes, bool requireSquare)
    {
        if (bytes is not { Length: > 0 } || Measure(bytes) is not { } size) return false;
        if (Math.Min(size.Width, size.Height) < MinSide) return false;
        return !requireSquare
            || Math.Abs(size.Width - size.Height) <= Math.Max(size.Width, size.Height) * SquareTolerance;
    }

    /// <summary>
    /// The centre square. A YouTube "Topic" upload letterboxes the real cover inside a 16:9 frame,
    /// so for those this IS the cover; for any other video it is the middle of the picture, which
    /// still beats a stretched thumbnail.
    /// </summary>
    public static byte[]? CropToSquare(byte[] bytes)
    {
        try
        {
            using var image = Image.Load(bytes);
            var side = Math.Min(image.Width, image.Height);
            var x = (image.Width - side) / 2;
            var y = (image.Height - side) / 2;
            image.Mutate(ctx => ctx.Crop(new Rectangle(x, y, side, side)));
            using var output = new MemoryStream();
            image.Save(output, new JpegEncoder { Quality = 90 });
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>JPEG as-is, anything else re-encoded, for a cover.jpg that is what its name says.</summary>
    public static byte[] ToJpeg(byte[] bytes)
    {
        try
        {
            if (Image.DetectFormat(bytes).Name.Equals("JPEG", StringComparison.OrdinalIgnoreCase)) return bytes;
            using var image = Image.Load(bytes);
            using var output = new MemoryStream();
            image.Save(output, new JpegEncoder { Quality = 90 });
            return output.ToArray();
        }
        catch
        {
            return bytes;
        }
    }

    /// <summary>
    /// No larger than <paramref name="maxSide"/> on its longer side, as a JPEG. The cover kept
    /// in every file of an album is its copy of the art, so a 3000 px master embedded in fifteen
    /// tracks would add tens of megabytes; the full master goes to cover.jpg instead. Returned
    /// as it is when it already fits or cannot be read.
    /// </summary>
    public static byte[] FitWithin(byte[] bytes, int maxSide)
    {
        try
        {
            if (Measure(bytes) is not { } size || Math.Max(size.Width, size.Height) <= maxSide) return bytes;
            using var image = Image.Load(bytes);
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(maxSide, maxSide),
                Mode = ResizeMode.Max,
                Sampler = KnownResamplers.Lanczos3,
            }));
            using var output = new MemoryStream();
            image.Save(output, new JpegEncoder { Quality = 92 });
            return output.ToArray();
        }
        catch
        {
            return bytes;
        }
    }

    private static readonly byte[] OctoMark = "Written by Octo"u8.ToArray();

    /// <summary>
    /// The JPEG with a comment saying Octo wrote it, so a later, sharper cover may replace a
    /// cover.jpg that is Octo's own and never one the owner put there. The comment goes after
    /// the APPn segments, where JFIF readers expect them to stay first. Anything that is not a
    /// JPEG comes back unchanged.
    /// </summary>
    public static byte[] MarkAsOcto(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || IsOctoCover(jpeg)) return jpeg;
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF && jpeg[at + 1] is >= 0xE0 and <= 0xEF)
            at += 2 + ((jpeg[at + 2] << 8) | jpeg[at + 3]);
        if (at > jpeg.Length) return jpeg;
        var length = OctoMark.Length + 2;
        var marked = new byte[jpeg.Length + 2 + length];
        Buffer.BlockCopy(jpeg, 0, marked, 0, at);
        marked[at] = 0xFF;
        marked[at + 1] = 0xFE;
        marked[at + 2] = (byte)(length >> 8);
        marked[at + 3] = (byte)length;
        Buffer.BlockCopy(OctoMark, 0, marked, at + 4, OctoMark.Length);
        Buffer.BlockCopy(jpeg, at, marked, at + 2 + length, jpeg.Length - at);
        return marked;
    }

    /// <summary>True when the JPEG carries the comment <see cref="MarkAsOcto"/> writes.</summary>
    public static bool IsOctoCover(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;
        var at = 2;
        // Only the header segments; the picture itself starts at SOS.
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF && jpeg[at + 1] != 0xDA && jpeg[at + 1] != 0xD9)
        {
            var length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (length < 2) return false;
            if (jpeg[at + 1] == 0xFE && length - 2 == OctoMark.Length && at + 4 + OctoMark.Length <= jpeg.Length
                && jpeg.AsSpan(at + 4, OctoMark.Length).SequenceEqual(OctoMark))
                return true;
            at += 2 + length;
        }
        return false;
    }

    public static string MimeType(byte[] bytes)
    {
        try
        {
            return Image.DetectFormat(bytes).DefaultMimeType;
        }
        catch
        {
            return "image/jpeg";
        }
    }
}
