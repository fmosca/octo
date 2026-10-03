using System.Text.RegularExpressions;

namespace Octo.Services.Updates;

/// <summary>
/// A dated Octo release such as "2026.10.02" or "2026.10.02.1", compared by its numbers. The
/// repo also publishes "desktop-v1.3.2" and "android-v1.3.0"; those never parse, which is how
/// the server's own releases are told apart from the apps'.
/// </summary>
public readonly partial record struct ReleaseVersion(int Year, int Month, int Day, int Patch) : IComparable<ReleaseVersion>
{
    [GeneratedRegex(@"^v?(\d{4})\.(\d{2})\.(\d{2})(?:\.(\d{1,4}))?$")]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        // .NET stamps "+<commit>" after the informational version; the release is what is before it.
        var match = Pattern().Match((text ?? "").Split('+')[0].Trim());
        if (!match.Success) return false;
        version = new ReleaseVersion(
            int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value),
            match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0);
        return true;
    }

    public int CompareTo(ReleaseVersion other) =>
        (Year, Month, Day, Patch).CompareTo((other.Year, other.Month, other.Day, other.Patch));

    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;

    public override string ToString() =>
        Patch > 0 ? $"{Year:D4}.{Month:D2}.{Day:D2}.{Patch}" : $"{Year:D4}.{Month:D2}.{Day:D2}";

    /// <summary>The release this build came from, e.g. "2026.10.02", with the commit cut off.</summary>
    public static string Running { get; } =
        (typeof(ReleaseVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
            ?.Split('+')[0])
        ?? typeof(ReleaseVersion).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
