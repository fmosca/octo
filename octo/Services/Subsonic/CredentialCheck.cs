using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Octo.Services.Subsonic;

/// <summary>What Navidrome said of a sign-in.</summary>
public enum CredentialVerdict { Accepted, Refused, Unreachable }

/// <summary>
/// Asks Navidrome whether a sign-in is good, for requests Octo answers without relaying: a star
/// that starts a download and a stream of an outside song. Navidrome checks everything relayed
/// to it, but it never sees those ids, so before this nothing checked them at all.
///
/// Only the sign-in goes to Navidrome, as a ping. An answer is kept under a SHA-256 of the
/// sign-in: a yes for ten minutes, so a player asking for the same address again in ranges pings
/// once, and a no for half a minute, so a wrong password cannot make Octo ask on every retry.
/// An unreachable Navidrome, or one that takes more than five seconds to answer, is never kept.
/// Requests with one sign-in arriving together make one call between them.
/// </summary>
public sealed class CredentialCheck
{
    internal static readonly TimeSpan AcceptedLifetime = TimeSpan.FromMinutes(10);
    internal TimeSpan RefusedLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a ping may take before Navidrome counts as unreachable. A request
    /// waits on it, and a player gives up long before a plain HTTP timeout would.</summary>
    internal TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(5);

    internal const int Capacity = 2048;
    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(1);

    private readonly ILogger<CredentialCheck> _logger;
    private readonly MemoryCache _verdicts = new(new MemoryCacheOptions { SizeLimit = Capacity });
    private readonly ConcurrentDictionary<string, Lazy<Task<CredentialVerdict>>> _asking = new(StringComparer.Ordinal);
    private long _lastWarning;

    public CredentialCheck(ILogger<CredentialCheck> logger) => _logger = logger;

    /// <summary>
    /// Navidrome's answer for this sign-in. None at all is refused without asking. The relay is
    /// the request's own, as with RequestIdentity. A caller that gives up gets Unreachable; the
    /// call it waited on carries on for the others.
    /// </summary>
    public async Task<CredentialVerdict> CheckAsync(SubsonicCredential? credential,
        SubsonicProxyService relay, CancellationToken cancellationToken = default)
    {
        if (credential is null) return CredentialVerdict.Refused;
        var slot = credential.Fingerprint;
        if (_verdicts.TryGetValue(slot, out CredentialVerdict known)) return known;
        var asking = _asking.GetOrAdd(slot,
            _ => new Lazy<Task<CredentialVerdict>>(() => AskAsync(slot, credential, relay)));
        try { return await asking.Value.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CredentialVerdict.Unreachable;
        }
    }

    private async Task<CredentialVerdict> AskAsync(string slot, SubsonicCredential credential,
        SubsonicProxyService relay)
    {
        try
        {
            if (_verdicts.TryGetValue(slot, out CredentialVerdict known)) return known;
            byte[] body;
            // The relay takes no token, so the wait is what is bounded; a late answer is dropped.
            try { (body, _) = await relay.RelayAsync("rest/ping", credential.Parameters()).WaitAsync(CheckTimeout); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException
                                           or OperationCanceledException or OctoNotConfiguredException)
            {
                WarnUnreachable(ex.GetType().Name);
                return CredentialVerdict.Unreachable;
            }
            var verdict = Status(body) switch
            {
                "ok" => CredentialVerdict.Accepted,
                "failed" => CredentialVerdict.Refused,
                _ => CredentialVerdict.Unreachable,
            };
            // Not a Subsonic answer at all: the URL points somewhere else. Not the user's fault.
            if (verdict == CredentialVerdict.Unreachable)
            {
                WarnUnreachable("an answer that was not Navidrome's");
                return verdict;
            }
            _verdicts.Set(slot, verdict, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow =
                    verdict == CredentialVerdict.Accepted ? AcceptedLifetime : RefusedLifetime,
            });
            return verdict;
        }
        finally
        {
            _asking.TryRemove(slot, out _);
        }
    }

    /// <summary>subsonic-response.status of a JSON answer, or null when it is not one.</summary>
    internal static string? Status(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("subsonic-response", out var response)
                   && response.TryGetProperty("status", out var status)
                   && status.ValueKind == JsonValueKind.String ? status.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private void WarnUnreachable(string reason)
    {
        // Once a minute at most: a player retrying through an outage would fill the log.
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastWarning);
        if (last != 0 && now - last < (long)WarnEvery.TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref _lastWarning, now, last) != last) return;
        _logger.LogWarning(
            "Octo could not reach Navidrome to check a sign-in ({Reason}), so outside songs are refused until it can",
            reason);
    }
}
