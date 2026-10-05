namespace Octo.Services.Common;

/// <summary>Whether anything is downloading. The background library jobs wait until nothing is,
/// so they never get in front of a person.</summary>
public interface IAcquisitionActivity { bool IsBusy { get; } }

/// <summary>A queued star or play, or any transfer at all: album tracks bypass the queue.
/// Resolved late, because hosted services are built before the download service is first used.</summary>
public sealed class AcquisitionActivity(IServiceProvider services) : IAcquisitionActivity
{
    public bool IsBusy => services.GetService<TrackAcquisitionQueue>() is { IsIdle: false }
        || services.GetService<IDownloadService>() is { HasActiveDownloads: true };
}
