using System.Diagnostics;
using Octo.Services.Common;
using Octo.Services.Library;

namespace Octo.Tests;

/// <summary>
/// W8: a replacement carries the tags Navidrome builds the song's ids from, read the way
/// Navidrome reads them, so it takes the original's place instead of arriving as a new song.
/// Everything else the new tagging wrote stays.
/// </summary>
public sealed class KeptIdentityTests : IDisposable
{
    private const string AlbumId = "1d2b6c3e-7a4f-4e1b-9c2d-3f4a5b6c7d8e";
    private const string TrackId = "8e7d6c5b-4a3f-4d2c-9b1a-0e9f8d7c6b5a";
    private const string NewAlbumId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    private const string NewTrackId = "11111111-2222-4333-8444-555555555555";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "octo-kept-" + Guid.NewGuid().ToString("N"));

    public KeptIdentityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Mp3() => Write(".mp3", AudioFixtures.Mp3());
    private string Flac() => Write(".flac", AudioFixtures.Flac());

    private string Write(string extension, byte[] bytes)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private void Ffmpeg(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-y -nostdin -hide_banner -v error " + arguments)
        {
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _dir,
        })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private static void TagAsTheNewPipelineWould(string path)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Title = "Teardrop (2019 Remaster)"; file.Tag.Album = "Mezzanine (Deluxe)";
        file.Tag.AlbumArtists = ["Massive Attack"]; file.Tag.Year = 2019; file.Tag.Genres = ["Trip Hop"];
        TagWriterExtras.SetRecordingId(file, "rec-teardrop");
        TagWriterExtras.SetText(file, TagFields.Isrc, "GBAAA9800001");
        TagWriterExtras.SetReplayGain(file, -6.52, 0.891251, null, null);
        TagWriterExtras.SetReleaseTrackId(file, NewTrackId);
        TagWriterExtras.SetText(file, TagFields.AlbumId, NewAlbumId);
        file.Save();
    }

    private static void AssertTheNewTaggingSurvived(string path)
    {
        using var file = TagLib.File.Create(path);
        Assert.Equal("rec-teardrop", TagWriterExtras.ReadRecordingId(file));
        Assert.Equal("GBAAA9800001", TagWriterExtras.ReadText(file, TagFields.Isrc));
        Assert.Equal("-6.52 dB", TagWriterExtras.ReadText(file, TagFields.TrackGain));
        Assert.Equal(["Trip Hop"], file.Tag.Genres);
    }

    [Fact]
    public void Mp3ToFlac_TheReplacementReadsAsTheOriginal()
    {
        var original = Mp3();
        using (var file = TagLib.File.Create(original))
        {
            file.Tag.Title = "Teardrop"; file.Tag.Album = "Mezzanine"; file.Tag.AlbumArtists = ["Massive Attack"];
            file.Tag.Track = 3; file.Tag.TrackCount = 11; file.Tag.Disc = 1;
            ((TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true)).SetTextFrame("TDRL", "1998-04-20");
            TagWriterExtras.SetText(file, TagFields.AlbumVersion, "Original");
            TagWriterExtras.SetText(file, TagFields.AlbumId, AlbumId.ToUpperInvariant());
            TagWriterExtras.SetReleaseTrackId(file, TrackId);
            file.Save();
        }
        var replacement = Flac(); TagAsTheNewPipelineWould(replacement);
        var identity = KeptIdentityTags.Read(original)!;
        KeptIdentityTags.Apply(replacement, identity);
        Assert.Equal((AlbumId, TrackId, "1998-04-20"), (identity.AlbumId, identity.ReleaseTrackId, identity.ReleaseDate));
        Assert.Equal(KeptIdentityTags.PidInputs(identity), KeptIdentityTags.PidInputs(KeptIdentityTags.Read(replacement)!));
        AssertTheNewTaggingSurvived(replacement);
    }

    [Fact]
    public void FlacToFlac_WhatTheOriginalLackedIsRemoved()
    {
        var original = Flac();
        using (var f = TagLib.File.Create(original)) { f.Tag.Title = "Teardrop"; f.Tag.Album = "Mezzanine"; f.Tag.AlbumArtists = ["Massive Attack"]; f.Save(); }
        var replacement = Flac(); TagAsTheNewPipelineWould(replacement);
        using (var f = TagLib.File.Create(replacement))
        {
            var x = (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true);
            x.SetField("ALBUMVERSION", "Deluxe"); x.SetField("MUSICBRAINZ_ALBUMCOMMENT", "remaster");
            x.SetField("RELEASEDATE", "2019-01-01"); x.SetField("YEAR", "2019"); x.SetField("ALBUM ARTIST", "Someone Else"); f.Save();
        }
        KeptIdentityTags.Apply(replacement, KeptIdentityTags.Read(original)!);
        using var after = TagLib.File.Create(replacement);
        var fields = ((TagLib.Ogg.XiphComment)after.GetTag(TagLib.TagTypes.Xiph, false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in new[] { "MUSICBRAINZ_ALBUMID", "MUSICBRAINZ_RELEASETRACKID", "ALBUMVERSION", "MUSICBRAINZ_ALBUMCOMMENT", "RELEASEDATE", "YEAR", "ALBUM ARTIST" })
            Assert.DoesNotContain(gone, fields);
        Assert.Equal(["Massive Attack"], after.Tag.AlbumArtists);
        AssertTheNewTaggingSurvived(replacement);
    }

    [Fact]
    public void Mp3ToFlac_TheReleaseTrackIdTheNewTaggingAddedIsRemoved()
    {
        var original = Mp3();
        using (var f = TagLib.File.Create(original)) { f.Tag.Title = "Teardrop"; f.Tag.Album = "Mezzanine"; f.Save(); }
        var replacement = Flac(); TagAsTheNewPipelineWould(replacement);
        KeptIdentityTags.Apply(replacement, KeptIdentityTags.Read(original)!);
        using var after = TagLib.File.Create(replacement);
        Assert.Null(TagWriterExtras.ReadText(after, TagFields.ReleaseTrackId));
        Assert.Null(KeptIdentityTags.Read(replacement)!.ReleaseTrackId);
    }

    [FfmpegFact]
    public void M4aToFlac_TheDateAtomCountsAsTheReleaseDate()
    {
        var original = Path.Combine(_dir, "original.m4a");
        Ffmpeg($"-f lavfi -i sine=frequency=440:duration=1 -c:a aac -b:a 128k \"{original}\"");
        using (var f = TagLib.File.Create(original))
        {
            f.Tag.Title = "Teardrop"; f.Tag.Album = "Mezzanine"; f.Tag.AlbumArtists = ["Massive Attack"]; f.Tag.Year = 1998;
            TagWriterExtras.SetText(f, TagFields.AlbumId, AlbumId); f.Save();
        }
        var replacement = Flac(); TagAsTheNewPipelineWould(replacement);
        var identity = KeptIdentityTags.Read(original)!;
        KeptIdentityTags.Apply(replacement, identity);
        Assert.Equal(("1998", AlbumId), (identity.ReleaseDate, identity.AlbumId));
        Assert.Equal(KeptIdentityTags.PidInputs(identity), KeptIdentityTags.PidInputs(KeptIdentityTags.Read(replacement)!));
        AssertTheNewTaggingSurvived(replacement);
    }

    [Theory]
    [InlineData("Massive Attack feat. Tracey Thorn", false, null, "Massive Attack")]
    [InlineData("Massive Attack", true, null, "Various Artists")]
    [InlineData("Massive Attack", false, "Massive Attack & Friends", "Massive Attack & Friends")]
    public void NoAlbumArtist_TakesTheNameNavidromeGaveTheAlbum(string artist, bool compilation, string? navidrome, string expected)
    {
        var original = Flac();
        using (var f = TagLib.File.Create(original)) { f.Tag.Title = "T"; f.Tag.Performers = [artist]; TagWriterExtras.SetCompilation(f, compilation); f.Save(); }
        Assert.Equal([expected], KeptIdentityTags.Read(original, navidrome)!.AlbumArtist);
    }

    /// <summary>Pin: TagLib# splits a version 3 TPE2 at "/", Navidrome's reader does not.</summary>
    [Fact]
    public void AVersion3AlbumArtistWithASlashStaysOneName()
    {
        var original = Mp3();
        using (var f = TagLib.File.Create(original))
        { var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true); id3.Version = 3; id3.SetTextFrame("TPE2", "AC/DC"); f.Save(); }
        Assert.Equal(["AC/DC"], KeptIdentityTags.Read(original)!.AlbumArtist);
    }

    [Theory]
    [InlineData("1998-04-20T10:00:00", "1998-04-20")] [InlineData("1998-04", "1998-04")] [InlineData("1998", "1998")]
    [InlineData("April 1998", "1998")] [InlineData("1998-13-01", "1998")] [InlineData("98", null)]
    public void NavidromeDate_MatchesParseDate(string raw, string? expected) => Assert.Equal(expected, KeptIdentityTags.NavidromeDate(raw));

    [Fact]
    public void NavidromeUuid_IsCanonicalOrNothing()
    {
        Assert.Equal(AlbumId, KeptIdentityTags.NavidromeUuid("{" + AlbumId.ToUpperInvariant() + "}"));
        Assert.Equal(AlbumId, KeptIdentityTags.NavidromeUuid("urn:uuid:" + AlbumId));
        Assert.Null(KeptIdentityTags.NavidromeUuid("not-an-id"));
    }
}
