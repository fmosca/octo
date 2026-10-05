using System.Globalization;
using System.Text.RegularExpressions;
using Octo.Services.Common;

namespace Octo.Services.Library;

/// <summary>The tag values Navidrome builds a song's track and album ids from, as it reads them.</summary>
public sealed record KeptIdentity(
    string Title, string? Album, IReadOnlyList<string> AlbumArtist, IReadOnlyList<string> AlbumArtists,
    string? AlbumVersion, string? ReleaseDate, string? AlbumId, string? ReleaseTrackId,
    uint Track, uint TrackCount, uint Disc, uint DiscCount, bool Compilation);

/// <summary>
/// Reads a file's identity the way Navidrome 0.64 does and writes it onto a replacement (W8).
/// Navidrome keeps a moved song's id, and every play, favorite and playlist place on it, only
/// when the new file has the same persistent id as the one that went missing. Key lists are
/// resources/mappings.yaml's aliases, lowercased, in its order.
/// </summary>
internal static class KeptIdentityTags
{
    internal static readonly string[] TitleKeys = ["tit2", "title", "©nam", "inam"];
    internal static readonly string[] AlbumKeys = ["talb", "album", "©alb", "wm/albumtitle", "iprd"];
    internal static readonly string[] AlbumArtistKeys = ["tpe2", "albumartist", "album artist", "album_artist", "aart", "wm/albumartist"];
    internal static readonly string[] AlbumArtistsKeys = ["txxx:album artists", "albumartists"];
    internal static readonly string[] AlbumVersionKeys = ["albumversion", "musicbrainz_albumcomment", "musicbrainz album comment"];
    internal static readonly string[] ReleaseDateKeys = ["tdrl", "releasedate", "©day", "wm/year", "year"];
    internal static readonly string[] AlbumIdKeys = ["txxx:musicbrainz album id", "musicbrainz_albumid", "musicbrainz album id", "----:com.apple.itunes:musicbrainz album id", "musicbrainz/album id"];
    internal static readonly string[] ReleaseTrackIdKeys = ["txxx:musicbrainz release track id", "musicbrainz_releasetrackid", "----:com.apple.itunes:musicbrainz release track id", "musicbrainz/release track id"];
    internal static readonly string[] ArtistKeys = ["tpe1", "artist", "©art", "author", "iart"];
    internal static readonly string[] ArtistsKeys = ["txxx:artists", "artists", "----:com.apple.itunes:artists", "wm/artists"];

    /// <summary>Every custom field this owns, removed from a replacement before the copy.</summary>
    private static readonly HashSet<string> Owned = new(
        [.. AlbumArtistKeys, .. AlbumArtistsKeys, .. AlbumVersionKeys, .. ReleaseDateKeys.Where(k => k != "©day"),
         .. AlbumIdKeys, .. ReleaseTrackIdKeys], StringComparer.Ordinal);

