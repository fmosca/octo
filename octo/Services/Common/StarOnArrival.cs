using System.Text.Json;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Subsonic;

namespace Octo.Services.Common;

/// <summary>
/// Favorites a starred outside song in Navidrome once its download lands, for the person who
/// starred it (#71). In most clients a star means "I like this", and Octo kept the song but lost
/// the like, because the id that was starred was never Navidrome's.
///
/// Navidrome favorites only as the user who signs the call, so that person's sign-in is held
/// with the download: in memory only, and dropped once the favorite is sent, once the download
/// ends without a song, or after a day. A restart loses what is held. A password is never held:
/// it is turned into a token first.
/// </summary>
public sealed class StarOnArrival : IDisposable
{
    /// <summary>What Octo's own apps send as c. Their star is the Add button: a copy, not a like.</summary>
    internal const string OctoAppClient = "Octo";

    private sealed record Hold(string Key, SubsonicCredential Credential, string Who, DateTime HeldAt);

    private readonly object _lock = new();
    private readonly List<Hold> _songs = [];
    private readonly List<Hold> _albums = [];
    private readonly AcquisitionTracker _tracker;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<SubsonicSettings> _settings;
    private readonly ILogger<StarOnArrival> _logger;
    private readonly TimeProvider _time;

    /// <summary>Calls Navidrome as the user. Tests set it; otherwise a relay from a fresh scope.</summary>
    internal Func<string, Dictionary<string, string>, Task<byte[]>>? Call { get; set; }
    /// <summary>Finds a placed file's Navidrome id. Tests set it; otherwise the path resolver.</summary>
    internal Func<string, string, string, CancellationToken, Task<string?>>? LibraryLookup { get; set; }
    internal TimeSpan VisibilityPoll { get; set; } = TimeSpan.FromSeconds(15);
    internal int VisibilityAttempts { get; set; } = 40;

