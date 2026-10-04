using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Octo.Services.Common;

namespace Octo.Services.Lyrics;

/// <summary>
/// What someone chose for one song's lyrics: a candidate, kept with its lyrics so the pin still
/// answers when the source is down or has changed, or none, which hides the song's lyrics. Held
/// for the whole server, so every client sees the same choice, and named by artist and title
/// too, so the legacy getLyrics call (which knows nothing else) honours it.
/// </summary>
public sealed record LyricsPin(
    string SongId, string Choice, string? Source, string? Synced, string? Plain,
    string? Artist, string? Title, string? SetBy, DateTime SetUtc)
{
    public const string Hidden = "none";
    public const string Auto = "auto";

    public bool IsHidden => Choice == Hidden;

    public LyricsResult? Lyrics => IsHidden ? null
        : new LyricsResult(Source ?? "pinned", Synced, Plain, false) { CandidateId = Choice };
}

/// <summary>
/// The pins, on disk beside the settings. Written whole on every change, through a temporary
/// file, since a pin is a deliberate act that happens a few times a day at most.
/// </summary>
public sealed class LyricsChoiceStore
{
    private readonly string? _path;
    private readonly ILogger<LyricsChoiceStore>? _logger;
    private readonly object _lock = new();
    private Dictionary<string, LyricsPin> _pins = new(StringComparer.Ordinal);

    public LyricsChoiceStore(string? path = null, ILogger<LyricsChoiceStore>? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _logger = logger;
        Load();
    }

    public LyricsPin? Get(string songId)
    {
        lock (_lock) return _pins.GetValueOrDefault(songId);
    }

    /// <summary>A pin for the song with this artist and title, whatever its id, and however the
    /// two are written ("Drake feat. Rihanna" or "Too Good (feat. Rihanna)"). Never a pin for
    /// another version of it.</summary>
    public LyricsPin? FindByName(string artist, string title)
    {
        if (SongIdentity.Key(artist).Length == 0 || SongIdentity.Key(title).Length == 0) return null;
        var want = SongIdentity.MatchKey(artist, title);
        lock (_lock)
            return _pins.Values
                .Where(pin => SongIdentity.MatchKey(pin.Artist, pin.Title) == want)
                .OrderByDescending(pin => pin.SetUtc)
                .FirstOrDefault();
    }

    public IReadOnlyList<LyricsPin> All()
    {
        lock (_lock) return _pins.Values.OrderByDescending(pin => pin.SetUtc).ToList();
    }

    /// <summary>Whether any song has a pin, so a caller can skip looking one up by name.</summary>
    public bool Any
    {
        get { lock (_lock) return _pins.Count > 0; }
    }

    /// <summary>Removes every pin for the song with this artist and title, whatever its id.</summary>
    public bool RemoveByName(string? artist, string? title)
    {
        if (SongIdentity.Key(artist).Length == 0 || SongIdentity.Key(title).Length == 0) return false;
        var want = SongIdentity.MatchKey(artist, title);
        lock (_lock)
        {
            var ids = _pins.Values.Where(pin => SongIdentity.MatchKey(pin.Artist, pin.Title) == want)
                .Select(pin => pin.SongId).ToList();
            if (ids.Count == 0) return false;
            foreach (var id in ids) _pins.Remove(id);
            Save();
            return true;
        }
    }

    public void Set(LyricsPin pin)
    {
        lock (_lock)
        {
            _pins[pin.SongId] = pin;
            Save();
        }
    }

    public bool Remove(string songId)
    {
        lock (_lock)
        {
            if (!_pins.Remove(songId)) return false;
            Save();
            return true;
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var pins = JsonSerializer.Deserialize<List<LyricsPin>>(File.ReadAllText(_path)) ?? [];
            _pins = pins.Where(pin => !string.IsNullOrEmpty(pin.SongId))
                .GroupBy(pin => pin.SongId).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Unreadable pins are no pins, never a failure to start.
            _logger?.LogWarning("lyrics choices could not be read: {M}", ex.Message);
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_pins.Values.ToList()));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("lyrics choices could not be written: {M}", ex.Message);
        }
    }
}

/// <summary>One entry offered to a person choosing lyrics, with enough to choose by.</summary>
public sealed record LyricsChoiceCandidate(
    string Id, string Source, string Title, string Artist, string? Album, int? DurationSeconds,
    string Kind, bool SameSong, IReadOnlyList<string> Preview);

/// <summary>
/// Choosing lyrics by hand, for the Octo app's "Choose other lyrics" (getLyricsCandidates and
/// setLyricsChoice) and the dashboard's picker, which are two doors to the same pins.
/// </summary>
public sealed class LyricsChoiceService
{
    /// <summary>How many entries per source are fetched for a preview. Each is a request, and
    /// KuGou and NetEase need one per entry.</summary>
    private const int PerSource = 4;

    private static readonly TimeSpan Remembered = TimeSpan.FromMinutes(30);

    private readonly LyricsService _lyrics;
    private readonly LyricsChoiceStore _store;
    private readonly ILogger<LyricsChoiceService> _logger;
    private readonly MemoryCache _fetched = new(new MemoryCacheOptions { SizeLimit = 1024 });

    public LyricsChoiceService(LyricsService lyrics, LyricsChoiceStore store, ILogger<LyricsChoiceService> logger)
    {
        _lyrics = lyrics;
        _store = store;
        _logger = logger;
    }

    public LyricsPin? PinFor(string songId) => _store.Get(songId);

    public LyricsPin? PinFor(string artist, string title) => _store.FindByName(artist, title);

