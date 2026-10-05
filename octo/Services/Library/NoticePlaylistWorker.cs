using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Fingerprint;
using Octo.Services.Subsonic;

namespace Octo.Services.Library;

/// <summary>What one sweep does to one person's notice playlist.</summary>
public sealed record NoticePlan(
    IReadOnlyList<string> Dismiss,
    IReadOnlyList<string> Adopt,
    IReadOnlyList<string> Remove,
    IReadOnlyList<NoticeEntry> Add);

/// <summary>
/// The whole reconcile decision, separated from the HTTP so it can be driven directly: what the
/// person answered by removing a track, what to take off because it is settled, and what fits.
/// </summary>
public static class NoticeReconcile
{
    public static NoticePlan Plan(IReadOnlyList<NoticeEntry> entries, IReadOnlySet<string> present, int max)
    {
        bool Listed(NoticeEntry entry) => entry.NavidromeId is { } id && present.Contains(id);

        // Queued and gone from the playlist: the person removed it by hand, which is an answer. A
        // duplicate group is one question, so taking any copy out answers it for the whole group.
        var removedByHand = entries
            .Where(entry => entry.State == NoticeState.Queued && entry.NavidromeId is not null && !Listed(entry))
            .ToList();
        var answeredGroups = removedByHand.Where(entry => entry.GroupKey is not null)
            .Select(entry => entry.GroupKey!).ToHashSet(StringComparer.Ordinal);
        var removedKeys = removedByHand.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var dismiss = entries
            .Where(entry => removedKeys.Contains(entry.Key)
                || (entry.IsOpen && entry.GroupKey is { } group && answeredGroups.Contains(group)))
            .Select(entry => entry.Key).ToList();
        var dismissing = dismiss.ToHashSet(StringComparer.Ordinal);
        bool StillAsking(NoticeEntry entry) => entry.IsOpen && !dismissing.Contains(entry.Key);

        // Waiting but already in the playlist (a restart between adding and recording, or the
        // person added it themselves): count it as asked rather than adding it twice.
        var adopt = entries
            .Where(entry => entry.State == NoticeState.Waiting && Listed(entry) && StillAsking(entry))
            .Select(entry => entry.Key).ToList();

        // Settled but still listed: take it off. A track an open question still needs stays,
        // whatever an older, settled entry about it says; a duplicate group found again after one
        // expired would otherwise lose its own tracks.
        var needed = entries.Where(entry => StillAsking(entry) && entry.NavidromeId is not null)
            .Select(entry => entry.NavidromeId!).ToHashSet(StringComparer.Ordinal);
        var remove = entries
            .Where(entry => Listed(entry) && !needed.Contains(entry.NavidromeId!)
                && (!entry.IsOpen || dismissing.Contains(entry.Key)))
            .Select(entry => entry.NavidromeId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var asked = entries.Count(entry => entry.State == NoticeState.Queued && Listed(entry) && StillAsking(entry))
            + adopt.Count;
        var room = Math.Max(0, max - asked);

        var waiting = entries
            .Where(entry => entry.State == NoticeState.Waiting && entry.NavidromeId is not null && !Listed(entry)
                && StillAsking(entry))
            .ToList();

        // A duplicate pair only makes sense together, so a group goes in whole or waits.
        var add = new List<NoticeEntry>();
        foreach (var group in waiting.GroupBy(entry => entry.GroupKey ?? entry.Key)
                     .OrderBy(group => group.Min(entry => entry.CreatedUtc)))
        {
            var members = group.OrderBy(entry => entry.Order).ToList();
            if (members.Count > room) { if (group.Key == members[0].Key) break; continue; }
            add.AddRange(members);
            room -= members.Count;
            if (room == 0) break;
        }
        return new NoticePlan(dismiss, adopt, remove, add);
    }
}

/// <summary>
/// Fills and tidies the playlists Octo uses to ask a person something (#47), and sends back the
/// answers AcoustID can use.
///
/// Every tick reads its settings afresh, so switching Review on or off needs no restart. The
/// playlists themselves are created by LibraryActionPlaylistProvisioner with the person's own
/// credentials the next time they list their playlists; this worker only ever adds and removes
/// tracks, as the admin.
/// </summary>
public sealed class NoticePlaylistWorker : BackgroundService
{
    private const int LookupsPerSweep = 50;
    private const int SubmissionBatch = 10;

