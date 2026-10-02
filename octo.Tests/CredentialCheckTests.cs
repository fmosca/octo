using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// The sign-in check that stands in front of outside songs: what it sends Navidrome, what it
/// keeps, for how long, and what it never keeps.
/// </summary>
public sealed class CredentialCheckTests
{
    /// <summary>A ping that accepts the token "good" for anyone. It can be made to fail, to
    /// hold every call until released, or to never answer at all.</summary>
    private sealed class FakePing : HttpMessageHandler
    {
        public int Calls;
        public readonly ConcurrentQueue<Uri> Urls = new();
        public bool Down;
        public TaskCompletionSource? Gate;
        /// <summary>Set to make every ping hang until the test lets it go.</summary>
        public TaskCompletionSource? Hang;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Urls.Enqueue(request.RequestUri!);
            // Bounded, so a check that waits forever fails the test instead of hanging it.
            if (Hang is { } hang) await Task.WhenAny(hang.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            if (Gate is { } gate) await gate.Task;
            if (Down) throw new HttpRequestException("connection refused");
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var body = query["t"] == "good"
                ? """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""
                : """{"subsonic-response":{"status":"failed","version":"1.16.1","error":{"code":40,"message":"Wrong username or password"}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private readonly FakePing _navidrome = new();
    private readonly CredentialCheck _check = new(NullLogger<CredentialCheck>.Instance);

    private SubsonicProxyService Relay() => new(new ReviewFixtures.OneClientFactory(_navidrome),
        new TestOptionsMonitor<SubsonicSettings>(new SubsonicSettings { Url = "http://navidrome.test" }),
        new HttpContextAccessor());

    private static SubsonicCredential? Credential(string token = "good", string salt = "salt",
        params (string Key, string Value)[] extra)
    {
        var parameters = new Dictionary<string, string>
        {
            ["u"] = "alice", ["t"] = token, ["s"] = salt, ["v"] = "1.16.1", ["c"] = "test",
        };
        foreach (var (key, value) in extra) parameters[key] = value;
        return SubsonicCredential.From(parameters);
    }

    [Fact]
    public async Task Accepted_IsKeptForTheSameSignIn()
    {
        Assert.Equal(CredentialVerdict.Accepted, await _check.CheckAsync(Credential(), Relay()));
        Assert.Equal(CredentialVerdict.Accepted, await _check.CheckAsync(Credential(), Relay()));
        Assert.Equal(1, _navidrome.Calls);
    }

    [Fact]
    public async Task Refused_IsKeptBriefly_ThenAskedAgain()
    {
        _check.RefusedLifetime = TimeSpan.FromMilliseconds(50);

        Assert.Equal(CredentialVerdict.Refused, await _check.CheckAsync(Credential("bad"), Relay()));
        Assert.Equal(CredentialVerdict.Refused, await _check.CheckAsync(Credential("bad"), Relay()));
        Assert.Equal(1, _navidrome.Calls);

        await Task.Delay(100);
        Assert.Equal(CredentialVerdict.Refused, await _check.CheckAsync(Credential("bad"), Relay()));
        Assert.Equal(2, _navidrome.Calls);
    }

    [Fact]
    public async Task Unreachable_IsNeverKept()
    {
        _navidrome.Down = true;

        Assert.Equal(CredentialVerdict.Unreachable, await _check.CheckAsync(Credential(), Relay()));
        Assert.Equal(CredentialVerdict.Unreachable, await _check.CheckAsync(Credential(), Relay()));
        Assert.Equal(2, _navidrome.Calls);
    }

    [Fact]
    public async Task ConcurrentChecks_ShareOneCall()
    {
        _navidrome.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = Relay();

        var checks = Enumerable.Range(0, 5).Select(_ => _check.CheckAsync(Credential(), relay)).ToList();
        await LastFmScrobbleServiceTests.Until(() => _navidrome.Calls >= 1);
        _navidrome.Gate.SetResult();

        Assert.All(await Task.WhenAll(checks), verdict => Assert.Equal(CredentialVerdict.Accepted, verdict));
        Assert.Equal(1, _navidrome.Calls);
    }

    [Fact]
    public async Task OnlyTheSignInGoesToNavidrome()
    {
        var credential = SubsonicCredential.From(new Dictionary<string, string>
        {
            ["u"] = "alice", ["t"] = "good", ["s"] = "salt", ["v"] = "1.16.1", ["c"] = "Symfonium",
            ["id"] = "x", ["query"] = "y", ["f"] = "xml",
        });

        Assert.Equal(CredentialVerdict.Accepted, await _check.CheckAsync(credential, Relay()));

        var url = Assert.Single(_navidrome.Urls);
        Assert.EndsWith("/rest/ping", url.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal(["c", "f", "s", "t", "u", "v"], query.AllKeys.OrderBy(key => key, StringComparer.Ordinal));
        // The client's own name, so the ping lands on the player Navidrome already keeps for it.
        Assert.Equal("Symfonium", query["c"]);
        Assert.Equal("json", query["f"]);
    }

    [Fact]
    public async Task ASignInWithNoClientNameIsSentAsOcto()
    {
        var credential = SubsonicCredential.From(new Dictionary<string, string>
        {
            ["u"] = "alice", ["t"] = "good", ["s"] = "salt",
        });

        await _check.CheckAsync(credential, Relay());

        var query = System.Web.HttpUtility.ParseQueryString(Assert.Single(_navidrome.Urls).Query);
        Assert.Equal("octo", query["c"]);
        Assert.Equal("1.16.1", query["v"]);
    }

    [Fact]
    public async Task ADifferentSaltIsADifferentSignIn()
    {
        await _check.CheckAsync(Credential(salt: "one"), Relay());
        await _check.CheckAsync(Credential(salt: "two"), Relay());

        Assert.Equal(2, _navidrome.Calls);
    }

    [Fact]
    public async Task NoSignIn_IsRefusedWithoutAsking()
    {
        var credential = SubsonicCredential.From(new Dictionary<string, string> { ["u"] = "alice" });

        Assert.Null(credential);
        Assert.Equal(CredentialVerdict.Refused, await _check.CheckAsync(credential, Relay()));
        Assert.Equal(0, _navidrome.Calls);
    }

    [Fact]
    public void Credential_ToString_HidesTheSecret()
    {
        var token = SubsonicCredential.From(new Dictionary<string, string>
        {
            ["u"] = "alice", ["t"] = "tok-3f9a", ["s"] = "salt-77c1",
        })!.ToString();
        var key = SubsonicCredential.From(new Dictionary<string, string> { ["apiKey"] = "key-b81e" })!.ToString();

        Assert.Contains("alice", token);
        Assert.DoesNotContain("tok-3f9a", token);
        Assert.DoesNotContain("salt-77c1", token);
        Assert.DoesNotContain("key-b81e", key);
    }

    [Fact]
    public async Task ASlowNavidromeIsUnreachableAfterFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), _check.CheckTimeout);
        _check.CheckTimeout = TimeSpan.FromMilliseconds(50);
        _navidrome.Hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(CredentialVerdict.Unreachable, await _check.CheckAsync(Credential(), Relay()));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"took {started.Elapsed}");

            // A timeout is never kept: the next request asks again.
            Assert.Equal(CredentialVerdict.Unreachable, await _check.CheckAsync(Credential(), Relay()));
            Assert.Equal(2, _navidrome.Calls);
        }
        finally
        {
            _navidrome.Hang.SetResult();
        }
    }
}
