using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class VectorLiteralConversionScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<VectorLiteralConversionFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<VectorLiteralConversionFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        private readonly Dictionary<string, DataTypeReference> _variableDataTypes = new(StringComparer.OrdinalIgnoreCase);

        public List<VectorLiteralConversionFinding> Findings { get; } = [];

        private static string DescribeElementKind(JsonValueKind kind) => kind switch
        {
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.String => "string",
            JsonValueKind.Null => "null",
            JsonValueKind.Object => "object",
            _ => kind.ToString(),
        };

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker) => _variableDataTypes.Clear();

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) => _variableDataTypes.Clear();

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
        {
            _variableDataTypes.Clear();
            foreach (var parameter in node.Parameters)
            {
                _variableDataTypes[parameter.VariableName.Value] = parameter.DataType;
            }
        }

        public void OnEnterCastCall(CastCall node, ModuleWalker walker) =>
            Inspect(node.DataType, node.Parameter);

        public void OnEnterConvertCall(ConvertCall node, ModuleWalker walker) =>
            Inspect(node.DataType, node.Parameter);

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker)
        {
            foreach (var declaration in node.Declarations)
            {
                _variableDataTypes[declaration.VariableName.Value] = declaration.DataType;

                if (declaration.Value is not null)
                {
                    Inspect(declaration.DataType, declaration.Value);
                }
            }
        }

        public void OnEnterSetVariableStatement(SetVariableStatement node, ModuleWalker walker)
        {
            if (node.Expression is not null && node.Variable?.Name is { } variableName
                && _variableDataTypes.TryGetValue(variableName, out var dataType))
            {
                Inspect(dataType, node.Expression);
            }
        }

        private void Inspect(DataTypeReference dataType, ScalarExpression parameter)
        {
            if (SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null, catalog.TypeAliases) is not { Category: SqlTypeCategory.Vector } vectorType
                || parameter is not StringLiteral stringLiteral)
            {
                return;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(stringLiteral.Value);
            }
            catch (JsonException)
            {
                return;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                var elementKinds = document.RootElement.EnumerateArray().Select(e => e.ValueKind).ToList();
                var elementCount = elementKinds.Count;
                var firstNonNumericIndex = elementKinds.FindIndex(k => k is not JsonValueKind.Number);
                if (firstNonNumericIndex >= 0)
                {
                    Findings.Add(new VectorLiteralConversionFinding(
                        stringLiteral.Value, vectorType.ToString(), DescribeElementKind(elementKinds[firstNonNumericIndex]), ActualElementCount: null, DeclaredDimensions: null,
                        VectorLiteralConversionFindingKind.NonNumericJsonElement, sourcePath, stringLiteral.StartLine, stringLiteral.StartColumn));
                    return;
                }

                if (vectorType.Length is { } declaredDimensions && elementCount != declaredDimensions
                    && elementKinds.All(k => k == JsonValueKind.Number))
                {
                    Findings.Add(new VectorLiteralConversionFinding(
                        stringLiteral.Value, vectorType.ToString(), ElementKind: null, elementCount, declaredDimensions,
                        VectorLiteralConversionFindingKind.ElementCountMismatch, sourcePath, stringLiteral.StartLine, stringLiteral.StartColumn));
                }
            }
        }
    }
}
