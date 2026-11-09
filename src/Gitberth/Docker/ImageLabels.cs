namespace Gitberth.Docker;

/// <summary>
/// Labels Gitberth puts on the images it builds. What a build found out travels with its image, so a rollback or a
/// settings change that reuses an image gets what that image was built with.
/// </summary>
public static class ImageLabels
{
    /// <summary>The container port the image was built to be published on.</summary>
    public const string Port = "gitberth.port";

    /// <summary>The cron jobs from the app's vercel.json, as JSON: [{"path":"/api/x","schedule":"0 6 * * *"}].</summary>
    public const string Crons = "gitberth.crons";

    /// <summary>The prefix the app's framework gives Vercel's variables, like NEXT_PUBLIC_.</summary>
    public const string EnvPrefix = "gitberth.env-prefix";

    /// <summary>"true" when the app signs people in with Auth.js (next-auth), which needs to know its public address.</summary>
    public const string AuthJs = "gitberth.authjs";

    /// <summary>"true" when the app's code checks process.env.VERCEL itself.</summary>
    public const string ChecksVercel = "gitberth.checks-vercel";

    /// <summary>On every image Gitberth builds now: an image without it was built before Gitberth kept what a build finds out.</summary>
    public const string Traits = "gitberth.traits";
}
