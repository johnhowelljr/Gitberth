using Gitberth.Data;

namespace Gitberth.Templates;

/// <summary>What an input of a template takes: what its form field is, and what its value must look like.</summary>
public enum TemplateInputKind { Text, Email, Url, Number, Bool }

/// <summary>
/// A value the person installing fills in (an admin's name, a time zone): one of the app's variables afterwards, which its
/// Environment tab changes.
/// </summary>
/// <param name="Name">The variable it fills (TZ): the Compose file uses it as ${TZ}.</param>
/// <param name="Default">What the field starts with; null for nothing.</param>
public sealed record TemplateInput(string Name, string Label, string? Help, string? Default, bool Required, TemplateInputKind Kind);

/// <summary>Which characters a generated secret is made of.</summary>
public enum SecretAlphabet { Alphanumeric, Hex }

/// <summary>A secret Gitberth makes at install (an encryption key, an admin password): a secret variable of the app's.</summary>
public sealed record TemplateSecret(string Name, int Length, SecretAlphabet Alphabet);

/// <summary>
/// A database the app gets from Gitberth's databases (Settings › Databases): a Postgres database on a server, or a Redis of
/// its own. Its connection string goes in <paramref name="Variable"/>, and in parts in Variable_HOST, _PORT, _NAME, _USER
/// and _PASSWORD for an app that wants them one by one.
/// </summary>
public sealed record TemplateDatabase(DatabaseKind Kind, string Variable);

/// <summary>The template's web service: the container its LAN address, domains and health checks go to.</summary>
/// <param name="Health">The path that answers once it's up (2xx or 3xx): its health URL.</param>
public sealed record TemplateWeb(string Service, int Port, string Health);

/// <summary>
/// One app of the template catalog (Templates/catalog/&lt;id&gt;.yaml): what it is, what it needs, and the Compose file it
/// runs, with ${…} for its inputs, generated secrets, databases and <see cref="TemplateCatalog.BuiltIns"/>.
/// </summary>
/// <param name="Id">Its file's name: uptime-kuma.</param>
/// <param name="Category">The gallery's chip it's under.</param>
/// <param name="Source">Its source code's home.</param>
/// <param name="Icon">Its icon's file next to it (uptime-kuma.svg).</param>
/// <param name="IconSource">Where its icon came from: the project's own repo, or "monogram" for one Gitberth drew.</param>
/// <param name="MemoryMb">What it needs at least, in MB, as the template page says.</param>
/// <param name="LatestOnly">Why an image runs its latest tag (the project publishes nothing else); null when every image is pinned.</param>
/// <param name="SharedNetwork">Every service on Gitberth's shared network, not only the web one (a worker that reaches the database).</param>
/// <param name="Notes">First-login facts, shown once it's installed: "The first account you make is the admin."</param>
public sealed record AppTemplate(string Id, string Name, string Tagline, string Description, string Category, string Website, string Source,
    string License, string Icon, string IconSource, int MemoryMb, TemplateWeb Web, IReadOnlyList<TemplateInput> Inputs,
    IReadOnlyList<TemplateSecret> Secrets, IReadOnlyList<TemplateDatabase> Databases, IReadOnlyList<string> Notes, string Compose,
    string? LatestOnly = null, bool SharedNetwork = false)
{
    /// <summary>It needs a Postgres server (Settings › Databases) to get its database from.</summary>
    public bool NeedsPostgres => Databases.Any(d => d.Kind == DatabaseKind.Postgres);

    /// <summary>The images its services run, as its Compose file names them.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    /// <summary>Its services, the web one first.</summary>
    public IReadOnlyList<string> Services { get; init; } = [];

    /// <summary>
    /// Until someone sets it up, whoever opens it first can take it over (its first visitor makes the admin account, or it
    /// signs in with a password everyone knows): its domain at install is put behind Cloudflare Access, or left for later.
    /// The catalog's open_until_setup.
    /// </summary>
    public bool OpenUntilSetup { get; init; }

    /// <summary>"512 MB", "1 GB".</summary>
    public string MemoryText => MemoryMb >= 1024 && MemoryMb % 512 == 0
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{MemoryMb / 1024.0:0.#} GB")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{MemoryMb} MB");

    /// <summary>"Postgres", "Postgres and Redis", or null for none.</summary>
    public string? DatabasesText => Databases.Count == 0 ? null
        : string.Join(" and ", Databases.Select(d => d.Kind == DatabaseKind.Redis ? "Redis" : "Postgres").Distinct());
}
