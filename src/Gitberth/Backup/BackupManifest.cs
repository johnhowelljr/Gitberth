using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Gitberth.Backup;

/// <summary>How a saved container comes back.</summary>
public enum ComesBack
{
    /// <summary>Created again from its inspect (an image app, cloudflared, an app's Redis, anything made by hand).</summary>
    Inspect,

    /// <summary>Deployed again by Gitberth after the restart (a Git app's container, a Compose app's services).</summary>
    Deploy,

    /// <summary>A database server: created from its inspect first, started, and its dump loaded.</summary>
    Database,
}

/// <summary>
/// The first part of every backup (JSON): what it holds and how to bring it back. It's encrypted like every other part.
/// </summary>
public sealed class BackupManifest
{
    public int Format { get; set; } = BackupFormat.Version;
    public string GitberthVersion { get; set; } = "";

    /// <summary>The last migration of the saved gitberth.db: a Gitberth that doesn't know it is older, and refuses the file.</summary>
    public string? LastMigration { get; set; }

    public DateTime SavedAt { get; set; }
    public string Host { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string DockerVersion { get; set; } = "";
    public string DockerApi { get; set; } = "";

    /// <summary>The compressor and its level, as the owner asked every backup to say.</summary>
    public string Compression { get; set; } = BackupFormat.Compression;

    public string Encryption { get; set; } = "AES-256-GCM in 1 MiB chunks, key from the passphrase with PBKDF2-HMAC-SHA256";

    /// <summary>Every running container was stopped for the save (one consistent moment).</summary>
    public bool StoppedEverything { get; set; }

    /// <summary>What was saved, as the save list showed it.</summary>
    public List<BackupItem> Items { get; set; } = [];

    /// <summary>The apps Gitberth deploys again after the restart (Git and Compose apps).</summary>
    public List<SavedApp> Apps { get; set; } = [];

    public List<SavedContainer> Containers { get; set; } = [];
    public List<SavedVolume> Volumes { get; set; } = [];
    public List<SavedDatabase> Databases { get; set; } = [];
    public List<SavedImage> Images { get; set; } = [];
    public List<SavedNetwork> Networks { get; set; } = [];

    /// <summary>The host folders the saved containers bind-mount, with their files' parts.</summary>
    public List<SavedHostFolder> HostFolders { get; set; } = [];

    /// <summary>Gitberth's own data, by part name (data/gitberth.db, data/keys…).</summary>
    public List<string> DataParts { get; set; } = [];

    /// <summary>The restore's steps, in order.</summary>
    public List<PlannedStep> Plan { get; set; } = [];

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static BackupManifest Parse(byte[] json) =>
        JsonSerializer.Deserialize<BackupManifest>(json, Json) ?? throw new JsonException("The manifest is empty.");
}

/// <summary>An app deployed again after the restart.</summary>
/// <param name="Running">It was meant to run (a stopped one isn't deployed).</param>
public sealed record SavedApp(int Id, string Name, string Slug, string Source, bool Compose, bool FromRepo, string Branch, string? Commit, bool Running);

/// <summary>A saved container: its inspect (and its image's), and how it comes back.</summary>
public sealed class SavedContainer
{
    public string Name { get; set; } = "";
    public ComesBack How { get; set; }
    public JsonObject Inspect { get; set; } = [];
    public JsonObject? Image { get; set; }

    /// <summary>It ran when it was saved (or before "stop everything" stopped it): it's started again.</summary>
    public bool Running { get; set; }

    /// <summary>The save list's item it belongs to.</summary>
    public string Item { get; set; } = "";
}

/// <summary>A saved volume: how to make it again, and its files' part (none when a database's dump stands in for them).</summary>
public sealed class SavedVolume
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "local";
    public Dictionary<string, string>? Options { get; set; }
    public Dictionary<string, string>? Labels { get; set; }

    /// <summary>The containers that use it, by name.</summary>
    public List<string> Users { get; set; } = [];

    public long? Bytes { get; set; }

    /// <summary>Its files' part (volume/&lt;name&gt;); null when it's a database's data, which its dump brings back.</summary>
    public string? Part { get; set; }
}

/// <summary>A database server's dump.</summary>
/// <param name="Engine">postgres or mysql.</param>
/// <param name="Command">How it was dumped (no secret in it).</param>
public sealed record SavedDatabase(string Container, string Engine, string User, string Command, string Part, IReadOnlyList<string> DataVolumes);

/// <summary>An image the saved containers run: saved whole (only this host had it), or pulled again.</summary>
public sealed class SavedImage
{
    public string Id { get; set; } = "";
    public List<string> Tags { get; set; } = [];

    /// <summary>repo@sha256:… the registry gave it, when known: pulled by it, then tagged.</summary>
    public string? Digest { get; set; }

    /// <summary>Its part (image/&lt;id&gt;), when it's saved whole.</summary>
    public string? Part { get; set; }

    public long? Bytes { get; set; }
}

/// <summary>A host folder a saved container bind-mounts: its path on the old host, and its files' part.</summary>
public sealed class SavedHostFolder
{
    /// <summary>Its absolute path on the host (/srv/photos).</summary>
    public string Path { get; set; } = "";

    /// <summary>The containers that mount it, by name.</summary>
    public List<string> Users { get; set; } = [];

    public long? Bytes { get; set; }

    /// <summary>Its files' part (folder:&lt;path&gt;).</summary>
    public string? Part { get; set; }
}

/// <summary>A network the saved containers are on, as Docker described it.</summary>
public sealed class SavedNetwork
{
    public string Name { get; set; } = "";
    public JsonObject Inspect { get; set; } = [];
}

/// <summary>One step of the restore, as the plan names it.</summary>
public sealed record PlannedStep(string Id, string Title, int Phase);
