namespace Gitberth.Previews;

/// <summary>
/// One change to previews at a time, whoever makes it: the watcher's pass, a removal by hand, a settings save, a promote,
/// and an app deleted with its previews. So a pass never makes a preview of an app while that app's previews are being
/// removed with it.
/// </summary>
public sealed class PreviewGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task WaitAsync(CancellationToken ct = default) => _gate.WaitAsync(ct);

    public void Release() => _gate.Release();
}
