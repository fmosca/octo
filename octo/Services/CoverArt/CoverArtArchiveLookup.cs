namespace Octo.Services.CoverArt;

/// <summary>
/// The Cover Art Archive, asked directly when a fingerprint named the MusicBrainz release: the
/// right pressing, no guessing by name (#51). The front of the release first, then of the release
/// group. No key; it redirects across hosts to archive.org, which HttpClient follows. Asked at
/// 1200, its largest thumbnail: the 500 one lost to the catalog's 1000 px covers.
/// </summary>
public sealed class CoverArtArchiveLookup
{
    public const string ClientName = "coverartarchive";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<CoverArtArchiveLookup> _logger;

    public CoverArtArchiveLookup(IHttpClientFactory http, ILogger<CoverArtArchiveLookup> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<byte[]?> TryFetchAsync(string? releaseId, string? releaseGroupId, CancellationToken ct)
    {
        // The 500 one only when the 1200 one is missing, which an old upload can be.
        foreach (var path in new[]
                 {
                     string.IsNullOrEmpty(releaseId) ? null : $"release/{releaseId}/front-1200",
                     string.IsNullOrEmpty(releaseId) ? null : $"release/{releaseId}/front-500",
                     string.IsNullOrEmpty(releaseGroupId) ? null : $"release-group/{releaseGroupId}/front-1200",
                     string.IsNullOrEmpty(releaseGroupId) ? null : $"release-group/{releaseGroupId}/front-500",
                 })
        {
            if (path is null) continue;
            try
            {
                using var response = await _http.CreateClient(ClientName).GetAsync(path, ct);
                // 404 is the ordinary answer for a release nobody has uploaded art for.
                if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("cover art archive {Path} failed: {M}", path, ex.Message);
            }
        }
        return null;
    }
}
