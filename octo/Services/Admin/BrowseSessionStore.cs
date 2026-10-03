using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Octo.Services.Admin;

/// <summary>
/// Short-lived tokens proving the holder authenticated as a Navidrome admin.
///
/// The admin UI has no authentication of its own, which is tolerable for settings
/// on a LAN but not for an endpoint that lists directories: unauthenticated, that
/// would make every Octo install an arbitrary directory-enumeration service. Rather
/// than invent a login system, the browse endpoints verify credentials against the
/// Navidrome that Octo already fronts and hand back one of these.
///
/// In memory only, and deliberately not persisted: a restart invalidating every
/// browse session is the correct trade for a token that grants filesystem visibility.
/// </summary>
public class BrowseSessionStore
{
    /// <summary>
    /// Sliding lifetime of a browse session. Long enough that configuring a server
    /// is not punctuated by repeated sign-ins, short enough that an unattended
    /// browser does not stay authorised indefinitely. The token is HttpOnly and
    /// SameSite=Strict, so reaching it means reaching the machine.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, (string User, DateTime Expires)> _sessions = new();

    /// <summary>Mint a token for a verified admin. Sliding expiry starts now.</summary>
    public string Create(string username)
    {
        Prune();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = (username, DateTime.UtcNow.Add(Ttl));
        return token;
    }

    /// <summary>
    /// True when the token is live. Valid use slides the expiry, so a user browsing
    /// steadily is not logged out mid-task while an abandoned token still lapses.
    /// </summary>
    public bool Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!_sessions.TryGetValue(token, out var session)) return false;
        if (session.Expires <= DateTime.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }
        _sessions[token] = (session.User, DateTime.UtcNow.Add(Ttl));
        return true;
    }

    /// <summary>
    /// The Navidrome admin a live token was minted for, or null. Slides the expiry like Validate.
    /// What the Better quality page acts as, so a file is only ever changed for a person who signed in.
    /// </summary>
    public string? UserOf(string? token) =>
        Validate(token) && _sessions.TryGetValue(token!, out var session) ? session.User : null;

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _sessions)
            if (kv.Value.Expires <= now)
                _sessions.TryRemove(kv.Key, out _);
    }
}
