using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Octo.Services.Common;
using Octo.Services.Soulseek;

namespace Octo.Services.CoverArt;

/// <summary>
/// Cover art via Apple's free iTunes Search API. No key, 1200x1200 JPEGs after
/// CDN substitution (and the full master for downloads, see TryFetchAlbumMasterAsync), very high coverage for mainstream Western releases —
/// weaker for international, indie, and underground.
///
/// Improvements over the original implementation:
/// - <c>country</c> filter dropped: US-only filtering kept missing releases that
///   are non-US-exclusive (lots of UK/EU/JP/KR catalog and re-releases).
/// - <c>limit</c> raised to 5: we then pick the result whose artist string
///   matches the routing best, instead of trusting iTunes' first hit blindly.
///   The first hit is often a "Karaoke Version" or different-artist cover that
///   shares the title.
/// - For routings whose Album is just the song title (the "single" convention
///   we use for placeholder songs), fall back from entity=album to entity=song
///   when the album-style query whiffs.
/// </summary>
public class ITunesCoverArtLookup : ICoverArtSource
{
    private readonly HttpClient _http;
    private readonly ILogger<ITunesCoverArtLookup> _logger;

    public string Name => "itunes";

    public ITunesCoverArtLookup(IHttpClientFactory httpClientFactory, ILogger<ITunesCoverArtLookup> logger)
    {
        _http = httpClientFactory.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(8);
        _logger = logger;
    }

