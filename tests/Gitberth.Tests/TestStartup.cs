using System.Runtime.CompilerServices;

namespace Gitberth.Tests;

/// <summary>What the test run needs before its first test.</summary>
internal static class TestStartup
{
    /// <summary>
    /// More threads ready in the pool from the start. xUnit runs the test classes side by side, and code that blocks on
    /// async work (the in-memory engines, Entries' flush) holds threads meanwhile: with the pool's usual minimum (one per
    /// core), a burst of them waits while the pool grows a thread at a time, and a test's background work can take seconds
    /// to start. That's never a test's fault, and it hides nothing: it only takes the starving out.
    /// </summary>
#pragma warning disable CA2255 // A test assembly is the one place a module initializer belongs: it runs before any test.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void ThreadsReady()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        var wanted = Math.Max(workers, 4 * Environment.ProcessorCount);
        ThreadPool.SetMinThreads(wanted, Math.Max(io, wanted));
    }
}
