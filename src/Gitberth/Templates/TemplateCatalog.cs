using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Network;
using Gitberth.Security;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Gitberth.Templates;

/// <summary>A template's icon: its bytes and media type.</summary>
public sealed record TemplateIcon(byte[] Bytes, string ContentType);

/// <summary>
/// The app templates bundled with Gitberth (Templates/catalog: one YAML file per template, with its icon): read once, each
/// checked (<see cref="Problems"/>); one that doesn't pass is left out of the gallery and said in the log. The gallery's
/// search and category chips (<see cref="Search"/>) and the icons' bytes come from here too.
/// </summary>
public sealed partial class TemplateCatalog
{
    /// <summary>Where the catalog's files are in Gitberth's assembly: Gitberth.Templates.catalog.&lt;file&gt;.</summary>
    public const string ResourcePrefix = "Gitberth.Templates.catalog.";

    /// <summary>
    /// What a template's Compose file can use besides its inputs, secrets and databases, set at install: APP_URL (its domain's
    /// https://, else its LAN address http://host:port), APP_HOST (that host name alone), APP_SCHEME (https or http) and
    /// APP_HTTPS (true with a domain). They're the app's variables afterwards, changed on its Environment tab like any other.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltIns = ["APP_URL", "APP_HOST", "APP_SCHEME", "APP_HTTPS"];

    /// <summary>The gallery's categories, in the order its chips show them.</summary>
    public static readonly IReadOnlyList<string> CategoryOrder =
        ["Monitoring", "Security", "Automation", "Development", "Reading", "Documents", "Productivity", "Home", "Finance", "Media", "Files", "Websites", "AI"];

    private static readonly Lazy<TemplateCatalog> BundledCatalog = new(() => FromAssembly(typeof(TemplateCatalog).Assembly));

    private readonly Dictionary<string, AppTemplate> _byId;
    private readonly Dictionary<string, TemplateIcon> _icons;

    /// <summary>The catalog Gitberth ships, read from its assembly once.</summary>
    public static TemplateCatalog Bundled => BundledCatalog.Value;

