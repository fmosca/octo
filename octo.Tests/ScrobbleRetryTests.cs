namespace Octo.Tests;

/// <summary>
/// A finished play sent twice is learned from once, but only once something did learn from it.
/// When the song could not be looked up the first time, the client's retry is the play.
/// </summary>
[Trait("Host", "Boot")]
public sealed class ScrobbleRetryTests
{
    [Fact]
    public async Task APlayNotTakenTheFirstTime_CountsWhenTheClientSendsItAgain()
    {
        await using var fixture = new RadioWebFactory();
        using var client = fixture.CreateClient();
        // Navidrome cannot say what the library song is for a moment.
        fixture.Handler.GetSongFailures = 1;
        var at = DateTimeOffset.UtcNow.AddMinutes(-4).ToUnixTimeMilliseconds();
        var url = $"/rest/scrobble?u=bob&t=token&s=salt&f=json&id=local-song&submission=true&time={at}";

        await client.GetStringAsync(url);
        Assert.Empty(fixture.State.GetUser("bob").Plays);

        await client.GetStringAsync(url);
        await client.GetStringAsync(url);

        Assert.Single(fixture.State.GetUser("bob").Plays);
    }
}
