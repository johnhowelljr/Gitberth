using System.Text.RegularExpressions;

namespace Gitberth.Detection;

// Apps set up for Vercel build with their framework's Node adapter instead: adapter-auto and adapter-vercel (SvelteKit)
// and @astrojs/vercel (Astro) only produce output for the platforms they know. The swap happens in the image build's copy
// of the repo: the Node adapter is installed after the dependencies, and a script points the config's import at it.
public sealed partial class ProjectAnalyzer
{
    // Where the adapter is imported. SvelteKit reads svelte.config.*, or from kit 2.62 the sveltekit() options in
    // vite.config.*, the only place kit 3 allows. Astro reads the first astro.config.* in this order.
    private static readonly string[] SvelteKitConfigs =
        ["svelte.config.js", "svelte.config.mjs", "svelte.config.ts", "vite.config.js", "vite.config.mjs", "vite.config.ts", "vite.config.cjs", "vite.config.mts", "vite.config.cts"];
    private static readonly string[] AstroConfigs = ["astro.config.mjs", "astro.config.js", "astro.config.ts", "astro.config.mts", "astro.config.cjs", "astro.config.cts"];

    /// <summary>
    /// Points the SvelteKit config at adapter-node in place of adapter-auto or adapter-vercel. adapter-node reads only its
    /// own options (out, precompress, envPrefix), so the Vercel ones (runtime, regions, ...) can stay. Like
    /// <see cref="AstroNodeSwap"/>, it runs in the app's folder after the install, in the build's copy of the repo. The
    /// script reaches node through a quoted heredoc, so nothing in it is expanded or escaped. It always succeeds: when it
    /// can't switch the adapter it says why and changes nothing, so any error comes from the framework itself.
    /// </summary>
    internal static readonly string SvelteKitNodeSwap = $$"""
        RUN node <<'EOF'
        const fs = require('fs');
        const configs = [{{JsStrings(SvelteKitConfigs)}}].filter((name) => fs.existsSync(name));
        let switched = false;
        for (const name of configs) {
          const text = fs.readFileSync(name, 'utf8');
          const swapped = text.replace(/(['"`])@sveltejs\/adapter-(?:auto|vercel)\1/g, '$1@sveltejs/adapter-node$1');
          if (swapped === text) continue;
          fs.writeFileSync(name, swapped);
          console.log(`Gitberth: ${name} now imports @sveltejs/adapter-node`);
          switched = true;
        }
        if (!switched) console.log(configs.length === 0 ? 'Gitberth: found no svelte.config or vite.config file, so the adapter was not switched'
          : `Gitberth: found no @sveltejs/adapter-auto or adapter-vercel import in ${configs.join(', ')}, so the adapter was not switched`);
        EOF
        """;

    /// <summary>
    /// Points the Astro config at a module that returns @astrojs/node in standalone mode, in place of @astrojs/vercel or
    /// its /serverless, /static and /edge entry points. The Node adapter needs a mode and takes none of the Vercel options
    /// (webAnalytics, isr, ...), so the module ignores whatever the config passes, and any call shape keeps working.
    /// </summary>
    internal static readonly string AstroNodeSwap = $$"""
        RUN node <<'EOF'
        const fs = require('fs');
        const configs = [{{JsStrings(AstroConfigs)}}].filter((name) => fs.existsSync(name));
        let switched = false;
        for (const name of configs) {
          const text = fs.readFileSync(name, 'utf8');
          const swapped = text.replace(/(['"`])@astrojs\/vercel(?:\/(?:serverless|static|edge))?\1/g, '$1./gitberth-astro-node.mjs$1');
          if (swapped === text) continue;
          fs.writeFileSync(name, swapped);
          console.log(`Gitberth: ${name} now imports @astrojs/node in standalone mode, through gitberth-astro-node.mjs`);
          switched = true;
        }
        if (switched) fs.writeFileSync('gitberth-astro-node.mjs', `// Written by Gitberth in place of @astrojs/vercel, whose options don't apply here.
        import node from '@astrojs/node';
        export default (..._options) => node({ mode: 'standalone' });
        `);
        else console.log(configs.length === 0 ? 'Gitberth: found no astro.config file, so the adapter was not switched'
          : `Gitberth: found no @astrojs/vercel import in ${configs.join(', ')}, so the adapter was not switched`);
        EOF
        """;

    /// <summary>@astrojs/node's standalone server, which listens on HOST and PORT.</summary>
    private static readonly string[] AstroStart = ["node", "./dist/server/entry.mjs"];

    /// <summary>Astro's own default port, and the one its Docker guide uses.</summary>
    private const int AstroPort = 4321;

    // The Node adapter major for each framework major, newest first, from the adapters' peerDependencies. A version newer
    // than these, or one package.json doesn't spell out ("latest", "catalog:"), gets the adapter's latest release.
    private static readonly (int Framework, int Adapter)[] AdapterNodeMajors = [(3, 6), (2, 5), (1, 1)];
    private static readonly (int Framework, int Adapter)[] AstroNodeMajors = [(7, 11), (6, 10), (5, 9), (4, 8), (3, 6), (2, 5), (1, 4)];

