using System.Reflection;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Sarif;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Reporting;

public sealed class RuleCatalogCoverageTests
{
    [Fact]
    public void EveryRuleIdGeneratorMethod_RejectsAnUnmappedEnumValueWithArgumentOutOfRangeException()
    {
        var failures = new List<string>();

        foreach (var method in RuleIdGeneratorMethods())
        {
            var enumType = method.GetParameters()[0].ParameterType;
            var unmapped = Enum.ToObject(enumType, UnusedUnderlyingValue(enumType));

            var invokeFailed = false;
            try
            {
                method.Invoke(null, [unmapped]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException)
            {
                invokeFailed = true;
            }

            if (!invokeFailed)
            {
                failures.Add($"{method.Name}({enumType.Name}) did not throw ArgumentOutOfRangeException for an unmapped value");
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void RuleId_UnmappedFindingConfidence_ThrowsArgumentOutOfRangeException()
    {
        var unmapped = (FindingConfidence)(-1);

        Assert.Throws<ArgumentOutOfRangeException>(() => SarifRuleCatalog.RuleId("silentscan/example/base-rule", unmapped));
    }

    [Theory]
    [InlineData(FindingConfidence.High, "silentscan/example/base-rule")]
    [InlineData(FindingConfidence.Medium, "silentscan/example/base-rule/medium-confidence")]
    [InlineData(FindingConfidence.Low, "silentscan/example/base-rule/low-confidence")]
    public void RuleId_KnownConfidence_AppendsExpectedSuffix(FindingConfidence confidence, string expected)
    {
        Assert.Equal(expected, SarifRuleCatalog.RuleId("silentscan/example/base-rule", confidence));
    }

    private static int UnusedUnderlyingValue(Type enumType)
    {
        var used = new HashSet<int>(Enum.GetValues(enumType).Cast<int>());
        var candidate = -1;
        while (used.Contains(candidate))
        {
            candidate--;
        }

        return candidate;
    }

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

    [Fact]
    public void AllRules_DynamicSqlOutcomeBaseRule_HasNoConfidenceVariants()
    {
        foreach (var outcome in Enum.GetValues<DynamicSqlOutcome>())
        {
            var baseId = SarifRuleCatalog.DynamicSqlRuleId(outcome);

            Assert.DoesNotContain(SarifRuleCatalog.AllRules, r => r.Id == SarifRuleCatalog.RuleId(baseId, FindingConfidence.Medium));
            Assert.DoesNotContain(SarifRuleCatalog.AllRules, r => r.Id == SarifRuleCatalog.RuleId(baseId, FindingConfidence.Low));
        }
    }

    [Fact]
    public void AllRules_NonDynamicSqlBaseRule_HasMediumAndLowConfidenceVariants()
    {
        Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == SarifRuleCatalog.RuleId(SarifRuleCatalog.FloatEqualityRuleId, FindingConfidence.Medium));
        Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == SarifRuleCatalog.RuleId(SarifRuleCatalog.FloatEqualityRuleId, FindingConfidence.Low));
    }

    [Fact]
    public void AllRules_MediumConfidenceVariant_DescriptionCarriesBaseRuleShortDescriptionWithConfidencePrefix()
    {
        var baseRule = SarifRuleCatalog.AllRules.Single(r => r.Id == SarifRuleCatalog.FloatEqualityRuleId);
        var mediumVariant = SarifRuleCatalog.AllRules.Single(r => r.Id == SarifRuleCatalog.RuleId(SarifRuleCatalog.FloatEqualityRuleId, FindingConfidence.Medium));

        Assert.Equal($"(Medium confidence) {baseRule.ShortDescription.Text}", mediumVariant.ShortDescription.Text);
    }
}
