using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Lidarr;
using Octo.Services.Soulseek;

namespace Octo.Services.Common;

/// <summary>
/// Routes explicit heart gestures, and plays when DownloadOnPlay or LidarrAlbumOnPlay ask
/// for it.
/// </summary>
public sealed class HeartAcquisitionCoordinator
{
    private readonly IOptionsMonitor<SubsonicSettings> _settings;
    private readonly TrackAcquisitionQueue _directQueue;
    private readonly IDownloadService _directDownloads;
    private readonly ILidarrHeartAcquisitionService _lidarr;
    private readonly ILogger<HeartAcquisitionCoordinator> _logger;

    /// <summary>Optional so a coordinator built without one still routes. The chain is the only
    /// place that knows which failure is the last, so it is the one that reports it.</summary>
    private readonly AcquisitionTracker? _tracker;
    private readonly ExternalIdRegistry? _idRegistry;

    public HeartAcquisitionCoordinator(IOptionsMonitor<SubsonicSettings> settings,
        TrackAcquisitionQueue directQueue, IDownloadService directDownloads,
        ILidarrHeartAcquisitionService lidarr, ILogger<HeartAcquisitionCoordinator> logger,
        AcquisitionTracker? tracker = null, ExternalIdRegistry? idRegistry = null)
    {
        _settings = settings;
        _directQueue = directQueue;
        _directDownloads = directDownloads;
        _lidarr = lidarr;
        _logger = logger;
        _tracker = tracker;
        _idRegistry = idRegistry;
    }

    public void QueueTrack(string provider, string externalId, string? requestedBy = null)
    {
        _ = AcquireTrackAsync(provider, externalId, requestedBy);
    }

    // Track ids handed to Lidarr on play, so a replay does not search every indexer again.
    // Cleared when full rather than aged: forgetting costs at most one more search.
    private const int LidarrPlayMemory = 10_000;
    private readonly ConcurrentDictionary<string, byte> _lidarrPlays = new();

    /// <summary>A client started an external track from its first byte.</summary>
    public void QueuePlay(string provider, string externalId, string? requestedBy = null,
        string? clientId = null, string? owner = null)
    {
        var settings = _settings.CurrentValue;
        // WaitForLosslessOnPlay acquires the track itself, from its own source.
        if (settings.DownloadOnPlay && !settings.WaitForLosslessOnPlay && PlaySource() is DownloadSource source)
            QueuePlayDownload(provider, externalId, source, requestedBy, clientId, owner);
        if (!settings.LidarrAlbumOnPlay) return;
        var key = $"{provider}:{externalId}";
        if (_lidarrPlays.Count >= LidarrPlayMemory) _lidarrPlays.Clear();
        if (_lidarrPlays.TryAdd(key, 0)) _ = HandPlayToLidarrAsync(key, provider, externalId, requestedBy);
    }

