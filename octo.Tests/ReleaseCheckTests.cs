using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Octo.Models.Settings;
using Octo.Services.Updates;

namespace Octo.Tests;

/// <summary>
/// Octo telling its owner a newer release is out. Only dated server releases count: the repo also
/// publishes the apps' releases, drafts and prereleases. A failed check keeps the last good answer.
/// </summary>
public sealed class ReleaseCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "octo-release-check-" + Guid.NewGuid());
    private readonly GitHub _github = new();
    private DateTime _now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private string CachePath => Path.Combine(_dir, "update", "release.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    internal const string Releases = """
        [
          { "tag_name": "desktop-v1.3.2", "name": "Octo for Windows and Linux 1.3.2", "draft": false, "prerelease": false, "body": "app", "html_url": "https://github.com/winters27/octo/releases/tag/desktop-v1.3.2", "published_at": "2026-10-03T10:42:57Z" },
          { "tag_name": "2026.10.05", "name": "2026.10.05", "draft": true, "prerelease": false, "body": "draft", "html_url": "", "published_at": null },
          { "tag_name": "2026.10.04", "name": "2026.10.04", "draft": false, "prerelease": true, "body": "pre", "html_url": "", "published_at": "2026-10-04T10:00:00Z" },
          { "tag_name": "android-v1.3.0", "name": "Octo for Android 1.3.0", "draft": false, "prerelease": false, "body": "app", "html_url": "", "published_at": "2026-10-03T11:20:07Z" },
          { "tag_name": "2026.10.02.1", "name": "", "draft": false, "prerelease": false, "body": "### Fix\n- one", "html_url": "https://github.com/winters27/octo/releases/tag/2026.10.02.1", "published_at": "2026-10-02T22:00:00Z" },
          { "tag_name": "2026.10.02", "name": "2026.10.02", "draft": false, "prerelease": false, "body": "two", "html_url": "https://github.com/winters27/octo/releases/tag/2026.10.02", "published_at": "2026-10-02T20:00:00Z" },
          { "tag_name": "2026.10.01", "name": "2026.10.01", "draft": false, "prerelease": false, "body": "one", "html_url": "https://github.com/winters27/octo/releases/tag/2026.10.01", "published_at": "2026-10-01T21:33:35Z" }
        ]
        """;

    private ReleaseCheck Check(string running, UpdateSettings? settings = null) =>
        new(CachePath, new ReviewFixtures.OneClientFactory(_github), TestOptions.Monitor(settings ?? new UpdateSettings()),
            NullLogger<ReleaseCheck>.Instance, () => _now, running);

    [Theory]
    [InlineData("2026.10.02", true)]
    [InlineData("2026.10.02.1", true)]
    [InlineData("v2026.10.02", true)]
    [InlineData("2026.10.02+ac471ba9406dffe65fe784a94bea5d863622f145", true)]
    [InlineData("desktop-v1.3.2", false)]
    [InlineData("android-v1.3.0", false)]
    [InlineData("1.0.0", false)]
    [InlineData("", false)]
    public void OnlyDatedReleasesParse(string text, bool parses) =>
        Assert.Equal(parses, ReleaseVersion.TryParse(text, out _));

    [Theory]
    [InlineData("2026.10.02.1", "2026.10.02")]
    [InlineData("2026.10.10", "2026.10.09")]
    [InlineData("2026.11.01", "2026.10.31")]
    [InlineData("2027.01.01", "2026.12.31.9")]
    public void ReleasesCompareByTheirNumbers(string newer, string older)
    {
        Assert.True(ReleaseVersion.TryParse(newer, out var a));
        Assert.True(ReleaseVersion.TryParse(older, out var b));
        Assert.True(a > b);
        Assert.True(b < a);
    }

    [Fact]
    public async Task AnOlderServerIsToldWhichReleasesAreNewer()
    {
        _github.Answer(HttpStatusCode.OK, Releases, etag: "\"abc\"");

        var view = await Check("2026.10.01").CheckAsync(manual: false, CancellationToken.None);

        Assert.True(view.UpdateAvailable);
        Assert.Equal("behind", view.Standing);
        Assert.Equal("2026.10.02.1", view.Latest!.Tag);
        // A release without a name is named for its tag.
        Assert.Equal("2026.10.02.1", view.Latest.Name);
        Assert.Equal(["2026.10.02.1", "2026.10.02"], view.Newer.Select(r => r.Tag));
        Assert.Null(view.Error);
        Assert.Equal(_now, view.CheckedUtc);
    }

    [Fact]
    public async Task AppsDraftsAndPrereleasesNeverCount()
    {
        _github.Answer(HttpStatusCode.OK, Releases);

        var view = await Check("2026.10.02.1").CheckAsync(manual: false, CancellationToken.None);

        Assert.False(view.UpdateAvailable);
        Assert.Equal("current", view.Standing);
        Assert.Empty(view.Newer);
    }

    [Fact]
    public async Task ABuildCutAfterTheNewestReleaseIsAhead()
    {
        _github.Answer(HttpStatusCode.OK, Releases);

        var view = await Check("2026.10.03").CheckAsync(manual: false, CancellationToken.None);

        Assert.False(view.UpdateAvailable);
        Assert.Equal("ahead", view.Standing);
    }

    [Fact]
    public async Task ABuildThatNamesNoReleaseIsNeverToldItIsBehind()
    {
        _github.Answer(HttpStatusCode.OK, Releases);

        var view = await Check("1.0.0").CheckAsync(manual: false, CancellationToken.None);

        Assert.False(view.UpdateAvailable);
        Assert.Equal("unknown", view.Standing);
        Assert.Equal("2026.10.02.1", view.Latest!.Tag);
    }

    [Fact]
    public async Task ItAsksTheReleaseListWithItsOwnName()
    {
        _github.Answer(HttpStatusCode.OK, Releases);

        await Check("2026.10.01").CheckAsync(manual: false, CancellationToken.None);

        var request = Assert.Single(_github.Requests);
        Assert.Equal("https://api.github.com/repos/winters27/octo/releases?per_page=30", request.Uri);
        Assert.StartsWith("Octo/2026.10.01", request.UserAgent);
    }

    [Fact]
    public async Task AnUnchangedListCostsNothingAndKeepsTheReleases()
    {
        _github.Answer(HttpStatusCode.OK, Releases, etag: "\"abc\"");
        var check = Check("2026.10.01");
        await check.CheckAsync(manual: false, CancellationToken.None);

        _now = _now.AddHours(7);
        _github.Answer(HttpStatusCode.NotModified, "");
        var view = await check.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal("\"abc\"", _github.Requests[1].IfNoneMatch);
        Assert.Equal("2026.10.02.1", view.Latest!.Tag);
        Assert.Equal(_now, view.CheckedUtc);
    }

    [Fact]
    public async Task GitHubsLimitKeepsTheLastAnswerAndSaysWhy()
    {
        _github.Answer(HttpStatusCode.OK, Releases);
        var check = Check("2026.10.01");
        await check.CheckAsync(manual: false, CancellationToken.None);
        var checkedAt = _now;

        _now = _now.AddHours(7);
        _github.Answer(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""", rateLimitLeft: "0");
        var view = await check.CheckAsync(manual: false, CancellationToken.None);

        Assert.Contains("limit", view.Error);
        Assert.Equal("2026.10.02.1", view.Latest!.Tag);
        Assert.True(view.UpdateAvailable);
        Assert.Equal(checkedAt, view.CheckedUtc);
    }

    [Fact]
    public async Task NoNetworkKeepsTheLastAnswerAndSaysWhy()
    {
        _github.Answer(HttpStatusCode.OK, Releases);
        var check = Check("2026.10.01");
        await check.CheckAsync(manual: false, CancellationToken.None);

        _now = _now.AddHours(7);
        _github.Throw();
        var view = await check.CheckAsync(manual: false, CancellationToken.None);

        Assert.Equal("Couldn't reach GitHub to look for a new release.", view.Error);
        Assert.Equal("2026.10.02.1", view.Latest!.Tag);
    }

    [Fact]
    public async Task ARestartAnswersFromTheSavedCheck()
    {
        _github.Answer(HttpStatusCode.OK, Releases);
        await Check("2026.10.01").CheckAsync(manual: false, CancellationToken.None);

        var view = Check("2026.10.01").View();

        Assert.Single(_github.Requests);
        Assert.True(view.UpdateAvailable);
        Assert.Equal("2026.10.02.1", view.Latest!.Tag);
    }

    [Fact]
    public async Task ChecksOffMeansGitHubIsNeverAsked()
    {
        var view = await Check("2026.10.01", new UpdateSettings { Check = false }).CheckAsync(manual: true, CancellationToken.None);

        Assert.Empty(_github.Requests);
        Assert.False(view.Enabled);
        Assert.False(view.UpdateAvailable);
    }

    [Fact]
    public async Task CheckNowIsHonouredOnceAMinute()
    {
        _github.Answer(HttpStatusCode.OK, Releases);
        var check = Check("2026.10.01");

        await check.CheckAsync(manual: true, CancellationToken.None);
        await check.CheckAsync(manual: true, CancellationToken.None);
        Assert.Single(_github.Requests);

        _now = _now.AddMinutes(1);
        await check.CheckAsync(manual: true, CancellationToken.None);
        Assert.Equal(2, _github.Requests.Count);
    }

    [Fact]
    public async Task AMalformedRepoIsRefusedWithoutAsking()
    {
        var view = await Check("2026.10.01", new UpdateSettings { Repo = "not a repo" }).CheckAsync(manual: false, CancellationToken.None);

        Assert.Empty(_github.Requests);
        Assert.Contains("owner/name", view.Error);
    }

    [Fact]
    public async Task AnAnswerAboutAnotherRepoIsNotShown()
    {
        _github.Answer(HttpStatusCode.OK, Releases);
        await Check("2026.10.01").CheckAsync(manual: false, CancellationToken.None);

        var view = Check("2026.10.01", new UpdateSettings { Repo = "someone/fork" }).View();

        Assert.Null(view.Latest);
        Assert.False(view.UpdateAvailable);
    }

    [Fact]
    public async Task AnswerThatIsNotAListIsAnError()
    {
        _github.Answer(HttpStatusCode.OK, """{"message":"odd"}""");

        var view = await Check("2026.10.01").CheckAsync(manual: false, CancellationToken.None);

        Assert.NotNull(view.Error);
        Assert.Null(view.Latest);
    }

    internal sealed class GitHub : HttpMessageHandler
    {
        private Func<HttpResponseMessage> _answer = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<(string Uri, string UserAgent, string? IfNoneMatch)> Requests { get; } = [];

        public void Answer(HttpStatusCode status, string body, string? etag = null, string? rateLimitLeft = null) =>
            _answer = () =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (etag is not null) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
                if (rateLimitLeft is not null) response.Headers.Add("x-ratelimit-remaining", rateLimitLeft);
                return response;
            };

        public void Throw() => _answer = () => throw new HttpRequestException("no network");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.UserAgent.ToString(),
                request.Headers.IfNoneMatch.FirstOrDefault()?.ToString()));
            return Task.FromResult(_answer());
        }
    }
}
