using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class StatementShapeScanner
{
    public static IReadOnlyList<StatementShapeFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);

        return
        [
            .. visitor.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    public static IReadOnlyList<StatementShapeFinding> ScanCatalog(DatabaseCatalog catalog)
    {
        var findings = new List<StatementShapeFinding>();

        foreach (var table in catalog.Tables)
        {
            if (table.Kind != CatalogTableKind.Table)
            {

                continue;
            }

            var hasPrimaryKey = table.Indexes.Any(i => i.Kind == CatalogIndexKind.PrimaryKey);
            if (hasPrimaryKey)
            {
                continue;
            }

            findings.Add(new StatementShapeFinding(
                StatementShapeFindingKind.TableWithNoPrimaryKey,
                table.QualifiedName,
                table.SourcePath,
                table.SourceLine,
                1,
                $"Table '{table.QualifiedName}' has no PRIMARY KEY constraint - no engine-enforced row uniqueness.",
                FindingConfidence.Medium));
        }

        return
        [
            .. findings
                .OrderBy(f => f.ModuleQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal),
        ];
    }

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private sealed class Visitor : ScopedRelationWalker
    {
        private readonly string sourcePath;
        private bool? _currentRoutineHasSetNocountOn;
        private int _currentRoutineLine;
        private int _currentRoutineColumn;
        private string? _currentRoutineModule;

        public Visitor(string sourcePath)
            : base(sourcePath, new DatabaseCatalog(), EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
        {
            this.sourcePath = sourcePath;
        }

        public List<StatementShapeFinding> Findings { get; } = [];

        private string CurrentModule => CurrentProcScope ?? sourcePath;

        protected override void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node)
        {
            if (node is ProcedureStatementBody)
            {
                EnterRoutine(node.StartLine, node.StartColumn);
            }
        }

        protected override void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node)
        {
            if (node is ProcedureStatementBody)
            {
                ExitRoutine();
            }
        }

        protected override void OnEnterTriggerBody(TriggerStatementBody node) =>
            EnterRoutine(node.StartLine, node.StartColumn);

        protected override void OnLeaveTriggerBody(TriggerStatementBody node) => ExitRoutine();

        public override void ExplicitVisit(PredicateSetStatement node)
        {
            if (_currentRoutineHasSetNocountOn == false
                && node.Options.HasFlag(SetOptions.NoCount)
                && node.IsOn)
            {
                _currentRoutineHasSetNocountOn = true;
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InsertStatement node)
        {
            var spec = node.InsertSpecification;
            if (spec is { Columns.Count: 0 })
            {
                Findings.Add(new StatementShapeFinding(
                    StatementShapeFindingKind.InsertWithoutColumnList,
                    CurrentModule,
                    sourcePath,
                    node.StartLine,
                    node.StartColumn,
                    "INSERT with no explicit column list - silently breaks if the target table's column order/count ever changes."));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.OrderByClause is { OrderByElements.Count: > 0 } orderBy
                && orderBy.OrderByElements.FirstOrDefault(e => e.Expression is IntegerLiteral) is { } ordinalElement)
            {
                Findings.Add(new StatementShapeFinding(
                    StatementShapeFindingKind.OrdinalOrderBy,
                    CurrentModule,
                    sourcePath,
                    ordinalElement.StartLine,
                    ordinalElement.StartColumn,
                    "ORDER BY references a SELECT-list position by ordinal number - silently wrong if the SELECT list's own column order changes."));
            }

            if (node.SelectElements.Any(e => e is SelectStarExpression))
            {
                Findings.Add(new StatementShapeFinding(
                    StatementShapeFindingKind.BareSelectStar,
                    CurrentModule,
                    sourcePath,
                    node.StartLine,
                    node.StartColumn,
                    "SELECT * - couples this query to the target's current column set.",
                    FindingConfidence.Low));
            }

            base.ExplicitVisit(node);
        }

        private void EnterRoutine(int line, int column)
        {
            _currentRoutineModule = CurrentModule;
            _currentRoutineHasSetNocountOn = false;
            _currentRoutineLine = line;
            _currentRoutineColumn = column;
        }

        private void ExitRoutine()
        {
            if (_currentRoutineHasSetNocountOn == false)
            {
                Findings.Add(new StatementShapeFinding(
                    StatementShapeFindingKind.MissingSetNocountOn,
                    _currentRoutineModule!,
                    sourcePath,
                    _currentRoutineLine,
                    _currentRoutineColumn,
                    $"'{_currentRoutineModule}' never sets NOCOUNT ON - every DML statement it runs sends a client-visible rowcount message.",
                    FindingConfidence.Medium));
            }

            _currentRoutineHasSetNocountOn = null;
        }
    }
}
