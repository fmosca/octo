using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Octo.Models.Settings;
using Octo.Services;
using Octo.Services.Common;
using Octo.Services.Library;
using Octo.Services.Local;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// A library replacement runs end to end here: a stand-in Navidrome resolves the song, the real
/// queue carries the request, and the test plays the download, so the swap's edges can be timed.
/// One action per song at a time, a joined download is never judged, the original must be the
/// file the action started with, and a replacement already in place is never undone.
/// </summary>
public sealed class LibraryActionOneAtATimeTests : IDisposable
{
    private const string FileName = "Massive Attack - Teardrop";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-oneatatime-" + Guid.NewGuid().ToString("N"));
    private readonly TrackAcquisitionQueue _queue = new(NullLogger<TrackAcquisitionQueue>.Instance);
    private readonly ExternalIdRegistry _ids = new();
    private readonly LibraryActionJournal _journal = new();
    private readonly Mock<ILocalLibraryService> _library = new();
    private readonly string _original;
    private readonly long _originalSize;

    public LibraryActionOneAtATimeTests()
    {
        Directory.CreateDirectory(_root);
        _original = Path.Combine(_root, FileName + ".mp3");
        File.WriteAllBytes(_original, AudioFixtures.Mp3());
        using (var file = TagLib.File.Create(_original))
        {
            file.Tag.Title = "Teardrop"; file.Tag.Album = "Mezzanine"; file.Tag.AlbumArtists = ["Massive Attack"];
            file.Save();
        }
        _originalSize = new FileInfo(_original).Length;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Revealed => Path.Combine(_root, FileName + ".flac");

    private LibraryActionExecutor Executor(bool keepOriginals = false,
        Octo.Services.Soulseek.ISoulseekLink? soulseekLink = null, UpgradeSources? sources = null)
    {
        var settings = TestOptions.Monitor(new LibraryActionSettings
        {
            Enabled = true, DryRun = false, AllowedUsers = ["alice"], KeepReplacedOriginals = keepOriginals,
            Actions =
            [
                new() { Action = LibraryAction.WrongVersion, Enabled = true },
                new() { Action = LibraryAction.BetterQuality, Enabled = true },
            ],
        });
        var factory = new ReviewFixtures.OneClientFactory(new Navidrome(this));
        var subsonic = TestOptions.Monitor(new SubsonicSettings
        {
            Url = "http://navidrome.test", AdminUsername = "admin", AdminPassword = "secret",
            AutoDetectDownloadPath = false,
        });
        var identity = new NavidromeIdentityService(subsonic, factory, NullLogger<NavidromeIdentityService>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Library:DownloadPath"] = _root }).Build();
        var resolver = new NavidromeSongPathResolver(identity, _library.Object, factory, subsonic, config,
            NullLogger<NavidromeSongPathResolver>.Instance);
        return new LibraryActionExecutor(resolver,
            new LibraryActionQuarantine(settings, NullLogger<LibraryActionQuarantine>.Instance),
            _journal, _library.Object, _ids, new RejectedPeerRegistry(), _queue, settings,
            TestOptions.Monitor(new SoulseekSettings()), subsonic, NullLogger<LibraryActionExecutor>.Instance,
            soulseekLink: soulseekLink, sources: sources)
        {
            HistoryPoll = TimeSpan.FromMilliseconds(1),
            HistoryAttempts = 1,
            ShowsAt = (_, _, _) => Task.FromResult(true),
            ForceScan = () => Task.FromResult(true),
        };
    }

    /// <summary>Navidrome's view of the song: the original, at the size it was scanned at.</summary>
    private sealed class Navidrome(LibraryActionOneAtATimeTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/login")
                return Task.FromResult(ReviewFixtures.Json("{\"token\":\"jwt\",\"isAdmin\":true,\"username\":\"admin\"}"));
            if (path == "/api/song/nd-1")
                return Task.FromResult(ReviewFixtures.Json(
                    $"{{\"id\":\"nd-1\",\"path\":\"{FileName}.mp3\",\"size\":{test._originalSize},\"title\":\"Teardrop\","
                    + "\"artist\":\"Massive Attack\",\"album\":\"Mezzanine\",\"suffix\":\"mp3\",\"duration\":330}"));
            return Task.FromResult(ReviewFixtures.Json("[]"));
        }
    }

    private static LibraryActionRequest WrongVersion() => new(LibraryAction.WrongVersion, "nd-1", "alice");

    private sealed class OfflineLink : Octo.Services.Soulseek.ISoulseekLink
    {
        public Task<Octo.Services.Soulseek.SoulseekServerReading?> ReadAsync(bool fresh, CancellationToken ct) =>
            Task.FromResult<Octo.Services.Soulseek.SoulseekServerReading?>(
                new(Octo.Services.Soulseek.SoulseekLinkState.NotLoggedIn, "Disconnecting", null, null));
        public TimeSpan HoldLimit => TimeSpan.FromHours(6);
        public DateTime UtcNow => DateTime.UtcNow;
        public Task<bool> WaitForLoginAsync(DateTime deadlineUtc, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task BetterQualityDuringASoulseekOutage_TouchesNothingAndStaysAsked()
    {
        var executor = Executor(soulseekLink: new OfflineLink());

        var outcome = await executor.ApplyAsync(new(LibraryAction.BetterQuality, "nd-1", "alice"));

        Assert.Equal(LibraryActionState.Failed, outcome.State);
        Assert.Equal(LibraryActionCodes.SoulseekOffline, outcome.Code);
        Assert.Equal(Octo.Services.Soulseek.SoulseekLink.OfflineText, outcome.Detail);
        Assert.False(outcome.Consumed);
        Assert.True(_queue.IsIdle);
        Assert.True(File.Exists(_original));
        Assert.Equal(_originalSize, new FileInfo(_original).Length);
        Assert.Empty(_journal.Recent(10));
    }

    private async Task<AcquisitionRequest> NextDownload()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return (await _queue.DequeueAsync(timeout.Token))!;
    }

    private string Staged(byte[] bytes, string extension)
    {
        var incoming = Path.Combine(_root, SoulseekDownloadService.IncomingFolderName);
        Directory.CreateDirectory(incoming);
        var staged = Path.Combine(incoming, $"replacement-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(staged, bytes);
        return staged;
    }

    /// <summary>What BaseDownloadService.RevealReplacementAsync does with the handoff.</summary>
    private static async Task<string> Reveal(AcquisitionRequest download, string staged)
    {
        var handoff = download.Replacement!;
        if (await handoff.BeforeReveal(staged) is { } problem)
        {
            File.Delete(staged);
            throw new ReplacementRejectedException(problem);
        }
        var target = handoff.TargetFor(Path.GetExtension(staged))!;
        File.Move(staged, target);
        handoff.RevealedPath = target;
        handoff.OnRevealed?.Invoke(target);
        return target;
    }

    private void Finish(AcquisitionRequest download, string path)
    {
        _queue.Release(download);
        download.Completion.TrySetResult(path);
    }

    private void Fail(AcquisitionRequest download, Exception ex)
    {
        _queue.Release(download);
        download.Completion.TrySetException(ex);
    }

    private IEnumerable<string> Quarantined() =>
        Directory.Exists(Path.Combine(_root, new LibraryActionSettings().EffectiveQuarantineDirectory))
            ? Directory.EnumerateFiles(Path.Combine(_root, new LibraryActionSettings().EffectiveQuarantineDirectory), "*.mp3", SearchOption.AllDirectories)
            : [];

    private LibraryActionEntry Action() =>
        _journal.Recent().Single(entry => !entry.Key.Contains("busy:", StringComparison.Ordinal));

    [Fact]
    public async Task ASecondActionOnTheSameSong_IsSkippedWhileTheFirstRuns()
    {
        var executor = Executor();
        var first = executor.ApplyAsync(WrongVersion());
        var download = await NextDownload();

        // The rating worker, the playlist worker or the weekly upgrade asking at the same time.
        var second = await executor.ApplyAsync(WrongVersion()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LibraryActionState.Skipped, second.State);
        Assert.Equal(LibraryActionExecutor.BusyText, second.Detail);
        Assert.True(File.Exists(_original));
        Assert.Contains(_journal.Recent(), entry => entry.Detail == LibraryActionExecutor.BusyText);

        Finish(download, await Reveal(download, Staged(AudioFixtures.Flac(), ".flac")));
        var outcome = await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LibraryActionState.Applied, outcome.State);
        Assert.True(File.Exists(Revealed));
        Assert.False(File.Exists(_original));
        Assert.Equal(LibraryActionState.Applied, Action().State);
        _library.Verify(l => l.ForgetMappingAsync(_original), Times.Once);
    }

    /// <summary>
    /// The request joined a download already in flight, so its handoff was never used and the
    /// file that came back belongs to whoever started it. Here that was another replacement,
    /// which took the original out and put its own file in its place.
    /// </summary>
    [Fact]
    public async Task AJoinedDownload_LeavesTheFileItReturnedAlone()
    {
        var externalId = _ids.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Song, Artist = "Massive Attack", Title = "Teardrop", Album = "Mezzanine", Duration = 330,
        });
        _ = _queue.Enqueue(SoulseekMetadataService.ProviderName, externalId, isStar: true,
            triggerAlbumDownload: false, forcePermanent: true);
        var download = await NextDownload();
        Assert.Null(download.Replacement);

        var action = Executor().ApplyAsync(WrongVersion());
        await LastFmScrobbleServiceTests.Until(() => download.RequestedBy.Contains("alice"));

        // The other action's swap: the original out, its replacement in.
        File.Move(_original, Path.Combine(_root, "elsewhere.mp3.bak"));
        File.WriteAllBytes(Revealed, AudioFixtures.Flac());
        Finish(download, Revealed);
        var outcome = await action.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LibraryActionState.Failed, outcome.State);
        Assert.Equal(LibraryActionExecutor.JoinedText, outcome.Detail);
        Assert.True(File.Exists(Revealed));
        Assert.Empty(Quarantined());
    }

    [Fact]
    public async Task AnOriginalThatChangedDuringTheDownload_IsNotSwappedOut()
    {
        var action = Executor().ApplyAsync(WrongVersion());
        var download = await NextDownload();

        // Rewritten while the replacement downloaded: same size, new modified time.
        File.SetLastWriteTimeUtc(_original, DateTime.UtcNow.AddHours(1));
        var staged = Staged(AudioFixtures.Flac(), ".flac");
        Fail(download, await Assert.ThrowsAsync<ReplacementRejectedException>(() => Reveal(download, staged)));
        var outcome = await action.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LibraryActionState.Failed, outcome.State);
        Assert.Contains("changed while it was downloading", outcome.Detail);
        Assert.True(File.Exists(_original));
        Assert.False(File.Exists(Revealed));
        Assert.Empty(Quarantined());
    }

    /// <summary>A refused replacement leaves the original exactly as it was, mapping included,
    /// so who sent it is still known and a favourite does not download it again.</summary>
    [Fact]
    public async Task ARefusedReplacement_KeepsTheOriginalsMapping()
    {
        var action = Executor().ApplyAsync(WrongVersion());
        var download = await NextDownload();

        var staged = Staged([], ".flac");
        Fail(download, await Assert.ThrowsAsync<ReplacementRejectedException>(() => Reveal(download, staged)));
        var outcome = await action.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LibraryActionState.Failed, outcome.State);
        Assert.True(File.Exists(_original));
        _library.Verify(l => l.ForgetMappingAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// The replacement moved in and then the bookkeeping after it failed (saving the mappings,
    /// say). Restoring the original now would put a duplicate beside it, or be refused at a
    /// shared path; the swap is done, so it counts as applied.
    /// </summary>
    [Fact]
    public async Task AFailureAfterTheReplacementMovedIn_DoesNotUndoIt()
    {
        var action = Executor(keepOriginals: true).ApplyAsync(WrongVersion());
        var download = await NextDownload();

        await Reveal(download, Staged(AudioFixtures.Flac(), ".flac"));
        Assert.Equal(Revealed, Action().RevealedPath);
        Fail(download, new IOException("the mappings could not be saved"));
        var outcome = await action.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LibraryActionState.Applied, outcome.State);
        Assert.Equal($"Replaced with {FileName}.flac.", outcome.Detail);
        Assert.True(File.Exists(Revealed));
        Assert.False(File.Exists(_original));
        Assert.Single(Quarantined());
    }

    private static readonly SoulseekSettings SlskdSetUp = new() { BaseUrl = "http://slskd:5030", Username = "u", Password = "p" };
    private static readonly LidarrSettings LidarrSetUp = new()
    {
        BaseUrl = "http://lidarr:8686", ApiKey = "k", RootFolderPath = "/music", QualityProfileId = 1, MetadataProfileId = 1,
    };

    private static UpgradeSources BothSources(Octo.Services.Soulseek.ISoulseekLink? link = null) =>
        new(TestOptions.Monitor(new LibraryActionSettings()), TestOptions.Monitor(SlskdSetUp), TestOptions.Monitor(LidarrSetUp), link);

    private static LibraryActionRequest BetterQuality() => new(LibraryAction.BetterQuality, "nd-1", "alice");

    /// <summary>A lossless file larger than the original, as Better quality demands.</summary>
    private string LosslessStaged() => Staged(new byte[_originalSize + 4096], ".flac");

    [Fact]
    public async Task BetterQuality_AsksLidarrWhenSoulseekFindsNothing()
    {
        var action = Executor(sources: BothSources()).ApplyAsync(BetterQuality());

        var soulseek = await NextDownload();
        Assert.Equal(DownloadSource.Soulseek, soulseek.SourceOverride);
        Assert.True(soulseek.UpgradeSearch);
        Fail(soulseek, new FileNotFoundException("no peer had it"));

        var lidarr = await NextDownload();
        Assert.Equal(DownloadSource.Lidarr, lidarr.SourceOverride);
        Assert.True(lidarr.UpgradeSearch);
        Finish(lidarr, await Reveal(lidarr, LosslessStaged()));
        var outcome = await action.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LibraryActionState.Applied, outcome.State);
        Assert.True(File.Exists(Revealed));
        Assert.False(File.Exists(_original));
    }

    [Fact]
    public async Task BetterQuality_AsksLidarrWhenSoulseeksCopyFailsTheChecks()
    {
        var action = Executor(sources: BothSources()).ApplyAsync(BetterQuality());

        var soulseek = await NextDownload();
        // An MP3 renamed: no larger than the original, so refused before it was ever placed.
        var staged = Staged(new byte[10], ".flac");
        Fail(soulseek, await Assert.ThrowsAsync<ReplacementRejectedException>(() => Reveal(soulseek, staged)));

        var lidarr = await NextDownload();
        Assert.Equal(DownloadSource.Lidarr, lidarr.SourceOverride);
        Finish(lidarr, await Reveal(lidarr, LosslessStaged()));

        Assert.Equal(LibraryActionState.Applied, (await action.WaitAsync(TimeSpan.FromSeconds(10))).State);
        Assert.False(File.Exists(_original));
    }

    [Fact]
    public async Task BetterQuality_DuringASoulseekOutage_GoesStraightToLidarr()
    {
        var action = Executor(soulseekLink: new OfflineLink(), sources: BothSources(new OfflineLink())).ApplyAsync(BetterQuality());

        var download = await NextDownload();
        Assert.Equal(DownloadSource.Lidarr, download.SourceOverride);
        Finish(download, await Reveal(download, LosslessStaged()));

        Assert.Equal(LibraryActionState.Applied, (await action.WaitAsync(TimeSpan.FromSeconds(10))).State);
    }

    [Fact]
    public async Task BetterQuality_WhenEverySourceMisses_SaysWhatEachFound()
    {
        var action = Executor(sources: BothSources()).ApplyAsync(BetterQuality());

        Fail(await NextDownload(), new FileNotFoundException("no peer had it"));
        Fail(await NextDownload(), new FileNotFoundException("Lidarr found no lossless copy within 30 minutes."));
        var outcome = await action.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LibraryActionState.Failed, outcome.State);
        Assert.Equal(LibraryActionCodes.NoReplacement, outcome.Code);
        Assert.Contains("Lidarr found no lossless copy", outcome.Detail);
        Assert.Contains("Before that, Soulseek: no peer had it", outcome.Detail);
        Assert.True(File.Exists(_original));
        Assert.Empty(Quarantined());
    }

    [Fact]
    public async Task BetterQuality_WithNoSourceSetUp_FailsWithoutDownloading()
    {
        var none = new UpgradeSources(TestOptions.Monitor(new LibraryActionSettings()),
            TestOptions.Monitor(new SoulseekSettings()), TestOptions.Monitor(new LidarrSettings()));

        var outcome = await Executor(sources: none).ApplyAsync(BetterQuality());

        Assert.Equal(LibraryActionState.Failed, outcome.State);
        Assert.Contains("no source set up", outcome.Detail);
        Assert.True(_queue.IsIdle);
        Assert.True(File.Exists(_original));
    }
}
