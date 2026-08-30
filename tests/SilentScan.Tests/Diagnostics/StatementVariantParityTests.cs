using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Diagnostics;

public sealed class StatementVariantParityTests
{
    private static readonly Assembly ScriptDomAssembly = typeof(TSqlFragment).Assembly;

    private static readonly Type StatementBaseType = typeof(TSqlStatement);

    [Fact]
    public void CatalogBuilder_HandlesEveryAlterAndCreateOrAlterSiblingItHandlesTheCreateFormOf()
    {
        AssertNoUndocumentedGap(GetExplicitVisitParameterTypeNames(typeof(CatalogBuilder)));
    }

    [Fact]
    public void TypedPredicateExtractor_HandlesEveryAlterAndCreateOrAlterSiblingItHandlesTheCreateFormOf()
    {
        AssertNoUndocumentedGap(GetExplicitVisitParameterTypeNames(typeof(TypedPredicateExtractor)));
    }

    [Fact]
    public void ScopedSqlVisitorBase_PushesCteScopeForEveryConcreteCteBearingStatement()
    {
        var scopedSqlVisitorBase = typeof(TypedPredicateExtractor).Assembly.GetType("SilentScan.Core.Predicates.ScopedSqlVisitorBase")!;
        AssertHandlesEveryCteBearingStatement(GetExplicitVisitParameterTypeNames(scopedSqlVisitorBase));
    }

    private static readonly Type CteBearingStatementBaseType =
        ScriptDomAssembly.GetType("Microsoft.SqlServer.TransactSql.ScriptDom.StatementWithCtesAndXmlNamespaces")!;

    private static readonly HashSet<string> DocumentedUnreachableCteBearingTypes = new(StringComparer.Ordinal)
    {
        "SelectStatementSnippet",
    };

    private static void AssertHandlesEveryCteBearingStatement(HashSet<string> handledTypeNames)
    {
        var gaps = ScriptDomAssembly.GetTypes()
            .Where(t => !t.IsAbstract && CteBearingStatementBaseType.IsAssignableFrom(t))
            .Select(t => t.Name)
            .Where(name => !handledTypeNames.Contains(name) && !DocumentedUnreachableCteBearingTypes.Contains(name))
            .ToList();

        Assert.True(gaps.Count == 0, string.Join("\n", gaps.Select(g => $"{g} is a concrete CTE-bearing statement with no ExplicitVisit override and no documented unreachability")));
    }

    private static void AssertNoUndocumentedGap(HashSet<string> handledTypeNames)
    {
        var gaps = new List<string>();

        foreach (var createTypeName in handledTypeNames.Where(n =>
            n.StartsWith("Create", StringComparison.Ordinal) && !n.StartsWith("CreateOrAlter", StringComparison.Ordinal)))
        {
            var stem = createTypeName["Create".Length..];

            foreach (var siblingPrefix in new[] { "Alter", "CreateOrAlter" })
            {
                var siblingTypeName = siblingPrefix + stem;
                if (siblingTypeName == createTypeName || ScriptDomAssembly.GetType($"Microsoft.SqlServer.TransactSql.ScriptDom.{siblingTypeName}") is not { } siblingType)
                {
                    continue;
                }

                if (!StatementBaseType.IsAssignableFrom(siblingType))
                {
                    continue;
                }

                if (siblingType.IsAbstract)
                {
                    continue;
                }

                if (handledTypeNames.Contains(siblingTypeName))
                {
                    continue;
                }

                if (IsDocumentedInCoverageMatrix(siblingTypeName))
                {
                    continue;
                }

                gaps.Add($"{createTypeName} is handled but sibling {siblingTypeName} is neither handled nor documented as a gap in the coverage matrix");
            }
        }

        Assert.True(gaps.Count == 0, string.Join("\n", gaps));
    }

    private static bool IsDocumentedInCoverageMatrix(string typeName) =>
        ConstructCoverageCatalog.Instance.Entries.Any(e =>
            e.Status != ConstructCoverageStatus.Handled &&
            (e.Construct.Contains(typeName, StringComparison.Ordinal) ||
             (e.Rationale is { } r && r.Contains(typeName, StringComparison.Ordinal))));

    private static HashSet<string> GetExplicitVisitParameterTypeNames(Type passType)
    {
        var candidateTypes = passType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).Append(passType);

        return [.. candidateTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.Name == "ExplicitVisit")
            .Select(m => m.GetParameters().Single().ParameterType)
            .Where(t => StatementBaseType.IsAssignableFrom(t))
            .Select(t => t.Name)];
    }
}
