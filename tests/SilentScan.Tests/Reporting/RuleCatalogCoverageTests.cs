using System.Reflection;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Tests.Reporting;

public sealed class RuleCatalogCoverageTests
{
    [Fact]
    public void EveryEmittableRuleId_HasAMatchingRuleCatalogEntry()
    {
        var knownIds = new HashSet<string>(RuleCatalog.BaseRules.Select(r => r.Id), StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var method in RuleIdGeneratorMethods())
        {
            var enumType = method.GetParameters()[0].ParameterType;
            foreach (var value in Enum.GetValues(enumType))
            {
                var ruleId = (string)method.Invoke(null, [value])!;
                if (!knownIds.Contains(ruleId))
                {
                    missing.Add($"{method.Name}({enumType.Name}.{value}) -> \"{ruleId}\"");
                }
            }
        }

        Assert.True(missing.Count == 0, "Rule id(s) with no matching RuleCatalog.BaseRules entry:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void RuleIdGeneratorMethods_FindsAtLeastTwentyMethods()
    {

        Assert.True(RuleIdGeneratorMethods().Count >= 20, $"Expected at least 20 rule-id generator methods, found {RuleIdGeneratorMethods().Count} - the reflection filter may no longer match SarifRuleCatalog's method shape.");
    }

    private static List<MethodInfo> RuleIdGeneratorMethods() =>
        [.. typeof(SarifRuleCatalog)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(string) && m.GetParameters() is [{ } p] && p.ParameterType.IsEnum)];
}
