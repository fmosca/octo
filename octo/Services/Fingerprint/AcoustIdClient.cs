using System.Text.Json;

namespace Octo.Services.Fingerprint;

/// <summary>One credited artist and the text MusicBrainz joins it to the next one with.</summary>
public sealed record AcoustIdCredit(string Name, string? ArtistId, string JoinPhrase);

/// <summary>
/// The release a recording was matched on. It supplies what names the album, numbers the
/// track and finds the cover, and it is chosen per recording by PickRelease.
/// </summary>
public sealed record AcoustIdRelease(
    string? ReleaseId, string? ReleaseGroupId, string? Title, int? Year,
    int? TrackNumber, int? TrackCount, int? DiscNumber,
    string? AlbumArtist, bool IsCompilation)
{
    /// <summary>The rest of what the service says about a release, kept for every release of
    /// every group so the chooser can weigh them: the group's title and kind, the release's own
    /// title, date and country, the track's own id, and the album artists' ids.</summary>
    public string? GroupTitle { get; init; }
    public string? PrimaryType { get; init; }
    public IReadOnlyList<string> SecondaryTypes { get; init; } = [];
    public string? Country { get; init; }
    public string? Date { get; init; }
    public string? ReleaseTrackId { get; init; }
    public int? DiscCount { get; init; }
    public IReadOnlyList<string> AlbumArtistIds { get; init; } = [];
}

/// <summary>
/// One recording AcoustID matched, with the MusicBrainz fields that come back in the same
/// lookup. There is no separate MusicBrainz client on purpose: AcoustID's metadata IS
/// MusicBrainz data, and asking for it via meta= costs nothing extra on a call already
/// being made and already inside a rate budget.
/// </summary>
public sealed record AcoustIdRecording(
    string RecordingId, string Title, IReadOnlyList<string> Artists, string? AlbumTitle, int? Year)
{
    public IReadOnlyList<AcoustIdCredit> Credits { get; init; } = [];
    public AcoustIdRelease? Release { get; init; }
    public int? DurationSeconds { get; init; }

    /// <summary>Every release of every group the recording is on, bounded, for the chooser.
    /// <see cref="Release"/> stays the one pick the old fields are read from.</summary>
    public IReadOnlyList<AcoustIdRelease> Releases { get; init; } = [];

    /// <summary>The codes the music database lists for the recording, normalised.</summary>
    public IReadOnlyList<string> Isrcs { get; init; } = [];

    /// <summary>How many submissions tie the fingerprint to this recording.</summary>
    public int Sources { get; init; }

    /// <summary>
    /// The credit as MusicBrainz prints it, join phrases and all. Never a bare comma join:
    /// Navidrome does not split artists on commas, so "Bizarrap, Rauw Alejandro" became one
    /// artist and one folder that neither of them owns (#49).
    /// </summary>
    public string ArtistCredit => Credits.Count > 0 ? JoinCredits(Credits) : JoinNames(Artists);

    /// <summary>The first credited artist: the one a folder is named after.</summary>
    public string? PrimaryArtist => Credits.Count > 0 ? Credits[0].Name : Artists.FirstOrDefault();

    internal static string JoinCredits(IReadOnlyList<AcoustIdCredit> credits)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < credits.Count; i++)
        {
            builder.Append(credits[i].Name);
            if (i == credits.Count - 1) break;
            // compress drops a join phrase the parent level already carries, so a missing one
            // is read the way MusicBrainz most often prints it.
            var join = credits[i].JoinPhrase;
            builder.Append(string.IsNullOrEmpty(join) ? (i == credits.Count - 2 ? " & " : ", ") : join);
        }
        return builder.ToString();
    }

    internal static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " & " + names[^1],
    };
}

public sealed record AcoustIdResult(double Score, IReadOnlyList<AcoustIdRecording> Recordings)
{
    /// <summary>The service's own id for the fingerprint, written to a confirmed file.</summary>
    public string? Id { get; init; }
}

