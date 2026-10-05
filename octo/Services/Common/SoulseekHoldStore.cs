using System.Text.Json;

namespace Octo.Services.Common;

/// <summary>Appended, never inserted: the file stores these as numbers.</summary>
public enum HeldKind { Track, Album }

/// <summary>A heart waiting for Soulseek. HeldSinceUtc survives a restart, so the wait is the
/// setting's length in total, not that long again after every restart.</summary>
public sealed record HeldAcquisition(HeldKind Kind, string Provider, string ExternalId, string? RequestedBy,
    DateTime HeldSinceUtc)
{
    public string Key => $"{Kind}:{Provider}:{ExternalId}";
}

/// <summary>
/// The hearts waiting out a Soulseek outage, on disk. Without it a restart during a six hour wait
/// would drop every one of them without a word, where before the wait existed they at least
/// landed as MP3s. Written on every change, inside the lock: there are a handful, and only during
/// an outage, and two writes outside it could land in the wrong order.
/// </summary>
public sealed class SoulseekHoldStore
{
    private readonly string? _path;
    private readonly ILogger<SoulseekHoldStore>? _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, HeldAcquisition> _held = new(StringComparer.Ordinal);

    public SoulseekHoldStore(string? path = null, ILogger<SoulseekHoldStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _logger = logger;
        try
        {
            if (_path is not null && File.Exists(_path))
                foreach (var held in JsonSerializer.Deserialize<List<HeldAcquisition>>(File.ReadAllText(_path)) ?? [])
                    _held[held.Key] = held;
        }
        catch (Exception ex) { _logger?.LogWarning("held downloads could not be read: {M}", ex.Message); }
    }

    public IReadOnlyList<HeldAcquisition> Snapshot()
    {
        lock (_lock) return _held.Values.OrderBy(h => h.HeldSinceUtc).ToList();
    }

    /// <summary>Records a hold. One already there keeps its first start time.</summary>
    public HeldAcquisition Hold(HeldAcquisition held)
    {
        lock (_lock)
        {
            if (_held.TryGetValue(held.Key, out var existing)) return existing;
            _held[held.Key] = held;
            Save();
            return held;
        }
    }

    public void Release(string key)
    {
        lock (_lock)
        {
            if (_held.Remove(key)) Save();
        }
    }

    // Called with the lock held.
    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_held.Values.ToList()));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) { _logger?.LogWarning("held downloads could not be written: {M}", ex.Message); }
    }
}
