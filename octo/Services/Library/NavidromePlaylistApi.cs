using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Subsonic;

namespace Octo.Services.Library;

/// <summary>
/// Navidrome's native playlist API, as the admin. Shared by the action-playlist sweep, which
/// removes what people asked for, and the notice worker, which adds what Octo is asking about.
///
/// An admin may edit any user's playlist (Navidrome's checkWritable), which is what lets Octo
/// fill a playlist the user owns while the user keeps it private.
/// </summary>
public sealed class NavidromePlaylistApi
{
    private readonly IHttpClientFactory _http;
    private readonly NavidromeIdentityService _identity;
    private readonly IOptionsMonitor<SubsonicSettings> _subsonic;
    private readonly ILogger<NavidromePlaylistApi> _logger;

    public NavidromePlaylistApi(IHttpClientFactory http, NavidromeIdentityService identity,
        IOptionsMonitor<SubsonicSettings> subsonic, ILogger<NavidromePlaylistApi> logger)
    {
        _http = http;
        _identity = identity;
        _subsonic = subsonic;
        _logger = logger;
    }

    private string BaseUrl => (_subsonic.CurrentValue.Url ?? "").TrimEnd('/');

    public async Task<IReadOnlyList<LibraryActionPlaylistWorker.PlaylistRow>> ListPlaylistsAsync(CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(jwt => Request(HttpMethod.Get, $"{BaseUrl}/api/playlist?_start=0&_end=1000", jwt), ct);
            if (response is not { IsSuccessStatusCode: true }) return [];
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            return LibraryActionPlaylistWorker.ParsePlaylists(doc.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not list playlists: {M}", ex.Message);
            return [];
        }
    }

    public async Task<IReadOnlyList<LibraryActionPlaylistWorker.PlaylistTrackRow>> ListTracksAsync(string playlistId,
        CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(jwt => Request(HttpMethod.Get,
                $"{BaseUrl}/api/playlist/{Uri.EscapeDataString(playlistId)}/tracks?_start=0&_end=500", jwt), ct);
            if (response is not { IsSuccessStatusCode: true }) return [];
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            return LibraryActionPlaylistWorker.ParseTracks(doc.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not list tracks in playlist {Id}: {M}", playlistId, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// One page of Navidrome's own song list, with how many rows it held, or null when it could not be
    /// read. The native list rather than search3: only this one reports the real library path, and
    /// the weekly upgrade remembers files by path (#70).
    /// </summary>
    public async Task<(IReadOnlyList<LibrarySongRow> Rows, int Count)?> ListSongsAsync(int start, int count,
        CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(jwt => Request(HttpMethod.Get,
                $"{BaseUrl}/api/song?_start={start}&_end={start + count}&_sort=id&_order=ASC", jwt), ct);
            if (response is not { IsSuccessStatusCode: true }) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            return QualityUpgradeWorker.ParseSongs(doc.RootElement);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not list songs: {M}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Remove tracks by POSITION. Positions are reassigned on every change, so callers re-read
    /// the list immediately before and send every position in one call.
    /// </summary>
    public async Task<bool> RemovePositionsAsync(string playlistId, IReadOnlyCollection<string> positions, CancellationToken ct)
    {
        if (positions.Count == 0) return true;
        var query = string.Join('&', positions.Select(position => $"id={Uri.EscapeDataString(position)}"));
        using var response = await SendAsync(jwt => Request(HttpMethod.Delete,
            $"{BaseUrl}/api/playlist/{Uri.EscapeDataString(playlistId)}/tracks?{query}", jwt), ct);
        if (response is { IsSuccessStatusCode: true }) return true;
        _logger.LogWarning("Could not remove {Count} track(s) from playlist {Id}: HTTP {Status}",
            positions.Count, playlistId, (int?)response?.StatusCode);
        return false;
    }

    /// <summary>Append tracks. Navidrome answers {"added": n}.</summary>
    public async Task<bool> AddTracksAsync(string playlistId, IReadOnlyCollection<string> mediaFileIds, CancellationToken ct)
    {
        if (mediaFileIds.Count == 0) return true;
        var body = JsonSerializer.Serialize(new { ids = mediaFileIds });
        using var response = await SendAsync(jwt =>
        {
            var request = Request(HttpMethod.Post, $"{BaseUrl}/api/playlist/{Uri.EscapeDataString(playlistId)}/tracks", jwt);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }, ct);
        if (response is { IsSuccessStatusCode: true }) return true;
        _logger.LogWarning("Could not add {Count} track(s) to playlist {Id}: HTTP {Status}",
            mediaFileIds.Count, playlistId, (int?)response?.StatusCode);
        return false;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string jwt)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Nd-Authorization", $"Bearer {jwt}");
        return request;
    }

    /// <summary>
    /// Send with the admin token, and on a 401 log in again and resend ONCE. Null when there is
    /// no admin credential at all, or the fresh token is refused too.
    /// </summary>
    private async Task<HttpResponseMessage?> SendAsync(Func<string, HttpRequestMessage> build, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_subsonic.CurrentValue.Url)) return null;
        var jwt = await _identity.EnsureAdminJwtAsync(ct);
        if (string.IsNullOrEmpty(jwt)) return null;

        var client = _http.CreateClient();
        using (var first = build(jwt))
        {
            var response = await client.SendAsync(first, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
            response.Dispose();
        }

        _identity.InvalidateAdminJwt(jwt);
        var fresh = await _identity.EnsureAdminJwtAsync(ct);
        if (string.IsNullOrEmpty(fresh) || fresh == jwt)
        {
            _logger.LogWarning("Navidrome refused Octo's admin token and no fresh one could be had");
            return null;
        }
        using var retry = build(fresh);
        return await client.SendAsync(retry, ct);
    }
}
