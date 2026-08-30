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
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return visitor.Candidates;
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

#pragma warning disable CS9107
    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog)
        : ScopedRelationWalker(sourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        public List<TempTableExecShapeCandidate> Candidates { get; } = [];

        public override void ExplicitVisit(InsertStatement node)
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
                var temp = catalog.Find(tempQualifiedName, CurrentProcScope);

                Candidates.Add(new TempTableExecShapeCandidate(
                    TempTableQualifiedName: tempQualifiedName,
                    TempTableColumns: temp?.Columns,
                    ExecutedProcQualifiedName: catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(procedureName)),
                    CallerScopeQualifiedName: CurrentProcScope,
                    SourcePath: sourcePath,
                    Line: node.StartLine,
                    Column: node.StartColumn));
            }

            base.ExplicitVisit(node);
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
