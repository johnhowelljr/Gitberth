using System.Text.RegularExpressions;

namespace Gitberth.Tests;

/// <summary>
/// Since MudBlazor 9 a MudMenu's own ActivatorContent no longer opens the menu when it's clicked: the content gets a
/// MenuContext and has to call it. A menu whose activator doesn't is dead (every ⋯ button and the account menu were, for a
/// while). This reads every Razor file and holds each ActivatorContent to calling its context.
/// </summary>
public class MenuActivatorTests
{
    private static readonly Regex Activator = new(@"<ActivatorContent(?:\s+Context=""(?<name>\w+)"")?\s*>(?<body>.*?)</ActivatorContent>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void Every_menu_activator_opens_its_menu()
    {
        var found = 0;
        var dead = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Components(), "*.razor", SearchOption.AllDirectories))
        {
            foreach (Match m in Activator.Matches(File.ReadAllText(file)))
            {
                found++;
                var name = m.Groups["name"].Success ? m.Groups["name"].Value : "context";
                if (!Regex.IsMatch(m.Groups["body"].Value, $@"\b{name}\.(ToggleAsync|OpenAsync)\b"))
                    dead.Add($"{Path.GetFileName(file)}: {m.Groups["body"].Value.Trim().Split('\n')[0].Trim()}");
            }
        }
        Assert.True(found > 0, "no ActivatorContent found: the search is looking in the wrong place");
        Assert.Empty(dead);
    }

    private static string Components()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var components = Path.Combine(dir.FullName, "src", "Gitberth", "Components");
            if (Directory.Exists(components)) return components;
        }
        throw new DirectoryNotFoundException("src/Gitberth/Components wasn't found above " + AppContext.BaseDirectory);
    }
}
