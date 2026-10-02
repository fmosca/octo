using System.Text.Json;

namespace Octo.Services.YouTube;

/// <summary>
/// Pure HTTP client for the yt-dlp-shim sidecar service. Octo never spawns
/// yt-dlp itself — the shim wraps it behind /search and /stream endpoints
/// in its own container, so any process-management quirks stay isolated.
/// </summary>
public class YouTubeResolver
{
    // Named clients registered in Program.cs. The "stream" client uses an infinite
    // timeout because /stream from the shim is open for the duration of playback;
    // the default HttpClient timeout would kill the read mid-song.
    public const string SearchClientName = "yt-dlp-shim-search";
    public const string StreamClientName = "yt-dlp-shim-stream";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YouTubeResolver> _logger;
    private readonly string _baseUrl;

    public YouTubeResolver(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<YouTubeResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _baseUrl = ResolveBaseUrl(configuration.GetValue<string>("YouTube:ShimUrl"));
    }

    /// <summary>
    /// The shim address every request is built on. Falls back to the compose
    /// service name when the setting is absent OR blank: the admin UI saves a
    /// cleared field as "", and "" is not null, so a plain null-coalesce left
    /// every request relative and the factory client with no base address.
    /// </summary>
    public string BaseUrl => _baseUrl;

    internal const string DefaultBaseUrl = "http://yt-dlp-shim:8080";