    /// <summary>
    /// The song's pin: by its id, or else by its artist and title. Navidrome gives a song a new id
    /// when its file is replaced (a better copy, a move), and the pin follows the song.
    /// </summary>
    public LyricsPin? PinFor(string songId, string? artist, string? title) =>
        _store.Get(songId)
        ?? (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title) ? null : _store.FindByName(artist, title));

    /// <summary>Whether any song has a pin.</summary>
    public bool AnyPins => _store.Any;

    /// <summary>"auto" when nothing is chosen, "none" when hidden, else the candidate id.</summary>
    public string ChoiceFor(string songId) => _store.Get(songId)?.Choice ?? LyricsPin.Auto;

    /// <summary>The song's choice, found by its id or else by its artist and title.</summary>
    public string ChoiceFor(string songId, string? artist, string? title) =>
        PinFor(songId, artist, title)?.Choice ?? LyricsPin.Auto;

    public IReadOnlyList<LyricsPin> All() => _store.All();

    /// <summary>
    /// Every entry the sources that are on hold for the song, the same song first, each with
    /// its lyrics fetched for a preview. Sources are asked side by side; entries not fetched
    /// when <paramref name="ct"/> runs out are left out, since without their lyrics there is
    /// nothing to choose by.
    /// </summary>
    public async Task<IReadOnlyList<LyricsChoiceCandidate>> CandidatesAsync(LyricsQuery query, CancellationToken ct)
    {
        var sources = _lyrics.Enabled;
        var perSource = await Task.WhenAll(sources.Select(source => FromSourceAsync(source, query, ct)));
        return perSource.SelectMany(list => list).ToList();
    }

    private async Task<List<LyricsChoiceCandidate>> FromSourceAsync(ILyricsSource source, LyricsQuery query, CancellationToken ct)
    {
        var offered = new List<LyricsChoiceCandidate>();
        try
        {
            var search = await source.SearchAsync(query, ct);
            var ranked = search.Candidates
                .Select(candidate => (Candidate: candidate, Same: IsThisSong(candidate, query)))
                .OrderByDescending(pair => pair.Same)
                .ThenBy(pair => query.DurationSeconds is > 0 && pair.Candidate.DurationSeconds is > 0
                    ? Math.Abs(pair.Candidate.DurationSeconds.Value - query.DurationSeconds.Value) : 0)
                .Take(PerSource);

            foreach (var (candidate, same) in ranked)
            {
                if (ct.IsCancellationRequested) break;
                var lyrics = candidate.Lyrics;
                if (lyrics is null)
                {
                    var lookup = await source.FetchAsync(candidate.Id, ct);
                    lyrics = lookup.Result;
                }
                if (lyrics is null) continue;
                _fetched.Set(candidate.CandidateId, lyrics with { CandidateId = candidate.CandidateId },
                    new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Remembered });
                offered.Add(new LyricsChoiceCandidate(candidate.CandidateId, source.Key, candidate.Title, candidate.Artist,
                    candidate.Album, candidate.DurationSeconds, KindOf(lyrics), same, LyricsText.Preview(lyrics)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Out of time: what was fetched stands.
        }
        catch (Exception ex)
        {
            _logger.LogDebug("lyrics candidates from {Source} failed: {M}", source.Key, ex.Message);
        }
        return offered;
    }

    private static bool IsThisSong(LyricsCandidate candidate, LyricsQuery query) =>
        LyricsIdentity.SameSong(query.Title, query.Artist, candidate.Title, candidate.Artist,
            candidate.Artist.Split(['、', ',', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        && LyricsIdentity.LengthFits(query.DurationSeconds, candidate.DurationSeconds);

    public static string KindOf(LyricsResult lyrics) => lyrics.Timing switch
    {
        LyricsTiming.Word => "word",
        LyricsTiming.Line => "line",
        LyricsTiming.Plain => "plain",
        _ => "instrumental",
    };

    /// <summary>The lyrics of one candidate: from the list just shown when it is still
    /// remembered, otherwise asked of its source again.</summary>
    public async Task<LyricsResult?> LyricsOfAsync(string candidateId, CancellationToken ct)
    {
        if (_fetched.TryGetValue(candidateId, out LyricsResult? known) && known is not null) return known;
        var colon = candidateId.IndexOf(':');
        if (colon <= 0 || _lyrics.Source(candidateId[..colon]) is not { } source) return null;
        var lookup = await source.FetchAsync(candidateId[(colon + 1)..], ct);
        return lookup.Result is { } result ? result with { CandidateId = candidateId } : null;
    }

    /// <summary>Pin a song to a candidate. False when the candidate's lyrics cannot be had.</summary>
    public async Task<bool> PinAsync(string songId, string candidateId, string? artist, string? title, string? who,
        CancellationToken ct)
    {
        var lyrics = await LyricsOfAsync(candidateId, ct);
        if (lyrics is null || (!lyrics.HasSynced && !lyrics.HasPlain)) return false;
        _store.Set(new LyricsPin(songId, candidateId, lyrics.Source, lyrics.Synced, lyrics.Plain, artist, title, who, DateTime.UtcNow));
        _logger.LogInformation("Lyrics for '{Artist} - {Title}' pinned to {Candidate} by {Who}", artist, title, candidateId, who ?? "the dashboard");
        return true;
    }

    public void Hide(string songId, string? artist, string? title, string? who)
    {
        _store.Set(new LyricsPin(songId, LyricsPin.Hidden, null, null, null, artist, title, who, DateTime.UtcNow));
        _logger.LogInformation("Lyrics for '{Artist} - {Title}' hidden by {Who}", artist, title, who ?? "the dashboard");
    }

    public bool Clear(string songId) => _store.Remove(songId);

    /// <summary>Back to automatic: the song's pin by id, and any it has by its artist and title,
    /// so a pin made under an older id does not come back.</summary>
    public bool Clear(string songId, string? artist, string? title) =>
        _store.Remove(songId) | _store.RemoveByName(artist, title);
}
