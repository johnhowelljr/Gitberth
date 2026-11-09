namespace Gitberth.Components.Shared;

/// <summary>A brand mark: its colour (the tile's tint), the glyph's colour on the tile, and its path in a 24 x 24 box.</summary>
public sealed record LogoMark(string Color, string Glyph, string Path);

/// <summary>What an app's logo tile shows: Gitberth's own mark, a brand mark, a two-letter monogram, or the cube.</summary>
/// <param name="Slug">The mark's Simple Icons slug (postgresql, nextdotjs); null for the others.</param>
/// <param name="Color">The tile's tint (--lc).</param>
/// <param name="Glyph">The mark's or the letters' colour (--lg).</param>
public sealed record LogoChoice(string? Slug, LogoMark? Mark, string? Monogram, string Color, string Glyph, bool Gitberth = false)
{
    /// <summary>Nothing to go by: the cube icon.</summary>
    public bool Cube => !Gitberth && Mark is null && Monogram is null;
}

/// <summary>
/// Which logo an app gets (design-system.md section 7), most specific first: Gitberth's own container; the framework
/// Gitberth detected in a Git app's repo; the image's repository (postgres, nginx, node); words in the container's or
/// image's name (how sample-node-express gets Express); else a monogram from the image's name, on one of eight muted
/// hues picked from the name, so the same image always looks the same.
/// </summary>
public static partial class LogoCatalog
{
    private const string Neutral = "#8b95a7";
    private const string NeutralGlyph = "#dfe5ee";

    // The muted hues monograms sit on: (tile, letters), the letters the hue lightened by about a quarter.
    private static readonly (string Color, string Glyph)[] Hues =
    [
        ("#5b7cfa", "#9db2fc"), ("#8b6cf0", "#b8a6f6"), ("#3fa7d6", "#86c9e7"), ("#34b38f", "#7fd1b9"),
        ("#d39b3a", "#e6c385"), ("#d9705a", "#e9a596"), ("#c0619c", "#dba2c4"), ("#7d8aa6", "#b3bccd"),
    ];

