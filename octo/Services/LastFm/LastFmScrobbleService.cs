using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Admin;

namespace Octo.Services.LastFm;

/// <summary>What Last.fm is told about one play.</summary>
public sealed record LastFmTrack(string Artist, string Title, string? Album, int? DurationSeconds);

/// <summary>One Navidrome user as the dashboard's Last.fm scrobbling card shows them.</summary>
/// <param name="ApprovalUrl">The last.fm page that approves Octo, while a Connect is waiting on it.</param>
/// <param name="LastSent">The latest play Last.fm took for this listener since Octo started.</param>
public sealed record LastFmScrobbleUser(string User, bool Connected, string? LastFmUser,
    bool AwaitingApproval, string? Notice, string? ApprovalUrl = null, LastFmSentPlay? LastSent = null);

/// <summary>A play Last.fm accepted, as the dashboard shows it.</summary>
public sealed record LastFmSentPlay(string Artist, string Title, DateTime PlayedAtUtc);

/// <summary>What Last.fm said of an API key and shared secret, for the dashboard to show beside
/// each field. <paramref name="Key"/> is ok, invalid, missing or unreachable;
/// <paramref name="Secret"/> is ok, invalid, same-as-key, missing or unchecked.</summary>
public sealed record LastFmCredentialCheck(string Key, string Secret, string? Message = null);

/// <summary>A Last.fm refusal the dashboard can show as it is.</summary>
public sealed class LastFmScrobbleException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Scrobbles plays to each listener's own Last.fm, and runs the dashboard's Connect flow that
/// links a Navidrome user to a Last.fm account.
///
/// Outside songs always come through here, since Navidrome has never heard of them. Library
/// songs come too unless the admin leaves them to Navidrome (<see cref="TakesLibraryPlays"/>),
/// for a Navidrome linked to Last.fm itself. Nothing waits on Last.fm: a play is queued and the
/// client has its answer straight away. The queue is in memory and bounded, like the
/// ListenBrainz path it sits beside, so an outage costs at most the plays still waiting when
/// Octo restarts.
/// </summary>
public sealed class LastFmScrobbleService
{
    public const string ClientName = "lastfm-scrobble";
    internal const string ApiUrl = "https://ws.audioscrobbler.com/2.0/";
    internal const string AuthUrl = "https://www.last.fm/api/auth/";

    /// <summary>The most plays Last.fm takes in one track.scrobble call.</summary>
    internal const int MaxBatch = 50;
    /// <summary>Plays kept per listener while Last.fm is unreachable. Past this the oldest go.</summary>
    internal const int MaxQueuedPerUser = 500;
    /// <summary>Tries per play before it is given up on.</summary>
    internal const int MaxAttempts = 5;

    internal const int ErrorInvalidSession = 9;
    internal const int ErrorTokenNotAuthorized = 14;
    internal const int ErrorTokenExpired = 15;
    internal const int ErrorRateLimited = 29;

    // Last.fm's auth tokens last an hour. A little less, so Finish never races the expiry.
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(55);
    private static readonly TimeSpan LongestRetryWait = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LongestPause = TimeSpan.FromHours(1);

    /// <summary>Last.fm ignores a play older than two weeks, so one is not queued at all.</summary>
    internal static readonly TimeSpan OldestPlay = TimeSpan.FromDays(14);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<LastFmSettings> _settings;
    private readonly SettingsFileWriter _settingsFile;
    private readonly ILogger<LastFmScrobbleService> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<PendingScrobble>> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _retryAt = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _pausedUntil = DateTime.MinValue;
    private int _rateLimitStrikes;
    private bool _draining;
    // Cuts the drain's wait short when a play arrives, so one listener resting for an hour
    // does not hold back everyone else's.
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _nowPlayingInFlight;

