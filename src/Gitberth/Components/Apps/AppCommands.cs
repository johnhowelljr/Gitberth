using Gitberth.Apps;
using Gitberth.Services;
using MudBlazor;

namespace Gitberth.Components.Apps;

/// <summary>
/// Start, stop and restart for any app, by key, the same from every page: each says first what it would do to the rest
/// of the host (the domains cloudflared serves, the apps that use a database), and what Gitberth won't do (stop itself)
/// is refused with the reason.
/// </summary>
public static class AppCommands
{
    /// <summary>Why the action can't be done here, or null.</summary>
    public static string? Refusal(AppSummary app, ContainerAction action) => (app.Role, action) switch
    {
        (ContainerRole.Self, ContainerAction.Stop or ContainerAction.Remove) => "Gitberth can't stop or remove its own container. Use docker compose on the host for that.",
        _ when app.State is AppState.Deploying or AppState.Changing => $"{app.Title} is being changed. Try again when that's done.",
        _ => null,
    };

    public static string Verb(ContainerAction action) => action switch
    {
        ContainerAction.Start => "Start",
        ContainerAction.Stop => "Stop",
        ContainerAction.Restart => "Restart",
        _ => "Remove",
    };

    /// <summary>Asks first when it affects anything else, runs it, and says how it went. True when it ran.</summary>
    public static async Task<bool> RunAsync(AppSummary app, ContainerAction action, AppActions actions, ContainerService containers,
        IDialogService dialogs, ISnackbar snackbar)
    {
        if (Refusal(app, action) is { } refusal)
        {
            snackbar.Add(refusal, Severity.Warning);
            return false;
        }
        try
        {
            if (action != ContainerAction.Start)
            {
                var row = (await containers.ListAsync()).Rows.FirstOrDefault(r => r.Container.Name == app.Container);
                var warnings = row is null ? [] : await containers.WarningsAsync(row, action);
                if (action == ContainerAction.Stop || warnings.Count > 0)
                {
                    var message = warnings.Count == 0
                        ? $"{app.Title} stops until you start it again." + (app.Source == AppSource.Git ? " New commits aren't deployed while it's stopped." : "")
                        : string.Join(" ", warnings);
                    var verb = Verb(action);
                    if (await dialogs.ShowMessageBoxAsync($"{verb} {app.Title}?", message, yesText: verb, cancelText: "Cancel") != true) return false;
                }
            }
            var error = action switch
            {
                ContainerAction.Start => await actions.StartAsync(app.Key),
                ContainerAction.Stop => await actions.StopAsync(app.Key),
                _ => await actions.RestartAsync(app.Key),
            };
            var done = action switch { ContainerAction.Start => "Started", ContainerAction.Stop => "Stopped", _ => "Restarted" };
            snackbar.Add(error ?? $"{done} {app.Title}.", error is null ? Severity.Success : Severity.Error);
            return error is null;
        }
        catch (Exception ex)
        {
            snackbar.Add(ex.Message, Severity.Error);
            return false;
        }
    }
}
