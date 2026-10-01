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
public class ITunesCoverArtLookup : ICoverArtSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<ITunesCoverArtLookup> _logger;

    public string Name => "itunes";

    public ITunesCoverArtLookup(IHttpClientFactory httpClientFactory, ILogger<ITunesCoverArtLookup> logger,
        string? cachePath = null)
    {
        _http = httpClientFactory.CreateClient();
        // A master at full size runs to a few megabytes; 8 s was cut close on a slow line.
        _http.Timeout = TimeSpan.FromSeconds(20);
        _logger = logger;
        _cachePath = string.IsNullOrWhiteSpace(cachePath) ? null : cachePath;
        if (_cachePath is null) return;
        LoadCache();
        _flushTimer = new Timer(_ => FlushCache(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
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
    /// that is smaller, so this reads as "as large as there is". 5000, as sacad asks: 3000
    /// capped the masters that are larger.</summary>
    internal const int MasterSide = 5000;

    /// <summary>The tile a preview shows, made by Apple, so a preview never downloads a master.</summary>
    internal const int ProbeThumbSide = 320;

    private static readonly SongMatchOptions AlbumTitles = new() { LengthToleranceSeconds = null };
    private static readonly Regex ReleaseSuffix = new(@"\s+-\s+(?:Single|EP)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    /// <summary>How long a match is trusted, and a miss (a search may find it later).</summary>
    private static readonly TimeSpan MasterUrlTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MasterMissTtl = TimeSpan.FromDays(1);
    private const int MasterUrlCap = 20_000;
    private readonly ConcurrentDictionary<string, (string? Url, DateTime At)> _masterUrls = new();

    /// <summary>Matches kept on disk, so a restart does not send a whole library back to
    /// Apple. Written at most every 15 seconds.</summary>
    private readonly string? _cachePath;
    private readonly Timer? _flushTimer;
    private int _dirty;

    /// <summary>
    /// Every search and lookup this class sends for a master waits its turn here, however many
    /// albums are being worked on at once. Apple documents about 20 a minute, but answered 30
    /// searches at 2 a second on 2026-10-01, and sacad asks up to 10 a second; so one a second,
    /// doubling (to 8 s at most) each time Apple refuses, and halving again after 50 answers
    /// in a row.
    /// </summary>
    internal static TimeSpan AppleInterval { get; set; } = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AppleIntervalMax = TimeSpan.FromSeconds(8);
    private static readonly SemaphoreSlim AppleGate = new(1, 1);
    private static DateTime _appleNext = DateTime.MinValue;
    private static TimeSpan _appleBackoff = TimeSpan.Zero;
    private static int _appleAnswered;

    /// <summary>Barcodes asked in one lookup. 30 came back in under a second, 29 matched.</summary>
    internal const int UpcBatch = 40;

    private static async Task WaitForAppleAsync(CancellationToken ct)
    {
        await AppleGate.WaitAsync(ct);
        try
        {
            var wait = _appleNext - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            var interval = _appleBackoff > AppleInterval ? _appleBackoff : AppleInterval;
            _appleNext = DateTime.UtcNow + interval;
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
            if ((int)response.StatusCode is 403 or 429)
            {
                // Slower from now on, for every album, not just this one.
                _appleBackoff = TimeSpan.FromTicks(Math.Min(AppleIntervalMax.Ticks,
                    Math.Max(AppleInterval.Ticks, _appleBackoff.Ticks) * 2));
                Interlocked.Exchange(ref _appleAnswered, 0);
                if (attempt > 0) return response;
                response.Dispose();
                _logger.LogInformation("Apple asked Octo to slow down; waiting a minute, then one every {Seconds} s",
                    _appleBackoff.TotalSeconds);
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                continue;
            }
            if (_appleBackoff > TimeSpan.Zero && Interlocked.Increment(ref _appleAnswered) >= 50)
            {
                Interlocked.Exchange(ref _appleAnswered, 0);
                _appleBackoff = _appleBackoff / 2 <= AppleInterval ? TimeSpan.Zero : _appleBackoff / 2;
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
                // A file's tag often has the 13-digit EAN ("0602475682233") where the store
                // keeps the 12-digit UPC; both are sent, and the answers matched back by name.
                var codes = batch.SelectMany(a => BarcodeForms(a.Upc)).Distinct(StringComparer.Ordinal);
                var url = "https://itunes.apple.com/lookup?entity=album&limit=200&upc="
                    + string.Join(',', codes.Select(Uri.EscapeDataString));
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
                        // Kept for both ways a song can ask: as its album, and as a single
                        // named after itself. A barcode names one release either way.
                        Remember(MasterKey(album.Artist, album.Album, single: false), hit);
                        Remember(MasterKey(album.Artist, album.Album, single: true), hit);
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
        if (await MasterUrlAsync(artist, album, title, ct) is not { } url) return null;
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

    /// <summary>
    /// What the master is, without downloading it: its size from the first 64 KB (Apple
    /// answers range requests) and Apple's own 320 px copy for a tile. A preview of a thousand
    /// albums is about 120 KB each this way, where whole masters were about 3.4 MB each.
    /// </summary>
    public async Task<(int Side, byte[] Thumb)?> TryProbeAlbumMasterAsync(string? artist, string? album, string? title,
        CancellationToken ct = default)
    {
        if (await MasterUrlAsync(artist, album, title, ct) is not { } url) return null;
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Get, url.Replace("100x100bb", $"{MasterSide}x{MasterSide}bb"));
            head.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 65_535);
            using var first = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!first.IsSuccessStatusCode) return null;
            var start = await ReadUpToAsync(await first.Content.ReadAsStreamAsync(ct), 65_536, ct);
            if (CoverImage.Measure(start) is not { } size) return null;

            using var tile = await _http.GetAsync(url.Replace("100x100bb", $"{ProbeThumbSide}x{ProbeThumbSide}bb"), ct);
            if (!tile.IsSuccessStatusCode) return null;
            return (Math.Min(size.Width, size.Height), await tile.Content.ReadAsByteArrayAsync(ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("iTunes master probe failed for {Artist} - {Album}: {M}", artist, album, ex.Message);
            return null;
        }
    }

    private static async Task<byte[]> ReadUpToAsync(Stream stream, int limit, CancellationToken ct)
    {
        await using var _ = stream;
        var buffer = new byte[limit];
        var read = 0;
        while (read < limit)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, limit - read), ct);
            if (n == 0) break;
            read += n;
        }
        return buffer[..read];
    }

    /// <summary>The album's master URL at 100 px (sizes are swapped into it), from the
    /// remembered matches or one search.</summary>
    private async Task<string?> MasterUrlAsync(string? artist, string? album, string? title, CancellationToken ct)
    {
        artist = artist?.Trim();
        if (string.IsNullOrEmpty(artist)) return null;
        var single = string.IsNullOrWhiteSpace(album)
            || (!string.IsNullOrWhiteSpace(title) && SongIdentity.Same(album, artist, title, artist, AlbumTitles).IsSame);
        var release = (single ? title ?? album : album)?.Trim();
        if (string.IsNullOrEmpty(release)) return null;

        var key = MasterKey(artist, release, single);
        if (_masterUrls.TryGetValue(key, out var known)
            && DateTime.UtcNow - known.At < (known.Url is null ? MasterMissTtl : MasterUrlTtl))
            return known.Url;
        var url = await FindMasterUrlAsync(artist, release, single, ct);
        Remember(key, url);
        return url;
    }

    private void Remember(string key, string? url)
    {
        if (_masterUrls.Count > MasterUrlCap) _masterUrls.Clear();
        _masterUrls[key] = (url, DateTime.UtcNow);
        Interlocked.Exchange(ref _dirty, 1);
    }

    private sealed record CachedMaster(string Key, string? Url, DateTime At);

    private void LoadCache()
    {
        try
        {
            if (_cachePath is null || !File.Exists(_cachePath)) return;
            var rows = JsonSerializer.Deserialize<List<CachedMaster>>(File.ReadAllText(_cachePath)) ?? [];
            foreach (var row in rows.Where(r => DateTime.UtcNow - r.At < (r.Url is null ? MasterMissTtl : MasterUrlTtl)))
                _masterUrls[row.Key] = (row.Url, row.At);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("iTunes match cache could not be read: {M}", ex.Message);
        }
    }

    private void FlushCache()
    {
        if (_cachePath is null || Interlocked.Exchange(ref _dirty, 0) == 0) return;
        try
        {
            var rows = _masterUrls.Select(pair => new CachedMaster(pair.Key, pair.Value.Url, pair.Value.At)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath + ".tmp", JsonSerializer.Serialize(rows));
            File.Move(_cachePath + ".tmp", _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _dirty, 1);
            _logger.LogDebug("iTunes match cache could not be written: {M}", ex.Message);
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        FlushCache();
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

    /// <summary>A barcode as given, and as the 12-digit UPC when it is a longer form with
    /// leading zeros. Only digits count; anything else is not a barcode.</summary>
    internal static IEnumerable<string> BarcodeForms(string? code)
    {
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length is < 8 or > 14) yield break;
        yield return digits;
        var upc = digits.TrimStart('0').PadLeft(12, '0');
        if (digits.Length > 12 && upc.Length == 12 && upc != digits) yield return upc;
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
