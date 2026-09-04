using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class FullTextPredicateInAggregateScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private static readonly HashSet<string> AggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "COUNT", "COUNT_BIG", "MIN", "MAX", "STRING_AGG",
        "VAR", "VARP", "STDEV", "STDEVP", "CHECKSUM_AGG",
    };

    public static IReadOnlyList<FullTextPredicateInAggregateFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<FullTextPredicateInAggregateFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private sealed class FullTextPredicateFinder : TSqlFragmentVisitor
    {
        public FullTextPredicate? Found { get; private set; }

        public override void ExplicitVisit(FullTextPredicate node) => Found ??= node;

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Critical Code Smell", "S1186:Methods should not be empty",
            Justification = "Deliberately stops descent into a nested subquery so a FullTextPredicate belonging to an inner scope is never matched to this function call's aggregate.")]
        public override void ExplicitVisit(ScalarSubquery node)
        {
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Critical Code Smell", "S1186:Methods should not be empty",
            Justification = "Deliberately stops descent into a nested query specification so a FullTextPredicate belonging to an inner scope is never matched to this function call's aggregate.")]
        public override void ExplicitVisit(QuerySpecification node)
        {
        }
    }

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<FullTextPredicateInAggregateFinding> Findings { get; } = [];

        public void OnEnterFunctionCall(FunctionCall node, ModuleWalker walker)
        {
            if (node.CallTarget is not null || node.OverClause is not null || node.FunctionName?.Value is not { } functionName
                || !AggregateFunctionNames.Contains(functionName))
            {
                return;
            }

            foreach (var parameter in node.Parameters)
            {
                var finder = new FullTextPredicateFinder();
                parameter.Accept(finder);
                if (finder.Found is { } predicate)
                {
                    var fullTextFunctionName = predicate.FullTextFunctionType == FullTextFunctionType.FreeText ? "FREETEXT" : "CONTAINS";
                    Findings.Add(new FullTextPredicateInAggregateFinding(
                        functionName.ToUpperInvariant(), fullTextFunctionName, sourcePath, node.StartLine, node.StartColumn));
                    break;
                }
            }
        }
    }
}
