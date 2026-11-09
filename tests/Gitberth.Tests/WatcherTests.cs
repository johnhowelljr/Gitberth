using Gitberth.Components.Shared;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Network;

namespace Gitberth.Tests;

public class WatcherTests
{
    private const string Running = "1111111111111111111111111111111111111111";
    private const string Broken = "2222222222222222222222222222222222222222";
    private const string Newer = "3333333333333333333333333333333333333333";

    [Fact]
    public void Deploys_a_new_commit() =>
        Assert.True(RepoWatcher.ShouldDeploy(new AppDefinition { CurrentCommit = Running, LastAttemptedCommit = Running }, Newer, deployInProgress: false));

    [Fact]
    public void Leaves_the_running_commit_alone() =>
        Assert.False(RepoWatcher.ShouldDeploy(new AppDefinition { CurrentCommit = Running, LastAttemptedCommit = Running }, Running, deployInProgress: false));

    [Fact]
    public void Never_retries_a_commit_that_failed() =>
        Assert.False(RepoWatcher.ShouldDeploy(new AppDefinition { CurrentCommit = Running, LastAttemptedCommit = Broken }, Broken, deployInProgress: false));

    [Fact]
    public void Does_not_undo_a_rollback()
    {
        // Rolled back to an older image: the branch tip is the commit that was tried last, so leave it.
        var app = new AppDefinition { CurrentCommit = Running, LastAttemptedCommit = Broken };
        Assert.False(RepoWatcher.ShouldDeploy(app, Broken, deployInProgress: false));
        Assert.True(RepoWatcher.ShouldDeploy(app, Newer, deployInProgress: false));
    }

    [Fact]
    public void Waits_while_a_deploy_is_running() =>
        Assert.False(RepoWatcher.ShouldDeploy(new AppDefinition { CurrentCommit = Running }, Newer, deployInProgress: true));

    [Fact]
    public void Apps_from_before_the_watcher_do_not_redeploy_their_running_commit() =>
        Assert.False(RepoWatcher.ShouldDeploy(new AppDefinition { CurrentCommit = Running, LastAttemptedCommit = null }, Running, deployInProgress: false));

    private const string ThisBuild = "build-new";
    private static readonly DateTime Now = new(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc);
    private static readonly AppDefinition AfterFailure = new() { CurrentCommit = Running, LastAttemptedCommit = Broken };

    private static LastAttempt Failed(string build, int minutesAgo, int failuresOnThisBuild = 1, DeployStatus status = DeployStatus.Failed, DeployTrigger trigger = DeployTrigger.Poll) =>
        new(Broken, status, trigger, build, Now.AddMinutes(-minutesAgo), failuresOnThisBuild);

    [Fact]
    public void A_failed_build_is_retried_after_a_gitberth_update() =>
        Assert.True(RepoWatcher.ShouldRetry(AfterFailure, Broken, Failed("build-old", minutesAgo: 1, failuresOnThisBuild: 0), ThisBuild, Now));

    [Fact]
    public void A_failed_build_from_before_builds_were_recorded_is_retried_once() =>
        Assert.True(RepoWatcher.ShouldRetry(AfterFailure, Broken, Failed(null!, minutesAgo: 1, failuresOnThisBuild: 0), ThisBuild, Now));

    [Fact]
    public void A_failed_build_is_retried_once_after_a_pause()
    {
        Assert.False(RepoWatcher.ShouldRetry(AfterFailure, Broken, Failed(ThisBuild, minutesAgo: 5), ThisBuild, Now));
        Assert.True(RepoWatcher.ShouldRetry(AfterFailure, Broken, Failed(ThisBuild, minutesAgo: 15), ThisBuild, Now));
        // The retry failed too: wait for a new commit or the next Gitberth update.
        Assert.False(RepoWatcher.ShouldRetry(AfterFailure, Broken, Failed(ThisBuild, minutesAgo: 60, failuresOnThisBuild: 2), ThisBuild, Now));
    }

    [Fact]
    public void A_retry_that_fails_waits_for_a_new_commit_or_the_next_update()
    {
        // Once per Gitberth update, not twice: the update's retry failing doesn't earn another try 15 minutes later.
        Assert.False(RepoWatcher.ShouldRetry(AfterFailure, Broken, Failed(ThisBuild, minutesAgo: 60, trigger: DeployTrigger.Retry), ThisBuild, Now));
        Assert.True(RepoWatcher.ShouldRetry(AfterFailure, Broken, Failed("build-old", minutesAgo: 60, trigger: DeployTrigger.Retry), ThisBuild, Now));
    }

    [Fact]
    public void Rolled_back_deploys_and_manual_rollbacks_are_left_alone()
    {
        // Retrying a deploy that started and crashed would take the running app down again.
        Assert.False(RepoWatcher.ShouldRetry(AfterFailure, Broken, Failed("build-old", minutesAgo: 60, status: DeployStatus.RolledBack), ThisBuild, Now));
        Assert.False(RepoWatcher.ShouldRetry(AfterFailure, Broken, Failed("build-old", minutesAgo: 60, trigger: DeployTrigger.Rollback), ThisBuild, Now));
    }

