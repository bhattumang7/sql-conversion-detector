using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class StringSplitArgumentScanner
{
    private const int MinimumEngineMajorVersionForThreeArgumentForm = 16;

    public static IReadOnlyList<StringSplitArgumentFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<StringSplitArgumentFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        private readonly Dictionary<string, SqlType?> _variableTypes = new(StringComparer.OrdinalIgnoreCase);

        public List<StringSplitArgumentFinding> Findings { get; } = [];

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker) => _variableTypes.Clear();

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) => SeedOwnParameters(walker);

        public void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) => _variableTypes.Clear();

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) => _variableTypes.Clear();

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker)
        {
            foreach (var declaration in node.Declarations)
            {
                _variableTypes[declaration.VariableName.Value] =
                    SqlTypeReferenceResolver.Resolve(declaration.DataType, columnCollation: null, catalog.TypeAliases);
            }
        }

        public void OnEnterGlobalFunctionTableReference(GlobalFunctionTableReference node, ModuleWalker walker)
        {
            var name = node.Name?.Value;

            if (!string.Equals(name, "STRING_SPLIT", StringComparison.OrdinalIgnoreCase) || node.Parameters.Count < 2)
            {
                return;
            }

            CheckCharacterArgument(node.Parameters[0]);
            CheckSeparatorArgument(node.Parameters[1]);

            if (node.Parameters.Count >= 3)
            {
                CheckEnableOrdinalArgument(node.Parameters[2]);
            }
        }

        private void SeedOwnParameters(ModuleWalker walker)
        {
            _variableTypes.Clear();
            if (walker.CurrentProcScope is { } scope && catalog.TryGetProcedureParameters(scope, out var ownFormalParameters))
            {
                foreach (var parameter in ownFormalParameters)
                {
                    _variableTypes[parameter.Name] = parameter.Type;
                }
            }
        }

        private void CheckSeparatorArgument(ScalarExpression separator)
        {
            if (separator is NullLiteral)
            {
                AddArgumentFinding(StringSplitArgumentFindingKind.SeparatorNotSingleCharacter, separator, "NULL");
                return;
            }

            if (LiteralComparisonFolder.TryFoldToString(separator) is { } folded && folded.Length != 1)
            {
                AddArgumentFinding(StringSplitArgumentFindingKind.SeparatorNotSingleCharacter, separator, FragmentTextRenderer.Render(separator));
                return;
            }

            CheckCharacterArgument(separator);
        }

        private void CheckCharacterArgument(ScalarExpression argument)
        {
            if (argument is NullLiteral)
            {
                return;
            }

            var type = ResolveType(argument);
            if (type is not null && !type.IsStringFamily)
            {
                Findings.Add(new StringSplitArgumentFinding(
                    StringSplitArgumentFindingKind.ArgumentTypeNotCharacter,
                    FragmentTextRenderer.Render(argument), type.ToString(),
                    sourcePath, argument.StartLine, argument.StartColumn));
            }
        }

        private void CheckEnableOrdinalArgument(ScalarExpression enableOrdinal)
        {
            if (catalog.EngineMajorVersion is { } engineMajorVersion && engineMajorVersion < MinimumEngineMajorVersionForThreeArgumentForm)
            {
                Findings.Add(new StringSplitArgumentFinding(
                    StringSplitArgumentFindingKind.ThreeArgumentFormRequiresNewerEngine,
                    FragmentTextRenderer.Render(enableOrdinal), engineMajorVersion.ToString(CultureInfo.InvariantCulture),
                    sourcePath, enableOrdinal.StartLine, enableOrdinal.StartColumn));
                return;
            }

            if (enableOrdinal is NullLiteral)
            {
                return;
            }

            if (ReferencesVariableOrColumn(enableOrdinal))
            {
                AddArgumentFinding(StringSplitArgumentFindingKind.EnableOrdinalNotConstant, enableOrdinal);
                return;
            }

            if (TryGetIntegerLiteralValue(enableOrdinal, out var value))
            {
                if (value != 0 && value != 1)
                {
                    AddArgumentFinding(StringSplitArgumentFindingKind.EnableOrdinalInvalidValue, enableOrdinal);
                }

                return;
            }

            if (enableOrdinal is Literal)
            {
                var type = ResolveType(enableOrdinal);
                if (type is not null && type.Category != SqlTypeCategory.Int && type.Category != SqlTypeCategory.Bit)
                {
                    Findings.Add(new StringSplitArgumentFinding(
                        StringSplitArgumentFindingKind.EnableOrdinalTypeNotInteger,
                        FragmentTextRenderer.Render(enableOrdinal), type.ToString(),
                        sourcePath, enableOrdinal.StartLine, enableOrdinal.StartColumn));
                }
            }
        }

        private static bool TryGetIntegerLiteralValue(ScalarExpression expression, out int value)
        {
            switch (expression)
            {
                case IntegerLiteral literal:
                    return int.TryParse(literal.Value, out value);

                case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative, Expression: IntegerLiteral literal }
                    when int.TryParse(literal.Value, out var magnitude):
                    value = -magnitude;
                    return true;

                case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive, Expression: IntegerLiteral literal }:
                    return int.TryParse(literal.Value, out value);

                default:
                    value = 0;
                    return false;
            }
        }

        private SqlType? ResolveType(ScalarExpression expression) =>
            ScalarExpressionResolver.ResolveScalarType(
                expression, [], sourcePath,
                new ScalarExpressionResolver.ScalarTypeContext(null, catalog.TypeAliases, catalog, _variableTypes));

        private void AddArgumentFinding(StringSplitArgumentFindingKind kind, ScalarExpression argument, string? argumentText = null) =>
            Findings.Add(new StringSplitArgumentFinding(
                kind, argumentText ?? FragmentTextRenderer.Render(argument), DetailText: null,
                sourcePath, argument.StartLine, argument.StartColumn));

        private static bool ReferencesVariableOrColumn(ScalarExpression expression)
        {
            var finder = new VariableOrColumnReferenceFinder();
            expression.Accept(finder);
            return finder.Found;
        }

        private sealed class VariableOrColumnReferenceFinder : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void ExplicitVisit(VariableReference node) => Found = true;

            public override void ExplicitVisit(ColumnReferenceExpression node) => Found = true;
        }
    }
}
