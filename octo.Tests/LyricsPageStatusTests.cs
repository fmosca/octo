using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octo.Services.Library;
using Octo.Services.Lyrics;

namespace Octo.Tests;

/// <summary>
/// The lyrics page's status, asked the way the dashboard asks it. It once could not be written at
/// all (two properties named "busy"), so the page showed nothing while a scan ran; the cover
/// page's status is asked too, as the same kind of page.
/// </summary>
public sealed class LyricsPageStatusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octo-lyrics-status-" + Guid.NewGuid().ToString("N"));

    public LyricsPageStatusTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static WebApplicationFactory<Program> Factory(LyricsLibraryStore store) =>
        new AdminWebFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            // In memory: the real ones live beside the settings file.
            services.RemoveAll<LyricsLibraryStore>();
            services.AddSingleton(store);
            services.RemoveAll<LyricsUndoJournal>();
            services.AddSingleton(new LyricsUndoJournal());
        }));

    private static async Task<HttpResponseMessage> Get(WebApplicationFactory<Program> factory, string url)
    {
        var token = factory.Services.GetRequiredService<Octo.Services.Admin.BrowseSessionStore>().Create("admin");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Octo-Browse-Token", token);
        return await factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task LyricsStatus_WithRowsAndABusyCount_IsWritten()
    {
        var store = new LyricsLibraryStore();
        store.Replace(new LyricsLibraryRun
        {
            RunId = "r1", Status = LyricsLibraryStatus.Completed, Mode = LyricsLibraryMode.Preview, Busy = 2,
            Rows = [new LyricsLibraryRow { Id = "a", Path = "/music/a.mp3", Artist = "A", Title = "T", Result = "found",
                Kind = "word", Source = "KuGou", Preview = ["first"], FoundSynced = "[00:01.00]secret text" }],
        });
        await using var factory = Factory(store);

        using var response = await Get(factory, "/api/admin/lyrics/library");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(2, json.RootElement.GetProperty("busy").GetInt32());
        Assert.False(json.RootElement.GetProperty("running").GetBoolean());
        Assert.Equal("found", json.RootElement.GetProperty("rows")[0].GetProperty("result").GetString());
        // The found lyrics stay on the server.
        Assert.DoesNotContain("secret text", body);
    }

    [Fact]
    public async Task CoverStatus_IsWritten()
    {
        await using var factory = Factory(new LyricsLibraryStore());

        using var response = await Get(factory, "/api/admin/covers/upgrade");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- the scan reads Navidrome's list, not every file ------------------------------------

    [Fact]
    public void Scan_ASongNavidromeNamesWithNoLyrics_IsListedWithoutOpeningIt()
    {
        // The file does not exist: opening it would fail, so a row proves it was not opened.
        var path = Path.Combine(_root, "Artist - Song.flac");
        var known = new NavidromeSongEntry(path, "al-1", "Artist", "Song", "Album", "[]");

        var (row, word) = LyricsLibraryWorker.ScanSong(path, known, lyricsFiles: []);

        Assert.False(word);
        Assert.Equal(("Artist", "Song", "Album", "none"), (row!.Artist, row.Title, row.Album, row.Has));
    }

    [Fact]
    public void Scan_ASongWithLyricsBesideIt_IsOpenedToLearnTheirTiming()
    {
        var path = Path.Combine(_root, "Artist - Song.mp3");
        File.WriteAllBytes(path, AudioFixtures.Mp3());
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Performers = ["Artist"];
            file.Tag.Title = "Song";
            file.Save();
        }
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:01.00]<00:01.00>word<00:02.00>\n");
        var known = new NavidromeSongEntry(path, "al-1", "Artist", "Song", null, null);

        var stems = LyricsLibraryWorker.LyricsFileStems(_root);
        var (row, word) = LyricsLibraryWorker.ScanSong(path, known, stems);

        Assert.Contains(Path.Combine(Path.GetFullPath(_root), "Artist - Song"), stems!);
        Assert.Null(row);
        Assert.True(word);
    }

    [Fact]
    public void Scan_TagLyricsNavidromeRead_AreOpened()
    {
        Assert.True(new NavidromeSongEntry("/x", null, "A", "T", null, "[{\"synced\":true}]").HasTagLyrics);
        Assert.False(new NavidromeSongEntry("/x", null, "A", "T", null, "[]").HasTagLyrics);
        Assert.False(new NavidromeSongEntry("/x", null, "A", "T", null, null).HasTagLyrics);
    }
}
