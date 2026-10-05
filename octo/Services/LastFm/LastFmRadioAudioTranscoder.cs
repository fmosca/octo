using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Octo.Services.LastFm;

public interface ILastFmRadioAudioTranscoder
{
    /// <summary>
    /// Transcodes one track to a headerless MP3 segment on <paramref name="output"/>.
    /// When <paramref name="targetLufs"/> is set the track is first measured (EBU R128)
    /// and brought to that integrated loudness with a static gain and a true-peak
    /// limiter. Returns what was measured, or null when measurement was not possible.
    /// </summary>
    Task<RadioAudioProfile?> TranscodeToMp3Async(Stream input, Stream output, int bitrateKbps,
        double? targetLufs, CancellationToken cancellationToken);
}

/// <summary>
/// What one radio track sounds like and what it is, gathered while it was prepared.
/// Loudness fields describe the SOURCE; <see cref="GainDb"/> is what was applied to
/// reach the target. The spectral fields are means over the track and describe its
/// character (brightness, noisiness, bandwidth), which survive the gain unchanged.
/// <see cref="Genre"/> is the catalogue genre the track resolved with and
/// <see cref="Tags"/> its Last.fm top tags (sub-genre), so the flow picker can judge
/// kinship alongside sound rather than by sound alone.
/// </summary>
public sealed record RadioAudioProfile(
    double IntegratedLufs,
    double LoudnessRangeLu,
    double TruePeakDbfs,
    double GainDb,
    double SpectralCentroidHz,
    double SpectralFlatness,
    double SpectralRolloffHz,
    string? Genre = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>Normalizes mixed local FLAC and external M4A sources into one MP3
/// byte stream. A fresh process per song prevents decoder state leaking across
/// track/container boundaries; its stdout is appended to the same client response.
/// The input is spooled to a temporary file so it can be read twice: once to
/// measure, once to encode with the measured gain.</summary>
public sealed class FfmpegLastFmRadioAudioTranscoder : ILastFmRadioAudioTranscoder
{
    /// <summary>Largest static gain applied in either direction. Anything further out is
    /// almost certainly a measurement of silence or damage, not a quiet master.</summary>
    internal const double MaximumGainDb = 18;

    /// <summary>-1 dBTP as a linear limit for the true-peak limiter.</summary>
    private const string LimiterCeiling = "0.891251";

    private static readonly Regex SpectralLine = new(
        @"^lavfi\.aspectralstats\.1\.(centroid|flatness|rolloff)=(-?[0-9.]+(?:e[-+]?\d+)?)\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public async Task<RadioAudioProfile?> TranscodeToMp3Async(Stream input, Stream output,
        int bitrateKbps, double? targetLufs, CancellationToken cancellationToken)
    {
        var spool = Path.Combine(Path.GetTempPath(), "octo-radio-in-" + Guid.NewGuid().ToString("N"));
        var spectral = spool + ".spectral";
        try
        {
            await using (var file = new FileStream(spool, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous))
                await input.CopyToAsync(file, cancellationToken);

            var measured = await MeasureLoudnessAsync(spool, cancellationToken);
            var gain = GainFor(measured?.IntegratedLufs, targetLufs);

            var filters = new List<string>();
            if (gain != 0)
            {
                filters.Add("volume=" + gain.ToString("0.00", CultureInfo.InvariantCulture) + "dB");
                filters.Add("alimiter=limit=" + LimiterCeiling + ":level=false:attack=5:release=50");
            }
            // The metadata sink is named relative to the temp directory, which RunAsync
            // uses as the working directory: a filter option value cannot carry the
            // drive colon or backslashes of an absolute Windows path unescaped.
            filters.Add("aspectralstats=win_size=4096");
            filters.Add("ametadata=mode=print:file=" + Path.GetFileName(spectral));

            await RunAsync(new[]
            {
                "-hide_banner", "-loglevel", "error", "-i", spool, "-vn",
                "-map_metadata", "-1", "-af", string.Join(',', filters),
                "-codec:a", "libmp3lame", "-b:a", $"{bitrateKbps}k",
                "-write_xing", "0", "-id3v2_version", "0", "-f", "mp3", "pipe:1"
            }, output, cancellationToken);

            if (measured is null) return null;
            var (centroid, flatness, rolloff) = ReadSpectral(spectral);
            return new RadioAudioProfile(measured.Value.IntegratedLufs, measured.Value.LoudnessRangeLu,
                measured.Value.TruePeakDbfs, gain, centroid, flatness, rolloff);
        }
        finally
        {
            TryDelete(spool);
            TryDelete(spectral);
        }
    }

    /// <summary>
    /// The static gain that takes a measured loudness to the target, bounded. No target,
    /// or a measurement that is not a number (silence reads as -inf), means no gain.
    /// </summary>
    internal static double GainFor(double? measuredLufs, double? targetLufs)
    {
        if (targetLufs is not { } target || measuredLufs is not { } measured) return 0;
        if (double.IsNaN(measured) || double.IsInfinity(measured)) return 0;
        return Math.Round(Math.Clamp(target - measured, -MaximumGainDb, MaximumGainDb), 2);
    }

    private static async Task<(double IntegratedLufs, double LoudnessRangeLu, double TruePeakDbfs)?>
        MeasureLoudnessAsync(string spool, CancellationToken cancellationToken)
    {
        // ebur128 prints its summary on stderr at the default log level; nothing else
        // in this invocation writes there. Decode to a fixed format first because a
        // stream whose first packet probes differently from the rest re-initialises
        // the graph and the scanner with it.
        await using var sink = Stream.Null;
        string report;
        try
        {
            report = await RunAsync(new[]
            {
                "-hide_banner", "-nostats", "-i", spool,
                "-af", "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,ebur128=peak=true",
                "-f", "null", "-"
            }, sink, cancellationToken, tolerateFailure: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        // The same summary the download path reads for ReplayGain, parsed in one place.
        var measured = Octo.Services.Audio.LoudnessMeter.Parse(report);
        return measured is null ? null : (measured.IntegratedLufs, measured.LoudnessRangeLu, measured.TruePeakDbfs);
    }

    private static (double Centroid, double Flatness, double Rolloff) ReadSpectral(string path)
    {
        if (!File.Exists(path)) return (0, 0, 0);
        double centroid = 0, flatness = 0, rolloff = 0;
        int centroids = 0, flatnesses = 0, rolloffs = 0;
        foreach (Match match in SpectralLine.Matches(File.ReadAllText(path)))
        {
            var value = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || double.IsInfinity(value)) continue;
            switch (match.Groups[1].Value.ToLowerInvariant())
            {
                case "centroid": centroid += value; centroids++; break;
                case "flatness": flatness += value; flatnesses++; break;
                case "rolloff": rolloff += value; rolloffs++; break;
            }
        }
        return (centroids == 0 ? 0 : centroid / centroids,
            flatnesses == 0 ? 0 : flatness / flatnesses,
            rolloffs == 0 ? 0 : rolloff / rolloffs);
    }

    /// <summary>Runs ffmpeg with stdout copied to <paramref name="output"/>; returns stderr.</summary>
    private static async Task<string> RunAsync(IEnumerable<string> arguments, Stream output,
        CancellationToken cancellationToken, bool tolerateFailure = false)
    {
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
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start()) throw new InvalidOperationException("ffmpeg did not start");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Continuous Radio needs ffmpeg in the Octo runtime image", ex);
        }

        try
        {
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await outputTask;
            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            if (process.ExitCode != 0 && !tolerateFailure)
                throw new InvalidOperationException($"ffmpeg exited {process.ExitCode}: {error.Trim()}");
            return error;
        }
        catch
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* best effort during disconnect/shutdown */ }
            }
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* temp cleanup is best effort */ }
    }
}
