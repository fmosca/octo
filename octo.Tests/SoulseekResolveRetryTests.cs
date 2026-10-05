using Octo.Services.Soulseek;
using Octo.Services.Common;

namespace Octo.Tests;

/// <summary>
/// slskd marks a transfer Succeeded before moving the file out of its incomplete
/// directory, and on bind mounts that move is a copy that can take seconds. The
/// one-shot disk check used to miss the mid-move file, fail the attempt, and
/// re-download the same track from the next peer. These tests pin the bounded
/// re-poll that closes that window.
///
/// They also pin what that window looks like from the outside: while the move runs,
/// the copy the resolver can see is the one in slskd's incomplete directory, and it
/// is about to stop existing. Handing that path back as final is what stranded a
/// finished download — placement skipped it (the file was gone), the tagger could
/// not open it, and the history recorded it with SizeBytes 0 — so a hit there is
/// held back until the settled copy appears.
/// </summary>
public class SoulseekResolveRetryTests
{
    private static SoulseekDownloadService.ResolvedPath Settled(string path) => new(path, false);
    private static SoulseekDownloadService.ResolvedPath Waiting(string path) => new(path, true);

    [Fact]
    public async Task ResolvesImmediately_WithoutWaiting()
    {
        var calls = 0;
        var result = await SoulseekDownloadService.RetryResolveAsync(
            () => { calls++; return Settled("/music/song.flac"); },
            maxWait: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.Equal("/music/song.flac", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ResolvesWhenFileAppearsMidWindow()
    {
        var calls = 0;
        var result = await SoulseekDownloadService.RetryResolveAsync(
            () => ++calls >= 3 ? Settled("/music/song.flac") : null,
            maxWait: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.Equal("/music/song.flac", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task GivesUpAfterMaxWait()
    {
        var result = await SoulseekDownloadService.RetryResolveAsync(
            () => (SoulseekDownloadService.ResolvedPath?)null,
            maxWait: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CancelledCaller_GetsOneFinalCheckInsteadOfTheWindow()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var calls = 0;
        var result = await SoulseekDownloadService.RetryResolveAsync(
            () => ++calls >= 2 ? Settled("/music/song.flac") : null,
            maxWait: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromSeconds(30),
            cts.Token);

        // First check misses, the delay is cancelled, the final check lands.
        Assert.Equal("/music/song.flac", result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AFileOnlyInSlskdsIncompleteDirectory_IsHeldUntilItSettles()
    {
        var calls = 0;
        var result = await SoulseekDownloadService.RetryResolveAsync(
            () => ++calls <= 3
                ? Waiting("/music/slskd/incomplete/Album/01 Song.flac")
                : Settled("/music/slskd/Album/01 Song.flac"),
            maxWait: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.Equal("/music/slskd/Album/01 Song.flac", result);
    }

    [Fact]
    public async Task AFileThatNeverLeavesIncomplete_IsStillReturnedAtTheDeadline()
    {
        var result = await SoulseekDownloadService.RetryResolveAsync(
            () => Waiting("/music/slskd/incomplete/Album/01 Song.flac"),
            maxWait: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        // A layout where slskd never moves the file (or never does within the window)
        // must still resolve: worse a path that may move than no path at all.
        Assert.Equal("/music/slskd/incomplete/Album/01 Song.flac", result);
    }

    [Theory]
    [InlineData("/music/slskd/incomplete/Album/01 Song.flac", true)]
    [InlineData("/music/slskd/Incomplete/01 Song.flac", true)]
    [InlineData("/music/incomplete/song.flac", true)]
    [InlineData("/music/slskd/Album/01 Song.flac", false)]
    [InlineData("/music/Incomplete Collection/01 Song.flac", false)]
    [InlineData("/music/slskd/Album/incomplete.flac", false)]
    public void IncompleteDirectories_AreRecognisedByTheSegmentNotTheWord(string path, bool expected)
        => Assert.Equal(expected, SoulseekDownloadService.IsIncompleteLocation(path));
}

/// <summary>
/// A Windows drive-letter path configured inside a Linux container is silently
/// created as a literal directory name; the detector behind the startup warning
/// must catch that shape and nothing else.
/// </summary>
public class WindowsDrivePathDetectionTests
{
    [Theory]
    [InlineData(@"E:\Media\Music")]
    [InlineData("E:/Media/Music")]
    [InlineData(@"c:\music")]
    public void DrivePaths_AreDetected(string path)
        => Assert.True(PathHelper.LooksLikeWindowsDrivePath(path));

    [Theory]
    [InlineData("/music")]
    [InlineData("./downloads")]
    [InlineData("music")]
    [InlineData("E:")]
    [InlineData("")]
    [InlineData(null)]
    public void NonDrivePaths_AreNot(string? path)
        => Assert.False(PathHelper.LooksLikeWindowsDrivePath(path));
}
