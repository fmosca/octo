using Microsoft.Extensions.Options;
using Octo.Models.Settings;

namespace Octo.Services.Soulseek;

public enum SoulseekLinkState { LoggedIn, NotLoggedIn, Unknown }

/// <summary>One reading of slskd's application state. State is slskd's own words, such as
/// "Disconnecting" or "Connected, LoggedIn". NextAttemptUtc is when slskd next tries to connect,
/// when it says.</summary>
public sealed record SoulseekServerReading(SoulseekLinkState Link, string? State, string? Username,
    DateTime? NextAttemptUtc);

/// <summary>
/// Whether slskd is logged in to Soulseek, for the parts of Octo that should wait for it rather
/// than settle for a lossy copy. An interface so the heart chain and the workers can be tested
/// against a scripted outage.
/// </summary>
public interface ISoulseekLink
{
    /// <summary>Null when slskd did not answer at all. Fresh skips the short cache.</summary>
    Task<SoulseekServerReading?> ReadAsync(bool fresh, CancellationToken ct);

    /// <summary>How long a Soulseek-first download waits for slskd to log back in. Zero is off.</summary>
    TimeSpan HoldLimit { get; }

    DateTime UtcNow { get; }

    /// <summary>
    /// Returns once slskd is logged in, cannot say, or <paramref name="deadlineUtc"/> has passed.
    /// True when Soulseek is worth trying; false when the wait ran out with slskd still out.
    /// </summary>
    Task<bool> WaitForLoginAsync(DateTime deadlineUtc, CancellationToken ct);
}

public sealed class SoulseekLink : ISoulseekLink
{
    /// <summary>What a request refused during an outage says.</summary>
    public const string OfflineText =
        "Soulseek is not connected (slskd is not logged in), so nothing changed. Octo tries again when it is back.";

    // Short enough that a heart just after slskd logs back in is not held for nothing, long
    // enough that forty held songs share one request.
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    // slskd itself retries about every five minutes, so looking more often finds nothing new.
    internal static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(30);

    private readonly IOptionsMonitor<SoulseekSettings> _settings;
    private readonly ILogger<SoulseekLink> _logger;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private SoulseekServerReading? _last;
    private DateTime _lastAt = DateTime.MinValue;
    private SoulseekLinkState? _lastLogged;

    public SoulseekLink(SoulseekClient client, IOptionsMonitor<SoulseekSettings> settings,
        ILogger<SoulseekLink> logger)
    {
        _settings = settings;
        _logger = logger;
        Read = ct => client.ReadServerAsync(ct);
    }

    // Seams, the same way SoulseekClient exposes Clock and PollInterval.
    internal Func<CancellationToken, Task<SoulseekServerReading?>> Read { get; set; }
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public DateTime UtcNow => Clock();

    public TimeSpan HoldLimit => TimeSpan.FromHours(_settings.CurrentValue.EffectiveOutageHoldHours);

    public async Task<SoulseekServerReading?> ReadAsync(bool fresh, CancellationToken ct)
    {
        await _readLock.WaitAsync(ct);
        try
        {
            if (!fresh && Clock() - _lastAt < CacheFor) return _last;
            var reading = await Read(ct);
            (_last, _lastAt) = (reading, Clock());
            NoteChange(reading);
            return reading;
        }
        finally
        {
            _readLock.Release();
        }
    }

    public async Task<bool> WaitForLoginAsync(DateTime deadlineUtc, CancellationToken ct)
    {
        while (true)
        {
            var reading = await ReadAsync(fresh: false, ct);
            if (reading?.Link != SoulseekLinkState.NotLoggedIn) return true;
            var left = deadlineUtc - Clock();
            if (left <= TimeSpan.Zero) return false;
            await Delay(left < PollEvery ? left : PollEvery, ct);
        }
    }

    /// <summary>The dashboard's line for slskd. Not logged in is a warning, not a failure: slskd
    /// is up and logs back in by itself.</summary>
    public static (bool Ok, bool Warning, string Detail) Describe(SoulseekServerReading? reading, int holdHours) =>
        reading switch
        {
            null => (false, false, "unreachable / auth failed"),
            { Link: SoulseekLinkState.LoggedIn } r =>
                (true, false, r.Username is { Length: > 0 } name ? $"logged in to Soulseek as {name}" : "logged in to Soulseek"),
            { Link: SoulseekLinkState.NotLoggedIn } r => (true, true, OutageDetail(r, holdHours)),
            _ => (true, false, "reachable"),
        };

    internal static string OutageDetail(SoulseekServerReading reading, int holdHours)
    {
        var text = $"Not connected to Soulseek (slskd says {reading.State ?? "not logged in"}). "
            + (holdHours > 0
                ? $"Downloads wait up to {holdHours} {(holdHours == 1 ? "hour" : "hours")} for it, then use the next source."
                : "Downloads use the next source.");
        if (reading.NextAttemptUtc is { } next) text += $" slskd tries again at {next:HH:mm} UTC.";
        return text;
    }

    // Once per change, so a three hour outage is two log lines, not one per held song.
    private void NoteChange(SoulseekServerReading? reading)
    {
        var now = reading?.Link;
        if (now is null or SoulseekLinkState.Unknown || now == _lastLogged) return;
        if (now == SoulseekLinkState.NotLoggedIn)
            _logger.LogWarning("slskd is not logged in to Soulseek ({State}); Soulseek-first downloads wait up to {Hours} h",
                reading!.State ?? "no state", _settings.CurrentValue.EffectiveOutageHoldHours);
        else if (_lastLogged is not null)
            _logger.LogInformation("slskd is logged in to Soulseek again");
        _lastLogged = now;
    }
}
