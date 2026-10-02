using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.Common;
using Octo.Services.Library;
using Octo.Services.Subsonic;

namespace Octo.Tests;

/// <summary>
/// #71 for library actions: a replacement asked for by rating carries the rater's own sign-in,
/// so whether they had favourited the song can be read before it is replaced. Nothing else reads.
/// </summary>
public sealed class LibraryActionStarTests
{
    private readonly ConcurrentQueue<(string Endpoint, Dictionary<string, string> Parameters)> _calls = new();

    private LibraryActionExecutor Executor(bool starred)
    {
        var stars = new StarOnArrival(new AcquisitionTracker(NullLogger<AcquisitionTracker>.Instance, null),
            scopes: null!, TestOptions.Monitor(new SubsonicSettings()), NullLogger<StarOnArrival>.Instance)
        {
            Call = (endpoint, parameters) =>
            {
                _calls.Enqueue((endpoint, parameters));
                var body = starred
                    ? """{"subsonic-response":{"status":"ok","song":{"id":"nd-1","starred":"2026-10-01T10:00:00Z"}}}"""
                    : """{"subsonic-response":{"status":"ok","song":{"id":"nd-1"}}}""";
                return Task.FromResult(Encoding.UTF8.GetBytes(body));
            },
        };
        return new LibraryActionExecutor(
            resolver: null!, quarantine: null!, journal: null!, library: null!, ids: null!,
            rejectedPeers: null!, acquisitions: null!,
            settings: TestOptions.Monitor(new LibraryActionSettings()),
            soulseek: TestOptions.Monitor(new SoulseekSettings()),
            subsonicSettings: TestOptions.Monitor(new SubsonicSettings()),
            logger: NullLogger<LibraryActionExecutor>.Instance,
            stars: stars);
    }

    private static SubsonicCredential Alice() => SubsonicCredential.From(new Dictionary<string, string>
    {
        ["u"] = "alice", ["t"] = "token", ["s"] = "salt",
    })!;

    [Fact]
    public void RatingCarriesTheRatersSignIn()
    {
        var request = LibraryActionRatingWorker.ToActionRequest(new RatingActionRequest(
            LibraryAction.BetterQuality, "nd-1", "alice", "alice", "token", "salt"));

        Assert.Equal(LibraryAction.BetterQuality, request.Action);
        Assert.Equal("nd-1", request.NavidromeId);
        Assert.Equal("alice", request.Username);
        Assert.NotNull(request.Credential);
        Assert.Equal("alice", request.Credential!.User);
        var parameters = request.Credential.Parameters();
        Assert.Equal("token", parameters["t"]);
        Assert.Equal("salt", parameters["s"]);
    }

    [Fact]
    public async Task ReplacementOfAFavourite_IsMarkedToCarry()
    {
        var executor = Executor(starred: true);

        var carry = await executor.WasStarredByRequesterAsync(
            new LibraryActionRequest(LibraryAction.BetterQuality, "nd-1", "alice", Alice()));

        Assert.True(carry);
        var call = Assert.Single(_calls);
        Assert.Equal("rest/getSong", call.Endpoint);
        Assert.Equal("nd-1", call.Parameters["id"]);
        Assert.Equal("alice", call.Parameters["u"]);
    }

    [Fact]
    public async Task DeleteNeverReads()
    {
        var executor = Executor(starred: true);

        var carry = await executor.WasStarredByRequesterAsync(
            new LibraryActionRequest(LibraryAction.Delete, "nd-1", "alice", Alice()));

        Assert.False(carry);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task PlaylistActionWithoutSignIn_DoesNotCarry()
    {
        var executor = Executor(starred: true);

        var carry = await executor.WasStarredByRequesterAsync(
            new LibraryActionRequest(LibraryAction.BetterQuality, "nd-1", "alice"));

        Assert.False(carry);
        Assert.Empty(_calls);
    }
}
