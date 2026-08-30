using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class TvfFenceScanner
{
    public static IReadOnlyList<TvfFenceFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, IReadOnlyDictionary<string, TvfFenceOrigin> fenceMap)
    {
        var rule = new Rule(parseResult.SourcePath, catalog, fenceMap);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return rule.Findings;
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private sealed class Rule(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, TvfFenceOrigin> fenceMap) : IModuleRule
    {
        public List<TvfFenceFinding> Findings { get; } = [];

        public void OnEnterFromClause(FromClause node, ModuleWalker walker)
        {
            var isStandalone = node.TableReferences.Count == 1 && node.TableReferences[0] is SchemaObjectFunctionTableReference;

            foreach (var tableReference in node.TableReferences)
            {
                Flatten(tableReference, isApplySecondSide: false, isStandalone);
            }
        }

        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker)
        {
            if (node.InsertSpecification.InsertSource is ExecuteInsertSource { Execute.ExecutableEntity: ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name: { } procedureName } })
            {
                Findings.Add(new TvfFenceFinding(
                    TvfFenceFindingKind.InsertExec,
                    FunctionQualifiedName: null,
                    ReferencedObjectQualifiedName: catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(procedureName)),
                    FunctionKind: null,
                    SourcePath: sourcePath,
                    Line: node.StartLine,
                    Column: node.StartColumn,
                    ReferenceFragmentText: FragmentTextRenderer.Render(node.InsertSpecification.InsertSource)));
            }
        }

        private void Flatten(TableReference tableReference, bool isApplySecondSide, bool isStandalone)
        {
            switch (tableReference)
            {
                case JoinTableReference join:
                    var isApply = join is UnqualifiedJoin { UnqualifiedJoinType: UnqualifiedJoinType.CrossApply or UnqualifiedJoinType.OuterApply };
                    Flatten(join.FirstTableReference, isApplySecondSide: false, isStandalone: false);
                    Flatten(join.SecondTableReference, isApplySecondSide: isApply, isStandalone: false);
                    break;

                case JoinParenthesisTableReference parenthesis:
                    Flatten(parenthesis.Join, isApplySecondSide, isStandalone: false);
                    break;

                case SchemaObjectFunctionTableReference function:
                    VisitFunctionReference(function, isApplySecondSide, isStandalone);
                    break;

                case NamedTableReference named:
                    VisitNamedReference(named);
                    break;
            }
        }

        private void VisitFunctionReference(SchemaObjectFunctionTableReference function, bool isApplySecondSide, bool isStandalone)
        {
            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(function.SchemaObject));
            if (!catalog.TryGetTableValuedFunctionKind(qualifiedName, out var kind))
            {
                return;
            }

            if (kind is TableValuedFunctionKind.Inline)
            {
                TryEmitNestedFinding(qualifiedName, function.StartLine, function.StartColumn, FragmentTextRenderer.Render(function));
                return;
            }

            var argumentColumns = isApplySecondSide
                ? function.Parameters.SelectMany(CollectColumnReferences)
                    .Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)
                    .Distinct(catalog.IdentifierComparer)
                    .ToList()
                : [];

            var isCorrelated = argumentColumns.Count > 0;
            var kindResult = TvfFenceClassifier.ClassifyDirectReference(isCorrelated, isStandalone);

            var correlatedColumns = isCorrelated ? argumentColumns : null;

            Findings.Add(new TvfFenceFinding(
                kindResult,
                FunctionQualifiedName: qualifiedName,
                ReferencedObjectQualifiedName: qualifiedName,
                FunctionKind: kind,
                SourcePath: sourcePath,
                Line: function.StartLine,
                Column: function.StartColumn,
                CorrelatedOuterColumns: correlatedColumns,
                ReferenceFragmentText: FragmentTextRenderer.Render(function)));
        }

        private void VisitNamedReference(NamedTableReference named)
        {
            var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
            TryEmitNestedFinding(qualifiedName, named.StartLine, named.StartColumn, FragmentTextRenderer.Render(named));
        }

        private void TryEmitNestedFinding(string qualifiedName, int line, int column, string fragmentText)
        {
            if (!fenceMap.TryGetValue(qualifiedName, out var origin))
            {
                return;
            }

            Findings.Add(new TvfFenceFinding(
                TvfFenceFindingKind.NestedUnderViewOrTvf,
                FunctionQualifiedName: origin.FunctionQualifiedName,
                ReferencedObjectQualifiedName: qualifiedName,
                FunctionKind: origin.FunctionKind,
                SourcePath: sourcePath,
                Line: line,
                Column: column,
                Depth: origin.Depth,
                OriginSourcePath: origin.OriginSourcePath,
                OriginLine: origin.OriginLine,
                ReferenceFragmentText: fragmentText));
        }

        private static List<ColumnReferenceExpression> CollectColumnReferences(ScalarExpression argument)
        {
            var finder = new ColumnReferenceCollector();
            argument.Accept(finder);
            return finder.Found;
        }

        private sealed class ColumnReferenceCollector : TSqlFragmentVisitor
        {
            public List<ColumnReferenceExpression> Found { get; } = [];

            public override void ExplicitVisit(ColumnReferenceExpression node) => Found.Add(node);
        }
    }
}
