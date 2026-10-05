using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Notifications;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// Downloads side by side were unsafe while a finished file was found by its name anywhere under
/// the music folder. Each Soulseek download now lands in a job folder of its own; these pin down
/// finding it there and nowhere else, the gate that holds transfers to the setting, slskd's
/// one-at-a-time POSTs, and the worker running several requests only once that is proven.
/// </summary>
public class ParallelDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-parallel-" + Guid.NewGuid().ToString("N"));

    public ParallelDownloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string relative, int size)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private const string Remote = @"Music\Artist\Album\03 - Song.flac";
    private const string Job = ".octo-incoming/slskd/aaaa";

    // ---- ResolveInJob ------------------------------------------------------------------------

    [Fact]
    public void TheFileInItsOwnJobFolderIsFound()
    {
        var mine = Write(Path.Combine(Job, "03 - Song.flac"), 1000);
        Assert.Equal(mine, SoulseekDownloadService.ResolveInJob([_root], Job, Remote, 1000, false));
    }

    [Fact]
    public void SlskdsRenamedCopyInsideTheJobFolderIsFound()
    {
        var renamed = Write(Path.Combine(Job, "03 - Song_638631234567890123.flac"), 1000);
        Assert.Equal(renamed, SoulseekDownloadService.ResolveInJob([_root], Job, Remote, 1000, false));
    }

    [Fact]
    public void ACleanedUpNameIsFoundByItsExactSize()
    {
        var cleaned = Write(Path.Combine(Job, "03 _ Song.flac"), 1000);
        Assert.Equal(cleaned, SoulseekDownloadService.ResolveInJob([_root], Job, Remote, 1000, false));
    }

    [Fact]
    public void TheSameFileInAnotherJobOrTheMusicRootIsNeverTaken()
    {
        Write(Path.Combine(".octo-incoming/slskd/bbbb", "03 - Song.flac"), 1000);
        Write(Path.Combine("Music", "Artist", "Album", "03 - Song.flac"), 1000);
        Write(Path.Combine("03 - Song.flac"), 1000);
        Assert.Null(SoulseekDownloadService.ResolveInJob([_root], Job, Remote, 1000, false));
    }

    [Fact]
    public void AnInterruptedTransferNeedsTheExactSize()
    {
        Write(Path.Combine(Job, "03 - Song.flac"), 1000 - 10);
        Assert.NotNull(SoulseekDownloadService.ResolveInJob([_root], Job, Remote, 1000, requireExactSize: false));
        Assert.Null(SoulseekDownloadService.ResolveInJob([_root], Job, Remote, 1000, requireExactSize: true));
    }

    [Fact]
    public void JobFoldersAreDotFoldersAndUnique()
    {
        var a = SoulseekDownloadService.NewJobDir();
        var b = SoulseekDownloadService.NewJobDir();
        Assert.StartsWith(".octo-incoming/slskd/", a);
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("..", a);
    }

    // ---- slskd's batch API -------------------------------------------------------------------

    [Fact]
    public void TheBatchPayloadHasSlskdsShape()
    {
        using var doc = JsonDocument.Parse(SoulseekClient.BatchPayload("peer", [("a\\b.flac", 123)], Job));
        var root = doc.RootElement;
        Assert.True(Guid.TryParse(root.GetProperty("id").GetString(), out _));
        Assert.Equal("peer", root.GetProperty("username").GetString());
        var file = Assert.Single(root.GetProperty("files").EnumerateArray());
        Assert.Equal("a\\b.flac", file.GetProperty("filename").GetString());
        Assert.Equal(123, file.GetProperty("size").GetInt64());
        Assert.Equal(Job, root.GetProperty("options").GetProperty("destination").GetString());
    }

    [Fact]
    public void ABatchAnswerGivesEachFilesTransferIdAndTheFailures()
    {
        var batch = SoulseekClient.ParseBatch("""
            {"batch":{"id":"b","username":"peer","transfers":[
                {"id":"t-1","filename":"a\\1.flac","state":"Queued, Locally"},
                {"id":"t-2","filename":"a\\2.flac","state":"Queued, Locally"}]},
             "failures":[{"filename":"a\\3.flac","message":"File not shared."}]}
            """);
        Assert.True(batch.Supported);
        Assert.Equal("t-1", batch.TransferIds["a\\1.flac"]);
        Assert.Equal("t-2", batch.TransferIds["a\\2.flac"]);
        Assert.Equal(("a\\3.flac", "File not shared."), Assert.Single(batch.Failures));
    }

    /// <summary>Answers slskd's POSTs from a script and records when each one was in flight.</summary>
    private sealed class ScriptedSlskd(Func<HttpRequestMessage, int, HttpResponseMessage> answer) : HttpMessageHandler
    {
        private int _posts;
        private int _inFlight;
        public int MostAtOnce;
        public int Posts => _posts;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v0/session")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"token":"jwt","expires":4102444800}""", Encoding.UTF8, "application/json"),
                };
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref MostAtOnce, now);
            try
            {
                await Task.Delay(30, ct);
                return answer(request, Interlocked.Increment(ref _posts));
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    private static SoulseekClient Client(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new SoulseekClient(factory.Object,
            Options.Create(new SoulseekSettings { BaseUrl = "http://slskd.test", Username = "u", Password = "p" }),
            NullLogger<SoulseekClient>.Instance)
        {
            SearchStartRetryDelay = TimeSpan.FromMilliseconds(1),
            MinSearchSpacing = TimeSpan.Zero,
        };
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string Accepted = """{"batch":{"transfers":[{"id":"t-1","filename":"f.flac"}]},"failures":[]}""";

    [Fact]
    public async Task ParallelEnqueuesNeverOverlapTheirPosts()
    {
        var slskd = new ScriptedSlskd((_, _) => Status(HttpStatusCode.Created, Accepted));
        var client = Client(slskd);

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            client.EnqueueBatchAsync("peer", [("f.flac", 1)], Job)));

        Assert.Equal(1, slskd.MostAtOnce);
        Assert.Equal(3, slskd.Posts);
        Assert.True(client.BatchesSupported);
    }

    [Fact]
    public async Task A429IsSentAgain()
    {
        var slskd = new ScriptedSlskd((_, n) => n <= 2 ? Status(HttpStatusCode.TooManyRequests) : Status(HttpStatusCode.Created, Accepted));
        var batch = await Client(slskd).EnqueueBatchAsync("peer", [("f.flac", 1)], Job);
        Assert.Equal("t-1", batch.TransferIds["f.flac"]);
        Assert.Equal(3, slskd.Posts);
    }

    [Fact]
    public async Task A429ThatNeverClearsFailsThatPeer()
    {
        var slskd = new ScriptedSlskd((_, _) => Status(HttpStatusCode.TooManyRequests));
        var client = Client(slskd);
        client.BatchesSupported = true;
        await Assert.ThrowsAsync<Exception>(() => client.EnqueueBatchAsync("peer", [("f.flac", 1)], Job));
        Assert.Equal(1 + SoulseekClient.OperationRetries, slskd.Posts);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task AnSlskdWithoutBatchesIsAskedOnceThenUsedTheOldWay(HttpStatusCode answer)
    {
        var slskd = new ScriptedSlskd((_, _) => Status(answer));
        var client = Client(slskd);
        Assert.False((await client.EnqueueBatchAsync("peer", [("f.flac", 1)], Job)).Supported);
        Assert.False((await client.EnqueueBatchAsync("peer", [("f.flac", 1)], Job)).Supported);
        Assert.Equal(1, slskd.Posts);
        Assert.False(client.BatchesSupported);
    }

    [Fact]
    public async Task OnceBatchesWorked_ABadRequestIsAnError()
    {
        var slskd = new ScriptedSlskd((_, n) => n == 1 ? Status(HttpStatusCode.Created, Accepted) : Status(HttpStatusCode.BadRequest, "bad"));
        var client = Client(slskd);
        await client.EnqueueBatchAsync("peer", [("f.flac", 1)], Job);
        await Assert.ThrowsAsync<Exception>(() => client.EnqueueBatchAsync("peer", [("f.flac", 1)], Job));
        Assert.True(client.BatchesSupported);
    }

    [Fact]
    public void ATransferIsFollowedByItsIdNotAnOlderOneOfTheSameName()
    {
        using var doc = JsonDocument.Parse("""
            {"username":"peer","directories":[{"directory":"a","files":[
                {"id":"old","filename":"a\\f.flac","state":"Completed, Cancelled"},
                {"id":"new","filename":"a\\f.flac","state":"InProgress"}]}]}
            """);
        Assert.Equal("Completed, Cancelled", SoulseekClient.FindTransferState(doc.RootElement, "a\\f.flac"));
        Assert.Equal("new", SoulseekClient.TransferId(SoulseekClient.FindTransfer(doc.RootElement, "a\\f.flac", "new")!.Value));
        Assert.Null(SoulseekClient.FindTransfer(doc.RootElement, "a\\f.flac", "gone"));
    }

    // ---- The transfer gate -------------------------------------------------------------------

    [Fact]
    public async Task TheGateLetsInItsWidthAndTheNextWaits()
    {
        var limiter = new TransferLimiter(() => 2);
        var a = await limiter.EnterAsync(default);
        var b = await limiter.EnterAsync(default);
        var c = limiter.EnterAsync(default);
        await Task.Delay(20);
        Assert.False(c.IsCompleted);
        a.Dispose();
        (await c.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        b.Dispose();
        Assert.Equal(0, limiter.InUse);
    }

    [Fact]
    public async Task WideningLetsAWaiterInOnTheNextExit_NarrowingDrains()
    {
        var width = 1;
        var limiter = new TransferLimiter(() => width);
        var a = await limiter.EnterAsync(default);
        var b = limiter.EnterAsync(default);
        width = 3;
        var c = await limiter.EnterAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); // room now
        Assert.False(b.IsCompleted);   // queued before the widening; let in by the next exit
        a.Dispose();
        var bSlot = await b.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, limiter.InUse);

        width = 1;
        var d = limiter.EnterAsync(default);
        c.Dispose();
        Assert.False(d.IsCompleted);   // still 1 in use, which is the width
        bSlot.Dispose();
        (await d.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal(0, limiter.InUse);
    }

    [Fact]
    public async Task ACancelledWaiterNeverTakesASlot()
    {
        var limiter = new TransferLimiter(() => 1);
        var held = await limiter.EnterAsync(default);
        using var cts = new CancellationTokenSource();
        var cancelled = limiter.EnterAsync(cts.Token);
        var next = limiter.EnterAsync(default);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        held.Dispose();
        (await next.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal(0, limiter.InUse);
    }

    [Fact]
    public async Task UnderLoadNeverMoreThanTheWidth()
    {
        for (var run = 0; run < 50; run++)
        {
            var limiter = new TransferLimiter(() => 3);
            var inside = 0;
            var most = 0;
            await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
            {
                using (await limiter.EnterAsync(default))
                {
                    var now = Interlocked.Increment(ref inside);
                    lock (limiter) most = Math.Max(most, now);
                    await Task.Delay(Random.Shared.Next(0, 3));
                    Interlocked.Decrement(ref inside);
                }
            }));
            Assert.True(most <= 3, $"run {run}: {most} at once");
            Assert.Equal(0, limiter.InUse);
        }
    }

    [Fact]
    public void ConcurrencyIsOneUntilProvenAndOneAgainOnceRefused()
    {
        var concurrency = new DownloadConcurrency(TestOptions.Monitor(new SoulseekSettings { ParallelDownloads = 4 }));
        Assert.Equal(1, concurrency.Current);
        Assert.Contains("until slskd", concurrency.Why);
        concurrency.Prove();
        Assert.Equal(4, concurrency.Current);
        concurrency.Refuse("slskd put a download outside the folder Octo asked for");
        Assert.Equal(1, concurrency.Current);
        concurrency.Prove();
        Assert.Equal(1, concurrency.Current);
        Assert.StartsWith("One at a time: slskd put", concurrency.Why);
    }

    [Fact]
    public void TheSettingIsClamped()
    {
        Assert.Equal(6, new SoulseekSettings { ParallelDownloads = 50 }.EffectiveParallelDownloads);
        Assert.Equal(1, new SoulseekSettings { ParallelDownloads = 0 }.EffectiveParallelDownloads);
        Assert.Equal(3, new SoulseekSettings().ParallelDownloads);
    }

    // ---- The worker ----------------------------------------------------------------------------

    private static (AcquisitionWorker Worker, TrackAcquisitionQueue Queue, Func<int> MostAtOnce, Func<int> Started)
        Worker(int width, TaskCompletionSource release)
    {
        var queue = new TrackAcquisitionQueue(NullLogger<TrackAcquisitionQueue>.Instance);
        var inside = 0;
        var most = 0;
        var started = 0;
        var downloads = new Mock<IDownloadService>();
        downloads.Setup(d => d.ExecuteAcquisitionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<DownloadSource?>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<bool>(), It.IsAny<Octo.Services.Library.ReplacementHandoff?>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref started);
                var now = Interlocked.Increment(ref inside);
                lock (downloads) most = Math.Max(most, now);
                await release.Task;
                Interlocked.Decrement(ref inside);
                return "/music/x.flac";
            });
        var concurrency = new DownloadConcurrency(TestOptions.Monitor(new SoulseekSettings { ParallelDownloads = width }));
        concurrency.Prove();
        var worker = new AcquisitionWorker(queue, downloads.Object, new ExternalIdRegistry(),
            new NotificationService([], TestOptions.Monitor(new NotificationSettings()), NullLogger<NotificationService>.Instance),
            NullLogger<AcquisitionWorker>.Instance, concurrency);
        return (worker, queue, () => Volatile.Read(ref most), () => Volatile.Read(ref started));
    }

    [Fact]
    public async Task TheWorkerRunsUpToTheWidthAndTheRestWait()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (worker, queue, most, started) = Worker(3, release);
        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        var done = Enumerable.Range(0, 5).Select(i => queue.Enqueue("soulseek", $"song-{i}", isStar: true,
            triggerAlbumDownload: false, forcePermanent: true)).ToList();

        await WaitUntil(() => started() == 3);
        await Task.Delay(50);
        Assert.Equal(3, started());
        release.SetResult();
        await Task.WhenAll(done).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, most());
        await worker.StopAsync(default);
    }

    [Fact]
    public async Task AtWidthOneTheWorkerIsStrictlyOneAtATime()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (worker, queue, most, started) = Worker(1, release);
        await worker.StartAsync(default);
        var done = Enumerable.Range(0, 3).Select(i => queue.Enqueue("soulseek", $"song-{i}", isStar: true,
            triggerAlbumDownload: false, forcePermanent: true)).ToList();
        await WaitUntil(() => started() == 1);
        await Task.Delay(50);
        Assert.Equal(1, started());
        release.SetResult();
        await Task.WhenAll(done).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, most());
        await worker.StopAsync(default);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(5);
        }
    }
}
