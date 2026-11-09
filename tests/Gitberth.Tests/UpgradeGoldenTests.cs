using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Network;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Gitberth.Tests;

/// <summary>
/// What orb and kanban look like to Gitberth must not move with the unified apps (5.3): their runtime and build
/// fingerprints, which decide ConfigChangePending and RebuildPending, and the docker run their deploys use. These are the
/// strings step 3 gives, written out: an app with no settings beyond the classic ones gives exactly them.
/// </summary>
public sealed class UpgradeGoldenTests
{
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());

    /// <summary>Like orb: one LAN web port, nothing else.</summary>
    private static AppDefinition Orb() => new()
    {
        Id = 1, Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb", CurrentImage = "gitberth/orb:1d2948c",
        Ports = [new PortMapping { Id = 1, AppId = 1, ContainerPort = 80, HostPort = 3000, Access = PortAccess.Lan, IsWeb = true }],
    };

    /// <summary>Like kanban, with everything an app had in step 3: a secret, a build-time variable, a volume, another network, limits.</summary>
    private AppDefinition Kanban() => new()
    {
        Id = 5, Name = "kanban", Slug = "kanban", Owner = "acme", Repo = "kanban", CurrentImage = "gitberth/kanban:425df8a",
        MemoryLimitMb = 512, CpuLimit = 1.5,
        Ports =
        [
            new PortMapping { Id = 6, AppId = 5, ContainerPort = 3000, HostPort = 3001, Access = PortAccess.Lan, IsWeb = true },
            new PortMapping { Id = 7, AppId = 5, ContainerPort = 9229, Access = PortAccess.Internal },
        ],
        EnvVars =
        [
            new EnvVar { Id = 1, AppId = 5, Key = "NEXT_PUBLIC_SITE", ProtectedValue = _protector.Protect("https://harborlab.com"), BuildTime = true },
            new EnvVar { Id = 2, AppId = 5, Key = "DATABASE_URL", ProtectedValue = _protector.Protect("postgres://kanban:s3cret@postgres:5432/kanban"), IsSecret = true },
        ],
        Volumes = [new VolumeMount { Id = 1, AppId = 5, Source = "uploads", Target = "/app/uploads" }],
        Networks = [new AppNetwork { Id = 1, AppId = 5, Network = "media", Alias = "kanban-db" }],
    };

    [Fact]
    public void The_fingerprints_of_orb_and_kanban_are_step_3s()
    {
        Assert.Equal(("-p 3000:80 --network name=gitberth,alias=orb###unless-stopped##", "main##Auto###True##"),
            AppService.Fingerprint(Orb(), _protector, "gitberth"));
        Assert.Equal((
                "--expose 9229 -p 3001:3000 --network name=gitberth,alias=kanban --network name=media,alias=kanban,alias=kanban-db"
                + "#DATABASE_URL=postgres://kanban:s3cret@postgres:5432/kanban|NEXT_PUBLIC_SITE=https://harborlab.com#uploads:/app/uploads:False#unless-stopped#512#1.5",
                "main##Auto###True#NEXT_PUBLIC_SITE=https://harborlab.com#/app/uploads"),
            AppService.Fingerprint(Kanban(), _protector, "gitberth"));
    }

    [Fact]
    public void The_docker_run_of_orb_and_kanban_is_step_3s()
    {
        Assert.Equal("""
            docker run -d --name gb-orb \
              --restart unless-stopped \
              --network name=gitberth,alias=orb \
              --label gitberth.managed=true \
              --label gitberth.app=orb \
              -p 3000:80 \
              gitberth/orb:1d2948c
            """.Replace("\r\n", "\n"), ContainerSpecFactory.For(Orb(), "gitberth/orb:1d2948c", [], "gitberth").ToDisplayString());

        var kanban = Kanban();
        var env = kanban.EnvVars.OrderBy(v => v.Id).Select(v => new EnvValue(v.Key, _protector.Unprotect(v.ProtectedValue), v.IsSecret)).ToList();
        var spec = ContainerSpecFactory.For(kanban, "gitberth/kanban:425df8a", env, "gitberth");
        Assert.Equal("""
            docker run -d --name gb-kanban \
              --restart unless-stopped \
              --network name=gitberth,alias=kanban \
              --network name=media,gw-priority=-1,alias=kanban,alias=kanban-db \
              --label gitberth.managed=true \
              --label gitberth.app=kanban \
              -p 3001:3000 \
              --expose 9229 \
              -e NEXT_PUBLIC_SITE=https://harborlab.com \
              -e DATABASE_URL=•••• \
              -v gb-kanban-uploads:/app/uploads \
              --memory 512m \
              --cpus 1.5 \
              gitberth/kanban:425df8a
            """.Replace("\r\n", "\n"), spec.ToDisplayString());
        // The secret travels in the docker CLI's own environment, never on its command line.
        var (args, processEnv) = spec.BuildRunCommand();
        Assert.Contains("DATABASE_URL", args);
        Assert.DoesNotContain(args, a => a.Contains("s3cret", StringComparison.Ordinal));
        Assert.Equal("postgres://kanban:s3cret@postgres:5432/kanban", processEnv["DATABASE_URL"]);
        // A deploy's daemon with json-file gets its rotation, as in step 3.
        Assert.Contains("--log-opt max-size=10m", ContainerSpecFactory.For(Orb(), "gitberth/orb:1d2948c", [], "gitberth", ["max-size=10m", "max-file=3"]).ToDisplayString());
    }
}
