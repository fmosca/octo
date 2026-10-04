namespace Octo.Models.Settings;

public class MetadataSettings
{
    /// <summary>
    /// Language code sent as Accept-Language to the external metadata APIs
    /// (Deezer, Last.fm). Deezer localizes album genre names by caller IP
    /// unless told otherwise, so a server hosted in a non-English country
    /// writes localized genre tags into downloaded files. Empty lets the
    /// provider decide from the server's IP.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// A download that still has no album after Deezer and its own tags is filed as a single
    /// under its title, instead of joining every other album-less track in Navidrome's one
    /// "[Unknown Album]" (#50). A single filed under its title is a real release, and it gives
    /// Navidrome something to group. Never applied to a compilation, where a hundred one-track
    /// albums would be worse than the bucket.
    /// Environment variable: ALBUM_FROM_TITLE
    /// </summary>
    public bool AlbumFromTitle { get; set; } = true;

    /// <summary>
    /// Ask the Cover Art Archive first when a fingerprint named the MusicBrainz release the album
    /// tag describes: the right pressing, no guessing by name (#51).
    /// Environment variable: COVER_ART_ARCHIVE
    /// </summary>
    public bool UseCoverArtArchive { get; set; } = true;

    /// <summary>
    /// Treat a cover that is not square as missing. A 16:9 cover is a video thumbnail; when
    /// nothing better turns up its centre square is used, which for a YouTube "Topic" upload is
    /// the real cover inside the letterbox. Off keeps whatever the source embedded.
    /// Environment variable: REPLACE_VIDEO_COVERS
    /// </summary>
    public bool ReplaceVideoCovers { get; set; } = true;

    /// <summary>
    /// Also write cover.jpg beside a download, which Navidrome reads, which survives a retag, and
    /// which covers a file the embed did not stick to. Only in the Organized layout and only in a
    /// folder the download created: Navidrome ranks cover.* above embedded art, so in a shared
    /// folder, or an album folder that was already there, one file would change every album's
    /// cover. Never replaces a cover.* or folder.* the owner put there; a cover.jpg Octo wrote
    /// itself (it carries a comment saying so) gives way to a larger one.
    /// Environment variable: COVER_FILE
    /// </summary>
    public bool WriteCoverFile { get; set; } = true;

    /// <summary>
    /// Embed the cover at the full size it was found, often 3000 px from iTunes, instead of
    /// shrinking it to <see cref="EmbeddedCoverSide"/>. Every file of an album carries its own
    /// copy, so a full-size cover adds a few megabytes to each; cover.jpg always gets the full
    /// size either way. Also what the cover upgrade embeds.
    /// Environment variable: FULL_SIZE_COVERS
    /// </summary>
    public bool EmbedFullSizeCovers { get; set; }

    /// <summary>The longest side of an embedded cover when <see cref="EmbedFullSizeCovers"/> is
    /// off: sharp across a phone's whole screen, a few hundred kilobytes.</summary>
    public const int EmbeddedCoverSide = 1500;

    /// <summary>
    /// Fetch lyrics: a sidecar beside each download, and live for any song as it plays when the
    /// library has none (#52). Off by default like the rest; there is no destructive path, since
    /// an unmatched track simply has no lyrics file and a wrong one is a text file to delete.
    /// Environment variable: LYRICS_FETCH
    /// </summary>
    public bool FetchLyrics { get; set; } = false;

    /// <summary>
    /// Lyrics sources, in order: song (the lyrics the song already has, in its tags or a file
    /// beside it, as Navidrome serves them), kugou (word-timed, deep catalogue, an unofficial
    /// API), lrclib (open, line-synced), netease (synced and deep on non-Western and older music,
    /// but an unofficial API, so it only runs when listed), lyricsovh (plain text). Timed beats
    /// plain, so a later source is only asked while nothing earlier had timing. Leaving a source
    /// out switches it off, except song, which cannot be switched off: left out, it is first.
    /// Environment variable: LYRICS_SOURCES
    /// </summary>
    public string LyricsSources { get; set; } = DefaultLyricsSources;

    public const string DefaultLyricsSources = "song,kugou,lrclib,lyricsovh";

    /// <summary>The song's own lyrics as a place in the order: in its tags or a file beside it.</summary>
    public const string SongLyricsSource = "song";

    public static readonly string[] KnownLyricsSources = [SongLyricsSource, "kugou", "lrclib", "netease", "lyricsovh"];

