using System.Text.RegularExpressions;
using Gitberth.Railpack;

namespace Gitberth.Tests;

/// <summary>
/// The Railpack release the code is pinned to (RailpackRelease) is the one Gitberth's image ships: the CLI from the same
/// frontend image the builds use, by its digest, and mise's release for both platforms, each checked by its sha256 and by
/// the build itself. The notices of what the image ships of others' go with it, and with the rig's copy of the tree.
/// </summary>
public class RailpackReleaseTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Dockerfile")) && Directory.Exists(Path.Combine(dir.FullName, "src", "Gitberth"))) return dir.FullName;
        throw new DirectoryNotFoundException("The repo's root wasn't found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Gitberths_image_ships_the_railpack_and_mise_the_code_is_pinned_to()
    {
        var dockerfile = File.ReadAllText(Path.Combine(Root, "Dockerfile")).Replace("\r\n", "\n");

        // Railpack's CLI is the binary of the frontend its builds use, by the same digest.
        Assert.Contains($"FROM {RailpackRelease.Frontend} AS railpack\n", dockerfile);
        Assert.Contains("COPY --from=railpack /railpack /usr/local/bin/railpack\n", dockerfile);
        // mise's release for both platforms, each added only when its sha256 matches.
        foreach (var arch in new[] { "x64", "arm64" })
            Assert.Matches(new Regex($@"ADD --checksum=sha256:[0-9a-f]{{64}} \\\n    https://github\.com/jdx/mise/releases/download/v{Regex.Escape(RailpackRelease.MiseVersion)}/"
                + $@"mise-v{Regex.Escape(RailpackRelease.MiseVersion)}-linux-{arch}-musl\.tar\.gz /mise\.tar\.gz\n"), dockerfile);
        // Where prepare's link points, and what the build checks it put there.
        Assert.Contains($"mv /tmp/mise/bin/mise {RailpackRelease.MisePath} ", dockerfile);
        Assert.Contains($"CI=1 {RailpackRelease.MisePath} --version | grep -qF {RailpackRelease.MiseVersion}", dockerfile);
        Assert.Contains($"railpack --version | grep -qF {RailpackRelease.Version}", dockerfile);
        // buildx, which Railpack's builds go through, is pinned too: Docker Hub's tag (no v) and its digest.
        Assert.Matches(new Regex(@"COPY --from=docker/buildx-bin:\d+\.\d+\.\d+@sha256:[0-9a-f]{64} /buildx /usr/libexec/docker/cli-plugins/docker-buildx\n"), dockerfile);
    }

    [Fact]
    public void The_notices_go_with_the_image_and_the_rigs_copy_of_the_tree()
    {
        var notices = File.ReadAllText(Path.Combine(Root, "THIRD-PARTY-NOTICES.md"));
        Assert.Contains($"Railpack {RailpackRelease.Version}", notices);
        Assert.Contains($"mise {RailpackRelease.MiseVersion}", notices);
        Assert.Contains("MIT License", notices);
        Assert.Contains("COPY LICENSE.md THIRD-PARTY-NOTICES.md ./", File.ReadAllText(Path.Combine(Root, "Dockerfile")));

        // The rig builds Gitberth's image from what sync_tree copies into it.
        var run = File.ReadAllText(Path.Combine(Root, "tests", "e2e", "run.sh")).Replace("\r\n", "\n");
        var sync = run[run.IndexOf("sync_tree() {", StringComparison.Ordinal)..];
        Assert.Contains("THIRD-PARTY-NOTICES.md", sync[..sync.IndexOf("\n}", StringComparison.Ordinal)]);
    }
}
