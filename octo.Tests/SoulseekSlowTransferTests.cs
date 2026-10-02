using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Settings;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// A slow peer with the right file is waited for as long as it keeps sending. A peer
/// that sends nothing for the whole window is given up on, and its transfer is
/// cancelled in slskd: left running, it finished minutes later and put a second copy
/// of the song in the library.
/// </summary>
public class SoulseekSlowTransferTests
{
    private const string Peer = "peer";
    private const string RemoteFile = @"Music\Artist\Album\13 - Song.flac";

    [Fact]
    public void AWatchKeepsWaitingWhileBytesArrive()
    {
        var t0 = DateTime.UtcNow;
        var watch = new TransferWatch(t0, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(60));
        for (var s = 5; s <= 300; s += 5) watch.Saw(s * 1000L, t0.AddSeconds(s));
        Assert.False(watch.Expired(t0.AddSeconds(305)));
        Assert.True(watch.Expired(t0.AddSeconds(310)));
    }

    [Fact]
    public void AWatchGivesUpWhenNothingNewArrives()
    {
        var t0 = DateTime.UtcNow;
        var watch = new TransferWatch(t0, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(60));
        watch.Saw(0, t0.AddSeconds(3));
        watch.Saw(500, t0.AddSeconds(4));
        // The same count again is not progress.
        watch.Saw(500, t0.AddSeconds(9));
        watch.Saw(null, t0.AddSeconds(12));
        Assert.False(watch.Expired(t0.AddSeconds(13)));
        Assert.True(watch.Expired(t0.AddSeconds(14)));
    }

    [Fact]
    public void AWatchStopsAtTheCeilingEvenWhileMoving()
    {
        var t0 = DateTime.UtcNow;
        var watch = new TransferWatch(t0, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        watch.Saw(1, t0.AddSeconds(59));
        Assert.False(watch.Expired(t0.AddSeconds(59)));
        Assert.True(watch.Expired(t0.AddSeconds(60)));
        Assert.True(watch.HitCeiling(t0.AddSeconds(60)));
    }

    [Fact]
    public async Task ASlowPeerThatKeepsSendingIsWaitedForPastTheWindow()
    {
        // Each poll is 0.5 s apart and brings more bytes; it finishes after 20 s, far
        // past the 1 s window.
        var slskd = new FakeSlskd(poll =>
            poll < 40 ? ("InProgress", poll * 10_000L) : ("Completed, Succeeded", 400_000L));

        var state = await Client(slskd).WaitForCompletionAsync(Peer, RemoteFile, perAttemptTimeoutSeconds: 1);

        Assert.Equal(SoulseekTransferState.Succeeded, state);
        Assert.True(slskd.Now - Start > TimeSpan.FromSeconds(1), "finished inside the window, so it proves nothing");
        Assert.Empty(slskd.Deletes);
    }

    [Fact]
    public async Task APeerThatSendsNothingIsCancelledInSlskd()
    {
        var slskd = new FakeSlskd(_ => ("Queued, Remotely", 0L));

        var state = await Client(slskd).WaitForCompletionAsync(Peer, RemoteFile, perAttemptTimeoutSeconds: 1);

        Assert.Equal(SoulseekTransferState.Errored, state);
        var delete = Assert.Single(slskd.Deletes);
        Assert.Equal("/api/v0/transfers/downloads/peer/transfer-7", delete.AbsolutePath);
        Assert.Equal("?remove=true", delete.Query);
    }

    [Fact]
    public async Task ATransferThatFinishedAsItWasGivenUpIsKeptNotCancelled()
    {
        var slskd = new FakeSlskd(_ => ("Completed, Succeeded", 400_000L));

        var state = await Client(slskd).CancelTransferAsync(Peer, RemoteFile);

        Assert.Equal(SoulseekTransferState.Succeeded, state);
        Assert.Empty(slskd.Deletes);
    }

    [Fact]
    public async Task ATransferSlskdNoLongerListsHasNothingToCancel()
    {
        var slskd = new FakeSlskd(_ => ("Queued, Remotely", 0L));

        var state = await Client(slskd).CancelTransferAsync(Peer, @"Music\Someone\Else.flac");

        Assert.Equal(SoulseekTransferState.Errored, state);
        Assert.Empty(slskd.Deletes);
    }

    private static SoulseekClient Client(FakeSlskd slskd)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(slskd));
        var settings = Options.Create(new SoulseekSettings
        {
            BaseUrl = "http://slskd.test:5030",
            Username = "octo",
            Password = "secret",
        });
        return new SoulseekClient(factory.Object, settings, NullLogger<SoulseekClient>.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            Clock = () => slskd.Now,
        };
    }

    // slskd as far as the wait sees it: a session, the user's downloads with one
    // transfer whose state each poll decides, and cancels.
    private static readonly DateTime Start = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    // slskd's clock moves half a second with every poll, so the wait never depends
    // on how busy the machine running the tests is.
    private sealed class FakeSlskd(Func<int, (string State, long Bytes)> onPoll) : HttpMessageHandler
    {
        private int _polls;

        public DateTime Now { get; private set; } = Start;

        public List<Uri> Deletes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v0/session")
                return Json($$"""{"token":"jwt","expires":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}""");
            if (request.Method == HttpMethod.Delete)
            {
                Deletes.Add(request.RequestUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            Now += TimeSpan.FromMilliseconds(500);
            var (state, bytes) = onPoll(_polls++);
            var file = RemoteFile.Replace(@"\", @"\\");
            return Json($$"""
                {"username":"{{Peer}}","directories":[{"directory":"x","files":[
                  {"id":"transfer-7","filename":"{{file}}","state":"{{state}}","size":400000,"bytesTransferred":{{bytes}}}
                ]}]}
                """);
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
