using System.Collections.Concurrent;

namespace Octo.Services.Lidarr;

/// <summary>
/// Which Lidarr albums a heart or an upgrade is working on, by MusicBrainz release group id. The
/// two must not share an album: a heart moves every imported file into Octo's layout, which
/// would pull the file an upgrade is waiting on out from under it, and an upgrade deletes the
/// files its search brought in, which would take a heart's songs.
/// </summary>
public sealed class LidarrAlbumClaims
{
    private readonly ConcurrentDictionary<string, byte> _hearts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _upgrades = new(StringComparer.OrdinalIgnoreCase);

    public void HeartStarted(string albumId) => _hearts[albumId] = 0;
    public void HeartEnded(string albumId) => _hearts.TryRemove(albumId, out _);
    public bool HeartBusy(string albumId) => _hearts.ContainsKey(albumId);

    public void UpgradeStarted(string albumId) => _upgrades.AddOrUpdate(albumId, 1, (_, count) => count + 1);

    public void UpgradeEnded(string albumId)
    {
        if (_upgrades.AddOrUpdate(albumId, 0, (_, count) => count - 1) <= 0)
            _upgrades.TryRemove(new KeyValuePair<string, int>(albumId, 0));
    }

    public bool UpgradeBusy(string albumId) => _upgrades.TryGetValue(albumId, out var count) && count > 0;
}
