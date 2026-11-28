namespace Gitberth.Keeper;

/// <summary>Keeping apps running: Program.cs's one line for this folder.</summary>
public static class KeeperSetup
{
    /// <summary>
    /// The keep-running check (apps that are meant to run but stopped by themselves are started again), its switch, and
    /// its background service. Program.cs calls it after every service it uses is registered, so its hosted service starts
    /// after the event watcher's and the start-up tasks'.
    /// </summary>
    public static IServiceCollection AddAppKeeper(this IServiceCollection services)
    {
        services.AddSingleton<KeeperSettings>();
        // One instance, as a service too: pages ask it what it says of an app, and the tests make its passes by hand.
        services.AddSingleton<AppKeeper>();
        services.AddHostedService(sp => sp.GetRequiredService<AppKeeper>());
        // Apps Docker says are unhealthy, restarted (at most 3 times in an hour); its switch is in HealthRules.
        services.AddSingleton<UnhealthyRestarts>();
        services.AddHostedService(sp => sp.GetRequiredService<UnhealthyRestarts>());
        return services;
    }
}
