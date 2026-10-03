namespace Octo.Models.Settings;

/// <summary>
/// Whether Octo looks for its own new releases, and where. Read through IOptionsMonitor at every
/// check, so a change applies without a restart.
/// </summary>
public class UpdateSettings
{
    /// <summary>
    /// Ask GitHub every 6 hours whether a newer Octo release is out, and say so on the dashboard
    /// (default: true). Off means Octo never contacts GitHub for this.
    /// Environment variable: UPDATES__CHECK
    /// </summary>
    public bool Check { get; set; } = true;

    /// <summary>
    /// The GitHub repository whose releases count, as "owner/name" (default: winters27/octo).
    /// Only a fork that publishes its own dated releases needs to change it.
    /// Environment variable: UPDATES__REPO
    /// </summary>
    public string Repo { get; set; } = "winters27/octo";
}
