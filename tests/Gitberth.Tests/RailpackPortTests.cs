using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Railpack;

namespace Gitberth.Tests;

/// <summary>
/// The container of an image Railpack built: PORT, its web container port, unless the app sets its own; a Command run
/// through the image's bash -c as one shell line; and what the deploy says about a port the app opened instead (plan D4).
/// </summary>
public class RailpackPortTests
{
    private static readonly PortMapping Web = new() { ContainerPort = 8080, HostPort = 3000, IsWeb = true };
    private static readonly PortMapping Debug = new() { ContainerPort = 9229, Access = PortAccess.Internal };

    [Fact]
    public void The_container_gets_PORT_its_web_container_port()
    {
        List<EnvValue> env = [new("NODE_ENV", "production", false)];

        var with = RailpackPort.With(env, [Debug, Web]);

        Assert.Equal([("NODE_ENV", "production", false), ("PORT", "8080", false)], with.Select(e => (e.Key, e.Value, e.IsSecret)));
        Assert.Equal(8080, RailpackPort.Added(env, [Debug, Web]));
        // What it was given stays as it was.
        Assert.Single(env);
    }

    [Fact]
    public void An_app_that_sets_PORT_keeps_its_own_and_one_without_a_web_port_gets_none()
    {
        List<EnvValue> own = [new("PORT", "5000", false)];
        Assert.Equal(own, RailpackPort.With(own, [Web]));
        Assert.Null(RailpackPort.Added(own, [Web]));

        Assert.Empty(RailpackPort.With([], [Debug]));
        Assert.Null(RailpackPort.Added([], []));
    }

    private static AppDefinition App(GitExtras? extras) => new() { Name = "shop", Slug = "shop", RuntimeExtras = GitExtras.Write(extras) };

    private static List<string> AfterImage(AppDefinition app, bool railpack)
    {
        var args = ContainerSpecFactory.For(app, "gitberth/shop:1a2b3c4", [], "gitberth", railpackImage: railpack).BuildRunCommand().Args;
        return args.Skip(args.IndexOf("gitberth/shop:1a2b3c4") + 1).ToList();
    }

    [Fact]
    public void A_command_goes_through_railpacks_bash_c_as_one_shell_line_with_its_words_quoted()
    {
        var app = App(new GitExtras { Command = ["node", "server.js", "--name", "it's mine", "--port=$PORT", ""] });

        Assert.Equal(["node server.js --name 'it'\\''s mine' '--port=$PORT' ''"], AfterImage(app, railpack: true));
        // Any other image takes the words as they are.
        Assert.Equal(["node", "server.js", "--name", "it's mine", "--port=$PORT", ""], AfterImage(app, railpack: false));
        // The docker run the deploy log shows is the one it runs: the image, then the one line.
        var serve = App(new GitExtras { Command = ["npm", "run", "serve"] });
        Assert.EndsWith("\\\n  gitberth/shop:1a2b3c4 'npm run serve'",
            ContainerSpecFactory.For(serve, "gitberth/shop:1a2b3c4", [], "gitberth", railpackImage: true).ToDisplayString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_entrypoint_of_its_own_or_a_single_word_is_left_as_it_is()
    {
        // Its own entrypoint replaces bash -c: the command's words go to it as they are.
        var entrypoint = App(new GitExtras { Entrypoint = ["tini", "--"], Command = ["node", "server.js"] });
        Assert.Equal(["--", "node", "server.js"], AfterImage(entrypoint, railpack: true));
        // One word is a shell line already: "npm start" given as one stays one.
        Assert.Equal(["npm start"], AfterImage(App(new GitExtras { Command = ["npm start"] }), railpack: true));
        Assert.Empty(AfterImage(App(null), railpack: true));
    }

    public static readonly TheoryData<int[], int[], string> Elsewhere = new()
    {
        { [4000, 9000], [], "It listens on 4000 and 9000, not 8080: set its web port on the Network tab, or have it listen on $PORT." },
        { [3000, 4000, 9000], [], "It listens on 3000, 4000 and 9000, not 8080: set its web port on the Network tab, or have it listen on $PORT." },
        { [], [4000], "It listens on 4000 only inside its container (127.0.0.1): have it listen on 0.0.0.0, on $PORT." },
        { [], [], "It doesn't listen on any other port either: have it listen on $PORT." },
    };

    [Theory]
    [MemberData(nameof(Elsewhere))]
    public void What_it_listens_on_instead_is_said_after_the_wait(int[] ports, int[] inside, string said)
    {
        Assert.Equal(said, RailpackText.ListensElsewhere(ports, inside, 8080));
    }

    [Fact]
    public void A_web_port_that_moves_says_where()
    {
        Assert.Equal("It listens on port 4000, not 8080, so its web port moves to 4000.", RailpackText.PortMoves(4000, 8080));
    }
}