    // A session Last.fm refused, or one disconnected on the dashboard. Settings reload a moment
    // after the file changes, and one set in the environment is not in the file at all, so this
    // is what stops the key being used again in the meantime.
    private readonly ConcurrentDictionary<string, byte> _revokedKeys = new(StringComparer.Ordinal);
    // When Last.fm first refused a session with error 9. The session rests until the grace is
    // over, its plays kept and still queued; only a second refusal after that removes it from
    // settings.json and drops them, since one error 9 has been known to be Last.fm's hiccup
    // rather than the listener revoking Octo.
    private readonly ConcurrentDictionary<string, DateTime> _refusedAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Token, DateTime Expires)> _pendingApprovals =
        new(StringComparer.OrdinalIgnoreCase);
    // A session Finish has just written. settings.json reaches the running settings a moment
    // after the write, and until then the listener would read as not connected, so the session
    // is used from here until the reloaded settings carry the same key.
    private readonly ConcurrentDictionary<string, LastFmUserSession> _justSaved = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LastFmSentPlay> _lastSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _notices = new(StringComparer.OrdinalIgnoreCase);

    public LastFmScrobbleService(IHttpClientFactory httpClientFactory,
        IOptionsMonitor<LastFmSettings> settings, SettingsFileWriter settingsFile,
        ILogger<LastFmScrobbleService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _settingsFile = settingsFile;
        _logger = logger;
    }

    /// <summary>First wait after a failed batch; each further failure doubles it.</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long every call stops after Last.fm says Octo is calling too often.</summary>
    internal TimeSpan RateLimitPause { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a session Last.fm refused rests before it is tried once more.</summary>
    internal TimeSpan RefusalGrace { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The clock every pause and wait is measured on. Tests move it by hand.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    private DateTime Now => Time.GetUtcNow().UtcDateTime;

    /// <summary>Plays queued or being sent, and Now Playing calls still out. Tests wait on it.</summary>
    internal int Outstanding
    {
        get
        {
            lock (_gate) return _queues.Values.Sum(queue => queue.Count) + Volatile.Read(ref _nowPlayingInFlight);
        }
    }

    /// <summary>True when the API key and shared secret are both saved, which Connect needs.</summary>
    public bool IsReady => IsReadyWith(_settings.CurrentValue);

    /// <summary>True when this listener's plays would be sent to Last.fm. A session resting
    /// after one refusal still counts: its plays wait for it.</summary>
    public bool IsEnabledFor(string username)
    {
        var settings = _settings.CurrentValue;
        return settings.ScrobbleExternalPlays && IsReadyWith(settings) && SavedSession(settings, username) is not null;
    }

    /// <summary>True when library plays go to Last.fm from here too, not only outside ones.</summary>
    public bool TakesLibraryPlays => _settings.CurrentValue.ScrobbleLibraryPlays;

    /// <summary>Tells Last.fm what the listener has just started. Not retried: by the time a
    /// retry landed the song would be over.</summary>
    public void NowPlaying(string username, LastFmTrack track)
    {
        if (!IsEnabledFor(username) || ActiveSession(_settings.CurrentValue, username) is null || !Usable(track)) return;
        Interlocked.Increment(ref _nowPlayingInFlight);
        _ = Task.Run(async () =>
        {
            try { await SendNowPlayingAsync(username.Trim(), track); }
            catch (Exception ex) { _logger.LogDebug(ex, "Last.fm Now Playing failed for {User}", username); }
            finally { Interlocked.Decrement(ref _nowPlayingInFlight); }
        });
    }

    /// <summary>
    /// Queues one completed play. The client already decided it counts (Last.fm asks for half
    /// the song or four minutes); the rules left to Octo are that Last.fm takes nothing
    /// shorter than 30 seconds and nothing played more than two weeks ago.
    /// </summary>
    /// <param name="chosenByUser">False for a play the listener did not pick, such as the next
    /// song on an Octo radio stream. Last.fm is told so.</param>
    public void Scrobble(string username, LastFmTrack track, DateTime playedAtUtc, bool chosenByUser = true)
    {
        if (!IsEnabledFor(username) || !Usable(track)) return;
        if (track.DurationSeconds is > 0 and < 30) return;
        if (DateTime.SpecifyKind(playedAtUtc, DateTimeKind.Utc) < Now - OldestPlay) return;
        var user = username.Trim();
        lock (_gate)
        {
            if (!_queues.TryGetValue(user, out var queue)) _queues[user] = queue = [];
            if (queue.Count >= MaxQueuedPerUser)
            {
                _logger.LogWarning("Last.fm queue for {User} is full; dropping the oldest play", user);
                queue.RemoveAt(0);
            }
            queue.Add(new PendingScrobble(track, DateTime.SpecifyKind(playedAtUtc, DateTimeKind.Utc), chosenByUser));
            if (_draining)
            {
                if (_wake.CurrentCount == 0) _wake.Release();
                return;
            }
            _draining = true;
        }
        _ = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Starts linking a Navidrome user to Last.fm: gets a request token and returns the page the
    /// admin opens to approve Octo. This is Last.fm's desktop flow, which needs no address Last.fm
    /// can call back to, so it works for an Octo nobody outside the house can reach.
    /// </summary>
    public async Task<string> BeginConnectAsync(string username, CancellationToken cancellationToken)
    {
        var user = RequireUsername(username);
        var settings = _settings.CurrentValue;
        if (!IsReadyWith(settings))
            throw new LastFmScrobbleException("Save the Last.fm API key and shared secret first.");
        var reply = await CallAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = "auth.getToken",
            ["api_key"] = settings.ApiKey.Trim(),
        }, settings.ApiSecret.Trim(), cancellationToken);
        var token = reply.Body?["token"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(token))
            throw new LastFmScrobbleException(reply.Ok ? "Last.fm sent no token." : Describe(reply), reply.Error);
        _pendingApprovals[user] = (token, Now + TokenLifetime);
        return ApprovalUrl(settings, token);
    }

    /// <summary>Finishes linking once the admin has approved Octo on last.fm, and saves the
    /// session for this Navidrome user.</summary>
    public async Task<LastFmUserSession> FinishConnectAsync(string username, CancellationToken cancellationToken)
    {
        var user = RequireUsername(username);
        var settings = _settings.CurrentValue;
        if (!IsReadyWith(settings))
            throw new LastFmScrobbleException("Save the Last.fm API key and shared secret first.");
        if (!_pendingApprovals.TryGetValue(user, out var pending) || pending.Expires < Now)
        {
            _pendingApprovals.TryRemove(user, out _);
            throw new LastFmScrobbleException("Start with Connect. An approval link lasts an hour.");
        }

        var reply = await CallAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = "auth.getSession",
            ["api_key"] = settings.ApiKey.Trim(),
            ["token"] = pending.Token,
        }, settings.ApiSecret.Trim(), cancellationToken);
        if (!reply.Ok)
        {
            if (reply.Error == ErrorTokenNotAuthorized)
                throw new LastFmScrobbleException(
                    "Last.fm has not seen the approval yet. Open the link, allow access, then Finish.", reply.Error);
            if (reply.Error is ErrorTokenExpired or 4) _pendingApprovals.TryRemove(user, out _);
            throw new LastFmScrobbleException(reply.Error is ErrorTokenExpired or 4
                ? "That approval link has expired. Connect again." : Describe(reply), reply.Error);
        }

        var key = reply.Body?["session"]?["key"]?.GetValue<string>();
        var name = reply.Body?["session"]?["name"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(key)) throw new LastFmScrobbleException("Last.fm sent no session key.");
        var session = new LastFmUserSession { SessionKey = key, LastFmUser = name };

        _settingsFile.Update(root =>
        {
            var sessions = SessionsIn(root, create: true)!;
            foreach (var existing in KeysFor(sessions, user)) sessions.Remove(existing);
            sessions[user] = new JsonObject { ["SessionKey"] = key, ["LastFmUser"] = name };
            return true;
        });
        _justSaved[user] = session;
        _pendingApprovals.TryRemove(user, out _);
        _revokedKeys.TryRemove(key, out _);
        _refusedAt.TryRemove(key, out _);
        _notices.TryRemove(user, out _);
        lock (_gate)
        {
            // Plays that waited on a refused session go with the new one straight away.
            _retryAt.Remove(user);
            if (_draining && _wake.CurrentCount == 0) _wake.Release();
        }
        _logger.LogInformation("Last.fm connected for {User} as {LastFmUser}", user, name);
        return session;
    }

    /// <summary>Forgets a Connect that is still waiting on its approval.</summary>
    public void CancelConnect(string username)
    {
        if (!string.IsNullOrWhiteSpace(username)) _pendingApprovals.TryRemove(username.Trim(), out _);
    }

    /// <summary>
    /// Asks Last.fm whether an API key and shared secret work together, before the dashboard saves
    /// them. A blank argument means the saved value. One signed auth.getSession with a token that
    /// was never issued answers all three: error 10 is an unknown key, 13 a signature the secret
    /// does not make for that key, and 4 (the token) means both are right. auth.getToken would
    /// not do: Last.fm hands out a token whatever the signature.
    /// </summary>
    public async Task<LastFmCredentialCheck> CheckCredentialsAsync(string? apiKey, string? apiSecret,
        CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        var key = (string.IsNullOrWhiteSpace(apiKey) ? settings.ApiKey : apiKey).Trim();
        var secret = (string.IsNullOrWhiteSpace(apiSecret) ? settings.ApiSecret : apiSecret).Trim();
        if (key.Length == 0) return new("missing", secret.Length == 0 ? "missing" : "unchecked");
        // The two look alike, and the key is the one Last.fm shows first, so this is the likely mix-up.
        var secretState = secret.Length == 0 ? "missing"
            : string.Equals(secret, key, StringComparison.OrdinalIgnoreCase) ? "same-as-key" : null;

        var reply = await CallAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = "auth.getSession",
            ["api_key"] = key,
            ["token"] = "00000000000000000000000000000000",
        }, secretState is null ? secret : "", cancellationToken);
        return reply.Error switch
        {
            10 or 26 => new("invalid", secretState ?? "unchecked", reply.Message),
            13 => new("ok", secretState ?? "invalid"),
            4 or 14 or 15 => new("ok", secretState ?? "ok"),
            0 when reply.Ok => new("ok", secretState ?? "ok"),
            _ => new("unreachable", secretState ?? "unchecked", Describe(reply)),
        };
    }

    /// <summary>
    /// Stops scrobbling for this user and forgets the session. Returns false when the session
    /// was not in settings.json, which means the environment sets it: it stops now, and comes back
    /// after a restart unless it is removed there too.
    /// </summary>
    public bool Disconnect(string username)
    {
        var user = RequireUsername(username);
        if (SavedSession(_settings.CurrentValue, user) is { } current)
        {
            _revokedKeys[current.SessionKey] = 0;
            _refusedAt.TryRemove(current.SessionKey, out _);
        }
        _justSaved.TryRemove(user, out _);
        _pendingApprovals.TryRemove(user, out _);
        _notices.TryRemove(user, out _);
        _lastSent.TryRemove(user, out _);
        lock (_gate)
        {
            _queues.Remove(user);
            _retryAt.Remove(user);
        }
        var removed = RemoveSavedSession(user, onlyKey: null);
        _logger.LogInformation("Last.fm disconnected for {User}", user);
        return removed;
    }

    /// <summary>Everyone the dashboard should list: the users it already knows of, plus anyone
    /// connected, part way through connecting, or with a notice waiting.</summary>
    public IReadOnlyList<LastFmScrobbleUser> Users(IEnumerable<string> knownUsers)
    {
        var settings = _settings.CurrentValue;
        var now = Now;
        return knownUsers.Concat(settings.UserSessions.Keys).Concat(_justSaved.Keys)
            .Concat(_pendingApprovals.Keys).Concat(_notices.Keys)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                var session = ActiveSession(settings, name);
                var waiting = _pendingApprovals.TryGetValue(name, out var pending) && pending.Expires > now;
                return new LastFmScrobbleUser(name, session is not null,
                    session is null ? null : NullIfEmpty(session.LastFmUser),
                    waiting,
                    _notices.TryGetValue(name, out var notice) ? notice : null,
                    waiting && IsReadyWith(settings) ? ApprovalUrl(settings, pending.Token) : null,
                    session is not null && _lastSent.TryGetValue(name, out var sent) ? sent : null);
            })
            .ToList();
    }

    /// <summary>
    /// Last.fm's request signature: every parameter except format and callback, sorted by name,
    /// each name followed by its value, the shared secret on the end, then an MD5 of the UTF-8.
    /// See last.fm/api/authspec, section 8.
    /// </summary>
    internal static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string secret)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in parameters
                     .Where(pair => pair.Key is not ("format" or "callback" or "api_sig"))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            text.Append(name).Append(value);
        text.Append(secret);
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private async Task SendNowPlayingAsync(string user, LastFmTrack track)
    {
        var settings = _settings.CurrentValue;
        var session = ActiveSession(settings, user);
        lock (_gate) if (Now < _pausedUntil) return;
        if (session is null) return;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = "track.updateNowPlaying",
            ["api_key"] = settings.ApiKey.Trim(),
            ["sk"] = session.SessionKey.Trim(),
            ["artist"] = track.Artist.Trim(),
            ["track"] = track.Title.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(track.Album)) parameters["album"] = track.Album.Trim();
        if (track.DurationSeconds is > 0)
            parameters["duration"] = track.DurationSeconds.Value.ToString(CultureInfo.InvariantCulture);
        var reply = await CallAsync(parameters, settings.ApiSecret.Trim(), CancellationToken.None);
        if (reply.Ok)
        {
            Accepted(user, session.SessionKey);
            return;
        }
        if (reply.Error == ErrorInvalidSession) MarkDisconnected(user, session.SessionKey, reply.Message);
        else if (reply.Error == ErrorRateLimited) lock (_gate) Pause();
        else _logger.LogDebug("Last.fm refused Now Playing for {User}: {Detail}", user, Describe(reply));
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            string user;
            List<PendingScrobble> batch;
            TimeSpan wait;
            lock (_gate)
            {
                var now = Now;
                (user, batch, wait) = NextBatch(now);
                if (batch.Count == 0 && wait <= TimeSpan.Zero)
                {
                    _draining = false;
                    return;
                }
            }
            if (batch.Count == 0)
            {
                // A new play wakes the loop early; otherwise it rests until the pause ends,
                // timed on the service's clock.
                using (var rest = new CancellationTokenSource(wait, Time))
                {
                    try { await _wake.WaitAsync(rest.Token); }
                    catch (OperationCanceledException) { }
                }
                continue;
            }
            try { await SendBatchAsync(user, batch); }
            catch (Exception ex)
            {
                // A bug here must not leave plays stuck with nothing draining them.
                _logger.LogWarning(ex, "Last.fm scrobble batch for {User} failed unexpectedly", user);
                lock (_gate) Forget(user, batch);
            }
        }
    }

    /// <summary>The next batch that may go now, or how long until one may. Called under the gate.</summary>
    private (string User, List<PendingScrobble> Batch, TimeSpan Wait) NextBatch(DateTime now)
    {
        if (_queues.Count == 0) return ("", [], TimeSpan.Zero);
        if (now < _pausedUntil) return ("", [], _pausedUntil - now);
        var soonest = DateTime.MaxValue;
        foreach (var (user, queue) in _queues)
        {
            var at = _retryAt.GetValueOrDefault(user, DateTime.MinValue);
            if (at <= now) return (user, queue.Take(MaxBatch).ToList(), TimeSpan.Zero);
            if (at < soonest) soonest = at;
        }
        return ("", [], soonest - now);
    }

    private async Task SendBatchAsync(string user, List<PendingScrobble> batch)
    {
        var settings = _settings.CurrentValue;
        var session = SavedSession(settings, user);
        if (!settings.ScrobbleExternalPlays || !IsReadyWith(settings) || session is null)
        {
            // Disconnected, or switched off, while these waited.
            lock (_gate) Forget(user, batch);
            return;
        }
        if (RestingUntil(session.SessionKey) is { } resting)
        {
            // Refused once, by a scrobble or a Now Playing: the plays wait out the grace.
            lock (_gate) _retryAt[user] = resting;
            return;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = "track.scrobble",
            ["api_key"] = settings.ApiKey.Trim(),
            ["sk"] = session.SessionKey.Trim(),
        };
        for (var index = 0; index < batch.Count; index++)
        {
            var (track, playedAt) = (batch[index].Track, batch[index].PlayedAtUtc);
            parameters[$"artist[{index}]"] = track.Artist.Trim();
            parameters[$"track[{index}]"] = track.Title.Trim();
            parameters[$"timestamp[{index}]"] = new DateTimeOffset(playedAt).ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(track.Album)) parameters[$"album[{index}]"] = track.Album.Trim();
            if (track.DurationSeconds is > 0)
                parameters[$"duration[{index}]"] = track.DurationSeconds.Value.ToString(CultureInfo.InvariantCulture);
            if (!batch[index].ChosenByUser) parameters[$"chosenByUser[{index}]"] = "0";
        }

        var reply = await CallAsync(parameters, settings.ApiSecret.Trim(), CancellationToken.None);
        if (reply.Ok)
        {
            var attributes = reply.Body?["scrobbles"]?["@attr"];
            _logger.LogInformation("Last.fm took {Accepted} of {Count} plays for {User} ({Ignored} ignored)",
                Number(attributes?["accepted"]) ?? batch.Count, batch.Count, user, Number(attributes?["ignored"]) ?? 0);
            lock (_gate)
            {
                _rateLimitStrikes = 0;
                _retryAt.Remove(user);
                Forget(user, batch);
            }
            Accepted(user, session.SessionKey);
            if ((Number(attributes?["accepted"]) ?? batch.Count) > 0)
            {
                var latest = batch.MaxBy(play => play.PlayedAtUtc)!;
                _lastSent[user] = new LastFmSentPlay(latest.Track.Artist.Trim(), latest.Track.Title.Trim(), latest.PlayedAtUtc);
            }
            return;
        }

        switch (reply.Error)
        {
            case ErrorInvalidSession:
                MarkDisconnected(user, session.SessionKey, reply.Message);
                lock (_gate)
                {
                    if (_revokedKeys.ContainsKey(session.SessionKey))
                    {
                        // Refused again after the grace: the listener did revoke Octo.
                        _queues.Remove(user);
                        _retryAt.Remove(user);
                    }
                    else
                    {
                        // The first refusal. The plays stay queued and nothing is sent for this
                        // listener until the grace is over.
                        _retryAt[user] = RestingUntil(session.SessionKey) ?? Now + RefusalGrace;
                    }
                }
                return;
            case ErrorRateLimited:
                lock (_gate) Pause();
                _logger.LogWarning("Last.fm asked Octo to slow down; scrobbles wait before the next try");
                return;
            case var _ when reply.Retryable:
                // Unreachable, a server error, or one of Last.fm's own "try again later"
                // answers (11, 16). Error 8 is not one: it comes back the same each time.
                lock (_gate)
                {
                    var attempts = 0;
                    foreach (var play in batch) attempts = Math.Max(attempts, ++play.Attempts);
                    var spent = batch.Where(play => play.Attempts >= MaxAttempts).ToList();
                    if (spent.Count > 0)
                    {
                        _logger.LogWarning("Giving up on {Count} Last.fm plays for {User} after {Attempts} tries: {Detail}",
                            spent.Count, user, MaxAttempts, Describe(reply));
                        Forget(user, spent);
                    }
                    var delay = TimeSpan.FromTicks(Math.Min(LongestRetryWait.Ticks,
                        RetryDelay.Ticks * (1L << Math.Min(attempts - 1, 10))));
                    _retryAt[user] = Now + delay;
                }
                return;
            default:
                // Anything else will be refused the same way next time, so retrying only delays the rest.
                _logger.LogWarning("Last.fm refused {Count} plays for {User}: {Detail}", batch.Count, user, Describe(reply));
                lock (_gate) Forget(user, batch);
                return;
        }
    }

    /// <summary>Removes these plays from the user's queue. Called under the gate.</summary>
    private void Forget(string user, IReadOnlyCollection<PendingScrobble> plays)
    {
        if (!_queues.TryGetValue(user, out var queue)) return;
        queue.RemoveAll(plays.Contains);
        if (queue.Count > 0) return;
        _queues.Remove(user);
        _retryAt.Remove(user);
    }

    /// <summary>Stops every call for a while, longer each time Last.fm says so again. Called under the gate.</summary>
    private void Pause()
    {
        _rateLimitStrikes++;
        var pause = TimeSpan.FromTicks(Math.Min(LongestPause.Ticks,
            RateLimitPause.Ticks * (1L << Math.Min(_rateLimitStrikes - 1, 10))));
        _pausedUntil = Now + pause;
    }

    /// <summary>
    /// Last.fm no longer accepts this session, which is what happens when the listener removes
    /// Octo from their Last.fm applications. The first refusal rests the session in memory and
    /// the dashboard says why; the saved session is kept, and so are the listener's plays, which
    /// keep queueing, because Last.fm has been known to say this once and mean nothing by it.
    /// After <see cref="RefusalGrace"/> the session is tried again, and a second refusal then
    /// removes it from settings.json for good.
    /// </summary>
    private void MarkDisconnected(string user, string sessionKey, string detail)
    {
        var now = Now;
        var why = string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail.Trim()})";
        if (_refusedAt.TryGetValue(sessionKey, out var first) && now - first >= RefusalGrace)
        {
            _revokedKeys[sessionKey] = 0;
            _refusedAt.TryRemove(sessionKey, out _);
            _notices[user] = $"Last.fm stopped accepting this connection on {now:yyyy-MM-dd HH:mm} UTC{why}"
                             + ". Connect again to resume scrobbling.";
            _logger.LogWarning("Last.fm refused the session for {User} again; it is removed until they connect again", user);
            RemoveSavedSession(user, onlyKey: sessionKey);
            return;
        }
        _refusedAt.TryAdd(sessionKey, now);
        _notices[user] = $"Last.fm refused this connection on {now:yyyy-MM-dd HH:mm} UTC{why}"
                         + ". Scrobbling for this listener is paused and their plays wait. Octo tries once more after an hour and"
                         + " removes the connection if Last.fm still refuses it. Connect again to resume now.";
        _logger.LogWarning("Last.fm refused the session for {User}; scrobbling for them is paused", user);
    }

    /// <summary>A call with this session went through, so an earlier refusal was not meant.</summary>
    private void Accepted(string user, string sessionKey)
    {
        if (_refusedAt.TryRemove(sessionKey, out _)) _notices.TryRemove(user, out _);
    }

    /// <summary>
    /// Removes the user's saved session. With <paramref name="onlyKey"/>, only when it is still
    /// that key, so a refusal of an old session cannot undo a reconnect that happened meanwhile.
    /// </summary>
    private bool RemoveSavedSession(string user, string? onlyKey)
    {
        try
        {
            return _settingsFile.Update(root =>
            {
                if (SessionsIn(root, create: false) is not { } sessions) return false;
                var matches = KeysFor(sessions, user)
                    .Where(key => onlyKey is null
                                  || string.Equals((sessions[key] as JsonObject)?["SessionKey"]?.GetValue<string>(),
                                      onlyKey, StringComparison.Ordinal))
                    .ToList();
                foreach (var key in matches) sessions.Remove(key);
                return matches.Count > 0;
            });
        }
        catch (Exception ex)
        {
            // The key is already revoked in memory, so this only matters after a restart.
            _logger.LogWarning(ex, "Could not remove the Last.fm session for {User} from settings", user);
            return false;
        }
    }

    /// <summary>The listener's session unless Last.fm has revoked it. It may be resting.</summary>
    private LastFmUserSession? SavedSession(LastFmSettings settings, string username)
    {
        var session = settings.SessionFor(username);
        if (_justSaved.TryGetValue(username.Trim(), out var fresh))
        {
            // The reload has caught up once the settings carry the same key.
            if (session?.SessionKey == fresh.SessionKey) _justSaved.TryRemove(username.Trim(), out _);
            else session = fresh;
        }
        return session is not null && !_revokedKeys.ContainsKey(session.SessionKey) ? session : null;
    }

    private static string ApprovalUrl(LastFmSettings settings, string token) =>
        $"{AuthUrl}?api_key={Uri.EscapeDataString(settings.ApiKey.Trim())}&token={Uri.EscapeDataString(token)}";

    /// <summary>The listener's session when it may be used right now: not revoked, and not
    /// resting after a refusal.</summary>
    private LastFmUserSession? ActiveSession(LastFmSettings settings, string username) =>
        SavedSession(settings, username) is { } session && RestingUntil(session.SessionKey) is null ? session : null;

    /// <summary>When a session Last.fm refused once may be tried again, or null when it is not
    /// resting.</summary>
    private DateTime? RestingUntil(string sessionKey) =>
        _refusedAt.TryGetValue(sessionKey, out var refused) && Now - refused < RefusalGrace
            ? refused + RefusalGrace : null;

    private async Task<Reply> CallAsync(Dictionary<string, string> parameters, string secret,
        CancellationToken cancellationToken)
    {
        parameters["api_sig"] = Sign(parameters, secret);
        parameters["format"] = "json";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
            {
                Content = new FormUrlEncodedContent(parameters),
            };
            using var response = await _httpClientFactory.CreateClient(ClientName).SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // Last.fm puts a refusal in the body under a 4xx status, so the body decides, not the status.
            JsonObject? document = null;
            try { document = JsonNode.Parse(body) as JsonObject; }
            catch (JsonException) { /* not JSON: judged by the status below */ }
            if (document?["error"] is JsonValue error && error.TryGetValue<int>(out var code))
                return new Reply(null, code, document["message"]?.GetValue<string>() ?? "",
                    Retryable: code is 11 or 16);
            if (response.IsSuccessStatusCode && document is not null) return new Reply(document, 0, "");
            return new Reply(null, 0, $"HTTP {(int)response.StatusCode}", Retryable: (int)response.StatusCode >= 500);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !cancellationToken.IsCancellationRequested)
        {
            return new Reply(null, 0, ex.Message, Retryable: true);
        }
    }

    private static string Describe(Reply reply) => reply.Error == 0
        ? $"Last.fm could not be reached: {reply.Message}"
        : $"Last.fm error {reply.Error}: {reply.Message}";

    private static bool IsReadyWith(LastFmSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.ApiSecret);

    private static bool Usable(LastFmTrack track) =>
        !string.IsNullOrWhiteSpace(track.Artist) && !string.IsNullOrWhiteSpace(track.Title);

    /// <summary>A Navidrome username fit to be a settings key. A colon would split it into two
    /// levels, because that is how configuration writes a path.</summary>
    private static string RequireUsername(string username)
    {
        var user = (username ?? "").Trim();
        if (user.Length is 0 or > 100 || user.Contains(':') || user.Any(char.IsControl))
            throw new LastFmScrobbleException("A Navidrome username is required.");
        return user;
    }

    private static JsonObject? SessionsIn(JsonObject root, bool create)
    {
        var sectionKey = root.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, "LastFm", StringComparison.OrdinalIgnoreCase));
        if (sectionKey is null || root[sectionKey] is not JsonObject section)
        {
            if (!create) return null;
            section = new JsonObject();
            root[sectionKey ?? "LastFm"] = section;
        }
        var sessionsKey = section.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, "UserSessions", StringComparison.OrdinalIgnoreCase));
        if (sessionsKey is not null && section[sessionsKey] is JsonObject sessions) return sessions;
        if (!create) return null;
        sessions = new JsonObject();
        section[sessionsKey ?? "UserSessions"] = sessions;
        return sessions;
    }

    private static List<string> KeysFor(JsonObject sessions, string user) =>
        sessions.Select(pair => pair.Key)
            .Where(key => string.Equals(key.Trim(), user, StringComparison.OrdinalIgnoreCase)).ToList();

    private static int? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out number) ? number : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <param name="Retryable">Worth sending again later: Last.fm unreachable, a server error,
    /// or its own "try again later" (11, 16).</param>
    private sealed record Reply(JsonObject? Body, int Error, string Message, bool Retryable = false)
    {
        public bool Ok => Body is not null;
    }

    private sealed class PendingScrobble(LastFmTrack track, DateTime playedAtUtc, bool chosenByUser)
    {
        public LastFmTrack Track { get; } = track;
        public DateTime PlayedAtUtc { get; } = playedAtUtc;
        public bool ChosenByUser { get; } = chosenByUser;
        public int Attempts { get; set; }
    }
}
