using System.Collections.Concurrent;
using Octo.Services.Common;
using Octo.Services.Metadata;

namespace Octo.Services.CoverArt;

/// <summary>What is known about an album from its files' tags.</summary>
public sealed record AlbumCoverQuery(string Artist, string? Album, string? Title,
    string? MusicBrainzReleaseId = null, string? MusicBrainzReleaseGroupId = null);

/// <summary>A cover found for an album, with its size.</summary>
public sealed record FoundCover(byte[] Bytes, string Source, int Side);

/// <summary>Finds the largest cover of one album. An interface so the upgrade can be tested
/// without the network.</summary>
public interface IAlbumCoverFinder
{
    Task<FoundCover?> FindAsync(AlbumCoverQuery query, CancellationToken ct);

    /// <summary>
    /// Gets ready for many albums at once, before <see cref="FindAsync"/> is asked about each:
    /// what can be matched in bulk is matched here, and what it is doing is reported in words.
    /// Optional; an album it did not reach is found the slow way.
    /// </summary>
    Task PrimeAsync(IReadOnlyList<AlbumCoverQuery> albums, IProgress<string>? status, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>
/// The largest cover of one album, for the cover upgrade. Every source must name the same
/// release, because what this finds is written into the owner's files: Apple's master on a
/// strict artist and album match, the Cover Art Archive by the MusicBrainz release the files
/// already name, and the catalog's album by the same strict match. All three are asked at once
/// and the largest wins.
///
/// Apple answers about 20 searches a minute, which made a thousand albums an hour. So a run
/// over many albums is primed first: each album's barcode from Deezer (which answers far more),
/// then one Apple lookup per 40 barcodes. Albums matched there skip Apple's search.
/// </summary>
public sealed class AlbumCoverFinder : IAlbumCoverFinder
{
    private static readonly SongMatchOptions AlbumTitles = new() { LengthToleranceSeconds = null };
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(15);
    private const int PrimeParallelism = 4;

    private readonly ITunesCoverArtLookup _itunes;
    private readonly CoverArtArchiveLookup _archive;
    private readonly DeezerMetadataService _deezer;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AlbumCoverFinder> _logger;

    /// <summary>The catalog's album found while priming, so its cover needs no second search.</summary>
    private readonly ConcurrentDictionary<string, DeezerMetadataService.AlbumHit?> _catalog = new();

    public AlbumCoverFinder(ITunesCoverArtLookup itunes, CoverArtArchiveLookup archive,
        DeezerMetadataService deezer, IHttpClientFactory http, ILogger<AlbumCoverFinder> logger)
    {
        _itunes = itunes;
        _archive = archive;
        _deezer = deezer;
        _http = http;
        _logger = logger;
    }

    private static string KeyOf(string artist, string album) => SongIdentity.MatchKey(artist, album);

    public async Task PrimeAsync(IReadOnlyList<AlbumCoverQuery> albums, IProgress<string>? status, CancellationToken ct)
    {
        var named = albums.Where(a => !string.IsNullOrWhiteSpace(a.Album) && !string.IsNullOrWhiteSpace(a.Artist))
            .DistinctBy(a => KeyOf(a.Artist, a.Album!))
            .ToList();
        if (named.Count == 0) return;

        var barcodes = new ConcurrentBag<(string Artist, string Album, string Upc)>();
        var found = 0;
        await Parallel.ForEachAsync(named, new ParallelOptions { MaxDegreeOfParallelism = PrimeParallelism, CancellationToken = ct },
            async (album, token) =>
            {
                var hit = await CatalogHitAsync(album.Artist, album.Album!, token);
                if (hit is not null && await _deezer.GetAlbumUpcAsync(hit.DeezerId, token) is { } upc)
                    barcodes.Add((album.Artist, album.Album!, upc));
                var n = Interlocked.Increment(ref found);
                if (n % 10 == 0 || n == named.Count) status?.Report($"Finding barcodes: {n:N0} of {named.Count:N0} albums");
            });
        var list = barcodes.ToList();
        var matched = await _itunes.PrimeByBarcodeAsync(list,
            new Progress<int>(done => status?.Report($"Matching with Apple: {done:N0} of {list.Count:N0} albums")), ct);
        _logger.LogInformation("Cover upgrade: {Barcodes} of {Albums} album(s) had a barcode, Apple matched {Matched} in bulk",
            barcodes.Count, named.Count, matched);
    }

    public async Task<FoundCover?> FindAsync(AlbumCoverQuery query, CancellationToken ct)
    {
        var itunes = Try("iTunes", () => _itunes.TryFetchAlbumMasterAsync(query.Artist, query.Album, query.Title, ct));
        var archive = string.IsNullOrEmpty(query.MusicBrainzReleaseId) && string.IsNullOrEmpty(query.MusicBrainzReleaseGroupId)
            ? Task.FromResult<byte[]?>(null)
            : Try("Cover Art Archive", () => _archive.TryFetchAsync(query.MusicBrainzReleaseId, query.MusicBrainzReleaseGroupId, ct));
        var catalog = string.IsNullOrWhiteSpace(query.Album)
            ? Task.FromResult<byte[]?>(null)
            : Try("Deezer", () => CatalogAsync(query.Artist, query.Album!, ct));
        await Task.WhenAll(itunes, archive, catalog);

        FoundCover? best = null;
        foreach (var (bytes, source) in new[] { (itunes.Result, "iTunes"), (archive.Result, "Cover Art Archive"), (catalog.Result, "Deezer") })
        {
            if (!CoverImage.IsUsable(bytes, requireSquare: true) || CoverImage.Measure(bytes!) is not { } size) continue;
            var side = Math.Min(size.Width, size.Height);
            if (best is null || side > best.Side) best = new FoundCover(bytes!, source, side);
        }
        return best;

        async Task<byte[]?> Try(string source, Func<Task<byte[]?>> fetch)
        {
            try
            {
                return await fetch();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("{Source} cover failed for {Artist} - {Album}: {M}", source, query.Artist, query.Album, ex.Message);
                return null;
            }
        }
    }

    private async Task<DeezerMetadataService.AlbumHit?> CatalogHitAsync(string artist, string album, CancellationToken ct)
    {
        var key = KeyOf(artist, album);
        if (_catalog.TryGetValue(key, out var known)) return known;
        try
        {
            var hits = await _deezer.SearchAlbumsAsync($"{artist} {album}", 10, ct);
            var hit = hits.FirstOrDefault(h => SongIdentity.Same(album, artist, h.Title, h.Artist, AlbumTitles).IsSame);
            if (_catalog.Count > 5000) _catalog.Clear();
            _catalog[key] = hit;
            return hit;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("catalog album search failed for {Artist} - {Album}: {M}", artist, album, ex.Message);
            return null;
        }
    }

    private async Task<byte[]?> CatalogAsync(string artist, string album, CancellationToken ct)
    {
        if (await CatalogHitAsync(artist, album, ct) is not { CoverUrl: { Length: > 0 } url }) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DownloadTimeout);
            using var response = await _http.CreateClient().GetAsync(url, timeout.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(timeout.Token) : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("catalog cover failed for {Artist} - {Album}: {M}", artist, album, ex.Message);
            return null;
        }
    }
}
