using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.Admin;
using Octo.Services.Library;

namespace Octo.Tests;

/// <summary>
/// "Find higher quality" from the apps and the Better quality page: a queue on disk, one job per
/// song, run up to as many at once as downloads may, waiting through a Soulseek outage, and the
/// page's endpoints only for a signed-in admin on the allowed list.
/// </summary>
[Trait("Host", "Boot")]
public sealed class UpgradeQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "octo-upgrades-" + Guid.NewGuid().ToString("N"));

    public UpgradeQueueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static UpgradeAsk Ask(string id) => new(id, Title: $"Song {id}", Artist: "Artist");

    // ---- The queue ---------------------------------------------------------------------------

    [Fact]
    public void ASongAlreadyQueuedIsNotQueuedTwice()
    {
        var queue = new UpgradeQueue();
        queue.Add([Ask("a"), Ask("b")], "alice", "app");
        var (jobs, refused) = queue.Add([Ask("a")], "bob", "page");
        Assert.Null(refused);
        Assert.Equal("alice", Assert.Single(jobs).RequestedBy);
        Assert.Equal(2, queue.Snapshot().Count);
        Assert.Equal(2, queue.OpenCount);
    }

    [Fact]
    public void OnlyJobsThatHaveNotStartedCanBeCancelled()
    {
        var queue = new UpgradeQueue();
        queue.Add([Ask("a"), Ask("b")], "alice", "app");
        var running = queue.TakeNext()!;
        Assert.Equal(1, queue.Cancel(["a", "b"]));
        Assert.Equal(running.NavidromeId, Assert.Single(queue.Snapshot()).NavidromeId);
    }

    [Fact]
    public void ARestartKeepsTheQueue_AndARunningJobGoesBackInIt()
    {
        var path = Path.Combine(_dir, "upgrades.json");
        var queue = new UpgradeQueue(path);
        queue.Add([Ask("a"), Ask("b")], "alice", "app");
        Assert.Equal(UpgradeStates.Working, queue.TakeNext()!.State);

        var again = new UpgradeQueue(path);
        Assert.All(again.Snapshot(), job => Assert.Equal(UpgradeStates.Queued, job.State));
        Assert.Equal(2, again.OpenCount);
    }

    [Fact]
    public void FinishedJobsAreKeptAWeek()
    {
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        var queue = new UpgradeQueue { Clock = () => now };
        queue.Add([Ask("a")], "alice", "app");
        queue.Update("a", job => job.State = UpgradeStates.Upgraded);
        now = now.AddDays(6);
        Assert.Single(queue.Snapshot());
        now = now.AddDays(2);
        Assert.Empty(queue.Snapshot());
    }

    [Fact]
    public void EachPersonSeesTheirOwnJobs()
    {
        var queue = new UpgradeQueue();
        queue.Add([Ask("a")], "alice", "app");
        queue.Add([Ask("b")], "bob", "app");
        Assert.Equal("a", Assert.Single(queue.Snapshot("Alice")).NavidromeId);
    }

    [Fact]
    public void ClearForgetsOnlyFinishedJobs()
    {
        var queue = new UpgradeQueue();
        queue.Add([Ask("a"), Ask("b")], "alice", "app");
        queue.Update("a", job => job.State = UpgradeStates.NotFound);
        Assert.Equal(1, queue.ClearFinished());
        Assert.Equal("b", Assert.Single(queue.Snapshot()).NavidromeId);
    }

    // ---- The worker --------------------------------------------------------------------------

    private static UpgradeWorker Worker(UpgradeQueue queue, int width, Func<LibraryActionRequest, Task<LibraryActionOutcome>> apply,
        Func<bool>? offline = null) =>
        new(queue, null!, null!, NullLogger<UpgradeWorker>.Instance)
        {
            Apply = (request, _) => apply(request),
            Describe = (_, _) => Task.FromResult<ResolvedSongFile?>(null),
            Width = () => width,
            SoulseekOffline = _ => Task.FromResult(offline?.Invoke() ?? false),
        };

    [Fact]
    public async Task NoMoreRunAtOnceThanDownloadsMay()
    {
        var queue = new UpgradeQueue();
        queue.Add(Enumerable.Range(0, 6).Select(i => Ask($"s{i}")), "alice", "app");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside = 0;
        var most = 0;
        var worker = Worker(queue, 3, async _ =>
        {
            var now = Interlocked.Increment(ref inside);
            lock (queue) most = Math.Max(most, now);
            await release.Task;
            Interlocked.Decrement(ref inside);
            return new LibraryActionOutcome(LibraryActionState.Applied, "Replaced with x.flac.");
        });

        Assert.Equal(3, await worker.TickAsync(default));
        Assert.Equal(0, await worker.TickAsync(default));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref inside) < 3 && DateTime.UtcNow < deadline) await Task.Delay(5);
        release.SetResult();
        await worker.DrainAsync();
        Assert.Equal(3, await worker.TickAsync(default));
        await worker.DrainAsync();

        Assert.Equal(3, most);
        Assert.All(queue.Snapshot(), job => Assert.Equal(UpgradeStates.Upgraded, job.State));
    }

    [Fact]
    public async Task DuringAnOutageAJobWaits_ThenRunsOnceSoulseekIsBack()
    {
        var queue = new UpgradeQueue();
        queue.Add([Ask("a")], "alice", "app");
        var offline = true;
        var tries = 0;
        var worker = Worker(queue, 1, _ =>
        {
            tries++;
            return Task.FromResult(offline
                ? new LibraryActionOutcome(LibraryActionState.Failed, Octo.Services.Soulseek.SoulseekLink.OfflineText, LibraryActionCodes.SoulseekOffline)
                : new LibraryActionOutcome(LibraryActionState.Applied, "Replaced."));
        }, () => offline);

        await worker.TickAsync(default);
        await worker.DrainAsync();
        var job = Assert.Single(queue.Snapshot());
        Assert.Equal(UpgradeStates.Waiting, job.State);
        Assert.Equal("Waiting for Soulseek", job.Detail);

        Assert.Equal(0, await worker.TickAsync(default));   // still out: not tried again
        offline = false;
        Assert.Equal(1, await worker.TickAsync(default));
        await worker.DrainAsync();
        Assert.Equal(UpgradeStates.Upgraded, Assert.Single(queue.Snapshot()).State);
        Assert.Equal(2, tries);
    }

    [Theory]
    [InlineData(LibraryActionState.Applied, null, UpgradeStates.Upgraded)]
    [InlineData(LibraryActionState.Rehearsed, null, UpgradeStates.Rehearsed)]
    [InlineData(LibraryActionState.Failed, LibraryActionCodes.SoulseekOffline, UpgradeStates.Waiting)]
    [InlineData(LibraryActionState.Failed, LibraryActionCodes.NoReplacement, UpgradeStates.NotFound)]
    [InlineData(LibraryActionState.Skipped, null, UpgradeStates.Skipped)]
    [InlineData(LibraryActionState.Unresolved, null, UpgradeStates.Failed)]
    [InlineData(LibraryActionState.Failed, null, UpgradeStates.Failed)]
    public void OutcomesMapOnTheCodeNotTheWords(LibraryActionState state, string? code, string expected) =>
        Assert.Equal(expected, UpgradeWorker.StateFor(new LibraryActionOutcome(state, "No Soulseek FLAC found, whatever", code)));

    [Fact]
    public async Task TheReplacementsDownloadIsWrittenOnTheJob()
    {
        var queue = new UpgradeQueue();
        queue.Add([Ask("a")], "alice", "app");
        var worker = Worker(queue, 1, request =>
        {
            request.OnReplacementQueued!("soulseek", "ext-1");
            Assert.Equal(LibraryAction.BetterQuality, request.Action);
            Assert.Equal("alice", request.Username);
            Assert.Equal("ext-1", request.OnReplacementQueued is null ? null : "ext-1");
            return Task.FromResult(new LibraryActionOutcome(LibraryActionState.Applied, "ok"));
        });
        await worker.TickAsync(default);
        await worker.DrainAsync();
        Assert.Equal("soulseek:ext-1", Assert.Single(queue.Snapshot()).AcquisitionKey);
    }

    [Fact]
    public async Task ASongWithNoCopyAnywhereIsNotRetriedByTheWeeklyRun()
    {
        var store = new QualityUpgradeStore();
        var queue = new UpgradeQueue();
        queue.Add([new UpgradeAsk("a", AttemptKey: "Artist/a.mp3|100")], "alice", "page");
        var worker = new UpgradeWorker(queue, null!, null!, NullLogger<UpgradeWorker>.Instance, attempts: store)
        {
            Apply = (_, _) => Task.FromResult(new LibraryActionOutcome(LibraryActionState.Failed, "nothing", LibraryActionCodes.NoReplacement)),
            Describe = (_, _) => Task.FromResult<ResolvedSongFile?>(null),
            Width = () => 1,
            SoulseekOffline = _ => Task.FromResult(false),
        };
        await worker.TickAsync(default);
        await worker.DrainAsync();
        Assert.True(store.Snapshot().Attempts.ContainsKey("Artist/a.mp3|100"));
    }

    [Fact]
    public void AnUpgradeSaysWhatChangedAndWhatItPassed()
    {
        var newFile = Path.Combine(_dir, "Song.flac");
        File.WriteAllBytes(newFile, FlacOf(200));
        var kept = Path.Combine(_dir, ".octo-trash", "2026-10-03", "Song.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllBytes(kept, AudioFixtures.Mp3());
        var settings = TestOptions.Monitor(new SoulseekSettings { VerifyDownloads = true, AcoustIdApiKey = "key", DetectTranscodes = true });
        var worker = new UpgradeWorker(new UpgradeQueue(), null!, null!, NullLogger<UpgradeWorker>.Instance, soulseekSettings: settings);

        var result = worker.Report(new LibraryActionOutcome(LibraryActionState.Applied, "Replaced.") { NewPath = newFile, QuarantinePath = kept },
            new UpgradeJob { StartedUtc = DateTime.UtcNow.AddSeconds(-68) });

        Assert.Equal("FLAC 16-bit 44.1 kHz", result.After);
        Assert.StartsWith("MP3", result.Before);
        Assert.Equal("Song.flac", result.NewFile);
        Assert.Equal(".octo-trash/2026-10-03", result.KeptAt);
        Assert.Equal(["the same length", "AcoustID: the same recording", "the spectrum: really lossless, not a converted MP3"], result.Checks);
        Assert.InRange(result.Seconds!.Value, 67, 70);
    }

    [Fact]
    public void WithoutAcoustIdTheReportDoesNotClaimIt()
    {
        var worker = new UpgradeWorker(new UpgradeQueue(), null!, null!, NullLogger<UpgradeWorker>.Instance,
            soulseekSettings: TestOptions.Monitor(new SoulseekSettings { VerifyDownloads = false, DetectTranscodes = true }));
        var result = worker.Report(new LibraryActionOutcome(LibraryActionState.Applied, "ok"), new UpgradeJob());
        Assert.DoesNotContain(result.Checks, check => check.Contains("AcoustID"));
    }

    private static byte[] FlacOf(int seconds)
    {
        using var stream = new MemoryStream();
        stream.Write("fLaC"u8);
        stream.Write([0x80, 0x00, 0x00, 0x22]);
        stream.Write([0x10, 0x00, 0x10, 0x00]);
        stream.Write([0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        const ulong sampleRate = 44100, channelsMinusOne = 1, bitsMinusOne = 15;
        var packed = (sampleRate << 44) | (channelsMinusOne << 41) | (bitsMinusOne << 36) | (44100UL * (ulong)seconds);
        for (var shift = 56; shift >= 0; shift -= 8) stream.WriteByte((byte)(packed >> shift));
        stream.Write(new byte[16]);
        return stream.ToArray();
    }

    // ---- The Better quality page's endpoints ------------------------------------------------

    private static WebFactory Page(bool allowed = true, bool dryRun = false) => new(allowed, dryRun);

    private sealed class WebFactory(bool allowed, bool dryRun) : IAsyncDisposable
    {
        public readonly UpgradeQueue Queue = new();
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory = new AdminWebFactory();
        private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _built;

        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Built => _built ??= _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LibraryActions:Enabled"] = "true",
                ["LibraryActions:DryRun"] = dryRun ? "true" : "false",
                ["LibraryActions:AllowedUsers:0"] = allowed ? "admin" : "someone-else",
                ["LibraryActions:Actions:0:Action"] = "BetterQuality",
                ["LibraryActions:Actions:0:Enabled"] = "true",
                ["Soulseek:Username"] = "slskd-user",
                ["Soulseek:Password"] = "slskd-pass",
            }));
            b.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<UpgradeQueue>();
                services.AddSingleton(Queue);
                // In memory, so signing in never writes beside a real settings file.
                services.RemoveAll<BrowseSessionStore>();
                services.AddSingleton(new BrowseSessionStore());
            });
        });

        public string SignIn() => Built.Services.GetRequiredService<BrowseSessionStore>().Create("admin");

        public HttpClient Client(string? token)
        {
            var client = Built.CreateClient();
            client.DefaultRequestHeaders.Add("X-Octo-Admin", "1");
            if (token is not null) client.DefaultRequestHeaders.Add("X-Octo-Browse-Token", token);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            if (_built is not null) await _built.DisposeAsync();
            await _factory.DisposeAsync();
        }
    }

    private static readonly object Body = new { songs = new[] { new { navidromeId = "nd-1", title = "Teardrop" } } };

    [Fact]
    public async Task ThePageQueuesOnlyForASignedInAdmin()
    {
        await using var page = Page();
        using var anonymous = page.Client(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/admin/upgrades", Body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/upgrades")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/lossy")).StatusCode);
        Assert.Empty(page.Queue.Snapshot());

        using var signedIn = page.Client(page.SignIn());
        var response = await signedIn.PostAsJsonAsync("/api/admin/upgrades", Body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = Assert.Single(page.Queue.Snapshot());
        Assert.Equal("admin", job.RequestedBy);
        Assert.Equal("page", job.Origin);

        using var doc = JsonDocument.Parse(await signedIn.GetStringAsync("/api/admin/upgrades"));
        Assert.Equal("nd-1", Assert.Single(doc.RootElement.GetProperty("jobs").EnumerateArray()).GetProperty("id").GetString());
        Assert.True(doc.RootElement.GetProperty("gate").GetProperty("allowed").GetBoolean());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public async Task RefreshingTheListIsNeverAValidationError(string refresh)
    {
        await using var page = Page();
        using var client = page.Client(page.SignIn());
        var response = await client.GetAsync($"/api/admin/lossy?refresh={refresh}");
        var body = await response.Content.ReadAsStringAsync();
        // The test host has no Navidrome admin credential, so the answer is that plain reason, never
        // ASP.NET's "the value is not valid" for the parameter itself.
        Assert.DoesNotContain("errors", body);
        Assert.Contains("Navidrome admin credential", body);
    }

    [Fact]
    public async Task AnAdminNotOnTheAllowedListIsRefused()
    {
        await using var page = Page(allowed: false);
        using var client = page.Client(page.SignIn());
        var response = await client.PostAsJsonAsync("/api/admin/upgrades", Body);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("allowed list", await response.Content.ReadAsStringAsync());
        Assert.Empty(page.Queue.Snapshot());
    }

    [Fact]
    public async Task WhileDryRunIsOnNothingIsQueued()
    {
        await using var page = Page(dryRun: true);
        using var client = page.Client(page.SignIn());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/upgrades", Body)).StatusCode);
        Assert.Empty(page.Queue.Snapshot());
    }

    [Fact]
    public async Task AWriteFromAnotherSiteIsRefusedEvenSignedIn()
    {
        await using var page = Page();
        var token = page.SignIn();
        using var client = page.Built.CreateClient();
        client.DefaultRequestHeaders.Add("X-Octo-Browse-Token", token);
        var response = await client.PostAsJsonAsync("/api/admin/upgrades", Body);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(page.Queue.Snapshot());
    }
}
