using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.Lyrics;

namespace Octo.Tests;

/// <summary>
/// The lyrics a song already has (its tags, or a file beside it) rank among the sources as
/// "song", and found lyrics are saved beside the song, inside it, or both. Octo replaces only
/// lyrics it wrote.
/// </summary>
public sealed class LyricsSongSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-song-lyrics-" + Guid.NewGuid().ToString("N"));

    public LyricsSongSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Source(string key, LyricsResult? found) : ILyricsSource
    {
        public string Key => key;
        public int Calls { get; private set; }

        public Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(found is null ? LyricsLookup.Miss : new LyricsLookup(found, false));
        }
    }

    private const string Words = "[00:01.00]<00:01.00>word <00:01.50>by word<00:02.00>";
    private const string Lines = "[00:01.00]line by line";
    private static readonly LyricsQuery Query = new("Artist", "Song", null, 200);

    private static LyricsService Service(string order, bool preferWords, params ILyricsSource[] sources) =>
        new(sources, TestOptions.Monitor(new MetadataSettings { LyricsSources = order, PreferWordTimedLyrics = preferWords }),
            NullLogger<LyricsService>.Instance);

    // ---- The order ---------------------------------------------------------------------------

    [Fact]
    public void Order_SavedWithoutTheSong_PutsItFirst()
    {
        Assert.Equal(["song", "kugou", "lrclib"], new MetadataSettings { LyricsSources = "kugou,lrclib" }.EffectiveLyricsSources);
        Assert.Equal(["kugou", "song", "lrclib"], new MetadataSettings { LyricsSources = "kugou,song,lrclib" }.EffectiveLyricsSources);
        Assert.Equal(["song", "kugou", "lrclib", "lyricsovh"], new MetadataSettings().EffectiveLyricsSources);
    }

    [Fact]
    public async Task Ranked_LineTimedOwnLyricsFirst_LoseToWordsWhenWordsArePreferred()
    {
        var kugou = new Source("kugou", new LyricsResult("KuGou", Words, null, false));

        var lookup = await Service("song,kugou", true, kugou).FindAsync(Query, CancellationToken.None, LyricsTiming.Line);

        Assert.Equal("KuGou", lookup.Result!.Source);
    }

    [Fact]
    public async Task Ranked_LineTimedOwnLyricsFirst_StandWhenWordsAreNotPreferred()
    {
        var kugou = new Source("kugou", new LyricsResult("KuGou", Words, null, false));

        var lookup = await Service("song,kugou", false, kugou).FindAsync(Query, CancellationToken.None, LyricsTiming.Line);

        Assert.True(lookup.Result!.IsSongsOwn);
        Assert.Equal(0, kugou.Calls);
    }

    [Fact]
    public async Task Ranked_WordTimedOwnLyrics_EndTheSearch()
    {
        var kugou = new Source("kugou", new LyricsResult("KuGou", Words, null, false));

        var lookup = await Service("song,kugou", true, kugou).FindAsync(Query, CancellationToken.None, LyricsTiming.Word);

        Assert.True(lookup.Result!.IsSongsOwn);
        Assert.Equal(0, kugou.Calls);
    }

    [Fact]
    public async Task Ranked_ASourceAboveTheSong_WinsAtTheSameTiming()
    {
        var lrclib = new Source("lrclib", new LyricsResult("LRCLIB", Lines, null, false));

        var lookup = await Service("lrclib,song", false, lrclib).FindAsync(Query, CancellationToken.None, LyricsTiming.Line);

        Assert.Equal("LRCLIB", lookup.Result!.Source);
    }

    [Fact]
    public async Task Ranked_PlainOwnLyrics_LoseToTimedOnesBelowThem()
    {
        var lrclib = new Source("lrclib", new LyricsResult("LRCLIB", Lines, null, false));

        var lookup = await Service("song,lrclib", false, lrclib).FindAsync(Query, CancellationToken.None, LyricsTiming.Plain);

        Assert.Equal("LRCLIB", lookup.Result!.Source);
    }

    [Fact]
    public async Task Ranked_NothingBetterFound_TheSongsOwnStand()
    {
        var lrclib = new Source("lrclib", null);

        var lookup = await Service("song,lrclib", true, lrclib).FindAsync(Query, CancellationToken.None, LyricsTiming.Line);

        Assert.True(lookup.Result!.IsSongsOwn);
        Assert.Equal(LyricsTiming.Line, lookup.Result.Timing);
    }

    // ---- Navidrome's answer ------------------------------------------------------------------

    [Fact]
    public void NavidromeTiming_ReadsCuesLinesAndPlain()
    {
        static byte[] Answer(string entries) => System.Text.Encoding.UTF8.GetBytes(
            """{"subsonic-response":{"status":"ok","lyricsList":{"structuredLyrics":[""" + entries + "]}}}");

        Assert.Equal(LyricsTiming.None, Octo.Controllers.SubsonicController.NavidromeLyricsTiming(Answer("")));
        Assert.Equal(LyricsTiming.Plain, Octo.Controllers.SubsonicController.NavidromeLyricsTiming(
            Answer("""{"synced":false,"line":[{"value":"a"}]}""")));
        Assert.Equal(LyricsTiming.Line, Octo.Controllers.SubsonicController.NavidromeLyricsTiming(
            Answer("""{"synced":true,"line":[{"start":0,"value":"a"}]}""")));
        Assert.Equal(LyricsTiming.Word, Octo.Controllers.SubsonicController.NavidromeLyricsTiming(
            Answer("""{"synced":true,"kind":"main","line":[{"start":0,"value":"a"}],"cueLine":[{"index":0,"cue":[]}]}""")));
    }

    [Fact]
    public void WithoutCues_DropsTheCueLinesAndKindOnly()
    {
        var body = System.Text.Encoding.UTF8.GetBytes(
            """{"subsonic-response":{"status":"ok","lyricsList":{"structuredLyrics":[{"synced":true,"kind":"main","line":[{"start":0,"value":"a"}],"cueLine":[{"index":0}]}]}}}""");

        var text = System.Text.Encoding.UTF8.GetString(Octo.Controllers.SubsonicController.WithoutCues(body));

        Assert.DoesNotContain("cueLine", text);
        Assert.DoesNotContain("kind", text);
        Assert.Contains("\"value\":\"a\"", text);
    }

    // ---- Saving --------------------------------------------------------------------------------

    private string Mp3(string name = "Artist - Song.mp3", string? tagLyrics = null)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, AudioFixtures.Mp3());
        if (tagLyrics is not null)
        {
            using var file = TagLib.File.Create(path);
            file.Tag.Lyrics = tagLyrics;
            file.Save();
        }
        return path;
    }

    private static string? TagLyrics(string path)
    {
        using var file = TagLib.File.Create(path);
        return file.Tag.Lyrics;
    }

    private static LyricsSidecarWriter Writer(string saveTo, LyricsResult found, string order = "song,kugou") =>
        new(new LyricsService([new Source("kugou", found)],
                TestOptions.Monitor(new MetadataSettings { LyricsSources = order }), NullLogger<LyricsService>.Instance),
            NullLogger<LyricsSidecarWriter>.Instance,
            TestOptions.Monitor(new MetadataSettings { SaveLyricsTo = saveTo }));

    private static LyricsJob Job(string path) => new(path, "Artist", "Song", null, 200);

    [Fact]
    public async Task Save_Inside_WritesTheTagsMarkedAsOctos_AndNoFile()
    {
        var song = Mp3();

        var write = await Writer(LyricsSaveTo.Inside, new LyricsResult("KuGou", Words, null, false))
            .WriteAsync(Job(song), upgrade: false, CancellationToken.None);

        Assert.Equal(LyricsWriteOutcome.Written, write.Outcome);
        Assert.Equal(LyricsSidecarWriter.OctoMark + "\n" + Words, TagLyrics(song)?.Replace("\r\n", "\n"));
        Assert.False(File.Exists(Path.ChangeExtension(song, ".lrc")));
        Assert.Equal(new SongLyrics(SongLyricsPlace.Inside, LyricsTiming.Word, true, false), SongLyrics.Of(song));
    }

    [Fact]
    public async Task Save_Both_WritesTheTagsAndAFile()
    {
        var song = Mp3();

        await Writer(LyricsSaveTo.Both, new LyricsResult("KuGou", Words, null, false))
            .WriteAsync(Job(song), upgrade: false, CancellationToken.None);

        Assert.StartsWith(LyricsSidecarWriter.OctoMark, TagLyrics(song));
        Assert.StartsWith(LyricsSidecarWriter.OctoMark, File.ReadAllText(Path.ChangeExtension(song, ".lrc")));
    }

    [Fact]
    public async Task Upgrade_SomeoneElsesTagLyrics_StayAndBetterOnesGoBeside()
    {
        var song = Mp3(tagLyrics: Lines);

        var write = await Writer(LyricsSaveTo.Inside, new LyricsResult("KuGou", Words, null, false))
            .WriteAsync(Job(song), upgrade: true, CancellationToken.None);

        Assert.Equal(LyricsWriteOutcome.Upgraded, write.Outcome);
        Assert.Equal(Lines, TagLyrics(song));
        Assert.Equal(LyricsSidecarWriter.OctoMark + "\n" + Words + "\n", File.ReadAllText(Path.ChangeExtension(song, ".lrc")));
    }

    [Fact]
    public async Task Upgrade_OctosOwnTagLyrics_AreReplacedInPlace()
    {
        var song = Mp3(tagLyrics: LyricsSidecarWriter.OctoMark + "\n" + Lines);

        var write = await Writer(LyricsSaveTo.Inside, new LyricsResult("KuGou", Words, null, false))
            .WriteAsync(Job(song), upgrade: true, CancellationToken.None);

        Assert.Equal(LyricsWriteOutcome.Upgraded, write.Outcome);
        Assert.Equal(LyricsSidecarWriter.OctoMark + "\n" + Words, TagLyrics(song)?.Replace("\r\n", "\n"));
        Assert.False(File.Exists(Path.ChangeExtension(song, ".lrc")));
    }

    [Fact]
    public async Task Upgrade_NothingBetter_LeavesTheSongAlone()
    {
        var song = Mp3(tagLyrics: Lines);

        var write = await Writer(LyricsSaveTo.Beside, new LyricsResult("LRCLIB", "[00:01.00]other lines", null, false))
            .WriteAsync(Job(song), upgrade: true, CancellationToken.None);

        Assert.Equal(LyricsWriteOutcome.AlreadyThere, write.Outcome);
        Assert.False(File.Exists(Path.ChangeExtension(song, ".lrc")));
    }

    [Fact]
    public async Task Upgrade_PlainTagLyrics_GetTimedOnesBeside()
    {
        var song = Mp3(tagLyrics: "just the words");

        var write = await Writer(LyricsSaveTo.Beside, new LyricsResult("LRCLIB", Lines, null, false))
            .WriteAsync(Job(song), upgrade: true, CancellationToken.None);

        Assert.Equal(LyricsWriteOutcome.Upgraded, write.Outcome);
        Assert.Equal("just the words", TagLyrics(song));
        Assert.True(File.Exists(Path.ChangeExtension(song, ".lrc")));
    }

    [Fact]
    public async Task NoUpgrade_ASongWithLyrics_IsNeverLookedUp()
    {
        var song = Mp3(tagLyrics: Lines);
        var kugou = new Source("kugou", new LyricsResult("KuGou", Words, null, false));
        var writer = new LyricsSidecarWriter(new LyricsService([kugou],
                TestOptions.Monitor(new MetadataSettings { LyricsSources = "kugou" }), NullLogger<LyricsService>.Instance),
            NullLogger<LyricsSidecarWriter>.Instance);

        var write = await writer.WriteAsync(Job(song), upgrade: false, CancellationToken.None);

        Assert.Equal(LyricsWriteOutcome.AlreadyThere, write.Outcome);
        Assert.Equal(0, kugou.Calls);
    }

    [Fact]
    public void SaveTo_AnythingUnknown_IsBeside()
    {
        Assert.Equal(LyricsSaveTo.Beside, LyricsSaveTo.Normalize("sideways"));
        Assert.Equal(LyricsSaveTo.Inside, LyricsSaveTo.Normalize(" INSIDE "));
        Assert.True(new MetadataSettings().SavesLyricsBeside);
        Assert.False(new MetadataSettings().SavesLyricsInside);
    }
}
