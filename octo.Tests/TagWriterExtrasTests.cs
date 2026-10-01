using Octo.Services.Common;

namespace Octo.Tests;

/// <summary>
/// Every new field lands in the exact frame Picard writes and the library server reads, on an
/// MP3 and on a FLAC: the raw frames are read back, not TagLib's properties, since four of
/// TagLib's own names differ from Picard's. A new ID3 tag is version 4 so the original date has
/// its own frame; a tag a file arrived with keeps its version.
/// </summary>
public sealed class TagWriterExtrasTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "octo-tags-" + Guid.NewGuid().ToString("N"));

    public TagWriterExtrasTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Mp3()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(path, AudioFixtures.Mp3());
        return path;
    }

    private string Flac()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".flac");
        File.WriteAllBytes(path, AudioFixtures.Flac());
        return path;
    }

    private static void WriteTheSet(string path)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Title = "Teardrop";
        TagWriterExtras.SetText(file, TagFields.Isrc, "GBAAA9800001");
        TagWriterExtras.SetText(file, TagFields.Label, "Virgin");
        TagWriterExtras.SetText(file, TagFields.CatalogNumber, "CDV 2851");
        TagWriterExtras.SetText(file, TagFields.Barcode, "724384559922");
        TagWriterExtras.SetMulti(file, TagFields.ReleaseType, ["album"]);
        TagWriterExtras.SetText(file, TagFields.ReleaseStatus, "official");
        TagWriterExtras.SetText(file, TagFields.ReleaseCountry, "GB");
        TagWriterExtras.SetOriginalDate(file, "1998-04-20");
        TagWriterExtras.SetReleaseTrackId(file, "t-mezz-3");
        TagWriterExtras.SetMulti(file, TagFields.AlbumArtistId, ["a-ma"]);
        TagWriterExtras.SetMulti(file, TagFields.ArtistId, ["a-ma", "a-ef"]);
        TagWriterExtras.SetText(file, TagFields.FingerprintId, "acoustid-1");
        TagWriterExtras.SetReplayGain(file, -6.52, 0.891251, null, null);
        file.Save();
    }

    private static string? Txxx(TagLib.Id3v2.Tag id3, string description) =>
        TagLib.Id3v2.UserTextInformationFrame.Get(id3, description, false)?.Text?.FirstOrDefault();

    private static string? Text(TagLib.Id3v2.Tag id3, string frame) =>
        id3.GetFrames<TagLib.Id3v2.TextInformationFrame>(frame).FirstOrDefault()?.Text?.FirstOrDefault();

    [Fact]
    public void Mp3_EveryFieldLandsInPicardsFrame_AndTheNewTagIsVersion4()
    {
        var path = Mp3();
        WriteTheSet(path);

        using var file = TagLib.File.Create(path);
        var id3 = Assert.IsType<TagLib.Id3v2.Tag>(file.GetTag(TagLib.TagTypes.Id3v2, false));
        Assert.Equal(4, id3.Version);
        Assert.Equal("GBAAA9800001", Text(id3, "TSRC"));
        Assert.Equal("Virgin", Text(id3, "TPUB"));
        Assert.Equal("CDV 2851", Txxx(id3, "CATALOGNUMBER"));
        Assert.Equal("724384559922", Txxx(id3, "BARCODE"));
        Assert.Equal("album", Txxx(id3, "MusicBrainz Album Type"));
        Assert.Equal("official", Txxx(id3, "MusicBrainz Album Status"));
        Assert.Equal("GB", Txxx(id3, "MusicBrainz Album Release Country"));
        Assert.Equal("1998-04-20", Text(id3, "TDOR"));
        Assert.Equal("t-mezz-3", Txxx(id3, "MusicBrainz Release Track Id"));
        Assert.Equal("a-ma", Txxx(id3, "MusicBrainz Album Artist Id"));
        Assert.Equal(["a-ma", "a-ef"], TagLib.Id3v2.UserTextInformationFrame.Get(id3, "MusicBrainz Artist Id", false)!.Text);
        Assert.Equal("acoustid-1", Txxx(id3, "Acoustid Id"));
        Assert.Equal("-6.52 dB", Txxx(id3, "REPLAYGAIN_TRACK_GAIN"));
        Assert.Equal("0.891251", Txxx(id3, "REPLAYGAIN_TRACK_PEAK"));
        Assert.Null(Txxx(id3, "REPLAYGAIN_ALBUM_GAIN"));
        // TagLib's own readers agree on the fields it names the same way.
        Assert.Equal("GBAAA9800001", file.Tag.ISRC);
        Assert.Equal("Virgin", file.Tag.Publisher);
    }

    [Fact]
    public void Flac_EveryFieldLandsInPicardsVorbisName()
    {
        var path = Flac();
        WriteTheSet(path);

        using var file = TagLib.File.Create(path);
        var xiph = Assert.IsType<TagLib.Ogg.XiphComment>(file.GetTag(TagLib.TagTypes.Xiph, false));
        Assert.Equal("GBAAA9800001", xiph.GetFirstField("ISRC"));
        Assert.Equal("Virgin", xiph.GetFirstField("LABEL"));
        Assert.Equal("CDV 2851", xiph.GetFirstField("CATALOGNUMBER"));
        Assert.Equal("724384559922", xiph.GetFirstField("BARCODE"));
        Assert.Equal("album", xiph.GetFirstField("RELEASETYPE"));
        Assert.Equal("official", xiph.GetFirstField("RELEASESTATUS"));
        Assert.Equal("GB", xiph.GetFirstField("RELEASECOUNTRY"));
        Assert.Equal("1998-04-20", xiph.GetFirstField("ORIGINALDATE"));
        Assert.Equal("1998", xiph.GetFirstField("ORIGINALYEAR"));
        Assert.Equal("t-mezz-3", xiph.GetFirstField("MUSICBRAINZ_RELEASETRACKID"));
        Assert.Equal("a-ma", xiph.GetFirstField("MUSICBRAINZ_ALBUMARTISTID"));
        Assert.Equal(["a-ma", "a-ef"], xiph.GetField("MUSICBRAINZ_ARTISTID"));
        Assert.Equal("acoustid-1", xiph.GetFirstField("ACOUSTID_ID"));
        Assert.Equal("-6.52 dB", xiph.GetFirstField("REPLAYGAIN_TRACK_GAIN"));
        Assert.Equal("0.891251", xiph.GetFirstField("REPLAYGAIN_TRACK_PEAK"));
        // Not TagLib's own spellings, which Picard does not read.
        Assert.Null(xiph.GetFirstField("ORGANIZATION"));
        Assert.Null(xiph.GetFirstField("MUSICBRAINZ_ALBUMTYPE"));
    }

    /// <summary>A peer's version 3 tag stays version 3 and gets the original year in TORY.</summary>
    [Fact]
    public void Mp3_ExistingVersion3Tag_KeepsItsVersion_AndGetsTory()
    {
        var path = Mp3();
        using (var file = TagLib.File.Create(path))
        {
            var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
            id3.Version = 3;
            file.Tag.Title = "From a peer";
            file.Save();
        }

        using (var file = TagLib.File.Create(path))
        {
            TagWriterExtras.SetOriginalDate(file, "1998-04-20");
            TagWriterExtras.SetText(file, TagFields.Label, "Virgin");
            file.Save();
        }

        using var reopened = TagLib.File.Create(path);
        var tag = (TagLib.Id3v2.Tag)reopened.GetTag(TagLib.TagTypes.Id3v2, false);
        Assert.Equal(3, tag.Version);
        // TagLib reads a version 3 TORY back under its version 4 id, so the frame on disk is
        // what proves the version 3 spelling: the bytes carry TORY, not TDOR.
        Assert.Equal("1998", Text(tag, "TDOR"));
        Assert.Equal("Virgin", Text(tag, "TPUB"));
        var bytes = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path).Take(2048).ToArray());
        Assert.Contains("TORY", bytes);
        Assert.DoesNotContain("TDOR", bytes);
    }

    [Fact]
    public void SetText_EmptyValue_WritesNothing()
    {
        var path = Flac();
        using (var file = TagLib.File.Create(path))
        {
            TagWriterExtras.SetText(file, TagFields.Label, "Peer's Label");
            file.Save();
        }
        using (var file = TagLib.File.Create(path))
        {
            TagWriterExtras.SetText(file, TagFields.Label, "");
            TagWriterExtras.SetText(file, TagFields.Barcode, null);
            TagWriterExtras.SetReplayGain(file, null, null, null, null);
            file.Save();
        }
        using var reopened = TagLib.File.Create(path);
        Assert.Equal("Peer's Label", TagWriterExtras.ReadText(reopened, TagFields.Label));
        Assert.Null(TagWriterExtras.ReadText(reopened, TagFields.Barcode));
        Assert.Null(TagWriterExtras.ReadText(reopened, TagFields.TrackGain));
    }

    [Theory]
    [InlineData(-6.52, "-6.52 dB")]
    [InlineData(3.1, "+3.10 dB")]
    [InlineData(0, "+0.00 dB")]
    public void GainText_AlwaysSignedTwoDecimalsAndADot(double gain, string expected) =>
        Assert.Equal(expected, TagWriterExtras.GainText(gain));

    [Fact]
    public void PeakText_SixDecimals() => Assert.Equal("0.966051", TagWriterExtras.PeakText(0.966051));

    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    public void ReadFacts_RoundTripsWhatWasWritten(string format)
    {
        var path = format == "mp3" ? Mp3() : Flac();
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = "Teardrop";
            file.Tag.Performers = ["Massive Attack"];
            file.Tag.Album = "Mezzanine";
            file.Tag.AlbumArtists = ["Massive Attack"];
            file.Tag.Year = 1998;
            file.Tag.Track = 3;
            file.Tag.Disc = 1;
            file.Tag.MusicBrainzReleaseId = "r-mezz";
            TagWriterExtras.SetText(file, TagFields.Isrc, "GBAAA9800001");
            TagWriterExtras.SetText(file, TagFields.Label, "Virgin");
            TagWriterExtras.SetText(file, TagFields.CatalogNumber, "CDV 2851");
            TagWriterExtras.SetText(file, TagFields.Barcode, "724384559922");
            TagWriterExtras.SetRecordingId(file, "rec-teardrop");
            TagWriterExtras.SetCompilation(file, true);
            file.Save();
        }

        var facts = TagWriterExtras.ReadFacts(path, tagsAreEvidence: true);

        Assert.True(facts.TagsAreEvidence);
        Assert.Equal("." + format, facts.Extension);
        Assert.Equal("Teardrop", facts.Title);
        Assert.Equal("Massive Attack", facts.Artist);
        Assert.Equal("Mezzanine", facts.Album);
        Assert.Equal("Massive Attack", facts.AlbumArtist);
        Assert.Equal(1998, facts.Year);
        Assert.Equal(3, facts.Track);
        Assert.Equal(1, facts.Disc);
        Assert.Equal(["GBAAA9800001"], facts.Isrcs);
        Assert.Equal("724384559922", facts.Barcode);
        Assert.Equal("CDV 2851", facts.CatalogNumber);
        Assert.Equal("Virgin", facts.Label);
        Assert.Equal("rec-teardrop", facts.RecordingId);
        Assert.Equal("r-mezz", facts.ReleaseId);
        Assert.True(facts.IsCompilation);
        Assert.Equal(44100, facts.SampleRate);
    }

    /// <summary>An uploader's name is not a credit: a staged upload gives only its length and format.</summary>
    [Fact]
    public void ReadFacts_TagsNotEvidence_ReadsOnlyLengthAndFormat()
    {
        var path = Mp3();
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Title = "Some Channel - Song";
            file.Tag.Album = "Some Channel";
            file.Save();
        }

        var facts = TagWriterExtras.ReadFacts(path, tagsAreEvidence: false);

        Assert.False(facts.TagsAreEvidence);
        Assert.Null(facts.Title);
        Assert.Null(facts.Album);
        Assert.Equal(".mp3", facts.Extension);
    }

    [Fact]
    public void ReadFacts_UnreadableFile_IsUnknownNotAThrow()
    {
        var path = Path.Combine(_dir, "garbage.flac");
        File.WriteAllBytes(path, [1, 2, 3]);
        var facts = TagWriterExtras.ReadFacts(path, true);
        Assert.Equal(0, facts.DurationSeconds);
        Assert.False(facts.TagsAreEvidence);
    }
}
