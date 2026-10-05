using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Octo.Services.Audio;

/// <summary>What one file sounds like by the loudness standard: its integrated loudness, its
/// loudness range and its true peak.</summary>
public sealed record Loudness(double IntegratedLufs, double LoudnessRangeLu, double TruePeakDbfs);

/// <summary>
/// ReplayGain values for one file: the gain that brings it to the reference level and its peak
/// as a fraction of full scale. Null when the measurement was silence or damage.
/// </summary>
public sealed record ReplayGainTags(double GainDb, double Peak, double IntegratedLufs)
{
    /// <summary>The level ReplayGain 2.0 brings every track to.</summary>
    public const double ReferenceLufs = -18;

    /// <summary>The most a gain may be in either direction; beyond it the measurement is of
    /// silence or damage, not a quiet master.</summary>
    public const double MaxGainDb = 24;

    /// <summary>The level Opus files are normalised to, with the gain stored in 1/256 dB.</summary>
    private const double OpusReferenceLufs = -23;

    public static ReplayGainTags? ForTrack(Loudness? loudness)
    {
        if (loudness is null) return null;
        if (double.IsNaN(loudness.IntegratedLufs) || double.IsInfinity(loudness.IntegratedLufs)) return null;
        var gain = Math.Round(Math.Clamp(ReferenceLufs - loudness.IntegratedLufs, -MaxGainDb, MaxGainDb), 2);
        var peakDb = double.IsNaN(loudness.TruePeakDbfs) || double.IsInfinity(loudness.TruePeakDbfs) ? -100 : loudness.TruePeakDbfs;
        var peak = Math.Round(Math.Pow(10, peakDb / 20), 6);
        return new ReplayGainTags(gain, peak, loudness.IntegratedLufs);
    }

    /// <summary>
    /// The album's values from its tracks: the gain for the album's loudness as a whole, taken
    /// as the power mean of the tracks' integrated loudness, and the loudest peak. Null when
    /// any track could not be measured, since an album gain for half an album is worse than none.
    /// </summary>
    public static ReplayGainTags? ForAlbum(IReadOnlyList<Loudness?> tracks)
    {
        if (tracks.Count == 0 || tracks.Any(t => t is null)) return null;
        var measured = tracks.Select(t => t!).ToList();
        if (measured.Any(t => double.IsNaN(t.IntegratedLufs) || double.IsInfinity(t.IntegratedLufs))) return null;
        var power = measured.Average(t => Math.Pow(10, t.IntegratedLufs / 10));
        var integrated = 10 * Math.Log10(power);
        var peak = measured.Max(t => double.IsNaN(t.TruePeakDbfs) || double.IsInfinity(t.TruePeakDbfs) ? -100 : t.TruePeakDbfs);
        return ForTrack(new Loudness(integrated, 0, peak));
    }

    public string GainText => GainDb.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + " dB";
    public string PeakText => Peak.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>The gain an Opus file carries, relative to -23 LUFS in 1/256 dB steps.</summary>
    public int OpusGain => (int)Math.Round((OpusReferenceLufs - IntegratedLufs) * 256);
}

public interface ILoudnessMeter
{
    /// <summary>Measure one file. Null when ffmpeg is missing, the file will not decode, or the
    /// time ran out; never a throw, never a failed download.</summary>
    Task<Loudness?> MeasureAsync(string path, int timeoutSeconds, CancellationToken ct = default);
}

/// <summary>
/// Measures a file's loudness with ffmpeg, the way the radio transcoder does: decode to one
/// fixed format, run the loudness filter with true peak on, and read the summary it prints on
/// stderr. A missing ffmpeg is latched after one warning, so a misbuilt image costs a log line
/// and not a spawned process per download.
/// </summary>
public sealed class LoudnessMeter : ILoudnessMeter
{
    private static readonly Regex LoudnessLine = new(
        @"^\s*(I|LRA|Peak):\s+(-?[0-9.]+|-inf|inf)\s+(LUFS|LU|dBFS)", RegexOptions.Multiline);

    private readonly ILogger<LoudnessMeter> _logger;
    private volatile bool _binaryMissing;

    public LoudnessMeter(ILogger<LoudnessMeter> logger) => _logger = logger;

    public async Task<Loudness?> MeasureAsync(string path, int timeoutSeconds, CancellationToken ct = default)
    {
        if (_binaryMissing || string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-nostats", "-i", path, "-vn",
                     "-af", "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,ebur128=peak=true",
                     "-f", "null", "-",
                 })
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start()) throw new InvalidOperationException("ffmpeg did not start");
        }
        catch (Exception ex)
        {
            _binaryMissing = true;
            _logger.LogWarning("ffmpeg is not in this image, so downloads get no ReplayGain: {M}", ex.Message);
            return null;
        }

        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cts.Token);
            await process.WaitForExitAsync(cts.Token);
            var report = await errorTask;
            var loudness = Parse(report);
            if (loudness is null)
                _logger.LogDebug("ffmpeg gave no loudness summary for {Path} (exit {Code})", path, process.ExitCode);
            return loudness;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            _logger.LogWarning("the loudness measurement of {Path} took longer than {Timeout}s; no ReplayGain", path, timeoutSeconds);
            return null;
        }
        catch (Exception ex)
        {
            Kill(process);
            _logger.LogDebug("the loudness measurement of {Path} failed: {M}", path, ex.Message);
            return null;
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }

    /// <summary>The summary block the loudness filter prints at the end of its stderr.</summary>
    internal static Loudness? Parse(string report)
    {
        double? integrated = null, range = null, peak = null;
        foreach (Match match in LoudnessLine.Matches(report))
        {
            var value = ParseLevel(match.Groups[2].Value);
            switch (match.Groups[1].Value)
            {
                case "I": integrated = value; break;
                case "LRA": range = value; break;
                case "Peak": peak = value; break;
            }
        }
        return integrated is null ? null : new Loudness(integrated.Value, range ?? 0, peak ?? 0);
    }

    private static double ParseLevel(string text) => text switch
    {
        "-inf" => double.NegativeInfinity,
        "inf" => double.PositiveInfinity,
        _ => double.Parse(text, CultureInfo.InvariantCulture),
    };
}
