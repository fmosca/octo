using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Octo.Models.Domain;
using Octo.Services.LastFm;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// An OpenSubsonic API key sign-in carries no <c>u</c> (Navidrome refuses one beside the key),
/// so everything Octo keeps per listener has to learn the name from Navidrome instead. Before,
/// such a listener learned nothing from a scrobble and shared one search order with every
/// other key user.
/// </summary>
[Trait("Host", "Boot")]
public sealed class RequestIdentityTests
{
    private static string RegisterOutsideSong(RadioWebFactory fixture) =>
        fixture.Services.GetRequiredService<ExternalIdRegistry>().Register(new SoulseekRouting
        {
            Kind = RoutingKind.Song, Artist = "Bladee", Title = "Be Nice 2 Me", Album = "Icedancer", Duration = 154,
        });

    private static async Task WhenIdle(RadioWebFactory fixture)
    {
        var service = fixture.Services.GetRequiredService<LastFmScrobbleService>();
        await LastFmScrobbleServiceTests.Until(() => service.Outstanding == 0);
    }

    [Fact]
    public async Task ApiKeyScrobble_ReachesTheKeyOwnersLastFm()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();

        var body = await client.GetStringAsync($"/rest/scrobble?apiKey=bob-key&v=1.16.1&c=x&f=json&id={id}&submission=true");
        await client.GetStringAsync($"/rest/scrobble?apiKey=bob-key&v=1.16.1&c=x&f=json&id={id}&submission=false");
        await WhenIdle(fixture);

