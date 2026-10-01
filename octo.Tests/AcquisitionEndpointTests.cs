using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Octo.Services.Common;

namespace Octo.Tests;

/// <summary>
/// getAcquisitions as the Octo app sees it through the Subsonic API: checked against
/// Navidrome with the caller's own credentials, scoped to what that caller hearted, and in the
/// exact JSON shape the app parses. Plus the extension that tells the app to ask at all.
/// </summary>
public sealed class AcquisitionEndpointTests
{
    /// <summary>Navidrome as far as these calls need it: a ping that accepts the token
    /// "good" for anyone, and a short extension list in either format.</summary>
    private sealed class FakeNavidrome : HttpMessageHandler
    {
        public int Pings;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var json = query["f"] == "json";
            if (uri.AbsolutePath.EndsWith("/rest/ping", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Pings);
                return Task.FromResult(Json(query["t"] == "good"
                    ? """{"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome"}}"""
                    : """{"subsonic-response":{"status":"failed","version":"1.16.1","error":{"code":40,"message":"Wrong username or password"}}}"""));
            }
            if (uri.AbsolutePath.EndsWith("/rest/getOpenSubsonicExtensions", StringComparison.Ordinal))
            {
                return Task.FromResult(json
                    ? Json("""{"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome","serverVersion":"0.58.0","openSubsonic":true,"openSubsonicExtensions":[{"name":"formPost","versions":[1]},{"name":"songLyrics","versions":[1]}]}}""")
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """<subsonic-response xmlns="http://subsonic.org/restapi" status="ok" version="1.16.1" type="navidrome"><openSubsonicExtensions name="formPost"><versions>1</versions></openSubsonicExtensions></subsonic-response>""",
                            Encoding.UTF8, "application/xml"),
                    });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class AcquisitionWebFactory : WebApplicationFactory<Program>
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "octo-acq-web-" + Guid.NewGuid());
        public FakeNavidrome Navidrome { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_directory);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Subsonic:Url"] = "http://navidrome.test",
                    ["Subsonic:AutoDetectDownloadPath"] = "false",
                    ["Soulseek:BaseUrl"] = "http://127.0.0.1:1",
                    ["YouTube:ShimUrl"] = "http://127.0.0.1:1",
                    ["Library:DownloadPath"] = _directory,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new ReviewFixtures.OneClientFactory(Navidrome));
            });
        }

        public AcquisitionTracker Tracker => Services.GetRequiredService<AcquisitionTracker>();

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string Auth(string user, string token = "good") =>
        $"u={user}&t={token}&s=salt&v=1.16.1&c=octo-android";

    private static void Seed(AcquisitionTracker tracker)
    {
        tracker.Begin("soulseek", "3kX9Qm", "3kX9Qm", "alice", "Daft Punk", "Da Funk", "Homework");
        tracker.Transfer("soulseek", "3kX9Qm", 12_180_000, 29_000_000, 42.0, "Soulseek");
        tracker.Begin("soulseek", "7bYz2p", "7bYz2p", "bob", "Air", "Sexy Boy", "Moon Safari");
    }

    [Fact]
    public async Task GetAcquisitions_ReturnsOnlyTheCallersRowsInTheAppsShape()
    {
        await using var factory = new AcquisitionWebFactory();
        using var client = factory.CreateClient();
        Seed(factory.Tracker);

        // No f=json: JSON regardless.
        var response = await client.GetAsync($"/rest/getAcquisitions.view?{Auth("alice")}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var envelope = doc.RootElement.GetProperty("subsonic-response");
        Assert.Equal("ok", envelope.GetProperty("status").GetString());
        Assert.Equal("1.16.1", envelope.GetProperty("version").GetString());
        Assert.Equal("octo", envelope.GetProperty("type").GetString());

        var rows = envelope.GetProperty("acquisitions").GetProperty("acquisition").EnumerateArray().ToList();
        var row = Assert.Single(rows);

        Assert.Equal(
            ["ahead", "album", "artist", "bytesDone", "bytesTotal", "error", "id", "libraryId", "note",
             "progress", "source", "startedAt", "state", "title", "updatedAt"],
            row.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("3kX9Qm", row.GetProperty("id").GetString());
        Assert.Equal("Daft Punk", row.GetProperty("artist").GetString());
        Assert.Equal("Da Funk", row.GetProperty("title").GetString());
        Assert.Equal("Homework", row.GetProperty("album").GetString());
        Assert.Equal("downloading", row.GetProperty("state").GetString());
        Assert.Equal(0.42, row.GetProperty("progress").GetDouble());
        Assert.Equal(12_180_000, row.GetProperty("bytesDone").GetInt64());
        Assert.Equal(29_000_000, row.GetProperty("bytesTotal").GetInt64());
        Assert.Equal("Soulseek", row.GetProperty("source").GetString());
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"), row.GetProperty("startedAt").GetString());
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$"), row.GetProperty("updatedAt").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("error").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("libraryId").ValueKind);
    }

    [Fact]
    public async Task GetAcquisitions_AnotherUserSeesOnlyTheirOwn()
    {
        await using var factory = new AcquisitionWebFactory();
        using var client = factory.CreateClient();
        Seed(factory.Tracker);

        using var bob = JsonDocument.Parse(await client.GetStringAsync($"/rest/getAcquisitions?{Auth("bob")}&f=json"));
        var row = Assert.Single(bob.RootElement.GetProperty("subsonic-response").GetProperty("acquisitions")
            .GetProperty("acquisition").EnumerateArray());
        Assert.Equal("7bYz2p", row.GetProperty("id").GetString());
        Assert.Equal("queued", row.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("progress").ValueKind);

        using var carol = JsonDocument.Parse(await client.GetStringAsync($"/rest/getAcquisitions?{Auth("carol")}&f=json"));
        Assert.Empty(carol.RootElement.GetProperty("subsonic-response").GetProperty("acquisitions")
            .GetProperty("acquisition").EnumerateArray());
    }

    [Fact]
    public async Task StarringAFoundSong_ListsItForTheStarrerUnderTheStarredId()
    {
        await using var factory = new AcquisitionWebFactory();
        using var client = factory.CreateClient();
        var id = factory.Services.GetRequiredService<Octo.Services.Soulseek.ExternalIdRegistry>()
            .Register(new Octo.Services.Soulseek.SoulseekRouting
            {
                Kind = Octo.Services.Soulseek.RoutingKind.Song,
                Artist = "Massive Attack", Title = "Teardrop", Album = "Mezzanine", Duration = 330,
            });

        var star = await client.GetAsync($"/rest/star.view?{Auth("alice")}&f=json&id={id}");
        star.EnsureSuccessStatusCode();

        // No worker runs in this host, so the heart waits in the queue, which is the point.
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/rest/getAcquisitions.view?{Auth("alice")}"));
        var row = Assert.Single(doc.RootElement.GetProperty("subsonic-response").GetProperty("acquisitions")
            .GetProperty("acquisition").EnumerateArray());
        Assert.Equal(id, row.GetProperty("id").GetString());
        Assert.Equal("queued", row.GetProperty("state").GetString());
        Assert.Equal("Massive Attack", row.GetProperty("artist").GetString());
        Assert.Equal("Teardrop", row.GetProperty("title").GetString());
        Assert.Empty(factory.Tracker.ForUser("bob"));
    }

    [Fact]
    public async Task GetAcquisitions_WrongCredentialsRevealNothing()
    {
        await using var factory = new AcquisitionWebFactory();
        using var client = factory.CreateClient();
        Seed(factory.Tracker);

        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/rest/getAcquisitions.view?{Auth("alice", "bad")}"));

        var envelope = doc.RootElement.GetProperty("subsonic-response");
        Assert.Equal("failed", envelope.GetProperty("status").GetString());
        Assert.Equal(40, envelope.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(envelope.TryGetProperty("acquisitions", out _));
        Assert.Equal(1, factory.Navidrome.Pings);
    }

    [Fact]
    public async Task GetOpenSubsonicExtensions_AddsOctoAcquisitionsToNavidromesList()
    {
        await using var factory = new AcquisitionWebFactory();
        using var client = factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/rest/getOpenSubsonicExtensions.view?f=json&v=1.16.1&c=octo-android"));

        var envelope = doc.RootElement.GetProperty("subsonic-response");
        Assert.Equal("ok", envelope.GetProperty("status").GetString());
        Assert.True(envelope.GetProperty("openSubsonic").GetBoolean());
        var extensions = envelope.GetProperty("openSubsonicExtensions").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!,
                e => e.GetProperty("versions").EnumerateArray().Select(v => v.GetInt32()).ToList());
        Assert.Equal([1], extensions["octoAcquisitions"]);
        Assert.Contains("formPost", extensions.Keys);
        Assert.Contains("songLyrics", extensions.Keys);
    }

    [Fact]
    public async Task GetOpenSubsonicExtensions_AddsItInXmlToo()
    {
        await using var factory = new AcquisitionWebFactory();
        using var client = factory.CreateClient();

        var xml = XDocument.Parse(await client.GetStringAsync("/rest/getOpenSubsonicExtensions?v=1.16.1&c=test"));

        XNamespace ns = "http://subsonic.org/restapi";
        var names = xml.Root!.Elements(ns + "openSubsonicExtensions")
            .Select(e => (string?)e.Attribute("name")).ToList();
        Assert.Contains("formPost", names);
        var ours = xml.Root.Elements(ns + "openSubsonicExtensions")
            .Single(e => (string?)e.Attribute("name") == "octoAcquisitions");
        Assert.Equal("1", ours.Element(ns + "versions")?.Value);
    }

    [Fact]
    public async Task AdminAcquisitions_ListsEveryonesRowsWithWhoAsked()
    {
        await using var factory = new AcquisitionWebFactory();
        using var client = factory.CreateClient();
        Seed(factory.Tracker);

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/admin/acquisitions"));

        var rows = doc.RootElement.GetProperty("acquisitions").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        var alice = rows.Single(r => r.GetProperty("id").GetString() == "3kX9Qm");
        Assert.Equal(["alice"], alice.GetProperty("requestedBy").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("soulseek", alice.GetProperty("provider").GetString());
        Assert.Equal("downloading", alice.GetProperty("state").GetString());
    }
}
