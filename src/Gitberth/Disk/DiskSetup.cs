namespace Gitberth.Disk;

/// <summary>Docker's disk: Program.cs's one line for this folder.</summary>
public static class DiskSetup
{
    /// <summary>
    /// The disk cleanup (build cache no build used for days, untagged images of old builds, leftover images of deleted
    /// apps) and the warnings before Docker's disk is full, with its background service. Program.cs calls it after every
    /// service it uses is registered.
    /// </summary>
    public static IServiceCollection AddDiskCare(this IServiceCollection services)
    {
        services.AddSingleton<DiskSettings>();
        // One instance, as a service too: Settings › Disk asks it for its report and its Clean up now.
        services.AddSingleton<DiskCleanup>();
        services.AddHostedService(sp => sp.GetRequiredService<DiskCleanup>());
        return services;
    }
}
