namespace Gitberth.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3+0123456789abcdef", "1.2.3", "0123456")]
    [InlineData("1.2.3-rc.1+abc", "1.2.3-rc.1", "abc")]
    [InlineData("1.0.0", "1.0.0", "")]
    [InlineData("", "0.0.0", "")]
    public void Parse_splits_the_number_from_a_short_commit(string informational, string number, string commit)
    {
        Assert.Equal((number, commit), AppVersion.Parse(informational));
    }

    [Fact]
    public void The_running_build_has_a_number()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", AppVersion.Number);
        Assert.StartsWith(AppVersion.Number, AppVersion.Display);
    }
}
