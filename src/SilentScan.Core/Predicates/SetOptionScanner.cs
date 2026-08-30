using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class SetOptionScanner
{
    private static readonly (SetOptions Flag, bool TriggerIsOn, SetOptionFindingKind Kind)[] SyntaxOnlyTriggers =
    [
        (SetOptions.NumericRoundAbort, true, SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature),
        (SetOptions.AnsiWarnings, false, SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature),
        (SetOptions.ConcatNullYieldsNull, false, SetOptionFindingKind.ConcatNullYieldsNullOffBlocksIndexedFeature),
        (SetOptions.AnsiPadding, false, SetOptionFindingKind.AnsiPaddingOffBlocksIndexedFeature),
    ];

    public static IReadOnlyList<SetOptionFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage)
    {
        var rule = CreateRule();
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(parseResult, catalog, lineage, rule);
    }

    internal static SetStatementRule CreateRule() => new();

    internal static IReadOnlyList<SetOptionFinding> Harvest(SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage, SetStatementRule rule)
    {
        var moduleQualifiedName = parseResult.SourcePath;
        var findings = new List<SetOptionFinding>();

        var isAdHocScript = IsAdHocScript(parseResult.Fragment);

        var quotedIdentifierOff = isAdHocScript
            ? SetOptionFlowTracker.ComputeFinalOffState(parseResult.Fragment, SetOptions.QuotedIdentifier)
            : catalog.TryGetModuleUsesQuotedIdentifier(moduleQualifiedName, out var usesQuotedIdentifier) && !usesQuotedIdentifier;

        var ansiNullsOff = isAdHocScript
            ? SetOptionFlowTracker.ComputeFinalOffState(parseResult.Fragment, SetOptions.AnsiNulls)
            : catalog.TryGetModuleUsesAnsiNulls(moduleQualifiedName, out var usesAnsiNulls) && !usesAnsiNulls;

        if (!quotedIdentifierOff && !ansiNullsOff && rule.MatchedStatements.Count == 0)
        {

            return findings;
        }

        if (!ModuleReachableObjectWalker.TryFindTouch(parseResult.Fragment, catalog, lineage, out var touch))
        {
            return findings;
        }

        if (quotedIdentifierOff)
        {
            findings.Add(new SetOptionFinding(
                SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature, moduleQualifiedName, parseResult.SourcePath,
                parseResult.Fragment.StartLine, parseResult.Fragment.StartColumn,
                touch.ObjectQualifiedName, touch.IndexName, touch.IsIndexedView));
        }

        if (ansiNullsOff)
        {
            findings.Add(new SetOptionFinding(
                SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature, moduleQualifiedName, parseResult.SourcePath,
                parseResult.Fragment.StartLine, parseResult.Fragment.StartColumn,
                touch.ObjectQualifiedName, touch.IndexName, touch.IsIndexedView));
        }

        foreach (var (statement, kind) in rule.MatchedStatements)
        {
            findings.Add(new SetOptionFinding(
                kind, moduleQualifiedName, parseResult.SourcePath,
                statement.StartLine, statement.StartColumn,
                touch.ObjectQualifiedName, touch.IndexName, touch.IsIndexedView));
        }

        return findings;
    }

    private static bool IsAdHocScript(TSqlFragment fragment)
    {
        var collector = new ModuleNameCollector();
        fragment.Accept(collector);
        return collector.Names.Count == 0;
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class SetStatementRule : IModuleRule
    {
        public List<(PredicateSetStatement Statement, SetOptionFindingKind Kind)> MatchedStatements { get; } = [];

        public void OnEnterPredicateSetStatement(PredicateSetStatement node, ModuleWalker walker)
        {
            foreach (var (flag, triggerIsOn, kind) in SyntaxOnlyTriggers)
            {
                if ((node.Options & flag) != 0 && node.IsOn == triggerIsOn)
                {
                    MatchedStatements.Add((node, kind));
                }
            }
        }
    }
}