public sealed record AcoustIdLookup(bool IsOk, string? Error, IReadOnlyList<AcoustIdResult> Results);

/// <summary>One confirmed fingerprint to send back, with the recording a person vouched for.</summary>
public sealed record AcoustIdSubmission(string Fingerprint, int DurationSeconds, string RecordingId, string? FileFormat);

public sealed class AcoustIdClient
{
    /// <summary>
    /// A recording on a long-lived catalogue can carry hundreds of release groups, and one
    /// fingerprint can match several ids. Both are bounded so a pathological response cannot
    /// turn a download into a CPU burn.
    /// </summary>
    /// <summary>
    /// What to ask AcoustID to return: recordings gives title and artists, releasegroups the
    /// canonical album title, releases the year, and compress asks for a gzipped body (which is
    /// why the named client sets AutomaticDecompression).
    ///
    /// SPACE separated, and that is not cosmetic. FormUrlEncodedContent encodes a literal '+'
    /// as %2B, so writing these joined by '+' sends AcoustID one unknown token rather than four
    /// fields. It answers 200 with a perfectly good score and NO metadata at all, every result
    /// then has zero recordings, and the verdict is permanently Inconclusive: the feature
    /// accepts every file forever while looking like it is working. Verified against the live
    /// API on 2026-09-18, one track, both spellings.
    ///
    /// tracks adds each release's mediums and the track's position on them, which is what numbers
    /// a file named from its match (#48). It only takes effect beside releases.
    ///
    /// isrcs and sources cost nothing on the same call: the recording's codes settle agreement
    /// with a request's code without a second service, and the submission count breaks ties.
    /// </summary>
    internal const string MetaFields = "recordings releasegroups releases tracks compress isrcs sources";

    private const int MaxResults = 10;
    private const int MaxRecordings = 25;
    private const int MaxReleaseGroups = 50;
    private const int MaxReleasesPerGroup = 10;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AcoustIdClient> _logger;

