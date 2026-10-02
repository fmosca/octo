using Octo.Services.Common;

namespace Octo.Services.LastFm;

/// <summary>
/// Spreads a song radio's artists out. The similar-tracks list puts the seed's own
/// album and artist at the top, so played in order a radio opened with three songs
/// from one album and sounded like the album, not a radio.
/// </summary>
public static class LastFmRadioSpacing
{
    /// <summary>How many other artists play before an artist comes round again.</summary>
    public const int Gap = 3;

    /// <summary>
    /// The songs in their order, except that a song waits until <see cref="Gap"/> other
    /// artists have played since its artist last did, counting the seed, which plays
    /// first. When every song left would have to wait, the next in order plays anyway,
    /// so nothing is ever dropped.
    /// </summary>
    public static List<T> Spread<T>(IReadOnlyList<T> songs, Func<T, string?> artistOf, string? seedArtist)
    {
        var pending = songs.ToList();
        var recent = new List<string>();
        var spread = new List<T>(songs.Count);
        Played(seedArtist);
        while (pending.Count > 0)
        {
            var next = pending.FindIndex(song => !recent.Contains(ArtistKey(artistOf(song))));
            if (next < 0) next = 0;
            spread.Add(pending[next]);
            Played(artistOf(pending[next]));
            pending.RemoveAt(next);
        }
        return spread;

        void Played(string? artist)
        {
            var key = ArtistKey(artist);
            if (key.Length == 0) return;
            recent.Remove(key);
            recent.Add(key);
            if (recent.Count > Gap) recent.RemoveAt(0);
        }
    }

    /// <summary>The main artist, however the credit is written: "Drake feat. Future" is Drake.</summary>
    private static string ArtistKey(string? artist) =>
        string.IsNullOrWhiteSpace(artist) ? "" : SongIdentity.Key(SongIdentity.PrimaryArtist(artist));
}
