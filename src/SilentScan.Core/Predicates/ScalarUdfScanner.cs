using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class ScalarUdfScanner
{
    public static IReadOnlyList<ScalarUdfFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, IReadOnlyDictionary<string, ScalarUdfOrigin> scalarUdfMap)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog, scalarUdfMap);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, ScalarUdfOrigin> scalarUdfMap) =>
        new(sourcePath, catalog, scalarUdfMap);

    internal static IReadOnlyList<ScalarUdfFinding> Harvest(Rule rule) => rule.Findings;

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog, IReadOnlyDictionary<string, ScalarUdfOrigin> scalarUdfMap) : IModuleRule
    {

        private readonly List<(int Start, int End, ScalarUdfContext Context)> _regions = [];

        private readonly HashSet<FunctionCall> _claimed = [];

        public List<ScalarUdfFinding> Findings { get; } = [];

        public void OnEnterWhereClause(WhereClause node, ModuleWalker walker) => RecordRegion(node.SearchCondition, ScalarUdfContext.Where);

        public void OnEnterHavingClause(HavingClause node, ModuleWalker walker) => RecordRegion(node.SearchCondition, ScalarUdfContext.Having);

        public void OnEnterJoinSearchCondition(QualifiedJoin node, ModuleWalker walker) => RecordRegion(node.SearchCondition, ScalarUdfContext.JoinOn);

        public void OnEnterMergeSearchCondition(MergeSpecification node, ModuleWalker walker) => RecordRegion(node.SearchCondition, ScalarUdfContext.MergeOn);

        public void OnEnterSelectScalarExpression(SelectScalarExpression node, ModuleWalker walker) => RecordRegion(node.Expression, ScalarUdfContext.SelectList);

        public void OnEnterOrderByClause(OrderByClause node, ModuleWalker walker) => RecordRegion(node, ScalarUdfContext.OrderBy);

        public void OnEnterGroupByClause(GroupByClause node, ModuleWalker walker) => RecordRegion(node, ScalarUdfContext.GroupBy);

        public void OnEnterAssignmentSetClause(AssignmentSetClause node, ModuleWalker walker) =>
            RecordRegion(node.NewValue, node.Variable is not null ? ScalarUdfContext.VariableAssignment : ScalarUdfContext.SetAssignment);

        public void OnEnterSelectSetVariable(SelectSetVariable node, ModuleWalker walker) => RecordRegion(node.Expression, ScalarUdfContext.VariableAssignment);

        public void OnEnterSetVariableStatement(SetVariableStatement node, ModuleWalker walker) => RecordRegion(node.Expression, ScalarUdfContext.VariableAssignment);

        public void OnEnterFromClause(FromClause node, ModuleWalker walker)
        {
            foreach (var tableReference in node.TableReferences)
            {
                Flatten(tableReference);
            }
        }

        public void OnEnterFunctionCall(FunctionCall node, ModuleWalker walker)
        {
            if (_claimed.Contains(node))
            {
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
        }

        private void RecordRegion(TSqlFragment? region, ScalarUdfContext context)
        {
            if (region is not null)
            {
                _regions.Add((region.StartOffset, region.StartOffset + region.FragmentLength, context));
            }
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

        private void Emit(FunctionCall node, string qualifiedName, ScalarUdfInfo info)
        {
            var context = ResolveContext(node);
            var kind = ScalarUdfClassifier.ClassifyInvocationKind(context);

            var (inlineability, blocker) = ScalarUdfInlineabilityClassifier.Classify(info, catalog.CompatibilityLevel);
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

        private ScalarUdfContext ResolveContext(FunctionCall node) =>
            ScalarUdfContextRegions.Resolve(_regions, node);

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