    public AcoustIdClient(IHttpClientFactory httpClientFactory, ILogger<AcoustIdClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Ask AcoustID what this fingerprint is. Returns null on any transport or parse
    /// failure, which the caller reads as "no verdict" and keeps the file.
    /// </summary>
    public async Task<AcoustIdLookup?> LookupAsync(string apiKey, string fingerprint,
        int durationSeconds, int timeoutSeconds)
    {
        // POST rather than GET: a Chromaprint fingerprint is kilobytes of base64 and would
        // blow past URL length limits.
        var form = new Dictionary<string, string>
        {
            ["client"] = apiKey,
            ["format"] = "json",
            ["duration"] = durationSeconds.ToString(),
            ["fingerprint"] = fingerprint,
            ["meta"] = MetaFields,
        };

        try
        {
            var client = _httpClientFactory.CreateClient(AcoustIdRateLimiter.ClientName);
            // Per-call rather than the client's own Timeout, so Soulseek:AcoustIdTimeoutSeconds
            // takes effect without a restart. The client keeps a generous ceiling behind this.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await client.PostAsync("v2/lookup", new FormUrlEncodedContent(form), cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                // The sweep yielding to a download, not AcoustID failing.
                if (AcoustIdRateLimiter.InBackground && response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    _logger.LogDebug("acoustid background lookup deferred");
                else
                    _logger.LogWarning("acoustid lookup answered {Status}", (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            return ParseLookup(doc.RootElement);
        }
        catch (Exception ex)
        {
            // Warning, not Debug. A parse failure here reads downstream as "accept every
            // file", so a silent degradation to a no-op is the worst outcome this feature
            // can have and must be visible in the log.
            _logger.LogWarning("acoustid lookup failed: {M}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Send confirmed fingerprints back to AcoustID (#47). Each carries a MusicBrainz recording a
    /// person vouched for by keeping the track; nothing is ever sent on Octo's own judgement.
    /// Posted through the same named client as lookups, so one 3/s budget covers both. True
    /// when AcoustID accepted the batch.
    /// </summary>
    public async Task<bool> SubmitAsync(string clientKey, string userKey, IReadOnlyList<AcoustIdSubmission> items,
        int timeoutSeconds)
    {
        if (items.Count == 0) return true;
        try
        {
            var client = _httpClientFactory.CreateClient(AcoustIdRateLimiter.ClientName);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await client.PostAsync("v2/submit",
                new FormUrlEncodedContent(BuildSubmitForm(clientKey, userKey, items)), cts.Token);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cts.Token));
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)) return true;

            // "invalid user API key" arrives as a 200 with an error envelope, like lookup refusals.
            var message = doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var m) ? m.GetString() : status;
            _logger.LogWarning("acoustid refused the submission: {Error}", message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("acoustid submission failed: {M}", ex.Message);
            return false;
        }
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> BuildSubmitForm(string clientKey, string userKey,
        IReadOnlyList<AcoustIdSubmission> items)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("client", clientKey),
            new("user", userKey),
            new("format", "json"),
            new("clientversion", $"octo-{Octo.Services.Common.OctoUserAgent.Version}"),
        };
        for (var i = 0; i < items.Count; i++)
        {
            form.Add(new($"duration.{i}", items[i].DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            form.Add(new($"fingerprint.{i}", items[i].Fingerprint));
            form.Add(new($"mbid.{i}", items[i].RecordingId));
            if (!string.IsNullOrEmpty(items[i].FileFormat)) form.Add(new($"fileformat.{i}", items[i].FileFormat!));
        }
        return form;
    }

    /// <summary>
    /// AcoustID answers some refusals with HTTP 200 and an error envelope, the exact shape
    /// that made over-budget Deezer calls silently destructive. IsOk is therefore read from
    /// the body, never from the status code.
    /// </summary>
    internal static AcoustIdLookup ParseLookup(JsonElement root)
    {
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            var message = root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var m) ? m.GetString() : status;
            return new AcoustIdLookup(false, message ?? "unknown error", []);
        }

        var results = new List<AcoustIdResult>();
        if (root.TryGetProperty("results", out var rs) && rs.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in rs.EnumerateArray().Take(MaxResults))
            {
                var score = result.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number
                    ? sc.GetDouble() : 0d;
                results.Add(new AcoustIdResult(score, ParseRecordings(result)) { Id = Str(result, "id") });
            }
        }

        return new AcoustIdLookup(true, null, results);
    }

    private static IReadOnlyList<AcoustIdRecording> ParseRecordings(JsonElement result)
    {
        if (!result.TryGetProperty("recordings", out var recs) || recs.ValueKind != JsonValueKind.Array)
            return [];

        var recordings = new List<AcoustIdRecording>();
        foreach (var rec in recs.EnumerateArray().Take(MaxRecordings))
        {
            var id = rec.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var title = rec.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";

            var artists = new List<string>();
            var credits = new List<AcoustIdCredit>();
            if (rec.TryGetProperty("artists", out var arts) && arts.ValueKind == JsonValueKind.Array)
                foreach (var artist in arts.EnumerateArray())
                    if (artist.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    {
                        artists.Add(name);
                        credits.Add(new AcoustIdCredit(name, Str(artist, "id"), Str(artist, "joinphrase") ?? ""));
                    }

            int? duration = rec.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(d.GetDouble()) : null;

            var release = PickRelease(rec);
            recordings.Add(new AcoustIdRecording(id, title, artists, release.Album, release.Year)
            {
                Credits = credits,
                Release = release.Detail,
                DurationSeconds = duration,
                Releases = AllReleases(rec),
                Isrcs = MusicBrainzClient.ParseIsrcs(rec),
                Sources = Int(rec, "sources") ?? 0,
            });
        }
        return recordings;
    }

    /// <summary>
    /// Prefer a plain studio album: a release group with no secondarytypes. Otherwise a
    /// compilation or a live album supplies the album name and year for a studio track.
    /// The year is the EARLIEST release in the chosen group, because a 2011 reissue is not
    /// the track's year. Within that group the earliest dated release is the one whose ids and
    /// track position are kept, so the tracks of one album converge on one release.
    /// </summary>
    private static (string? Album, int? Year, AcoustIdRelease? Detail) PickRelease(JsonElement recording)
    {
        if (!recording.TryGetProperty("releasegroups", out var groups)
            || groups.ValueKind != JsonValueKind.Array) return (null, null, null);

        JsonElement? chosen = null;
        foreach (var group in groups.EnumerateArray().Take(MaxReleaseGroups))
        {
            chosen ??= group;
            var isAlbum = group.TryGetProperty("type", out var ty)
                && string.Equals(ty.GetString(), "Album", StringComparison.OrdinalIgnoreCase);
            var hasSecondary = group.TryGetProperty("secondarytypes", out var sec)
                && sec.ValueKind == JsonValueKind.Array && sec.GetArrayLength() > 0;
            if (isAlbum && !hasSecondary) { chosen = group; break; }
        }
        if (chosen is not { } pick) return (null, null, null);

        var album = Str(pick, "title");
        var isCompilation = pick.TryGetProperty("secondarytypes", out var types)
            && types.ValueKind == JsonValueKind.Array
            && types.EnumerateArray().Any(type =>
                string.Equals(type.GetString(), "Compilation", StringComparison.OrdinalIgnoreCase));

        string? albumArtist = null;
        if (pick.TryGetProperty("artists", out var groupArtists) && groupArtists.ValueKind == JsonValueKind.Array)
        {
            var credits = groupArtists.EnumerateArray()
                .Where(artist => Str(artist, "name") is { Length: > 0 })
                .Select(artist => new AcoustIdCredit(Str(artist, "name")!, Str(artist, "id"), Str(artist, "joinphrase") ?? ""))
                .ToList();
            if (credits.Count > 0) albumArtist = AcoustIdRecording.JoinCredits(credits);
        }
        if (string.Equals(albumArtist, "Various Artists", StringComparison.OrdinalIgnoreCase)) isCompilation = true;

        int? year = null;
        JsonElement? earliest = null;
        var earliestDate = (Year: int.MaxValue, Month: 0, Day: 0);
        if (pick.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
        {
            foreach (var release in releases.EnumerateArray())
            {
                earliest ??= release;
                if (!release.TryGetProperty("date", out var date) || date.ValueKind != JsonValueKind.Object) continue;
                if (!date.TryGetProperty("year", out var y) || y.ValueKind != JsonValueKind.Number) continue;
                var candidate = y.GetInt32();
                if (candidate <= 0) continue;
                if (year is null || candidate < year) year = candidate;
                var when = (Year: candidate, Month: Int(date, "month") ?? 0, Day: Int(date, "day") ?? 0);
                if (when.CompareTo(earliestDate) < 0) { earliestDate = when; earliest = release; }
            }
        }

        int? trackNumber = null, trackCount = null, disc = null;
        string? releaseId = null, releaseTitle = null;
        if (earliest is { } rel)
        {
            releaseId = Str(rel, "id");
            // compress drops a release title equal to its group's.
            releaseTitle = Str(rel, "title");
            if (rel.TryGetProperty("mediums", out var mediums) && mediums.ValueKind == JsonValueKind.Array)
                foreach (var medium in mediums.EnumerateArray())
                {
                    if (!medium.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array
                        || tracks.GetArrayLength() == 0) continue;
                    trackNumber = Int(tracks[0], "position");
                    trackCount = Int(medium, "track_count");
                    disc = Int(medium, "position");
                    break;
                }
        }

        var clean = string.IsNullOrWhiteSpace(album) ? null : album;
        return (clean, year, new AcoustIdRelease(releaseId, Str(pick, "id"), releaseTitle ?? clean, year,
            trackNumber, trackCount, disc, albumArtist, isCompilation));
    }

    /// <summary>
    /// Every release of every group, bounded, each with what the chooser weighs: the group's
    /// kind, the release's date and country, the track's position and id. The same reading as
    /// PickRelease, over all of them instead of the one it picks.
    /// </summary>
    private static IReadOnlyList<AcoustIdRelease> AllReleases(JsonElement recording)
    {
        if (!recording.TryGetProperty("releasegroups", out var groups) || groups.ValueKind != JsonValueKind.Array) return [];

        var releases = new List<AcoustIdRelease>();
        foreach (var group in groups.EnumerateArray().Take(MaxReleaseGroups))
        {
            var groupId = Str(group, "id");
            var groupTitle = Str(group, "title");
            var primaryType = Str(group, "type");
            var secondaryTypes = group.TryGetProperty("secondarytypes", out var sec) && sec.ValueKind == JsonValueKind.Array
                ? sec.EnumerateArray().Select(t => t.GetString()).OfType<string>().ToList() : [];
            var (albumArtist, albumArtistIds) = GroupCredit(group);
            var isCompilation = secondaryTypes.Any(t => string.Equals(t, "Compilation", StringComparison.OrdinalIgnoreCase))
                || string.Equals(albumArtist, "Various Artists", StringComparison.OrdinalIgnoreCase);

            if (!group.TryGetProperty("releases", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                releases.Add(new AcoustIdRelease(null, groupId, groupTitle, null, null, null, null, albumArtist, isCompilation)
                {
                    GroupTitle = groupTitle, PrimaryType = primaryType, SecondaryTypes = secondaryTypes, AlbumArtistIds = albumArtistIds,
                });
                continue;
            }

            foreach (var release in list.EnumerateArray().Take(MaxReleasesPerGroup))
            {
                int? year = null;
                string? date = null;
                if (release.TryGetProperty("date", out var when) && when.ValueKind == JsonValueKind.Object
                    && Int(when, "year") is { } y && y > 0)
                {
                    year = y;
                    var month = Int(when, "month");
                    var day = Int(when, "day");
                    date = month is > 0 ? (day is > 0 ? $"{y:0000}-{month:00}-{day:00}" : $"{y:0000}-{month:00}") : $"{y:0000}";
                }

                int? trackNumber = null, trackCount = null, disc = null;
                string? trackId = null;
                if (release.TryGetProperty("mediums", out var mediums) && mediums.ValueKind == JsonValueKind.Array)
                    foreach (var medium in mediums.EnumerateArray())
                    {
                        if (!medium.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array
                            || tracks.GetArrayLength() == 0) continue;
                        trackNumber = Int(tracks[0], "position");
                        trackId = Str(tracks[0], "id");
                        trackCount = Int(medium, "track_count");
                        disc = Int(medium, "position");
                        break;
                    }

                // compress drops a release title equal to its group's.
                var title = Str(release, "title") ?? groupTitle;
                releases.Add(new AcoustIdRelease(Str(release, "id"), groupId, title, year, trackNumber, trackCount, disc,
                    albumArtist, isCompilation)
                {
                    GroupTitle = groupTitle,
                    PrimaryType = primaryType,
                    SecondaryTypes = secondaryTypes,
                    Country = Str(release, "country"),
                    Date = date,
                    ReleaseTrackId = trackId,
                    DiscCount = Int(release, "medium_count"),
                    AlbumArtistIds = albumArtistIds,
                });
            }
        }
        return releases;
    }

    private static (string? Credit, IReadOnlyList<string> Ids) GroupCredit(JsonElement group)
    {
        if (!group.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array) return (null, []);
        var credits = artists.EnumerateArray()
            .Where(artist => Str(artist, "name") is { Length: > 0 })
            .Select(artist => new AcoustIdCredit(Str(artist, "name")!, Str(artist, "id"), Str(artist, "joinphrase") ?? ""))
            .ToList();
        if (credits.Count == 0) return (null, []);
        return (AcoustIdRecording.JoinCredits(credits), credits.Select(c => c.ArtistId).OfType<string>().ToList());
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) ? number : null;
}
