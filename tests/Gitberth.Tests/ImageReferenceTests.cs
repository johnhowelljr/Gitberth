using Gitberth.Images;

namespace Gitberth.Tests;

/// <summary>Image references read as Docker reads them (its distribution/reference rules).</summary>
public class ImageReferenceTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("postgres", "docker.io", "library/postgres", null, null, "postgres", "docker.io/library/postgres:latest", "latest")]
    [InlineData("postgres:18", "docker.io", "library/postgres", "18", null, "postgres:18", "docker.io/library/postgres:18", "18")]
    [InlineData("library/postgres", "docker.io", "library/postgres", null, null, "postgres", "docker.io/library/postgres:latest", "latest")]
    [InlineData("docker.io/postgres", "docker.io", "library/postgres", null, null, "postgres", "docker.io/library/postgres:latest", "latest")]
    [InlineData("index.docker.io/x/y:1", "docker.io", "x/y", "1", null, "x/y:1", "docker.io/x/y:1", "1")]
    [InlineData("registry-1.docker.io/louislam/uptime-kuma:1", "docker.io", "louislam/uptime-kuma", "1", null, "louislam/uptime-kuma:1", "docker.io/louislam/uptime-kuma:1", "1")]
    [InlineData("ghcr.io/o/r:1.2", "ghcr.io", "o/r", "1.2", null, "ghcr.io/o/r:1.2", "ghcr.io/o/r:1.2", "1.2")]
    [InlineData("localhost:5000/x", "localhost:5000", "x", null, null, "localhost:5000/x", "localhost:5000/x:latest", "latest")]
    [InlineData("localhost/x:dev", "localhost", "x", "dev", null, "localhost/x:dev", "localhost/x:dev", "dev")]
    [InlineData("  nginx:stable-alpine  ", "docker.io", "library/nginx", "stable-alpine", null, "nginx:stable-alpine", "docker.io/library/nginx:stable-alpine", "stable-alpine")]
    [InlineData("gitberth-sample-node-express", "docker.io", "library/gitberth-sample-node-express", null, null, "gitberth-sample-node-express",
        "docker.io/library/gitberth-sample-node-express:latest", "latest")]
    public void Reads_the_forms_people_write(string text, string registry, string repository, string? tag, string? digest, string familiar, string canonical,
        string tagOrDigest)
    {
        var reference = ImageReference.Parse(text);

        Assert.NotNull(reference);
        Assert.Equal((registry, repository, tag, digest), (reference.Registry, reference.Repository, reference.Tag, reference.Digest));
        Assert.Equal(familiar, reference.Familiar);
        Assert.Equal(canonical, reference.Canonical);
        Assert.Equal(tagOrDigest, reference.TagOrDigest);
        Assert.Equal($"{registry}/{repository}", reference.FromImage);
        Assert.Equal(registry == "docker.io", reference.IsDockerHub);
    }

    [Fact]
    public void A_digest_is_what_is_pulled_and_checked_even_with_a_tag()
    {
        var pinned = ImageReference.Parse($"registry.lan:5000/a/b@sha256:{Hex}")!;
        Assert.Equal(("registry.lan:5000", "a/b", null, $"sha256:{Hex}"), (pinned.Registry, pinned.Repository, pinned.Tag, pinned.Digest));
        Assert.Equal($"registry.lan:5000/a/b@sha256:{Hex}", pinned.Canonical);
        Assert.Equal($"sha256:{Hex}", pinned.TagOrDigest);

        // name:tag@sha256:…, where the digest wins: the tag is kept only to show.
        var both = ImageReference.Parse($"x:tag@sha256:{Hex}")!;
        Assert.Equal(("docker.io", "library/x", "tag", $"sha256:{Hex}"), (both.Registry, both.Repository, both.Tag, both.Digest));
        Assert.Equal($"docker.io/library/x@sha256:{Hex}", both.Canonical);
        Assert.Equal($"sha256:{Hex}", both.TagOrDigest);
        Assert.Equal($"x:tag@sha256:{Hex}", both.Familiar);
        Assert.Equal("x:tag", both.WithoutDigest.Familiar);
    }

    [Fact]
    public void Names_the_registry_and_the_short_name()
    {
        Assert.Equal(("Docker Hub", "postgres"), (ImageReference.Parse("postgres:18")!.RegistryName, ImageReference.Parse("postgres:18")!.ShortName));
        Assert.Equal(("ghcr.io", "r"), (ImageReference.Parse("ghcr.io/o/r")!.RegistryName, ImageReference.Parse("ghcr.io/o/r")!.ShortName));
        Assert.Equal("uptime-kuma", ImageReference.Parse("louislam/uptime-kuma:1")!.ShortName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Postgres")]
    [InlineData("postgres:")]
    [InlineData("postgres:bad tag")]
    [InlineData("postgres:-dash")]
    [InlineData("-postgres")]
    [InlineData("postgres@sha256:abc")]
    [InlineData("postgres@sha256:" + "0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:" + Hex)]
    [InlineData(Hex)]
    [InlineData("a//b")]
    [InlineData("ghcr.io/")]
    [InlineData("http://ghcr.io/x")]
    [InlineData("postgres:18 --rm")]
    [InlineData("my_registry.lan:5000/x")]
    public void Refuses_what_is_not_a_reference(string? text) => Assert.Null(ImageReference.Parse(text));

    [Fact]
    public void An_image_id_is_one_but_not_a_reference()
    {
        Assert.True(ImageReference.IsImageId(Hex));
        Assert.True(ImageReference.IsImageId("sha256:" + Hex));
        Assert.False(ImageReference.IsImageId("postgres:18"));
    }
}
