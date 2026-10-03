using Octo.Services.Fingerprint;
using Octo.Services.Soulseek;

namespace Octo.Tests;

/// <summary>
/// A live take is never what a request meant unless it said so. 2026-10-03: a heart on
/// Silverstein's "Smile in Your Sleep" took a peer's plainly named file from the folder
/// "Decade (live at the El Mocambo) (2010)", and AcoustID confirmed it at 100% as the live
/// recording. The folder is now read when peers are picked, and a recording MusicBrainz only
/// lists on live albums is refused like a wrong song.
/// </summary>
public class LiveVersionTests
{
    private const string LivePath = @"Music\Silverstein\Decade (live at the El Mocambo) (2010)\17 - Smile in Your Sleep.flac";

    [Fact]
    public void AFileInALiveAlbumsFolderIsNotTheStudioSong() =>
        Assert.True(SoulseekDownloadService.FromLiveFolder(LivePath, "Smile in Your Sleep", "Sad Songs Vol. 1"));

    [Fact]
    public void TheStudioAlbumsFolderIsFine() =>
        Assert.False(SoulseekDownloadService.FromLiveFolder(
            @"Music\Silverstein\Discovering the Waterfront (2005)\05 - Smile in Your Sleep.flac", "Smile in Your Sleep", null));

    [Theory]
    [InlineData(@"Music\Queen\Live at Wembley '86\CD1\01 - One Vision.flac")]
    [InlineData(@"Nirvana\MTV Unplugged in New York\01 - About a Girl.flac")]
    [InlineData(@"Shares\Pearl Jam - 2000-06-25 Katowice [bootleg]\03 - Corduroy.flac")]
    [InlineData(@"Music/Artist/Album (Live)/01 - Song.flac")]
    public void LiveAlbumFoldersAreRead(string path) =>
        Assert.True(SoulseekDownloadService.FromLiveFolder(path, "Song", "Some Studio Album"));

    [Fact]
    public void AShareCalledLiveAtTheTopIsNotTheAlbum() =>
        // Only the album folder and the one above it say what the file is.
        Assert.False(SoulseekDownloadService.FromLiveFolder(@"Live Music\Rock\Silverstein\Discovering the Waterfront\05 - Smile in Your Sleep.flac",
            "Smile in Your Sleep", "Discovering the Waterfront"));

    [Theory]
    [InlineData("Smile in Your Sleep", "Decade (live at the El Mocambo)")]
    [InlineData("Smile in Your Sleep (Live)", null)]
    [InlineData("Doll Parts", "Live Through This")]
    public void ALiveRequestTakesALiveFolder(string title, string? album) =>
        Assert.False(SoulseekDownloadService.FromLiveFolder(LivePath, title, album));

    private static AcoustIdRecording Recording(string id, params (string Group, string[] Types)[] albums) =>
        new(id, "Smile in Your Sleep", ["Silverstein"], albums.FirstOrDefault().Group, 2010)
        {
            Releases = albums.Select(album => new AcoustIdRelease(null, "rg-" + album.Group, album.Group, 2010, null, null, null,
                "Silverstein", false) { GroupTitle = album.Group, PrimaryType = "Album", SecondaryTypes = album.Types }).ToList(),
        };

    private static AcoustIdLookup Lookup(params AcoustIdRecording[] recordings) =>
        new(true, null, [new AcoustIdResult(1.0, recordings)]);

    private static readonly AcoustIdRecording LiveOnly = Recording("live", ("Decade (live at the El Mocambo)", ["Live"]));
    private static readonly AcoustIdRecording Studio = Recording("studio", ("Discovering the Waterfront", []), ("Decade (live at the El Mocambo)", ["Live"]));

    [Fact]
    public void ADownloadRefusesARecordingOnlyLiveAlbumsList()
    {
        var verdict = DownloadVerificationService.Decide(Lookup(LiveOnly), "Silverstein", "Smile in Your Sleep", 0.85,
            tagsAuthoritative: true, refuseLive: true);

        Assert.Equal(VerificationVerdict.Mismatch, verdict.Verdict);
        Assert.Equal("is a live recording, from 'Decade (live at the El Mocambo)'", verdict.DenyReason);
    }

    [Fact]
    public void ASongAlreadyInTheLibraryIsNeverRefusedForBeingLive() =>
        // The Review sweep and the tag preview ask without refuseLive: a live album may be yours on purpose.
        Assert.Equal(VerificationVerdict.Confirmed, DownloadVerificationService.Decide(Lookup(LiveOnly), "Silverstein",
            "Smile in Your Sleep", 0.85, tagsAuthoritative: true).Verdict);

    [Fact]
    public void AStudioRecordingThatIsAlsoOnALiveAlbumIsFine() =>
        Assert.Equal(VerificationVerdict.Confirmed, DownloadVerificationService.Decide(Lookup(Studio), "Silverstein",
            "Smile in Your Sleep", 0.85, tagsAuthoritative: true, refuseLive: true).Verdict);

    [Fact]
    public void TheSameAudioListedBothWaysConfirmsAsTheStudioRecording()
    {
        var verdict = DownloadVerificationService.Decide(Lookup(LiveOnly, Studio), "Silverstein", "Smile in Your Sleep", 0.85,
            tagsAuthoritative: true, refuseLive: true);

        Assert.Equal(VerificationVerdict.Confirmed, verdict.Verdict);
        Assert.Equal("studio", verdict.RecordingId);
    }

    [Fact]
    public void ARecordingWithNoAlbumsListedIsNotJudged() =>
        Assert.False(DownloadVerificationService.OnlyOnLiveAlbums(new AcoustIdRecording("x", "Song", ["A"], null, null)));
}