    [Fact]
    public void Only_the_branch_tip_is_retried_and_only_when_it_is_not_running()
    {
        Assert.False(RepoWatcher.ShouldRetry(AfterFailure, Newer, Failed("build-old", minutesAgo: 60), ThisBuild, Now));
        Assert.False(RepoWatcher.ShouldRetry(new AppDefinition { CurrentCommit = Broken, LastAttemptedCommit = Broken }, Broken, Failed("build-old", minutesAgo: 60), ThisBuild, Now));
        Assert.False(RepoWatcher.ShouldRetry(AfterFailure, Broken, null, ThisBuild, Now));
    }
}

public class PortFollowingTests
{
    [Theory]
    [InlineData(80, null, "NodeSpa", 3000, 3000)]   // planner to Next.js website, added before ports were remembered
    [InlineData(3000, 3000, "NextJs", 8080, 8080)]  // the app's EXPOSE changed
    [InlineData(5000, 3000, "NextJs", 8080, null)]  // set by hand: stays
    [InlineData(5000, null, "NodeSpa", 3000, null)] // set by hand before ports were remembered: stays
    [InlineData(80, null, "Dockerfile", 3000, null)] // unknown what was picked: stays
    [InlineData(3000, 3000, "NextJs", 3000, null)]  // unchanged
    public void A_port_gitberth_picked_follows_the_app(int current, int? lastDetected, string lastKind, int detected, int? expected) =>
        Assert.Equal(expected, DeployPipeline.PortToFollow(current, lastDetected, lastKind, detected));

    [Fact]
    public void The_web_port_follows_detection_with_several_ports()
    {
        PortMapping[] ports =
        [
            new() { Id = 1, ContainerPort = 53, HostPort = 5353, Protocol = "udp" },
            new() { Id = 2, ContainerPort = 3000, HostPort = 3001, IsWeb = true },
            new() { Id = 3, ContainerPort = 9229, Access = PortAccess.Internal },
        ];

        // The web port moves, not the first one, however many the app has.
        var moved = DeployPipeline.Follow(ports, lastDetected: 3000, "NextJs", detected: 8080);
        Assert.Equal(((int?)2, (int?)8080), (moved?.Web.Id, moved?.To));
        // One set by hand stays, and so does one that would land on another of the app's ports, UDP or not.
        Assert.Null(DeployPipeline.Follow(ports, lastDetected: 5000, "NextJs", detected: 8080));
        Assert.Null(DeployPipeline.Follow(ports, lastDetected: 3000, "NextJs", detected: 9229));
        Assert.Null(DeployPipeline.Follow(ports, lastDetected: 3000, "NextJs", detected: 53));
        // A worker without a web port has nothing to follow the app.
        Assert.Null(DeployPipeline.Follow([ports[0], ports[2]], lastDetected: 3000, "NextJs", detected: 8080));
    }

    [Fact]
    public void A_build_still_needs_one_when_its_main_domain_moved_while_it_ran()
    {
        // Nothing saved meanwhile: the build made it all live.
        Assert.False(DeployPipeline.StillNeedsBuild("main#web", "main#web", vercel: true, "app.harborlab.com", "app.harborlab.com"));
        // Another domain made main while it built: the image has the old one in Vercel's variables.
        Assert.True(DeployPipeline.StillNeedsBuild("main#web", "main#web", vercel: true, "app.harborlab.com", "harborlab.com"));
        // Without Vercel's variables the image doesn't have the domain.
        Assert.False(DeployPipeline.StillNeedsBuild("main#web", "main#web", vercel: false, null, "harborlab.com"));
        // Build settings saved differently while it ran; the app deleted meanwhile.
        Assert.True(DeployPipeline.StillNeedsBuild("main#web", "dev#web", vercel: false, null, null));
        Assert.False(DeployPipeline.StillNeedsBuild("main#web", null, vercel: true, "app.harborlab.com", null));
    }
}

public class LogLineTests
{
    [Theory]
    [InlineData("13:24:25 ==> Cloning acme/web (main)", "13:24:25", "Cloning acme/web (main)", "step")]
    [InlineData("13:24:30 ==> Deploy succeeded", "13:24:30", "Deploy succeeded", "ok")]
    [InlineData("13:24:30 ==> Failed: docker run failed", "13:24:30", "Failed: docker run failed", "error")]
    [InlineData("13:24:30 ==> Rolled back to the previous container.", "13:24:30", "Rolled back to the previous container.", "warn")]
    [InlineData("13:24:26   | FROM nginx:stable-alpine", "13:24:26", "FROM nginx:stable-alpine", "dim")]
    [InlineData("13:24:27 #5 [1/3] FROM docker.io/library/nginx", "13:24:27", "#5 [1/3] FROM docker.io/library/nginx", "stage")]
    [InlineData("13:24:27 warning: no lockfile", "13:24:27", "warning: no lockfile", "warn")]
    [InlineData("13:24:27 #9 ERROR: process did not complete", "13:24:27", "#9 ERROR: process did not complete", "error")]
    [InlineData("plain line without a time", null, "plain line without a time", "plain")]
    public void Parses(string raw, string? time, string text, string kind)
    {
        var line = LogLine.Parse(raw);
        Assert.Equal(time, line.Time);
        Assert.Equal(text, line.Text);
        Assert.Equal(kind, line.Kind);
    }
}
