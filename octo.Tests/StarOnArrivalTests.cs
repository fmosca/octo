using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// #71: with StarDownloadsForRequester on (off by default), a star from another app on a song
/// Octo found becomes a Navidrome favorite once the song lands, for the person who starred it,
/// signed as them, and only once. A song already owned is favorited whatever it says.
/// </summary>
public sealed class StarOnArrivalTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ConcurrentQueue<(string Endpoint, Dictionary<string, string> Parameters)> _calls = new();
    private readonly TestOptionsMonitor<SubsonicSettings> _settings =
        TestOptions.Monitor(new SubsonicSettings { StarDownloadsForRequester = true });
    private bool _alreadyStarred;

    private static AcquisitionTracker Tracker(bool canLook = true)
    {
        var tracker = new AcquisitionTracker(NullLogger<AcquisitionTracker>.Instance, null)
        {
            VisibilityPoll = TimeSpan.FromMilliseconds(10),
        };
        // Each song turns up in Navidrome under its title, lower case: "T1" is nd-t1.
        if (canLook) tracker.LibraryLookup = (_, title, _, _) => Task.FromResult<string?>("nd-" + title.ToLowerInvariant());
        return tracker;
    }

    private StarOnArrival Stars(AcquisitionTracker tracker, TimeProvider? time = null) =>
        new(tracker, scopes: null!, _settings, NullLogger<StarOnArrival>.Instance, time)
        {
            VisibilityPoll = TimeSpan.FromMilliseconds(10),
            Call = (endpoint, parameters) =>
            {
                _calls.Enqueue((endpoint, parameters));
                var body = endpoint == "rest/getSong"
                    ? "{\"subsonic-response\":{\"status\":\"ok\",\"song\":{\"id\":\"" + parameters["id"] + "\",\"albumId\":\"al-1\""
                      + (_alreadyStarred ? ",\"starred\":\"2026-10-01T10:00:00Z\"" : "") + "}}}"
                    : """{"subsonic-response":{"status":"ok"}}""";
                return Task.FromResult(Encoding.UTF8.GetBytes(body));
            },
        };

    private static SubsonicCredential Credential(string user) => SubsonicCredential.From(new Dictionary<string, string>
    {
        ["u"] = user, ["t"] = "token-" + user, ["s"] = "salt-" + user, ["c"] = "Symfonium",
    })!;

    private List<Dictionary<string, string>> Calls(string endpoint) =>
        _calls.Where(call => call.Endpoint == endpoint).Select(call => call.Parameters).ToList();

    [Fact]
    public async Task AStarredSongIsFavoritedForTheStarrerWhenItArrives()
    {
        var tracker = Tracker();
        using var stars = Stars(tracker);
        tracker.Begin("soulseek", "abc", "abc", "alice", "Massive Attack", "Teardrop");
        stars.HoldSong("soulseek", "abc", Credential("alice"), "alice");

        tracker.Imported("soulseek", "abc", "Massive Attack", "Teardrop", "/music/teardrop.flac");

        await LastFmScrobbleServiceTests.Until(() => Calls("rest/star").Count == 1);
        var calls = _calls.ToList();
        Assert.Equal("rest/getSong", calls[0].Endpoint);
        Assert.Equal("nd-teardrop", calls[0].Parameters["id"]);
        Assert.Equal("alice", calls[0].Parameters["u"]);
        var star = Calls("rest/star").Single();
        Assert.Equal("nd-teardrop", star["id"]);
        Assert.Equal("alice", star["u"]);
        Assert.Equal("token-alice", star["t"]);
        Assert.Equal("salt-alice", star["s"]);
        Assert.Equal(0, stars.Held);
    }

    [Fact]
    public async Task EachStarrerGetsTheirOwnFavorite()
    {
        var tracker = Tracker();
        using var stars = Stars(tracker);
        tracker.Begin("soulseek", "abc", "abc", "alice", "A", "Song");
        stars.HoldSong("soulseek", "abc", Credential("alice"), "alice");
        stars.HoldSong("soulseek", "abc", Credential("bob"), "bob");

        tracker.Imported("soulseek", "abc", "A", "Song", "/music/song.flac");

        await LastFmScrobbleServiceTests.Until(() => Calls("rest/star").Count == 2);
        Assert.Equal(["alice", "bob"], Calls("rest/star").Select(call => call["u"]).Order());
        Assert.All(Calls("rest/star"), call => Assert.Equal("nd-song", call["id"]));
    }

    [Fact]
    public void AFailedDownloadDropsTheHoldWithoutACall()
    {
        var tracker = Tracker();
        using var stars = Stars(tracker);
        tracker.Begin("soulseek", "abc", "abc", "alice");
        stars.HoldSong("soulseek", "abc", Credential("alice"), "alice");

        tracker.Fail("soulseek", "abc", "No source had it");

        Assert.Equal(0, stars.Held);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task ASongNavidromeNeverShowsIsNotFavorited()
    {
        var tracker = Tracker(canLook: false);
        using var stars = Stars(tracker);
        tracker.Begin("soulseek", "abc", "abc", "alice");
        stars.HoldSong("soulseek", "abc", Credential("alice"), "alice");

        tracker.Imported("soulseek", "abc", "A", "Song", "/music/song.flac");

        Assert.Equal(0, stars.Held);
        await Task.Delay(100);
        Assert.Empty(_calls);
    }

    private static void StartAlbum(AcquisitionTracker tracker, StarOnArrival stars)
    {
        tracker.BeginAlbum("soulseek", "alb", "alice");
        stars.HoldAlbum("soulseek", "alb", Credential("alice"), "alice");
        tracker.Announce("soulseek", "alb", null,
            [("t1", "Air", "T1", "Moon Safari"), ("t2", "Air", "T2", "Moon Safari")]);
    }

    [Fact]
    public async Task AnAlbumStarFavoritesTheAlbumOnce()
    {
        var tracker = Tracker();
        using var stars = Stars(tracker);
        StartAlbum(tracker, stars);

        tracker.Imported("soulseek", "t1", "Air", "T1", "/music/t1.flac");
        tracker.Imported("soulseek", "t2", "Air", "T2", "/music/t2.flac");

        await LastFmScrobbleServiceTests.Until(() => Calls("rest/star").Count == 1);
        await Task.Delay(100);
        var star = Assert.Single(Calls("rest/star"));
        Assert.Equal("al-1", star["albumId"]);
        Assert.False(star.ContainsKey("id"));
        Assert.Equal("alice", star["u"]);
        Assert.Equal(0, stars.Held);
    }

    [Fact]
    public async Task AnAlbumHoldSurvivesAFailedTrack()
    {
        var tracker = Tracker();
        using var stars = Stars(tracker);
        StartAlbum(tracker, stars);

        tracker.Fail("soulseek", "t1", "No source had it");
        Assert.Equal(1, stars.Held);
        tracker.Imported("soulseek", "t2", "Air", "T2", "/music/t2.flac");

        await LastFmScrobbleServiceTests.Until(() => Calls("rest/star").Count == 1);
        Assert.Equal("al-1", Calls("rest/star").Single()["albumId"]);
    }

    [Fact]
    public async Task SettingOffAtArrivalStarsNothing()
    {
        var tracker = Tracker();
        using var stars = Stars(tracker);
        tracker.Begin("soulseek", "abc", "abc", "alice");
        stars.HoldSong("soulseek", "abc", Credential("alice"), "alice");
        _settings.Set(new SubsonicSettings { StarDownloadsForRequester = false });

        tracker.Imported("soulseek", "abc", "A", "Song", "/music/song.flac");

        await LastFmScrobbleServiceTests.Until(() => stars.Held == 0);
        await Task.Delay(100);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task TheDefaultIsThatADownloadIsNotFavorited()
    {
        Assert.False(new SubsonicSettings().StarDownloadsForRequester);
        var tracker = Tracker();
        _settings.Set(new SubsonicSettings());
        using var stars = Stars(tracker);
        tracker.Begin("soulseek", "abc", "abc", "alice");
        stars.HoldSong("soulseek", "abc", Credential("alice"), "alice");

        tracker.Imported("soulseek", "abc", "A", "Song", "/music/song.flac");

        await LastFmScrobbleServiceTests.Until(() => stars.Held == 0);
        await Task.Delay(100);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task ASongAlreadyOwnedIsFavoritedWithTheSettingOff()
    {
        var tracker = Tracker();
        _settings.Set(new SubsonicSettings { StarDownloadsForRequester = false });
        using var stars = Stars(tracker);
        tracker.Begin("soulseek", "abc", "abc", "alice");
        stars.HoldSong("soulseek", "abc", Credential("alice"), "alice");

        Assert.True(stars.FavoriteOwned("soulseek", "abc", "nd-owned", "A", "Song", "/music/song.flac"));

        await LastFmScrobbleServiceTests.Until(() => Calls("rest/star").Count == 1);
        Assert.Equal("nd-owned", Calls("rest/star").Single()["id"]);
        Assert.Equal(0, stars.Held);
    }

    [Fact]
    public void AnOwnedSongFromOctosOwnAppsIsNotFavorited()
    {
        using var stars = Stars(Tracker());
        Assert.False(stars.FavoriteOwned("soulseek", "abc", "nd-owned", "A", "Song", "/music/song.flac"));
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task AlreadyAFavoriteIsLeftAlone()
    {
        _alreadyStarred = true;
        var tracker = Tracker();
        using var stars = Stars(tracker);
        tracker.Begin("soulseek", "abc", "abc", "alice");
        stars.HoldSong("soulseek", "abc", Credential("alice"), "alice");

        tracker.Imported("soulseek", "abc", "A", "Song", "/music/song.flac");

        await LastFmScrobbleServiceTests.Until(() => Calls("rest/getSong").Count == 1);
        await Task.Delay(100);
        Assert.Empty(Calls("rest/star"));
    }

    [Fact]
    public void HoldsExpireAfterADay()
    {
        var clock = new ManualClock();
        using var stars = Stars(Tracker(), clock);
        stars.HoldSong("soulseek", "old", Credential("alice"), "alice");

        clock.Now += TimeSpan.FromHours(25);
        stars.HoldSong("soulseek", "new", Credential("alice"), "alice");

        Assert.Equal(1, stars.Held);
    }

    [Theory]
    [InlineData("Octo", true)]
    [InlineData("octo", true)]
    [InlineData(" Octo ", true)]
    [InlineData("octo-android", false)]
    [InlineData("Symfonium", false)]
    [InlineData(null, false)]
    public void IsOctoApp(string? client, bool expected) => Assert.Equal(expected, StarOnArrival.IsOctoApp(client));

    [Fact]
    public async Task StarWhenVisibleFavoritesTheReplacement()
    {
        using var stars = Stars(Tracker());
        stars.LibraryLookup = (_, _, _, _) => Task.FromResult<string?>("nd-new");

        stars.StarWhenVisible(Credential("alice"), "alice", "Air", "La Femme d'Argent", "/music/femme.flac");

        await LastFmScrobbleServiceTests.Until(() => Calls("rest/star").Count == 1);
        var star = Calls("rest/star").Single();
        Assert.Equal("nd-new", star["id"]);
        Assert.Equal("alice", star["u"]);
    }

    [Theory]
    [InlineData("s3cret")]
    [InlineData("enc:733363726574")]
    public async Task APasswordIsNeverHeld_OnlyATokenMadeFromIt(string sent)
    {
        var tracker = Tracker();
        using var stars = Stars(tracker);
        tracker.Begin("soulseek", "abc", "abc", "alice");
        var credential = SubsonicCredential.From(new Dictionary<string, string>
        {
            ["u"] = "alice", ["p"] = sent, ["c"] = "Symfonium",
        })!;
        stars.HoldSong("soulseek", "abc", credential, "alice");

        tracker.Imported("soulseek", "abc", "A", "Song", "/music/song.flac");

        await LastFmScrobbleServiceTests.Until(() => Calls("rest/star").Count == 1);
        Assert.All(_calls, call =>
        {
            var parameters = call.Parameters;
            Assert.False(parameters.ContainsKey("p"));
            Assert.Equal("alice", parameters["u"]);
            Assert.Matches("^[0-9a-f]{12}$", parameters["s"]);
            var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("s3cret" + parameters["s"]))).ToLowerInvariant();
            Assert.Equal(expected, parameters["t"]);
        });
    }

    [Fact]
    public void WithoutPassword_LeavesATokenSignInAsItIs()
    {
        var credential = Credential("alice");
        Assert.Same(credential, credential.WithoutPassword());
    }
}