    /// <summary>
    /// adapter-auto or adapter-vercel, when the SvelteKit config imports it. In Docker adapter-auto finds no platform it
    /// knows and outputs no server.
    /// </summary>
    private static string? SvelteKitVercelAdapter(string root)
    {
        foreach (var name in SvelteKitConfigs)
        {
            if (ReadFirst(root, name) is { } config && SvelteKitVercelImport().Match(config) is { Success: true } m) return "adapter-" + m.Groups[2].Value;
        }
        return null;
    }

    /// <summary>The adapter-node build, with adapter-node added to the image and swapped in for the one the config imports.</summary>
    private static DetectionResult SvelteKitOnNode(DetectionResult r, PackageJson pkg, NodeBuild b, int node, string adapter)
    {
        r.Kind = ProjectKind.SvelteKit;
        r.Title = $"SvelteKit ({adapter} → adapter-node)";
        r.ContainerPort = 3000;
        r.Evidence.Add($"Built with @sveltejs/adapter-node instead of {adapter}, without changing the repo");
        var major = Majors(pkg, "@sveltejs/kit", AdapterNodeMajors)?.Adapter;
        r.Dockerfile = DockerfileTemplates.SvelteKitNode(node, WithNodeAdapter(b, pkg, "@sveltejs/adapter-node", major, SvelteKitNodeSwap));
        return r;
    }

    /// <summary>
    /// How an Astro app with a Node or Vercel adapter builds: whether its @astrojs/vercel import is swapped for @astrojs/node,
    /// and whether the result is a server. Null without either adapter (a static site, as before), and for @astrojs/node
    /// in middleware mode, where the app runs its own server.
    /// </summary>
    private static (bool Swap, bool Server)? AstroAdapter(string root, PackageJson pkg)
    {
        var config = ReadFirst(root, AstroConfigs);
        if (config is null || AstroMiddleware().IsMatch(config)) return null;
        var swap = AstroVercelImport().IsMatch(config);
        if (!swap && !AstroNodeImport().IsMatch(config)) return null;
        // From Astro 5 the Node adapter always builds a server. Before that, static output (the default) is a static site in dist/.
        var astro = Majors(pkg, "astro", AstroNodeMajors)?.Framework;
        return (swap, astro is null or >= 5 || AstroServerOutput().IsMatch(config));
    }

    /// <summary>@astrojs/node's standalone server, or the static site in dist/ that older Astro versions build with it.</summary>
    private static DetectionResult Astro(DetectionResult r, PackageJson pkg, NodeBuild b, int node, VercelConfig? vercel, (bool Swap, bool Server) astro)
    {
        var adapter = "@astrojs/node";
        if (astro.Swap)
        {
            r.Evidence.Add("Built with @astrojs/node instead of @astrojs/vercel, without changing the repo");
            b = WithNodeAdapter(b, pkg, "@astrojs/node", Majors(pkg, "astro", AstroNodeMajors)?.Adapter, AstroNodeSwap);
            adapter = "@astrojs/vercel → @astrojs/node";
        }
        if (!astro.Server) return Spa(r, pkg, b, astro.Swap ? $"Astro (static, {adapter})" : "Astro (static)", node, vercel?.OutputDirectory ?? "dist", spaFallback: false);
        r.Kind = ProjectKind.NodeServer;
        r.Title = $"Astro ({adapter})";
        r.ContainerPort = AstroPort;
        r.Evidence.Add($"Starts with `{string.Join(' ', AstroStart)}`, the adapter's standalone server");
        r.Evidence.Add($"Port {AstroPort} (Astro's default; the app gets PORT={AstroPort})");
        r.Dockerfile = DockerfileTemplates.NodeServer(node, b, AstroStart, AstroPort);
        return r;
    }

    /// <summary>The build with a Node adapter installed after the dependencies, unless the app has it already, and swapped into the config.</summary>
    private static NodeBuild WithNodeAdapter(NodeBuild b, PackageJson pkg, string adapter, int? major, string swap)
    {
        var installs = b.Installs.ToList();
        if (!pkg.Has(adapter)) installs.Add($"RUN {b.Pm.CacheMount}{b.Pm.AddDevCommand(major is int m ? $"{adapter}@{m}" : adapter)}");
        installs.Add(swap);
        return b with { Installs = installs };
    }

    /// <summary>The row of the newest framework major that package.json's version allows ("^2.5.0" → kit 2), or null.</summary>
    private static (int Framework, int Adapter)? Majors(PackageJson pkg, string framework, (int Framework, int Adapter)[] table)
    {
        if (!pkg.Versions.TryGetValue(framework, out var spec)) return null;
        foreach (var row in table)
            if (NodeVersions.Satisfies(row.Framework, spec)) return row;
        return null;
    }

    private static string JsStrings(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    [GeneratedRegex(@"(['""`])@sveltejs/adapter-(auto|vercel)\1")]
    private static partial Regex SvelteKitVercelImport();

    [GeneratedRegex(@"(['""`])@astrojs/vercel(/(serverless|static|edge))?\1")]
    private static partial Regex AstroVercelImport();

    [GeneratedRegex(@"(['""`])@astrojs/node\1")]
    private static partial Regex AstroNodeImport();

    [GeneratedRegex(@"\bmode\s*:\s*['""`]middleware['""`]")]
    private static partial Regex AstroMiddleware();

    [GeneratedRegex(@"\boutput\s*:\s*['""`](server|hybrid)['""`]")]
    private static partial Regex AstroServerOutput();
}
