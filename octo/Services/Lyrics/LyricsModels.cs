namespace Octo.Services.Lyrics;

public sealed record LyricsQuery(string Artist, string Title, string? Album, int? DurationSeconds);

/// <summary>How much timing a lyric carries, best last, so a comparison ranks them.</summary>
public enum LyricsTiming { None = 0, Plain = 1, Line = 2, Word = 3 }

/// <summary>
/// Synced is LRC text with timestamps, and when a source has word timing it is enhanced LRC:
/// the standard line tags plus a &lt;mm:ss.xx&gt; tag before each word, which any player that
/// reads .lrc still shows line by line. Plain is untimed text. Instrumental means the source
/// knows the track has no words, which is an answer, not a miss.
/// </summary>
public sealed record LyricsResult(string Source, string? Synced, string? Plain, bool Instrumental)
{
    public bool HasSynced => !string.IsNullOrWhiteSpace(Synced);
    public bool HasPlain => !string.IsNullOrWhiteSpace(Plain);
    public bool HasWordTiming => HasSynced && LyricsText.HasWordTags(Synced);

    public LyricsTiming Timing => SongTiming is { } song ? song
        : Instrumental ? LyricsTiming.None
        : HasWordTiming ? LyricsTiming.Word
        : HasSynced ? LyricsTiming.Line
        : HasPlain ? LyricsTiming.Plain
        : LyricsTiming.None;

    /// <summary>
    /// Set on a stand-in for the lyrics the song already has (in its tags or a file beside it),
    /// which Navidrome serves and Octo only ranks: how they are timed. The text stays Navidrome's.
    /// </summary>
    public LyricsTiming? SongTiming { get; init; }

    /// <summary>Whether this stands in for the song's own lyrics: serve Navidrome's.</summary>
    public bool IsSongsOwn => SongTiming is not null;

    /// <summary>A stand-in for the song's own lyrics, timed as given.</summary>
    public static LyricsResult SongsOwn(LyricsTiming timing) =>
        new(Octo.Models.Settings.MetadataSettings.SongLyricsSource, null, null, false) { SongTiming = timing };

    /// <summary>The candidate these lyrics came from ("kugou:..."), so a pin can name it.</summary>
    public string? CandidateId { get; init; }

    /// <summary>Why the match is not certain (the length could not be checked, or is close to
    /// the edge), for the library job's review list. Null when nothing is in doubt.</summary>
    public string? Doubt { get; init; }
}

/// <summary>
/// A source's answer. Transient means it could not answer right now (rate limited, overloaded,
/// timed out), which is never remembered as "this song has no lyrics": LRCLIB sheds load with
/// 503s often enough that caching those as misses would blank songs for no reason.
/// </summary>
public sealed record LyricsLookup(LyricsResult? Result, bool Transient)
{
    public static readonly LyricsLookup Miss = new(null, false);
    public static readonly LyricsLookup Failed = new(null, true);
}

/// <summary>
/// One entry a source's search returned: which song it says it is, before its lyrics are
/// fetched. Id is the source's own and opaque; CandidateId adds the source, so it can be
/// handed to a client and come back in setLyricsChoice. Lyrics is filled when the search
/// answer already carried them (LRCLIB does).
/// </summary>
public sealed record LyricsCandidate(string Source, string Id, string Title, string Artist, string? Album,
    int? DurationSeconds)
{
    public string CandidateId => $"{Source}:{Id}";
    public LyricsResult? Lyrics { get; init; }
}

public sealed record LyricsSearch(IReadOnlyList<LyricsCandidate> Candidates, bool Transient)
{
    public static readonly LyricsSearch Empty = new([], false);
    public static readonly LyricsSearch Failed = new([], true);
}

public interface ILyricsSource
{
    /// <summary>The name in LYRICS_SOURCES: kugou, lrclib, netease or lyricsovh.</summary>
    string Key { get; }

    /// <summary>The lyrics of the one entry that is this song, or a miss.</summary>
    Task<LyricsLookup> FindAsync(LyricsQuery query, CancellationToken ct);

    /// <summary>Every entry the source's search returns for the song, same song or not, for a
    /// person choosing between them. Nothing is filtered here; identity is the caller's call.</summary>
    Task<LyricsSearch> SearchAsync(LyricsQuery query, CancellationToken ct) => Task.FromResult(LyricsSearch.Empty);

    /// <summary>The lyrics of one entry by the id its search gave.</summary>
    Task<LyricsLookup> FetchAsync(string id, CancellationToken ct) => Task.FromResult(LyricsLookup.Miss);
}
