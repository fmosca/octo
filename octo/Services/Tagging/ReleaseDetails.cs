using System.Text.Json;

namespace Octo.Services.Tagging;

/// <summary>One track of a release as the music database lists it.</summary>
public sealed record ReleaseTrack(string? RecordingId, string? ReleaseTrackId, int? Position, string? Number,
    int DiscNumber, int TrackCount, string? Title, int? LengthSeconds, IReadOnlyList<string> Isrcs);

/// <summary>A genre people voted on for a release, with the vote count.</summary>
public sealed record ReleaseGenre(string Name, int Count);

/// <summary>
/// What one music database release lookup adds to a candidate: its label and catalogue number,
/// barcode, status, country and date, its group's kind and first release date, the album
/// artist's ids, every track's position and ids, and the genres people voted on. A block the
/// answer lacks is null or empty, never a throw.
/// </summary>
public sealed record ReleaseDetails(
    string ReleaseId, string? Title, string? Status, string? Date, string? Country, string? Barcode,
    string? Label, string? CatalogNumber, string? GroupId, string? GroupTitle, string? GroupFirstReleaseDate,
    string? PrimaryType, IReadOnlyList<string> SecondaryTypes, string? AlbumArtist,
    IReadOnlyList<string> AlbumArtistIds, int DiscCount, IReadOnlyList<ReleaseTrack> Tracks,
    IReadOnlyList<ReleaseGenre> Genres)
{
    public bool IsCompilation => SecondaryTypes.Any(t => string.Equals(t, "Compilation", StringComparison.OrdinalIgnoreCase))
        || string.Equals(AlbumArtist, "Various Artists", StringComparison.OrdinalIgnoreCase);

    /// <summary>The track that is this recording, or null when the release does not list it.</summary>
    public ReleaseTrack? TrackFor(string? recordingId) =>
        string.IsNullOrEmpty(recordingId) ? null
            : Tracks.FirstOrDefault(t => string.Equals(t.RecordingId, recordingId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The genres with at least <paramref name="minVotes"/> votes, most voted first.</summary>
    public IReadOnlyList<string> TopGenres(int minVotes = 2, int max = 3) =>
        Genres.Where(g => g.Count >= minVotes).OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.Ordinal)
            .Select(g => g.Name).Take(max).ToList();

    /// <summary>Read the release lookup's answer. Null for a document that is not a release.</summary>
    public static ReleaseDetails? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var id = Str(root, "id");
        if (string.IsNullOrEmpty(id)) return null;

        string? label = null, catalogNumber = null;
        if (root.TryGetProperty("label-info", out var labels) && labels.ValueKind == JsonValueKind.Array)
            foreach (var info in labels.EnumerateArray())
            {
                catalogNumber ??= Clean(Str(info, "catalog-number"));
                if (info.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.Object)
                    label ??= Clean(Str(l, "name"));
                if (label is not null && catalogNumber is not null) break;
            }

        string? groupId = null, groupTitle = null, groupFirst = null, primaryType = null;
        var secondaryTypes = new List<string>();
        if (root.TryGetProperty("release-group", out var group) && group.ValueKind == JsonValueKind.Object)
        {
            groupId = Str(group, "id");
            groupTitle = Str(group, "title");
            groupFirst = Clean(Str(group, "first-release-date"));
            primaryType = Str(group, "primary-type");
            if (group.TryGetProperty("secondary-types", out var secondary) && secondary.ValueKind == JsonValueKind.Array)
                secondaryTypes.AddRange(secondary.EnumerateArray().Select(t => t.GetString()).OfType<string>());
        }

        var (albumArtist, albumArtistIds) = Credits(root);

        var tracks = new List<ReleaseTrack>();
        var discCount = 0;
        if (root.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            foreach (var medium in media.EnumerateArray())
            {
                discCount++;
                var disc = Int(medium, "position") ?? discCount;
                var count = Int(medium, "track-count") ?? 0;
                if (!medium.TryGetProperty("tracks", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                if (count == 0) count = list.GetArrayLength();
                foreach (var track in list.EnumerateArray())
                {
                    string? recordingId = null, title = null;
                    int? length = null;
                    IReadOnlyList<string> isrcs = [];
                    if (track.TryGetProperty("recording", out var recording) && recording.ValueKind == JsonValueKind.Object)
                    {
                        recordingId = Str(recording, "id");
                        title = Str(recording, "title");
                        isrcs = Isrcs(recording);
                        if (Int(recording, "length") is { } ms) length = (int)Math.Round(ms / 1000.0);
                    }
                    title ??= Str(track, "title");
                    if (length is null && Int(track, "length") is { } trackMs) length = (int)Math.Round(trackMs / 1000.0);
                    tracks.Add(new ReleaseTrack(recordingId, Str(track, "id"), Int(track, "position"), Str(track, "number"),
                        disc, count, title, length, isrcs));
                }
            }

        var genres = new List<ReleaseGenre>();
        if (root.TryGetProperty("genres", out var genreList) && genreList.ValueKind == JsonValueKind.Array)
            foreach (var genre in genreList.EnumerateArray())
                if (Str(genre, "name") is { Length: > 0 } name)
                    genres.Add(new ReleaseGenre(name, Int(genre, "count") ?? 0));

        return new ReleaseDetails(id, Str(root, "title"), Clean(Str(root, "status")), Clean(Str(root, "date")),
            Clean(Str(root, "country")), Clean(Str(root, "barcode")), label, catalogNumber, groupId, groupTitle,
            groupFirst, primaryType, secondaryTypes, albumArtist, albumArtistIds, discCount, tracks, genres);
    }

    /// <summary>The release's artist credit as the database prints it, and each credited artist's id.</summary>
    internal static (string? Credit, IReadOnlyList<string> Ids) Credits(JsonElement element)
    {
        if (!element.TryGetProperty("artist-credit", out var credit) || credit.ValueKind != JsonValueKind.Array)
            return (null, []);
        var text = new System.Text.StringBuilder();
        var ids = new List<string>();
        foreach (var entry in credit.EnumerateArray())
        {
            var name = Str(entry, "name");
            if (entry.TryGetProperty("artist", out var artist) && artist.ValueKind == JsonValueKind.Object)
            {
                name ??= Str(artist, "name");
                if (Str(artist, "id") is { Length: > 0 } id) ids.Add(id);
            }
            text.Append(name).Append(Str(entry, "joinphrase"));
        }
        var joined = text.ToString().Trim();
        return (joined.Length == 0 ? null : joined, ids);
    }

    internal static IReadOnlyList<string> Isrcs(JsonElement element) =>
        element.TryGetProperty("isrcs", out var isrcs) && isrcs.ValueKind == JsonValueKind.Array
            ? isrcs.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? Octo.Services.Common.SongIdentity.NormalizeIsrc(i.GetString()) : null)
                .OfType<string>().Distinct(StringComparer.Ordinal).ToList()
            : [];

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;
}
