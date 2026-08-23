using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
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

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        private string? _currentProcScope;

        public List<TempTableExecShapeCandidate> Candidates { get; } = [];

        public override void ExplicitVisit(CreateProcedureStatement node) => VisitProcedureBody(node.ProcedureReference.Name, node);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitProcedureBody(node.ProcedureReference.Name, node);

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
                var temp = catalog.Find(tempQualifiedName, _currentProcScope);

                Candidates.Add(new TempTableExecShapeCandidate(
                    TempTableQualifiedName: tempQualifiedName,
                    TempTableColumns: temp?.Columns,
                    ExecutedProcQualifiedName: catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(procedureName)),
                    CallerScopeQualifiedName: _currentProcScope,
                    SourcePath: sourcePath,
                    Line: node.StartLine,
                    Column: node.StartColumn));
            }

            base.ExplicitVisit(node);
        }

        private void VisitProcedureBody(SchemaObjectName name, TSqlFragment node)
        {
            var previousScope = _currentProcScope;
            _currentProcScope = SchemaObjectNameHelper.Qualify(name);
            node.AcceptChildren(this);
            _currentProcScope = previousScope;
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
