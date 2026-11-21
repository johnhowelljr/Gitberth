namespace Gitberth.Tests;

/// <summary>
/// The one wait for something that happens in the background and has no event to wait on: the condition is checked every
/// 25 ms until it holds. Its limit only bounds a hang, so it's generous: a passing run never waits it out, and a run on a
/// busy machine (every test class at once, the server under load) still passes. A test never asserts after a fixed sleep;
/// a retry schedule is played with <see cref="HeldClock"/> or <see cref="FakeClock"/>, an audit entry waited for with
/// <see cref="AuditWaits.RecordedAsync"/>.
/// </summary>
internal static class Eventually
{
    /// <summary>How long a condition may take: a guard against a hang, never what a passing test waits.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(25);

    public static Task UntilAsync(Func<bool> check, string what, TimeSpan? limit = null) =>
        UntilAsync(() => Task.FromResult(check()), what, limit);

    public static async Task UntilAsync(Func<Task<bool>> check, string what, TimeSpan? limit = null) =>
        await UntilAsync<object>(async () => await check() ? (object)true : null, what, limit);

    /// <summary>The first thing <paramref name="check"/> gives that isn't null.</summary>
    public static async Task<T> UntilAsync<T>(Func<Task<T?>> check, string what, TimeSpan? limit = null) where T : class
    {
        var within = limit ?? Limit;
        var until = DateTime.UtcNow + within;
        while (true)
        {
            if (await check() is { } done) return done;
            if (DateTime.UtcNow > until) Assert.Fail($"Waited {within.TotalSeconds:0} s for {what}, and it didn't happen.");
            await Task.Delay(Every);
        }
    }
}
