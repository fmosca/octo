namespace Octo.Services.Library;

/// <summary>
/// What a library action hands the download so the replacement takes the original's place in
/// Navidrome rather than arriving as a new song (W8).
/// </summary>
public sealed class ReplacementHandoff
{
    public required string OriginalPath { get; init; }
    public required KeptIdentity Identity { get; init; }

    /// <summary>Called once the replacement is tagged and still out of the scanner's sight.
    /// Null means go ahead, and the original has been moved out; otherwise why it is refused.</summary>
    public required Func<string, Task<string?>> BeforeReveal { get; init; }

    /// <summary>Set by the download once the replacement moved in. Null when the download ran
    /// without this handoff, because it joined one already in flight.</summary>
    public string? RevealedPath { get; internal set; }

    /// <summary>The original's folder and name with the new extension, or null when another
    /// file holds that name. With the same extension this is the original's own path, which
    /// Navidrome updates in place.</summary>
    public string? TargetFor(string extension)
    {
        var target = Path.Combine(Path.GetDirectoryName(OriginalPath)!,
            Path.GetFileNameWithoutExtension(OriginalPath) + extension);
        return File.Exists(target) ? null : target;
    }
}

/// <summary>The library action refused the replacement before it was ever in the library.</summary>
public sealed class ReplacementRejectedException(string problem) : Exception(problem)
{
    public string Problem => Message;
}
