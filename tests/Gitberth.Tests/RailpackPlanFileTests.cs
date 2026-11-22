using System.Text.Json.Nodes;
using Gitberth.Railpack;

namespace Gitberth.Tests;

/// <summary>Railpack 0.43.0's own output, from the spike's runs (Fixtures/Railpack), byte for byte.</summary>
internal static class RailpackFixtures
{
    public static string Text(string file) => ImageKit.Fixture("Railpack", file);

    public static JsonObject Plan(string file) => RailpackPlanFile.Parse(Text(file))!;

    public static JsonObject Info(string file) => RailpackInfoParser.ParseObject(Text(file))!;
}

/// <summary>
/// Gitberth's edits to the plan Railpack writes, and what it reads from it, on Railpack's own plans: .git left out once,
/// a named subset of the secrets made all of them (so the frontend's unpinned alpine:latest never runs), every command
/// step given all of them without the cache, and the start command, secrets and images read back.
/// </summary>
public class RailpackPlanFileTests
{
    private static List<string> Strings(JsonNode? node) => node is JsonArray a ? a.Select(v => v!.GetValue<string>()).ToList() : [];

    private static Dictionary<string, JsonObject> Steps(JsonObject plan) =>
        plan["steps"]!.AsArray().OfType<JsonObject>().ToDictionary(s => s["name"]!.GetValue<string>());

    [Fact]
    public void Git_is_left_out_of_the_image_once()
    {
        // The shell plan has no exclude at all; Node's leaves out node_modules.
        Assert.Equal([".git"], Strings(RailpackPlanFile.Adjust(RailpackFixtures.Plan("plan-shell.json"), noCache: false)["exclude"]));
        var node = RailpackPlanFile.Adjust(RailpackFixtures.Plan("plan-node-subsets.json"), noCache: false);
        Assert.Equal(["node_modules", ".git"], Strings(node["exclude"]));
        // Edited twice (a build tried again), it's there once.
        Assert.Equal(["node_modules", ".git"], Strings(RailpackPlanFile.Adjust(node, noCache: false)["exclude"]));
    }

    [Fact]
    public void A_named_subset_of_the_secrets_becomes_all_of_them_and_a_step_without_secrets_is_left_alone()
    {
        var plan = RailpackPlanFile.Adjust(RailpackFixtures.Plan("plan-node-subsets.json"), noCache: false);
        var steps = Steps(plan);

        // Node's install step named NPM_TOKEN: the frontend would hash it in alpine:latest.
        Assert.Equal(["*"], Strings(steps["install"]["secrets"]));
        Assert.Equal(["*"], Strings(steps["build"]["secrets"]));
        Assert.Null(steps["packages:mise"]["secrets"]);
        Assert.Null(steps["packages:apt:runtime"]["secrets"]);
        // What the build must be given stays as Railpack wrote it.
        Assert.Equal(["NPM_TOKEN"], RailpackPlanFile.Secrets(plan));
    }

    [Fact]
    public void Without_the_cache_every_step_that_runs_a_command_takes_all_the_secrets()
    {
        var node = Steps(RailpackPlanFile.Adjust(RailpackFixtures.Plan("plan-node-subsets.json"), noCache: true));
        // mise install and apt-get had no secrets: now the salted secrets-hash reaches them, so they run again.
        Assert.Equal(["*"], Strings(node["packages:mise"]["secrets"]));
        Assert.Equal(["*"], Strings(node["packages:apt:runtime"]["secrets"]));
        Assert.Equal(["*"], Strings(node["install"]["secrets"]));

        // The shell plan's mise step only names its image: there's nothing in it to run again.
        var shell = Steps(RailpackPlanFile.Adjust(RailpackFixtures.Plan("plan-shell.json"), noCache: true));
        Assert.Null(shell["packages:mise"]["secrets"]);
        Assert.Equal(["*"], Strings(shell["build"]["secrets"]));
    }

    [Fact]
    public void A_secret_the_build_is_never_given_comes_out_of_the_plan()
    {
        var plan = RailpackPlanFile.Parse("""{"secrets":["PATH","NPM_TOKEN","HOME"],"steps":[{"name":"build","secrets":["PATH"],"commands":[{"cmd":"make"}]}]}""")!;

        RailpackPlanFile.Adjust(plan, noCache: false, RailpackPlanner.NotPassed.ToList());

        Assert.Equal(["NPM_TOKEN"], RailpackPlanFile.Secrets(plan));
        Assert.Equal(["*"], Strings(Steps(plan)["build"]["secrets"]));
    }

    [Fact]
    public void The_start_command_secrets_and_images_are_read_from_the_plan()
    {
        var shell = RailpackFixtures.Plan("plan-shell.json");
        var node = RailpackFixtures.Plan("plan-node-subsets.json");

        Assert.Equal(("sh start.sh", "npm run start"), (RailpackPlanFile.StartCommand(shell), RailpackPlanFile.StartCommand(node)));
        Assert.Null(RailpackPlanFile.StartCommand(RailpackPlanFile.Parse("""{"deploy":{"startCommand":"  "}}""")!));
        Assert.Empty(RailpackPlanFile.Secrets(shell));
        Assert.Equal(["RAILPACK_BUILD_CMD"], RailpackPlanFile.Secrets(RailpackFixtures.Plan("plan-shell-buildcmd.json")));
        // The steps' images, and the deploy's base when it's an image (Node's base is a step of its own).
        Assert.Equal(["ghcr.io/railwayapp/railpack-builder:mise-2026.10.14", "ghcr.io/railwayapp/railpack-runtime:mise-2026.10.14"], RailpackPlanFile.Images(shell));
        Assert.Equal(["ghcr.io/railwayapp/railpack-builder:mise-2026.10.14", "ghcr.io/railwayapp/railpack-runtime:mise-2026.10.14"], RailpackPlanFile.Images(node));
    }

    [Fact]
    public void An_edited_plan_is_written_back_as_the_frontend_reads_it()
    {
        var edited = RailpackPlanFile.Adjust(RailpackFixtures.Plan("plan-node-subsets.json"), noCache: false);

        var back = RailpackPlanFile.Parse(RailpackPlanFile.Write(edited))!;

        Assert.True(JsonNode.DeepEquals(edited, back));
        Assert.Equal("sh -c 'apt-get update && apt-get install -y libatomic1'", Steps(back)["packages:apt:runtime"]["commands"]![0]!["cmd"]!.GetValue<string>());
        Assert.Null(RailpackPlanFile.Parse("not a plan"));
        Assert.Null(RailpackPlanFile.Parse("[1, 2]"));
    }
}
