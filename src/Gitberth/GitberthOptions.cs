using Gitberth.Network;

namespace Gitberth;

/// <summary>Process-level settings that come from environment variables (not editable in the UI).</summary>
public sealed class GitberthOptions
{
    private readonly string? _ownContainer;

    public required string DataDir { get; init; }
    public string DockerPath { get; init; } = "docker";
    public string GitPath { get; init; } = "git";
    public string? PublicHost { get; init; }
    public string? InitialAdminPassword { get; init; }
    public bool RunningInContainer { get; init; }
    public string Network { get; init; } = "gitberth";

    /// <summary>
    /// Gitberth's own container as Docker knows it, when it runs in one: its ID, read from the container's own mounts
    /// (<see cref="HostScanner.OwnIdFromMounts"/>), else its hostname, which is the container's short ID unless it was
    /// started with another (docker run --hostname). Null outside a container. Tests give their own.
    /// </summary>
    public string? OwnContainer
    {
        get => _ownContainer ?? (RunningInContainer ? HostScanner.OwnIdFromMounts() ?? Environment.MachineName : null);
        init => _ownContainer = value;
    }

    /// <summary>The engine address, read the same way the docker CLI reads it.</summary>
    public string DockerHost { get; init; } = "unix:///var/run/docker.sock";

    public string DbPath => Path.Combine(DataDir, "gitberth.db");
    public string KeysDir => Path.Combine(DataDir, "keys");
    public string LogsDir => Path.Combine(DataDir, "logs");
    public string WorkDir => Path.Combine(DataDir, "work");

    public static GitberthOptions FromConfiguration(IConfiguration config, string contentRoot)
    {
        var inContainer = string.Equals(config["DOTNET_RUNNING_IN_CONTAINER"], "true", StringComparison.OrdinalIgnoreCase);
        // Blank counts as unset, as it does for the other settings.
        var dataDir = NullIfBlank(config["GITBERTH_DATA"]) ?? (inContainer ? "/data" : Path.Combine(contentRoot, "App_Data"));
        return new GitberthOptions
        {
            DataDir = Path.GetFullPath(dataDir),
            DockerPath = NullIfBlank(config["GITBERTH_DOCKER"]) ?? "docker",
            GitPath = NullIfBlank(config["GITBERTH_GIT"]) ?? "git",
            PublicHost = NullIfBlank(config["GITBERTH_PUBLIC_HOST"]),
            InitialAdminPassword = NullIfBlank(config["GITBERTH_ADMIN_PASSWORD"]),
            RunningInContainer = inContainer,
            Network = NullIfBlank(config["GITBERTH_NETWORK"]) ?? "gitberth",
            DockerHost = NullIfBlank(config["DOCKER_HOST"]) ?? "unix:///var/run/docker.sock",
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
