using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Octo.Models.Settings;
using Octo.Services.Common;

namespace Octo.Services.Lyrics;

/// <summary>
/// Lyrics from the sources LYRICS_SOURCES names, in that order (#52). "song" there is the lyrics
/// the song already has, which the caller passes in, so they rank like any source. Word timing beats line
/// timing beats plain text. A synced answer ends the search, unless "prefer word-timed lyrics"
/// is on and it has only line timing: then later sources are still asked for word timing, and
/// the line-timed answer is kept in case none has it. A plain answer is always kept in case
/// something timed turns up.
/// </summary>
public sealed class LyricsService : IDisposable
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(30);

    private readonly IReadOnlyList<ILyricsSource> _sources;
    private readonly IOptionsMonitor<MetadataSettings> _settings;
    private readonly ILogger<LyricsService> _logger;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 512 });

    public LyricsService(IEnumerable<ILyricsSource> sources, IOptionsMonitor<MetadataSettings> settings,
        ILogger<LyricsService> logger)
    {
        _sources = sources.ToList();
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The sources in the order they are asked, only those that are on.</summary>
    public IReadOnlyList<ILyricsSource> Enabled =>
        _settings.CurrentValue.EffectiveLyricsSources
            .Select(name => _sources.FirstOrDefault(source => source.Key == name))
            .OfType<ILyricsSource>()
            .ToList();

    public ILyricsSource? Source(string key) => _sources.FirstOrDefault(source => source.Key == key);

    /// <summary>
    /// The best lyrics the sources have. When <paramref name="ct"/> runs out part way (a
    /// client waiting has a budget), the best answer found so far is returned rather than
    /// nothing, and remembered only briefly. <paramref name="songsOwn"/> is how the lyrics the
    /// song already has are timed, None when it has none; at "song"'s place in the order they
    /// answer as <see cref="LyricsResult.SongsOwn"/>, and the caller serves its own copy.
    /// </summary>
    public async Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct,
        LyricsTiming songsOwn = LyricsTiming.None)
    {
        if (string.IsNullOrWhiteSpace(query.Artist) || string.IsNullOrWhiteSpace(query.Title)) return LyricsLookup.Miss;

        var settings = _settings.CurrentValue;
        var preferWords = settings.PreferWordTimedLyrics;
        var key = $"{SongIdentity.MatchKey(query.Artist, query.Title)}|{query.DurationSeconds}"
            + $"|{string.Join(',', settings.EffectiveLyricsSources)}|{preferWords}|{songsOwn}";
        if (_cache.TryGetValue(key, out LyricsLookup? cached) && cached is not null) return cached;

        LyricsResult? best = null;
        var transient = false;
        foreach (var name in settings.EffectiveLyricsSources)
        {
            if (name == Octo.Models.Settings.MetadataSettings.SongLyricsSource)
            {
                if (songsOwn == LyricsTiming.None) continue;
                var own = LyricsResult.SongsOwn(songsOwn);
                if (songsOwn == LyricsTiming.Word || (songsOwn == LyricsTiming.Line && !preferWords))
                    return Remember(key, new LyricsLookup(own, false), HitTtl);
                if (best is null || own.Timing > best.Timing) best = own;
                continue;
            }
            if (_sources.FirstOrDefault(s => s.Key == name) is not { } source) continue;
            LyricsLookup lookup;
            try { lookup = await source.FindAsync(query, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                transient = true;
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("lyrics source {Source} threw: {M}", source.Key, ex.Message);
                lookup = LyricsLookup.Failed;
            }
            if (ct.IsCancellationRequested && lookup.Result is null)
            {
                transient = true;
                break;
            }

            if (lookup.Transient) { transient = true; continue; }
            if (lookup.Result is not { } result) continue;
            if (result.Instrumental || result.Timing == LyricsTiming.Word
                || (result.Timing == LyricsTiming.Line && !preferWords))
                return Remember(key, new LyricsLookup(result, false), HitTtl);
            if (best is null || result.Timing > best.Timing) best = result;
        }

        // An answer found while a better source could not be asked is kept only briefly, so the
        // better one gets another chance soon.
        if (best is not null) return Remember(key, new LyricsLookup(best, false), transient ? MissTtl : HitTtl);
        if (transient) return LyricsLookup.Failed;
        return Remember(key, LyricsLookup.Miss, MissTtl);
    }

    private LyricsLookup Remember(string key, LyricsLookup lookup, TimeSpan ttl)
    {
        _cache.Set(key, lookup, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });
        return lookup;
    }

    /// <summary>Forget what was found, after the source order or a pin changed.</summary>
    public void Clear() => _cache.Clear();

    public void Dispose() => _cache.Dispose();
}
