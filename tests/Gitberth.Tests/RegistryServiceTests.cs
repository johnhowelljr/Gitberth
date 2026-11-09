using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Images;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>Registry sign-ins: verified with Docker before they're saved, kept encrypted, sent per call as X-Registry-Auth.</summary>
public class RegistryServiceTests : IDisposable
{
    private readonly ImageKit.Db _db = new();
    private readonly ImageKit.Engine _engine = new();
    private readonly Security.SecretProtector _protector = ImageKit.Protector();

    public void Dispose() => _db.Dispose();

    private RegistryService Registries() => new(_db, _protector, _engine);

    private async Task<List<RegistryCredential>> RowsAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.Registries.AsNoTracking().ToListAsync();
    }

    private static JsonObject Decode(string header) =>
        JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(header.Replace('-', '+').Replace('_', '/'))))!.AsObject();

    [Fact]
    public async Task Keeps_the_secret_encrypted_after_docker_took_it()
    {
        var saved = await Registries().SaveAsync(new RegistryInput(null, "https://ghcr.io/", "  octo ", "ghp_s3cret"));

        Assert.True(saved.Ok, saved.Message);
        Assert.Equal("Signed in to ghcr.io as octo. Saved.", saved.Message);
        var row = Assert.Single(await RowsAsync());
        Assert.Equal(("ghcr.io", "octo", saved.Id), (row.Host, row.Username, (int?)row.Id));
        Assert.NotEqual("ghp_s3cret", row.ProtectedSecret);
        Assert.DoesNotContain("ghp_s3cret", row.ProtectedSecret);
        Assert.Equal("ghp_s3cret", _protector.Unprotect(row.ProtectedSecret));
        Assert.NotNull(row.VerifiedAt);
        Assert.Equal(["auth ghcr.io"], _engine.Calls);

        var listed = Assert.Single(await Registries().ListAsync());
        Assert.Equal(("ghcr.io", "octo", false), (listed.Host, listed.Username, listed.NeedsSecret));
    }

    [Fact]
    public async Task A_sign_in_docker_refuses_saves_nothing()
    {
        _engine.Auth = _ => new DockerApiException(401, "Get \"https://ghcr.io/v2/\": unauthorized: authentication required");

        var saved = await Registries().SaveAsync(new RegistryInput(null, "ghcr.io", "octo", "wrong"));

        Assert.False(saved.Ok);
        Assert.Null(saved.Id);
        Assert.Equal("ghcr.io refused the sign-in, so nothing was saved. Docker said: Get \"https://ghcr.io/v2/\": unauthorized: authentication required", saved.Message);
        Assert.Empty(await RowsAsync());
    }

    [Theory]
    [InlineData("docker.io")]
    [InlineData("index.docker.io")]
    [InlineData("https://index.docker.io/v1/")]
    [InlineData("registry-1.docker.io")]
    [InlineData("hub.docker.com")]
    public async Task Docker_hubs_names_are_one(string host)
    {
        var saved = await Registries().SaveAsync(new RegistryInput(null, host, "octo", "dckr_pat_x"));

        Assert.True(saved.Ok, saved.Message);
        Assert.Equal("Signed in to Docker Hub as octo. Saved.", saved.Message);
        Assert.Equal("docker.io", Assert.Single(await RowsAsync()).Host);
        // docker login's own address for Docker Hub.
        Assert.Equal(["auth https://index.docker.io/v1/"], _engine.Calls);

        foreach (var reference in new[] { "postgres:18", "docker.io/library/postgres", "index.docker.io/louislam/uptime-kuma:1", "registry-1.docker.io/x/y" })
            Assert.NotNull(await Registries().AuthHeaderAsync(ImageReference.Parse(reference)!));
        Assert.Null(await Registries().AuthHeaderAsync(ImageReference.Parse("ghcr.io/o/r")!));
    }

    [Fact]
    public async Task The_header_is_base64url_json_with_the_server_address()
    {
        // Six ? and six ~ in a row end a base64 group of three bytes wherever they fall: a ? there gives a /, a ~ a +.
        const string password = "??????~~~~~~";
        await Registries().SaveAsync(new RegistryInput(null, "registry.lan:5000", "ci", password));
        await Registries().SaveAsync(new RegistryInput(null, "docker.io", "octo", "dckr_pat_x"));

        var lan = await Registries().AuthHeaderAsync(ImageReference.Parse("registry.lan:5000/team/app:2")!);
        var hub = await Registries().AuthHeaderAsync(ImageReference.Parse("postgres:18")!);

        var json = Convert.FromBase64String(lan!.Replace('-', '+').Replace('_', '/'));
        var standard = Convert.ToBase64String(json);
        Assert.Contains('+', standard);
        Assert.Contains('/', standard);
        // URL-safe, as the docker CLI writes it.
        Assert.Equal(standard.Replace('+', '-').Replace('/', '_'), lan);
        var auth = Decode(lan);
        Assert.Equal(("ci", password, "registry.lan:5000"), ((string)auth["username"]!, (string)auth["password"]!, (string)auth["serveraddress"]!));
        Assert.Equal(["username", "password", "serveraddress"], auth.Select(p => p.Key));
        Assert.Equal("https://index.docker.io/v1/", (string)Decode(hub!)["serveraddress"]!);
    }

    // SEC-1 (changed by the backend fixer): an app's own sign-in goes first, but only to its own registry; for another
    // registry it's the one for that registry, if any.
    [Fact]
    public async Task An_apps_own_sign_in_goes_first_but_only_to_its_registry()
    {
        var mirror = await Registries().SaveAsync(new RegistryInput(null, "mirror.lan", "bot", "m"));
        await Registries().SaveAsync(new RegistryInput(null, "docker.io", "octo", "h"));

        var named = await Registries().AuthHeaderAsync(ImageReference.Parse("mirror.lan/postgres:18")!, mirror.Id);
        var elsewhere = await Registries().AuthHeaderAsync(ImageReference.Parse("postgres:18")!, mirror.Id);
        var byHost = await Registries().AuthHeaderAsync(ImageReference.Parse("postgres:18")!, registryId: 999);

        Assert.Equal("bot", (string)Decode(named!)["username"]!);
        Assert.Equal("octo", (string)Decode(elsewhere!)["username"]!);
        Assert.Equal("octo", (string)Decode(byHost!)["username"]!);
    }

    [Fact]
    public async Task Changing_a_sign_in_without_its_secret_keeps_and_checks_the_saved_one()
    {
        var first = await Registries().SaveAsync(new RegistryInput(null, "ghcr.io", "octo", "ghp_one"));
        _engine.Calls.Clear();

        var renamed = await Registries().SaveAsync(new RegistryInput(first.Id, "ghcr.io", "octo2", ""));

        Assert.True(renamed.Ok, renamed.Message);
        var row = Assert.Single(await RowsAsync());
        Assert.Equal(("octo2", "ghp_one"), (row.Username, _protector.Unprotect(row.ProtectedSecret)));
        Assert.Equal(["auth ghcr.io"], _engine.Calls);

        Assert.Equal("Give its password or access token.", (await Registries().SaveAsync(new RegistryInput(null, "quay.io", "x", ""))).Message);
        Assert.Equal("There's a sign-in for ghcr.io already: change that one.", (await Registries().SaveAsync(new RegistryInput(null, "ghcr.io", "x", "y"))).Message);
        Assert.Equal("Give the registry's address, such as ghcr.io or registry.lan:5000.", (await Registries().SaveAsync(new RegistryInput(null, "not a host", "x", "y"))).Message);
        Assert.Equal("Give the user name it signs in with.", (await Registries().SaveAsync(new RegistryInput(null, "quay.io", " ", "y"))).Message);
    }

    [Fact]
    public async Task A_secret_that_cant_be_read_any_more_needs_its_password_again()
    {
        await using (var db = _db.CreateDbContext())
        {
            // Encrypted with keys that are gone.
            db.Registries.Add(new RegistryCredential { Host = "ghcr.io", Username = "octo", ProtectedSecret = ImageKit.Protector().Protect("lost") });
            await db.SaveChangesAsync();
        }

        Assert.True(Assert.Single(await Registries().ListAsync()).NeedsSecret);
        Assert.Null(await Registries().AuthHeaderAsync(ImageReference.Parse("ghcr.io/o/r")!));
        var again = await Registries().SaveAsync(new RegistryInput((await RowsAsync())[0].Id, "ghcr.io", "octo", ""));
        Assert.Equal("Give its password or access token again: the saved one can't be read any more.", again.Message);
    }

    [Fact]
    public async Task Removing_a_sign_in_takes_it_off_the_apps_that_named_it()
    {
        var saved = await Registries().SaveAsync(new RegistryInput(null, "ghcr.io", "octo", "x"));
        var app = await _db.AddAsync(new AppDefinition { Name = "app", Slug = "app", RegistryId = saved.Id });

        await Registries().RemoveAsync(saved.Id!.Value);

        Assert.Empty(await RowsAsync());
        Assert.Null((await _db.AppAsync(app.Id)).RegistryId);
    }
}
