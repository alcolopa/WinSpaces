using System.Linq;
using WindowsSpaces.App.ViewModels;
using WindowsSpaces.Core;
using Xunit;
using Monitor = WindowsSpaces.Core.Monitor;

namespace WindowsSpaces.Tests.App;

public class RulesViewModelTests
{
    private static readonly Monitor MonA = new("MON-A", "\\\\.\\DISPLAY1", new System.Drawing.Rectangle(0, 0, 1920, 1080), IsPrimary: true);

    [Fact]
    public void AddRule_AddsNewRuleToCollection()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new RulesViewModel(config);

        vm.AddRule("MON-A");

        Assert.Single(vm.Rules);
        var rule = vm.Rules.First();
        Assert.StartsWith("New Rule", rule.RuleName);
        Assert.Equal("MON-A", rule.TargetMonitorId);
        Assert.Equal(1, rule.TargetWorkspaceIndex);
    }

    [Fact]
    public void RemoveRule_RemovesRuleFromCollection()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Rules = new[]
            {
                new ApplicationRule("rule-1", "Name 1", "notepad.exe", null, null, "MON-A", 1),
                new ApplicationRule("rule-2", "Name 2", "slack.exe", null, null, "MON-A", 2)
            }
        };
        var vm = new RulesViewModel(config);

        vm.RemoveRule("rule-1");

        Assert.Single(vm.Rules);
        Assert.Equal("rule-2", vm.Rules.First().Id);
    }

    [Fact]
    public void UpdateRule_ModifiesPropertiesAndCleansStrings()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA }) with
        {
            Rules = new[]
            {
                new ApplicationRule("rule-1", "Name 1", "notepad.exe", null, null, "MON-A", 1)
            }
        };
        var vm = new RulesViewModel(config);

        vm.UpdateRule("rule-1", "Updated Name", "  notepad2.exe  ", "  WindowClass  ", "  ", "MON-B", 2);

        var rule = vm.Rules.First();
        Assert.Equal("Updated Name", rule.RuleName);
        Assert.Equal("notepad2.exe", rule.ProcessPath);
        Assert.Equal("WindowClass", rule.WindowClass);
        Assert.Null(rule.WindowTitle); // Whitespace only gets normalized to null
        Assert.Equal("MON-B", rule.TargetMonitorId);
        Assert.Equal(2, rule.TargetWorkspaceIndex);
    }

    [Fact]
    public void TrySave_WithValidRules_ReturnsTrueAndUpdatedConfig()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new RulesViewModel(config);
        vm.AddRule("MON-A");
        vm.UpdateRule(vm.Rules.First().Id, "My rule", "notepad.exe", null, null, "MON-A", 1);

        var success = vm.TrySave(out var updated, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Single(updated.ActiveRules);
        Assert.Equal("My rule", updated.ActiveRules.First().RuleName);
    }

    [Fact]
    public void TrySave_WithInvalidRule_ReturnsFalseAndError()
    {
        var config = AppConfiguration.CreateDefault(new[] { MonA });
        var vm = new RulesViewModel(config);
        vm.AddRule("MON-A");
        // No criteria specified (notepad.exe, window class, and window title are all empty)
        vm.UpdateRule(vm.Rules.First().Id, "My rule", "", null, "   ", "MON-A", 1);

        var success = vm.TrySave(out _, out var error);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("no matching criteria", error);
    }
}
