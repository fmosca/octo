using Octo.Services.LastFm;

namespace Octo.Tests;

/// <summary>
/// A song radio spreads its artists out, so it does not open with the seed's album.
/// </summary>
public class LastFmRadioSpacingTests
{
    private static List<string> Spread(string seedArtist, params string[] songs) =>
        LastFmRadioSpacing.Spread(songs, s => s.Split(" / ")[0], seedArtist);

    [Fact]
    public void TheSeedsAlbumMatesWaitForOtherArtists()
    {
        // The radio from "$uicideboy$ - BLOODSWEAT", 2026-10-02: the two songs at the
        // top were from the seed's own album.
        var spread = Spread("$uicideboy$",
            "$uicideboy$ / 2009 Reggie Bush", "$uicideboy$ / Angel Grove",
            "Scrim / Father, Hold Me", "Pouya / FIVE SIX", "Bones / HDMI",
            "Night Lovell / Alone", "$uicideboy$ / Matte Black");

        Assert.Equal(new[]
        {
            "Scrim / Father, Hold Me", "Pouya / FIVE SIX", "Bones / HDMI",
            "$uicideboy$ / 2009 Reggie Bush", "Night Lovell / Alone",
            "$uicideboy$ / Angel Grove", "$uicideboy$ / Matte Black",
        }, spread);
    }

    [Fact]
    public void AGuestCreditIsTheSameArtist()
    {
        var spread = Spread("Drake", "Drake feat. Future / Life Is Good", "SZA / Kill Bill");
        Assert.Equal(new[] { "SZA / Kill Bill", "Drake feat. Future / Life Is Good" }, spread);
    }

    [Fact]
    public void NothingIsDroppedWhenOneArtistFillsTheList()
    {
        var songs = new[] { "Bones / A", "Bones / B", "Bones / C" };
        Assert.Equal(songs, Spread("Bones", songs));
    }

    [Fact]
    public void AListAlreadySpreadKeepsItsOrder()
    {
        var songs = new[] { "Scrim / A", "Pouya / B", "Bones / C", "Scrim / D", "Pouya / E" };
        Assert.Equal(songs, Spread("$uicideboy$", songs));
    }

    [Fact]
    public void SongsWithNoArtistAreNeverHeldBack()
    {
        var spread = LastFmRadioSpacing.Spread(new[] { "", "x" }, s => s.Length == 0 ? null : "A", null);
        Assert.Equal(new[] { "", "x" }, spread);
    }
}
