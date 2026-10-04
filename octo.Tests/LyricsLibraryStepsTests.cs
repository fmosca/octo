using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.Lyrics;

namespace Octo.Tests;

/// <summary>
/// The lyrics page's steps on real files: a scan lists songs with no lyrics or weaker ones and
/// changes nothing, a preview looks up only the picked songs, Save writes only those, and Undo
/// puts back what Save wrote.
/// </summary>
public sealed class LyricsLibraryStepsTests : IDisposable
{
    private const string Words = "[00:01.00]<00:01.00>word <00:01.50>by word<00:02.00>";
    private const string Lines = "[00:01.00]line by line";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-lyrics-steps-" + Guid.NewGuid().ToString("N"));

    public LyricsLibraryStepsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "music"));
        LyricsLibraryWorker.Gap = TimeSpan.Zero;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Source(Func<LyricsQuery, LyricsResult?> answer) : ILyricsSource
    {
        public string Key => "kugou";
        public List<string> Asked { get; } = [];

        public Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct)
        {
            Asked.Add(query.Title);
            return Task.FromResult(answer(query) is { } found ? new LyricsLookup(found, false) : LyricsLookup.Miss);
        }
    }

    private string Song(string name, string? artist, string title, string? tagLyrics = null)
    {
        var path = Path.Combine(_root, "music", name);
        File.WriteAllBytes(path, AudioFixtures.Mp3());
        using var file = TagLib.File.Create(path);
        file.Tag.Performers = artist is null ? [] : [artist];
        file.Tag.Title = title;
        if (tagLyrics is not null) file.Tag.Lyrics = tagLyrics;
        file.Save();
        return path;
    }

    private static string? TagLyrics(string path)
    {
        using var file = TagLib.File.Create(path);
        return file.Tag.Lyrics;
    }

    private (LyricsLibraryWorker Worker, LyricsLibraryStore Store, Source Source) Worker(
        Func<LyricsQuery, LyricsResult?> answer, string saveTo = LyricsSaveTo.Beside)
    {
        var source = new Source(answer);
        var settings = TestOptions.Monitor(new MetadataSettings { FetchLyrics = true, LyricsSources = "song,kugou", SaveLyricsTo = saveTo });
        var writer = new LyricsSidecarWriter(new LyricsService([source], settings, NullLogger<LyricsService>.Instance),
            NullLogger<LyricsSidecarWriter>.Instance, settings);
        var store = new LyricsLibraryStore();
        var provider = new ServiceCollection().BuildServiceProvider();
        var worker = new LyricsLibraryWorker(store, writer, settings, provider.GetRequiredService<IServiceScopeFactory>(),
            () => Path.Combine(_root, "music"), NullLogger<LyricsLibraryWorker>.Instance, new LyricsUndoJournal());
        return (worker, store, source);
    }

    private static Task Step(LyricsLibraryWorker worker, LyricsLibraryMode mode, List<string>? picked = null) =>
        worker.RunAsync(new LyricsLibraryRequest(false, Mode: mode, Scope: "WholeLibrary", Picked: picked), CancellationToken.None);

    [Fact]
    public async Task Scan_ListsMissingAndWeakerLyrics_AndChangesNothing()
    {
        var none = Song("01 None.mp3", "Artist", "None");
        var line = Song("02 Line.mp3", "Artist", "Line", tagLyrics: Lines);
        var word = Song("03 Word.mp3", "Artist", "Word");
        File.WriteAllText(Path.ChangeExtension(word, ".lrc"), LyricsSidecarWriter.OctoMark + "\n" + Words + "\n");
        Song("04 Untagged.mp3", null, "Untagged");
        var (worker, store, source) = Worker(_ => new LyricsResult("KuGou", Words, null, false));

        await Step(worker, LyricsLibraryMode.Scan);

        var run = store.Current;
        Assert.Equal(LyricsLibraryStatus.Completed, run.Status);
        Assert.Equal([("None", "none"), ("Line", "line")], run.Rows.Select(row => (row.Title, row.Has)));
        Assert.Equal(1, run.WordAlready);
        Assert.Equal(1, run.Skipped);
        Assert.Empty(source.Asked);
        Assert.False(File.Exists(Path.ChangeExtension(none, ".lrc")));
        Assert.Equal(Lines, TagLyrics(line));
    }

    [Fact]
    public async Task PreviewSaveUndo_WorkOnlyOnThePickedSongs()
    {
        var none = Song("01 None.mp3", "Artist", "None");
        var line = Song("02 Line.mp3", "Artist", "Line", tagLyrics: Lines);
        var skipped = Song("03 Skipped.mp3", "Artist", "Skipped");
        var (worker, store, source) = Worker(_ => new LyricsResult("KuGou", Words, null, false));
        await Step(worker, LyricsLibraryMode.Scan);
        var ids = store.Current.Rows.ToDictionary(row => row.Title, row => row.Id);

        await Step(worker, LyricsLibraryMode.Preview, [ids["None"], ids["Line"]]);

        Assert.Equal(["None", "Line"], source.Asked);
        Assert.All(store.Current.Rows.Where(row => row.Title != "Skipped"), row =>
        {
            Assert.Equal("found", row.Result);
            Assert.Equal("word", row.Kind);
            Assert.Equal(["word by word"], row.Preview);
        });
        Assert.False(File.Exists(Path.ChangeExtension(none, ".lrc")));

        await Step(worker, LyricsLibraryMode.Save, [ids["None"], ids["Line"], ids["Skipped"]]);

        Assert.Equal(2, store.Current.Written);
        Assert.StartsWith(LyricsSidecarWriter.OctoMark, File.ReadAllText(Path.ChangeExtension(none, ".lrc")));
        // Someone else's lyrics in the tags stay, and the better ones go beside them.
        Assert.Equal(Lines, TagLyrics(line));
        Assert.StartsWith(LyricsSidecarWriter.OctoMark, File.ReadAllText(Path.ChangeExtension(line, ".lrc")));
        Assert.False(File.Exists(Path.ChangeExtension(skipped, ".lrc")));
        Assert.True(worker.CanUndo);

        await Step(worker, LyricsLibraryMode.Undo);

        Assert.False(File.Exists(Path.ChangeExtension(none, ".lrc")));
        Assert.False(File.Exists(Path.ChangeExtension(line, ".lrc")));
        Assert.Equal(Lines, TagLyrics(line));
        Assert.False(worker.CanUndo);
        Assert.Equal(2, store.Current.Written);
    }

    [Fact]
    public async Task SaveInside_ThenUndo_PutsTheTagsBack()
    {
        var song = Song("01 None.mp3", "Artist", "None");
        var (worker, store, _) = Worker(_ => new LyricsResult("KuGou", Words, null, false), LyricsSaveTo.Inside);
        await Step(worker, LyricsLibraryMode.Scan);
        var id = store.Current.Rows.Single().Id;
        await Step(worker, LyricsLibraryMode.Preview, [id]);

        await Step(worker, LyricsLibraryMode.Save, [id]);

        Assert.StartsWith(LyricsSidecarWriter.OctoMark, TagLyrics(song));
        Assert.False(File.Exists(Path.ChangeExtension(song, ".lrc")));

        await Step(worker, LyricsLibraryMode.Undo);

        Assert.True(string.IsNullOrEmpty(TagLyrics(song)));
    }

    [Fact]
    public async Task Preview_NothingBetter_IsNotOfferedForSaving()
    {
        Song("01 Line.mp3", "Artist", "Line", tagLyrics: Lines);
        var (worker, store, _) = Worker(_ => new LyricsResult("LRCLIB", "[00:01.00]other lines", null, false));
        await Step(worker, LyricsLibraryMode.Scan);
        var id = store.Current.Rows.Single().Id;

        await Step(worker, LyricsLibraryMode.Preview, [id]);
        await Step(worker, LyricsLibraryMode.Save, [id]);

        Assert.Equal("none", store.Current.Rows.Single().Result);
        Assert.Equal(0, store.Current.Written);
        Assert.False(worker.CanUndo);
    }

    [Fact]
    public async Task Undo_LeavesAFileThatChangedSinceAlone()
    {
        var song = Song("01 None.mp3", "Artist", "None");
        var (worker, store, _) = Worker(_ => new LyricsResult("KuGou", Words, null, false));
        await Step(worker, LyricsLibraryMode.Scan);
        var id = store.Current.Rows.Single().Id;
        await Step(worker, LyricsLibraryMode.Preview, [id]);
        await Step(worker, LyricsLibraryMode.Save, [id]);
        File.WriteAllText(Path.ChangeExtension(song, ".lrc"), "[00:01.00]the owner's own now\n");

        await Step(worker, LyricsLibraryMode.Undo);

        Assert.Equal("[00:01.00]the owner's own now\n", File.ReadAllText(Path.ChangeExtension(song, ".lrc")));
        Assert.Equal(1, store.Current.Skipped);
    }
}