    // DetectedKind (Detection.ProjectKind's names) for a Git app: a framework, or a runtime when that's all it says.
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NextJs"] = "nextdotjs", ["Nuxt"] = "nuxt", ["SvelteKit"] = "svelte", ["Static"] = "html5", ["DotNetWasm"] = "blazor",
        ["Dockerfile"] = "docker",
    };

    private static readonly Dictionary<string, string> KindRuntimes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NodeServer"] = "nodedotjs", ["NodeSpa"] = "vite", ["Python"] = "python", ["Go"] = "go", ["Ruby"] = "ruby", ["Php"] = "php",
        ["Java"] = "openjdk", ["Rust"] = "rust", ["Deno"] = "deno", ["DotNetWeb"] = "dotnet", ["DotNetWorker"] = "dotnet",
    };

    // An image's repository, the last part of its name: the catalogue of well-known images and runtimes.
    private static readonly Dictionary<string, string> Repositories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgres"] = "postgresql", ["postgresql"] = "postgresql", ["postgis"] = "postgresql", ["mysql"] = "mysql", ["mariadb"] = "mariadb",
        ["mongo"] = "mongodb", ["mongodb"] = "mongodb", ["redis"] = "redis", ["valkey"] = "redis", ["rabbitmq"] = "rabbitmq",
        ["nginx"] = "nginx", ["caddy"] = "caddy", ["traefik"] = "traefikproxy", ["grafana"] = "grafana", ["prometheus"] = "prometheus",
        ["minio"] = "minio", ["cloudflared"] = "cloudflare", ["portainer"] = "portainer", ["portainer-ce"] = "portainer", ["docker"] = "docker",
        ["dind"] = "docker", ["watchtower"] = "watchtower", ["caprover"] = "caprover", ["node"] = "nodedotjs", ["python"] = "python",
        ["golang"] = "go", ["aspnet"] = "dotnet", ["sdk"] = "dotnet", ["runtime"] = "dotnet", ["eclipse-temurin"] = "openjdk",
        ["openjdk"] = "openjdk", ["amazoncorretto"] = "openjdk", ["ruby"] = "ruby", ["php"] = "php", ["rust"] = "rust", ["deno"] = "deno",
        ["bun"] = "bun", ["strapi"] = "strapi", ["vercel"] = "vercel",
    };

    // Words in a name, in the order they're tried: frameworks before the runtimes they run on.
    private static readonly (string[] Words, string Slug)[] Frameworks =
    [
        (["react", "router"], "reactrouter"), (["vite", "vue"], "vuedotjs"), (["next"], "nextdotjs"), (["nextjs"], "nextdotjs"),
        (["nuxt"], "nuxt"), (["astro"], "astro"), (["angular"], "angular"), (["cra"], "createreactapp"), (["vue"], "vuedotjs"),
        (["vite"], "vite"), (["svelte"], "svelte"), (["sveltekit"], "svelte"), (["express"], "express"), (["fastify"], "fastify"),
        (["hono"], "hono"), (["koa"], "koa"), (["nestjs"], "nestjs"), (["nest"], "nestjs"), (["strapi"], "strapi"),
        (["typescript"], "typescript"), (["vercel"], "vercel"), (["django"], "django"), (["flask"], "flask"), (["fastapi"], "fastapi"),
        (["aiohttp"], "aiohttp"), (["dash"], "plotly"), (["plotly"], "plotly"), (["falcon"], "falcon"), (["gradio"], "gradio"),
        (["poetry"], "poetry"), (["sanic"], "sanic"), (["streamlit"], "streamlit"), (["uv"], "uv"), (["gin"], "gin"),
        (["quarkus"], "quarkus"), (["spring"], "springboot"), (["ktor"], "ktor"), (["laravel"], "laravel"), (["symfony"], "symfony"),
        (["rails"], "rubyonrails"), (["actix"], "actix"), (["rocket"], "rocket"), (["blazor"], "blazor"), (["static"], "html5"),
        (["html"], "html5"), (["dockerfile"], "docker"), (["grafana"], "grafana"), (["prometheus"], "prometheus"),
        (["postgres"], "postgresql"), (["mysql"], "mysql"), (["mariadb"], "mariadb"), (["mongo"], "mongodb"), (["redis"], "redis"),
        (["rabbitmq"], "rabbitmq"), (["nginx"], "nginx"), (["caddy"], "caddy"), (["traefik"], "traefikproxy"), (["minio"], "minio"),
        (["cloudflared"], "cloudflare"), (["portainer"], "portainer"), (["watchtower"], "watchtower"),
    ];

    private static readonly (string[] Words, string Slug)[] Runtimes =
    [
        (["node"], "nodedotjs"), (["nodejs"], "nodedotjs"), (["bun"], "bun"), (["deno"], "deno"), (["python"], "python"), (["go"], "go"),
        (["golang"], "go"), (["dotnet"], "dotnet"), (["aspnet"], "dotnet"), (["java"], "openjdk"), (["kotlin"], "openjdk"),
        (["jvm"], "openjdk"), (["ruby"], "ruby"), (["php"], "php"), (["rust"], "rust"),
    ];

    /// <summary>The brand mark for a slug; null when the catalogue has none.</summary>
    public static LogoMark? Find(string slug) => Marks.GetValueOrDefault(slug);

    /// <summary>Every slug the catalogue has a mark for.</summary>
    public static IReadOnlyCollection<string> Slugs => Marks.Keys;

    /// <param name="image">The image as Docker shows it: postgres:18, cloudflare/cloudflared:latest, an ID.</param>
    /// <param name="name">The container's name (or the app's title).</param>
    /// <param name="kind">A Git app's detected kind (NextJs, Static, NodeServer…); null for others.</param>
    /// <param name="self">Gitberth's own container.</param>
    public static LogoChoice Resolve(string? image, string? name, string? kind = null, bool self = false)
    {
        var repository = Repository(image);
        if (self || (repository == "gitberth" && name is "gitberth")) return new(null, null, null, "#6f78fb", "#ffffff", Gitberth: true);

        if (kind is not null && Kinds.TryGetValue(kind, out var byKind)) return Mark(byKind);
        if (repository is not null && Repositories.TryGetValue(repository, out var byRepository)) return Mark(byRepository);

        var words = Words(name).Concat(Words(repository)).ToList();
        foreach (var (need, slug) in Frameworks)
            if (need.All(words.Contains)) return Mark(slug);
        if (kind is not null && KindRuntimes.TryGetValue(kind, out var runtime)) return Mark(runtime);
        foreach (var (need, slug) in Runtimes)
            if (need.All(words.Contains)) return Mark(slug);

        var basis = repository ?? (string.IsNullOrWhiteSpace(name) ? null : name);
        if (basis is null) return new(null, null, null, Neutral, NeutralGlyph);
        var (color, glyph) = Hues[Hash(basis) % Hues.Length];
        return new(null, null, Monogram(basis), color, glyph);
    }

    /// <summary>Two letters for a name: the first letters of its first two words, else its first two letters.</summary>
    public static string Monogram(string name)
    {
        var parts = Split(name).Where(w => w.Length > 0 && char.IsLetterOrDigit(w[0])).ToList();
        if (parts.Count > 1 && parts[0] is "gitberth" or "gb") parts.RemoveAt(0);
        if (parts.Count > 1 && parts[0] is "sample") parts.RemoveAt(0);
        var letters = parts.Count switch
        {
            0 => name.Where(char.IsLetterOrDigit).Take(2),
            1 => parts[0].Take(2),
            _ => [parts[0][0], parts[1][0]],
        };
        return new string(letters.ToArray()).ToUpperInvariant();
    }

    /// <summary>The last part of an image's name, without its registry, tag or digest: cloudflare/cloudflared:latest gives cloudflared.</summary>
    public static string? Repository(string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.StartsWith("sha256:", StringComparison.Ordinal)) return null;
        var at = image.IndexOf('@');
        var name = at >= 0 ? image[..at] : image;
        var slash = name.LastIndexOf('/');
        var colon = name.LastIndexOf(':');
        if (colon > slash) name = name[..colon];
        name = name[(slash + 1)..].ToLowerInvariant();
        return name.Length == 0 || name.All(char.IsAsciiHexDigit) && name.Length >= 12 ? null : name;
    }

    private static LogoChoice Mark(string slug) =>
        Find(slug) is { } mark ? new(slug, mark, null, mark.Color, mark.Glyph) : new(null, null, null, Neutral, NeutralGlyph);

    private static IEnumerable<string> Words(string? text) => Split(text);

    private static IEnumerable<string> Split(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.ToLowerInvariant().Split(['-', '_', '.', '/', ':', '@', ' '], StringSplitOptions.RemoveEmptyEntries);

    // A stable hash (string.GetHashCode changes with every run of the process): FNV-1a.
    private static int Hash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return (int)(hash & 0x7fffffff);
    }
}
