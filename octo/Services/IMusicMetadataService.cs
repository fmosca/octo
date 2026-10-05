using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Models.Download;
using Octo.Models.Search;
using Octo.Models.Subsonic;
using Octo.Services.LastFm;

namespace Octo.Services;

/// <summary>
/// Interface for external music metadata search service
/// (Deezer API, Spotify API, MusicBrainz, etc.)
/// </summary>
public interface IMusicMetadataService
{
    /// <summary>
    /// Searches for songs on external providers
    /// </summary>
    /// <param name="query">Search term</param>
    /// <param name="limit">Maximum number of results</param>
    /// <returns>List of found songs</returns>
    Task<List<Song>> SearchSongsAsync(string query, int limit = 20);

    /// <summary>
    /// Searches for songs given an already-split artist and title. Preferred when
    /// the caller has structured fields (e.g. Last.fm results) so providers don't
    /// have to reverse-parse a concatenated query — which corrupts multi-word
    /// artists like "The Beatles" into artist="The", title="Beatles ...".
    /// <paramref name="durationSeconds"/> if known is stored on the placeholder
    /// so the client's scrub bar shows the right total length on first play.
    /// </summary>
    Task<List<Song>> SearchSongsByArtistTitleAsync(string artist, string title, int limit = 1, int? durationSeconds = null);

    /// <summary>
    /// Fills in real duration/album/year on external placeholder songs (which
    /// otherwise show a 3:00 fallback) from a metadata provider. Bounded, cached,
    /// best-effort; does not block or fail the search if the provider is down.
    /// </summary>
    Task EnrichExternalSongsAsync(List<Song> songs, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Gives external songs outside a search (station playlists) the lengths already known
    /// for them, and looks the rest up in the background so the next response has them.
    /// Never waits on the network: whatever is not known yet keeps the fallback for now.
    /// </summary>
    void CompleteSongLengths(IReadOnlyList<Song> songs) { }

    /// <summary>
    /// Resolves the real YouTube video (and its duration) for the top of a search
    /// result so the shown length matches the audio that plays. Bounded + cached;
    /// also stores the videoId so playback reuses the same video. When <paramref name="background"/>
    /// is true the songs are already sent: only the routing is written, a video a play already
    /// pinned is kept, and the shim's background lane is used.
    /// </summary>
    /// <param name="interactive">
    /// True when a client is waiting on this (opening a track). It then skips the background
    /// prewarm gate, which drops a resolve that cannot get a permit inside its bounded wait —
    /// and a dropped resolve leaves the 180 s placeholder the client is about to draw its
    /// scrub bar from. The shim's own gate reserves capacity for interactive work.
    /// </param>
    Task ResolveTopDurationsAsync(List<Song> songs, CancellationToken ct = default,
        bool interactive = false, bool background = false)
        => Task.CompletedTask;

    /// <summary>
    /// Best-effort pre-resolve of upstream identifiers (e.g. YouTube videoIds) for
    /// the first N songs of a freshly-built search result. Called fire-and-forget
    /// so search3 still returns instantly. Provider-specific (a Deezer/Qobuz
    /// implementation would no-op); default no-op preserves source compatibility
    /// for any provider that doesn't need it.
    /// </summary>
    Task PrewarmYouTubeIdsAsync(IEnumerable<Song> songs, int topN, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Same as <see cref="PrewarmYouTubeIdsAsync"/> but accepts raw song ids — the
    /// implementation looks each id up in its own routing registry to find the
    /// artist/title to resolve. Used by the scrobble-driven sliding-window
    /// prewarm where the controller only has the scrobbled song id and the
    /// upcoming-songs list it stored at search time.
    /// </summary>
    Task PrewarmYouTubeIdsForSongIdsAsync(IEnumerable<string> songIds, int topN, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Best-effort pre-fetch of cover art for the first N songs of a freshly-built
    /// search result, so a client that renders them a moment later finds the image
    /// already cached instead of triggering the fetch itself. Fire-and-forget, on
    /// each source's background rate-limit lane so it never queues behind a live
    /// search or cover request. Default no-op preserves source compatibility for
    /// any provider that doesn't need it.
    /// </summary>
    Task PrewarmCoverArtAsync(IEnumerable<Song> songs, int topN, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Searches for albums on external providers
    /// </summary>
    Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken ct = default);
    
    /// <summary>
    /// Searches for artists on external providers
    /// </summary>
    Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20);
    
    /// <summary>
    /// Combined search (songs, albums, artists)
    /// </summary>
    Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20);
    
    /// <summary>
    /// Gets details of an external song
    /// </summary>
    Task<Song?> GetSongAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Gets details of an external album with its songs
    /// </summary>
    Task<Album?> GetAlbumAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Gets details of an external artist
    /// </summary>
    Task<Artist?> GetArtistAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Gets an artist's albums
    /// </summary>
    Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId);

