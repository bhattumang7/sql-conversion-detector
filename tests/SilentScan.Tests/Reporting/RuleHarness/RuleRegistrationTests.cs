using SilentScan.Core.Reporting.RuleHarness;

namespace SilentScan.Tests.Reporting.RuleHarness;

public sealed class RuleRegistrationTests
{
    [Fact]
    public void EveryIRuleImplementationInCoreIsRegistered()
    {
        var assembly = typeof(RuleRegistry).Assembly;
        var ruleTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IRule).IsAssignableFrom(t))
            .ToList();

        var registeredTypes = RuleRegistry.All.Select(r => r.GetType()).ToHashSet();

        var unregistered = ruleTypes.Where(t => !registeredTypes.Contains(t)).ToList();

        Assert.True(unregistered.Count == 0,
            "IRule implementations exist that are never registered in RuleRegistry.All: " +
            string.Join(", ", unregistered.Select(t => t.FullName)));
    }

    [Fact]
    public void EveryRegisteredRuleIdIsUnique()
    {
        var duplicates = RuleRegistry.All
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Duplicate rule ids in RuleRegistry.All: " + string.Join(", ", duplicates));
    }
}
