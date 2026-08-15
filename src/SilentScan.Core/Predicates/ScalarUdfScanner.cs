using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

/// <summary>
/// The scalar-UDF stream (docs/detection-checklist.md Tier 1 #1) - every finding here needs the
/// catalog's own <see cref="ScalarUdfInfo"/>. A 2-part function call is only ever a scalar UDF
/// reference when the catalog says so; a call this scan never saw DDL for, and every built-in
/// (single-part, null <see cref="FunctionCall.CallTarget"/>) call, never produces a finding.
/// </summary>
public static class ScalarUdfScanner
{
    public static IReadOnlyList<ScalarUdfFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, IReadOnlyDictionary<string, ScalarUdfOrigin> scalarUdfMap)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog, scalarUdfMap);
        parseResult.Fragment.Accept(visitor);
        return visitor.Findings;
    }

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, ScalarUdfOrigin> scalarUdfMap) : TSqlFragmentVisitor
    {
        // (Start, End, Context) - registered before the containing clause's children are
        // visited, so a FunctionCall found anywhere underneath resolves to the innermost
        // (shortest) region that contains it. Kind (predicate vs projection) is derived from
        // Context itself, not tracked separately - Where/JoinOn/Having/MergeOn ARE the predicate
        // regions.
        private readonly List<(int Start, int End, ScalarUdfContext Context)> _regions = [];

        private readonly HashSet<FunctionCall> _claimed = [];

        public List<ScalarUdfFinding> Findings { get; } = [];

        public override void ExplicitVisit(WhereClause node) => ClaimRegion(node.SearchCondition, node, ScalarUdfContext.Where);

        public override void ExplicitVisit(HavingClause node) => ClaimRegion(node.SearchCondition, node, ScalarUdfContext.Having);

        public override void ExplicitVisit(QualifiedJoin node) => ClaimRegion(node.SearchCondition, node, ScalarUdfContext.JoinOn);

        public override void ExplicitVisit(MergeSpecification node) => ClaimRegion(node.SearchCondition, node, ScalarUdfContext.MergeOn);

        public override void ExplicitVisit(SelectScalarExpression node) => ClaimRegion(node.Expression, node, ScalarUdfContext.SelectList);

        public override void ExplicitVisit(OrderByClause node) => ClaimRegion(node, node, ScalarUdfContext.OrderBy);

        public override void ExplicitVisit(GroupByClause node) => ClaimRegion(node, node, ScalarUdfContext.GroupBy);

        public override void ExplicitVisit(AssignmentSetClause node) =>
            ClaimRegion(node.NewValue, node, node.Variable is not null ? ScalarUdfContext.VariableAssignment : ScalarUdfContext.SetAssignment);

        public override void ExplicitVisit(SelectSetVariable node) => ClaimRegion(node.Expression, node, ScalarUdfContext.VariableAssignment);

        public override void ExplicitVisit(SetVariableStatement node) => ClaimRegion(node.Expression, node, ScalarUdfContext.VariableAssignment);

        private void ClaimRegion(TSqlFragment? region, TSqlFragment node, ScalarUdfContext context)
        {
            if (region is not null)
            {
                _regions.Add((region.StartOffset, region.StartOffset + region.FragmentLength, context));
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(FromClause node)
        {
            foreach (var tableReference in node.TableReferences)
            {
                Flatten(tableReference);
            }

            base.ExplicitVisit(node);
        }

        private void Flatten(TableReference tableReference)
        {
            switch (tableReference)
            {
                case JoinTableReference join:
                    Flatten(join.FirstTableReference);
                    Flatten(join.SecondTableReference);
                    break;

                case JoinParenthesisTableReference parenthesis:
                    Flatten(parenthesis.Join);
                    break;

                case SchemaObjectFunctionTableReference function:
                    var functionQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(function.SchemaObject));
                    TryEmitNested(functionQualifiedName, function.StartLine, function.StartColumn, FragmentTextRenderer.Render(function));
                    break;

                case NamedTableReference named:
                    var namedQualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(named.SchemaObject));
                    TryEmitNested(namedQualifiedName, named.StartLine, named.StartColumn, FragmentTextRenderer.Render(named));
                    break;
            }

            // A TVF-call table reference's own arguments can themselves contain a scalar-UDF
            // call (FROM dbo.SomeTvf(dbo.fn_Compute(@x))) - that is a genuine per-row
            // ProjectionInvocation, reached through the ordinary FunctionCall visit below since
            // this method never claims/skips those argument subtrees.
        }

        private void TryEmitNested(string qualifiedName, int line, int column, string fragmentText)
        {
            if (!scalarUdfMap.TryGetValue(qualifiedName, out var origin))
            {
                return;
            }

            Findings.Add(new ScalarUdfFinding(
                ScalarUdfFindingKind.NestedUnderViewOrTvf,
                FunctionQualifiedName: origin.FunctionQualifiedName,
                ReferencedObjectQualifiedName: qualifiedName,
                UdfKind: origin.UdfKind,
                Inlineability: ScalarUdfInlineability.Unknown,
                InlineabilityBlocker: null,
                IsSchemaBound: null,
                ConstantArgumentsNotFolded: false,
                ClrDataAccess: null,
                Context: origin.OriginContext,
                SchemaDependencyKind: null,
                SourcePath: sourcePath,
                Line: line,
                Column: column,
                Depth: origin.Depth,
                OriginSourcePath: origin.OriginSourcePath,
                OriginLine: origin.OriginLine,
                ReferenceFragmentText: fragmentText));
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (_claimed.Contains(node))
            {
                base.ExplicitVisit(node);
                return;
            }

            if (node.CallTarget is MultiPartIdentifierCallTarget)
            {
                var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.QualifyFunctionCall(node));
                if (catalog.TryGetScalarUdfInfo(qualifiedName, out var info) && info is not null)
                {
                    Emit(node, qualifiedName, info);
                    ClaimNestedFunctionCalls(node);
                }
            }

            base.ExplicitVisit(node);
        }

        private void Emit(FunctionCall node, string qualifiedName, ScalarUdfInfo info)
        {
            var context = ResolveContext(node);
            var kind = context.IsPredicate() ? ScalarUdfFindingKind.PredicateInvocation : ScalarUdfFindingKind.ProjectionInvocation;

            var (inlineability, blocker) = ScalarUdfInlineabilityClassifier.Classify(info);
            var constantArgumentsNotFolded = info.IsSchemaBound == false && node.Parameters.Count > 0 && node.Parameters.All(p => p is Literal);

            Findings.Add(new ScalarUdfFinding(
                kind,
                FunctionQualifiedName: qualifiedName,
                ReferencedObjectQualifiedName: qualifiedName,
                UdfKind: info.Kind,
                Inlineability: inlineability,
                InlineabilityBlocker: blocker,
                IsSchemaBound: info.IsSchemaBound,
                ConstantArgumentsNotFolded: constantArgumentsNotFolded,
                ClrDataAccess: info.ClrDataAccess,
                Context: context,
                SchemaDependencyKind: null,
                SourcePath: sourcePath,
                Line: node.StartLine,
                Column: node.StartColumn,
                ReferenceFragmentText: FragmentTextRenderer.Render(node)));
        }

        private ScalarUdfContext ResolveContext(FunctionCall node)
        {
            var best = default((int Start, int End, ScalarUdfContext Context)?);
            foreach (var region in _regions)
            {
                if (node.StartOffset < region.Start || node.StartOffset >= region.End)
                {
                    continue;
                }

                if (best is null || region.End - region.Start < best.Value.End - best.Value.Start)
                {
                    best = region;
                }
            }

            return best?.Context ?? ScalarUdfContext.Other;
        }


        private void ClaimNestedFunctionCalls(FunctionCall node)
        {
            var visitor = new NestedFunctionCallCollector();
            foreach (var parameter in node.Parameters)
            {
                parameter.Accept(visitor);
            }

            foreach (var nested in visitor.Found)
            {
                _claimed.Add(nested);
            }
        }

        private sealed class NestedFunctionCallCollector : TSqlFragmentVisitor
        {
            public List<FunctionCall> Found { get; } = [];

            public override void ExplicitVisit(FunctionCall node)
            {
                Found.Add(node);
                base.ExplicitVisit(node);
            }
        }
    }
}