    public StarOnArrival(AcquisitionTracker tracker, IServiceScopeFactory scopes,
        IOptionsMonitor<SubsonicSettings> settings, ILogger<StarOnArrival> logger, TimeProvider? time = null)
    {
        _tracker = tracker; _scopes = scopes; _settings = settings; _logger = logger;
        _time = time ?? TimeProvider.System;
        _tracker.Ended += OnEnded;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public static bool IsOctoApp(string? client) =>
        string.Equals(client?.Trim(), OctoAppClient, StringComparison.OrdinalIgnoreCase);

    internal int Held { get { lock (_lock) return _songs.Count + _albums.Count; } }

    public void HoldSong(string provider, string externalId, SubsonicCredential credential, string? who) =>
        Add(_songs, AcquisitionTracker.KeyOf(provider, externalId), credential, who);

    public void HoldAlbum(string provider, string albumId, SubsonicCredential credential, string? who) =>
        Add(_albums, AcquisitionTracker.KeyOf(provider, albumId), credential, who);

    private void Add(List<Hold> holds, string key, SubsonicCredential credential, string? who)
    {
        // Held for up to a day, so a password is swapped for a token made from it first.
        credential = credential.WithoutPassword();
        // An API key names nobody until Navidrome says, so part of its fingerprint stands in.
        var name = string.IsNullOrWhiteSpace(who) ? $"API key {credential.Fingerprint[..8]}" : who.Trim();
        lock (_lock)
        {
            Prune();
            // A second star from the same person replaces the first: one favorite each.
            holds.RemoveAll(hold => hold.Key == key && string.Equals(hold.Who, name, StringComparison.OrdinalIgnoreCase));
            holds.Add(new Hold(key, credential, name, Now));
            var over = _songs.Count + _albums.Count - AcquisitionTracker.Capacity;
            if (over > 0) holds.RemoveRange(0, Math.Min(over, holds.Count));
        }
    }

    private void OnEnded(AcquisitionEnd end)
    {
        List<Hold> songs, albums;
        lock (_lock)
        {
            songs = Take(_songs, hold => hold.Key == end.Key);
            // An album keeps its hold through a track that failed: the next one may land.
            albums = end.LibraryId is null ? [] : Take(_albums, hold => end.AlbumKeys.Contains(hold.Key));
        }
        if (songs.Count + albums.Count == 0) return;
        if (end.LibraryId is not { } libraryId)
        {
            if (end.Done)
                foreach (var hold in songs)
                    _logger.LogInformation("'{Artist} - {Title}' is in the folder but Navidrome did not show it, so it was not favorited for {User}",
                        end.Artist, end.Title, hold.Who);
            return;
        }
        if (!_settings.CurrentValue.StarDownloadsForRequester) return;
        // Off whatever reported the end, and carrying no request's context, so a call made as
        // one person can never pick up another request's parameters.
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(() => PlaceAsync(libraryId, songs, albums));
    }

    private async Task PlaceAsync(string libraryId, List<Hold> songs, List<Hold> albums)
    {
        foreach (var hold in songs) await StarSongAsync(hold.Credential, hold.Who, libraryId);
        foreach (var hold in albums)
        {
            try
            {
                var albumId = SongFacts(await CallAsync("rest/getSong", hold.Credential.Parameters(("id", libraryId))))?.AlbumId;
                if (albumId is null) { _logger.LogInformation("Navidrome did not say which album {Id} is on, so no album was favorited for {User}", libraryId, hold.Who); continue; }
                Report(await CallAsync("rest/star", hold.Credential.Parameters(("albumId", albumId))), hold.Who, $"album {albumId}");
            }
            catch (Exception ex) { _logger.LogInformation("Could not favorite the album of {Id} for {User}: {Reason}", libraryId, hold.Who, ex.Message); }
        }
    }

    /// <summary>
    /// A heart on a song already in the library: favorite the library's copy now for whoever
    /// hearted it, whatever StarDownloadsForRequester says, since that setting is only about
    /// downloads. By its Navidrome id, or found by its path when only that is known. False when
    /// nobody's sign-in was held (Octo's own apps, whose heart means Add).
    /// </summary>
    public bool FavoriteOwned(string provider, string externalId, string? libraryId, string artist, string title, string path)
    {
        List<Hold> songs;
        lock (_lock) songs = Take(_songs, hold => hold.Key == AcquisitionTracker.KeyOf(provider, externalId));
        if (songs.Count == 0) return false;
        foreach (var hold in songs)
        {
            if (libraryId is not null)
                using (ExecutionContext.SuppressFlow())
                    _ = Task.Run(() => StarSongAsync(hold.Credential, hold.Who, libraryId));
            else StarWhenVisible(hold.Credential, hold.Who, artist, title, path);
        }
        return true;
    }

    /// <summary>A heart on an album already whole in the library: favorite the album now, found
    /// through one of its songs, whatever StarDownloadsForRequester says.</summary>
    public bool FavoriteOwnedAlbum(string provider, string albumId, string libraryIdOfASong)
    {
        List<Hold> albums;
        lock (_lock) albums = Take(_albums, hold => hold.Key == AcquisitionTracker.KeyOf(provider, albumId));
        if (albums.Count == 0) return false;
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(() => PlaceAsync(libraryIdOfASong, [], albums));
        return true;
    }

    /// <summary>Whether this person has favorited a song. False when Navidrome cannot say.</summary>
    public async Task<bool> IsStarredAsync(SubsonicCredential credential, string navidromeId)
    {
        try { return SongFacts(await CallAsync("rest/getSong", credential.Parameters(("id", navidromeId))))?.Starred == true; }
        catch (Exception ex) { _logger.LogDebug("Could not read whether {Id} is a favorite: {Reason}", navidromeId, ex.Message); return false; }
    }

    /// <summary>Favorite a file for this person once Navidrome shows it, for a replacement that
    /// arrives as a new song. Polls about ten minutes, then gives up with a log line.</summary>
    public void StarWhenVisible(SubsonicCredential credential, string who, string artist, string title, string path)
    {
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(async () =>
            {
                if ((LibraryLookup ?? ResolveLookup()) is not { } lookup)
                {
                    _logger.LogInformation("Octo has no Navidrome sign-in of its own yet, so the replacement of '{Title}' was not favorited for {User}", title, who);
                    return;
                }
                for (var attempt = 0; attempt < Math.Max(1, VisibilityAttempts); attempt++)
                {
                    if (attempt > 0) await Task.Delay(VisibilityPoll);
                    string? id = null;
                    try { id = await lookup(artist, title, path, CancellationToken.None); }
                    catch (Exception ex) { _logger.LogDebug("Library lookup for {Path} failed: {Reason}", path, ex.Message); }
                    if (!string.IsNullOrWhiteSpace(id)) { await StarSongAsync(credential, who, id); return; }
                }
                _logger.LogInformation("Navidrome never showed the replacement of '{Title}', so it was not favorited for {User}", title, who);
            });
    }

    private async Task StarSongAsync(SubsonicCredential credential, string who, string libraryId)
    {
        try
        {
            // Already a favorite: starring again would only move it to the top of the list.
            if (SongFacts(await CallAsync("rest/getSong", credential.Parameters(("id", libraryId))))?.Starred == true) return;
            Report(await CallAsync("rest/star", credential.Parameters(("id", libraryId))), who, libraryId);
        }
        catch (Exception ex) { _logger.LogInformation("Could not favorite {Id} for {User}: {Reason}", libraryId, who, ex.Message); }
    }

    private void Report(byte[] body, string who, string what)
    {
        if (CredentialCheck.Status(body) == "ok") _logger.LogInformation("Favorited {What} for {User}, who starred it", what, who);
        // Most likely the password changed while the download ran.
        else _logger.LogInformation("Navidrome would not favorite {What} for {User}", what, who);
    }

    internal static (bool Starred, string? AlbumId)? SongFacts(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("subsonic-response", out var response)
                || !response.TryGetProperty("song", out var song)) return null;
            return (song.TryGetProperty("starred", out var starred) && starred.ValueKind == JsonValueKind.String,
                song.TryGetProperty("albumId", out var album) && album.ValueKind == JsonValueKind.String ? album.GetString() : null);
        }
        catch (JsonException) { return null; }
    }

    private async Task<byte[]> CallAsync(string endpoint, Dictionary<string, string> parameters)
    {
        if (Call is not null) return await Call(endpoint, parameters);
        using var scope = _scopes.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<SubsonicProxyService>().RelayAsync(endpoint, parameters)).Body;
    }

    private Func<string, string, string, CancellationToken, Task<string?>>? ResolveLookup()
    {
        // Both are singletons, so the scope ending does not end them.
        using var scope = _scopes.CreateScope();
        var identity = scope.ServiceProvider.GetService<NavidromeIdentityService>();
        var resolver = scope.ServiceProvider.GetService<Octo.Services.Library.NavidromeSongPathResolver>();
        return identity?.GetScanAuth() is null || resolver is null ? null : resolver.FindIdByPathAsync;
    }

    private void Prune()
    {
        _songs.RemoveAll(hold => Now - hold.HeldAt >= AcquisitionTracker.StalledRetention);
        _albums.RemoveAll(hold => Now - hold.HeldAt >= AcquisitionTracker.StalledRetention);
    }

    private static List<Hold> Take(List<Hold> holds, Func<Hold, bool> match)
    {
        var taken = holds.Where(match).ToList();
        holds.RemoveAll(hold => match(hold));
        return taken;
    }

    public void Dispose()
    {
        _tracker.Ended -= OnEnded;
        int lost;
        lock (_lock) { lost = _songs.Count + _albums.Count; _songs.Clear(); _albums.Clear(); }
        if (lost > 0)
            _logger.LogInformation("Octo is stopping with {Count} starred download(s) still to favorite; they will arrive without the favorite", lost);
    }
}
