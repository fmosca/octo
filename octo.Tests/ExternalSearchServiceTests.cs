using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.LastFm;

namespace Octo.Tests;

public class ExternalSearchServiceTests
{
    [Theory]
    [InlineData(50, 0)]
    [InlineData(5, 1)]
    public async Task PadsWithTopTracksOnlyWhenTrackSearchIsThin(int searchHits, int expectedTopTrackCalls)
    {
        var calls = new List<string>();
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
            {
                var url = request.RequestUri!.ToString();
                calls.Add(url);
                var tracks = string.Join(",", Enumerable.Range(1, searchHits)
                    .Select(i => $$"""{"name":"Song {{i}}","artist":"Artist"}"""));
                var body = url.Contains("method=track.search")
                    ? """{"results":{"trackmatches":{"track":[""" + tracks + "]}}}"
                    : """{"toptracks":{"track":[]}}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            });
        var lastFm = new LastFmService(new HttpClient(handler.Object),
            TestOptions.Monitor(new LastFmSettings { ApiKey = "key" }),
            Options.Create(new MetadataSettings()),
            NullLogger<LastFmService>.Instance);
        var search = new ExternalSearchService(new Mock<IMusicMetadataService>().Object,
            NullLogger<ExternalSearchService>.Instance, lastFm);

        await search.GetAsync("artist");

        Assert.Equal(expectedTopTrackCalls, calls.Count(url => url.Contains("method=artist.gettoptracks")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchWaitsForYouTubeDurationsOnlyWhenConfigured(bool waitForDurations)
    {
        var durations = new TaskCompletionSource();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(m => m.SearchSongsByArtistTitleAsync("Artist", "Song", 1, null))
            .ReturnsAsync([new Song { Artist = "Artist", Title = "Song" }]);
        metadata.Setup(m => m.ResolveTopDurationsAsync(It.IsAny<List<Song>>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback((List<Song> _, CancellationToken _, bool _, bool background) => started.TrySetResult(background))
            .Returns(durations.Task);
        var search = new ExternalSearchService(metadata.Object, NullLogger<ExternalSearchService>.Instance,
            OneHitLastFm(), TestOptions.Monitor(new SubsonicSettings { WaitForSearchDurations = waitForDurations }));

        var result = search.GetAsync("song");
        var background = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The durations pass is still running, so only a search that does not wait can be done.
        Assert.Equal(!waitForDurations, background);
        if (waitForDurations) Assert.False(result.IsCompleted);
        else Assert.Single(await result.WaitAsync(TimeSpan.FromSeconds(5)));
        durations.SetResult();
        Assert.Single(await result.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task BackgroundDurationsRunBeforeTheVideoPrewarm()
    {
        var durations = new TaskCompletionSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prewarmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new Mock<IMusicMetadataService>();
        metadata.Setup(m => m.SearchSongsByArtistTitleAsync("Artist", "Song", 1, null))
            .ReturnsAsync([new Song { Artist = "Artist", Title = "Song" }]);
        metadata.Setup(m => m.ResolveTopDurationsAsync(It.IsAny<List<Song>>(), It.IsAny<CancellationToken>(), false, true))
            .Callback(() => started.TrySetResult()).Returns(durations.Task);
        metadata.Setup(m => m.PrewarmYouTubeIdsAsync(It.IsAny<IEnumerable<Song>>(), 12, It.IsAny<CancellationToken>()))
            .Callback(() => prewarmed.TrySetResult()).Returns(Task.CompletedTask);
        var search = new ExternalSearchService(metadata.Object, NullLogger<ExternalSearchService>.Instance,
            OneHitLastFm(), TestOptions.Monitor(new SubsonicSettings { WaitForSearchDurations = false }));

        await search.GetAsync("song").WaitAsync(TimeSpan.FromSeconds(5));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(prewarmed.Task.IsCompleted);

        durations.SetResult();
        await prewarmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static LastFmService OneHitLastFm()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":{"trackmatches":{"track":[{"name":"Song","artist":"Artist"}]}}}"""),
            });
        return new LastFmService(new HttpClient(handler.Object),
            TestOptions.Monitor(new LastFmSettings { ApiKey = "key" }),
            Options.Create(new MetadataSettings()),
            NullLogger<LastFmService>.Instance);
    }
}
