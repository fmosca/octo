using System.Security.Cryptography;
using System.Text;

namespace Octo.Services.Subsonic;

/// <summary>
/// The part of a Subsonic request that signs it in, and nothing else: u with t and s, u with p,
/// or an API key. Copied out so a check, or a call made later as that person, carries none of
/// the ids, queries or other values the client sent.
///
/// In memory only. ToString names the user and no more, because a record that carries one
/// prints its members, and a cache files it under Fingerprint, a SHA-256 of the values.
/// </summary>
public sealed class SubsonicCredential
{
    /// <summary>Every parameter Navidrome signs a Subsonic request in with.</summary>
    private static readonly string[] SignInKeys = ["u", "t", "s", "p", "apiKey", "jwt"];

    private readonly Dictionary<string, string> _values;

    private SubsonicCredential(Dictionary<string, string> values, string version, string client)
    {
        _values = values;
        Version = version;
        Client = client;
        var canonical = string.Join('\n', SignInKeys.Select(key => $"{key}={values.GetValueOrDefault(key, "")}"));
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>u as sent, or null for an API key sign-in, which names nobody by itself.</summary>
    public string? User => _values.GetValueOrDefault("u");

    internal string Version { get; }

    /// <summary>The client name the request sent as c, or "octo" when it sent none.</summary>
    internal string Client { get; }

    /// <summary>A SHA-256 of the sign-in, for filing an answer under. Never the values.</summary>
    public string Fingerprint { get; }

    /// <summary>The sign-in a request carries, or null when it carries none.</summary>
    public static SubsonicCredential? From(IReadOnlyDictionary<string, string> parameters)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in SignInKeys)
            if (parameters.GetValueOrDefault(key) is { Length: > 0 } value) values[key] = value;
        // A name alone proves nothing, and Navidrome would refuse it anyway.
        if (values.Keys.All(key => key == "u")) return null;
        return new SubsonicCredential(values,
            parameters.GetValueOrDefault("v") is { Length: > 0 } version ? version : "1.16.1",
            parameters.GetValueOrDefault("c") is { Length: > 0 } client ? client : "octo");
    }

    /// <summary>
    /// Parameters for a call made as this person: the sign-in, their version, and the client
    /// name their request sent, so Navidrome files the call under the player it already keeps
    /// for that app (Octo's relays pass c through unchanged). Always JSON. A fresh copy each
    /// time, so a caller can add an id.
    /// </summary>
    public Dictionary<string, string> Parameters(params (string Key, string Value)[] extra)
    {
        var parameters = new Dictionary<string, string>(_values, StringComparer.Ordinal)
        {
            ["v"] = Version, ["c"] = Client, ["f"] = "json",
        };
        foreach (var (key, value) in extra) parameters[key] = value;
        return parameters;
    }

    public override string ToString() => $"SubsonicCredential({User ?? "API key"})";
}
