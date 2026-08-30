using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class TempTableExecShapeCandidateScanner
{
    public static IReadOnlyList<TempTableExecShapeCandidate> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = new Rule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return rule.Candidates;
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<TempTableExecShapeCandidate> Candidates { get; } = [];

        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker)
        {
            if (node.InsertSpecification is
                {
                    Target: NamedTableReference { SchemaObject: { BaseIdentifier.Value: var targetName } targetSchemaObject },
                    InsertSource: ExecuteInsertSource
                    {
                        Execute.ExecutableEntity: ExecutableProcedureReference
                        {
                            ProcedureReference.ProcedureReference.Name: { } procedureName,
                        },
                    },
                }
                && targetName.StartsWith('#'))
            {
                var tempQualifiedName = SchemaObjectNameHelper.Qualify(targetSchemaObject);
                var temp = catalog.Find(tempQualifiedName, walker.CurrentProcScope);

                Candidates.Add(new TempTableExecShapeCandidate(
                    TempTableQualifiedName: tempQualifiedName,
                    TempTableColumns: temp?.Columns,
                    ExecutedProcQualifiedName: catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(procedureName)),
                    CallerScopeQualifiedName: walker.CurrentProcScope,
                    SourcePath: sourcePath,
                    Line: node.StartLine,
                    Column: node.StartColumn));
            }
        }
    }
}

public readonly record struct TempTableExecShapeCandidate(
    string TempTableQualifiedName,
    IReadOnlyList<CatalogColumn>? TempTableColumns,
    string ExecutedProcQualifiedName,
    string? CallerScopeQualifiedName,
    string SourcePath,
    int Line,
    int Column);