        Assert.Contains("\"status\":\"ok\"", body);
        Assert.Equal("sk-bob", Assert.Single(fixture.Handler.LastFm.CallsTo("track.scrobble"))["sk"]);
        Assert.Single(fixture.Handler.LastFm.CallsTo("track.updateNowPlaying"));
        Assert.Single(fixture.Handler.ListenBrainzSubmissions,
            submission => submission.Authorization == "Token lb-bob-token");
        Assert.Single(fixture.State.GetUser("bob").Plays);
        // Asked once, then remembered.
        Assert.Equal(1, fixture.Handler.TokenInfoCalls);
    }

    [Fact]
    public async Task ApiKeyScrobble_WhenNavidromeWillNotSayWhose_LearnsNothing()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        fixture.Handler.TokenInfoFails = true;
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();

        var body = await client.GetStringAsync($"/rest/scrobble?apiKey=bob-key&v=1.16.1&c=x&f=json&id={id}&submission=true");
        await WhenIdle(fixture);

        Assert.Contains("\"status\":\"ok\"", body);
        Assert.Equal(1, fixture.Handler.TokenInfoCalls);
        Assert.Empty(fixture.Handler.LastFm.Calls);
        Assert.Empty(fixture.Handler.ListenBrainzSubmissions);
        Assert.Empty(fixture.State.GetUser("bob").Plays);
    }

    /// <summary>A key Navidrome refuses is never looked up: the credential check comes first.</summary>
    [Fact]
    public async Task UnknownApiKey_IsNeverLookedUp()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();

        var body = await client.GetStringAsync($"/rest/scrobble?apiKey=stranger&v=1.16.1&c=x&f=json&id={id}&submission=true");
        await WhenIdle(fixture);

        Assert.Contains("failed", body);
        Assert.Equal(0, fixture.Handler.TokenInfoCalls);
        Assert.Empty(fixture.Handler.LastFm.Calls);
    }

    [Fact]
    public async Task TwoApiKeyUsers_NeverShareASearchOrder()
    {
        await using var fixture = new SearchPagingWebFactory();
        using var client = fixture.CreateClient();
        var cache = fixture.Services.GetRequiredService<SearchSongOrderCache>();

        await client.GetStringAsync(Search(0, 20, "&apiKey=alice-key"));
        await client.GetStringAsync(Search(0, 30, "&apiKey=bob-key"));
        await client.GetStringAsync(Search(20, 20, "&apiKey=alice-key"));

        Assert.Equal(20, cache.Get(SearchSongOrderCache.Key("alice", "Test", "rest/search3", null, "paging"), 20, 20)!.PageOneCount);
        Assert.Equal(30, cache.Get(SearchSongOrderCache.Key("bob", "Test", "rest/search3", null, "paging"), 30, 30)!.PageOneCount);
        Assert.Null(cache.Get(SearchSongOrderCache.Key("", "Test", "rest/search3", null, "paging"), 20, 20));
        Assert.Equal(2, fixture.Upstream.TokenInfoCalls);
    }

    /// <summary>Nobody to file an order under: page one keeps nothing, and a later page reads
    /// nothing, not even an order some other nameless request left, and is built again.</summary>
    [Fact]
    public async Task NoIdentity_NeitherReadsNorWritesTheSearchOrder()
    {
        await using var fixture = new SearchPagingWebFactory();
        using var client = fixture.CreateClient();
        var cache = fixture.Services.GetRequiredService<SearchSongOrderCache>();

        await client.GetStringAsync(Search(0, 20, "&apiKey=unknown-key"));
        Assert.Equal(0, cache.Count);

        var planted = new Song { Id = "ph-planted", Artist = "Someone", Title = "Planted", IsLocal = false };
        cache.Set(SearchSongOrderCache.Key("", "Test", "rest/search3", null, "paging"),
            SearchSongOrder.From([planted], 20, 12, 8, []));
        var anonymous = Ids(await client.GetStringAsync(Search(20, 20, "")));
        var named = Ids(await client.GetStringAsync(Search(20, 20, "&u=carol&t=token&s=salt")));

        Assert.DoesNotContain("ph-planted", anonymous);
        Assert.Equal(named, anonymous);
        Assert.NotNull(cache.Get(SearchSongOrderCache.Key("carol", "Test", "rest/search3", null, "paging"), 20, 20));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void RadioStreamSession_KeepsTheApiKey_ForTheRelaysItMakesLater()
    {
        var store = new LastFmRadioStreamSessionStore();
        var token = store.Issue("bob", "station", new Dictionary<string, string>
        {
            ["apiKey"] = "bob-key", ["v"] = "1.16.1", ["c"] = "x", ["id"] = "not-auth",
        });

        var session = store.Get(token)!;
        Assert.Equal("bob-key", session.Authentication["apiKey"]);
        Assert.DoesNotContain("id", session.Authentication.Keys);
    }

    [Fact]
    public void TokenInfo_IsReadOnlyFromAnOkAnswer()
    {
        static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

        Assert.Equal("bob", RequestIdentity.TokenInfoUsername(Bytes(
            """{"subsonic-response":{"status":"ok","tokenInfo":{"username":" bob "}}}""")));
        Assert.Null(RequestIdentity.TokenInfoUsername(Bytes(
            """{"subsonic-response":{"status":"failed","tokenInfo":{"username":"bob"}}}""")));
        Assert.Null(RequestIdentity.TokenInfoUsername(Bytes(
            """{"subsonic-response":{"status":"ok","tokenInfo":{"username":""}}}""")));
        Assert.Null(RequestIdentity.TokenInfoUsername(Bytes("<subsonic-response status=\"ok\"/>")));
    }

    /// <summary>A Navidrome whose tokenInfo will not say is not asked again on every request.</summary>
    [Fact]
    public async Task ApiKey_NavidromeWouldNotName_IsNotAskedAgainStraightAway()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        fixture.Handler.TokenInfoFails = true;
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();

        for (var i = 0; i < 3; i++)
            await client.GetStringAsync($"/rest/scrobble?apiKey=bob-key&v=1.16.1&c=x&f=json&id={id}&submission=false");

        Assert.Equal(1, fixture.Handler.TokenInfoCalls);
        Assert.Empty(fixture.Handler.LastFm.Calls);
    }

    /// <summary>Only briefly, though: once the wait is over the key is asked about again.</summary>
    [Fact]
    public async Task ApiKey_NavidromeWouldNotName_IsAskedAgainAfterAWhile()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        fixture.Services.GetRequiredService<RequestIdentity>().UnnamedLifetime = TimeSpan.FromMilliseconds(1);
        fixture.Handler.TokenInfoFails = true;
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();
        var url = $"/rest/scrobble?apiKey=bob-key&v=1.16.1&c=x&f=json&id={id}&submission=false";

        await client.GetStringAsync(url);
        await Task.Delay(50);
        fixture.Handler.TokenInfoFails = false;
        await client.GetStringAsync(url);
        await WhenIdle(fixture);

        Assert.Equal(2, fixture.Handler.TokenInfoCalls);
        Assert.Single(fixture.Handler.LastFm.CallsTo("track.updateNowPlaying"));
    }

    /// <summary>Requests with one key arriving together make one tokenInfo call between them.</summary>
    [Fact]
    public async Task ApiKey_RequestsArrivingTogether_AskOnce()
    {
        await using var fixture = new RadioWebFactory(lastFmScrobbling: true);
        fixture.Handler.TokenInfoDelay = TimeSpan.FromMilliseconds(300);
        var id = RegisterOutsideSong(fixture);
        using var client = fixture.CreateClient();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.GetStringAsync($"/rest/scrobble?apiKey=bob-key&v=1.16.1&c=x&f=json&id={id}&submission=false")));
        await WhenIdle(fixture);

        Assert.Equal(1, fixture.Handler.TokenInfoCalls);
        Assert.Equal(4, fixture.Handler.LastFm.CallsTo("track.updateNowPlaying").Count);
    }

    private static string Search(int offset, int count, string auth) =>
        $"/rest/search3.view?query=paging&songCount={count}&songOffset={offset}&albumCount=0&artistCount=0" +
        $"{auth}&v=1.16.1&c=Test&f=json";

    private static List<string> Ids(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("subsonic-response").GetProperty("searchResult3")
            .TryGetProperty("song", out var songs)
            ? songs.EnumerateArray().Select(song => song.GetProperty("id").GetString()!).ToList()
            : [];
    }
}
