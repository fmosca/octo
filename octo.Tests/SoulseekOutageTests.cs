using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Lidarr;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// slskd can answer every call while it is not logged in to Soulseek, as it did through Soulseek's
/// maintenance on 2026-10-03, when every search failed and a hearted album landed as YouTube MP3s.
/// These pin down reading that state, the dashboard's words for it, and hearts waiting it out.
/// </summary>
public class SoulseekOutageTests
{
    private const string LoggedInJson = """
        {"version":{"current":"0.26.0"},
         "server":{"address":"server.slsknet.org","ipEndPoint":"208.76.170.59:2271","state":"Connected, LoggedIn",
                   "isConnected":true,"isConnecting":false,"isLoggedIn":true,"isLoggingIn":false,"isTransitioning":false},
         "user":{"username":"winters27"},
         "connectionWatchdog":{"isEnabled":true,"isAttemptingConnection":false}}
        """;

    // Disconnected: slskd leaves address and ipEndPoint out entirely.
    private const string DisconnectingJson = """
        {"server":{"state":"Disconnecting","isConnected":false,"isLoggedIn":false,"isTransitioning":true},
         "user":{"username":"winters27"},
         "connectionWatchdog":{"isEnabled":true,"isAttemptingConnection":true,"nextAttemptAt":"2026-10-03T05:48:13Z"}}
        """;

    [Fact]
    public void ALoggedInSlskdReadsAsLoggedIn()
    {
        var reading = SoulseekClient.ParseServerReading(LoggedInJson);
        Assert.Equal(SoulseekLinkState.LoggedIn, reading.Link);
        Assert.Equal("Connected, LoggedIn", reading.State);
        Assert.Equal("winters27", reading.Username);
    }

    [Fact]
    public void ADisconnectingSlskdReadsAsNotLoggedIn_WithItsNextTry()
    {
        var reading = SoulseekClient.ParseServerReading(DisconnectingJson);
        Assert.Equal(SoulseekLinkState.NotLoggedIn, reading.Link);
        Assert.Equal("Disconnecting", reading.State);
        Assert.Equal(new DateTime(2026, 10, 3, 5, 48, 13, DateTimeKind.Utc), reading.NextAttemptUtc);
        Assert.Equal(DateTimeKind.Utc, reading.NextAttemptUtc!.Value.Kind);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"server":{"state":"Connected"}}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void AShapeWithoutTheFlagsIsUnknown(string json) =>
        Assert.Equal(SoulseekLinkState.Unknown, SoulseekClient.ParseServerReading(json).Link);

    [Fact]
    public void TheDashboardWarnsWhenSlskdIsUpButNotLoggedIn()
    {
        var reading = SoulseekClient.ParseServerReading(DisconnectingJson);
        var (ok, warning, detail) = SoulseekLink.Describe(reading, 6);
        Assert.True(ok);
        Assert.True(warning);
        Assert.Contains("Not connected to Soulseek", detail);
        Assert.Contains("slskd says Disconnecting", detail);
        Assert.Contains("6 hours", detail);
        Assert.Contains("05:48 UTC", detail);

        Assert.Contains("Downloads use the next source.", SoulseekLink.Describe(reading, 0).Detail);
        Assert.Contains("1 hour for it", SoulseekLink.Describe(reading, 1).Detail);
    }

    [Fact]
    public void TheDashboardLinesForTheOtherStates()
    {
        Assert.Equal((false, false, "unreachable / auth failed"), SoulseekLink.Describe(null, 6));
        Assert.Equal((true, false, "logged in to Soulseek as winters27"),
            SoulseekLink.Describe(SoulseekClient.ParseServerReading(LoggedInJson), 6));
        Assert.Equal((true, false, "reachable"),
            SoulseekLink.Describe(new SoulseekServerReading(SoulseekLinkState.Unknown, null, null, null), 6));
    }

    [Fact]
    public void NoDashLikeCharactersInTheWords()
    {
        var detail = SoulseekLink.Describe(SoulseekClient.ParseServerReading(DisconnectingJson), 6).Detail;
        Assert.DoesNotContain("—", detail);
        Assert.DoesNotContain("–", detail);
        Assert.DoesNotContain(" - ", detail);
        Assert.DoesNotContain("—", SoulseekLink.OfflineText);
    }

    // ---- SoulseekLink: the wait and the cache -------------------------------------------------

