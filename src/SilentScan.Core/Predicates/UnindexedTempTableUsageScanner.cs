using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class UnindexedTempTableUsageScanner
{
    public static IReadOnlyList<UnindexedTempTableUsageFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);

        var tempIdentifierComparer = TypeInference.Collation.IdentifierComparer(catalog.EffectiveTempdbCollation);
        var findings = new List<UnindexedTempTableUsageFinding>();

        foreach (var declaration in visitor.Declarations)
        {
            var usage = visitor.Usages.FirstOrDefault(u =>
                u.Scope == declaration.Scope
                && tempIdentifierComparer.Equals(u.TempTableName, declaration.TempTableName));

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

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

#pragma warning disable CS9107
    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog)
        : ScopedRelationWalker(sourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        public List<Declaration> Declarations { get; } = [];

        public List<Usage> Usages { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.Into is { BaseIdentifier.Value: var tempName } into && tempName.StartsWith('#'))
            {
                var qualified = catalog.Find(SchemaObjectNameHelper.Qualify(into), CurrentProcScope)?.QualifiedName
                    ?? SchemaObjectNameHelper.Qualify(into);
                Declarations.Add(new Declaration(tempName, qualified, CurrentProcScope, node.StartLine));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QualifiedJoin node)
        {
            TryRecordJoinOperand(node.FirstTableReference, node);
            TryRecordJoinOperand(node.SecondTableReference, node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.WhereClause is { } where
                && node.FromClause?.TableReferences is [NamedTableReference { SchemaObject.BaseIdentifier.Value: var name }]
                && name.StartsWith('#'))
            {
                Usages.Add(new Usage(name, CurrentProcScope, UnindexedTempTableUsageKind.FilteredInWhere, where.StartLine, where.StartColumn));
            }

            base.ExplicitVisit(node);
        }

        private void TryRecordJoinOperand(TableReference side, TSqlFragment joinNode)
        {
            if (side is NamedTableReference { SchemaObject.BaseIdentifier.Value: var name } && name.StartsWith('#'))
            {
                Usages.Add(new Usage(name, CurrentProcScope, UnindexedTempTableUsageKind.JoinOperand, joinNode.StartLine, joinNode.StartColumn));
            }
        }
    }
}
