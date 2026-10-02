using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Library;
using Octo.Services.Notifications;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// W8: a replacement takes the original's folder and name, Octo checks that Navidrome kept the
/// ORIGINAL id on the new file, records the answer on the action, and only when it did not,
/// favourites the new song again for the person who rated it.
/// </summary>
public sealed class LibraryActionKeepIdentityTests : IDisposable
{
    private const string Key = "key-1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "octo-keepid-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Endpoint, Dictionary<string, string> Parameters)> _calls = new();

    public LibraryActionKeepIdentityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private StarOnArrival Stars()
    {
        var stars = new StarOnArrival(new AcquisitionTracker(NullLogger<AcquisitionTracker>.Instance, null),
            scopes: null!, TestOptions.Monitor(new SubsonicSettings()), NullLogger<StarOnArrival>.Instance)
        {
            VisibilityPoll = TimeSpan.FromMilliseconds(10),
            LibraryLookup = (_, _, _, _) => Task.FromResult<string?>("nd-new"),
            Call = (endpoint, parameters) =>
            {
                _calls.Enqueue((endpoint, parameters));
                var body = endpoint == "rest/getSong"
                    ? "{\"subsonic-response\":{\"status\":\"ok\",\"song\":{\"id\":\"" + parameters["id"] + "\"}}}"
                    : """{"subsonic-response":{"status":"ok"}}""";
                return Task.FromResult(Encoding.UTF8.GetBytes(body));
            },
        };
        return stars;
    }

    private static LibraryActionExecutor Executor(LibraryActionJournal journal, StarOnArrival? stars = null) => new(
        resolver: null!, quarantine: null!, journal: journal, library: null!, ids: null!,
        rejectedPeers: null!, acquisitions: null!,
        settings: TestOptions.Monitor(new LibraryActionSettings()),
        soulseek: TestOptions.Monitor(new SoulseekSettings()),
        subsonicSettings: TestOptions.Monitor(new SubsonicSettings()),
        logger: NullLogger<LibraryActionExecutor>.Instance,
        stars: stars)
    {
        HistoryPoll = TimeSpan.FromMilliseconds(1),
        HistoryAttempts = 3,
    };

    private ResolvedSongFile Original(string? path = null) => new("nd-1",
        path ?? Path.Combine(_dir, "Massive Attack - Teardrop.mp3"), 1000,
        "Teardrop", "Massive Attack", "Mezzanine", "mp3", 330, PathSource.NativeApi);

    private static LibraryActionJournal Journal()
    {
        var journal = new LibraryActionJournal();
        journal.Record(new LibraryActionEntry(Key, LibraryAction.BetterQuality, "nd-1", "alice",
            "Teardrop", "Massive Attack", "Mezzanine", "/music/a.mp3", null, PathSource.NativeApi,
            LibraryActionState.Applied, "Replaced with a.flac.", DryRun: false, DateTime.UtcNow));
        return journal;
    }

    private static LibraryActionEntry Entry(LibraryActionJournal journal) => journal.Recent().Single(entry => entry.Key == Key);

    private static KeptIdentity Identity() => new("Teardrop", "Mezzanine", ["Massive Attack"], [], null, null, null, null,
        3, 11, 1, 1, false);

    private static SubsonicCredential Alice() => SubsonicCredential.From(new Dictionary<string, string>
    {
        ["u"] = "alice", ["t"] = "token", ["s"] = "salt", ["c"] = "Symfonium",
    })!;

    private List<Dictionary<string, string>> Calls(string endpoint) =>
        _calls.Where(call => call.Endpoint == endpoint).Select(call => call.Parameters).ToList();

    [Fact]
    public void TheReplacementTargetIsTheOriginalsFolderAndStem()
    {
        var original = Original();
        File.WriteAllBytes(original.AbsolutePath, [1, 2, 3]);
        var handoff = LibraryActionExecutor.HandoffFor(original, Identity(), _ => Task.FromResult<string?>(null));

        var flac = Path.Combine(_dir, "Massive Attack - Teardrop.flac");
        Assert.Equal(flac, handoff.TargetFor(".flac"));
        Assert.Null(handoff.TargetFor(".mp3"));
        File.Delete(original.AbsolutePath);
        Assert.Equal(original.AbsolutePath, handoff.TargetFor(".mp3"));
        File.WriteAllBytes(flac, [1, 2, 3]);
        Assert.Null(handoff.TargetFor(".flac"));
    }

    [Fact]
    public async Task TheOriginalIdShowingTheNewFile_IsHistoryKept()
    {
        var journal = Journal();
        var executor = Executor(journal);
        var asked = new List<string>();
        var scans = 0;
        executor.ShowsAt = (id, _, _) => { asked.Add(id); return Task.FromResult(asked.Count >= 2); };
        executor.ForceScan = () => { scans++; return Task.FromResult(true); };

        var kept = await executor.ConfirmHistoryKeptAsync(
            new LibraryActionRequest(LibraryAction.BetterQuality, "nd-1", "alice"), Original(),
            Path.Combine(_dir, "Massive Attack - Teardrop.flac"), Key, carryStar: false);

        Assert.True(kept);
        Assert.Equal(2, asked.Count);
        Assert.All(asked, id => Assert.Equal("nd-1", id));
        Assert.Equal(1, scans);
        var entry = Entry(journal);
        Assert.True(entry.HistoryKept);
        Assert.Contains(LibraryActionExecutor.HistoryKeptText, entry.Detail);
    }

    [Fact]
    public async Task NavidromeTakingItForANewSong_FallsBackToTheRatersFavourite()
    {
        var journal = Journal();
        var executor = Executor(journal, Stars());
        executor.ShowsAt = (_, _, _) => Task.FromResult(false);
        executor.ForceScan = () => Task.FromResult(true);

        var kept = await executor.ConfirmHistoryKeptAsync(
            new LibraryActionRequest(LibraryAction.BetterQuality, "nd-1", "alice", Alice()), Original(),
            Path.Combine(_dir, "Massive Attack - Teardrop.flac"), Key, carryStar: true);

        Assert.False(kept);
        Assert.False(Entry(journal).HistoryKept);
        Assert.Contains(LibraryActionExecutor.HistoryLostText, Entry(journal).Detail);
        await LastFmScrobbleServiceTests.Until(() => Calls("rest/star").Count == 1);
        var star = Calls("rest/star").Single();
        Assert.Equal("nd-new", star["id"]);
        Assert.Equal("alice", star["u"]);
    }

    [Fact]
    public async Task NotKeptWithoutAFavourite_StarsNothing()
    {
        var journal = Journal();
        var executor = Executor(journal, Stars());
        executor.ShowsAt = (_, _, _) => Task.FromResult(false);
        executor.ForceScan = () => Task.FromResult(true);

        var kept = await executor.ConfirmHistoryKeptAsync(
            new LibraryActionRequest(LibraryAction.BetterQuality, "nd-1", "alice", Alice()), Original(),
            Path.Combine(_dir, "Massive Attack - Teardrop.flac"), Key, carryStar: false);

        Assert.False(kept);
        Assert.False(Entry(journal).HistoryKept);
        await Task.Delay(100);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task TheWorkerHandsTheReplacementHandoffToTheDownload()
    {
        var handoff = LibraryActionExecutor.HandoffFor(Original(), Identity(), _ => Task.FromResult<string?>(null));
        var queue = new TrackAcquisitionQueue(NullLogger<TrackAcquisitionQueue>.Instance);
        var downloads = new Mock<IDownloadService>();
        downloads.Setup(d => d.ExecuteAcquisitionAsync("soulseek", "id-1", false, true, DownloadSource.Soulseek,
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>(), true,
                It.Is<ReplacementHandoff?>(h => ReferenceEquals(h, handoff))))
            .ReturnsAsync("/music/a.flac");
        var worker = new AcquisitionWorker(queue, downloads.Object, new ExternalIdRegistry(),
            new NotificationService([], TestOptions.Monitor(new NotificationSettings()), NullLogger<NotificationService>.Instance),
            NullLogger<AcquisitionWorker>.Instance);
        await worker.StartAsync(default);
        var path = await queue.Enqueue("soulseek", "id-1", isStar: true, triggerAlbumDownload: false, forcePermanent: true,
            sourceOverride: DownloadSource.Soulseek, upgradeSearch: true, replacement: handoff);
        await worker.StopAsync(default);
        Assert.Equal("/music/a.flac", path);
    }
}
