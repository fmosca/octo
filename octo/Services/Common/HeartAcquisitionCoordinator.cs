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

    /// <summary>Optional: without one nothing waits for Soulseek, which is how it always was.</summary>
    private readonly ISoulseekLink? _soulseek;
    private readonly SoulseekHoldStore? _holds;

    // Plays waiting for Soulseek, one per song however often it is played meanwhile.
    private readonly ConcurrentDictionary<string, byte> _heldPlays = new();

    public HeartAcquisitionCoordinator(IOptionsMonitor<SubsonicSettings> settings,
        TrackAcquisitionQueue directQueue, IDownloadService directDownloads,
        ILidarrHeartAcquisitionService lidarr, ILogger<HeartAcquisitionCoordinator> logger,
        AcquisitionTracker? tracker = null, ExternalIdRegistry? idRegistry = null,
        ISoulseekLink? soulseek = null, SoulseekHoldStore? holds = null)
    {
        _settings = settings;
        _directQueue = directQueue;
        _directDownloads = directDownloads;
        _lidarr = lidarr;
        _logger = logger;
        _tracker = tracker;
        _idRegistry = idRegistry;
        _soulseek = soulseek;
        _holds = holds;
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
        {
            if (source == DownloadSource.YouTube || _soulseek is not { HoldLimit.Ticks: > 0 })
                QueuePlayDownload(provider, externalId, source, requestedBy, clientId, owner);
            else
                _ = HoldThenQueuePlayAsync(provider, externalId, source, requestedBy, clientId, owner);
        }
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

    /// <summary>
    /// A play during a Soulseek outage waits for it like a heart, but only in memory: a play is a
    /// hint, and one lost to a restart costs a replay.
    /// </summary>
    private async Task HoldThenQueuePlayAsync(string provider, string externalId, DownloadSource source,
        string? requestedBy, string? clientId, string? owner)
    {
        var key = $"{provider}:{externalId}";
        var link = _soulseek!;
        try
        {
            if ((await link.ReadAsync(fresh: false, CancellationToken.None))?.Link == SoulseekLinkState.NotLoggedIn)
            {
                if (!_heldPlays.TryAdd(key, 0)) return;
                try { await link.WaitForLoginAsync(link.UtcNow + link.HoldLimit, CancellationToken.None); }
                finally { _heldPlays.TryRemove(key, out _); }
            }
            QueuePlayDownload(provider, externalId, source, requestedBy, clientId, owner);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Play download for {Provider}:{Id} could not wait for Soulseek: {Message}",
                provider, externalId, ex.Message);
        }
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

    /// <summary>A heart that was waiting for Soulseek when Octo restarted. Its saved start time
    /// keeps the original deadline; the entry goes once the chain has finished either way.</summary>
    internal void ResumeTrack(HeldAcquisition held) => _ = ResumeAsync(held,
        () => AcquireTrackAsync(held.Provider, held.ExternalId, held.RequestedBy, held.HeldSinceUtc));

    internal void ResumeAlbum(HeldAcquisition held) => _ = ResumeAsync(held,
        () => AcquireAlbumAsync(held.Provider, held.ExternalId, held.RequestedBy, held.HeldSinceUtc));

    internal async Task ResumeAsync(HeldAcquisition held, Func<Task> chain)
    {
        try { await chain(); }
        catch (Exception ex) { _logger.LogWarning("Resumed {Key} failed: {Message}", held.Key, ex.Message); }
        finally { _holds?.Release(held.Key); }
    }

    internal async Task AcquireTrackAsync(string provider, string externalId,
        string? requestedBy = null, DateTime? heldSinceUtc = null)
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

            // Soulseek waits out an outage, before the step and again when slskd lost its login
            // partway through it. Every other source, and Soulseek once the wait is over, goes on
            // exactly as before.
            var waitUntil = SoulseekDeadline(steps[index], heldSinceUtc);
            Exception failure;
            while (true)
            {
                await HoldForSoulseekAsync(HeldKind.Track, provider, externalId, requestedBy, waitUntil);
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
                    failure = ex;
                    if (!await SoulseekDroppedAsync(waitUntil)) break;
                    _logger.LogInformation(
                        "Soulseek lost its connection while getting {Provider}:{Id}; waiting for it rather than moving on",
                        provider, externalId);
                }
            }

            // A muted mid-chain failure must still leave a trace, or a track that
            // silently fell through every source is undiagnosable from the logs.
            _logger.LogWarning("Heart source {Source} failed for track {Provider}:{Id}: {Message}",
                steps[index], provider, externalId, failure.Message);
            if (isLast)
            {
                _tracker?.Fail(provider, externalId, failure.Message);
                return;
            }
            // The next enabled source owns the fallback.
        }
        // Only reached when the last source was Lidarr and it said no. It records its own
        // reason first; this is the fallback when it could not.
        _tracker?.Fail(provider, externalId, "No download source could get this song.");
    }

    internal async Task AcquireAlbumAsync(string provider, string albumExternalId,
        string? requestedBy = null, DateTime? heldSinceUtc = null)
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

            var waitUntil = SoulseekDeadline(steps[index], heldSinceUtc);
            Exception? failure;
            while (true)
            {
                await HoldForSoulseekAsync(HeldKind.Album, provider, albumExternalId, requestedBy, waitUntil);
                failure = null;
                try
                {
                    if (await _directDownloads.DownloadAlbumWithSourceAsync(
                            provider, albumExternalId, ToDirectSource(steps[index]),
                            suppressSummary: !isLast,
                            requestedBy: requestedBy is null ? null : [requestedBy]))
                        return;
                }
                catch (Exception ex) { failure = ex; }
                // A walk that came up short because slskd dropped partway: the tracks it got stay,
                // and the next walk skips them.
                if (!await SoulseekDroppedAsync(waitUntil)) break;
                _logger.LogInformation(
                    "Soulseek lost its connection during album {Provider}:{Id}; waiting for it rather than moving on",
                    provider, albumExternalId);
            }

            if (failure is not null)
            {
                _logger.LogWarning("Heart source {Source} failed for album {Provider}:{Id}: {Message}",
                    steps[index], provider, albumExternalId, failure.Message);
                if (isLast)
                {
                    _tracker?.FailAlbum(provider, albumExternalId, failure.Message);
                    return;
                }
            }
            // Continue down the configured priority list.
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

    /// <summary>When a Soulseek step stops waiting for slskd, or null when it never waits: another
    /// source, no link, or the wait switched off.</summary>
    private DateTime? SoulseekDeadline(HeartDownloadSource step, DateTime? heldSinceUtc) =>
        step == HeartDownloadSource.Soulseek && _soulseek is { } link && link.HoldLimit > TimeSpan.Zero
            ? (heldSinceUtc ?? link.UtcNow) + link.HoldLimit
            : null;

    /// <summary>
    /// Waits while slskd says it is not logged in, up to <paramref name="waitUntil"/>. On disk for
    /// the length of the wait, so a restart picks the heart up again.
    /// </summary>
    private async Task HoldForSoulseekAsync(HeldKind kind, string provider, string id, string? requestedBy,
        DateTime? waitUntil)
    {
        if (waitUntil is not { } deadline || _soulseek is not { } link) return;
        var reading = await link.ReadAsync(fresh: false, CancellationToken.None);
        if (reading?.Link != SoulseekLinkState.NotLoggedIn) return;

        var held = _holds?.Hold(new HeldAcquisition(kind, provider, id, requestedBy, deadline - link.HoldLimit));
        if (kind == HeldKind.Track)
            _tracker?.Stage(provider, id, AcquisitionState.Queued, "Soulseek",
                $"Waiting for Soulseek to come back, until {deadline:HH:mm} UTC");
        _logger.LogInformation("Soulseek is not connected (slskd says {State}); holding {Kind} {Provider}:{Id} until {Deadline:HH:mm} UTC",
            reading.State ?? "not logged in", kind, provider, id, deadline);
        try
        {
            var back = await link.WaitForLoginAsync(deadline, CancellationToken.None);
            if (back) _logger.LogInformation("Soulseek is back; going on with {Kind} {Provider}:{Id}", kind, provider, id);
            else _logger.LogWarning("Soulseek still not connected after the wait; {Kind} {Provider}:{Id} goes on to the next source",
                kind, provider, id);
            if (kind == HeldKind.Track)
                _tracker?.Stage(provider, id, AcquisitionState.Queued, "Soulseek",
                    back ? "Soulseek is back" : "Soulseek did not come back in time");
        }
        finally
        {
            if (held is not null) _holds!.Release(held.Key);
        }
    }

    /// <summary>True when the step just failed because slskd lost its login and there is still time
    /// to wait. A fresh read: the cached one may be from before the drop.</summary>
    private async Task<bool> SoulseekDroppedAsync(DateTime? waitUntil) =>
        waitUntil is { } deadline && _soulseek is { } link && link.UtcNow < deadline
        && (await link.ReadAsync(fresh: true, CancellationToken.None))?.Link == SoulseekLinkState.NotLoggedIn;

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