    // The two names TagLib itself renames on read (its TXXX and MP4 freeform tables).
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MusicBrainz Album Id"] = "musicbrainz_albumid",
        ["MusicBrainz Release Track Id"] = "musicbrainz_releasetrackid",
    };
    private static string Key(string name) => Renamed.TryGetValue(name, out var renamed) ? renamed : name.ToLowerInvariant();

    internal const string VariousArtists = "Various Artists", UnknownArtist = "[Unknown Artist]";
    private static readonly Regex ArtistSplit = new(@"(?: / | feat\. | feat | ft\. | ft |; )", RegexOptions.IgnoreCase);
    private static readonly Regex Year = new("([12][0-9][0-9][0-9])");
    private const string AppleMean = "com.apple.iTunes";
    // TagLib# keeps its atom names internal: the same four bytes, with 0xA9 for the copyright sign.
    private static readonly TagLib.ByteVector Nam = Atom("©nam"), Alb = Atom("©alb"), Aart = Atom("aART"),
        Art = Atom("©ART"), Day = Atom("©day");
    private static TagLib.ByteVector Atom(string name) => TagLib.ByteVector.FromString(name, TagLib.StringType.Latin1);

    /// <summary>The original's identity, or null when the file cannot be read. Read while it is
    /// still in place. <paramref name="navidromeAlbumArtist"/> is the album artist Navidrome
    /// stored for it, used only when the file names none.</summary>
    public static KeptIdentity? Read(string path, string? navidromeAlbumArtist = null)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var view = NavidromeView(file);
            var albumArtist = Values(view, AlbumArtistKeys);
            var albumArtists = Values(view, AlbumArtistsKeys);
            // No album artist tag: Navidrome made one up, and that name is what its album id
            // hashes. Written as the tag it hashes the same, and the track artist stays new.
            if (albumArtist.Count == 0 && albumArtists.Count == 0)
                albumArtist = [FallbackAlbumArtist(view, navidromeAlbumArtist, TagWriterExtras.IsCompilation(file))];
            var tag = file.Tag;
            return new KeptIdentity(
                // No title tag: Navidrome titles it by its file name, which the replacement keeps.
                Title: Values(view, TitleKeys).FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path),
                Album: Values(view, AlbumKeys).FirstOrDefault(),
                AlbumArtist: albumArtist, AlbumArtists: albumArtists,
                AlbumVersion: Values(view, AlbumVersionKeys).FirstOrDefault(),
                ReleaseDate: Values(view, ReleaseDateKeys).Select(NavidromeDate).FirstOrDefault(d => d is not null),
                AlbumId: Values(view, AlbumIdKeys).Select(NavidromeUuid).FirstOrDefault(u => u is not null),
                ReleaseTrackId: Values(view, ReleaseTrackIdKeys).Select(NavidromeUuid).FirstOrDefault(u => u is not null),
                Track: tag.Track, TrackCount: tag.TrackCount, Disc: tag.Disc, DiscCount: tag.DiscCount,
                Compilation: TagWriterExtras.IsCompilation(file));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Overwrite the replacement's identity with the original's and remove what the
    /// original lacked. Cover, ReplayGain, lyrics, genre, ISRC, label and recording id stay.</summary>
    public static void Apply(string path, KeptIdentity identity)
    {
        using var file = TagLib.File.Create(path);
        Remove(file);
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            id3.RemoveFrames("TDRL");
            // Version 3 holds one album artist per frame; several need version 4.
            if (identity.AlbumArtist.Count > 1 && id3.Version < 4) id3.Version = 4;
        }
        // The date atom is a release date alias on MP4, so it goes when the original had none.
        if (identity.ReleaseDate is null && file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
            apple.ClearData(Day);

        var tag = file.Tag;
        tag.Title = identity.Title;
        tag.Album = identity.Album;
        tag.AlbumArtists = [.. identity.AlbumArtist];
        tag.Track = identity.Track; tag.TrackCount = identity.TrackCount;
        tag.Disc = identity.Disc; tag.DiscCount = identity.DiscCount;
        TagWriterExtras.SetCompilation(file, identity.Compilation);
        TagWriterExtras.SetExact(file, TagFields.AlbumArtists, identity.AlbumArtists);
        TagWriterExtras.SetExact(file, TagFields.AlbumVersion, identity.AlbumVersion is { } version ? [version] : []);
        TagWriterExtras.SetText(file, TagFields.ReleaseDate, identity.ReleaseDate);
        TagWriterExtras.SetText(file, TagFields.AlbumId, identity.AlbumId);
        TagWriterExtras.SetReleaseTrackId(file, identity.ReleaseTrackId);
        file.Save();
    }

    /// <summary>Navidrome's two persistent ids' inputs as one string: equal strings, equal ids.</summary>
    internal static string PidInputs(KeptIdentity i) => string.Join("|", i.ReleaseTrackId, i.AlbumId,
        string.Join("\u0001", i.AlbumArtist), string.Join("\u0001", i.AlbumArtists), i.Album, i.AlbumVersion, i.ReleaseDate, i.Title);

    /// <summary>The keys and values Navidrome's tag reader sees, for the fields that matter here.
    /// Only the container Navidrome reads: ID3 on MP3, Vorbis on FLAC and Ogg, iTunes atoms on MP4.</summary>
    internal static Dictionary<string, List<string>> NavidromeView(TagLib.File file)
    {
        var view = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string key, IEnumerable<string>? values)
        {
            if (values is null) return;
            if (!view.TryGetValue(key, out var list)) view[key] = list = [];
            list.AddRange(values.Where(value => !string.IsNullOrEmpty(value)));
        }
        switch (file)
        {
            case TagLib.Flac.File or TagLib.Ogg.File when file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph:
                foreach (var name in xiph) Add(name.ToLowerInvariant(), xiph.GetField(name));
                break;
            case TagLib.Mpeg4.File when file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple:
                Add("title", apple.GetText(Nam));
                Add("album", apple.GetText(Alb));
                Add("albumartist", apple.GetText(Aart));
                Add("artist", apple.GetText(Art));
                Add("©day", apple.GetText(Day));
                foreach (var (name, values) in Freeform(apple)) Add(Key(name), values);
                break;
            default:
                if (file.GetTag(TagLib.TagTypes.Id3v2, false) is not TagLib.Id3v2.Tag id3) break;
                string[] Frame(string id) => id3.GetFrames<TagLib.Id3v2.TextInformationFrame>(id).SelectMany(f => f.Text).ToArray();
                // TagLib# splits a version 3 TPE1/TPE2 at "/"; the reader Navidrome uses does not.
                string[] Joined(string id) => Frame(id) is { Length: > 1 } parts && id3.Version < 4 ? [string.Join("/", parts)] : Frame(id);
                Add("title", Frame("TIT2"));
                Add("album", Frame("TALB"));
                Add("albumartist", Joined("TPE2"));
                Add("artist", Joined("TPE1"));
                Add("releasedate", Frame("TDRL"));
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                    if (frame.Description is { Length: > 0 } description) Add(Key(description), frame.Text);
                break;
        }
        return view;
    }

    private static List<string> Values(Dictionary<string, List<string>> view, string[] keys) =>
        keys.SelectMany(key => view.GetValueOrDefault(key) ?? []).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The album artist Navidrome named an album that names none (map_participants.go).</summary>
    internal static string FallbackAlbumArtist(Dictionary<string, List<string>> view, string? navidrome, bool compilation)
    {
        if (!string.IsNullOrWhiteSpace(navidrome)) return navidrome;
        if (compilation) return VariousArtists;
        if (Values(view, ArtistsKeys).FirstOrDefault() is { } first) return first;
        var artist = Values(view, ArtistKeys);
        return artist.Count == 1 ? ArtistSplit.Split(artist[0]).FirstOrDefault(part => part.Length > 0) ?? artist[0]
            : artist.FirstOrDefault() ?? UnknownArtist;
    }

    /// <summary>Navidrome's parseDate (metadata.go:174-203): null when there is no year.</summary>
    internal static string? NavidromeDate(string value)
    {
        if (value.Length < 4 || Year.Match(value) is not { Success: true } match) return null;
        if (value.Length < 5) return match.Groups[1].Value;
        var head = value[..Math.Min(10, value.Length)];
        return DateTime.TryParseExact(head, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || DateTime.TryParseExact(head, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? head : match.Groups[1].Value;
    }

    /// <summary>An id Navidrome keeps, in its canonical form; null for one it would drop.</summary>
    internal static string? NavidromeUuid(string value)
    {
        var text = value.Length == 45 && value.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase) ? value[9..]
            : value.Length == 38 ? value[1..^1] : value;
        return Guid.TryParseExact(text, text.Length == 32 ? "N" : "D", out var id) ? id.ToString("D") : null;
    }

    /// <summary>The iTunes freeform atoms by their own names. Confirm TagLib# member names; adapt names only.</summary>
    private static IEnumerable<(string Name, string[] Values)> Freeform(TagLib.Mpeg4.AppleTag apple)
    {
        foreach (var box in apple.Where(box => box.BoxType == "----").ToList())
        {
            string? Part(string type) => box.Children.OfType<TagLib.Mpeg4.AppleAdditionalInfoBox>()
                .FirstOrDefault(child => child.BoxType == type)?.Text;
            if (Part("mean") == AppleMean && Part("name") is { Length: > 0 } name)
                yield return (name, apple.GetDashBoxes(AppleMean, name) ?? []);
        }
    }

    private static void Remove(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
            foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>()
                         .Where(f => Owned.Contains(Key(f.Description ?? ""))).ToList())
                id3.RemoveFrame(frame);
        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
            foreach (var name in xiph.Where(n => Owned.Contains(n.ToLowerInvariant())).ToList()) xiph.RemoveField(name);
        if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
            foreach (var (name, _) in Freeform(apple).Where(f => Owned.Contains(Key(f.Name))).ToList())
                apple.SetDashBox(AppleMean, name, null);
    }
}
