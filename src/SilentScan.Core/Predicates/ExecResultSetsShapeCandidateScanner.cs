using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class ExecResultSetsShapeCandidateScanner
{
    public static IReadOnlyList<ExecResultSetsShapeCandidate> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = new Rule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return rule.Candidates;
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<ExecResultSetsShapeCandidate> Candidates { get; } = [];

        public void OnEnterExecuteStatement(ExecuteStatement node, ModuleWalker walker)
        {
            if (node.ExecuteSpecification is not
                {
                    ExecutableEntity: ExecutableProcedureReference
                    {
                        ProcedureReference.ProcedureReference.Name: { } procedureName,
                    },
                })
            {
                return;
            }

            var resultSetsOption = node.Options.OfType<ResultSetsExecuteOption>().FirstOrDefault();
            if (resultSetsOption is not
                {
                    ResultSetsOptionKind: ResultSetsOptionKind.ResultSetsDefined,
                    Definitions: [InlineResultSetDefinition inlineDefinition, ..],
                })
            {
                return;
            }

            if (TryResolveDeclaredColumns(inlineDefinition, catalog) is not { } declaredColumns)
            {
                return;
            }

            Candidates.Add(new ExecResultSetsShapeCandidate(
                DeclaredColumns: declaredColumns,
                ExecutedProcQualifiedName: catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(procedureName)),
                CallerScopeQualifiedName: walker.CurrentProcScope,
                SourcePath: sourcePath,
                Line: node.StartLine,
                Column: node.StartColumn));
        }

        private static List<ExecResultSetsDeclaredColumn>? TryResolveDeclaredColumns(
            InlineResultSetDefinition inlineDefinition, DatabaseCatalog catalog)
        {
            var declaredColumns = new List<ExecResultSetsDeclaredColumn>(inlineDefinition.ResultColumnDefinitions.Count);
            foreach (var columnDefinition in inlineDefinition.ResultColumnDefinitions)
            {
                if (columnDefinition.ColumnDefinition is not { ColumnIdentifier: { } identifier, DataType: { } dataType })
                {
                    return null;
                }

                var type = SqlTypeReferenceResolver.Resolve(dataType, columnDefinition.ColumnDefinition.Collation, catalog.TypeAliases);
                if (type is null)
                {
                    return null;
                }

                declaredColumns.Add(new ExecResultSetsDeclaredColumn(identifier.Value, type));
            }

            return declaredColumns;
        }
    }
}

public readonly record struct ExecResultSetsDeclaredColumn(string Name, SqlType Type);

public readonly record struct ExecResultSetsShapeCandidate(
    IReadOnlyList<ExecResultSetsDeclaredColumn> DeclaredColumns,
    string ExecutedProcQualifiedName,
    string? CallerScopeQualifiedName,
    string SourcePath,
    int Line,
    int Column);
