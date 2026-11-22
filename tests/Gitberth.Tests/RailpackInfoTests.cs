using Gitberth.Railpack;

namespace Gitberth.Tests;

/// <summary>
/// What railpack prepare's info file says, read from Railpack's own: a plan that worked (its provider, tools, logs, and
/// what the plan adds), a failed one (its logs only, its provider from "Detected …"), and what each failure ends with.
/// </summary>
public class RailpackInfoTests
{
    [Fact]
    public void A_plan_that_worked_says_what_railpack_found()
    {
        var info = RailpackInfoParser.Parse(RailpackFixtures.Info("info-shell.json"), RailpackFixtures.Plan("plan-shell.json"));

        Assert.Equal(("0.43.0", "shell", "sh start.sh"), (info.Version, info.Provider, info.StartCommand));
        Assert.Equal(["shell"], info.Providers);
        Assert.Empty(info.Packages);
        Assert.Empty(info.Secrets);
        Assert.Equal("sh", info.Metadata["detectedShellInterpreter"]);
        // Railpack's capitalised keys; an empty DocsPath is none.
        Assert.Equal(
        [
            new RailpackLog("info", "Detected Shell", null),
            new RailpackLog("info", "Using shell script: start.sh with interpreter: sh", null),
            new RailpackLog("suggestion", "Add a `.dockerignore` file to exclude files from the build", "/config/excluding-files"),
        ], info.Logs);
        Assert.Equal("Shell script with Railpack", RailpackProviders.Summary(info));
    }

    [Fact]
    public void Its_tools_and_secrets_come_with_it()
    {
        var info = RailpackInfoParser.Parse(RailpackFixtures.Info("info-node-subsets.json"), RailpackFixtures.Plan("plan-node-subsets.json"));

        Assert.Equal([new RailpackPackage("node", "lts", "24.22.2", "railpack default")], info.Packages);
        Assert.Equal(["NPM_TOKEN"], info.Secrets);
        Assert.Equal(("node", "npm run start", "Node.js with Railpack"), (info.Provider, info.StartCommand, RailpackProviders.Summary(info)));
        Assert.Equal(["No node package manager detected, using npm"], info.Logs.Where(l => l.Level == "warn").Select(l => l.Message));
        Assert.Equal("Railpack found Node.js with Railpack: node 24.22.2", RailpackText.Found(info));
    }

    [Fact]
    public void Lower_case_keys_and_a_capitalised_provider_are_read_too()
    {
        var info = RailpackInfoParser.Parse(RailpackInfoParser.ParseObject("""
            {"detectedProviders":["Elixir"],"logs":[{"level":"WARN","msg":"Pin your Elixir","docsPath":"/languages/elixir"}],"success":true}
            """)!, null);

        Assert.Equal(("elixir", RailpackRelease.Version), (info.Provider, info.Version));
        Assert.Equal([new RailpackLog("warn", "Pin your Elixir", "/languages/elixir")], info.Logs);
        Assert.Null(info.StartCommand);
    }

    [Fact]
    public void A_failed_info_has_only_its_logs_and_its_provider_comes_from_detected()
    {
        var info = RailpackInfoParser.Parse(RailpackFixtures.Info("info-nostart.json"), null);

        Assert.Equal("node", info.Provider);
        Assert.Equal(["node"], info.Providers);
        Assert.Empty(info.Packages);
        Assert.Empty(info.Metadata);
        // Nothing detected at all: no provider.
        Assert.Equal("", RailpackInfoParser.Parse(RailpackFixtures.Info("info-nothing.json"), null).Provider);
    }

    public static readonly TheoryData<string, RailpackFailure, string> Failures = new()
    {
        { "info-nothing.json", RailpackFailure.NothingFound, "Railpack could not determine how to build the app." },
        { "info-nostart.json", RailpackFailure.NoStartCommand, "No start command detected. Specify a start command: https://railpack.com/config/file#deploy" },
        // The JSON reader's words, after the warning that names railpack.json.
        { "info-badconfig.json", RailpackFailure.BadConfig, "line 4, column 1: parsing object after value: unexpected EOF" },
        // mise's (musl's) words for a lookup with no network, after 20 seconds.
        { "info-offline.json", RailpackFailure.Offline, "failed to lookup address information: Try again" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Each_failure_is_told_by_its_first_error(string fixture, RailpackFailure failure, string why)
    {
        var logs = RailpackInfoParser.Logs(RailpackFixtures.Info(fixture));

        Assert.Equal((failure, why), RailpackInfoParser.Classify(logs, ""));
    }

    [Fact]
    public void Anything_else_is_in_railpacks_own_words()
    {
        Assert.Equal((RailpackFailure.Other, "secret NPM_TOKEN: not found"),
            RailpackInfoParser.Classify([new RailpackLog("info", "Detected Node", null), new RailpackLog("error", "secret NPM_TOKEN: not found\nmore", null)], ""));
        // No error in the logs: the output's last line, and the network's words count there too.
        Assert.Equal((RailpackFailure.Other, "exit status 2"), RailpackInfoParser.Classify([], "planning\nexit status 2\n"));
        Assert.Equal(RailpackFailure.Offline, RailpackInfoParser.Classify([], "Get \"https://mise-versions.jdx.dev/\": dial tcp: lookup mise-versions.jdx.dev: no such host").Failure);
    }

    [Fact]
    public void Railpacks_try_again_says_what_the_network_did()
    {
        var stderr = RailpackFixtures.Text("prepare-shell-noseed.err");

        Assert.True(RailpackInfoParser.LooksOffline(stderr));
        Assert.Equal("dial tcp 127.0.0.1:443: connect: connection refused", RailpackInfoParser.NetworkWords(stderr));
        Assert.Equal("Railpack couldn't look up the tools' versions online (dial tcp 127.0.0.1:443: connect: connection refused). The next deploy tries again.",
            RailpackText.Offline(RailpackInfoParser.NetworkWords(stderr)));
        Assert.False(RailpackInfoParser.LooksOffline(RailpackFixtures.Text("prepare-shell.txt")));
    }
}
