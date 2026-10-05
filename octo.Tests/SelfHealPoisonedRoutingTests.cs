// Verifies the self-poisoned routing repairs itself: a routing whose ExternalArtistId carries
// a registry id (which a past build wrote into the persisted registry) is re-resolved by name
// instead of asking the catalog for a non-numeric artist id. The real
// DeezerMetadataService answers from a fake HTTP surface, so the test sees which URLs are hit.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Metadata;
using Octo.Services.Soulseek;
using Octo.Services.YouTube;

namespace Octo.Tests;

public class SelfHealPoisonedRoutingTests
{
    private sealed class FakeDeezer(Dictionary<string, string> routes) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Calls.Add(url);
            foreach (var (needle, body) in routes)
                if (url.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                    });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private sealed class OneClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        public HttpMessageHandler Handler => handler;
    }

    private const string SearchBody =
        """{"data":[{"id":419,"name":"Faith No More","picture_xl":"http://p/419","nb_fan":1000000}]}""";
    private const string TopBody =
        """{"data":[{"id":9001,"title":"Epic","duration":293,"artist":{"id":419,"name":"Faith No More"},"album":{"id":70,"title":"The Real Thing"}}]}""";

    private static DeezerMetadataService BuildDeezer(FakeDeezer fake) =>
        new(new OneClientFactory(fake),
            new TestOptionsMonitor<MetadataSettings>(new MetadataSettings()),
            NullLogger<DeezerMetadataService>.Instance);

    private static SoulseekMetadataService BuildSoulseek(DeezerMetadataService deezer,
        ExternalIdRegistry registry, FakeDeezer fake)
        => new(new YouTubeResolver(new OneClientFactory(fake),
                new ConfigurationBuilder().Build(),
                NullLogger<YouTubeResolver>.Instance),
            registry, deezer, coverArt: null, NullLogger<SoulseekMetadataService>.Instance);

    [Fact]
    public async Task TopTracks_ARegistryIdAsTheCatalogId_ReResolvesByName()
    {
        // Arrange: a fake catalog that answers the name search and the numeric top call.
        var fake = new FakeDeezer(new Dictionary<string, string>
        {
            ["/artist/419/top"] = TopBody,
            ["/search/artist"] = SearchBody,
        });
        var deezer = BuildDeezer(fake);
        var registry = new ExternalIdRegistry();
        // The routing a poisoned deployment wrote: the routing's own registry id carried
        // as the catalog id.
        var innerId = registry.Register(new SoulseekRouting
        { Kind = RoutingKind.Artist, Artist = "Faith No More" });
        var poisonedId = registry.Register(new SoulseekRouting
        { Kind = RoutingKind.Artist, Artist = "Faith No More", ExternalArtistId = innerId });
        var service = BuildSoulseek(deezer, registry, fake);

        // Act
        var songs = await service.TopTracksAsync(SoulseekMetadataService.ProviderName, poisonedId, 5);

        // Assert: the name search re-resolved the catalog id, and the top call went to it.
        var row = Assert.Single(songs ?? []);
        Assert.Equal("Epic", row.Title);
        Assert.Equal("419", registry.Lookup(poisonedId)!.ExternalArtistId);
        var urls = string.Join(" ", fake.Calls);
        Assert.Contains("/artist/419/top", urls);
        Assert.DoesNotContain("/artist/" + innerId + "/top", urls);
    }

    [Fact]
    public async Task TopTracks_ANumericCatalogId_IsTakenAsIs()
    {
        var fake = new FakeDeezer(new Dictionary<string, string> { ["/artist/419/top"] = TopBody });
        var deezer = BuildDeezer(fake);
        var registry = new ExternalIdRegistry();
        var settled = registry.Register(new SoulseekRouting
        { Kind = RoutingKind.Artist, Artist = "Faith No More", ExternalArtistId = "419" });
        var service = BuildSoulseek(deezer, registry, fake);

        var songs = await service.TopTracksAsync(SoulseekMetadataService.ProviderName, settled, 5);

        Assert.Equal("Epic", Assert.Single(songs ?? []).Title);
        Assert.Equal("419", registry.Lookup(settled)!.ExternalArtistId);
        // A numeric id is trusted: no name search is paid.
        Assert.DoesNotContain("/search/artist", string.Join(" ", fake.Calls));
    }
}