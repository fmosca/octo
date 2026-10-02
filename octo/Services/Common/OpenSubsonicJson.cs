using System.Text.Json;
using System.Text.Json.Nodes;

namespace Octo.Services.Common;

/// <summary>
/// Readers for the nested objects OpenSubsonic adds to a song, for the paths that rebuild a
/// library row from Navidrome's answer instead of passing that answer through. Those paths
/// (radio, the Discovery blend) hand-build a <see cref="Models.Domain.Song"/>, so every field
/// they do not carry is a field the client does not get — which is how library tracks came to
/// play unnormalised beside the tracks they were normalised against.
/// </summary>
public static class OpenSubsonicJson
{
    /// <summary>
    /// The four ReplayGain values Navidrome lists on a song, from the response body Octo
    /// parsed itself. All null when the song carries no <c>replayGain</c> object, which is
    /// what Navidrome answers for a file without ReplayGain tags.
    /// </summary>
    public static (double? TrackGain, double? AlbumGain, double? TrackPeak, double? AlbumPeak)
        ReplayGain(JsonElement song)
    {
        if (!song.TryGetProperty("replayGain", out var gain) || gain.ValueKind != JsonValueKind.Object)
            return (null, null, null, null);
        return (Number(gain, "trackGain"), Number(gain, "albumGain"),
                Number(gain, "trackPeak"), Number(gain, "albumPeak"));
    }

    /// <inheritdoc cref="ReplayGain(JsonElement)"/>
    public static (double? TrackGain, double? AlbumGain, double? TrackPeak, double? AlbumPeak)
        ReplayGain(JsonObject song)
    {
        if (song["replayGain"] is not JsonObject gain)
            return (null, null, null, null);
        return (Number(gain, "trackGain"), Number(gain, "albumGain"),
                Number(gain, "trackPeak"), Number(gain, "albumPeak"));
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static double? Number(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