    /// <summary>The sources in order, the song's own always among them (first when the saved
    /// order leaves it out, as every order saved before it was a choice does).</summary>
    public IReadOnlyList<string> EffectiveLyricsSources
    {
        get
        {
            var listed = (LyricsSources ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(source => source.ToLowerInvariant())
                .Where(source => KnownLyricsSources.Contains(source))
                .Distinct()
                .ToList();
            if (!listed.Contains(SongLyricsSource)) listed.Insert(0, SongLyricsSource);
            return listed;
        }
    }

    /// <summary>
    /// Where lyrics Octo finds for a song are saved: beside (a .lrc or .txt next to the song, the
    /// default), inside (in the song's own tags), or both. Inside rewrites the audio file, which
    /// on a cloud mount uploads it again, and Navidrome sees it only after a scan, which Octo
    /// asks for. Octo marks what it writes either way, and never replaces lyrics it did not write.
    /// Environment variable: LYRICS_SAVE_TO
    /// </summary>
    public string SaveLyricsTo { get; set; } = LyricsSaveTo.Beside;

    /// <summary>Whether found lyrics go in a file beside the song.</summary>
    public bool SavesLyricsBeside => LyricsSaveTo.Normalize(SaveLyricsTo) != LyricsSaveTo.Inside;

    /// <summary>Whether found lyrics go in the song's own tags.</summary>
    public bool SavesLyricsInside => LyricsSaveTo.Normalize(SaveLyricsTo) != LyricsSaveTo.Beside;

    /// <summary>
    /// Word-timed lyrics beat line-timed ones from an earlier source: with this on, a source that
    /// only has line timing does not end the search, and a later one with word timing wins. Off,
    /// the first timed answer is taken, which asks fewer services per song.
    /// Environment variable: LYRICS_PREFER_WORD_TIMED
    /// </summary>
    public bool PreferWordTimedLyrics { get; set; } = true;

    /// <summary>
    /// Let "Find lyrics for the library" write lyrics files beside every library song, not only
    /// the ones Octo downloaded. Off by default: a folder of rips or purchases is the owner's,
    /// and Octo does not add files to it unless asked. Existing lyrics files and lyrics embedded
    /// in a song are never replaced either way.
    /// Environment variable: LYRICS_WRITE_BESIDE_ALL
    /// </summary>
    public bool WriteLyricsBesideAllSongs { get; set; } = false;

    /// <summary>
    /// File a song that arrived without an album under the first release of its recording
    /// (its original album), even when the file's own tags name a compilation or a later
    /// pressing it was ripped from. Off keeps the file's own album when the source tagged one.
    /// Environment variable: PREFER_ORIGINAL_ALBUM
    /// </summary>
    public bool PreferOriginalAlbum { get; set; } = true;

    /// <summary>
    /// The year a song shows is its recording's first release, not the pressing it was matched
    /// to. A 2011 remaster of a 1991 album reads 1991. The pressing's own date stays in the
    /// download's tag report. Off writes the pressing's date.
    /// Environment variable: YEAR_FROM_ORIGINAL_RELEASE
    /// </summary>
    public bool YearFromOriginalRelease { get; set; } = true;

    /// <summary>
    /// Countries whose pressings win a tie, in order, as two-letter codes ("US, XW, GB").
    /// Empty means no preference.
    /// Environment variable: PREFERRED_COUNTRIES
    /// </summary>
    public string PreferredCountries { get; set; } = string.Empty;

    public IReadOnlyList<string> EffectivePreferredCountries => (PreferredCountries ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(code => code.ToUpperInvariant()).Where(code => code.Length == 2).Distinct().ToList();

    /// <summary>
    /// Ask the music database for the chosen release's label, catalogue number, barcode, status
    /// and track ids (one request per download, about a second), and search it by name when
    /// the fingerprint named nothing. Needs no key. Off tags from the fingerprint and the
    /// catalog alone.
    /// Environment variable: RELEASE_DETAILS_LOOKUP
    /// </summary>
    public bool ReleaseDetailsLookup { get; set; } = true;

    /// <summary>
    /// Measure each download's loudness and write ReplayGain track tags, which both Octo apps
    /// and most players use to even out volume. Decodes the whole file once, beside the
    /// lookups, so it rarely adds time.
    /// Environment variable: REPLAYGAIN
    /// </summary>
    public bool ReplayGain { get; set; } = true;

    /// <summary>
    /// How long the loudness measurement may take before the download goes on without it.
    /// A FLAC on local disk takes a few seconds; a slow mount can take far longer.
    /// Environment variable: REPLAYGAIN_TIMEOUT_SECONDS
    /// </summary>
    public int ReplayGainTimeoutSeconds { get; set; } = 45;

    public int EffectiveReplayGainTimeoutSeconds => Math.Clamp(ReplayGainTimeoutSeconds, 10, 300);

    /// <summary>
    /// Rehearse the release matching: work out what every download would be tagged as and show
    /// it in Fetched songs, but write only what Octo wrote before. For trying the matching on
    /// real downloads before trusting it.
    /// Environment variable: TAG_REHEARSAL
    /// </summary>
    public bool TagRehearsal { get; set; } = false;
}

/// <summary>The places found lyrics can be saved to: <see cref="MetadataSettings.SaveLyricsTo"/>.</summary>
public static class LyricsSaveTo
{
    public const string Beside = "beside";
    public const string Inside = "inside";
    public const string Both = "both";

    /// <summary>One of the three; anything else is beside, the one that never touches a song.</summary>
    public static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        Inside => Inside,
        Both => Both,
        _ => Beside,
    };
}
