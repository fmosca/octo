using System.Collections.Concurrent;
using System.Threading.Channels;
using Octo.Services.Common;
using Octo.Services.Metadata;

namespace Octo.Services.CoverArt;

/// <summary>What is known about an album from its files' tags.</summary>
public sealed record AlbumCoverQuery(string Artist, string? Album, string? Title,
    string? MusicBrainzReleaseId = null, string? MusicBrainzReleaseGroupId = null, string? Barcode = null);

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
/// over many albums is primed: each album's barcode (from its own tags when they carry one,
/// else from Deezer, which answers far more), and every 20 barcodes found go to Apple as one
/// lookup. It is a pipeline (Brandon, 2026-10-01): barcodes, Apple's answers and each album's
/// covers all move at once, and an album waits only for its own batch, never for the whole run.
/// </summary>
public sealed class AlbumCoverFinder : IAlbumCoverFinder
{
    private static readonly SongMatchOptions AlbumTitles = new() { LengthToleranceSeconds = null };
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(15);
    private const int PrimeParallelism = 4;

    /// <summary>Barcodes sent to Apple as soon as this many are waiting (more go along when
    /// they piled up during Apple's pacing, up to <see cref="ITunesCoverArtLookup.UpcBatch"/>).</summary>
    internal const int AppleBatchMin = 20;

    /// <summary>A batch smaller than <see cref="AppleBatchMin"/> goes anyway after this long
    /// without a new barcode, so the last albums are not kept waiting.</summary>
    internal static TimeSpan AppleBatchIdle { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest an album waits for its batch before it is searched on its own.</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(15);

    private readonly ITunesCoverArtLookup _itunes;
    private readonly CoverArtArchiveLookup _archive;
    private readonly DeezerMetadataService _deezer;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AlbumCoverFinder> _logger;

    /// <summary>The catalog's album found while priming, so its cover needs no second search.</summary>
    private readonly ConcurrentDictionary<string, DeezerMetadataService.AlbumHit?> _catalog = new();

    /// <summary>Albums being primed, done once Apple has answered for their batch (or they had
    /// no barcode): <see cref="FindAsync"/> waits for this and no longer.</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _ready = new();

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

    private void Ready(string artist, string album)
    {
        if (_ready.TryGetValue(KeyOf(artist, album), out var done)) done.TrySetResult();
    }

    public async Task PrimeAsync(IReadOnlyList<AlbumCoverQuery> albums, IProgress<string>? status, CancellationToken ct)
    {
        var named = albums.Where(a => !string.IsNullOrWhiteSpace(a.Album) && !string.IsNullOrWhiteSpace(a.Artist))
            .DistinctBy(a => KeyOf(a.Artist, a.Album!))
            .ToList();
        _ready.Clear();
        foreach (var album in named)
            _ready[KeyOf(album.Artist, album.Album!)] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (named.Count == 0) return;

        var barcodes = Channel.CreateUnbounded<(string Artist, string Album, string Upc)>();
        int looked = 0, coded = 0, fromTags = 0, matched = 0;
        void Report() => status?.Report(
            $"barcodes {Volatile.Read(ref looked):N0} of {named.Count:N0} · Apple matched {Volatile.Read(ref matched):N0}");

        try
        {
            // Barcodes: the album's own tag when it has one, else the catalog's.
            var producer = Task.Run(async () =>
            {
                try
                {
                    await Parallel.ForEachAsync(named, new ParallelOptions { MaxDegreeOfParallelism = PrimeParallelism, CancellationToken = ct },
                        async (album, token) =>
                        {
                            var upc = album.Barcode;
                            if (!string.IsNullOrWhiteSpace(upc)) Interlocked.Increment(ref fromTags);
                            else if (await CatalogHitAsync(album.Artist, album.Album!, token) is { } hit)
                                upc = await _deezer.GetAlbumUpcAsync(hit.DeezerId, token);
                            if (!string.IsNullOrWhiteSpace(upc))
                            {
                                Interlocked.Increment(ref coded);
                                await barcodes.Writer.WriteAsync((album.Artist, album.Album!, upc.Trim()), token);
                            }
                            else Ready(album.Artist, album.Album!);
                            if (Interlocked.Increment(ref looked) % 10 == 0) Report();
                        });
                }
                finally
                {
                    barcodes.Writer.TryComplete();
                }
            }, ct);

            // Apple: a batch as soon as enough are waiting, or the producer has gone quiet.
            var reader = barcodes.Reader;
            var pending = new List<(string Artist, string Album, string Upc)>();
            while (true)
            {
                while (pending.Count < ITunesCoverArtLookup.UpcBatch && reader.TryRead(out var item)) pending.Add(item);
                var finished = reader.Completion.IsCompleted;
                var idle = false;
                if (pending.Count < AppleBatchMin && !finished)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(AppleBatchIdle);
                    try
                    {
                        await reader.WaitToReadAsync(wait.Token);
                        continue;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        idle = true;
                    }
                }
                if (pending.Count == 0)
                {
                    if (finished || reader.Completion.IsCompleted) break;
                    continue;
                }
                if (pending.Count >= AppleBatchMin || finished || idle)
                {
                    var send = pending;
                    pending = [];
                    Interlocked.Add(ref matched, await _itunes.PrimeByBarcodeAsync(send, null, ct));
                    foreach (var album in send) Ready(album.Artist, album.Album);
                    Report();
                }
            }
            await producer;
            _logger.LogInformation(
                "Cover upgrade: {Coded} of {Albums} album(s) had a barcode ({Tags} from their own tags), Apple matched {Matched} in bulk",
                coded, named.Count, fromTags, matched);
        }
        finally
        {
            // However it ended, nobody waits on an album this will never answer for.
            foreach (var done in _ready.Values) done.TrySetResult();
        }
    }

    public async Task<FoundCover?> FindAsync(AlbumCoverQuery query, CancellationToken ct)
    {
        // An album being primed waits for its own batch at Apple, so its master is matched
        // in bulk rather than searched; the other sources start at once.
        var ready = !string.IsNullOrWhiteSpace(query.Album) && _ready.TryGetValue(KeyOf(query.Artist, query.Album!), out var tcs)
            ? tcs.Task.WaitAsync(ReadyTimeout, ct).ContinueWith(_ => { }, TaskScheduler.Default)
            : Task.CompletedTask;
        var itunes = Try("iTunes", async () =>
        {
            await ready;
            return await _itunes.TryFetchAlbumMasterAsync(query.Artist, query.Album, query.Title, ct);
        });
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
            var hits = await _deezer.SearchAlbumsAsync($"{artist} {album}", 10, ct, keepSingles: true);
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
