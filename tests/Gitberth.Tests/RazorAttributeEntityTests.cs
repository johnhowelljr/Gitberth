using System.Text.RegularExpressions;

namespace Gitberth.Tests;

/// <summary>
/// An HTML entity in a Razor attribute's value, like placeholder="*.md&amp;#10;docs/", reaches the page as those
/// characters when Blazor builds the element attribute by attribute, as it does for one with @bind or any other directive,
/// and for a component's parameter: Ignored paths' and Paste a .env file's examples showed "&amp;#10;" for a while. This
/// reads every Razor file and holds every attribute's value to having none: a character the markup can't hold goes in from
/// C#, like placeholder="@Example".
/// </summary>
public class RazorAttributeEntityTests
{
    // name="…" with an entity inside the quotes: &#10;, &#x0A;, &amp; and the like.
    private static readonly Regex Attribute = new(
        @"(?<![\w-])(?<name>[A-Za-z][\w:.-]*)=""(?<value>[^""]*&(?:#\d+|#[xX][0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]*);[^""]*)""",
        RegexOptions.Compiled);

    [Fact]
    public void No_attribute_in_a_razor_file_holds_an_html_entity()
    {
        var files = 0;
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Components(), "*.razor", SearchOption.AllDirectories))
        {
            files++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in Attribute.Matches(lines[i]))
                    found.Add($"{Path.GetFileName(file)}:{i + 1}: {m.Groups["name"].Value}=\"{m.Groups["value"].Value}\"");
        }
        Assert.True(files > 0, "no Razor file found: the search is looking in the wrong place");
        Assert.Empty(found);
    }

    [Theory]
    [InlineData("""<textarea placeholder="*.md&#10;docs/" @bind="x"></textarea>""")]
    [InlineData("""<input title="Tom &amp; Jerry" />""")]
    [InlineData("""<Field Help="one&#x0A;two" />""")]
    public void The_check_finds_an_entity_in_an_attribute(string line) => Assert.Matches(Attribute, line);

    [Theory]
    [InlineData("""<textarea placeholder="@IgnoredExample" @bind="x"></textarea>""")]
    [InlineData("""<a href="/apps?sort=name&dir=asc">Apps</a>""")]
    [InlineData("""<Btn Disabled="@(a && b)">Go</Btn>""")]
    [InlineData("""<p>Tom &amp; Jerry</p>""")]
    public void The_check_leaves_other_text_alone(string line) => Assert.DoesNotMatch(Attribute, line);

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
