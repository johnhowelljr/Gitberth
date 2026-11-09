using Gitberth.Sources;

namespace Gitberth.Tests;

/// <summary>A pasted command split into words as a POSIX shell would, with nothing run or looked up.</summary>
public class ShellWordsTests
{
    private static (IReadOnlyList<string> Words, IReadOnlyList<ParseIssue> Issues) Split(string text) => ShellWords.Split(text);

    [Fact]
    public void Reads_single_and_double_quotes()
    {
        var (words, issues) = Split("docker run -e \"A=b c\" -e 'D=$e and \"f\"' --name=\"my app\" nginx");

        Assert.Equal(["docker", "run", "-e", "A=b c", "-e", "D=$e and \"f\"", "--name=my app", "nginx"], words);
        // $e in single quotes is text, not a variable.
        Assert.Empty(issues);
    }

    [Fact]
    public void Reads_backslash_escapes_as_a_shell_does()
    {
        var (words, issues) = Split("echo a\\ b \"c\\\"d\" 'e\\f' \"g\\h\" \\$HOME");

        Assert.Equal(["echo", "a b", "c\"d", "e\\f", "g\\h", "$HOME"], words);
        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("docker run \\\n  -p 80:80 \\\n  nginx")]
    [InlineData("docker run \\\r\n  -p 80:80 \\\r\n  nginx\r\n")]
    [InlineData("docker run \\\r\n  -p 80:80 \\\r\n  nginx\r\n\r\n")]
    public void Reads_line_continuations_with_lf_or_crlf(string text)
    {
        var (words, issues) = Split(text);

        Assert.Equal(["docker", "run", "-p", "80:80", "nginx"], words);
        Assert.Empty(issues);
    }

    [Fact]
    public void Reads_a_backslash_with_spaces_after_it_as_a_continuation_and_says_so()
    {
        var (words, issues) = Split("docker run \\   \n  nginx");

        Assert.Equal(["docker", "run", "nginx"], words);
        Assert.Equal(ParseSeverity.Note, Assert.Single(issues).Severity);
    }

    [Theory]
    [InlineData("docker run `\r\n  -p 80:80 `\r\n  nginx", "` (PowerShell's)")]
    [InlineData("docker run ^\r\n  -p 80:80 ^\r\n  nginx", "^ (Windows' cmd)")]
    public void Reads_windows_line_continuations_with_a_note(string text, string how)
    {
        var (words, issues) = Split(text);

        Assert.Equal(["docker", "run", "-p", "80:80", "nginx"], words);
        Assert.Equal($"Lines ending in {how} are read as one command.", Assert.Single(issues).Text);
    }

    [Theory]
    [InlineData("docker run -e TOKEN=$(cat token.txt) nginx", "$(cat token.txt)")]
    [InlineData("docker run -e TOKEN=\"$(cat token.txt)\" nginx", "$(cat token.txt)")]
    [InlineData("docker run -e TOKEN=`cat token.txt` nginx", "`cat token.txt`")]
    [InlineData("docker run -e N=$((1 + 2)) nginx", "$((1 + 2))")]
    public void Command_substitution_is_an_error_and_is_never_run(string text, string fragment)
    {
        var (words, issues) = Split(text);

        var error = Assert.Single(issues);
        Assert.Equal((ParseSeverity.Error, "Command substitution isn't run: paste the command with its values.", fragment), (error.Severity, error.Text, error.Fragment));
        Assert.Equal("nginx", words[^1]);
    }

    [Fact]
    public void A_variable_is_kept_as_written_with_a_note_once()
    {
        var (words, issues) = Split("docker run -v $HOME/data:/data -e A=${PWD} -e B=$HOME nginx");

        Assert.Equal(["docker", "run", "-v", "$HOME/data:/data", "-e", "A=${PWD}", "-e", "B=$HOME", "nginx"], words);
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(ParseSeverity.Note, i.Severity));
        Assert.StartsWith("$HOME is kept as written", issues[0].Text);
        Assert.StartsWith("${PWD} is kept as written", issues[1].Text);
    }

    [Theory]
    [InlineData("docker pull nginx && docker run nginx", "&& docker run nginx")]
    [InlineData("docker run nginx; docker logs nginx", "; docker logs nginx")]
    [InlineData("docker run nginx || true", "|| true")]
    [InlineData("docker run nginx | tee out", "| tee out")]
    [InlineData("docker run nginx & docker ps", "& docker ps")]
    public void Two_commands_are_an_error(string text, string fragment)
    {
        var (_, issues) = Split(text);

        var error = Assert.Single(issues);
        Assert.Equal(ParseSeverity.Error, error.Severity);
        Assert.StartsWith("Paste one command", error.Text);
        Assert.Equal(fragment, error.Fragment);
    }

    [Fact]
    public void A_new_line_starts_another_command()
    {
        var (words, issues) = Split("docker run nginx\ndocker run redis\n");

        Assert.Equal(["docker", "run", "nginx"], words);
        Assert.Equal("Paste one command: the next line starts another one. End a line with \\ to carry on to the next.", Assert.Single(issues).Text);
    }

    [Theory]
    [InlineData("docker run nginx;")]
    [InlineData("docker run nginx &")]
    [InlineData("docker run nginx # the web server\n# and nothing else\n")]
    [InlineData("\n\n  docker run nginx  \n\n")]
    public void What_ends_one_command_is_no_second_one(string text)
    {
        var (words, issues) = Split(text);

        Assert.Equal(["docker", "run", "nginx"], words);
        Assert.Empty(issues);
    }

    [Fact]
    public void A_quote_that_is_not_closed_is_an_error()
    {
        var (_, issues) = Split("docker run -e \"A=b nginx");

        Assert.Equal("A quote isn't closed: add the \" that ends it.", Assert.Single(issues).Text);
    }

    [Fact]
    public void A_redirection_is_an_error_and_its_file_is_no_word()
    {
        var (words, issues) = Split("docker run nginx > out.log 2>&1");

        Assert.Equal(["docker", "run", "nginx"], words);
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(ParseSeverity.Error, i.Severity));
        Assert.Equal(["> out.log", ">&1"], issues.Select(i => i.Fragment));
    }

    [Fact]
    public void In_compose_a_new_line_is_a_space_and_a_variable_is_the_containers()
    {
        var (words, issues) = ShellWords.Split("sh -c\n'echo $HOME'\n--flag", commandLine: false);

        Assert.Equal(["sh", "-c", "echo $HOME", "--flag"], words);
        Assert.Empty(issues);
    }
}
