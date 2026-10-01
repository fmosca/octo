using System.Diagnostics;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Fingerprint;
using Octo.Services.Metadata;

namespace Octo.Services.Tagging;

/// <summary>
/// Works out what a downloaded file is, by every source that can say: the fingerprint service's
/// answer (already paid for by verification), the catalog's ranked hits, the file's own tags,
/// and the music database when the fingerprint named nothing. Every candidate is weighed
/// against what was asked for and what landed, and the winner's release is looked up for the
/// facts only the database has. Every external call has its own cap; a timeout costs fields,
/// never the download. Reads only: the plan it returns is applied by the caller.
/// </summary>
public sealed class ReleaseIdentifier
{
    internal static readonly TimeSpan CatalogBudget = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan DatabaseBudget = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DetailsBudget = TimeSpan.FromSeconds(10);
    internal const int CatalogCandidates = 2;

    private readonly IServiceProvider _services;
    private readonly ILogger<ReleaseIdentifier> _logger;

    public ReleaseIdentifier(IServiceProvider services, ILogger<ReleaseIdentifier> logger)
    {
        _services = services;
        _logger = logger;
    }

    private MetadataSettings Metadata =>
        _services.GetService<IOptionsMonitor<MetadataSettings>>()?.CurrentValue ?? new MetadataSettings();
    private SoulseekSettings Soulseek =>
        _services.GetService<IOptionsMonitor<SoulseekSettings>>()?.CurrentValue ?? new SoulseekSettings();

    /// <summary>The request as evidence: what was asked for, before anything corrected it.</summary>
    public static TagRequest RequestFor(Song song, string artist, string title, string? album, int? track)
    {
        var parsed = SongIdentity.ParseTitle(title);
        return new TagRequest(artist, title, string.IsNullOrWhiteSpace(album) ? null : album, track, song.DiscNumber,
            song.Duration, SongIdentity.NormalizeIsrc(song.Isrc), CatalogAlbumIdOf(song.AlbumId), song.ExternalId,
            SongIdentity.DistinctVersions(parsed));
    }

    /// <summary>"ext-deezer-album-123456" is the catalog's 123456.</summary>
    private static string? CatalogAlbumIdOf(string? albumId)
    {
        if (string.IsNullOrEmpty(albumId)) return null;
        var at = albumId.LastIndexOf('-');
        return at >= 0 && at < albumId.Length - 1 ? albumId[(at + 1)..] : albumId;
    }

