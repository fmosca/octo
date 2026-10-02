using System.Collections.Concurrent;
using System.Threading.Channels;
using Octo.Models.Settings;

namespace Octo.Services.Common;

/// <summary>One queued request to fetch a permanent copy of a track.</summary>
public sealed class AcquisitionRequest
{
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }

    /// <summary>Carried per request rather than inferred from whoever won a race:
    /// heart routing decides song-versus-album scope, while playback never expands to an album.</summary>
    public bool TriggerAlbumDownload { get; init; }
    public bool ForcePermanent { get; init; }
    public bool IsStar { get; init; }
    public DownloadSource? SourceOverride { get; init; }
    public bool NotifyOnFailure { get; init; } = true;

    // Set when a heart joins a request a play started. From then on the heart owns the
    // outcome: a failure is reported the way a star's is, and the play leaves the row alone.
    private volatile bool _heartNotifies;
    private volatile bool _heartJoined;
    public bool HeartJoined => _heartJoined;
    public bool NotifiesOnFailure => (IsStar && NotifyOnFailure) || _heartNotifies;
    internal void JoinHeart(bool notifyOnFailure)
    {
        if (notifyOnFailure) _heartNotifies = true;
        _heartJoined = true;
    }

    private int _upgradeSearch;

    /// <summary>Search the slow, wide way. Settable after queueing, like RequestedBy, so a Better
    /// quality that joins a request already in flight still widens its search.</summary>
    public bool UpgradeSearch => Volatile.Read(ref _upgradeSearch) == 1;
    internal void AskForUpgradeSearch() => Volatile.Write(ref _upgradeSearch, 1);

    private readonly ConcurrentDictionary<string, byte> _requestedBy =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every user who asked for this track, not only the one who asked first.
    ///
    /// A star for a track already in flight joins this request rather than queueing a second
    /// transfer, so whoever wins that race is an accident of timing. Attributing the file to
    /// them alone would drop everyone else who asked for the same thing.
    /// </summary>
    public IReadOnlyList<string> RequestedBy =>
        _requestedBy.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Record one more asker. Safe to call after the request is already queued.</summary>
    internal void AddRequester(string? username)
    {
        if (!string.IsNullOrWhiteSpace(username)) _requestedBy.TryAdd(username.Trim(), 0);
    }

    /// <summary>
    /// RunContinuationsAsynchronously is required. Without it, completing this runs the
    /// waiting request's continuation — response headers, body writes, the client's whole
    /// download — inline on the worker thread, stalling the queue behind the slowest client.
    /// </summary>
    public TaskCompletionSource<string> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Key => $"{Provider}:{ExternalId}";
}

/// <summary>
/// Serialises permanent-copy downloads onto a single background worker.
///
/// The point is that a download must never be tied to the HTTP request that asked for it.
/// Playing a track used to run the whole Soulseek transfer inside the stream request, so a
/// client giving up cancelled the transfer mid-flight while slskd finished the file anyway,
/// and Octo kept no record of it (issue #9).
///
/// Stars get their own channel and are never dropped: a star is explicit user intent,
/// whereas a play is a hint that can be shed under load.
/// </summary>
public sealed class TrackAcquisitionQueue
{
    // A play storm (skipping through a queue) must not be able to enqueue unbounded work,
    // but a star must always land, so the two channels are bounded differently.
    private const int PlayCapacity = 32;
    private const int StarCapacity = 512;

