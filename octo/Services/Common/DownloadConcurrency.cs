using Microsoft.Extensions.Options;
using Octo.Models.Settings;

namespace Octo.Services.Common;

/// <summary>
/// How many downloads may transfer at once, and the gate that holds them to it.
///
/// One at a time used to be the only safe number: a finished Soulseek file was found by its leaf
/// name anywhere under the music folder, so two transfers in flight could claim each other's file.
/// Each Soulseek download now lands in a folder of its own. Parallel is earned, not assumed: until
/// slskd has put a download in its folder (Prove), Current is 1, and if slskd ever puts one
/// elsewhere (Refuse), Current is 1 for the rest of the process.
/// </summary>
public sealed class DownloadConcurrency
{
    private readonly IOptionsMonitor<SoulseekSettings> _settings;
    private readonly ILogger<DownloadConcurrency>? _logger;
    private int _proven;
    private string? _refused;

    public DownloadConcurrency(IOptionsMonitor<SoulseekSettings> settings, ILogger<DownloadConcurrency>? logger = null)
    {
        _settings = settings;
        _logger = logger;
        Transfers = new TransferLimiter(() => Current);
    }

    /// <summary>Taken around each transfer, so album walks and hearts outside the queue count too.</summary>
    public TransferLimiter Transfers { get; }

    public bool DestinationsProven => Volatile.Read(ref _proven) == 1 && _refused is null;

    public int Current => DestinationsProven ? _settings.CurrentValue.EffectiveParallelDownloads : 1;

    /// <summary>Why Current is what it is, in words for the dashboard.</summary>
    public string Why => _refused is { } refused
        ? $"One at a time: {refused}."
        : !DestinationsProven
            ? "One at a time until slskd has put a download in its own folder."
            : Current == 1 ? "One at a time, as set." : $"Up to {Current} at once.";

    /// <summary>A file was found in the folder Octo asked slskd to put it in.</summary>
    public void Prove()
    {
        if (Interlocked.Exchange(ref _proven, 1) == 0 && _refused is null)
            _logger?.LogInformation("slskd puts each download in its own folder, so up to {N} run at once",
                _settings.CurrentValue.EffectiveParallelDownloads);
    }

    /// <summary>slskd put a download somewhere else. Back to one at a time until Octo restarts.</summary>
    public void Refuse(string reason)
    {
        if (Interlocked.CompareExchange(ref _refused, reason, null) is null)
            _logger?.LogWarning("Downloads go one at a time again: {Reason}", reason);
    }
}

/// <summary>
/// An async gate whose width is read on every entry and exit, so a live setting change applies to
/// the next transfer and a narrower width drains instead of cutting anything off.
/// </summary>
public sealed class TransferLimiter(Func<int> width)
{
    private readonly object _lock = new();
    private readonly LinkedList<TaskCompletionSource<bool>> _waiting = new();
    private int _inUse;

    public int InUse { get { lock (_lock) return _inUse; } }

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool> turn;
        lock (_lock)
        {
            if (_inUse < Math.Max(1, width())) { _inUse++; return new Slot(this); }
            turn = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.AddLast(turn);
        }
        using (ct.Register(() => { lock (_lock) _waiting.Remove(turn); turn.TrySetCanceled(ct); }))
            await turn.Task; // a leaving holder handed its slot over, so _inUse already counts this one
        return new Slot(this);
    }

    private void Leave()
    {
        lock (_lock)
        {
            // Hand the slot straight on while the width allows; the leaver is still counted here.
            while (_inUse <= Math.Max(1, width()) && _waiting.First is { } next)
            {
                _waiting.RemoveFirst();
                if (next.Value.TrySetResult(true)) return;
            }
            _inUse--;
        }
    }

    private sealed class Slot(TransferLimiter owner) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) owner.Leave(); }
    }
}
