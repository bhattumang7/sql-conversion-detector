using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class VectorFunctionArgumentScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private static readonly IReadOnlyDictionary<string, int[]> VectorArgumentIndexesByFunction = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["VECTOR_DISTANCE"] = [1, 2],
        ["VECTOR_NORM"] = [0],
        ["VECTORPROPERTY"] = [0],
    };

    public static IReadOnlyList<VectorFunctionArgumentFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<VectorFunctionArgumentFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        private readonly Dictionary<string, SqlType?> _variables = new(StringComparer.OrdinalIgnoreCase);

        private static string DescribeArgument(string functionName, int index) => functionName.ToUpperInvariant() switch
        {
            "VECTOR_DISTANCE" => index == 1 ? "first vector argument" : "second vector argument",
            _ => "vector argument",
        };

        public List<VectorFunctionArgumentFinding> Findings { get; } = [];

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker) => _variables.Clear();

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) => _variables.Clear();

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
        {
            _variables.Clear();
            foreach (var parameter in node.Parameters)
            {
                _variables[parameter.VariableName.Value] = SqlTypeReferenceResolver.Resolve(parameter.DataType, columnCollation: null, catalog.TypeAliases);
            }
        }

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker)
        {
            foreach (var declaration in node.Declarations)
            {
                _variables[declaration.VariableName.Value] = SqlTypeReferenceResolver.Resolve(declaration.DataType, columnCollation: null, catalog.TypeAliases);
            }
        }

        public void OnEnterFunctionCall(FunctionCall node, ModuleWalker walker)
        {
            if (node.CallTarget is not null || node.FunctionName?.Value is not { } functionName
                || !VectorArgumentIndexesByFunction.TryGetValue(functionName, out var argumentIndexes))
            {
                return;
            }

            var scopeChain = walker.CurrentScopeChain();
            var context = new ScalarExpressionResolver.ScalarTypeContext(Ledger: null, catalog.TypeAliases, catalog, _variables);

            SqlType? firstVectorType = null;
            ScalarExpression? firstVectorExpression = null;

            foreach (var argumentIndex in argumentIndexes)
            {
                if (node.Parameters.Count <= argumentIndex)
                {
                    continue;
                }

                var argumentExpression = node.Parameters[argumentIndex];
                var argumentType = ScalarExpressionResolver.ResolveScalarType(argumentExpression, scopeChain, sourcePath, context);
                if (argumentType is null)
                {
                    continue;
                }

                if (argumentType.Category != SqlTypeCategory.Vector)
                {
                    Findings.Add(new VectorFunctionArgumentFinding(
                        functionName.ToUpperInvariant(), DescribeArgument(functionName, argumentIndex), argumentType.ToString(), OtherTypeDisplay: null,
                        VectorFunctionArgumentFindingKind.NonVectorOperand, sourcePath, argumentExpression.StartLine, argumentExpression.StartColumn));
                    continue;
                }

                if (firstVectorExpression is null)
                {
                    firstVectorType = argumentType;
                    firstVectorExpression = argumentExpression;
                    continue;
                }

                if (firstVectorType!.Length is { } firstDimensions && argumentType.Length is { } secondDimensions && firstDimensions != secondDimensions)
                {
                    Findings.Add(new VectorFunctionArgumentFinding(
                        functionName.ToUpperInvariant(), "vector arguments", firstVectorType.ToString(), argumentType.ToString(),
                        VectorFunctionArgumentFindingKind.DimensionMismatch, sourcePath, node.StartLine, node.StartColumn));
                }
            }
        }
    }
}
