using Octo.Models.Domain;
using Octo.Services.Common;
using LibraryTrack = Octo.Services.Common.AlbumFillIn.LibraryTrack;

namespace Octo.Tests;

/// <summary>
/// getAlbum fills a library album in from a catalog album only when that album holds the
/// library's songs, never on the name alone (octo-player#1).
/// </summary>
public sealed class AlbumFillInTests
{
    private static LibraryTrack Lib(string title, int? duration = null, params string[] isrcs) =>
        new(title, duration, isrcs);

    private static Song Cat(string title, int? duration = null, string? isrc = null) =>
        new() { Title = title, Duration = duration, Isrc = isrc };

    [Fact]
    public void Holds_OneOwnedSongOfAFullAlbum()
    {
        Assert.True(AlbumFillIn.Holds([Lib("My Bad", 180)], [Cat("Intro", 60), Cat("My Bad", 181), Cat("Outro", 90)]));
    }

    [Fact]
    public void Holds_NotAnAlbumThatOnlySharesTheName()
    {
        Assert.False(AlbumFillIn.Holds(
            [Lib("Believer (Rock Version)", 216), Lib("Mi Mi Mi (Rock Version)", 168), Lib("MAGIC", 134)],
            [Cat("Love Tonight (Nightcore Remix)", 142), Cat("Angel (Nightcore Remix)", 159), Cat("Sad Songs & Depression (Nightcore Remix)", 280)]));
    }

    [Fact]
    public void Holds_NotWhenMostOfTheLibraryAlbumIsElsewhere()
    {
        // One title in common by chance, out of many: another record.
        var library = Enumerable.Range(1, 20).Select(i => Lib($"Song {i}")).ToList();
        Assert.False(AlbumFillIn.Holds(library, [Cat("Song 1"), Cat("Other A"), Cat("Other B")]));
    }

    [Fact]
    public void Holds_ADeluxeLibraryAlbumAgainstTheStandardEdition()
    {
        // Twelve songs, ten on the standard edition: the same record with two bonus songs.
        var library = Enumerable.Range(1, 12).Select(i => Lib($"Song {i}", 200 + i)).ToList();
        Assert.True(AlbumFillIn.Holds(library, Enumerable.Range(1, 10).Select(i => Cat($"Song {i}", 200 + i)).ToList()));
    }

    [Fact]
    public void Holds_TheSameTitleAtAnotherLengthIsAnotherRecording()
    {
        // "Intro" by name only: a 40 s intro is not the 4 minute one.
        Assert.False(AlbumFillIn.Holds([Lib("Intro", 40)], [Cat("Intro", 240), Cat("Other", 200)]));
        Assert.True(AlbumFillIn.Holds([Lib("Intro", 40)], [Cat("Intro", 43), Cat("Other", 200)]));
    }

    [Fact]
    public void Holds_AnIsrcMatchesWhateverTheTitleSays()
    {
        Assert.True(AlbumFillIn.Holds(
            [Lib("Song - 2011 Remaster", 200, "GB-AAA-00-00001")],
            [Cat("Song", 230, "GBAAA0000001"), Cat("Other", 200)]));
    }

    [Fact]
    public void Holds_ALengthUnknownOnOneSideDoesNotCountAgainst()
    {
        Assert.True(AlbumFillIn.Holds([Lib("Song")], [Cat("Song", 200), Cat("Other", 200)]));
    }

    [Fact]
    public void Holds_CountsAGuestCreditAsTheSameSongButNotALiveTake()
    {
        Assert.True(AlbumFillIn.Holds([Lib("Song (feat. Guest)")], [Cat("Song"), Cat("Other")]));
        Assert.False(AlbumFillIn.Holds([Lib("Song (Live)")], [Cat("Song"), Cat("Other")]));
    }

    [Fact]
    public void Holds_CountsTwoCopiesOfOneSongOnce()
    {
        // A FLAC and an MP3 of "Song", and "Extra": one of two songs shared, which is half.
        Assert.True(AlbumFillIn.Holds([Lib("Song"), Lib("Song"), Lib("Extra")], [Cat("Song"), Cat("Other")]));
    }

    [Fact]
    public void Holds_NothingForAnEmptyLibraryAlbum()
    {
        Assert.False(AlbumFillIn.Holds([], [Cat("Song")]));
        Assert.False(AlbumFillIn.Holds([new LibraryTrack(null, null, []), Lib("")], [Cat("Song")]));
    }

    [Fact]
    public void Owned_ByIsrcOrTitle_SoNoSongIsOfferedTwice()
    {
        var library = new[] { Lib("Song - 2011 Remaster", 200, "GBAAA0000001"), Lib("Other", 100) };

        Assert.True(AlbumFillIn.Owned(library, Cat("Song", 230, "GBAAA0000001")));
        Assert.True(AlbumFillIn.Owned(library, Cat("Other", 300)));
        Assert.False(AlbumFillIn.Owned(library, Cat("New Song", 200)));
    }

    [Fact]
    public void FromSubsonic_ReadsNavidromesSong()
    {
        var song = new Dictionary<string, object>
        {
            ["title"] = "One", ["duration"] = 100, ["isrc"] = new List<object> { "GBAAA0000001" },
        };

        var track = AlbumFillIn.FromSubsonic(song);

        Assert.Equal("One", track.Title);
        Assert.Equal(100, track.Duration);
        Assert.Equal(["GBAAA0000001"], track.Isrcs);
        Assert.Equal(["GBAAA0000001"], AlbumFillIn.FromSubsonic(new Dictionary<string, object> { ["isrc"] = "GBAAA0000001" }).Isrcs);
        Assert.Empty(AlbumFillIn.FromSubsonic(null).Isrcs);
    }

    [Fact]
    public void Candidates_SameArtistAndTitleFirst_ThenLooserMatches()
    {
        var hits = new List<Album>
        {
            new() { Id = "deluxe", Title = "Test Album (Deluxe)", Artist = "Test Artist" },
            new() { Id = "other", Title = "Test Album", Artist = "Someone Else" },
            new() { Id = "exact", Title = "Test Album", Artist = "Test Artist" },
            new() { Id = "unknown", Title = "Test Album", Artist = null! },
        };

        var ids = AlbumFillIn.Candidates(hits, "Test Artist", "Test Album", librarySongs: 2).Select(a => a.Id).ToList();

        Assert.Equal(["exact", "deluxe"], ids);
    }

    [Fact]
    public void Candidates_LeavesOutAnAlbumTooSmallToHoldTheLibrarysSongs()
    {
        // 388 songs cannot be half on a 3-track album: turned down before its tracks are fetched.
        var hits = new List<Album>
        {
            new() { Id = "ep", Title = "Nightcore", Artist = "Nightcore", SongCount = 3 },
            new() { Id = "unknown", Title = "Nightcore", Artist = "Nightcore" },
        };

        Assert.Equal(["unknown"], AlbumFillIn.Candidates(hits, "Nightcore", "Nightcore", librarySongs: 388).Select(a => a.Id));
        Assert.Equal(["ep", "unknown"], AlbumFillIn.Candidates(hits, "Nightcore", "Nightcore", librarySongs: 6).Select(a => a.Id));
    }
}