    /// <param name="files">Each file's name (uptime-kuma.yaml, uptime-kuma.svg) and bytes.</param>
    public TemplateCatalog(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        var list = files.ToList();
        _icons = new Dictionary<string, TemplateIcon>(StringComparer.Ordinal);
        foreach (var (name, bytes) in list)
            if (IconType(name) is { } type) _icons[name] = new TemplateIcon(bytes, type);
        var templates = new List<AppTemplate>();
        var problems = new List<string>();
        foreach (var (name, bytes) in list.Where(f => f.Name.EndsWith(".yaml", StringComparison.Ordinal)).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var id = name[..^".yaml".Length];
            var (template, parseProblems) = Parse(id, Encoding.UTF8.GetString(bytes));
            var found = template is null ? parseProblems : [.. parseProblems, .. Problems(template, _icons.GetValueOrDefault(template.Icon))];
            if (templates.Any(t => t.Id == id)) found.Add("Its id is another template's.");
            if (found.Count > 0)
            {
                problems.AddRange(found.Select(p => $"{id}: {p}"));
                continue;
            }
            templates.Add(template!);
        }
        All = templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        LoadProblems = problems;
        _byId = All.ToDictionary(t => t.Id, StringComparer.Ordinal);
        Categories = All.Select(t => t.Category).Distinct()
            .OrderBy(c => CategoryOrder.ToList().IndexOf(c) is var i and >= 0 ? i : int.MaxValue).ThenBy(c => c, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every template that passed its checks, by name.</summary>
    public IReadOnlyList<AppTemplate> All { get; }

    /// <summary>Why a file was left out ("ghost: …"); empty when every one passed.</summary>
    public IReadOnlyList<string> LoadProblems { get; }

    /// <summary>The categories templates are in, in the chips' order.</summary>
    public IReadOnlyList<string> Categories { get; }

    public AppTemplate? Find(string? id) => id is not null && _byId.TryGetValue(id, out var t) ? t : null;

    /// <summary>The template's icon; null when it has none (the page then shows a monogram).</summary>
    public TemplateIcon? Icon(string id) => Find(id) is { } t ? _icons.GetValueOrDefault(t.Icon) : null;

    /// <summary>The catalog in Gitberth's assembly: every resource under <see cref="ResourcePrefix"/>.</summary>
    public static TemplateCatalog FromAssembly(Assembly assembly) =>
        new(assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Select(n =>
        {
            using var stream = assembly.GetManifestResourceStream(n)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return (n[ResourcePrefix.Length..], memory.ToArray());
        }));

    // ---- The gallery ----

    /// <summary>
    /// The gallery's templates for a search and a category chip: every word of the search in its name, tagline,
    /// description, category or id (any case), in the category when one is chosen. Names that start with the search come
    /// first, then names that have it, then the rest, each by name.
    /// </summary>
    public IReadOnlyList<AppTemplate> Search(string? query, string? category = null)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var q = string.Join(' ', words);
        return All.Where(t => category is null || t.Category == category)
            .Where(t => words.All(w => Haystack(t).Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => q.Length == 0 ? 0 : t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Haystack(AppTemplate t) => $"{t.Name} {t.Tagline} {t.Description} {t.Category} {t.Id}";

    // ---- Reading a file ----

    private static readonly HashSet<string> TopKeys =
    [
        "id", "name", "tagline", "description", "category", "website", "source", "license", "icon", "icon_source", "memory", "latest_only",
        "shared_network", "open_until_setup", "web", "inputs", "secrets", "databases", "notes", "compose",
    ];

    /// <summary>A template from its YAML file, or null with why not (a key it doesn't know, a missing one, a wrong value).</summary>
    public static (AppTemplate? Template, List<string> Problems) Parse(string id, string yaml)
    {
        var problems = new List<string>();
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map) return (null, ["It isn't one YAML mapping."]);
            root = map;
        }
        catch (YamlException ex)
        {
            return (null, [$"It isn't valid YAML: {ex.Message}"]);
        }
        foreach (var key in root.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "").Where(k => !TopKeys.Contains(k)))
            problems.Add($"It has a key Gitberth doesn't know: {key}.");

        string Text(YamlMappingNode m, string key, bool required = true)
        {
            var value = Scalar(m, key);
            if (required && string.IsNullOrWhiteSpace(value)) problems.Add($"It needs {key}.");
            return value?.Trim() ?? "";
        }

        var fileId = Text(root, "id");
        if (fileId.Length > 0 && fileId != id) problems.Add($"Its id ({fileId}) isn't its file's name ({id}).");
        var web = root.Children.TryGetValue(new YamlScalarNode("web"), out var w) && w is YamlMappingNode webMap ? webMap : null;
        if (web is null) problems.Add("It needs web: its service, port and health path.");
        var port = web is null ? 0 : Int(Scalar(web, "port")) ?? 0;
        var memory = Int(Scalar(root, "memory"));
        if (memory is null) problems.Add("It needs memory: the MB it needs at least.");

        var inputs = new List<TemplateInput>();
        foreach (var node in Sequence(root, "inputs"))
        {
            if (node is not YamlMappingNode m)
            {
                problems.Add("Each input is a mapping.");
                continue;
            }
            var kindText = Scalar(m, "kind") ?? "text";
            if (!Enum.TryParse<TemplateInputKind>(kindText, ignoreCase: true, out var kind) || kindText.Any(char.IsDigit))
            {
                problems.Add($"The input {Scalar(m, "name")} has a kind Gitberth doesn't know: {kindText} (text, email, url, number or bool).");
                kind = TemplateInputKind.Text;
            }
            Unknown(m, ["name", "label", "help", "default", "required", "kind"], $"the input {Scalar(m, "name")}", problems);
            inputs.Add(new TemplateInput(Text(m, "name"), Text(m, "label"), Scalar(m, "help")?.Trim(), Scalar(m, "default"),
                Bool(Scalar(m, "required")) ?? false, kind));
        }

        var secrets = new List<TemplateSecret>();
        foreach (var node in Sequence(root, "secrets"))
        {
            if (node is not YamlMappingNode m)
            {
                problems.Add("Each secret is a mapping.");
                continue;
            }
            Unknown(m, ["name", "length", "alphabet"], $"the secret {Scalar(m, "name")}", problems);
            var alphabetText = Scalar(m, "alphabet") ?? "alphanumeric";
            if (!Enum.TryParse<SecretAlphabet>(alphabetText, ignoreCase: true, out var alphabet) || alphabetText.Any(char.IsDigit))
            {
                problems.Add($"The secret {Scalar(m, "name")} has an alphabet Gitberth doesn't know: {alphabetText} (alphanumeric or hex).");
                alphabet = SecretAlphabet.Alphanumeric;
            }
            secrets.Add(new TemplateSecret(Text(m, "name"), Int(Scalar(m, "length")) ?? 32, alphabet));
        }

        var databases = new List<TemplateDatabase>();
        foreach (var node in Sequence(root, "databases"))
        {
            if (node is not YamlMappingNode m)
            {
                problems.Add("Each database is a mapping.");
                continue;
            }
            Unknown(m, ["kind", "variable"], "a database", problems);
            var kindText = Scalar(m, "kind") ?? "";
            if (!Enum.TryParse<DatabaseKind>(kindText, ignoreCase: true, out var kind) || kindText.Any(char.IsDigit))
            {
                problems.Add($"A database's kind is postgres or redis, not {kindText}.");
                continue;
            }
            databases.Add(new TemplateDatabase(kind, Scalar(m, "variable")?.Trim() is { Length: > 0 } v ? v
                : kind == DatabaseKind.Redis ? Databases.DatabaseNames.RedisVariable : Databases.DatabaseNames.PostgresVariable));
        }

        var notes = Sequence(root, "notes").Select(n => (n as YamlScalarNode)?.Value?.Trim() ?? "").ToList();
        if (web is not null) Unknown(web, ["service", "port", "health"], "web", problems);
        var compose = Scalar(root, "compose") ?? "";
        if (compose.Trim().Length == 0) problems.Add("It needs compose: the Compose file it runs.");
        if (problems.Count > 0) return (null, problems);

        var info = ComposeFiles.Read(compose);
        var template = new AppTemplate(fileId, Text(root, "name"), Text(root, "tagline"), Collapse(Text(root, "description")), Text(root, "category"),
            Text(root, "website"), Text(root, "source"), Text(root, "license"), Text(root, "icon"), Text(root, "icon_source"), memory ?? 0,
            new TemplateWeb(Text(web!, "service"), port, Text(web!, "health")), inputs, secrets, databases, notes, compose,
            Scalar(root, "latest_only")?.Trim() is { Length: > 0 } latest ? latest : null, Bool(Scalar(root, "shared_network")) ?? false)
        {
            Images = info.Services.Where(s => s.Image is not null).Select(s => s.Image!).Distinct().ToList(),
        };
        template = template with
        {
            Services = info.Services.Select(s => s.Name).OrderBy(s => s == template.Web.Service ? 0 : 1).ToList(),
            OpenUntilSetup = Bool(Scalar(root, "open_until_setup")) ?? false,
        };
        return (problems.Count > 0 ? null : template, problems);
    }

    private static void Unknown(YamlMappingNode m, string[] known, string what, List<string> problems)
    {
        foreach (var key in m.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "").Where(k => !known.Contains(k)))
            problems.Add($"{Capital(what)} has a key Gitberth doesn't know: {key}.");
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // A description folded over lines reads as one paragraph.
    private static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

    private static string? Scalar(YamlMappingNode m, string key) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode s ? s.Value : null;

    private static IEnumerable<YamlNode> Sequence(YamlMappingNode m, string key) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlSequenceNode s ? s.Children : [];

    private static int? Int(string? text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool? Bool(string? text) => text?.Trim().ToLowerInvariant() switch { "true" or "yes" => true, "false" or "no" => false, _ => null };

    private static string? IconType(string name) =>
        name.EndsWith(".svg", StringComparison.Ordinal) ? "image/svg+xml" : name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : null;

    // ---- Checking a template ----

    /// <summary>
    /// Why a template can't be in the gallery; empty when it can. Its words and links are there; its Compose file reads as one
    /// app whose web service is the one it names and publishes nothing itself (Gitberth gives it its LAN port); every image is
    /// pinned to a version tag (latest only with the reason why); it uses no YAML anchors, aliases or merge keys (Compose
    /// applies them, a check of the file as written wouldn't see what they bring in); no service builds, names its
    /// container, publishes ports, reads files (env_file, secrets, configs), extends another, or shares another container's
    /// namespaces or volumes; its volumes are plain named ones of its own and its networks plain bridges; and nothing in it
    /// would be taken out of a pull request's preview (<see cref="ComposeTransform"/>'s preview checks: privileges, devices,
    /// GPUs, the host's namespaces and folders, host names that lead to the host, the Docker socket, hooks, runtimes and the
    /// rest). Every ${…} is one of its inputs, secrets, databases or the built-ins, and each of those is used; no secret is
    /// written in the file; and its icon is there, small and harmless.
    /// </summary>
    public static List<string> Problems(AppTemplate t, TemplateIcon? icon)
    {
        var problems = new List<string>();
        if (!IdPattern().IsMatch(t.Id)) problems.Add("Its id is lowercase letters, digits and '-'.");
        if (t.Tagline.Length > 90) problems.Add("Its tagline is longer than 90 characters.");
        if (t.Description.Length is < 60 or > 900) problems.Add("Its description is a few sentences (60 to 900 characters).");
        if (!CategoryOrder.Contains(t.Category)) problems.Add($"Its category {t.Category} isn't one of the gallery's ({string.Join(", ", CategoryOrder)}).");
        foreach (var (what, url) in new[] { ("website", t.Website), ("source", t.Source) })
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") problems.Add($"Its {what} isn't an https:// address.");
        if (t.MemoryMb is < 32 or > 16384) problems.Add("Its memory is between 32 and 16384 MB.");
        if (t.Web.Port is < 1 or > 65535) problems.Add("Its web port is between 1 and 65535.");
        if (!t.Web.Health.StartsWith('/')) problems.Add("Its health path starts with /.");
        foreach (var note in t.Notes.Where(n => n.Length == 0 || !".!?)".Contains(n[^1]))) problems.Add($"A note isn't a sentence: \"{note}\".");
        if (t.IconSource.Length == 0) problems.Add("It needs icon_source: where its icon came from (its repo's file, or monogram).");

        // Names: valid, unique, and none taken by a built-in or a database's parts.
        var names = t.Inputs.Select(i => i.Name).Concat(t.Secrets.Select(s => s.Name)).Concat(t.Databases.Select(d => d.Variable)).ToList();
        foreach (var name in names.Where(n => !Detection.EnvRules.IsValidKey(n))) problems.Add($"{name} isn't a valid variable name.");
        foreach (var name in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1)) problems.Add($"{name.Key} is declared twice.");
        foreach (var name in names.Where(n => BuiltIns.Contains(n))) problems.Add($"{name} is a built-in: Gitberth sets it.");
        var parts = t.Databases.SelectMany(PartsOf).ToList();
        foreach (var name in names.Where(n => parts.Contains(n))) problems.Add($"{name} is a database's part: Gitberth sets it.");
        foreach (var i in t.Inputs)
        {
            if (i.Label.Length == 0) problems.Add($"The input {i.Name} needs a label.");
            if (i.Default is { } d && InputProblem(i, d) is { } bad) problems.Add($"The input {i.Name}'s default: {bad}");
        }
        foreach (var s in t.Secrets.Where(s => s.Length is < 16 or > 128)) problems.Add($"The secret {s.Name} is 16 to 128 characters long.");
        foreach (var kind in t.Databases.GroupBy(d => d.Kind).Where(g => g.Key == DatabaseKind.Redis && g.Count() > 1))
            problems.Add("It has one Redis at most.");

        // The Compose file: one app with this web service.
        var info = ComposeFiles.Read(t.Compose);
        if (info.Error is { } error)
        {
            problems.Add($"Its Compose file: {error}");
            return problems;
        }
        problems.AddRange(ComposeApps.Problems(t.Compose, t.Web.Service).Select(p => $"Its Compose file: {p}"));
        var composeProblems = ComposeProblems(t);
        problems.AddRange(composeProblems);
        // What a template's file may do is what a pull request's may: one that a preview would lose something of is refused.
        // (After an anchor, a merge key or a broken file there's nothing to check it with.)
        if (!composeProblems.Contains(AnchorsProblem)) problems.AddRange(PreviewProblems(t, names.Concat(BuiltIns).Concat(t.Databases.SelectMany(PartsOf))));

        // Every ${…} is defined, and everything defined is used.
        var used = ComposeVariables.Used(t.Compose).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var defined = names.Concat(BuiltIns).Concat(parts).ToHashSet(StringComparer.Ordinal);
        foreach (var name in used.Where(n => !defined.Contains(n)).Order(StringComparer.Ordinal))
            problems.Add($"Its Compose file uses ${{{name}}}, which isn't one of its inputs, secrets, databases or built-ins.");
        foreach (var name in t.Inputs.Select(i => i.Name).Concat(t.Secrets.Select(s => s.Name)).Where(n => !used.Contains(n)))
            problems.Add($"{name} is declared but its Compose file doesn't use it.");
        foreach (var d in t.Databases.Where(d => !used.Contains(d.Variable) && !PartsOf(d).Any(used.Contains)))
            problems.Add($"Its {d.Kind} database's {d.Variable} isn't used by its Compose file (as a URL or in parts).");

        // The icon.
        if (icon is null) problems.Add($"Its icon {t.Icon} isn't next to it.");
        else problems.AddRange(IconProblems(t.Icon, icon));
        return problems;
    }

    /// <summary>The parts a database's variable is given in (V_HOST, V_PORT, V_NAME, V_USER, V_PASSWORD; Redis has no name or user).</summary>
    public static IEnumerable<string> PartsOf(TemplateDatabase d) => Databases.DatabaseUrls.PartNames(d.Variable, d.Kind);

    /// <summary>Why a value doesn't fit an input; null when it does.</summary>
    public static string? InputProblem(TemplateInput input, string value)
    {
        if (value.Length == 0) return input.Required ? $"{input.Label} is needed." : null;
        return input.Kind switch
        {
            TemplateInputKind.Email when !EmailPattern().IsMatch(value) => $"{input.Label} isn't an email address.",
            TemplateInputKind.Url when !Uri.TryCreate(value, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") =>
                $"{input.Label} isn't an http:// or https:// address.",
            TemplateInputKind.Number when !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) =>
                $"{input.Label} is a whole number.",
            TemplateInputKind.Bool when value is not ("true" or "false") => $"{input.Label} is true or false.",
            _ when value.Contains('\n') || value.Contains('\r') => $"{input.Label} is one line.",
            _ => null,
        };
    }

    private const string AnchorsProblem = "Its Compose file uses YAML anchors, aliases or merge keys (<<): a template's file says everything where it applies.";

    // The service keys a template never has, whatever a preview would keep: Gitberth gives the web service its port and names
    // the containers, and a template reads no files, builds nothing and shares no other container's namespaces or volumes.
    private static readonly string[] RefusedServiceKeys =
    [
        "ports", "build", "container_name", "network_mode", "privileged", "devices", "cap_add", "pid", "ipc", "uts", "cgroup", "userns_mode",
        "env_file", "secrets", "configs", "extends", "profiles", "links", "volumes_from",
    ];

    // What the Compose file's services may not do, read from the file itself.
    private static List<string> ComposeProblems(AppTemplate t)
    {
        var problems = new List<string>();
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(t.Compose));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode map) return ["Its Compose file isn't one YAML mapping."];
            root = map;
        }
        catch (YamlException ex)
        {
            return [$"Its Compose file isn't valid YAML: {ex.Message}"];
        }
        // Compose applies anchors and merge keys (x-defaults: &d … <<: *d); what they'd bring into a service is never seen here.
        if (UsesAnchors(root)) return [AnchorsProblem];
        var declaredVolumes = root.Children.TryGetValue(new YamlScalarNode("volumes"), out var v) && v is YamlMappingNode vm
            ? vm.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "").ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var (key, node) in root.Children)
        {
            var name = (key as YamlScalarNode)?.Value;
            if (name is not ("services" or "volumes" or "networks")) problems.Add($"Its Compose file has a top-level {name}, which a template doesn't use.");
            if (name is "volumes" or "networks" && node is YamlMappingNode m)
                foreach (var (entryKey, entry) in m.Children)
                {
                    var options = entry is YamlMappingNode e ? e.Children.Select(c => ((c.Key as YamlScalarNode)?.Value ?? "", (c.Value as YamlScalarNode)?.Value)).ToList() : [];
                    // A volume plain and of its own (a name could be another's, Gitberth's /data among them; a driver's options can
                    // mount the host's folders); a network a plain bridge (another driver, or its addresses, can put it on the LAN).
                    var other = name == "volumes" ? options : options.Where(o => !(o.Item1 == "driver" && o.Item2 == "bridge")).ToList();
                    if (other.Count > 0)
                        problems.Add($"Its Compose file's {(name == "volumes" ? "volume" : "network")} {(entryKey as YamlScalarNode)?.Value} has {string.Join(", ", other.Select(o => o.Item1))}: "
                            + (name == "volumes" ? "a template's volumes are plain ones of its own." : "a template's networks are plain bridges of its own."));
                    else if (entry is not (YamlMappingNode or YamlScalarNode { Value: null or "" }))
                        problems.Add($"Its Compose file's {(name == "volumes" ? "volume" : "network")} {(entryKey as YamlScalarNode)?.Value} isn't a mapping.");
                }
        }
        if (root.Children.TryGetValue(new YamlScalarNode("services"), out var s) && s is YamlMappingNode services)
            foreach (var (key, node) in services.Children)
            {
                var service = (key as YamlScalarNode)?.Value ?? "";
                if (node is not YamlMappingNode m) continue;
                foreach (var refused in RefusedServiceKeys)
                    if (m.Children.ContainsKey(new YamlScalarNode(refused)))
                        problems.Add(refused == "ports"
                            ? $"{service} publishes ports itself: Gitberth gives the web service its LAN port, and the others stay inside the app."
                            : $"{service} has {refused}, which a template doesn't use.");
                var image = Scalar(m, "image");
                if (image is null) problems.Add($"{service} has no image.");
                else if (ImageProblem(image, t.LatestOnly) is { } bad) problems.Add($"{service}: {bad}");
                foreach (var volume in m.Children.TryGetValue(new YamlScalarNode("volumes"), out var vs) && vs is YamlSequenceNode seq ? seq.Children : [])
                {
                    var source = volume is YamlScalarNode sv ? (sv.Value ?? "").Split(':')[0]
                        : volume is YamlMappingNode lv ? Scalar(lv, "type") == "volume" ? Scalar(lv, "source") ?? "" : Scalar(lv, "type") == "tmpfs" ? null : "(a bind)"
                        : "";
                    if (source is null) continue;
                    if (!declaredVolumes.Contains(source)) problems.Add($"{service} mounts {source}, which isn't a named volume of the file's own.");
                }
                foreach (var (envKey, envValue) in Environment(m))
                {
                    var literal = Placeholder().Replace(envValue, "");
                    if (Masking.IsSecretName(envKey) && literal.Trim().Length > 0 && !NotSecret(envKey, literal))
                        problems.Add($"{service}'s {envKey} is written in the file: a secret comes from the template's secrets or databases.");
                    if (Masking.HasUrlPassword(literal)) problems.Add($"{service}'s {envKey} has a password written in the file.");
                }
            }
        return problems;
    }

    // An anchor (an alias is its node again) or a merge key anywhere in the file.
    private static bool UsesAnchors(YamlNode node) =>
        !node.Anchor.IsEmpty
        || node is YamlMappingNode m && m.Children.Any(kv => kv.Key is YamlScalarNode { Value: "<<" } || UsesAnchors(kv.Key) || UsesAnchors(kv.Value))
        || node is YamlSequenceNode s && s.Children.Any(UsesAnchors);

    /// <summary>
    /// What a pull request's preview would lose of the template's file (<see cref="ComposeTransform.Apply"/> as a preview:
    /// privileges, devices, GPUs, the Docker socket, the host's namespaces, folders and network, host names that lead to the
    /// host, hooks, runtimes, sysctls, security options, volume and network drivers' options…), as problems: a template gets
    /// no more of the host than a preview does.
    /// </summary>
    private static List<string> PreviewProblems(AppTemplate t, IEnumerable<string> variables)
    {
        JsonObject? model;
        try
        {
            model = ModelOf(t.Compose);
        }
        catch (YamlException ex)
        {
            return [$"Its Compose file isn't valid YAML: {ex.Message}"];
        }
        if (model is null) return ["Its Compose file isn't one YAML mapping."];
        var target = new ComposeTarget
        {
            Slug = t.Id, WebService = t.Web.Service, DeploymentId = 1, Trigger = DeployTrigger.Created, Tag = "template", Preview = true,
            ReleaseDir = "/data/compose/template/release", ReleaseHostDir = "/data/compose/template/release",
            DataDir = "/data/compose/template/data", DataHostDir = "/data/compose/template/data",
            Ports = [new PortMapping { ContainerPort = t.Web.Port, HostPort = t.Web.Port, Access = PortAccess.Lan, IsWeb = true }],
            Kind = _ => PathKind.None, LinksOut = (_, _) => false, Variables = variables.ToHashSet(StringComparer.Ordinal),
        };
        ComposePlan plan;
        try
        {
            plan = ComposeTransform.Apply(model, target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException or System.Text.Json.JsonException)
        {
            return [$"Its Compose file couldn't be checked as a preview's: {ex.Message}"];
        }
        return [.. plan.Problems.Select(p => $"Its Compose file: {p}"), .. plan.Notes.Select(n => $"Its Compose file reaches past its containers, which a template's mustn't. {n}")];
    }

    /// <summary>
    /// The template's Compose file roughly as docker compose config gives it, for the preview checks: its ${…} filled with a
    /// stand-in, $$ as $, each service's volumes in long form, and its top-level volumes and networks as mappings.
    /// </summary>
    internal static JsonObject? ModelOf(string compose)
    {
        var filled = Placeholder().Replace(compose, m => m.Value == "$$" ? "$" : "x");
        var stream = new YamlStream();
        stream.Load(new StringReader(filled));
        if (stream.Documents.Count == 0 || ToJson(stream.Documents[0].RootNode) is not JsonObject model) return null;
        foreach (var (_, node) in model["services"] as JsonObject ?? [])
        {
            if (node is not JsonObject service || service["volumes"] is not JsonArray volumes) continue;
            var longForm = new JsonArray();
            foreach (var volume in volumes)
            {
                if (volume is JsonObject written)
                {
                    longForm.Add(written.DeepClone());
                    continue;
                }
                var parts = (volume is JsonValue v && v.TryGetValue<string>(out var text) ? text : "").Split(':');
                var mount = new JsonObject
                {
                    ["type"] = ComposeFiles.IsPath(parts[0]) ? "bind" : "volume", ["source"] = parts[0], ["target"] = parts.Length > 1 ? parts[1] : parts[0],
                };
                if (parts.Length > 2 && parts[2].Split(',').Contains("ro")) mount["read_only"] = true;
                longForm.Add(mount);
            }
            service["volumes"] = longForm;
        }
        foreach (var section in new[] { "volumes", "networks" })
            if (model[section] is JsonObject top)
                foreach (var key in top.Select(k => k.Key).ToList())
                    top[key] ??= new JsonObject();
        return model;
    }

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(kv => KeyValuePair.Create((kv.Key as YamlScalarNode)?.Value ?? "", ToJson(kv.Value)))),
        YamlSequenceNode s => new JsonArray(s.Children.Select(ToJson).ToArray()),
        YamlScalarNode { Value: null or "" } plain when plain.Style == YamlDotNet.Core.ScalarStyle.Plain => null,
        YamlScalarNode scalar => JsonValue.Create(scalar.Value),
        _ => null,
    };

    // A value with a secret-sounding name that isn't one: a file path, a switch, or a reference to a secret (MYSQL_PASSWORD_FILE).
    private static bool NotSecret(string key, string literal) =>
        literal.Trim() is "true" or "false" or "1" or "0" or "yes" or "no" || literal.StartsWith('/') || key.EndsWith("_FILE", StringComparison.Ordinal);

    // A service's environment: a mapping, or a list of KEY=value.
    private static IEnumerable<(string Key, string Value)> Environment(YamlMappingNode service)
    {
        if (!service.Children.TryGetValue(new YamlScalarNode("environment"), out var env)) yield break;
        if (env is YamlMappingNode map)
            foreach (var (k, value) in map.Children)
                yield return ((k as YamlScalarNode)?.Value ?? "", (value as YamlScalarNode)?.Value ?? "");
        else if (env is YamlSequenceNode list)
            foreach (var item in list.Children.OfType<YamlScalarNode>())
            {
                var text = item.Value ?? "";
                var eq = text.IndexOf('=');
                yield return eq < 0 ? (text, "") : (text[..eq], text[(eq + 1)..]);
            }
    }

    /// <summary>Why an image reference isn't pinned to a version tag; null when it is.</summary>
    public static string? ImageProblem(string image, string? latestOnly)
    {
        if (image.Contains('$')) return $"its image {image} is a variable.";
        if (image.Contains('@')) return null;
        var slash = image.LastIndexOf('/');
        var colon = image.LastIndexOf(':');
        if (colon < 0 || colon < slash) return $"its image {image} has no tag: pin it to a version.";
        var tag = image[(colon + 1)..];
        if (tag == "latest") return latestOnly is null ? $"its image {image} runs latest: pin it to a version, or say in latest_only why the project has nothing else." : null;
        return tag is "stable" or "main" or "master" or "nightly" or "edge" or "release" ? $"its image {image} runs {tag}, which moves: pin it to a version." : null;
    }

    private static List<string> IconProblems(string name, TemplateIcon icon)
    {
        var problems = new List<string>();
        if (icon.Bytes.Length > 64 * 1024) problems.Add($"Its icon {name} is over 64 KB.");
        if (icon.ContentType == "image/png")
        {
            if (icon.Bytes.Length < 8 || icon.Bytes[0] != 0x89 || icon.Bytes[1] != (byte)'P') problems.Add($"Its icon {name} isn't a PNG.");
            return problems;
        }
        var svg = Encoding.UTF8.GetString(icon.Bytes);
        if (!svg.Contains("<svg", StringComparison.Ordinal)) problems.Add($"Its icon {name} isn't an SVG.");
        if (UnsafeSvg().Match(svg) is { Success: true } m) problems.Add($"Its icon {name} has {m.Value.Trim()}: an icon is a picture, nothing more.");
        return problems;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,40}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\$\{[^}]*\}|\$\$|\$[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"<script|<foreignObject|\son[a-z]+\s*=|(?:href|src)\s*=\s*[""']?(?:https?:|//|javascript:|data:)|url\(\s*[""']?(?:https?:|//)|@import", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeSvg();
}
