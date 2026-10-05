using System.Text;

namespace Octo.Services.Lyrics;

/// <summary>Where a song's lyrics are: nowhere, in a file beside it, or in its own tags.</summary>
public enum SongLyricsPlace { None, Beside, Inside }

/// <summary>
/// The lyrics a song file has now, as Navidrome serves them: a .lrc beside it first, then a .txt,
/// then its tags (Navidrome's default LyricsPriority). Octos is whether Octo wrote them, so may
/// replace them. Unknown is a lyrics file in a format Octo does not read, which it leaves alone.
/// </summary>
public sealed record SongLyrics(SongLyricsPlace Where, LyricsTiming Timing, bool Octos, bool Unknown)
{
    public static readonly SongLyrics Nothing = new(SongLyricsPlace.None, LyricsTiming.None, false, false);

    /// <summary>Lyrics files Navidrome or another player may read beside a song, besides .lrc and .txt.</summary>
    private static readonly string[] OtherExtensions = [".ttml", ".elrc", ".srt", ".yaml", ".yml"];

    public static SongLyrics Of(string audioPath) => Of(audioPath, null, readTags: true);

    /// <summary>The same, with the lyrics in the song's tags already read (a scan reads every tag
    /// once).</summary>
    public static SongLyrics Of(string audioPath, string? tagLyrics) => Of(audioPath, tagLyrics, readTags: false);

    private static SongLyrics Of(string audioPath, string? tagLyrics, bool readTags)
    {
        var stem = Stem(audioPath);
        if (File.Exists(stem + ".lrc"))
            return new(SongLyricsPlace.Beside, TimingOf(ReadQuietly(stem + ".lrc")), LyricsSidecarWriter.IsOctos(stem + ".lrc"), false);
        if (File.Exists(stem + ".txt"))
            return new(SongLyricsPlace.Beside, TimingOf(ReadQuietly(stem + ".txt")), false, false);
        if (OtherExtensions.Any(extension => File.Exists(stem + extension)))
            return new(SongLyricsPlace.Beside, LyricsTiming.None, false, true);
        var inside = readTags ? TagLyrics(audioPath) : tagLyrics;
        if (string.IsNullOrWhiteSpace(inside)) return Nothing;
        return new(SongLyricsPlace.Inside, TimingOf(inside), IsOctosText(inside), false);
    }

    /// <summary>Whether Octo may write lyrics in the song's tags: they hold none, or Octo's.</summary>
    public static bool MayWriteInside(string audioPath)
    {
        try
        {
            using var file = TagLib.File.Create(audioPath);
            var lyrics = file.Tag.Lyrics;
            return string.IsNullOrWhiteSpace(lyrics) || IsOctosText(lyrics);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether Octo may write a lyrics file beside the song: a .lrc for timed lyrics where there
    /// is none or Octo's, a .txt for plain ones only where there is no lyrics file at all.
    /// </summary>
    public static bool MayWriteBeside(string stem, bool timed)
    {
        if (OtherExtensions.Any(extension => File.Exists(stem + extension))) return false;
        if (timed) return !File.Exists(stem + ".lrc") || LyricsSidecarWriter.IsOctos(stem + ".lrc");
        return !File.Exists(stem + ".lrc") && !File.Exists(stem + ".txt");
    }

    /// <summary>How lyrics text is timed: word tags, line tags, or neither.</summary>
    public static LyricsTiming TimingOf(string? text) =>
        string.IsNullOrWhiteSpace(text) ? LyricsTiming.None
        : LyricsText.HasWordTags(text) ? LyricsTiming.Word
        : LyricsText.HasTimestamps(text) ? LyricsTiming.Line
        : LyricsTiming.Plain;

    private static bool IsOctosText(string text) =>
        string.Equals(text.TrimStart('﻿').Split('\n', 2)[0].Trim(), LyricsSidecarWriter.OctoMark, StringComparison.Ordinal);

    private static string? TagLyrics(string audioPath)
    {
        try
        {
            using var file = TagLib.File.Create(audioPath);
            return file.Tag.Lyrics;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadQuietly(string path)
    {
        try { return File.ReadAllText(path, Encoding.UTF8); }
        catch { return null; }
    }

    private static string Stem(string audioPath) =>
        Path.Combine(Path.GetDirectoryName(audioPath)!, Path.GetFileNameWithoutExtension(audioPath));
}
