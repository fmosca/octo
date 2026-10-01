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
}

/// <summary>
/// The largest cover of one album, for the cover upgrade. Every source must name the same
/// release, because what this finds is written into the owner's files: Apple's master on a
/// strict artist and album match, the Cover Art Archive by the MusicBrainz release the files
/// already name, and the catalog's album by the same strict match. All three are asked and the
/// largest wins; a run goes through a library once, so the extra lookups are worth it.
/// </summary>
public sealed class AlbumCoverFinder : IAlbumCoverFinder
{
    private static readonly SongMatchOptions AlbumTitles = new() { LengthToleranceSeconds = null };
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(15);

    private readonly ITunesCoverArtLookup _itunes;
    private readonly CoverArtArchiveLookup _archive;
    private readonly DeezerMetadataService _deezer;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AlbumCoverFinder> _logger;

    public AlbumCoverFinder(ITunesCoverArtLookup itunes, CoverArtArchiveLookup archive,
        DeezerMetadataService deezer, IHttpClientFactory http, ILogger<AlbumCoverFinder> logger)
    {
        _itunes = itunes;
        _archive = archive;
        _deezer = deezer;
        _http = http;
        _logger = logger;
    }

    public async Task<FoundCover?> FindAsync(AlbumCoverQuery query, CancellationToken ct)
    {
        FoundCover? best = null;

        void Offer(byte[]? bytes, string source)
        {
            if (!CoverImage.IsUsable(bytes, requireSquare: true) || CoverImage.Measure(bytes!) is not { } size) return;
            var side = Math.Min(size.Width, size.Height);
            if (best is null || side > best.Side) best = new FoundCover(bytes!, source, side);
        }

        try
        {
            Offer(await _itunes.TryFetchAlbumMasterAsync(query.Artist, query.Album, query.Title, ct), "iTunes");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("iTunes master failed for {Artist} - {Album}: {M}", query.Artist, query.Album, ex.Message);
        }

        if (!string.IsNullOrEmpty(query.MusicBrainzReleaseId) || !string.IsNullOrEmpty(query.MusicBrainzReleaseGroupId))
            Offer(await _archive.TryFetchAsync(query.MusicBrainzReleaseId, query.MusicBrainzReleaseGroupId, ct),
                "Cover Art Archive");

        if (!string.IsNullOrWhiteSpace(query.Album))
            Offer(await CatalogAsync(query.Artist, query.Album!, ct), "Deezer");

        return best;
    }

    private async Task<byte[]?> CatalogAsync(string artist, string album, CancellationToken ct)
    {
        try
        {
            var hits = await _deezer.SearchAlbumsAsync($"{artist} {album}", 10, ct);
            var hit = hits.FirstOrDefault(h => !string.IsNullOrEmpty(h.CoverUrl)
                && SongIdentity.Same(album, artist, h.Title, h.Artist, AlbumTitles).IsSame);
            if (hit?.CoverUrl is not { } url) return null;
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
