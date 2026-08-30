using System.Reflection;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Diagnostics;

public sealed class PredicateLocationCoverageTests
{
    private static readonly Assembly CoreAssembly = typeof(TypedPredicateExtractor).Assembly;

    private static readonly string[] ModuleWalkerPredicateLocationHookNames =
    [
        "OnEnterQuerySpecificationScope",
        "OnEnterUpdateStatementScope",
        "OnEnterDeleteStatementScope",
    ];

    private static readonly HashSet<string> ModuleWalkerRuleTypeNames = new(StringComparer.Ordinal)
    {
        "SilentScan.Core.Predicates.CatchAllPredicateScanner+Rule",
        "SilentScan.Core.Predicates.FloatEqualityPredicateScanner+Rule",
        "SilentScan.Core.Predicates.NotInNullableSubqueryScanner+Rule",
        "SilentScan.Core.Predicates.OperandComparabilityScanner+Rule",
        "SilentScan.Core.Predicates.TryCastComputedColumnPredicateScanner+Rule",
        "SilentScan.Core.Predicates.NonSargablePredicateScanner+Rule",
    };

    private static readonly (string TypeName, string MethodName) FlowDrivenPredicateLocationSites =
        ("SilentScan.Core.Predicates.ParameterReassignmentPredicateScanner+Rule", "InspectStatementForFindings");

    private static readonly (string TypeName, string MethodName)[] DmlTargetScopeHookSites =
    [
        ("SilentScan.Core.Predicates.SelfReferencingDmlScanner+Rule", "OnEnterUpdateStatementScope"),
        ("SilentScan.Core.Predicates.SelfReferencingDmlScanner+Rule", "OnEnterDeleteStatementScope"),
        ("SilentScan.Core.Predicates.SelfReferencingDmlScanner+Rule", "OnEnterMergeStatementScope"),
    ];

    private static readonly string[] HandRolledScopeResolutionCalleeNames = ["Resolve", "ResolveForDataModification", "ResolveForMerge"];

    [Fact]
    public void ModuleWalkerRules_RouteEveryOverriddenScopeHookThroughInspectAllPredicateLocations()
    {
        var gaps = new List<string>();

        foreach (var typeName in ModuleWalkerRuleTypeNames)
        {
            var ruleType = ResolveRegisteredType(typeName);

            foreach (var hookName in ModuleWalkerPredicateLocationHookNames)
            {
                var hook = ruleType.GetMethod(
                    hookName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                if (hook is not null && !IlCallGraph.Calls(hook, "InspectAllPredicateLocations"))
                {
                    gaps.Add($"{typeName}.{hookName} no longer calls InspectAllPredicateLocations");
                }
            }
        }

        Assert.True(gaps.Count == 0, string.Join("\n", gaps));
    }

    [Fact]
    public void FlowDrivenScanner_RoutesStatementInspectionThroughInspectAllPredicateLocations()
    {
        var (typeName, methodName) = FlowDrivenPredicateLocationSites;
        var visitorType = ResolveRegisteredType(typeName);
        var method = visitorType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            ?? throw new InvalidOperationException($"{typeName}.{methodName} no longer exists - update this test");

        Assert.True(
            IlCallGraph.Calls(method, "InspectAllPredicateLocations"),
            $"{typeName}.{methodName} no longer calls InspectAllPredicateLocations");
    }

    [Fact]
    public void DmlTargetScopeScanner_ConsumesSharedWalkerScopeInsteadOfHandRolledResolution()
    {
        var gaps = new List<string>();

        foreach (var (typeName, methodName) in DmlTargetScopeHookSites)
        {
            var visitorType = ResolveRegisteredType(typeName);
            var method = visitorType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? throw new InvalidOperationException($"{typeName}.{methodName} no longer exists - update this test");

            foreach (var calleeName in HandRolledScopeResolutionCalleeNames)
            {
                if (IlCallGraph.Calls(method, calleeName))
                {
                    gaps.Add($"{typeName}.{methodName} calls '{calleeName}' directly again instead of consuming the base class's scope chain");
                }
            }
        }

        Assert.True(gaps.Count == 0, string.Join("\n", gaps));
    }

    private static Type ResolveRegisteredType(string typeName) =>
        CoreAssembly.GetType(typeName) ?? throw new InvalidOperationException($"{typeName} no longer exists - update the registered scanner list in this test");
}
