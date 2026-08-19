using System.Reflection;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Tests.Reporting;

/// <summary>
/// Pins the catalog-to-producer link SARIF's own docs-regeneration test (RulesDocGeneratorTests)
/// enforces only for the OTHER direction (docs/rules.html never drifting from RuleCatalog) -
/// this is the missing forward direction: every rule id one of SarifRuleCatalog's ~36 enum-keyed
/// *RuleId(kind) methods can actually EMIT must have a matching RuleCatalog.BaseRules entry, or a
/// SARIF result's own ruleId would point at a rule the "rules" block (and docs/rules.html) never
/// describes. Reflects over every SarifRuleCatalog method shaped exactly like a rule-id generator
/// (public static string, single enum parameter) and every value of that enum - the same
/// reflection-over-a-closed-set forcing-function pattern
/// ColumnProvenanceSubtypeCoverageTests already uses for ColumnProvenance's own subtypes, applied
/// here to catch the same class of drift: a new enum member added to a *FindingKind type without a
/// matching RuleCatalog entry.
/// </summary>
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
        // A regression guard for the reflection filter itself - if SarifRuleCatalog's method
        // naming/shape convention ever changes, this fails loudly (0 or a suspiciously low count)
        // instead of the coverage test above silently checking nothing.
        Assert.True(RuleIdGeneratorMethods().Count >= 20, $"Expected at least 20 rule-id generator methods, found {RuleIdGeneratorMethods().Count} - the reflection filter may no longer match SarifRuleCatalog's method shape.");
    }

    private static List<MethodInfo> RuleIdGeneratorMethods() =>
        [.. typeof(SarifRuleCatalog)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(string) && m.GetParameters() is [{ } p] && p.ParameterType.IsEnum)];
}
