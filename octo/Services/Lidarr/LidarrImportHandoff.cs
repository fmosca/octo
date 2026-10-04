using System.Collections.Concurrent;

namespace Octo.Services.Lidarr;

/// <param name="Path">Where Lidarr put the file, as Octo sees it.</param>
/// <param name="Quiet">No notice for this one: the album's other songs, or an album heart, which
/// gets one notice for the whole album instead.</param>
public sealed record LidarrImport(string Path, bool Quiet);

/// <summary>
/// The files a Lidarr heart brought in, waiting for the download pipeline to take them by the
/// song's external id. Lidarr is then only how the file was found: the pipeline checks it with
/// AcoustID and the spectrum, identifies, tags, places and records it exactly as it does a
/// Soulseek download.
/// </summary>
public sealed class LidarrImportHandoff
{
    private readonly ConcurrentDictionary<string, LidarrImport> _offers = new(StringComparer.Ordinal);

    public void Offer(string externalId, string path, bool quiet) => _offers[externalId] = new LidarrImport(path, quiet);

    /// <summary>The file for this song, once; null when none is waiting.</summary>
    public LidarrImport? Take(string externalId) => _offers.TryRemove(externalId, out var offer) ? offer : null;

    /// <summary>Takes an offer back. True when it was still waiting, so the pipeline never used it.</summary>
    public bool Withdraw(string externalId) => _offers.TryRemove(externalId, out _);
}
