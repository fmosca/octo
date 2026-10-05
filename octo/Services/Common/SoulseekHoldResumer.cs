namespace Octo.Services.Common;

/// <summary>Picks up the hearts a restart interrupted while they waited for Soulseek.</summary>
public sealed class SoulseekHoldResumer(SoulseekHoldStore store, HeartAcquisitionCoordinator hearts,
    ILogger<SoulseekHoldResumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var held = store.Snapshot();
        if (held.Count > 0)
            logger.LogInformation("Resuming {Count} downloads that were waiting for Soulseek before the restart", held.Count);
        foreach (var entry in held)
        {
            if (entry.Kind == HeldKind.Album) hearts.ResumeAlbum(entry);
            else hearts.ResumeTrack(entry);
        }
        return Task.CompletedTask;
    }
}
