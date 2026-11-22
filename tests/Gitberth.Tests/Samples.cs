using System.Text.Json;
using System.Text.Json.Serialization;
using Gitberth.Detection;

namespace Gitberth.Tests;

/// <summary>What a sample project in tests/samples must be detected as, and how the Docker check knows it works.</summary>
/// <param name="Kind">The <see cref="ProjectKind"/> detection must give.</param>
/// <param name="Title">Text the detection's title must contain, like "FastAPI".</param>
/// <param name="Port">The container port detection must give; null for a worker.</param>
/// <param name="AppDir">The app's folder inside the sample, for samples laid out like a monorepo.</param>
/// <param name="Path">What the Docker check requests.</param>
/// <param name="Expect">Text the answer must contain.</param>
/// <param name="Env">Variables the container gets in the Docker check.</param>
/// <param name="Worker">No HTTP: the Docker check only makes sure it keeps running.</param>
/// <param name="Builder">"railpack" for a project only Railpack builds: Gitberth's analyzer must hand it over (Unknown,
/// Unrecognized), and the Docker check, which builds with Gitberth's Dockerfiles, leaves it out.</param>
/// <param name="RailpackProvider">The Railpack provider whose marker files the sample has (shell, elixir, gleam…).</param>
public sealed record SampleSpec(
    string Kind,
    string Title,
    int? Port,
    [property: JsonPropertyName("appDir")] string? AppDir = null,
    string Path = "/",
    string Expect = "",
    Dictionary<string, string>? Env = null,
    bool Worker = false,
    string? Builder = null,
    [property: JsonPropertyName("railpackProvider")] string? RailpackProvider = null)
{
    /// <summary>Railpack builds it, not a Dockerfile Gitberth writes.</summary>
    public bool Railpack => Builder == "railpack";
}

/// <summary>
/// The sample projects in tests/samples: small real layouts of each framework Gitberth builds. Every one is detected in
/// the tests, and tests/samples/check-on-docker.sh builds and runs them all on a Docker host.
/// </summary>
public static class Samples
{
    public static string Root { get; } = FindRoot();

    public static string Dir(string name) => System.IO.Path.Combine(Root, name);

    public static IEnumerable<string> Names() =>
        Directory.EnumerateDirectories(Root)
            .Where(d => File.Exists(System.IO.Path.Combine(d, "sample.json")))
            .Select(d => System.IO.Path.GetFileName(d))
            .Order(StringComparer.Ordinal);

    public static SampleSpec Spec(string name) =>
        JsonSerializer.Deserialize<SampleSpec>(File.ReadAllText(System.IO.Path.Combine(Dir(name), "sample.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
        ?? throw new InvalidOperationException($"{name}/sample.json is empty.");

    public static DetectionResult Analyze(string name) => new ProjectAnalyzer().Analyze(Dir(name), new AnalyzeOptions(AppDir: Spec(name).AppDir));

    // The tests run from tests/Gitberth.Tests/bin/<config>/<tfm>; the samples sit next to the test project.
    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var samples = System.IO.Path.Combine(dir.FullName, "tests", "samples");
            if (Directory.Exists(samples)) return samples;
        }
        throw new DirectoryNotFoundException("tests/samples wasn't found above " + AppContext.BaseDirectory);
    }
}

public class SampleTests
{
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var name in Samples.Names()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Each_sample_is_detected_the_way_its_sample_json_says(string name)
    {
        var spec = Samples.Spec(name);

        var r = Samples.Analyze(name);

        if (spec.Railpack)
        {
            // Gitberth doesn't build it: the analyzer's catch-all, which Detect automatically hands to Railpack, and the
            // files Railpack's provider goes by are there.
            Assert.Equal(ProjectKind.Unknown, r.Kind);
            Assert.True(r.Unrecognized, $"{name}: an Unknown Gitberth explains isn't handed to Railpack ({r.Title})");
            Assert.Equal(spec.Kind, r.Kind.ToString());
            Assert.Contains(spec.Title, r.Title);
            Assert.Equal(spec.Port, r.ContainerPort);
            var folder = spec.AppDir is { Length: > 0 } sub ? System.IO.Path.Combine(Samples.Dir(name), sub) : Samples.Dir(name);
            Assert.NotEmpty(Gitberth.Railpack.RailpackProviders.Markers(spec.RailpackProvider, folder));
            return;
        }
        Assert.True(r.CanBuild, $"{name}: {string.Join(" ", r.Warnings)}");
        Assert.Equal(spec.Kind, r.Kind.ToString());
        Assert.Contains(spec.Title, r.Title);
        Assert.Equal(spec.Port, r.ContainerPort);
        Assert.True(r.Dockerfile is not null || r.RepoDockerfile is not null, $"{name} has no Dockerfile to build with");
    }

    /// <summary>
    /// Writes each sample's generated Dockerfile, ignore file and what to check, for tests/samples/check-on-docker.sh.
    /// Only runs when GITBERTH_SAMPLES_OUT names the folder to write to.
    /// </summary>
    [Fact]
    public void Writes_each_samples_dockerfile_for_the_docker_check()
    {
        if (Environment.GetEnvironmentVariable("GITBERTH_SAMPLES_OUT") is not { Length: > 0 } output) return;
        foreach (var name in Samples.Names())
        {
            var spec = Samples.Spec(name);
            // Railpack builds these, not a Dockerfile Gitberth writes: nothing for the Docker check to build.
            if (spec.Railpack) continue;
            var r = Samples.Analyze(name);
            var dir = Directory.CreateDirectory(Path.Combine(output, name)).FullName;
            var dockerfile = r.Dockerfile is not null
                ? DockerfileMarkers.Expand(r.Dockerfile, [], [])
                : File.ReadAllText(Path.Combine(Samples.Dir(name), r.RepoDockerfile!.Replace('/', Path.DirectorySeparatorChar)));
            File.WriteAllText(Path.Combine(dir, "Dockerfile"), dockerfile);
            if (r.DockerIgnore is not null) File.WriteAllText(Path.Combine(dir, "Dockerfile.dockerignore"), r.DockerIgnore);
            File.WriteAllText(Path.Combine(dir, "env"), string.Concat((spec.Env ?? []).Select(e => $"{e.Key}={e.Value}\n")));
            File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new
            {
                context = r.BuildContext,
                port = r.ContainerPort,
                path = spec.Path,
                expect = spec.Expect,
                worker = spec.Worker,
                title = r.Title,
            }));
        }
    }
}