    /// <summary>
    /// Identify one file. <paramref name="tagsAreEvidence"/> is false for a file whose tags are
    /// an uploader's, not a peer's. The album context, when the file is one track of a walk,
    /// steers the choice onto the walk's release.
    /// </summary>
    public async Task<TagPlan> IdentifyAsync(Song song, TagRequest request, string filePath, bool tagsAreEvidence,
        AlbumTagContext? album, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var metadata = Metadata;
        var soulseek = Soulseek;
        var settings = MatchingSettings.From(metadata, soulseek);
        var notes = new List<string>();
        var stages = new Dictionary<string, double>();

        var file = TagWriterExtras.ReadFacts(filePath, tagsAreEvidence);
        var lookup = song.Verification?.Lookup;
        var threshold = settings.FingerprintThreshold;
        var fingerprinted = FingerprintedIds(lookup, threshold);
        var candidates = new List<ReleaseCandidate>(CandidateSources.FromFingerprint(lookup, threshold));

        var musicBrainz = _services.GetService<MusicBrainzClient>();
        var deezer = _services.GetService<DeezerMetadataService>();
        var lookups = metadata.ReleaseDetailsLookup && musicBrainz is not null;

        // The likeliest release's details are asked for while the catalog is still answering,
        // so the common case pays for neither in series.
        Task<ReleaseDetails?>? prefetch = null;
        string? prefetchId = null;
        if (lookups && PreScore(candidates) is { } likely)
        {
            prefetchId = likely;
            prefetch = LookupDetailsAsync(musicBrainz!, likely, ct);
        }

        DeezerMetadataService.FullTrackMeta? catalogBest = null;
        if (deezer is not null)
        {
            var clock = Stopwatch.StartNew();
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(CatalogBudget);
                var answer = await deezer.EnrichTrackCandidatesAsync(request.Artist, request.Title, CatalogCandidates, budget.Token);
                if (answer.DidNotAnswer) notes.Add("the catalog did not answer");
                foreach (var hit in answer.Hits)
                {
                    catalogBest ??= hit;
                    candidates.Add(CandidateSources.FromCatalog(hit, request.Title));
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                notes.Add($"the catalog did not answer in {CatalogBudget.TotalSeconds:0} s");
            }
            catch (Exception ex)
            {
                notes.Add("the catalog could not be asked");
                _logger.LogDebug("catalog candidates failed for '{Artist} - {Title}': {M}", request.Artist, request.Title, ex.Message);
            }
            stages["catalog"] = clock.Elapsed.TotalSeconds;
        }

        if (CandidateSources.FromFileTags(file, request) is { } fromFile) candidates.Add(fromFile);

        // The music database is asked by name only when the fingerprint named nothing: it is a
        // second a call, and the fingerprint's answer is better evidence than a name search.
        if (lookups && !candidates.Any(c => c.Source == TagSource.Fingerprint))
        {
            var clock = Stopwatch.StartNew();
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(DatabaseBudget);
                if (request.Isrc is { } isrc)
                {
                    using var doc = await musicBrainz!.LookupIsrcAsync(isrc, budget.Token);
                    if (doc is not null) candidates.AddRange(CandidateSources.FromIsrcLookup(doc.RootElement));
                    else notes.Add("the music database did not answer the code lookup");
                }
                if (!candidates.Any(c => c.Source == TagSource.Database))
                {
                    using var doc = await musicBrainz!.SearchRecordingsAsync(request.Artist, request.Title, file.DurationSeconds, budget.Token);
                    if (doc is not null) candidates.AddRange(CandidateSources.FromDatabaseSearch(doc.RootElement));
                    else notes.Add("the music database did not answer the search");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                notes.Add($"the music database did not answer in {DatabaseBudget.TotalSeconds:0} s");
            }
            catch (Exception ex)
            {
                notes.Add("the music database could not be asked");
                _logger.LogDebug("database candidates failed for '{Artist} - {Title}': {M}", request.Artist, request.Title, ex.Message);
            }
            stages["database"] = clock.Elapsed.TotalSeconds;
        }

        var evidence = new TagEvidence(request, file, threshold, fingerprinted);
        var plan = ReleaseChooser.Choose(evidence, candidates, settings);
        plan.CatalogBest = catalogBest;
        plan.Notes.AddRange(notes);
        album?.PreferSettledRelease(plan);

        // The release's own facts, for a winner the fingerprint service or the music database named.
        if (lookups && plan.Chosen is { Candidate: { ReleaseId.Length: > 0, Source: TagSource.Fingerprint or TagSource.Database } chosen }
            && plan.Confidence is TagConfidence.Strong or TagConfidence.Medium or TagConfidence.Ambiguous)
        {
            var clock = Stopwatch.StartNew();
            ReleaseDetails? details = null;
            try
            {
                if (prefetch is not null && string.Equals(prefetchId, chosen.ReleaseId, StringComparison.OrdinalIgnoreCase))
                {
                    details = await prefetch;
                    plan.DetailsPrefetchHit = details is not null;
                }
                else details = await LookupDetailsAsync(musicBrainz!, chosen.ReleaseId!, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogDebug("release details failed for {Release}: {M}", chosen.ReleaseId, ex.Message);
            }
            if (details is not null) plan.With(details);
            else plan.Notes.Add("the music database did not answer the release lookup; label, catalogue number and barcode may be missing");
            stages["details"] = clock.Elapsed.TotalSeconds;
        }
        else if (prefetch is not null)
            _ = prefetch.ContinueWith(_ => { }, TaskScheduler.Default);

        foreach (var stage in stages) plan.StageSeconds[stage.Key] = stage.Value;
        plan.StageSeconds["identify"] = started.Elapsed.TotalSeconds;
        return plan;
    }

    private static async Task<ReleaseDetails?> LookupDetailsAsync(MusicBrainzClient client, string releaseId, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(DetailsBudget);
        try
        {
            return await client.LookupReleaseAsync(releaseId, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The recordings the fingerprint service named at or above the threshold.</summary>
    internal static IReadOnlySet<string> FingerprintedIds(AcoustIdLookup? lookup, double threshold) =>
        lookup is not { IsOk: true } ? new HashSet<string>()
            : lookup.Results.Where(r => r.Score >= threshold).SelectMany(r => r.Recordings)
                .Select(r => r.RecordingId).Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A cheap guess at the release the chooser will pick, for the details prefetch: a plain
    /// album over anything else, then the earliest first release, then the earliest pressing.
    /// </summary>
    internal static string? PreScore(IReadOnlyList<ReleaseCandidate> fingerprinted) =>
        fingerprinted.Where(c => c.Source == TagSource.Fingerprint && c.ReleaseId is { Length: > 0 })
            .OrderBy(c => string.Equals(c.PrimaryType, "Album", StringComparison.OrdinalIgnoreCase) && c.SecondaryTypes.Count == 0 ? 0
                : string.Equals(c.PrimaryType, "Single", StringComparison.OrdinalIgnoreCase) || string.Equals(c.PrimaryType, "EP", StringComparison.OrdinalIgnoreCase) ? 1
                : 2)
            .ThenBy(c => c.GroupFirstReleaseDate ?? "9999", StringComparer.Ordinal)
            .ThenBy(c => c.ReleaseDate ?? "9999", StringComparer.Ordinal)
            .Select(c => c.ReleaseId)
            .FirstOrDefault();
}
