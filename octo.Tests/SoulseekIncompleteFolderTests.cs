using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// slskd marks a transfer Succeeded before moving it out of its incomplete folder, and a
/// full-size copy there used to be taken as the download, then deleted under Octo (#69).
/// </summary>
public sealed class SoulseekIncompleteFolderTests : IDisposable
{
    private const string Remote = @"Music\Artist\Album\13 - Song.flac";
    private const string Leaf = "13 - Song.flac";
    private const int Size = 200_000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-incomplete-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string relative, int size = Size)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private string? Resolve(string remote = Remote, IReadOnlyCollection<string>? excluded = null) =>
        SoulseekDownloadService.ResolveLocalPath(remote, Size, false, [_root],
            excluded ?? [SoulseekDownloadService.DefaultIncompleteFolderName]);

    [Fact]
    public void AFullSizeCopyInTheIncompleteFolderIsNeverTheAnswer()
    {
        var partial = Write(Path.Combine("slskd", "incomplete", "peer", "Music", "Artist", "Album", Leaf));
        Assert.Null(Resolve());
        // The same file with nothing excluded is found, so the null above is the exclusion at work.
        Assert.Equal(partial, Resolve(excluded: []));
    }

    [Fact]
    public void AnIncompleteFolderNamedBySlskdIsExcludedToo()
    {
        Write(Path.Combine("slskd", "partial", "peer", "Music", "Artist", "Album", Leaf));
        Assert.Null(Resolve(excluded: SoulseekDownloadService.ExcludedFolderNames(@"D:\slskd\partial\")));
    }

    [Fact]
    public void APeersOwnFolderCalledIncompleteIsStillFound()
    {
        var final = Write(Path.Combine("slskd", "incomplete", Leaf));
        Assert.Equal(final, Resolve(@"Music\Artist\incomplete\13 - Song.flac"));
    }

    [Theory]
    [InlineData(null, new[] { "incomplete" })]
    [InlineData("/app/incomplete", new[] { "incomplete" })]
    [InlineData(@"D:\slskd\Partial\", new[] { "incomplete", "Partial" })]
    public void ExcludedNames(string? configured, string[] expected) =>
        Assert.Equal(expected, SoulseekDownloadService.ExcludedFolderNames(configured));

    [Fact]
    public async Task TheWaitOutlastsSlskdsMoveAndReturnsTheFinalPath()
    {
        var partial = Write(Path.Combine("slskd", "incomplete", "peer", "Music", "Artist", "Album", Leaf));
        var final = Path.Combine(_root, "slskd", "Album", Leaf);
        var mover = Task.Run(async () =>
        {
            await Task.Delay(50);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            File.Move(partial, final);
            Directory.Delete(Path.GetDirectoryName(partial)!); // slskd removes the emptied folder
        });

        var result = await SoulseekDownloadService.RetryResolveAsync(
            () => SoulseekDownloadService.ResolveLocalPath(Remote, Size, false, [_root], ["incomplete"]),
            maxWait: TimeSpan.FromSeconds(5), pollInterval: TimeSpan.FromMilliseconds(10), CancellationToken.None);
        await mover;

        Assert.Equal(final, result);
        Assert.True(File.Exists(result));
    }

    [Fact]
    public void ACopySlskdRenamedOnAClashIsFound()
    {
        // slskd names the newcomer <name>_<DateTime.UtcNow.Ticks><ext> when the name is taken.
        Write(Path.Combine("slskd", "Album", Leaf), size: 1_000); // the older file that took the name
        var renamed = Write(Path.Combine("slskd", "Album", "13 - Song_638950000000000000.flac"));
        Assert.Equal(renamed, Resolve());
    }

    [Fact]
    public void ANameThatOnlyLooksRenamedIsNot()
    {
        Write(Path.Combine("slskd", "Album", "13 - Song_2.flac"));
        Write(Path.Combine("slskd", "Other", "13 - Song_638950000000000000.flac"));
        Assert.Null(Resolve());
    }
}
