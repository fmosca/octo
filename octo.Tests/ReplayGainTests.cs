using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Octo.Models.Domain;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Soulseek;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// Volume normalisation is client-side and reads OpenSubsonic's replayGain, so what Octo
/// puts in that object is the whole of its influence on loudness. A library song used to be
/// rebuilt without it in the radio and Discovery paths, and an outside song left with an
/// empty one however loud the video behind it was — which is why a queue of normalised
/// library tracks and raw YouTube previews could not be levelled.
/// </summary>
public sealed class ReplayGainTests
{
    private static SubsonicResponseBuilder Builder(ExternalIdRegistry registry) =>
        new(registry, Options.Create(new SubsonicSettings()));

    private static Song Song(bool local) => new()
    {
        Id = "mXFKjv7oqx1HTOzoJP1nk3", Title = "One Woman (Album Version)", Artist = "Randy Rogers Band",
        Album = "Randy Rogers Band", Duration = 245, IsLocal = local,
    };

    private static Dictionary<string, object> Gain(Song song, ExternalIdRegistry registry) =>
        Assert.IsType<Dictionary<string, object>>(Builder(registry).ConvertSongToJson(song)["replayGain"]);

    [Fact]
    public void OutsideSong_CarriesTheGainMeasuredOnTheVideoThatPlays()
    {
        var registry = new ExternalIdRegistry();
        var id = registry.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Song, Artist = "Randy Rogers Band",
            Title = "One Woman (Album Version)", YouTubeId = "vid123",
        });
        Assert.True(registry.RememberGain(id, -7.5, -0.5));

        var song = Song(local: false);
        song.Id = id;
        var gain = Gain(song, registry);

        Assert.Equal(-7.5, gain["trackGain"]);
        // Linear, the shape Navidrome serves a peak in: -0.5 dBFS is 0.944061.
        Assert.Equal(0.944061, gain["trackPeak"]);
    }

    [Fact]
    public void OutsideSong_NotMeasuredYet_StillAnswersWithAnObject()
    {
        // The shim measures the first time it sees a video, so the first resolve of a
        // track legitimately has nothing. An empty object is what Navidrome answers with
        // for an untagged file, and clients already handle that shape.
        Assert.Empty(Gain(Song(local: false), new ExternalIdRegistry()));
    }

    [Fact]
    public void LibrarySong_CarriesTheGainsNavidromeHolds()
    {
        var song = Song(local: true);
        song.TrackGain = -9.47;
        song.AlbumGain = -9.13;
        song.TrackPeak = 0.983337;
        song.AlbumPeak = 0.989014;

        var gain = Gain(song, new ExternalIdRegistry());

        Assert.Equal(-9.47, gain["trackGain"]);
        Assert.Equal(-9.13, gain["albumGain"]);
        Assert.Equal(0.983337, gain["trackPeak"]);
        Assert.Equal(0.989014, gain["albumPeak"]);
    }

    [Fact]
    public void RememberingNoGain_LeavesAnExistingOneAlone()
    {
        // Every resolve of an unmeasured video answers with no gain, and those resolves
        // are frequent (getSong asks on every request). Storing the absence would wipe a
        // measurement that had already landed.
        var registry = new ExternalIdRegistry();
        var id = registry.Register(new SoulseekRouting
        {
            Kind = RoutingKind.Song, Artist = "A", Title = "T", YouTubeId = "vid123",
        });
        Assert.True(registry.RememberGain(id, -7.5, -0.5));

        Assert.False(registry.RememberGain(id, null, null));
        var song = Song(local: false);
        song.Id = id;
        Assert.Equal(-7.5, Gain(song, registry)["trackGain"]);
    }

    [Fact]
    public void ReplayGain_IsReadFromNavidromeJsonInBothShapes()
    {
        var element = JsonDocument.Parse(
            """{"replayGain":{"trackGain":-9.47,"albumGain":-9.13,"trackPeak":0.98,"albumPeak":0.99}}""").RootElement;
        var fromElement = OpenSubsonicJson.ReplayGain(element);
        Assert.Equal(-9.47, fromElement.TrackGain);
        Assert.Equal(-9.13, fromElement.AlbumGain);
        Assert.Equal(0.98, fromElement.TrackPeak);
        Assert.Equal(0.99, fromElement.AlbumPeak);

        var node = JsonNode.Parse("""{"replayGain":{"trackGain":-1.5}}""")!.AsObject();
        var fromNode = OpenSubsonicJson.ReplayGain(node);
        Assert.Equal(-1.5, fromNode.TrackGain);
        Assert.Null(fromNode.AlbumGain);
        Assert.Null(fromNode.TrackPeak);

        // No replayGain object at all: a file without ReplayGain tags.
        var absent = OpenSubsonicJson.ReplayGain(JsonDocument.Parse("""{"title":"x"}""").RootElement);
        Assert.Null(absent.TrackGain);
        Assert.Null(OpenSubsonicJson.ReplayGain(JsonNode.Parse("""{"title":"x"}""")!.AsObject()).TrackGain);
    }
}