    private readonly NoticeQueue _queue;
    private readonly NavidromePlaylistApi _api;
    private readonly NavidromeSongPathResolver _resolver;
    private readonly NavidromeIdentityService _identity;
    private readonly AcoustIdClient _acoustId;
    private readonly MusicBrainzClient _musicBrainz;
    private readonly IOptionsMonitor<LibraryActionSettings> _settings;
    private readonly IOptionsMonitor<SoulseekSettings> _soulseek;
    private readonly ILogger<NoticePlaylistWorker> _logger;

    public NoticePlaylistWorker(NoticeQueue queue, NavidromePlaylistApi api, NavidromeSongPathResolver resolver,
        NavidromeIdentityService identity, AcoustIdClient acoustId, MusicBrainzClient musicBrainz,
        IOptionsMonitor<LibraryActionSettings> settings, IOptionsMonitor<SoulseekSettings> soulseek,
        ILogger<NoticePlaylistWorker> logger)
    {
        _queue = queue;
        _api = api;
        _resolver = resolver;
        _identity = identity;
        _acoustId = acoustId;
        _musicBrainz = musicBrainz;
        _settings = settings;
        _soulseek = soulseek;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _settings.CurrentValue;
            // Per-sweep catch is mandatory: BackgroundServiceExceptionBehavior defaults to
            // StopHost, so one unhandled exception here would take Octo down.
            try
            {
                if (settings.Enabled && settings.NoticesEnabled && _identity.HasAdminIdentity)
                    await SweepAsync(settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Notice playlist sweep failed"); }

            try { await Task.Delay(settings.EffectivePollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(LibraryActionSettings settings, CancellationToken ct)
    {
        await ResolveIdsAsync(ct);

        var playlists = await _api.ListPlaylistsAsync(ct);
        foreach (var user in (settings.AllowedUsers ?? []).Where(user => !string.IsNullOrWhiteSpace(user)))
        foreach (var kind in settings.EnabledNoticeKinds())
        {
            try { await ReconcileAsync(user.Trim(), kind, settings, playlists, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not update {Kind} for {User}: {M}", kind, user, ex.Message);
            }
        }

        await SubmitKeptAsync(settings, ct);
        _queue.Flush();
    }

    /// <summary>A new download only has a Navidrome id once Navidrome has scanned it.</summary>
    private async Task ResolveIdsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _queue.DueForLookup(now, LookupsPerSweep))
        {
            if (!File.Exists(entry.LocalPath) || now - entry.CreatedUtc > TimeSpan.FromDays(7))
            {
                _queue.Resolve(entry.Key, NoticeState.Expired);
                continue;
            }
            var id = await _resolver.FindIdByPathAsync(entry.Artist, entry.Title, entry.LocalPath, ct);
            if (id is null) _queue.DeferLookup(entry.Key, now);
            else _queue.SetNavidromeId(entry.Key, id);
        }
    }

    private async Task ReconcileAsync(string user, NoticeKind kind, LibraryActionSettings settings,
        IReadOnlyList<LibraryActionPlaylistWorker.PlaylistRow> playlists, CancellationToken ct)
    {
        var title = settings.NoticeTitle(kind);
        var playlist = playlists.FirstOrDefault(row =>
            row.Owner.Equals(user, StringComparison.OrdinalIgnoreCase)
            && row.Name.Equals(title, StringComparison.OrdinalIgnoreCase));
        // Created with the person's own credentials the next time they list their playlists.
        if (playlist is null) return;

        var tracks = await _api.ListTracksAsync(playlist.Id, ct);
        var present = tracks.Select(track => track.MediaFileId).ToHashSet(StringComparer.Ordinal);
        var plan = NoticeReconcile.Plan(_queue.ForUser(user, kind), present, settings.EffectiveNoticeMaxTracks);

        foreach (var key in plan.Dismiss) _queue.Resolve(key, NoticeState.Dismissed);
        _queue.MarkQueued(plan.Adopt);

        if (plan.Remove.Count > 0)
        {
            var positions = tracks.Where(track => plan.Remove.Contains(track.MediaFileId, StringComparer.Ordinal))
                .Select(track => track.Position).Where(position => position.Length > 0).ToList();
            await _api.RemovePositionsAsync(playlist.Id, positions, ct);
        }

        if (plan.Add.Count > 0 && await _api.AddTracksAsync(playlist.Id, plan.Add.Select(entry => entry.NavidromeId!).ToList(), ct))
            _queue.MarkQueued(plan.Add.Select(entry => entry.Key));

        if (plan.Dismiss.Count + plan.Remove.Count + plan.Add.Count > 0)
            _logger.LogInformation("{Kind} for {User}: {Added} added, {Removed} settled, {Dismissed} dismissed",
                kind, user, plan.Add.Count, plan.Remove.Count, plan.Dismiss.Count);
    }

    /// <summary>
    /// Send the fingerprints people confirmed. Only with consent and a user key, never in a dry
    /// run, never for a track AcoustID confidently called something else, only for the standard
    /// 120-second fingerprint AcoustID's own tools make, and only with one unambiguous recording.
    /// </summary>
    private async Task SubmitKeptAsync(LibraryActionSettings settings, CancellationToken ct)
    {
        var soulseek = _soulseek.CurrentValue;
        if (!MaySubmit(settings, soulseek)) return;

        var ready = new List<(NoticeEntry Entry, AcoustIdSubmission Item)>();
        var refused = new List<string>();
        foreach (var entry in _queue.AwaitingSubmission())
        {
            if (!Submittable(entry, soulseek))
            {
                refused.Add(entry.Key);
                continue;
            }
            var recording = entry.CandidateRecordingId
                ?? await _musicBrainz.FindRecordingAsync(entry.Artist, entry.Title, entry.DurationSeconds, ct);
            if (recording is null)
            {
                _logger.LogInformation("Kept '{Artist} - {Title}', but no single MusicBrainz recording fits it, so nothing was sent",
                    entry.Artist, entry.Title);
                refused.Add(entry.Key);
                continue;
            }
            ready.Add((entry, new AcoustIdSubmission(entry.Fingerprint!, entry.DurationSeconds, recording, entry.FileFormat)));
        }
        if (refused.Count > 0) _queue.MarkSubmitted(refused, sent: false);

        foreach (var batch in ready.Chunk(SubmissionBatch))
        {
            if (!await _acoustId.SubmitAsync(soulseek.AcoustIdApiKey, soulseek.AcoustIdUserApiKey,
                    batch.Select(pair => pair.Item).ToList(), soulseek.EffectiveAcoustIdTimeoutSeconds))
                continue;
            _queue.MarkSubmitted(batch.Select(pair => pair.Entry.Key), sent: true);
            _logger.LogInformation("Sent {Count} confirmed fingerprint(s) to AcoustID", batch.Length);
        }
    }

    /// <summary>Consent and both keys, and never in a dry run.</summary>
    internal static bool MaySubmit(LibraryActionSettings settings, SoulseekSettings soulseek) =>
        soulseek.SubmitConfirmedFingerprints && !settings.DryRun
        && !string.IsNullOrWhiteSpace(soulseek.AcoustIdApiKey) && !string.IsNullOrWhiteSpace(soulseek.AcoustIdUserApiKey);

    /// <summary>
    /// Only what AcoustID could not place: a track it confidently named as something else was
    /// kept for a reason that is not "AcoustID is missing this". And only the standard 120-second
    /// fingerprint AcoustID's own tools make, so a shortened one never lands beside them.
    /// Never a library sweep question: its tags were never confirmed by anyone.
    /// </summary>
    internal static bool Submittable(NoticeEntry entry, SoulseekSettings soulseek) =>
        entry.Origin == NoticeOrigin.Download
        && entry.Cause is InconclusiveReason.NoEntry or InconclusiveReason.BelowThreshold
        && soulseek.FingerprintSeconds == 120
        && entry.DurationSeconds > 0
        && !string.IsNullOrEmpty(entry.Fingerprint);
}
