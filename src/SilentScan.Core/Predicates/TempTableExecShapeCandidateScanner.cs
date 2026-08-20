using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Dynamic SQL quality" item 3: <c>INSERT INTO #temp EXEC
/// OtherProc</c> assumes <c>OtherProc</c>'s described result-set shape matches <c>#temp</c>'s own
/// declared columns. This pass finds the SITES only - a bare, catalog-only, no-network AST walk,
/// exactly mirroring <see cref="TvfFenceScanner"/>'s own <c>ExplicitVisit(InsertStatement)</c>
/// handling of the identical <c>ExecuteInsertSource</c>/<c>ExecutableProcedureReference</c> shape
/// (used there for a different finding). The live round trip that actually describes each site's
/// executed proc (<c>sys.dm_exec_describe_first_result_set</c>, live-mode only) happens
/// afterward, in <c>SilentScan.Live.Catalog.TempTableExecShapeChecker</c> - this pass only decides
/// WHERE to probe and resolves the caller-side half (the temp table's own declared shape) that
/// needs no network round trip at all.
///
/// Only a genuine named-procedure EXEC target is a candidate - <c>INSERT INTO #temp EXEC(@sql)</c>
/// (a string-form EXEC) has no fixed shape to describe and is out of scope by construction,
/// matching <c>SilentScan.Verify.Catalog.LiveReadOnlyGuard.AssertDescribeFirstResultSetProbeOnly</c>'s
/// own restriction to a named-procedure reference.
/// </summary>
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

/// <summary>
/// One <c>INSERT INTO #temp EXEC proc</c> site. <see cref="TempTableColumns"/> is null when the
/// temp table's own declared shape could not be resolved in the catalog (declared via a shape
/// this tool's own module-body catalog pass doesn't model, or genuinely never DECLAREd before
/// this site) - reported as an honest unanalyzed case downstream, never guessed.
/// </summary>
public readonly record struct TempTableExecShapeCandidate(
    string TempTableQualifiedName,
    IReadOnlyList<CatalogColumn>? TempTableColumns,
    string ExecutedProcQualifiedName,
    string? CallerScopeQualifiedName,
    string SourcePath,
    int Line,
    int Column);
