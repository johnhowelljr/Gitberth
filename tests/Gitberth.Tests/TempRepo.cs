namespace Gitberth.Tests;

/// <summary>A throwaway folder that stands in for a cloned repo.</summary>
public sealed class TempRepo : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("gitberth-test-").FullName;

    public TempRepo With(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}
