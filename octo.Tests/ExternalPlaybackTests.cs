using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Local;

namespace Octo.Tests;

public sealed class ExternalPlaybackTests
{
    [Fact]
    public async Task ExternalPlaybackUsesYouTubeRegardlessOfLegacyStorageMode()
    {
        var downloads = new Mock<IDownloadService>();
        downloads.Setup(service => service.GetDirectStreamAsync(
                "soulseek", "track-id", null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectStreamInfo
            {
                AudioStream = new MemoryStream([1, 2, 3]),
                ContentType = "audio/mp4",
                ContentLength = 3,
                StatusCode = 200,
            });
        var library = new Mock<ILocalLibraryService>();
        library.Setup(service => service.ParseSongId("external-track"))
            .Returns((true, "soulseek", "track-id"));

        await using var factory = CreateFactory(downloads, library, waitForLossless: false);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/rest/stream?id=external-track&f=json&u=alice&t=good&s=salt&v=1.16.1&c=test");

        response.EnsureSuccessStatusCode();
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync());
        downloads.Verify(service => service.GetDirectStreamAsync(
            "soulseek", "track-id", null,
            It.IsAny<CancellationToken>()), Times.Once);
        downloads.Verify(service => service.DownloadAndStreamAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WaitForLosslessStillAcquiresBeforePlaybackWhenEnabled()
    {
        var downloads = new Mock<IDownloadService>();
        var library = new Mock<ILocalLibraryService>();
        library.Setup(service => service.ParseSongId("external-track"))
            .Returns((true, "soulseek", "track-id"));

        await using var factory = CreateFactory(downloads, library, waitForLossless: true);
        using var client = factory.CreateClient();
        var responseTask = client.GetAsync("/rest/stream?id=external-track&f=json&u=alice&t=good&s=salt&v=1.16.1&c=test");

        var queue = factory.Services.GetRequiredService<TrackAcquisitionQueue>();
        // Only a guard against hanging: under a full-suite load the first request through a
        // fresh host, sign-in check included, has taken over 5 seconds to reach the queue.
        using var dequeueTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = await queue.DequeueAsync(dequeueTimeout.Token);
        Assert.NotNull(request);
        Assert.False(request.IsStar);
        Assert.False(request.TriggerAlbumDownload);
        Assert.True(request.ForcePermanent);

        var path = Path.Combine(Path.GetTempPath(), $"octo-playback-{Guid.NewGuid():N}.flac");
        try
        {
            await File.WriteAllBytesAsync(path, [4, 5, 6]);
            request.Completion.TrySetResult(path);
            queue.Release(request);

            using var response = await responseTask;
            response.EnsureSuccessStatusCode();
            Assert.Equal([4, 5, 6], await response.Content.ReadAsByteArrayAsync());
            downloads.Verify(service => service.GetDirectStreamAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("bytes=0-", 1)]
    [InlineData("bytes=4096-", 0)]
    public async Task StreamingAnOutsideSongQueuesItsDownloadOnlyFromTheFirstByte(string? range, int waiting)
    {
        var downloads = new Mock<IDownloadService>();
        downloads.Setup(s => s.GetDirectStreamAsync("soulseek", "track-id", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DirectStreamInfo { AudioStream = new MemoryStream([1, 2, 3]),
                ContentType = "audio/mp4", ContentLength = 3, StatusCode = 200 });
        var library = new Mock<ILocalLibraryService>();
        library.Setup(s => s.ParseSongId("external-track")).Returns((true, "soulseek", "track-id"));
        await using var factory = CreateFactory(downloads, library, waitForLossless: false, downloadOnPlay: true);
        using var client = factory.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Get,
            "/rest/stream?id=external-track&f=json&u=alice&t=good&s=salt&v=1.16.1&c=test");
        if (range is not null) message.Headers.TryAddWithoutValidation("Range", range);

        using var response = await client.SendAsync(message);

        response.EnsureSuccessStatusCode();
        Assert.Equal(waiting, factory.Services.GetRequiredService<TrackAcquisitionQueue>().WaitingPlays);
    }

    [Theory]
    [InlineData("GET", null, true)]
    [InlineData("GET", "", true)]
    [InlineData("GET", "bytes=0-", true)]
    [InlineData("GET", "bytes=0-1", true)]
    [InlineData("GET", "bytes=10-20", false)]
    [InlineData("HEAD", null, false)]
    public void OnlyARequestFromTheFirstByteIsAPlay(string method, string? range, bool play) =>
        Assert.Equal(play, Octo.Controllers.SubsonicController.IsFirstByteRequest(method, range));

    /// <summary>Navidrome accepting every sign-in: these tests are about what plays, not who.</summary>
    private sealed class PingOk : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"subsonic-response":{"status":"ok","version":"1.16.1"}}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Mock<IDownloadService> downloads,
        Mock<ILocalLibraryService> library,
        bool waitForLossless,
        bool downloadOnPlay = false)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Subsonic:Url"] = "http://navidrome.invalid",
                        ["Subsonic:StorageMode"] = "Cache",
                        ["Subsonic:DownloadSource"] = "Soulseek",
                        ["Subsonic:WaitForLosslessOnPlay"] = waitForLossless.ToString(),
                        ["Subsonic:DownloadOnPlay"] = downloadOnPlay.ToString(),
                        ["Library:DownloadPath"] = Path.GetTempPath(),
                    }));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    // An outside song is played only for a sign-in Navidrome accepts.
                    services.RemoveAll<IHttpClientFactory>();
                    services.AddSingleton<IHttpClientFactory>(new ReviewFixtures.OneClientFactory(new PingOk()));
                    services.RemoveAll<IDownloadService>();
                    services.RemoveAll<ILocalLibraryService>();
                    services.AddSingleton(downloads.Object);
                    services.AddSingleton(library.Object);
                });
            });
    }
}
