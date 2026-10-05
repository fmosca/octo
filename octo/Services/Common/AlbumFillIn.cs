using Octo.Models.Domain;

namespace Octo.Services.Common;

/// <summary>
/// Which catalog album may fill in a library album's missing songs on getAlbum. A name is not
/// an identity: "Nightcore" by "Nightcore" names one 3-song album on Deezer and a listener's own
/// 388-song collection in the library, with no song in common (octo-player#1). Navidrome's
/// getAlbum carries no album id the catalog shares (no barcode, and a MusicBrainz id only for
/// files tagged that way), so the albums are compared by what is on them, as Roon does: the
/// catalog album must hold most of the library album's songs, each known by its ISRC or by its
/// title and length.
/// </summary>
public static class AlbumFillIn
{
    /// <summary>Two lengths further apart than this, in seconds, are two recordings.</summary>
    public const int LengthSlackSeconds = 5;

    /// <summary>A library song as Navidrome's getAlbum lists it.</summary>
    public sealed record LibraryTrack(string? Title, int? Duration, IReadOnlyCollection<string?> Isrcs);

    /// <summary>A song from Navidrome's JSON, as the merge holds it: title, duration and isrc.</summary>
    public static LibraryTrack FromSubsonic(object? song)
    {
        if (song is not Dictionary<string, object> dict) return new LibraryTrack(null, null, []);
        var title = dict.TryGetValue("title", out var t) ? t?.ToString() : null;
        int? duration = dict.TryGetValue("duration", out var d) ? d switch
        {
            int i => i,
            double x => (int)Math.Round(x),
            _ => null,
        } : null;
        // OpenSubsonic sends a list; an older server may send one string.
        IReadOnlyCollection<string?> isrcs = dict.TryGetValue("isrc", out var i2) ? i2 switch
        {
            string one => [one],
            IEnumerable<object> many => many.Select(v => v?.ToString()).ToList(),
            _ => [],
        } : [];
        return new LibraryTrack(title, duration, isrcs);
    }

    /// <summary>
    /// The search hits that could be the library album, best first: the same artist and title,
    /// then a looser match where one title or artist name holds the other. A hit with too few
    /// tracks to hold half of the library album's <paramref name="librarySongs"/> is left out
    /// before its tracklist is ever fetched.
    /// </summary>
    public static IReadOnlyList<Album> Candidates(IEnumerable<Album> hits, string artistName, string albumName, int librarySongs)
    {
        var list = hits.Where(h => h.Artist != null && !TooSmall(h.SongCount, librarySongs)).ToList();
        var wantedTitle = SongIdentity.Key(albumName);
        var wantedArtist = SongIdentity.Key(artistName);

        var exact = list.Where(h =>
            SongIdentity.SameArtistName(h.Artist, artistName) && SongIdentity.Key(h.Title) == wantedTitle);
        var loose = list.Where(h =>
        {
            var title = SongIdentity.Key(h.Title);
            return SongIdentity.Key(h.Artist).Contains(wantedArtist)
                && title.Length > 0 && wantedTitle.Length > 0
                && (title.Contains(wantedTitle) || wantedTitle.Contains(title));
        });
        return exact.Concat(loose).Distinct().ToList();
    }

    /// <summary>
    /// Whether a catalog album holds the library album: at least one song in common, and at
    /// least half of the library album's songs on it. A library album with more of its own
    /// songs than the catalog album shares is another record that happens to have the name.
    /// </summary>
    public static bool Holds(IReadOnlyList<LibraryTrack> library, IReadOnlyList<Song> catalog)
    {
        var songs = Distinct(library);
        if (songs.Count == 0) return false;
        var shared = songs.Count(l => catalog.Any(c => SameRecording(l, c)));
        return shared > 0 && shared * 2 >= songs.Count;
    }

    /// <summary>
    /// Whether the library already has this catalog song, so it is not offered as missing: the
    /// same ISRC, or the same title ("Song (feat. X)" and "Song" alike, never "Song (Live)").
    /// Length is not asked here, as a second row of the same title reads as a duplicate.
    /// </summary>
    public static bool Owned(IReadOnlyList<LibraryTrack> library, Song catalog)
    {
        var key = SongIdentity.TitleKey(catalog.Title);
        return library.Any(l =>
            SongIdentity.SharesIsrc(l.Isrcs, CatalogIsrcs(catalog))
            || (key.Length > 0 && SongIdentity.TitleKey(l.Title) == key));
    }

    /// <summary>
    /// One recording on both sides: the same ISRC, or the same title at nearly the same length.
    /// An ISRC that differs proves nothing, as a reissue can be given a new one.
    /// </summary>
    public static bool SameRecording(LibraryTrack library, Song catalog)
    {
        if (SongIdentity.SharesIsrc(library.Isrcs, CatalogIsrcs(catalog))) return true;
        var key = SongIdentity.TitleKey(library.Title);
        return key.Length > 0
            && key == SongIdentity.TitleKey(catalog.Title)
            && LengthsAgree(library.Duration, catalog.Duration);
    }

    private static bool TooSmall(int? catalogSongs, int librarySongs) =>
        catalogSongs is > 0 && catalogSongs.Value * 2 < librarySongs;

    private static bool LengthsAgree(int? a, int? b) =>
        a is not > 0 || b is not > 0 || Math.Abs(a.Value - b.Value) <= LengthSlackSeconds;

    private static IEnumerable<string?> CatalogIsrcs(Song song) => [song.Isrc, .. song.Isrcs];

    /// <summary>
    /// The library's songs once each, a FLAC and an MP3 of one song counting once, with the
    /// ISRCs of every copy.
    /// </summary>
    private static List<LibraryTrack> Distinct(IReadOnlyList<LibraryTrack> library) =>
        library
            .Where(l => SongIdentity.TitleKey(l.Title).Length > 0)
            .GroupBy(l => SongIdentity.TitleKey(l.Title), StringComparer.Ordinal)
            .Select(g => new LibraryTrack(
                g.First().Title,
                g.Select(l => l.Duration).FirstOrDefault(d => d > 0),
                g.SelectMany(l => l.Isrcs).ToList()))
            .ToList();

    /// <summary>How many songs the library album holds, each counted once.</summary>
    public static int CountSongs(IReadOnlyList<LibraryTrack> library) => Distinct(library).Count;
}