    public async Task<byte[]?> TryFetchAsync(SoulseekRouting routing, bool background = false, CancellationToken ct = default)
    {
        var artist = (routing.Artist ?? "").Trim();
        if (routing.Kind == RoutingKind.Album)
        {
            var album = (routing.Album ?? routing.Title ?? "").Trim();
            if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(album)) return null;

            // Album-style lookup. For our placeholder "singles" the album is the
            // song title — iTunes may return a song-level hit shaped like an
            // album anyway, or fall through to song-entity fallback below.
            var hit = await SearchAndScoreAsync($"{artist} {album}", "album", artist, ct);
            if (hit != null) return await DownloadHiResAsync(hit, ct);

            return await SearchSongFallbackAsync(artist, album, ct);
        }
        if (routing.Kind == RoutingKind.Artist)
        {
            if (string.IsNullOrEmpty(artist)) return null;
            var hit = await SearchAndScoreAsync(artist, "musicArtist", artist, ct);
            return hit is null ? null : await DownloadHiResAsync(hit, ct);
        }
        // Song
        var title = (routing.Title ?? "").Trim();
        if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(title)) return null;
        var songHit = await SearchAndScoreAsync($"{artist} {title}", "song", artist, ct);
        return songHit is null ? null : await DownloadHiResAsync(songHit, ct);
    }

    /// <summary>The size asked of Apple's CDN for a master. It answers with the original when
    /// that is smaller, so this reads as "as large as there is".</summary>
    internal const int MasterSide = 3000;

    private static readonly SongMatchOptions AlbumTitles = new() { LengthToleranceSeconds = null };
    private static readonly Regex ReleaseSuffix = new(@"\s+-\s+(?:Single|EP)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly TimeSpan MasterUrlTtl = TimeSpan.FromHours(6);
    private readonly ConcurrentDictionary<string, (string? Url, DateTime At)> _masterUrls = new();

    /// <summary>Apple's search answers about 20 requests a minute from one address, and turns
    /// away more for a while. Every search and lookup this class sends for a master waits its
    /// turn here, however many albums are being worked on at once.</summary>
    internal static TimeSpan AppleInterval { get; set; } = TimeSpan.FromSeconds(3.2);
    private static readonly SemaphoreSlim AppleGate = new(1, 1);
    private static DateTime _appleNext = DateTime.MinValue;

    /// <summary>Barcodes asked in one lookup. 30 came back in under a second, 29 matched.</summary>
    internal const int UpcBatch = 40;

    private static async Task WaitForAppleAsync(CancellationToken ct)
    {
        await AppleGate.WaitAsync(ct);
        try
        {
            var wait = _appleNext - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _appleNext = DateTime.UtcNow + AppleInterval;
        }
        finally
        {
            AppleGate.Release();
        }
    }

    /// <summary>A search or lookup through the gate. A refusal (403 or 429) waits a minute and
    /// tries once more, rather than losing the album.</summary>
    private async Task<HttpResponseMessage?> AppleGetAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await WaitForAppleAsync(ct);
            var response = await _http.GetAsync(url, ct);
            if ((int)response.StatusCode is 403 or 429 && attempt == 0)
            {
                response.Dispose();
                _logger.LogInformation("Apple asked Octo to slow down; waiting a minute");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                continue;
            }
            return response;
        }
        return null;
    }

    private static string MasterKey(string artist, string release, bool single) =>
        SongIdentity.MatchKey(artist, release) + (single ? "|single" : "|album");

    /// <summary>
    /// Finds many albums' masters at once by barcode: one Apple lookup for up to
    /// <see cref="UpcBatch"/> albums, where a search costs one request an album. Each answer is
    /// matched back by artist and album name (Apple does not say which barcode it answered, and
    /// adds the odd stray), and kept where <see cref="TryFetchAlbumMasterAsync"/> looks first.
    /// Returns how many were matched; an album left out is found by search as before.
    /// </summary>
    public async Task<int> PrimeByBarcodeAsync(IReadOnlyList<(string Artist, string Album, string Upc)> albums,
        IProgress<int>? progress, CancellationToken ct)
    {
        var matched = 0;
        var done = 0;
        foreach (var batch in albums.Where(a => !string.IsNullOrWhiteSpace(a.Upc)).Chunk(UpcBatch))
        {
            try
            {
                var url = "https://itunes.apple.com/lookup?entity=album&limit=200&upc="
                    + string.Join(',', batch.Select(a => Uri.EscapeDataString(a.Upc.Trim())));
                using var resp = await AppleGetAsync(url, ct);
                if (resp is { IsSuccessStatusCode: true })
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    var collections = doc.RootElement.TryGetProperty("results", out var results)
                        ? results.EnumerateArray()
                            .Where(r => Text(r, "wrapperType") == "collection")
                            .Select(r => (Name: ReleaseSuffix.Replace(Text(r, "collectionName") ?? "", ""),
                                By: Text(r, "artistName"), Art: Text(r, "artworkUrl100"),
                                Clean: Text(r, "collectionExplicitness") == "cleaned"))
                            .Where(r => !string.IsNullOrEmpty(r.Art) && r.Art!.Contains("100x100bb"))
                            .ToList()
                        : [];
                    foreach (var album in batch)
                    {
                        var hit = collections
                            .Where(c => SongIdentity.Same(album.Album, album.Artist, c.Name, c.By, AlbumTitles).IsSame)
                            .OrderBy(c => c.Clean)
                            .Select(c => c.Art)
                            .FirstOrDefault();
                        if (hit is null) continue;
                        _masterUrls[MasterKey(album.Artist, album.Album, single: false)] = (hit, DateTime.UtcNow);
                        matched++;
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("iTunes barcode lookup failed: {M}", ex.Message);
            }
            done += batch.Length;
            progress?.Report(done);
        }
        return matched;
    }


    /// <summary>
    /// The album's own cover at the largest size Apple has, or null. Strict where
    /// <see cref="TryFetchAsync"/> is loose: the artist AND the album title must be the same
    /// release (a single is matched by its song), because this cover is written into files,
    /// where another album's art would be a wrong tag rather than a soft picture. The match is
    /// remembered for a few hours, so an album's tracks ask Apple once between them.
    /// </summary>
    public async Task<byte[]?> TryFetchAlbumMasterAsync(string? artist, string? album, string? title,
        CancellationToken ct = default)
    {
        artist = artist?.Trim();
        if (string.IsNullOrEmpty(artist)) return null;
        var single = string.IsNullOrWhiteSpace(album)
            || (!string.IsNullOrWhiteSpace(title) && SongIdentity.Same(album, artist, title, artist, AlbumTitles).IsSame);
        var release = (single ? title ?? album : album)?.Trim();
        if (string.IsNullOrEmpty(release)) return null;

        var key = MasterKey(artist, release, single);
        string? url;
        if (_masterUrls.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < MasterUrlTtl)
            url = known.Url;
        else
        {
            url = await FindMasterUrlAsync(artist, release, single, ct);
            if (_masterUrls.Count > 2000) _masterUrls.Clear();
            _masterUrls[key] = (url, DateTime.UtcNow);
        }
        if (url is null) return null;

        foreach (var side in new[] { MasterSide, 1200 })
        {
            var sized = url.Replace("100x100bb", $"{side}x{side}bb");
            try
            {
                using var resp = await _http.GetAsync(sized, ct);
                if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("iTunes master {Url} failed: {M}", sized, ex.Message);
            }
        }
        return null;
    }

    private async Task<string?> FindMasterUrlAsync(string artist, string release, bool single, CancellationToken ct)
    {
        // An album by its name; a single by its song, whose release iTunes names "Song - Single".
        var entity = single ? "song" : "album";
        try
        {
            var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString($"{artist} {release}")}&entity={entity}&limit=15";
            using var resp = await AppleGetAsync(url, ct);
            if (resp is not { IsSuccessStatusCode: true }) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("results", out var results)) return null;

            string? clean = null;
            foreach (var item in results.EnumerateArray())
            {
                var art = Text(item, "artworkUrl100");
                var by = Text(item, "artistName");
                var collection = ReleaseSuffix.Replace(Text(item, "collectionName") ?? "", "");
                if (string.IsNullOrEmpty(art) || !art.Contains("100x100bb") || string.IsNullOrEmpty(by)) continue;
                if (!SongIdentity.Same(release, artist, collection, by, AlbumTitles).IsSame) continue;
                if (single && !SongIdentity.Same(release, artist, Text(item, "trackName"), by, AlbumTitles).IsSame) continue;
                // The explicit and clean releases share a cover almost always; the explicit one
                // first, since that is the one a library usually holds.
                if (Text(item, "collectionExplicitness") == "cleaned") { clean ??= art; continue; }
                return art;
            }
            return clean;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("iTunes master search failed for {Artist} - {Release}: {M}", artist, release, ex.Message);
            return null;
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Issue a search and rank the up-to-5 results by closeness of the artist
    /// match, returning the artworkUrl100 of the best one. Without scoring,
    /// "Drake — Hold On, We're Going Home" would frequently come back as the
    /// karaoke version's cover when it appeared first in the index.
    /// </summary>
    private async Task<string?> SearchAndScoreAsync(string term, string entity, string expectedArtist, CancellationToken ct)
    {
        try
        {
            var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&entity={entity}&limit=5";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return null;

            string? bestUrl = null;
            int bestScore = int.MinValue;
            foreach (var item in results.EnumerateArray())
            {
                var artist = item.TryGetProperty("artistName", out var a) ? a.GetString() ?? "" : "";
                var artwork = item.TryGetProperty("artworkUrl100", out var aw) ? aw.GetString() : null;
                if (string.IsNullOrEmpty(artwork)) continue;

                var score = ScoreArtistMatch(expectedArtist, artist);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestUrl = artwork;
                }
            }
            return bestUrl;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "iTunes search failed for '{Term}'", term);
            return null;
        }
    }

    /// <summary>
    /// Singles often have no proper "album" entry on iTunes — the song exists
    /// but only as a track. Re-query with entity=song so we still get cover art.
    /// </summary>
    private async Task<byte[]?> SearchSongFallbackAsync(string artist, string albumOrTitle, CancellationToken ct)
    {
        var hit = await SearchAndScoreAsync($"{artist} {albumOrTitle}", "song", artist, ct);
        return hit is null ? null : await DownloadHiResAsync(hit, ct);
    }

    private async Task<byte[]?> DownloadHiResAsync(string artworkUrl100, CancellationToken ct)
    {
        // iTunes CDN serves arbitrary sizes by URL substring substitution. 1200x1200, so a
        // cover shown large (a phone's now playing screen, a desktop's full player) is
        // sharp; 600 was visibly soft there. Smaller covers are scaled down by the client.
        var hiRes = artworkUrl100.Replace("100x100bb", "1200x1200bb");
        try
        {
            using var resp = await _http.GetAsync(hiRes, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "iTunes artwork download failed for {Url}", hiRes);
            return null;
        }
    }

    /// <summary>
    /// Cheap case-insensitive substring score. Exact equality is best, then
    /// containment in either direction, then any token overlap. We don't need
    /// a real edit distance — we just need to push wrong-artist hits to the
    /// bottom and let a correct-artist hit win.
    /// </summary>
    private static int ScoreArtistMatch(string expected, string actual)
    {
        if (string.IsNullOrEmpty(actual)) return 0;
        var e = expected.Trim().ToLowerInvariant();
        var a = actual.Trim().ToLowerInvariant();
        if (a == e) return 100;
        if (a.Contains(e) || e.Contains(a)) return 60;
        var eTokens = e.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var aTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int overlap = eTokens.Count(t => aTokens.Contains(t));
        return overlap * 10;
    }
}
