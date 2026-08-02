using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Diagnostics;

/// <summary>
/// The mechanical backstop coverage-remediation-plan.md Phase 2.1 calls for: <c>CatalogBuilder</c>
/// and <c>TypedPredicateExtractor</c> both dispatch through ScriptDOM's <c>TSqlFragmentVisitor</c>,
/// which binds <c>Accept()</c> at compile time to the most specific <c>ExplicitVisit</c> overload
/// that exists - overriding only a shared base type (or only <c>CreateXStatement</c>) silently
/// never fires for a sibling <c>AlterXStatement</c>/<c>CreateOrAlterXStatement</c> node. That
/// exact shape of bug hit procedures (fixed in the original audit), triggers (fixed in this
/// plan), and views/functions in the lineage pass (also fixed in this plan). Rather than trust
/// the next one gets caught by a human re-reading the same twelve-line comment, this reflects
/// over the real ScriptDOM assembly, finds every CreateXStatement with an Alter/CreateOrAlter
/// sibling, and asserts each pass either overrides ExplicitVisit for every sibling or the
/// coverage matrix documents the gap by name.
/// </summary>
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

    /// <summary>
    /// A separate parity dimension from the Create/Alter one above: every concrete
    /// <see cref="StatementWithCtesAndXmlNamespaces"/> subtype (SELECT/INSERT/UPDATE/DELETE/
    /// MERGE, plus <see cref="SelectStatementSnippet"/>) must either push CTE scope
    /// (<c>ExplicitVisit</c> override calling <c>PushCteScope</c>) or be a documented,
    /// confirmed-unreachable exception - otherwise a CTE referenced from that statement kind's
    /// own query silently fails to resolve. Found the hard way: INSERT had no override at all in
    /// <c>NonSargablePredicateScanner</c> until this test was written (TypedPredicateExtractor
    /// already had one), so `WITH cte AS (...) INSERT INTO t SELECT ... FROM cte WHERE
    /// UPPER(cte.Col) = 'x'` failed to resolve <c>cte</c> in FromScopeResolver there.
    /// </summary>
    [Fact]
    public void TypedPredicateExtractor_PushesCteScopeForEveryConcreteCteBearingStatement()
    {
        AssertHandlesEveryCteBearingStatement(GetExplicitVisitParameterTypeNames(typeof(TypedPredicateExtractor)));
    }

    [Fact]
    public void NonSargablePredicateScanner_PushesCteScopeForEveryConcreteCteBearingStatement()
    {
        AssertHandlesEveryCteBearingStatement(GetExplicitVisitParameterTypeNames(typeof(NonSargablePredicateScanner)));
    }

    private static readonly Type CteBearingStatementBaseType =
        ScriptDomAssembly.GetType("Microsoft.SqlServer.TransactSql.ScriptDom.StatementWithCtesAndXmlNamespaces")!;

    /// <summary>
    /// <see cref="SelectStatementSnippet"/> is a distinct ScriptDOM node from <see cref="SelectStatement"/>
    /// (its own <c>ExplicitVisit</c> overload on <c>TSqlFragmentVisitor</c>, not reached by a plain
    /// <c>ExplicitVisit(SelectStatement)</c> override), but it is only ever produced by ScriptDOM's
    /// incremental/snippet parsing APIs (IntelliSense-style partial-statement parsing) - never by
    /// <see cref="Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser.Parse(System.IO.TextReader,out System.Collections.Generic.IList{ParseError})"/>,
    /// the only entry point <c>SqlScriptParser</c> (and therefore every pass in this codebase) uses.
    /// Confirmed unreachable from real parsed T-SQL, not merely unhandled.
    /// </summary>
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

        // Only genuine "CreateXStatement" names seed a family - "CreateOrAlterXStatement" is
        // itself a sibling checked FROM that seed, not a second seed (its stem extraction would
        // otherwise produce the nonsensical "OrAlterXStatement").
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

                // An abstract type (e.g. AlterTableStatement, the base of
                // AlterTableAddTableElementStatement/AlterColumnStatement/... - ALTER TABLE's
                // ScriptDOM naming doesn't mirror CREATE TABLE 1:1) can never be the concrete
                // type ScriptDOM's parser actually constructs, so double-dispatch could never
                // target it - not a real gap, just this heuristic's naive string-stem match
                // colliding with an unrelated base class that happens to share a name pattern.
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

    /// <summary>
    /// A matrix row only excuses a code gap when it currently CLAIMS one - Gap or Ledgered. A
    /// Handled row mentioning this type name is not an excuse, it's a contradiction: the matrix
    /// says the code handles it, reflection just proved it doesn't. Matching on status here is
    /// what stops the exact failure mode this test itself hit while being written - a stale
    /// "Gap" row for CreateOrAlterTriggerStatement kept citing a defect the code had already
    /// fixed, which silently satisfied an earlier, looser version of this check.
    /// </summary>
    private static bool IsDocumentedInCoverageMatrix(string typeName) =>
        ConstructCoverageCatalog.Instance.Entries.Any(e =>
            e.Status != ConstructCoverageStatus.Handled &&
            (e.Construct.Contains(typeName, StringComparison.Ordinal) ||
             (e.Rationale is { } r && r.Contains(typeName, StringComparison.Ordinal))));

    /// <summary>
    /// Every concrete <see cref="TSqlStatement"/>-derived type <paramref name="passType"/> (or any
    /// nested private type inside it, e.g. a <c>Visitor</c> class) overrides
    /// <c>ExplicitVisit(T node)</c> for - the same signal ScriptDOM's own dispatch uses, so this
    /// is exact, not a heuristic.
    /// <c>BindingFlags.DeclaredOnly</c> is load-bearing here, not decorative: without it,
    /// <c>GetMethods()</c> on a type deriving from <c>TSqlFragmentVisitor</c> returns every
    /// INHERITED no-op <c>ExplicitVisit</c> overload the base class itself declares for every
    /// statement type in ScriptDOM - which would make this method report every statement kind
    /// as "handled" regardless of whether the pass actually overrides it, defeating the whole
    /// point (caught by manually disabling a real override while writing this test and watching
    /// the assertion still pass).
    /// </summary>
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
