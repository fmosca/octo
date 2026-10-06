using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Octo.Models.Domain;
using Octo.Services;
using Octo.Services.Local;
using Octo.Services.Metadata;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// getTopSongs keyed by the spec's artist NAME must not shadow a library artist:
/// the relay runs first, and the catalog answers only when Navidrome returned no
/// rows for that name. Arpeggi's id form is not affected by the precedence.
/// </summary>
[Trait("Host", "Boot")]
public sealed class TopSongsByNameTests
{
    private sealed class UpstreamHandler : HttpMessageHandler
    {
        public int TopSongRelays;
        public string? TopSongArtist;

        /// <summary>When set, Navidrome answers getTopSongs with no song rows:
        /// the library does not know the artist.</summary>
        public bool ReturnNoTopSongs;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (path.Equals("rest/getTopSongs", StringComparison.OrdinalIgnoreCase)
                || path.Equals("rest/getTopSongs.view", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref TopSongRelays);
                TopSongArtist = query["artist"];
                var rows = ReturnNoTopSongs
                    ? """{"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome","topSongs":{"song":[]}}}"""
                    : """{"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome","topSongs":{"song":[{"id":"lib-1","title":"Naima","artist":"John Coltrane","duration":240}]}}}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(rows, Encoding.UTF8, "application/json"),
                });
            }
            if (path.Equals("rest/ping", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome"}}""",
                        Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"subsonic-response":{"status":"ok","version":"1.16.1","type":"navidrome"}}""",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class TopSongsWebFactory : WebApplicationFactory<Program>
    {
        public UpstreamHandler Navidrome { get; } = new();
        public Mock<IMusicMetadataService> Metadata { get; } = new();
        public int TopTracksCalls;
        public List<(string Provider, string ExternalId, int Limit)> TopTracksAsked { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Subsonic:Url"] = "http://navidrome.test",
                    ["Subsonic:AutoDetectDownloadPath"] = "false",
                    ["Library:DownloadPath"] = Path.Combine(Path.GetTempPath(), "octo-topsongs-" + Guid.NewGuid()),
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new FixedFactory(Navidrome));
                services.RemoveAll<IMusicMetadataService>();
                Metadata.Setup(service => service.TopTracksAsync(
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((string provider, string externalId, int limit, CancellationToken _) =>
                    {
                        Interlocked.Increment(ref TopTracksCalls);
                        TopTracksAsked.Add((provider, externalId, limit));
                        return new List<Song>
                        {
                            new() { Id = "cat-1", Artist = "Faith No More", Title = "Epic", Duration = 293 },
                        };
                    });
                // Every other metadata call (search resolve, biography, related) answers empty.
                Metadata.Setup(service => service.SearchSongsByArtistTitleAsync(
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>()))
                    .ReturnsAsync([]);
                // The fallback walks the catalog by name; Moq's default for a List is null,
                // which would throw where an empty answer is what the caller reads.
                Metadata.Setup(service => service.SearchArtistsAsync(
                        It.IsAny<string>(), It.IsAny<int>()))
                    .ReturnsAsync((string query, int limit) =>
                    {
                        // The catalog knows the one artist the test names for the fallback;
                        // anything else is not on the catalog either.
                        return query == "Faith No More"
                            ? new List<Artist>
                            {
                                new() { Name = "Faith No More", ExternalProvider = "soulseek",
                                        ExternalId = "deezer-419", Id = "cat-art-1" },
                            }
                            : new List<Artist>();
                    });
                services.AddSingleton(Metadata.Object);
            });
        }

        private sealed class FixedFactory(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }
    }

    private static string Auth() => "u=admin&t=good&s=salt&v=1.16.1&c=topsongstest";

    [Fact]
    public async Task NameCall_LibraryHoldsTheArtist_ReturnsTheRelayedRows()
    {
        // Arrange: Navidrome answers artist=John Coltrane with one song.
        await using var factory = new TopSongsWebFactory();
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/rest/getTopSongs.view?{Auth()}&artist=John%20Coltrane&count=5&f=json");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var songs = doc.RootElement.GetProperty("subsonic-response")
            .GetProperty("topSongs").GetProperty("song");

        // Assert: Navidrome's row is the answer for the library artist — no catalog call.
        Assert.Equal(1, factory.Navidrome.TopSongRelays);
        Assert.Equal("John Coltrane", factory.Navidrome.TopSongArtist);
        Assert.Equal(0, factory.TopTracksCalls);
        Assert.Equal("Naima", songs.EnumerateArray().First().GetProperty("title").GetString());
    }

    [Fact]
    public async Task NameCall_NavidromeHasNoRows_CatalogKnowsIt_FallsThroughToTheCatalog()
    {
        // Arrange: the library does not know this artist (no rows relayed), and the
        // catalog search resolves the name to an outside artist.
        await using var factory = new TopSongsWebFactory();
        factory.Navidrome.ReturnNoTopSongs = true;
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/rest/getTopSongs.view?{Auth()}&artist=Faith%20No%20More&count=5&f=json");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var envelope = doc.RootElement.GetProperty("subsonic-response");

        // Assert: the relay ran, found nothing, and the catalog filled in.
        Assert.Equal(1, factory.Navidrome.TopSongRelays);
        Assert.Equal(1, factory.TopTracksCalls);
        var asked = Assert.Single(factory.TopTracksAsked);
        Assert.Equal("soulseek", asked.Provider);
        var rows = envelope.GetProperty("topSongs").GetProperty("song");
        Assert.Equal("Epic", rows.EnumerateArray().First().GetProperty("title").GetString());
    }

    [Fact]
    public async Task IdForm_NeverFallsThrough_AswersCatalog()
    {
        // Arrange: Arpeggi's form — a registry id, no name parameter.
        await using var factory = new TopSongsWebFactory();
        factory.Navidrome.ReturnNoTopSongs = true;
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ExternalIdRegistry>();
        var id = registry.Register(new Octo.Services.Soulseek.SoulseekRouting
        {
            Kind = Octo.Services.Soulseek.RoutingKind.Artist, Artist = "Faith No More",
        });

        // Act
        var response = await client.GetAsync($"/rest/getTopSongs.view?{Auth()}&id={id}&count=5&f=json");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var rows = doc.RootElement.GetProperty("subsonic-response")
            .GetProperty("topSongs").GetProperty("song");

        // Assert: the id is octo's own, so the catalog answers regardless of the relay.
        Assert.Equal(0, factory.Navidrome.TopSongRelays);
        Assert.Equal(1, factory.TopTracksCalls);
        Assert.Equal("Epic", rows.EnumerateArray().First().GetProperty("title").GetString());
    }
}