    internal static string ResolveBaseUrl(string? configured)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured.Trim();
        return value.TrimEnd('/');
    }

    /// <summary>
    /// Resolves "Artist - Title" to a single best YouTube hit via the shim.
    /// </summary>
    /// <param name="background">
    /// True only for fire-and-forget prewarm. The shim keeps a slice of its
    /// yt-dlp gate unreachable to background work, so a user pressing play never
    /// queues behind a prewarm burst. Absence means interactive, so a call site
    /// that forgets this fails safe: slower prewarm, never a slower play.
    /// </param>
    public async Task<YouTubeHit?> SearchAsync(string query, int? durationHint = null,
        bool background = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var url = $"{_baseUrl}/search?q={Uri.EscapeDataString(query)}"
            + (durationHint is int dh && dh > 0 ? $"&duration={dh}" : "")
            + (background ? "&bg=1" : "");
        try
        {
            var http = _httpClientFactory.CreateClient(SearchClientName);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("shim /search HTTP {Code} for '{Q}'", (int)resp.StatusCode, query);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var videoId = root.TryGetProperty("video_id", out var v) ? v.GetString() : null;
            if (string.IsNullOrEmpty(videoId)) return null;
            var (gain, peak) = Loudness(root);
            return new YouTubeHit
            {
                VideoId = videoId,
                Title = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                Duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null,
                Channel = root.TryGetProperty("channel", out var c) ? c.GetString() : null,
                GainDb = gain,
                PeakDb = peak
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("shim /search failed for '{Q}': {Msg}", query, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The shim's loudness numbers from a resolve payload, when it has measured that
    /// video. Both are absent for a video the background measurement has not reached yet
    /// (`measured: false`), which is a normal answer, not an error.
    /// </summary>
    private static (double? Gain, double? Peak) Loudness(JsonElement root) =>
        (root.TryGetProperty("gain_db", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetDouble() : null,
         root.TryGetProperty("peak_db", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null);

    /// <summary>
    /// Fast metadata-only lookup via the shim's /meta (flat search, no URL
    /// resolution). Returns the top video's id + duration for showing an accurate
    /// length without paying the full /search extraction.
    /// </summary>
    /// <param name="background">
    /// Same meaning as on <see cref="SearchAsync"/>: true only for fire-and-forget
    /// prewarm, so the shim's gate never makes an interactive caller queue behind it.
    /// </param>
    public async Task<YouTubeHit?> MetaAsync(string query, int? durationHint = null,
        bool background = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var url = $"{_baseUrl}/meta?q={Uri.EscapeDataString(query)}"
            + (durationHint is int dh && dh > 0 ? $"&duration={dh}" : "")
            + (background ? "&bg=1" : "");
        try
        {
            var http = _httpClientFactory.CreateClient(SearchClientName);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var vid = root.TryGetProperty("video_id", out var v) ? v.GetString() : null;
            if (string.IsNullOrEmpty(vid)) return null;
            var (gain, peak) = Loudness(root);
            return new YouTubeHit
            {
                VideoId = vid,
                Title = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                Duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null,
                GainDb = gain,
                PeakDb = peak
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a streaming connection to the shim's /stream endpoint. The shim
    /// proxies bytes from YouTube's CDN to us; we hand the resulting stream
    /// off to ASP.NET which forwards it to the Subsonic client.
    ///
    /// <paramref name="rangeHeader"/> is forwarded verbatim to the shim, which
    /// passes it on to googlevideo. iOS Subsonic clients (Arpeggi, Narjo) probe
    /// with `Range: bytes=0-1` and won't play audio/mp4 unless the server
    /// returns 206 with a valid Content-Range, so this passthrough is required
    /// for them to even attempt playback.
    ///
    /// Returns (stream, contentType, contentLength, statusCode, contentRange,
    /// owner) or null on failure. Caller owns the stream + response. The
    /// HttpClient itself is owned by IHttpClientFactory and must NOT be
    /// disposed — doing so tears down the handler/connection backing the
    /// returned stream and reads die mid-song.
    /// </summary>
    public async Task<(Stream stream, string contentType, long? contentLength, int statusCode, string? contentRange, HttpResponseMessage owner)?> OpenStreamAsync(string videoId, string? rangeHeader = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId)) return null;
        var url = $"{_baseUrl}/stream?id={Uri.EscapeDataString(videoId)}";

        var http = _httpClientFactory.CreateClient(StreamClientName);

        HttpResponseMessage? resp = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(rangeHeader))
            {
                req.Headers.TryAddWithoutValidation("Range", rangeHeader);
            }
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            // Accept 200 (full body) and 206 (partial content). A 416 from the
            // shim is a real unsatisfiable-range answer and is returned as its
            // own result so the controller can relay the status + Content-Range
            // (`bytes */<total>`) to the client — turning it into null would
            // collapse the probe answer into a 404 and hide the resource size.
            // Anything else means the upstream/shim couldn't satisfy the request.
            var shimStatus = (int)resp.StatusCode;
            if (shimStatus != 200 && shimStatus != 206 && shimStatus != 416)
            {
                _logger.LogWarning("shim /stream HTTP {Code} for {Vid}", shimStatus, videoId);
                resp.Dispose();
                return null;
            }
            if (shimStatus == 416)
            {
                var range = resp.Content.Headers.ContentRange?.ToString();
                var length = resp.Content.Headers.ContentLength;
                resp.Dispose();
                return (Stream.Null, "application/json", length, 416, range, null);
            }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "audio/mp4";
            var contentLength = resp.Content.Headers.ContentLength;
            var contentRange = resp.Content.Headers.ContentRange?.ToString();
            var statusCode = (int)resp.StatusCode;
            var taken = resp; resp = null; // ownership transferred to caller via OwningStream wrapper
            return (stream, contentType, contentLength, statusCode, contentRange, taken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("shim /stream failed for {Vid}: {Msg}", videoId, ex.Message);
            resp?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Downloads a YouTube video as MP3 to {destWithoutExt}.mp3 via the shim's
    /// /download endpoint, passing clean artist/title so the file is tagged for
    /// the library. Returns the saved path, or null on failure. Uses the
    /// infinite-timeout stream client because a download can take a while.
    /// </summary>
    public async Task<string?> DownloadAsync(string videoId, string destWithoutExt,
        string? artist = null, string? title = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(destWithoutExt)) return null;
        var url = $"{_baseUrl}/download?id={Uri.EscapeDataString(videoId)}&dest={Uri.EscapeDataString(destWithoutExt)}"
            + (string.IsNullOrEmpty(artist) ? "" : $"&artist={Uri.EscapeDataString(artist)}")
            + (string.IsNullOrEmpty(title) ? "" : $"&title={Uri.EscapeDataString(title)}");
        try
        {
            var http = _httpClientFactory.CreateClient(StreamClientName);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("shim /download HTTP {Code} for vid={Vid}", (int)resp.StatusCode, videoId);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("shim /download failed for {Vid}: {Msg}", videoId, ex.Message);
            return null;
        }
    }
}

public class YouTubeHit
{
    public string VideoId { get; set; } = "";
    public string? Title { get; set; }
    public int? Duration { get; set; }
    public string? Channel { get; set; }

    /// <summary>
    /// ReplayGain gain in dB for the video, when the shim has already measured it.
    /// The shim measures asynchronously when a video is first resolved, so the first
    /// resolve of a track carries no gain and a later one does: the pin sites store it
    /// when it appears, and getSong asks again on every request, which is what closes
    /// the loop. Null means "not measured yet", never "0 dB".
    /// </summary>
    public double? GainDb { get; set; }

    /// <summary>Sample peak in dBFS that <see cref="GainDb"/> was capped by, exactly as the
    /// shim measured it. Served beside the gain (as a linear peak) so a client can apply
    /// the gain without clipping.</summary>
    public double? PeakDb { get; set; }
}
