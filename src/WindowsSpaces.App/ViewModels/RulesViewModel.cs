using WindowsSpaces.Core;

namespace WindowsSpaces.App.ViewModels;

public sealed class RulesViewModel
{
    private readonly AppConfiguration _original;
    private readonly List<ApplicationRule> _rules;

    public RulesViewModel(AppConfiguration current)
    {
        _original = current;
        _rules = current.ActiveRules.ToList();
    }

    public IReadOnlyList<ApplicationRule> Rules => _rules;

    public void AddRule(string targetMonitorId)
    {
        var id = Guid.NewGuid().ToString("N");
        var nextRuleNumber = _rules.Count + 1;
        var rule = new ApplicationRule(
            Id: id,
            RuleName: $"New Rule {nextRuleNumber}",
            ProcessPath: null,
            WindowClass: null,
            WindowTitle: null,
            TargetMonitorId: targetMonitorId,
            TargetWorkspaceIndex: 1
        );
        _rules.Add(rule);
    }

    public void RemoveRule(string ruleId)
    {
        var index = _rules.FindIndex(r => r.Id == ruleId);
        if (index >= 0)
        {
            _rules.RemoveAt(index);
        }
    }

    public void UpdateRule(
        string ruleId,
        string ruleName,
        string? processPath,
        string? windowClass,
        string? windowTitle,
        string targetMonitorId,
        int targetWorkspaceIndex)
    {
        var index = _rules.FindIndex(r => r.Id == ruleId);
        if (index >= 0)
        {
            var cleanProcessPath = string.IsNullOrWhiteSpace(processPath) ? null : processPath.Trim();
            var cleanWindowClass = string.IsNullOrWhiteSpace(windowClass) ? null : windowClass.Trim();
            var cleanWindowTitle = string.IsNullOrWhiteSpace(windowTitle) ? null : windowTitle.Trim();

            _rules[index] = new ApplicationRule(
                Id: ruleId,
                RuleName: ruleName,
                ProcessPath: cleanProcessPath,
                WindowClass: cleanWindowClass,
                WindowTitle: cleanWindowTitle,
                TargetMonitorId: targetMonitorId,
                TargetWorkspaceIndex: targetWorkspaceIndex
            );
        }
    }

    public bool TrySave(out AppConfiguration updated, out string? error)
    {
        var candidate = _original with { Rules = _rules };
        if (!candidate.Validate(out error))
        {
            updated = _original;
            return false;
        }

        updated = candidate;
        return true;
    }
}
