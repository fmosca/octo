using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;

namespace Octo.Services.Library;

/// <summary>
/// Whether a hearted outside song, or every song of a hearted outside album, is already in the
/// library. Asked before a heart goes anywhere: a song you have is favourited, never downloaded
/// again, and never waits behind other downloads, a Soulseek outage, or a whole Lidarr album.
/// </summary>
public sealed class HeartOwnership(
    LibraryOwnership ownership,
    IMusicMetadataService metadata,
    IOptionsMonitor<SubsonicSettings> subsonic,
    ILogger<HeartOwnership> logger,
    UpgradeQueue? upgrades = null,
    IOptionsMonitor<LibraryActionSettings>? actions = null,
    UpgradeSources? sources = null)
{
    /// <summary>The library's copy of the hearted song, or null when it is not there, the check is
    /// off, or the song cannot be told (then the heart downloads, as before).</summary>
    public async Task<(Song Song, OwnedCopy Copy)?> FindSongAsync(string provider, string externalId, CancellationToken ct = default)
    {
        if (!subsonic.CurrentValue.SkipOwnedSongs) return null;
        try
        {
            var song = await metadata.GetSongAsync(provider, externalId);
            if (song is null) return null;
            var owned = await ownership.FindAsync(song.Artist, song.Title, song.Duration, song.Album, ct);
            return owned is null ? null : (song, owned);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Could not tell whether hearted {Provider}:{Id} is owned: {Message}", provider, externalId, ex.Message);
            return null;
        }
    }

    /// <summary>The hearted album and the library's copy of each of its songs, but only when every
    /// song is there; null otherwise, and the album heart goes on to fetch what is missing.</summary>
    public async Task<(Album Album, IReadOnlyList<(Song Song, OwnedCopy Copy)> Songs)?> FindWholeAlbumAsync(
        string provider, string albumId, CancellationToken ct = default)
    {
        if (!subsonic.CurrentValue.SkipOwnedSongs) return null;
        try
        {
            var album = await metadata.GetAlbumAsync(provider, albumId);
            if (album is null || album.Songs.Count == 0) return null;
            var found = new List<(Song, OwnedCopy)>();
            foreach (var song in album.Songs)
            {
                var owned = await ownership.FindAsync(song.Artist, song.Title, song.Duration, song.Album ?? album.Title, ct);
                if (owned is null) return null;
                found.Add((song, owned));
            }
            return (album, found);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Could not tell whether hearted album {Provider}:{Id} is owned: {Message}", provider, albumId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Queue an owned lossy copy for Better quality, when a lossless source is set up and every gate
    /// of the action is open for the person who hearted it. True when it was queued.
    /// </summary>
    public bool QueueUpgradeIfWanted(OwnedCopy owned, Song song, string? requestedBy)
    {
        if (owned.Lossless || owned.NavidromeId is null || upgrades is null || actions is null) return false;
        if (sources is not null && !sources.Ready) return false;
        if (!LibraryOwnership.UpgradeAllowed(actions.CurrentValue, requestedBy)) return false;
        upgrades.Add([new UpgradeAsk(owned.NavidromeId, song.Title, song.Artist, song.Album, owned.Suffix)], requestedBy!, "heart");
        return true;
    }
}
