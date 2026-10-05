using System.Globalization;
using Octo.Services.Audio;

namespace Octo.Tests;

/// <summary>
/// The loudness summary ffmpeg prints, the ReplayGain maths from it, and the formats players
/// read: a gain with two decimals and " dB", a peak with six, and a dot whatever the culture.
/// </summary>
public class LoudnessMeterTests
{
    /// <summary>A real summary block, captured from ffmpeg 6 with ebur128=peak=true.</summary>
    private const string Summary = """
    [Parsed_ebur128_1 @ 0x55d0] Summary:

      Integrated loudness:
        I:         -11.5 LUFS
        Threshold: -21.8 LUFS

      Loudness range:
        LRA:         6.3 LU
        Threshold: -31.9 LUFS
        LRA low:   -15.6 LUFS
        LRA high:   -9.3 LUFS

      True peak:
        Peak:       -0.3 dBFS
    """;

    [Fact]
    public void Parse_ReadsIntegratedRangeAndTruePeak()
    {
        var loudness = LoudnessMeter.Parse(Summary)!;
        Assert.Equal(-11.5, loudness.IntegratedLufs, 3);
        Assert.Equal(6.3, loudness.LoudnessRangeLu, 3);
        Assert.Equal(-0.3, loudness.TruePeakDbfs, 3);
    }

    [Fact]
    public void Parse_NoSummary_IsNull() => Assert.Null(LoudnessMeter.Parse("some other stderr"));

    [Fact]
    public void Parse_Silence_IsNegativeInfinity()
    {
        var loudness = LoudnessMeter.Parse("  I:         -inf LUFS\n  Peak:       -inf dBFS\n")!;
        Assert.True(double.IsNegativeInfinity(loudness.IntegratedLufs));
    }

    [Fact]
    public void ForTrack_GainAndPeakFromTheSummary()
    {
        var tags = ReplayGainTags.ForTrack(LoudnessMeter.Parse(Summary))!;
        Assert.Equal(-6.5, tags.GainDb, 3);
        Assert.Equal(0.966051, tags.Peak, 6);
        Assert.Equal("-6.50 dB", tags.GainText);
        Assert.Equal("0.966051", tags.PeakText);
    }

    [Fact]
    public void ForTrack_SilenceOrNothing_IsNull()
    {
        Assert.Null(ReplayGainTags.ForTrack(null));
        Assert.Null(ReplayGainTags.ForTrack(new Loudness(double.NegativeInfinity, 0, double.NegativeInfinity)));
        Assert.Null(ReplayGainTags.ForTrack(new Loudness(double.NaN, 0, 0)));
    }

    [Fact]
    public void ForTrack_GainIsClampedToTwentyFourDecibels()
    {
        Assert.Equal(24, ReplayGainTags.ForTrack(new Loudness(-70, 0, -40))!.GainDb);
        Assert.Equal(-24, ReplayGainTags.ForTrack(new Loudness(20, 0, 3))!.GainDb);
    }

    [Fact]
    public void OpusGain_IsRelativeToMinus23InStepsOf256()
    {
        var tags = ReplayGainTags.ForTrack(new Loudness(-11.5, 0, -0.3))!;
        Assert.Equal((int)Math.Round((-23 + 11.5) * 256), tags.OpusGain);
        Assert.Equal(-2944, tags.OpusGain);
    }

    [Fact]
    public void ForAlbum_PowerMeanOfTheTracks_AndTheLoudestPeak()
    {
        var album = ReplayGainTags.ForAlbum([new Loudness(-10, 0, -1), new Loudness(-14, 0, -0.5)])!;
        var expected = 10 * Math.Log10((Math.Pow(10, -1.0) + Math.Pow(10, -1.4)) / 2);
        Assert.Equal(Math.Round(-18 - expected, 2), album.GainDb, 3);
        Assert.Equal(Math.Round(Math.Pow(10, -0.5 / 20), 6), album.Peak, 6);
    }

    [Fact]
    public void ForAlbum_AnyUnmeasuredTrack_GivesNoAlbumGain()
    {
        Assert.Null(ReplayGainTags.ForAlbum([new Loudness(-10, 0, -1), null]));
        Assert.Null(ReplayGainTags.ForAlbum([]));
    }

    /// <summary>A server in a comma-decimal locale must still write a dot.</summary>
    [Fact]
    public void Formats_UseADotWhateverTheCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var tags = new ReplayGainTags(-6.5, 0.966051, -11.5);
            Assert.Equal("-6.50 dB", tags.GainText);
            Assert.Equal("0.966051", tags.PeakText);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public async Task Measure_MissingFile_IsNull()
    {
        var meter = new LoudnessMeter(Microsoft.Extensions.Logging.Abstractions.NullLogger<LoudnessMeter>.Instance);
        Assert.Null(await meter.MeasureAsync(Path.Combine(Path.GetTempPath(), "octo-no-such-file.flac"), 5));
    }
}