    private static (SoulseekLink Link, List<TimeSpan> Delays) ScriptedLink(params SoulseekLinkState?[] script)
    {
        var now = new DateTime(2026, 10, 3, 3, 0, 0, DateTimeKind.Utc);
        var delays = new List<TimeSpan>();
        var reads = 0;
        var link = new SoulseekLink(null!, TestOptions.Monitor(new SoulseekSettings()), NullLogger<SoulseekLink>.Instance);
        link.Read = _ =>
        {
            var state = script[Math.Min(reads++, script.Length - 1)];
            return Task.FromResult(state is { } s ? new SoulseekServerReading(s, s.ToString(), null, null) : null);
        };
        link.Clock = () => now;
        link.Delay = (span, _) => { delays.Add(span); now += span; return Task.CompletedTask; };
        return (link, delays);
    }

    [Fact]
    public async Task TheWaitEndsWhenSlskdLogsBackIn()
    {
        var (link, delays) = ScriptedLink(SoulseekLinkState.NotLoggedIn, SoulseekLinkState.NotLoggedIn, SoulseekLinkState.LoggedIn);
        var back = await link.WaitForLoginAsync(link.UtcNow.AddHours(6), default);
        Assert.True(back);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)], delays);
    }

    [Fact]
    public async Task TheWaitRunsOutAtTheDeadline()
    {
        var (link, delays) = ScriptedLink(SoulseekLinkState.NotLoggedIn);
        var back = await link.WaitForLoginAsync(link.UtcNow.AddSeconds(70), default);
        Assert.False(back);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)], delays);
    }

    [Fact]
    public async Task AnSlskdThatDoesNotAnswerIsNeverWaitedFor()
    {
        var (link, delays) = ScriptedLink([null]);
        Assert.True(await link.WaitForLoginAsync(link.UtcNow.AddHours(6), default));
        Assert.Empty(delays);
    }

    [Fact]
    public async Task ReadsWithinTenSecondsShareOneRequest()
    {
        var now = DateTime.UtcNow;
        var calls = 0;
        var link = new SoulseekLink(null!, TestOptions.Monitor(new SoulseekSettings()), NullLogger<SoulseekLink>.Instance)
        {
            Read = _ => { calls++; return Task.FromResult<SoulseekServerReading?>(new(SoulseekLinkState.LoggedIn, null, null, null)); },
            Clock = () => now,
        };
        await link.ReadAsync(fresh: false, default);
        now += TimeSpan.FromSeconds(9);
        await link.ReadAsync(fresh: false, default);
        Assert.Equal(1, calls);
        await link.ReadAsync(fresh: true, default);
        Assert.Equal(2, calls);
        now += TimeSpan.FromSeconds(11);
        await link.ReadAsync(fresh: false, default);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void TheHoldLimitIsClampedAndLive()
    {
        var settings = TestOptions.Monitor(new SoulseekSettings { OutageHoldHours = 99 });
        var link = new SoulseekLink(null!, settings, NullLogger<SoulseekLink>.Instance);
        Assert.Equal(TimeSpan.FromHours(48), link.HoldLimit);
        settings.Set(new SoulseekSettings { OutageHoldHours = 0 });
        Assert.Equal(TimeSpan.Zero, link.HoldLimit);
        Assert.Equal(6, new SoulseekSettings().OutageHoldHours);
    }

    // ---- The heart chain ----------------------------------------------------------------------

    /// <summary>A link the test drives: its state, its clock, and when a wait has begun.</summary>
    private sealed class FakeLink : ISoulseekLink
    {
        private readonly object _lock = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SoulseekLinkState? State { get; private set; } = SoulseekLinkState.LoggedIn;
        public TimeSpan HoldLimit { get; set; } = TimeSpan.FromHours(6);
        public DateTime UtcNow { get; private set; } = new(2026, 10, 3, 3, 0, 0, DateTimeKind.Utc);
        public int Reads;
        public TaskCompletionSource Waiting { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SoulseekServerReading?> ReadAsync(bool fresh, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(State is { } s
                ? new SoulseekServerReading(s, s == SoulseekLinkState.NotLoggedIn ? "Disconnecting" : "Connected, LoggedIn", "u", null)
                : null);
        }

        public async Task<bool> WaitForLoginAsync(DateTime deadlineUtc, CancellationToken ct)
        {
            while (true)
            {
                Task changed;
                lock (_lock)
                {
                    if (State != SoulseekLinkState.NotLoggedIn) return true;
                    if (UtcNow >= deadlineUtc) return false;
                    changed = _changed.Task;
                    Waiting.TrySetResult();
                }
                await changed;
            }
        }

        public void Set(SoulseekLinkState? state) { lock (_lock) State = state; Pulse(); }
        public void Advance(TimeSpan by) { lock (_lock) UtcNow += by; Pulse(); }
        public void ExpectAnotherWait() => Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Pulse()
        {
            TaskCompletionSource old;
            lock (_lock) { old = _changed; _changed = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            old.TrySetResult();
        }
    }

    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    private static SubsonicSettings SoulseekThenYouTube() => new()
    {
        HeartDownloadSources =
        [
            new() { Source = HeartDownloadSource.Soulseek, Enabled = true },
            new() { Source = HeartDownloadSource.YouTube, Enabled = true },
            new() { Source = HeartDownloadSource.Lidarr, Enabled = false },
        ],
    };

    private static (HeartAcquisitionCoordinator Hearts, TrackAcquisitionQueue Queue, Mock<IDownloadService> Direct)
        Chain(FakeLink link, SoulseekHoldStore? holds = null, SubsonicSettings? settings = null)
    {
        var queue = new TrackAcquisitionQueue(new Mock<ILogger<TrackAcquisitionQueue>>().Object);
        var direct = new Mock<IDownloadService>();
        var hearts = new HeartAcquisitionCoordinator(
            TestOptions.Monitor(settings ?? SoulseekThenYouTube()), queue, direct.Object,
            new Mock<ILidarrHeartAcquisitionService>().Object,
            new Mock<ILogger<HeartAcquisitionCoordinator>>().Object,
            soulseek: link, holds: holds ?? new SoulseekHoldStore());
        return (hearts, queue, direct);
    }

    private static async Task<AcquisitionRequest> Next(TrackAcquisitionQueue queue)
    {
        using var timeout = new CancellationTokenSource(Soon);
        return (await queue.DequeueAsync(timeout.Token))!;
    }

    private static void Fail(TrackAcquisitionQueue queue, AcquisitionRequest request, string why = "no peer")
    {
        queue.Release(request);
        request.Completion.TrySetException(new FileNotFoundException(why));
    }

    private static void Land(TrackAcquisitionQueue queue, AcquisitionRequest request)
    {
        request.Completion.TrySetResult("/music/song.flac");
        queue.Release(request);
    }

    [Fact]
    public async Task AHeartDuringAnOutageWaitsForSoulseekAndNeverTouchesYouTube()
    {
        var link = new FakeLink();
        link.Set(SoulseekLinkState.NotLoggedIn);
        var holds = new SoulseekHoldStore();
        var (hearts, queue, _) = Chain(link, holds);

        var heart = hearts.AcquireTrackAsync("soulseek", "song-1", "winters");
        await link.Waiting.Task.WaitAsync(Soon);

        Assert.True(queue.IsIdle);
        var held = Assert.Single(holds.Snapshot());
        Assert.Equal(HeldKind.Track, held.Kind);
        Assert.Equal("winters", held.RequestedBy);
        Assert.Equal(link.UtcNow, held.HeldSinceUtc);

        link.Set(SoulseekLinkState.LoggedIn);
        var request = await Next(queue);
        Assert.Equal(DownloadSource.Soulseek, request.SourceOverride);
        Land(queue, request);
        await heart.WaitAsync(Soon);

        Assert.True(queue.IsIdle);
        Assert.Empty(holds.Snapshot());
    }

    [Fact]
    public async Task AfterTheWaitRunsOut_SoulseekIsTriedOnceThenYouTube()
    {
        var link = new FakeLink();
        link.Set(SoulseekLinkState.NotLoggedIn);
        var (hearts, queue, _) = Chain(link);

        var heart = hearts.AcquireTrackAsync("soulseek", "song-1");
        await link.Waiting.Task.WaitAsync(Soon);
        link.Advance(TimeSpan.FromHours(6));

        var soulseek = await Next(queue);
        Assert.Equal(DownloadSource.Soulseek, soulseek.SourceOverride);
        Fail(queue, soulseek);

        var youTube = await Next(queue);
        Assert.Equal(DownloadSource.YouTube, youTube.SourceOverride);
        Land(queue, youTube);
        await heart.WaitAsync(Soon);
    }

    [Fact]
    public async Task ASoulseekFailureWhileLoggedIn_GoesStraightToYouTube()
    {
        var link = new FakeLink();
        var (hearts, queue, _) = Chain(link);

        var heart = hearts.AcquireTrackAsync("soulseek", "song-1");
        Fail(queue, await Next(queue));
        var youTube = await Next(queue);
        Assert.Equal(DownloadSource.YouTube, youTube.SourceOverride);
        Land(queue, youTube);
        await heart.WaitAsync(Soon);
        Assert.False(link.Waiting.Task.IsCompleted);
    }

    [Fact]
    public async Task WhenSlskdDropsMidDownload_TheHeartWaitsAndTriesSoulseekAgain()
    {
        var link = new FakeLink();
        var (hearts, queue, _) = Chain(link);

        var heart = hearts.AcquireTrackAsync("soulseek", "song-1");
        var first = await Next(queue);
        link.Set(SoulseekLinkState.NotLoggedIn);
        Fail(queue, first, "search start failed");
        await link.Waiting.Task.WaitAsync(Soon);

        link.Set(SoulseekLinkState.LoggedIn);
        var again = await Next(queue);
        Assert.Equal(DownloadSource.Soulseek, again.SourceOverride);
        Land(queue, again);
        await heart.WaitAsync(Soon);
    }

    [Fact]
    public async Task AnAlbumWalkCutShortByADrop_IsWalkedAgainOnSoulseekNotYouTube()
    {
        var link = new FakeLink();
        var (hearts, _, direct) = Chain(link);
        var walks = 0;
        direct.Setup(d => d.DownloadAlbumWithSourceAsync("soulseek", "album-1", DownloadSource.Soulseek,
                It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(() =>
            {
                if (++walks == 1) { link.Set(SoulseekLinkState.NotLoggedIn); return false; }
                return true;
            });

        var heart = hearts.AcquireAlbumAsync("soulseek", "album-1");
        await link.Waiting.Task.WaitAsync(Soon);
        link.Set(SoulseekLinkState.LoggedIn);
        await heart.WaitAsync(Soon);

        Assert.Equal(2, walks);
        direct.Verify(d => d.DownloadAlbumWithSourceAsync("soulseek", "album-1", DownloadSource.YouTube,
            It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()), Times.Never);
    }

    [Fact]
    public async Task WithTheWaitOff_NothingIsReadAndTheChainIsAsBefore()
    {
        var link = new FakeLink { HoldLimit = TimeSpan.Zero };
        link.Set(SoulseekLinkState.NotLoggedIn);
        var (hearts, queue, _) = Chain(link);

        var heart = hearts.AcquireTrackAsync("soulseek", "song-1");
        Fail(queue, await Next(queue));
        Land(queue, await Next(queue));
        await heart.WaitAsync(Soon);
        Assert.Equal(0, link.Reads);
    }

    [Fact]
    public async Task AResumedHeartKeepsItsOriginalDeadline()
    {
        var link = new FakeLink();
        link.Set(SoulseekLinkState.NotLoggedIn);
        var holds = new SoulseekHoldStore();
        var saved = new HeldAcquisition(HeldKind.Track, "soulseek", "song-1", "winters",
            link.UtcNow - TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        holds.Hold(saved);
        var (hearts, queue, _) = Chain(link, holds);

        var resumed = hearts.ResumeAsync(saved,
            () => hearts.AcquireTrackAsync(saved.Provider, saved.ExternalId, saved.RequestedBy, saved.HeldSinceUtc));
        await link.Waiting.Task.WaitAsync(Soon);
        link.Advance(TimeSpan.FromMinutes(1));

        Fail(queue, await Next(queue));
        Land(queue, await Next(queue));
        await resumed.WaitAsync(Soon);
        Assert.Empty(holds.Snapshot());
    }

    [Fact]
    public void TheHoldStoreKeepsTheFirstStartAndSurvivesARestart()
    {
        var dir = Directory.CreateTempSubdirectory("octo-holds-");
        try
        {
            var path = Path.Combine(dir.FullName, "soulseek-holds.json");
            var first = new DateTime(2026, 10, 3, 3, 0, 0, DateTimeKind.Utc);
            var store = new SoulseekHoldStore(path);
            store.Hold(new HeldAcquisition(HeldKind.Album, "soulseek", "a", null, first));
            var again = store.Hold(new HeldAcquisition(HeldKind.Album, "soulseek", "a", null, first.AddHours(1)));
            Assert.Equal(first, again.HeldSinceUtc);

            var reloaded = new SoulseekHoldStore(path);
            var held = Assert.Single(reloaded.Snapshot());
            Assert.Equal(first, held.HeldSinceUtc);
            Assert.Equal(HeldKind.Album, held.Kind);

            reloaded.Release(held.Key);
            Assert.Empty(new SoulseekHoldStore(path).Snapshot());
        }
        finally { dir.Delete(recursive: true); }
    }
}
