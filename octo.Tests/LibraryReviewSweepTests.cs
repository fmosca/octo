using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Fingerprint;
using Octo.Services.Library;
using Octo.Services.Local;

namespace Octo.Tests;

/// <summary>#72: the sweep asks about the library and never acts, and it gets out of a download's way.</summary>
public class LibraryReviewSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-sweep-" + Guid.NewGuid().ToString("N"));
    private string StatePath => Path.Combine(_root, ".state", "review-sweep.json");

    private readonly List<ReviewSweepStore> _stores = [];

    public LibraryReviewSweepTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeVerifier : IReviewSweepVerifier
    {
        public bool IsReady { get; set; } = true;
        public List<string> Asked { get; } = [];
        public Func<VerificationResult> Answer { get; set; } = () => Confirmed();
        public Task<VerificationResult> VerifyAsync(string path, string? artist, string? title)
        {
            Asked.Add(Path.GetFileName(path));
            return Task.FromResult(Answer());
        }
    }

    private sealed class FakeActivity : IAcquisitionActivity { public bool IsBusy { get; set; } }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static VerificationResult Confirmed(int file = 200, int? recording = 200) => new()
    {
        Verdict = VerificationVerdict.Confirmed, DurationSeconds = file, RecordingId = "rec",
        Match = new AcoustIdRecording("rec", "Teardrop", ["Massive Attack"], "Mezzanine", 1998) { DurationSeconds = recording },
    };

    private static LibraryActionSettings Settings(int perHour = 60) => new()
        { Enabled = true, ReviewEnabled = true, ReviewSweepPerHour = perHour, AllowedUsers = ["alice", "bob"] };

    private string Song(string name, string content = "x")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private LibraryReviewSweepWorker Worker(FakeVerifier verifier, NoticeQueue? queue = null,
        LibraryActionSettings? settings = null, FakeActivity? activity = null,
        IEnumerable<string>? octoDownloads = null, TimeProvider? time = null,
        Action<LibraryReviewSweepWorker>? whileListing = null)
    {
        // A worker built after another is a restart, and a stopping host flushes the store.
        foreach (var previous in _stores) previous.Flush();
        var store = new ReviewSweepStore(StatePath);
        _stores.Add(store);
        LibraryReviewSweepWorker worker = null!;
        var library = new Mock<ILocalLibraryService>();
        library.Setup(l => l.GetMappingsAsync()).Callback(() => whileListing?.Invoke(worker))
            .ReturnsAsync((IReadOnlyList<LocalSongMapping>)
                (octoDownloads ?? []).Select(path => new LocalSongMapping { LocalPath = path }).ToList());
        worker = new LibraryReviewSweepWorker(store, queue ?? new NoticeQueue(), verifier,
            activity ?? new FakeActivity(), library.Object, TestOptions.Monitor(settings ?? Settings()),
            TestOptions.Monitor(new SubsonicSettings { AdminUsername = "bob" }), () => _root,
            NullLogger<LibraryReviewSweepWorker>.Instance, time);
        return worker;
    }

    /// <summary>A clock that moves only when told to, with a flush timer that fires only when told to.</summary>
    private sealed class StoreClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
        public TimeSpan Step { get; init; }
        public TimerCallback? Tick { get; private set; }
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Add(ref _ticks, Step.Ticks), TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Tick = callback;
            return new Inert();
        }
        private sealed class Inert : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private int FineOnDisk() =>
        System.Text.Json.JsonSerializer.Deserialize<ReviewSweepState>(File.ReadAllText(StatePath))!.Fine;

    [Fact]
    public void TheStore_WritesAtMostOncePerWindow_AndKeepsTheRestForTheNextFlush()
    {
        var clock = new StoreClock();
        var store = new ReviewSweepStore(StatePath, time: clock);
        _stores.Add(store);

        store.Update(s => s.Fine = 1);
        Assert.Equal(1, FineOnDisk());
        store.Update(s => s.Fine = 2);
        store.Update(s => s.Fine = 3);
        Assert.Equal(1, FineOnDisk());

        clock.Tick!(null);
        Assert.Equal(3, FineOnDisk());
        store.Update(s => s.Fine = 4);
        Assert.Equal(3, FineOnDisk());

        clock.Advance(ReviewSweepStore.FlushInterval);
        store.Update(s => s.Fine = 5);
        Assert.Equal(5, FineOnDisk());

        store.Update(s => s.Fine = 6);
        Assert.Equal(5, FineOnDisk());
        store.Flush();
        Assert.Equal(6, new ReviewSweepStore(StatePath).Read(s => s.Fine));
    }

    [Fact]
    public async Task APause_IsNeverLostToAnOlderWrite()
    {
        // Every change is old enough to be written at once.
        var store = new ReviewSweepStore(StatePath, time: new StoreClock { Step = ReviewSweepStore.FlushInterval });
        _stores.Add(store);
        using var snapshotTaken = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var held = 0;
        store.BeforeWrite = () =>
        {
            if (Interlocked.Exchange(ref held, 1) == 1) return;
            snapshotTaken.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        };

        // A tick's write is held between its snapshot and the disk while a Pause comes in.
        var tick = Task.Run(() => store.Update(s => s.Fine++));
        Assert.True(snapshotTaken.Wait(TimeSpan.FromSeconds(10)));
        var pause = Task.Run(() => { store.Update(s => s.Paused = true); store.Flush(); });
        await Task.WhenAny(pause, Task.Delay(300));
        release.Set();
        await Task.WhenAll(tick, pause);

        var restarted = new ReviewSweepStore(StatePath);
        Assert.True(restarted.Read(s => s.Paused));
        Assert.Equal(1, restarted.Read(s => s.Fine));
    }

    [Fact]
    public async Task StartingOverWhileTheLibraryIsListed_ListsItAgain()
    {
        Song("a.flac"); Song("b.flac");
        var verifier = new FakeVerifier();
        var resets = 0;
        var worker = Worker(verifier, whileListing: w => { if (resets++ == 0) w.Reset(); });

        await worker.TickAsync(default);
        Assert.Empty(verifier.Asked);

        await worker.TickAsync(default);
        Assert.Equal(["a.flac"], verifier.Asked);
        Assert.Equal(2, worker.Status().Total);
    }

    [Fact]
    public async Task ASongInSlskdsIncompleteFolder_IsNeverChecked()
    {
        Directory.CreateDirectory(Path.Combine(_root, "incomplete"));
        Directory.CreateDirectory(Path.Combine(_root, "Artist"));
        Song(Path.Combine("incomplete", "half.flac"));
        Song(Path.Combine("Artist", "whole.flac"));
        var verifier = new FakeVerifier();
        var worker = Worker(verifier);
        for (var i = 0; i < 3; i++) await worker.TickAsync(default);
        Assert.Equal(["whole.flac"], verifier.Asked);
        Assert.Equal(1, worker.Status().Total);
    }

    [Fact]
    public async Task ZeroAnHour_IsOff()
    {
        Song("a.flac");
        var verifier = new FakeVerifier();
        var worker = Worker(verifier, settings: Settings(perHour: 0));
        await worker.TickAsync(default);
        Assert.Empty(verifier.Asked);
        Assert.Equal("Off", worker.Status().State);
    }

    [Fact]
    public async Task ARestart_CarriesOnWhereItStopped()
    {
        Song("a.flac"); Song("b.flac"); Song("c.flac");
        var first = new FakeVerifier();
        var worker = Worker(first);
        await worker.TickAsync(default);
        await worker.TickAsync(default);
        Assert.Equal(["a.flac", "b.flac"], first.Asked);

        var second = new FakeVerifier();
        await Worker(second).TickAsync(default);
        Assert.Equal(["c.flac"], second.Asked);
    }

    [Fact]
    public async Task AChangedFile_IsCheckedAgain_AndAnUnchangedOneIsNot()
    {
        var a = Song("a.flac"); Song("b.flac");
        var clock = new ManualClock();
        var verifier = new FakeVerifier();
        var worker = Worker(verifier, time: clock);
        for (var i = 0; i < 3; i++) await worker.TickAsync(default);
        Assert.Equal(["a.flac", "b.flac"], verifier.Asked);

        File.WriteAllText(a, "replaced with a longer file");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(5));
        clock.Now += LibraryReviewSweepWorker.PassInterval + TimeSpan.FromMinutes(1);
        await worker.TickAsync(default);
        await worker.TickAsync(default);
        Assert.Equal(["a.flac", "b.flac", "a.flac"], verifier.Asked);
    }

    [Fact]
    public async Task SongsOctoDownloaded_AreSkipped()
    {
        var mine = Song("a.flac"); Song("b.flac");
        var verifier = new FakeVerifier();
        await Worker(verifier, octoDownloads: [mine]).TickAsync(default);
        Assert.Equal(["b.flac"], verifier.Asked);
    }

    [Fact]
    public async Task OnlyTheKeeperIsAsked()
    {
        Song("a.flac");
        var queue = new NoticeQueue();
        await Worker(new FakeVerifier { Answer = () => ReviewFixtures.Unknown }, queue).TickAsync(default);
        Assert.Single(queue.ForUser("bob", NoticeKind.Review));
        Assert.Empty(queue.ForUser("alice", NoticeKind.Review));
    }

    [Theory]
    [InlineData("bob", "bob")]
    [InlineData("root", "alice")]
    [InlineData(null, "alice")]
    public void Keeper_IsTheAllowedAdmin_ElseTheFirstAllowedUser(string? admin, string expected)
        => Assert.Equal(expected, LibraryReviewSweepWorker.Keeper(Settings(), admin));

    [Fact]
    public async Task AConfidentDifferentRecording_IsAskedAbout_AndNothingIsDone()
    {
        var path = Song("a.flac");
        var queue = new NoticeQueue();
        var verifier = new FakeVerifier { Answer = () => new VerificationResult
        {
            Verdict = VerificationVerdict.Mismatch, Score = 0.97, MatchedArtist = "Portishead",
            MatchedTitle = "Roads", RecordingId = "rec-roads", Fingerprint = "AQADtEqk", DurationSeconds = 305,
        } };
        await Worker(verifier, queue).TickAsync(default);

        var entry = Assert.Single(queue.ForUser("bob", NoticeKind.Review));
        Assert.Equal(InconclusiveReason.SoundsLikeAnother, entry.Cause);
        Assert.Equal(NoticeOrigin.LibrarySweep, entry.Origin);
        Assert.Equal("Sounds like 'Portishead - Roads', not what its tags say", entry.Reason);
        Assert.Null(entry.Fingerprint);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ASongMuchShorterThanItsRecording_IsAskedAbout()
    {
        Song("a.flac");
        var queue = new NoticeQueue();
        await Worker(new FakeVerifier { Answer = () => Confirmed(file: 120, recording: 300) }, queue).TickAsync(default);
        Assert.Equal(InconclusiveReason.LengthOff, Assert.Single(queue.ForUser("bob", NoticeKind.Review)).Cause);
    }

    [Theory]
    [InlineData(219, 200, false)]
    [InlineData(221, 200, true)]
    [InlineData(659, 600, false)]
    [InlineData(661, 600, true)]
    [InlineData(200, null, false)]
    public void LengthOff_IsMoreThanTwentySecondsOrATenth(int file, int? recording, bool expected)
        => Assert.Equal(expected, LibraryReviewSweepWorker.IsLengthOff(file, recording));

    [Fact]
    public void KeepingALibraryQuestion_SendsNothingToAcoustId()
    {
        var queue = new NoticeQueue();
        queue.AddReview("bob", "/music/a.flac", new Song { Artist = "Massive Attack", Title = "Teardrop" },
            ReviewFixtures.Unknown, NoticeOrigin.LibrarySweep);
        var key = NoticeQueue.ReviewKey("bob", "/music/a.flac");
        queue.SetNavidromeId(key, "nd-1");
        queue.MarkQueued([key]);

        Assert.NotNull(queue.MarkKept("bob", "nd-1"));
        Assert.Empty(queue.AwaitingSubmission());
        Assert.False(NoticePlaylistWorker.Submittable(new NoticeEntry
        {
            Cause = InconclusiveReason.NoEntry, Fingerprint = "AQADtEqk", DurationSeconds = 330,
            Origin = NoticeOrigin.LibrarySweep,
        }, new SoulseekSettings { FingerprintSeconds = 120 }));
    }

    [Fact]
    public async Task FiftyOpenLibraryQuestions_PauseIt_AndAnAnswerLetsItCarryOn()
    {
        Song("z.flac");
        var queue = new NoticeQueue();
        for (var i = 0; i < LibraryReviewSweepWorker.MaxOpenQuestions; i++)
            queue.AddReview("bob", $"/music/{i}.flac", new Song { Artist = "A", Title = $"T{i}" },
                ReviewFixtures.Unknown, NoticeOrigin.LibrarySweep);
        var verifier = new FakeVerifier();
        var worker = Worker(verifier, queue);

        await worker.TickAsync(default);
        Assert.Empty(verifier.Asked);
        Assert.Equal("Paused", worker.Status().State);

        queue.Resolve(NoticeQueue.ReviewKey("bob", "/music/0.flac"), NoticeState.Kept);
        await worker.TickAsync(default);
        Assert.Equal(["z.flac"], verifier.Asked);
    }

    [Fact]
    public async Task ADownloadInFlight_MakesItWait()
    {
        Song("a.flac");
        var verifier = new FakeVerifier();
        var activity = new FakeActivity { IsBusy = true };
        var worker = Worker(verifier, activity: activity);

        Assert.Equal(LibraryReviewSweepWorker.BusyCheck, await worker.TickAsync(default));
        Assert.Empty(verifier.Asked);
        activity.IsBusy = false;
        await worker.TickAsync(default);
        Assert.Equal(["a.flac"], verifier.Asked);
    }

    [Fact]
    public async Task AnUnansweredLookup_IsTriedAgain_NotRecorded()
    {
        Song("a.flac"); Song("b.flac");
        var verifier = new FakeVerifier { Answer = () => new VerificationResult { Reason = InconclusiveReason.LookupFailed } };
        var worker = Worker(verifier);
        await worker.TickAsync(default);
        verifier.Answer = () => Confirmed();
        await worker.TickAsync(default);
        Assert.Equal(["a.flac", "a.flac"], verifier.Asked);
    }
}

public class AcoustIdBackgroundLaneTests
{
    [Fact]
    public async Task TheBackgroundLane_IsRefusedWhileADownloadWaits_AndNeverQueues()
    {
        using var limiter = new AcoustIdRateLimiter();
        for (var i = 0; i < 3; i++) Assert.True((await limiter.AcquireAsync(default)).IsAcquired);
        var download = limiter.AcquireAsync(default).AsTask();

        var background = await AcoustIdRateLimiter.InBackgroundAsync(async () => await limiter.AcquireAsync(default));

        Assert.False(background.IsAcquired);
        Assert.True((await download).IsAcquired);
    }

    [Fact]
    public async Task TheBackgroundLane_GetsAPermitWhenNobodyWaits()
    {
        using var limiter = new AcoustIdRateLimiter();
        Assert.True((await AcoustIdRateLimiter.InBackgroundAsync(async () => await limiter.AcquireAsync(default))).IsAcquired);
    }

    [Fact]
    public async Task TheBackgroundFlag_DoesNotLeakToTheCaller()
    {
        await AcoustIdRateLimiter.InBackgroundAsync(() => Task.FromResult(0));
        Assert.False(AcoustIdRateLimiter.InBackground);
    }
}