    private void QueuePlayDownload(string provider, string externalId, DownloadSource source,
        string? requestedBy, string? clientId, string? owner)
    {
        var request = _directQueue.TryEnqueuePlay(provider, externalId, source, requestedBy, onQueued: () =>
        {
            // Same row a heart gets, opened before the worker can report its first stage.
            var routing = _idRegistry?.Lookup(externalId);
            _tracker?.Begin(provider, externalId, clientId, owner, routing?.Artist, routing?.Title, routing?.Album);
            _tracker?.Stage(provider, externalId, AcquisitionState.Queued,
                source == DownloadSource.YouTube ? "YouTube" : "Soulseek");
        });
        if (request is null) return;
        _ = request.Completion.Task.ContinueWith(task =>
        {
            var reason = task.Exception?.GetBaseException().Message;
            _logger.LogDebug("Play download failed for {Provider}:{Id}: {Message}", provider, externalId, reason);
            // A heart that joined owns the row, and may still be trying its next source.
            if (!request.IsStar && !request.HeartJoined) _tracker?.Fail(provider, externalId, reason);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task HandPlayToLidarrAsync(string key, string provider, string externalId, string? requestedBy)
    {
        try
        {
            if (await _lidarr.TryAcquireTrackAsync(provider, externalId, notifyFailure: false, requestedBy)) return;
            _logger.LogDebug("Lidarr took no album for played track {Provider}:{Id}", provider, externalId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Lidarr hand-off failed for played track {Provider}:{Id}: {Message}",
                provider, externalId, ex.Message);
        }
        // Not handed off, so the next play of this track tries again.
        _lidarrPlays.TryRemove(key, out _);
    }

    public void QueueAlbum(string provider, string albumExternalId, string? requestedBy = null)
    {
        _ = AcquireAlbumAsync(provider, albumExternalId, requestedBy);
    }

    internal async Task AcquireTrackAsync(string provider, string externalId,
        string? requestedBy = null)
    {
        var steps = EnabledSteps(albumHeart: false);
        for (var index = 0; index < steps.Count; index++)
        {
            var isLast = index == steps.Count - 1;
            // One entry for the whole chain. The first direct source is still waiting for the
            // worker; every later source, and Lidarr, starts over by looking.
            _tracker?.Stage(provider, externalId,
                index == 0 && steps[index] != HeartDownloadSource.Lidarr
                    ? AcquisitionState.Queued : AcquisitionState.Searching,
                SourceName(steps[index]),
                index == 0 ? null : $"{SourceName(steps[index - 1])} couldn't get it, trying {SourceName(steps[index])}");
            if (steps[index] == HeartDownloadSource.Lidarr)
            {
                if (await _lidarr.TryAcquireTrackAsync(provider, externalId, isLast, requestedBy)) return;
                continue;
            }

            try
            {
                await _directQueue.Enqueue(provider, externalId, isStar: true,
                    triggerAlbumDownload: false, forcePermanent: true,
                    sourceOverride: ToDirectSource(steps[index]), notifyOnFailure: isLast,
                    // Passed on every step, not only the first. A track that fails its way
                    // down the source chain is still the same person's star.
                    requestedBy: requestedBy);
                return;
            }
            catch (Exception ex)
            {
                // A muted mid-chain failure must still leave a trace, or a track that
                // silently fell through every source is undiagnosable from the logs.
                _logger.LogWarning("Heart source {Source} failed for track {Provider}:{Id}: {Message}",
                    steps[index], provider, externalId, ex.Message);
                if (isLast)
                {
                    _tracker?.Fail(provider, externalId, ex.Message);
                    return;
                }
                // The next enabled source owns the fallback.
            }
        }
        // Only reached when the last source was Lidarr and it said no. It records its own
        // reason first; this is the fallback when it could not.
        _tracker?.Fail(provider, externalId, "No download source could get this song.");
    }

    internal async Task AcquireAlbumAsync(string provider, string albumExternalId,
        string? requestedBy = null)
    {
        var steps = EnabledSteps(albumHeart: true);
        for (var index = 0; index < steps.Count; index++)
        {
            var isLast = index == steps.Count - 1;
            if (steps[index] == HeartDownloadSource.Lidarr)
            {
                if (await _lidarr.TryAcquireAlbumAsync(provider, albumExternalId, isLast, requestedBy)) return;
                continue;
            }

            try
            {
                if (await _directDownloads.DownloadAlbumWithSourceAsync(
                        provider, albumExternalId, ToDirectSource(steps[index]),
                        suppressSummary: !isLast,
                        requestedBy: requestedBy is null ? null : [requestedBy]))
                    return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Heart source {Source} failed for album {Provider}:{Id}: {Message}",
                    steps[index], provider, albumExternalId, ex.Message);
                if (isLast)
                {
                    _tracker?.FailAlbum(provider, albumExternalId, ex.Message);
                    return;
                }
                // Continue down the configured priority list.
            }
        }
        // Every source has had its go. Tracks the last walk already settled keep what it said;
        // this only closes the ones nothing finished.
        _tracker?.FailAlbum(provider, albumExternalId, "No download source could get this track.");
    }

    private List<HeartDownloadSource> EnabledSteps(bool albumHeart) =>
        _settings.CurrentValue.EffectiveHeartDownloadSources()
            .Where(step => albumHeart ? step.AlbumEnabled == true : step.SongEnabled == true)
            .Select(step => step.Source)
            .ToList();

    private DownloadSource? PlaySource()
    {
        var sources = EnabledSteps(albumHeart: false).Where(s => s != HeartDownloadSource.Lidarr).ToList();
        if (sources.Count == 0) return null;
        return sources[0] == HeartDownloadSource.YouTube ? DownloadSource.YouTube
            : sources.Contains(HeartDownloadSource.YouTube) ? DownloadSource.SoulseekThenYouTube
            : DownloadSource.Soulseek;
    }

    private static string SourceName(HeartDownloadSource source) => source switch
    {
        HeartDownloadSource.Lidarr => "Lidarr",
        HeartDownloadSource.YouTube => "YouTube",
        _ => "Soulseek",
    };

    private static DownloadSource ToDirectSource(HeartDownloadSource source) => source switch
    {
        HeartDownloadSource.YouTube => DownloadSource.YouTube,
        _ => DownloadSource.Soulseek,
    };
}
