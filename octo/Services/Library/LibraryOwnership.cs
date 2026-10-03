using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Local;
using Octo.Services.Subsonic;

namespace Octo.Services.Library;

/// <summary>A copy of a song already in the library, and whether it is lossless.</summary>
public sealed record OwnedCopy(string? NavidromeId, string AbsolutePath, string Suffix, int BitRate, bool Lossless);

public enum OwnedDecision { Download, KeepYours, KeepAndUpgrade }

/// <summary>
/// Whether a song is already in the library, so a heart, an album walk or a play never adds a
/// second copy. Octo used to check only its own record of the outside id it downloaded, so a song
/// owned before Octo, or fetched once under another outside id, came down again.
///
/// The same song means the same MatchKey (artist and title, one version: a live take or a remix is
/// its own song), and a length within 8 seconds, or, when a length is unknown, the same album, so
/// one album's "Intro" is never taken for another's.
/// </summary>
public sealed class LibraryOwnership
{
    internal const int DurationToleranceSeconds = 8;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    private readonly IOptionsMonitor<SubsonicSettings> _subsonic;
    private readonly NavidromeIdentityService _identity;
    private readonly IHttpClientFactory _http;
    private readonly NavidromeSongPathResolver _resolver;
    private readonly ILocalLibraryService _library;
    private readonly ILogger<LibraryOwnership> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, OwnedCopy? Copy)> _cache = new(StringComparer.Ordinal);

    public LibraryOwnership(IOptionsMonitor<SubsonicSettings> subsonic, NavidromeIdentityService identity,
        IHttpClientFactory http, NavidromeSongPathResolver resolver, ILocalLibraryService library,
        ILogger<LibraryOwnership> logger)
    {
        _subsonic = subsonic;
        _identity = identity;
        _http = http;
        _resolver = resolver;
        _library = library;
        _logger = logger;
        Search = SearchNavidromeAsync;
        Resolve = async (id, ct) => (await _resolver.ResolveAsync(id, ct))?.AbsolutePath;
    }

    /// <summary>One library song as search3 answers it.</summary>
    internal sealed record Candidate(string Id, string Artist, string Title, string? Album, int? Duration, string Suffix, int BitRate);

    // Seams: tests answer from a list instead of a Navidrome.
    internal Func<string, CancellationToken, Task<IReadOnlyList<Candidate>?>> Search { get; set; }
    internal Func<string, CancellationToken, Task<string?>> Resolve { get; set; }
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The library's copy of this song, the best one when there are several, or null.</summary>
    public async Task<OwnedCopy?> FindAsync(string? artist, string? title, int? durationSeconds, string? album,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;
        var key = $"{SongIdentity.MatchKey(artist, title)}|{durationSeconds}|{SongIdentity.Key(album)}";
        if (_cache.TryGetValue(key, out var cached) && Clock() - cached.At < CacheFor) return cached.Copy;

        OwnedCopy? copy = null;
        try
        {
            copy = await FromNavidromeAsync(artist, title, durationSeconds, album, ct)
                ?? await FromOwnDownloadsAsync(artist, title, album);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never a reason to hold a download back: it goes ahead, as before this check existed.
            _logger.LogDebug("Could not tell whether '{Artist} - {Title}' is owned: {M}", artist, title, ex.Message);
        }
        _cache[key] = (Clock(), copy);
        return copy;
    }

    /// <summary>Forget what was found, for a song that has just been placed or replaced.</summary>
    public void Forget() => _cache.Clear();

    /// <summary>
    /// What to do with a song about to download. Keep a lossless copy; keep a lossy one and queue
    /// it for Better quality when a lossless source is in the chain and the asker may; keep it
    /// otherwise too, since an MP3 for an MP3 is no upgrade.
    /// </summary>
    public static OwnedDecision Decide(OwnedCopy? owned, bool sourceCanBeLossless, bool upgradeAllowed) =>
        owned is null ? OwnedDecision.Download
        : owned.Lossless || !sourceCanBeLossless || !upgradeAllowed || owned.NavidromeId is null ? OwnedDecision.KeepYours
        : OwnedDecision.KeepAndUpgrade;

    internal static bool SameSong(Candidate candidate, string artist, string title, int? durationSeconds, string? album)
    {
        if (SongIdentity.MatchKey(candidate.Artist, candidate.Title) != SongIdentity.MatchKey(artist, title)) return false;
        if (durationSeconds is > 0 && candidate.Duration is > 0)
            return Math.Abs(candidate.Duration.Value - durationSeconds.Value) <= DurationToleranceSeconds;
        var wanted = SongIdentity.Key(album);
        return wanted.Length > 0 && SongIdentity.Key(candidate.Album) == wanted;
    }

    private async Task<OwnedCopy?> FromNavidromeAsync(string artist, string title, int? duration, string? album, CancellationToken ct)
    {
        if (await Search($"{artist} {title}", ct) is not { } candidates) return null;
        foreach (var candidate in candidates
                     .Where(c => SameSong(c, artist, title, duration, album))
                     .OrderByDescending(c => DuplicateScanWorker.IsLosslessFile(c.Suffix, c.BitRate))
                     .ThenByDescending(c => c.BitRate))
        {
            // The verified path, as library actions use: a copy whose file cannot be found is not owned.
            if (await Resolve(candidate.Id, ct) is not { } path || !File.Exists(path)) continue;
            return new OwnedCopy(candidate.Id, path, candidate.Suffix, candidate.BitRate,
                DuplicateScanWorker.IsLosslessFile(candidate.Suffix, candidate.BitRate));
        }
        return null;
    }

    private async Task<OwnedCopy?> FromOwnDownloadsAsync(string artist, string title, string? album)
    {
        if (await _library.FindMappingByTagsAsync(artist, title, album) is not { } mapping) return null;
        var suffix = Path.GetExtension(mapping.LocalPath).TrimStart('.').ToLowerInvariant();
        return new OwnedCopy(null, mapping.LocalPath, suffix, 0, DuplicateScanWorker.IsLosslessFile(suffix, 0));
    }

    private async Task<IReadOnlyList<Candidate>?> SearchNavidromeAsync(string query, CancellationToken ct)
    {
        var baseUrl = _subsonic.CurrentValue.Url;
        if (string.IsNullOrWhiteSpace(baseUrl) || _identity.GetScanAuth() is not { } auth) return null;
        var url = $"{baseUrl.TrimEnd('/')}/rest/search3?f=json&c=octo&v=1.16.1"
            + $"&query={Uri.EscapeDataString(query)}&songCount=20&albumCount=0&artistCount=0"
            + $"&u={Uri.EscapeDataString(auth.user)}&t={auth.token}&s={auth.salt}";
        using var response = await _http.CreateClient().GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        if (!doc.RootElement.TryGetProperty("subsonic-response", out var envelope)
            || !envelope.TryGetProperty("searchResult3", out var result)
            || !result.TryGetProperty("song", out var songs) || songs.ValueKind != JsonValueKind.Array)
            return [];
        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        return songs.EnumerateArray()
            .Where(song => Str(song, "id") is { Length: > 0 })
            .Select(song => new Candidate(Str(song, "id")!, Str(song, "artist") ?? "", Str(song, "title") ?? "",
                Str(song, "album"), Int(song, "duration"), Str(song, "suffix") ?? "", Int(song, "bitRate") ?? 0))
            .ToList();
    }
}
