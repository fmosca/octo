using Microsoft.AspNetCore.Mvc;
using Octo.Services.Admin;
using Octo.Services.CoverArt;
using Octo.Services.Subsonic;

namespace Octo.Controllers;

/// <summary>
/// The dashboard's "Upgrade cover art": find the largest cover for every album and embed it.
/// It rewrites the owner's files, so it needs a Navidrome admin sign-in like the genre backfill,
/// and a whole-library run (not a preview) needs the music path typed back.
/// </summary>
[ApiController]
[Route("api/admin/covers/upgrade")]
public sealed class CoverUpgradeController : ControllerBase
{
    private readonly BrowseSessionStore _sessions;
    private readonly CoverUpgradeWorker _worker;
    private readonly NavidromeIdentityService _identity;
    private readonly IConfiguration _config;
    private readonly ILogger<CoverUpgradeController> _logger;

    public CoverUpgradeController(BrowseSessionStore sessions, CoverUpgradeWorker worker,
        NavidromeIdentityService identity, IConfiguration config, ILogger<CoverUpgradeController> logger)
    {
        _sessions = sessions;
        _worker = worker;
        _identity = identity;
        _config = config;
        _logger = logger;
    }

    private bool Signed(string? header) =>
        _sessions.Validate(Request.Cookies[AdminController.BrowseCookieName] ?? header);

    private IActionResult SignIn() => Unauthorized(new { error = "Sign in with your Navidrome admin account first." });

    private string MusicPath()
    {
        var fallback = _config["Library:DownloadPath"] ?? "/music";
        return _identity.EffectiveDownloadPath(fallback);
    }

    public sealed record StartRequest(string? Scope, bool DryRun, bool FolderCovers = true, string? Confirm = null);

    [HttpGet]
    public IActionResult Get([FromHeader(Name = "X-Octo-Browse-Token")] string? token)
    {
        if (!Signed(token)) return SignIn();
        var run = _worker.Current;
        return Ok(new
        {
            run.RunId,
            status = run.Status.ToString(),
            scope = run.Scope.ToString(),
            run.DryRun,
            run.FolderCovers,
            run.FullSize,
            run.Undo,
            run.StartedUtc,
            run.FinishedUtc,
            run.Total,
            run.Processed,
            run.Upgraded,
            run.Kept,
            run.Files,
            run.Failed,
            run.LastFolder,
            run.Reason,
            run.Errors,
            run.Preview,
            canResume = run.CanResume,
            canUndo = _worker.CanUndo,
            musicPath = MusicPath(),
        });
    }

    [HttpPost]
    public IActionResult Start([FromBody] StartRequest request, [FromHeader(Name = "X-Octo-Browse-Token")] string? token)
    {
        if (!Signed(token)) return SignIn();
        if (!Enum.TryParse<CoverUpgradeScope>(request.Scope, ignoreCase: true, out var scope))
            scope = CoverUpgradeScope.OctoDownloads;

        var root = MusicPath();
        if (scope == CoverUpgradeScope.WholeLibrary && !request.DryRun
            && !string.Equals(request.Confirm?.Trim(), root, StringComparison.Ordinal))
            return BadRequest(new { error = $"To rewrite the whole library, type the music path exactly: {root}" });

        if (!_worker.TryEnqueue(new CoverUpgradeRequest(scope, request.DryRun, request.FolderCovers)))
            return Conflict(new { error = "A cover upgrade is already running." });
        _logger.LogInformation("Cover upgrade requested: scope {Scope}, dryRun {DryRun}, folder covers {Folder}",
            scope, request.DryRun, request.FolderCovers);
        return Accepted(new { started = true });
    }

    [HttpPost("cancel")]
    public IActionResult Cancel([FromHeader(Name = "X-Octo-Browse-Token")] string? token)
    {
        if (!Signed(token)) return SignIn();
        _worker.RequestCancel();
        return Accepted(new { cancelling = true });
    }

    [HttpPost("resume")]
    public IActionResult Resume([FromHeader(Name = "X-Octo-Browse-Token")] string? token)
    {
        if (!Signed(token)) return SignIn();
        var run = _worker.Current;
        if (!run.CanResume) return BadRequest(new { error = "There is nothing to resume." });
        if (!_worker.TryEnqueue(new CoverUpgradeRequest(run.Scope, run.DryRun, run.FolderCovers)))
            return Conflict(new { error = "A cover upgrade is already running." });
        return Accepted(new { resumed = true });
    }

    [HttpPost("undo")]
    public IActionResult Undo([FromHeader(Name = "X-Octo-Browse-Token")] string? token)
    {
        if (!Signed(token)) return SignIn();
        if (!_worker.CanUndo) return BadRequest(new { error = "There is no cover upgrade to undo." });
        if (!_worker.TryEnqueue(new CoverUpgradeRequest(CoverUpgradeScope.WholeLibrary, DryRun: false, FolderCovers: true, Undo: true)))
            return Conflict(new { error = "A cover upgrade is already running." });
        _logger.LogInformation("Cover upgrade undo requested");
        return Accepted(new { started = true });
    }
}
