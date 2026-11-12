using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;

namespace Gitberth.Tests;

/// <summary>The VOLUMEs an image declares that an app's mounts don't cover, and how they're named.</summary>
public sealed class DeclaredVolumesTests
{
    private static List<(string Source, string Target)> Missing(IEnumerable<VolumeMount> mounts, params string[] declared) =>
        DeclaredVolumes.Missing(mounts, declared).Select(v => (v.Source, v.Target)).ToList();

    [Fact]
    public void Each_uncovered_path_gets_a_volume_named_after_its_last_segment()
    {
        Assert.Equal([("data", "/data"), ("postgresql", "/var/lib/postgresql")], Missing([], "/var/lib/postgresql", "/data"));
    }

    [Fact]
    public void A_path_a_mount_is_on_already_is_left_alone_whatever_kind_of_mount_it_is()
    {
        VolumeMount[] mounts =
        [
            new() { Source = "files", Target = "/data" },
            new() { Source = "/srv/cache", Target = "/var/cache/" },
        ];

        Assert.Empty(Missing(mounts, "/data/", "/var/cache"));
    }

    [Fact]
    public void A_name_that_is_taken_gets_a_number()
    {
        VolumeMount[] mounts = [new() { Source = "data", Target = "/srv/data" }];

        Assert.Equal([("data-2", "/data"), ("data-3", "/opt/data")], Missing(mounts, "/opt/data", "/data"));
    }

    [Fact]
    public void A_path_a_tmpfs_is_on_gets_no_volume()
    {
        Assert.Equal([("data", "/data")], DeclaredVolumes.Missing([], ["/data", "/tmp/cache"], tmpfs: ["/tmp/cache/"]).Select(v => (v.Source, v.Target)));
    }

    [Fact]
    public void Paths_that_are_not_absolute_or_are_the_root_or_come_twice_are_skipped()
    {
        Assert.Equal([("data", "/data")], Missing([], "relative/path", "/", "", "/data", "/data/"));
    }

    [Theory]
    [InlineData("""{"/data":{},"/var/lib/app":{}}""", "/data|/var/lib/app")]
    [InlineData("null", "")]
    [InlineData("{}", "")]
    [InlineData("sha256:1", "")]
    [InlineData("", "")]
    public void Dockers_volumes_json_gives_the_paths(string json, string expected)
    {
        Assert.Equal(expected, string.Join('|', DockerCli.ParseVolumesJson(json)));
    }
}
