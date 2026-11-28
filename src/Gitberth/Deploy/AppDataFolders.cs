using System.Globalization;

namespace Gitberth.Deploy;

/// <summary>
/// The folders under Gitberth's data folder that belong to one app, by its ID: its kept uploads (/data/uploads/&lt;id&gt;) and
/// its config files (/data/configs/&lt;id&gt;). Both go in a backup with the rest of /data, and both go with the app.
/// </summary>
public static class AppDataFolders
{
    public static string UploadsRoot(GitberthOptions options) => Path.Combine(options.DataDir, "uploads");

    public static string ConfigsRoot(GitberthOptions options) => Path.Combine(options.DataDir, "configs");

    public static string Uploads(GitberthOptions options, int appId) => Path.Combine(UploadsRoot(options), appId.ToString(CultureInfo.InvariantCulture));

    public static string Configs(GitberthOptions options, int appId) => Path.Combine(ConfigsRoot(options), appId.ToString(CultureInfo.InvariantCulture));

    /// <summary>The app's folders go with its row.</summary>
    public static void Remove(GitberthOptions options, int appId)
    {
        DeployPaths.TryDeleteDirectory(Uploads(options, appId));
        DeployPaths.TryDeleteDirectory(Configs(options, appId));
    }
}
