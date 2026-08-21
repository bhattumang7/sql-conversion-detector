using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "SET options that silently disable plan features" - the
/// module-level (catalog flag: QUOTED_IDENTIFIER, ANSI_NULLS) and syntax-only (in-body SET:
/// NUMERIC_ROUNDABORT, ANSI_WARNINGS, CONCAT_NULL_YIELDS_NULL) halves of the stream, gated by the
/// same <see cref="ModuleReachableObjectWalker"/> precision guard - computed once per module and
/// reused across every kind, since it's the same question ("does this module touch a
/// filtered-index table or an indexed view") regardless of which SET option triggered the check.
/// Live-mode only: <see cref="DatabaseCatalog.IsIndexedView"/>/<see
/// cref="DatabaseCatalog.TryGetModuleUsesQuotedIdentifier"/>/<see
/// cref="DatabaseCatalog.TryGetModuleUsesAnsiNulls"/> are all populated only by
/// <c>LiveCatalogReader</c>/<c>LiveScanRunner</c>, so a file-mode scan never produces any finding
/// from this scanner - not a bug, the same "always empty for file mode" honesty every other
/// live-only stream in this codebase already documents.
/// </summary>
public static class SetOptionScanner
{
    /// <summary>Which flag bit + required IsOn state triggers each syntax-only kind, and the SetOptionFindingKind it reports - one table instead of one hand-written branch per option.</summary>
    private static readonly (SetOptions Flag, bool TriggerIsOn, SetOptionFindingKind Kind)[] SyntaxOnlyTriggers =
    [
        (SetOptions.NumericRoundAbort, true, SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature),
        (SetOptions.AnsiWarnings, false, SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature),
        (SetOptions.ConcatNullYieldsNull, false, SetOptionFindingKind.ConcatNullYieldsNullOffBlocksIndexedFeature),
        (SetOptions.AnsiPadding, false, SetOptionFindingKind.AnsiPaddingOffBlocksIndexedFeature),
    ];

    public static IReadOnlyList<SetOptionFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage)
    {
        var moduleQualifiedName = parseResult.SourcePath;
        var findings = new List<SetOptionFinding>();

        var visitor = new SetStatementVisitor();
        parseResult.Fragment.Accept(visitor);

        var quotedIdentifierOff = catalog.TryGetModuleUsesQuotedIdentifier(moduleQualifiedName, out var usesQuotedIdentifier) && !usesQuotedIdentifier;
        var ansiNullsOff = catalog.TryGetModuleUsesAnsiNulls(moduleQualifiedName, out var usesAnsiNulls) && !usesAnsiNulls;

        if (!quotedIdentifierOff && !ansiNullsOff && visitor.MatchedStatements.Count == 0)
        {
            // Nothing this module's own text/catalog flags could ever trigger a finding for -
            // skip the (relatively expensive) reachable-object walk entirely, matching every
            // other precision-guarded stream's own short-circuit discipline.
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

        foreach (var (statement, kind) in visitor.MatchedStatements)
        {
            findings.Add(new SetOptionFinding(
                kind, moduleQualifiedName, parseResult.SourcePath,
                statement.StartLine, statement.StartColumn,
                touch.ObjectQualifiedName, touch.IndexName, touch.IsIndexedView));
        }

        return findings;
    }

    /// <summary>
    /// <c>SET NUMERIC_ROUNDABORT ON, ANSI_WARNINGS OFF</c> is legal T-SQL (a comma-separated
    /// option list sharing one <c>IsOn</c>) - each trigger independently tests its own bit against
    /// its own required <c>IsOn</c> state, so a single statement can match more than one kind.
    /// </summary>
    private sealed class SetStatementVisitor : TSqlFragmentVisitor
    {
        public List<(PredicateSetStatement Statement, SetOptionFindingKind Kind)> MatchedStatements { get; } = [];

        public override void Visit(PredicateSetStatement node)
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
