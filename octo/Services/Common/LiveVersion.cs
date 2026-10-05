using System.Text.RegularExpressions;

namespace Octo.Services.Common;

/// <summary>
/// Live takes are never what a request meant unless it said so. A title like "Song (Live)" is
/// already its own version to SongIdentity; this covers the places that rule cannot see: the
/// album folder a peer files a plainly named track under ("Decade (live at the El Mocambo)"),
/// and a recording MusicBrainz only ever lists on live albums.
/// </summary>
internal static partial class LiveVersion
{
    [GeneratedRegex(@"\b(live|unplugged|in concert|concert|bootleg)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Marker();

    public static bool Mentions(string? text) => !string.IsNullOrWhiteSpace(text) && Marker().IsMatch(text);

    /// <summary>
    /// Whether the request itself asks for a live take, by its title or its album. Generous on
    /// purpose: a title such as "Live Forever" lets a live take through rather than ever
    /// refusing a song someone hearted from a live album.
    /// </summary>
    public static bool Requested(string? title, string? album) => Mentions(title) || Mentions(album);
}