    private readonly Channel<AcquisitionRequest> _plays =
        Channel.CreateBounded<AcquisitionRequest>(new BoundedChannelOptions(PlayCapacity)
        {
            // Wait, NOT DropWrite: DropWrite makes TryWrite return true while discarding,
            // which is the opposite of the logged, observable drop we want.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private readonly Channel<AcquisitionRequest> _stars =
        Channel.CreateBounded<AcquisitionRequest>(new BoundedChannelOptions(StarCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    // Producer-side, so a duplicate never consumes a queue slot at all. Consumer-side dedup
    // would be too late: Feishin re-requests /rest/stream on seek, so duplicates are routine.
    private readonly ConcurrentDictionary<string, AcquisitionRequest> _inFlight = new();

    // Requests in the plays channel that the worker has not taken yet.
    private int _waitingPlays;
    internal int WaitingPlays => Volatile.Read(ref _waitingPlays);

    private readonly ILogger<TrackAcquisitionQueue> _logger;

    public TrackAcquisitionQueue(ILogger<TrackAcquisitionQueue> logger) => _logger = logger;

    /// <summary>
    /// Queue an acquisition, or join the one already running for this track. The returned
    /// task completes when the file is on disk and fully registered; callers that do not
    /// care may ignore it entirely.
    /// </summary>
    public Task<string> Enqueue(string provider, string externalId, bool isStar,
        bool triggerAlbumDownload, bool forcePermanent,
        DownloadSource? sourceOverride = null, bool notifyOnFailure = true,
        string? requestedBy = null, bool upgradeSearch = false)
    {
        var request = new AcquisitionRequest
        {
            Provider = provider,
            ExternalId = externalId,
            IsStar = isStar,
            TriggerAlbumDownload = triggerAlbumDownload,
            ForcePermanent = forcePermanent,
            SourceOverride = sourceOverride,
            NotifyOnFailure = notifyOnFailure,
        };
        request.AddRequester(requestedBy);
        if (upgradeSearch) request.AskForUpgradeSearch();

        var existing = _inFlight.GetOrAdd(request.Key, request);
        if (!ReferenceEquals(existing, request))
        {
            // Already queued or running. Join it rather than fetching the same file twice,
            // and record this caller on the request that is actually going to run, so the
            // file is attributed to everyone who asked and not just to whoever was first.
            existing.AddRequester(requestedBy);
            if (upgradeSearch) existing.AskForUpgradeSearch();
            // A heart for a track a play already asked for. The source cannot change any more, but
            // the failure is now someone's explicit ask, and the heart chain still owns its fallback.
            if (isStar && !existing.IsStar) existing.JoinHeart(notifyOnFailure);
            return existing.Completion.Task;
        }

        if (!isStar) Interlocked.Increment(ref _waitingPlays);
        var channel = isStar ? _stars : _plays;
        if (!channel.Writer.TryWrite(request))
        {
            if (!isStar) Interlocked.Decrement(ref _waitingPlays);
            // Full. Say so out loud: silently shedding work is how "why didn't that
            // download?" becomes unanswerable.
            _logger.LogWarning(
                "Acquisition queue full ({Kind}); dropped {Provider}:{Id}",
                isStar ? "star" : "play", provider, externalId);
            Release(request);
            request.Completion.TrySetException(
                new InvalidOperationException("Acquisition queue is full; try again shortly."));
            return request.Completion.Task;
        }

        _logger.LogInformation("Queued {Kind} acquisition for {Provider}:{Id}",
            isStar ? "star" : "play", provider, externalId);
        return request.Completion.Task;
    }

    /// <summary>
    /// Queue a copy of a played track unless another played track is still waiting. Radio
    /// asks for every track it plays, and a backlog of those is work nobody wants by the
    /// time it runs. A dropped play leaves no claim behind, so the next play asks again.
    /// Returns null when dropped, otherwise the request carrying this play (new, or one
    /// already queued or running). <paramref name="onQueued"/> runs only for a new request,
    /// before the worker can see it.
    /// </summary>
    internal AcquisitionRequest? TryEnqueuePlay(string provider, string externalId,
        DownloadSource sourceOverride, string? requestedBy, Action? onQueued = null)
    {
        var request = new AcquisitionRequest
        {
            Provider = provider, ExternalId = externalId, IsStar = false,
            TriggerAlbumDownload = false, ForcePermanent = true,
            SourceOverride = sourceOverride, NotifyOnFailure = false,
        };
        request.AddRequester(requestedBy);
        // Already wanted: ride along, whatever is waiting.
        if (_inFlight.TryGetValue(request.Key, out var known))
        {
            known.AddRequester(requestedBy);
            return known;
        }
        // The waiting slot is taken BEFORE the key is claimed. Claiming first and letting go on a
        // full slot would leave a heart that joined in between waiting on a request nobody runs:
        // Release does not complete it.
        if (Interlocked.CompareExchange(ref _waitingPlays, 1, 0) != 0)
        {
            _logger.LogDebug("Skipped play acquisition for {Provider}:{Id}: another played track is still waiting",
                provider, externalId);
            return null;
        }
        var existing = _inFlight.GetOrAdd(request.Key, request);
        if (!ReferenceEquals(existing, request))
        {
            Interlocked.Decrement(ref _waitingPlays);
            existing.AddRequester(requestedBy);
            return existing;
        }
        onQueued?.Invoke();
        if (!_plays.Writer.TryWrite(request))
        {
            Interlocked.Decrement(ref _waitingPlays);
            _logger.LogWarning("Acquisition queue full (play); dropped {Provider}:{Id}", provider, externalId);
            Release(request);
            request.Completion.TrySetException(new InvalidOperationException("Acquisition queue is full; try again shortly."));
            return request;
        }
        _logger.LogInformation("Queued play acquisition for {Provider}:{Id}", provider, externalId);
        return request;
    }

    /// <summary>
    /// Take the next request, preferring stars. Returns null when both channels complete.
    /// </summary>
    internal async Task<AcquisitionRequest?> DequeueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_stars.Reader.TryRead(out var star)) return star;
            if (_plays.Reader.TryRead(out var play)) { Interlocked.Decrement(ref _waitingPlays); return play; }

            var starWait = _stars.Reader.WaitToReadAsync(ct).AsTask();
            var playWait = _plays.Reader.WaitToReadAsync(ct).AsTask();
            var ready = await Task.WhenAny(starWait, playWait);
            if (!await ready) continue;
        }
        return null;
    }

    /// <summary>
    /// Give up the dedup claim. MUST run on every terminal outcome, including a drop and a
    /// failure, not just on success: a leaked claim makes every future request for that
    /// track join a job that will never run.
    /// </summary>
    internal void Release(AcquisitionRequest request) =>
        _inFlight.TryRemove(new KeyValuePair<string, AcquisitionRequest>(request.Key, request));
}
