using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Fingerprint;
using Octo.Services.Soulseek;

namespace Octo.Services.CoverArt;

/// <summary>The cover a download gets, where it came from, and whether it is simply the one the
/// file already carries (so there is nothing to rewrite).</summary>
public sealed record CoverChoice(byte[] Bytes, string Source, bool KeepsExisting);

/// <summary>
/// The download-time cover chain (#51). The download path used to embed one Deezer URL and stop,
/// so anything Deezer did not know was written with no art, while the aggregator that already
/// knew iTunes and Last.fm sat unused beside it.
///
/// Asked in order: the Cover Art Archive when a fingerprint named the release the album tag
/// describes, the catalog's own cover, the aggregator by name, and last the file's own art. The
/// first one at least <see cref="SharpSide"/> wide wins at once; otherwise the largest one seen
/// does. Taking the first usable one let a 500 px archive scan or a peer's 200 px thumbnail
/// beat the catalog's 1000 px cover. A cover that is not square counts as missing, and a
/// letterboxed video frame gives up its centre only when nothing else was found.
/// </summary>
public sealed class DownloadCoverResolver
{
    /// <summary>A slow cover source must cost seconds, never the download: the whole finalize
    /// phase runs under the download lock.</summary>
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Big enough to stop looking: the catalog's own covers are 1000 px.</summary>
    internal const int SharpSide = 1000;

    private readonly CoverArtArchiveLookup _archive;
    private readonly CoverArtAggregator _aggregator;
    private readonly IHttpClientFactory _http;
    private readonly IOptionsMonitor<MetadataSettings> _settings;
    private readonly ILogger<DownloadCoverResolver> _logger;

    public DownloadCoverResolver(CoverArtArchiveLookup archive, CoverArtAggregator aggregator,
        IHttpClientFactory http, IOptionsMonitor<MetadataSettings> settings, ILogger<DownloadCoverResolver> logger)
    {
        _archive = archive;
        _aggregator = aggregator;
        _http = http;
        _settings = settings;
        _logger = logger;
    }

    public async Task<CoverChoice?> ResolveAsync(Song song, byte[]? embedded, CancellationToken ct)
    {
        var settings = _settings.CurrentValue;
        var requireSquare = settings.ReplaceVideoCovers;
        CoverChoice? best = null;
        var bestSide = 0;

        // True when this one is sharp enough to stop; otherwise it is kept if it is the biggest.
        bool Offer(byte[]? bytes, string source, bool keepsExisting = false)
        {
            if (!CoverImage.IsUsable(bytes, requireSquare) || CoverImage.Measure(bytes!) is not { } size) return false;
            var side = Math.Min(size.Width, size.Height);
            if (side > bestSide)
            {
                best = new CoverChoice(bytes!, source, keepsExisting);
                bestSide = side;
            }
            return side >= SharpSide;
        }

        // Only when the album tag IS the release the fingerprint matched: a download tagged with
        // a compilation's name must not get the original album's cover.
        if (settings.UseCoverArtArchive
            && (song.MusicBrainzReleaseId is { Length: > 0 } || song.MusicBrainzReleaseGroupId is { Length: > 0 })
            && VerificationResult.AlbumIsFromRelease(song))
        {
            var archived = await _archive.TryFetchAsync(song.MusicBrainzReleaseId, song.MusicBrainzReleaseGroupId, ct);
            if (Offer(archived, "Cover Art Archive")) return best;
        }

        if ((song.CoverArtUrlLarge ?? song.CoverArtUrl) is { Length: > 0 } url)
        {
            var catalog = await DownloadAsync(url, ct);
            if (Offer(catalog, "the catalog")) return best;
        }

        try
        {
            var routing = new SoulseekRouting
            {
                Kind = string.IsNullOrWhiteSpace(song.Album) ? RoutingKind.Song : RoutingKind.Album,
                Artist = song.PrimaryArtist ?? song.Artist,
                Title = song.Title,
                Album = song.Album,
            };
            var aggregated = await _aggregator.GetCoverAsync(routing, background: true, ct);
            if (Offer(aggregated, "a cover search")) return best;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("cover search failed for {Artist} - {Title}: {M}", song.Artist, song.Title, ex.Message);
        }

        if (embedded is { Length: > 0 }) Offer(embedded, "the file itself", keepsExisting: true);
        if (best is not null)
        {
            if (bestSide < SharpSide)
                _logger.LogInformation("Best cover for {Artist} - {Title} is {Side} px, from {Source}",
                    song.Artist, song.Title, bestSide, best.Source);
            return best;
        }

        if (embedded is { Length: > 0 } && requireSquare
            && CoverImage.CropToSquare(embedded) is { } cropped && CoverImage.IsUsable(cropped, true))
            return new(cropped, "the centre of a video frame", false);
        return null;
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CatalogTimeout);
            using var response = await _http.CreateClient().GetAsync(url, timeout.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(timeout.Token) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("cover download {Url} failed: {M}", url, ex.Message);
            return null;
        }
    }
}
