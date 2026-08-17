using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>docs/detection-checklist.md "Second OSS/commercial sweep": SELECT INTO temp table
/// later joined/filtered with no index. Two AST passes over the same scoped walk: collect every
/// <c>SELECT ... INTO #temp</c> declaration site, then collect every qualifying usage site
/// (a JOIN operand, or the sole FROM-clause source under a WHERE clause) in the SAME
/// proc/trigger/batch scope - reuses the catalog's own already-tracked <see
/// cref="CatalogTable.Indexes"/> for the "no index" half (see <see
/// cref="UnindexedTempTableUsageFinding"/> for the full precision-guard rationale).</summary>
public static class UnindexedTempTableUsageScanner
{
    public static IReadOnlyList<UnindexedTempTableUsageFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(catalog);
        parseResult.Fragment.Accept(visitor);

        var findings = new List<UnindexedTempTableUsageFinding>();

        foreach (var declaration in visitor.Declarations)
        {
            var usage = visitor.Usages.FirstOrDefault(u =>
                u.Scope == declaration.Scope
                && string.Equals(u.TempTableName, declaration.TempTableName, StringComparison.OrdinalIgnoreCase));

            if (usage is null)
            {
                continue;
            }

            var temp = catalog.Find(declaration.TempQualifiedName, declaration.Scope);
            if (temp is null || temp.Indexes.Count != 0)
            {
                continue;
            }

            findings.Add(new UnindexedTempTableUsageFinding(
                usage.Kind,
                declaration.TempQualifiedName,
                parseResult.SourcePath,
                declaration.Line,
                usage.Line,
                usage.Column));
        }

        return
        [
            .. findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.DeclarationLine),
        ];
    }

    private sealed record Declaration(string TempTableName, string TempQualifiedName, string? Scope, int Line);

    private sealed record Usage(string TempTableName, string? Scope, UnindexedTempTableUsageKind Kind, int Line, int Column);

    private sealed class Visitor(DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        private string? _currentScope;

        public List<Declaration> Declarations { get; } = [];

        public List<Usage> Usages { get; } = [];

        public override void ExplicitVisit(CreateProcedureStatement node) => VisitScopedBody(node.ProcedureReference.Name, node);

        public override void ExplicitVisit(AlterProcedureStatement node) => VisitScopedBody(node.ProcedureReference.Name, node);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitScopedBody(node.ProcedureReference.Name, node);

        public override void ExplicitVisit(CreateTriggerStatement node) => VisitScopedBody(node.Name, node);

        public override void ExplicitVisit(AlterTriggerStatement node) => VisitScopedBody(node.Name, node);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitScopedBody(node.Name, node);

        private void VisitScopedBody(SchemaObjectName name, TSqlFragment node)
        {
            var previousScope = _currentScope;
            _currentScope = SchemaObjectNameHelper.Qualify(name);
            node.AcceptChildren(this);
            _currentScope = previousScope;
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.Into is { BaseIdentifier.Value: var tempName } into && tempName.StartsWith('#'))
            {
                var qualified = catalog.Find(SchemaObjectNameHelper.Qualify(into), _currentScope)?.QualifiedName
                    ?? SchemaObjectNameHelper.Qualify(into);
                Declarations.Add(new Declaration(tempName, qualified, _currentScope, node.StartLine));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QualifiedJoin node)
        {
            TryRecordJoinOperand(node.FirstTableReference, node);
            TryRecordJoinOperand(node.SecondTableReference, node);
            base.ExplicitVisit(node);
        }

        private void TryRecordJoinOperand(TableReference side, TSqlFragment joinNode)
        {
            if (side is NamedTableReference { SchemaObject.BaseIdentifier.Value: var name } && name.StartsWith('#'))
            {
                Usages.Add(new Usage(name, _currentScope, UnindexedTempTableUsageKind.JoinOperand, joinNode.StartLine, joinNode.StartColumn));
            }
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.WhereClause is { } where
                && node.FromClause?.TableReferences is [NamedTableReference { SchemaObject.BaseIdentifier.Value: var name }]
                && name.StartsWith('#'))
            {
                Usages.Add(new Usage(name, _currentScope, UnindexedTempTableUsageKind.FilteredInWhere, where.StartLine, where.StartColumn));
            }

            base.ExplicitVisit(node);
        }
    }
}
