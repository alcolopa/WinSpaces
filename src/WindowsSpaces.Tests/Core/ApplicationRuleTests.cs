using WindowsSpaces.Core;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class ApplicationRuleTests
{
    [Fact]
    public void Matches_ProcessPathSubstring_ReturnsTrue()
    {
        var rule = new ApplicationRule("1", "Slack Rule", "slack.exe", null, null, "MON-1", 2);

        Assert.True(rule.Matches("C:\\Users\\emili\\AppData\\Local\\slack\\slack.exe", "SlackClass", "Slack"));
    }

    [Fact]
    public void Matches_ProcessPathDifferent_ReturnsFalse()
    {
        var rule = new ApplicationRule("1", "Slack Rule", "slack.exe", null, null, "MON-1", 2);

        Assert.False(rule.Matches("C:\\Windows\\notepad.exe", "Notepad", "Untitled - Notepad"));
    }

    [Fact]
    public void Matches_WindowClassExact_ReturnsTrue()
    {
        var rule = new ApplicationRule("2", "Notepad Class Rule", null, "Notepad", null, "MON-1", 1);

        Assert.True(rule.Matches("C:\\Windows\\notepad.exe", "Notepad", "Untitled"));
        Assert.True(rule.Matches(null, "notepad", null)); // Case-insensitive matching
    }

    [Fact]
    public void Matches_WindowTitleSubstring_ReturnsTrue()
    {
        var rule = new ApplicationRule("3", "Dev Title Rule", null, null, "VS Code", "MON-2", 3);

        Assert.True(rule.Matches("C:\\Path\\code.exe", "Chrome_WidgetWin_1", "Project - VS Code - Workspace"));
    }

    [Fact]
    public void Matches_EmptyRuleCriteria_ReturnsFalse()
    {
        var rule = new ApplicationRule("4", "Empty Rule", null, null, null, "MON-1", 1);

        Assert.False(rule.Matches("C:\\Windows\\notepad.exe", "Notepad", "Untitled"));
    }
}
