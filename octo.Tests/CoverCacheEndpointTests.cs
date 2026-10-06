using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Octo.Tests;

/// <summary>
/// getCoverArt against a library id: the first request relays to Navidrome, a repeat —
/// what a queue view fires constantly — must come from the cache instead of another
/// multi-second upstream fetch, and the credential check must run even on a cache hit,
/// so nobody rides a cached cover on someone else's sign-in.
/// </summary>
[Trait("Host", "Boot")]
public sealed class CoverCacheEndpointTests
{
    /// <summary>Navidrome as far as these calls need it: a ping that accepts the token
    /// "good" for anyone, and a getCoverArt that serves a small image whose bytes,
    /// content type and call count the tests can watch. `ServePng` makes it answer
    /// PNG, the type a Navidrome library cover can carry alongside JPEG.</summary>
    private sealed class FakeNavidrome : HttpMessageHandler
    {
        public int CoverCalls;
        public bool ServePng;
        public bool Down;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (path.EndsWith("/rest/ping", StringComparison.Ordinal) && !Down)
                return Task.FromResult(Json(query["t"] == "good"
                    ? """{"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome"}}"""
                    : """{"subsonic-response":{"status":"failed","version":"1.16.1","error":{"code":40,"message":"Wrong username or password"}}}"""));
            if (path.EndsWith("/rest/getCoverArt", StringComparison.Ordinal) && !Down)
            {
                Interlocked.Increment(ref CoverCalls);
                // The first byte is the parsed size % 256, so a mixed-up cache key
                // would serve one size's bytes for another and fail below.
                var size = query["size"] ?? "";
                var body = new byte[] { int.TryParse(size, out var n) ? (byte)(n % 256) : (byte)63, 0, 0, 0 };
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(body),
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    ServePng ? "image/png" : "image/jpeg");
                return Task.FromResult(response);
            }
            // Down: both pings and covers get connection-level failures, the way an
            // unreachable Navidrome behaves from the relay's seat.
            if (Down) throw new HttpRequestException("navidrome down for the test");
            if (path.EndsWith("/rest/getMusicFolders", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"subsonic-response":{"status":"ok","version":"1.16.1","musicFolders":{"musicFolder":[{"id":0,"name":"lib"}]}}}"""));
            return Task.FromResult(Json("""{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    private sealed class CoverWebFactory : WebApplicationFactory<Program>
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "octo-cover-web-" + Guid.NewGuid());
        public FakeNavidrome Navidrome { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_directory);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Subsonic:Url"] = "http://navidrome.test",
                    ["Subsonic:AutoDetectDownloadPath"] = "false",
                    ["Library:DownloadPath"] = _directory,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new ReviewFixtures.OneClientFactory(Navidrome));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        }
    }

    private const string Auth = "u=alice&t=good&s=salt&v=1.16.1&c=test&f=json";
    private const string AuthBad = "u=alice&t=bad&s=salt&v=1.16.1&c=test&f=json";

    [Fact]
    public async Task RepeatedFetch_RelaysOnce()
    {
        await using var factory = new CoverWebFactory();
        using var client = factory.CreateClient();

        var first = await client.GetAsync($"/rest/getCoverArt.view?{Auth}&id=mf-abc&size=800");
        var second = await client.GetAsync($"/rest/getCoverArt.view?{Auth}&id=mf-abc&size=800");

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, factory.Navidrome.CoverCalls);
    }

    [Fact]
    public async Task DifferentSize_IsADifferentResponse()
    {
        await using var factory = new CoverWebFactory();
        using var client = factory.CreateClient();

        var small = await client.GetAsync($"/rest/getCoverArt.view?{Auth}&id=mf-abc&size=300");
        var large = await client.GetAsync($"/rest/getCoverArt.view?{Auth}&id=mf-abc&size=800");

        small.EnsureSuccessStatusCode();
        large.EnsureSuccessStatusCode();
        Assert.NotEqual(await small.Content.ReadAsByteArrayAsync(), await large.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, factory.Navidrome.CoverCalls);
    }

    [Fact]
    public async Task BadCredentials_NeverRideACachedEntry()
    {
        await using var factory = new CoverWebFactory();
        using var client = factory.CreateClient();

        var good = await client.GetAsync($"/rest/getCoverArt.view?{Auth}&id=mf-abc&size=800");
        good.EnsureSuccessStatusCode();

        var bad = await client.GetAsync($"/rest/getCoverArt.view?{AuthBad}&id=mf-abc&size=800");

        // The cached bytes belong to an accepted sign-in; a wrong token gets the same
        // refusal it would have had without the cache (a Subsonic error envelope under
        // HTTP 200), and no Navidrome cover call and no cache entry ride on it.
        var body = await bad.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":40", body);
        Assert.Equal(1, factory.Navidrome.CoverCalls);
    }

    [Fact]
    public async Task RelayedContentType_IsKeptWithTheCachedBytes()
    {
        await using var factory = new CoverWebFactory();
        using var client = factory.CreateClient();
        factory.Navidrome.ServePng = true;

        var first = await client.GetAsync($"/rest/getCoverArt.view?{Auth}&id=mf-abc&size=800");
        var second = await client.GetAsync($"/rest/getCoverArt.view?{Auth}&id=mf-abc&size=800");

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/png", second.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, factory.Navidrome.CoverCalls);
    }

    [Fact]
    public async Task NavidromeOutage_ServesThePlaceholderNotAnError()
    {
        // CredentialCheck remembers a verdict for ten minutes, but this factory boots
        // its own, so with Navidrome never answering the ping the verdict is
        // Unreachable — and a cover outage must fall through to the unbranded
        // placeholder, the way it did before the cache existed, because an error
        // envelope is what makes a client drop the queue row.
        await using var factory = new CoverWebFactory();
        using var client = factory.CreateClient();
        factory.Navidrome.Down = true;

        var outage = await client.GetAsync($"/rest/getCoverArt.view?{Auth}&id=mf-abc&size=800");

        Assert.True(outage.IsSuccessStatusCode, $"status={(int)outage.StatusCode}");
        Assert.Contains("image/jpeg", outage.Content.Headers.ContentType?.MediaType ?? "");
        Assert.Equal(0, factory.Navidrome.CoverCalls);
    }
}