    /// <summary>
    /// Gets an artist's albums for a library artist's page. The library's album titles say
    /// which of two artists of one name is meant; a provider that cannot use them ignores them.
    /// </summary>
    Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId,
        IReadOnlyCollection<string>? libraryAlbumTitles)
        => GetArtistAlbumsAsync(externalProvider, externalId);

    /// <summary>
    /// Gets an artist's albums with only the track counts already known, asking the catalog
    /// for none: for counts shown beside an artist's page, whose own album list asks for the
    /// missing ones at the same moment. A provider with no such lookup answers the plain list.
    /// </summary>
    Task<List<Album>> GetArtistAlbumsKnownCountsAsync(string externalProvider, string externalId)
        => GetArtistAlbumsAsync(externalProvider, externalId);
    
    /// <summary>
    /// Searches for playlists on external providers
    /// </summary>
    /// <param name="query">Search term</param>
    /// <param name="limit">Maximum number of results</param>
    /// <returns>List of found playlists</returns>
    Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20);
    
    /// <summary>
    /// Gets details of an external playlist (metadata only, not tracks)
    /// </summary>
    /// <param name="externalProvider">Provider name (e.g., "deezer", "qobuz")</param>
    /// <param name="externalId">Playlist ID from the provider</param>
    /// <returns>Playlist details or null if not found</returns>
    Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId);
    
    /// <summary>
    /// Gets all tracks from an external playlist
    /// </summary>
    /// <param name="externalProvider">Provider name (e.g., "deezer", "qobuz")</param>
    /// <param name="externalId">Playlist ID from the provider</param>
    /// <returns>List of songs in the playlist</returns>
    Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId);

    /// <summary>
    /// The artists the catalog plays alongside an outside artist, its "similar artists"
    /// row. Rows are outside artist rows the client can open. Null when the id is not an
    /// outside artist of this provider.
    /// </summary>
    Task<List<Artist>?> RelatedArtistsAsync(string externalProvider, string externalId,
        int limit = 20, CancellationToken ct = default)
        => Task.FromResult<List<Artist>?>(null);

    /// <summary>
    /// An outside artist's most-played songs, as outside song rows the client can play.
    /// Null when the id is not an outside artist of this provider.
    /// </summary>
    Task<List<Song>?> TopTracksAsync(string externalProvider, string externalId,
        int limit = 20, CancellationToken ct = default)
        => Task.FromResult<List<Song>?>(null);

    /// <summary>
    /// An outside artist's biography and its summary, or empty strings when no source has
    /// one. Null when the id is not an outside artist of this provider. The last.fm service
    /// is the caller's to hand over: the provider knows the artist's real name, last.fm
    /// holds the texts.
    /// </summary>
    Task<(string Biography, string Summary)?> BiographyAsync(string externalProvider, string externalId,
        LastFmService lastFm, CancellationToken ct = default)
        => Task.FromResult<(string, string)?>(null);
}
