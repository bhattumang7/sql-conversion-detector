using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class ProcCallTableValuedArgumentMismatchScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<ProcCallTableValuedArgumentMismatchFinding> Scan(
        SqlParseResult parseResult, ProcCallGraph graph, DatabaseCatalog catalog, SkipLedger ledger)
    {
        var rule = CreateRule(parseResult.SourcePath, graph, catalog, ledger);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, ProcCallGraph graph, DatabaseCatalog catalog, SkipLedger ledger) =>
        new(sourcePath, graph, catalog, ledger);

    internal static IReadOnlyList<ProcCallTableValuedArgumentMismatchFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal),
        ];

    private const string TvpPopulationConstructKind = "table-valued parameter population";

    private sealed record NarrowingWrite(
        string ColumnName, string ColumnTypeDisplay, string CallerExpressionDisplay, string CallerTypeDisplay,
        WriteLossKind Kind, int Line, int Column);

    internal sealed class Rule(string sourcePath, ProcCallGraph graph, DatabaseCatalog catalog, SkipLedger ledger) : IModuleRule
    {
        public List<ProcCallTableValuedArgumentMismatchFinding> Findings { get; } = [];

        private readonly Dictionary<string, SqlType?> _scalarVariableTypes = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _tvpVariableTypes = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<NarrowingWrite>> _narrowingWritesByVariable = new(StringComparer.OrdinalIgnoreCase);

        public void OnEnterDeclareVariableStatement(DeclareVariableStatement node, ModuleWalker walker)
        {
            foreach (var declaration in node.Declarations)
            {
                var variableName = declaration.VariableName.Value;
                if (declaration.DataType is UserDataTypeReference userType
                    && catalog.Find(SchemaObjectNameHelper.Qualify(userType.Name)) is { Kind: CatalogTableKind.TableType } tableType)
                {
                    _tvpVariableTypes[variableName] = tableType.QualifiedName;
                    _narrowingWritesByVariable.Remove(variableName);
                    continue;
                }

                _scalarVariableTypes[variableName] =
                    SqlTypeReferenceResolver.Resolve(declaration.DataType, columnCollation: null, catalog.TypeAliases);
                _tvpVariableTypes.Remove(variableName);
            }
        }

        public void OnEnterTSqlBatch(TSqlBatch node, ModuleWalker walker) => ResetScope();

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) => EnterScope(walker);

        public void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker) => ResetScope();

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) => EnterScope(walker);

        public void OnLeaveTriggerBody(TriggerStatementBody node, ModuleWalker walker) => ResetScope();

        private void ResetScope()
        {
            _scalarVariableTypes.Clear();
            _tvpVariableTypes.Clear();
            _narrowingWritesByVariable.Clear();
        }

        private void EnterScope(ModuleWalker walker)
        {
            ResetScope();
            if (walker.CurrentProcScope is { } scope && catalog.TryGetProcedureParameters(scope, out var formalParameters))
            {
                foreach (var parameter in formalParameters)
                {
                    if (parameter.TableTypeQualifiedName is { } tableTypeQualifiedName)
                    {
                        _tvpVariableTypes[parameter.Name] = tableTypeQualifiedName;
                    }
                    else
                    {
                        _scalarVariableTypes[parameter.Name] = parameter.Type;
                    }
                }
            }
        }

        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker)
        {
            if (node.InsertSpecification is not
                {
                    Target: VariableTableReference { Variable.Name: var targetName },
                    InsertSource: var insertSource,
                }
                || !_tvpVariableTypes.TryGetValue(targetName, out var tableTypeQualifiedName)
                || catalog.Find(tableTypeQualifiedName) is not { } tableType)
            {
                return;
            }

            if (insertSource is not ValuesInsertSource valuesSource)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, TvpPopulationConstructKind,
                    $"'{targetName}' (table type '{tableTypeQualifiedName}') is populated from something other than a literal VALUES list - column-wise narrowing not resolved for this INSERT source");
                return;
            }

            var targetColumns = ResolveTargetColumns(node.InsertSpecification.Columns, tableType);
            if (targetColumns is null)
            {
                ledger.Record(
                    AnalysisPass.Predicates, sourcePath, node.StartLine, node.StartColumn, TvpPopulationConstructKind,
                    $"'{targetName}' (table type '{tableTypeQualifiedName}') is populated with an explicit column list this scan could not match against the type's own declared columns - column-wise narrowing not resolved for this INSERT");
                return;
            }

            var writes = _narrowingWritesByVariable.TryGetValue(targetName, out var existing) ? existing : [];
            var seenColumns = new HashSet<string>(writes.Select(w => w.ColumnName), catalog.IdentifierComparer);

            foreach (var row in valuesSource.RowValues.Where(row => row.ColumnValues.Count == targetColumns.Count))
            {
                CollectRowNarrowing(row.ColumnValues, targetColumns, seenColumns, writes);
            }

            if (writes.Count > 0)
            {
                _narrowingWritesByVariable[targetName] = writes;
            }
        }

        private void CollectRowNarrowing(
            IList<ScalarExpression> rowValues, List<CatalogColumn?> targetColumns, HashSet<string> seenColumns, List<NarrowingWrite> writes)
        {
            for (var i = 0; i < targetColumns.Count; i++)
            {
                var column = targetColumns[i];
                if (column is null || column.Type is null || !seenColumns.Add(column.Name))
                {
                    continue;
                }

                var valueExpression = rowValues[i];
                var sourceType = ScalarExpressionResolver.ResolveScalarType(
                    valueExpression, [], sourcePath,
                    new ScalarExpressionResolver.ScalarTypeContext(null, catalog.TypeAliases, catalog, _scalarVariableTypes));

                if (WriteLossClassifier.Classify(column.Type, sourceType, valueExpression, isVariableTarget: false) is { } kind)
                {
                    writes.Add(new NarrowingWrite(
                        column.Name, column.Type.ToString(), FragmentTextRenderer.Render(valueExpression), sourceType!.ToString(),
                        kind, valueExpression.StartLine, valueExpression.StartColumn));
                }
            }
        }

        private List<CatalogColumn?>? ResolveTargetColumns(IList<ColumnReferenceExpression> explicitColumns, CatalogTable tableType)
        {
            if (explicitColumns.Count == 0)
            {
                return [.. tableType.Columns];
            }

            var resolved = new List<CatalogColumn?>(explicitColumns.Count);
            foreach (var explicitColumn in explicitColumns)
            {
                var columnName = explicitColumn.MultiPartIdentifier.Identifiers[^1].Value;
                var match = tableType.FindColumn(columnName, catalog.IdentifierComparer);
                if (match is null)
                {
                    return null;
                }

                resolved.Add(match);
            }

            return resolved;
        }

        public void OnEnterExecuteStatement(ExecuteStatement node, ModuleWalker walker)
        {
            var callSite = new SourceSpan(sourcePath, node.StartLine, node.StartColumn);
            if (graph.EdgeAt(callSite) is not { } edge)
            {
                return;
            }

            foreach (var argument in edge.Arguments)
            {
                if (argument.FormalTableTypeQualifiedName is not { } formalTableTypeQualifiedName
                    || argument.CallerVariableName is not { } callerVariableName
                    || !_tvpVariableTypes.TryGetValue(callerVariableName, out var callerTableTypeQualifiedName)
                    || !catalog.IdentifierComparer.Equals(callerTableTypeQualifiedName, formalTableTypeQualifiedName)
                    || !_narrowingWritesByVariable.TryGetValue(callerVariableName, out var writes))
                {
                    continue;
                }

                foreach (var write in writes)
                {
                    Findings.Add(new ProcCallTableValuedArgumentMismatchFinding(
                        walker.CurrentProcScope, edge.CalleeQualifiedName, argument.FormalParameterName,
                        formalTableTypeQualifiedName, write.ColumnName, write.CallerExpressionDisplay, write.CallerTypeDisplay,
                        write.ColumnTypeDisplay, write.Kind, sourcePath, write.Line, write.Column));
                }
            }
        }
    }
}
