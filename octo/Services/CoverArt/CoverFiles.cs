namespace Octo.Services.CoverArt;

/// <summary>
/// The cover.jpg beside an album, which Navidrome ranks above the art inside the files. Octo
/// marks every cover.jpg it writes (a JPEG comment), so it can later replace its own with a
/// larger one and never touch one the owner put there.
/// </summary>
public static class CoverFiles
{
    public const string FileName = "cover.jpg";

    /// <summary>
    /// Whether <paramref name="cover"/> may go to <paramref name="dir"/>/cover.jpg. Into a folder
    /// with no cover file only when the caller made the folder (see the download path's sidecar
    /// rule). Over an existing one only when it is Octo's own cover.jpg and smaller, and never
    /// when any other cover.* or folder.* sits there.
    /// </summary>
    public static bool ShouldWrite(string dir, byte[] cover, bool folderIsNew)
    {
        try
        {
            if (Directory.EnumerateFiles(dir, "folder.*").Any()) return false;
            var existing = Directory.EnumerateFiles(dir, "cover.*").ToList();
            if (existing.Count == 0) return folderIsNew;
            if (existing.Count != 1 || !Path.GetFileName(existing[0]).Equals(FileName, StringComparison.OrdinalIgnoreCase))
                return false;
            var current = File.ReadAllBytes(existing[0]);
            return CoverImage.IsOctoCover(current) && IsLarger(cover, current);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The longer side of <paramref name="candidate"/> beats <paramref name="current"/>'s.</summary>
    public static bool IsLarger(byte[] candidate, byte[]? current) =>
        CoverImage.Measure(candidate) is { } next
        && (current is null || CoverImage.Measure(current) is not { } now
            || Math.Min(next.Width, next.Height) > Math.Min(now.Width, now.Height));

    /// <summary>Writes the cover as a marked JPEG, through a temporary file, so a reader never
    /// sees half of it.</summary>
    public static void Write(string dir, byte[] cover)
    {
        var path = Path.Combine(dir, FileName);
        var temp = path + ".octo-tmp";
        File.WriteAllBytes(temp, CoverImage.MarkAsOcto(CoverImage.ToJpeg(cover)));
        // An owner's "Cover.JPG" on a case-sensitive disk is another file; ShouldWrite has
        // already refused that folder, so this only ever replaces Octo's own.
        File.Move(temp, path, overwrite: true);
    }
}
