using Gitberth.Services;

namespace Gitberth.Tests;

public class ContainerLocksTests
{
    [Fact]
    public async Task A_second_change_waits_for_the_first()
    {
        var locks = new ContainerLocks();
        var first = await locks.AcquireAsync("postgres");

        Assert.True(locks.IsHeld("postgres"));
        Assert.Null(await locks.TryAcquireAsync("postgres"));
        // Each container has its own.
        using (var other = await locks.TryAcquireAsync("redis")) Assert.NotNull(other);
        Assert.False(locks.IsHeld("redis"));

        var second = locks.AcquireAsync("postgres");
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        first.Dispose();
        // Letting go twice doesn't free it for a third.
        first.Dispose();
        using var held = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(locks.IsHeld("postgres"));
        Assert.Null(await locks.TryAcquireAsync("postgres"));
    }
}
