using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "SET options that silently disable plan features" - the
/// module-level (<see cref="SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature"/>) and
/// syntax-only (<see cref="SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature"/>)
/// halves of the stream, gated by the same <see cref="ModuleReachableObjectWalker"/> precision
/// guard - computed once per module and reused across both kinds, since it's the same question
/// ("does this module touch a filtered-index table or an indexed view") regardless of which SET
/// option triggered the check. Live-mode only: <see cref="DatabaseCatalog.IsIndexedView"/> and
/// <see cref="DatabaseCatalog.TryGetModuleUsesQuotedIdentifier"/> are both populated only by
/// <c>LiveCatalogReader</c>/<c>LiveScanRunner</c>, so a file-mode scan never produces any finding
/// from this scanner - not a bug, the same "always empty for file mode" honesty every other
/// live-only stream in this codebase already documents.
/// </summary>
public static class SetOptionScanner
{
    public static IReadOnlyList<SetOptionFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog, LineageCatalog lineage)
    {
        var moduleQualifiedName = parseResult.SourcePath;
        var findings = new List<SetOptionFinding>();

        var visitor = new SetStatementVisitor();
        parseResult.Fragment.Accept(visitor);

        var quotedIdentifierOff = catalog.TryGetModuleUsesQuotedIdentifier(moduleQualifiedName, out var usesQuotedIdentifier) && !usesQuotedIdentifier;
        if (!quotedIdentifierOff && visitor.NumericRoundabortOnStatements.Count == 0)
        {
            // Nothing this module's own text/catalog flag could ever trigger a finding for -
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

        foreach (var statement in visitor.NumericRoundabortOnStatements)
        {
            findings.Add(new SetOptionFinding(
                SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature, moduleQualifiedName, parseResult.SourcePath,
                statement.StartLine, statement.StartColumn,
                touch.ObjectQualifiedName, touch.IndexName, touch.IsIndexedView));
        }

        return findings;
    }

    private sealed class SetStatementVisitor : TSqlFragmentVisitor
    {
        public List<PredicateSetStatement> NumericRoundabortOnStatements { get; } = [];

        public override void Visit(PredicateSetStatement node)
        {
            if ((node.Options & SetOptions.NumericRoundAbort) != 0 && node.IsOn)
            {
                NumericRoundabortOnStatements.Add(node);
            }
        }
    }
}
