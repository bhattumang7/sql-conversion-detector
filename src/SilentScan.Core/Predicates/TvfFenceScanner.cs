using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Pass 3 sibling: the MSTVF-as-fence stream (docs/detection-checklist.md Tier 1 #2). Every
/// finding here needs the catalog's own <see cref="TableValuedFunctionKind"/> - the call site
/// <c>FROM dbo.fn(@x)</c> is textually identical whether <c>fn</c> is an inline TVF (expanded
/// like a view, no finding) or a multi-statement/CLR one (an optimization fence with a fixed
/// cardinality estimate) - so an unresolvable function name never produces a finding here,
/// rather than guessing either way.
/// </summary>
public static class TvfFenceScanner
{
    public static IReadOnlyList<TvfFenceFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, IReadOnlyDictionary<string, TvfFenceOrigin> fenceMap)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog, fenceMap);
        parseResult.Fragment.Accept(visitor);
        return visitor.Findings;
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, TvfFenceOrigin> fenceMap) : TSqlFragmentVisitor
    {
        public List<TvfFenceFinding> Findings { get; } = [];

        public override void ExplicitVisit(FromClause node)
        {
            var isStandalone = node.TableReferences.Count == 1 && node.TableReferences[0] is SchemaObjectFunctionTableReference;

            foreach (var tableReference in node.TableReferences)
            {
                Flatten(tableReference, isApplySecondSide: false, isStandalone);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InsertStatement node)
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

            base.ExplicitVisit(node);
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

            // An inline TVF is called with the exact same function-call syntax as a
            // multi-statement/CLR one (FROM dbo.fn(@x)) - it is never itself a fence, but its
            // own body can still (transitively) name one, exactly like a plain view referenced
            // by name can. Without this, a fence hidden behind an inline TVF wrapper - the same
            // "permissions function wrapped in a view" shape, just called via function syntax
            // instead of a bare table name - never surfaced at all.
            if (kind is TableValuedFunctionKind.Inline)
            {
                TryEmitNestedFinding(qualifiedName, function.StartLine, function.StartColumn, FragmentTextRenderer.Render(function));
                return;
            }

            var argumentColumns = isApplySecondSide
                ? function.Parameters.SelectMany(CollectColumnReferences)
                    .Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
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
