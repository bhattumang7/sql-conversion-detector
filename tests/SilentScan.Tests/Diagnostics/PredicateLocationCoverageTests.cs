using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Diagnostics;

public sealed class PredicateLocationCoverageTests
{
    private static readonly Assembly CoreAssembly = typeof(TypedPredicateExtractor).Assembly;

    private static readonly string[] PredicateLocationScopeHookNames =
    [
        "OnQuerySpecificationScope",
        "OnUpdateStatementScope",
        "OnDeleteStatementScope",
    ];

    private static readonly HashSet<string> SharedWalkerScannerVisitorTypeNames = new(StringComparer.Ordinal)
    {
        "SilentScan.Core.Predicates.CatchAllPredicateScanner+Visitor",
        "SilentScan.Core.Predicates.FloatEqualityPredicateScanner+Visitor",
        "SilentScan.Core.Predicates.NotInNullableSubqueryScanner+Visitor",
        "SilentScan.Core.Predicates.OperandComparabilityScanner+Visitor",
        "SilentScan.Core.Predicates.TryCastComputedColumnPredicateScanner+Visitor",
    };

    private static readonly HashSet<string> HandRolledJoinWalkScannerVisitorTypeNames = new(StringComparer.Ordinal)
    {
        "SilentScan.Core.Predicates.NonSargablePredicateScanner+Visitor",
        "SilentScan.Core.Predicates.TypedPredicateExtractor+Visitor",
    };

    private static readonly (string MethodName, Type ParameterType)[] RequiredHandRolledOverrides =
    [
        ("ExplicitVisit", typeof(WhereClause)),
        ("ExplicitVisit", typeof(HavingClause)),
        ("ExplicitVisit", typeof(QualifiedJoin)),
    ];

    private static readonly (string TypeName, string MethodName) FlowDrivenPredicateLocationSites =
        ("SilentScan.Core.Predicates.ParameterReassignmentPredicateScanner+Visitor", "InspectStatementForFindings");

    private static readonly (string TypeName, string MethodName)[] DmlTargetScopeHookSites =
    [
        ("SilentScan.Core.Predicates.SelfReferencingDmlScanner+Visitor", "OnUpdateStatementScope"),
        ("SilentScan.Core.Predicates.SelfReferencingDmlScanner+Visitor", "OnDeleteStatementScope"),
        ("SilentScan.Core.Predicates.SelfReferencingDmlScanner+Visitor", "OnMergeStatementScope"),
    ];

    private static readonly string[] HandRolledScopeResolutionCalleeNames = ["Resolve", "ResolveForDataModification", "ResolveForMerge"];

    [Fact]
    public void SharedWalkerScanners_RouteEveryOverriddenScopeHookThroughInspectAllPredicateLocations()
    {
        var gaps = new List<string>();

        foreach (var typeName in SharedWalkerScannerVisitorTypeNames)
        {
            var visitorType = ResolveRegisteredType(typeName);

            foreach (var hookName in PredicateLocationScopeHookNames)
            {
                var hook = visitorType.GetMethod(
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
    public void HandRolledJoinWalkScanners_StillOverrideEveryPredicateLocationEntryPoint()
    {
        var gaps = new List<string>();

        foreach (var typeName in HandRolledJoinWalkScannerVisitorTypeNames)
        {
            var visitorType = ResolveRegisteredType(typeName);

            foreach (var (methodName, parameterType) in RequiredHandRolledOverrides)
            {
                var method = visitorType.GetMethod(
                    methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null, [parameterType], modifiers: null);

                if (method is null)
                {
                    gaps.Add($"{typeName} no longer overrides {methodName}({parameterType.Name})");
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
