using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Statement-shape advice" - see <see cref="StatementShapeFinding"/>
/// for which members shipped here vs. were investigated and closed/superseded elsewhere.
/// </summary>
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

    /// <summary>Catalog-only: every base table with no PRIMARY KEY constraint, once per table -
    /// mirrors <see cref="MaxTypedColumnScanner"/>'s own "walk the catalog directly, no AST" shape.</summary>
    public static IReadOnlyList<StatementShapeFinding> ScanCatalog(DatabaseCatalog catalog)
    {
        var findings = new List<StatementShapeFinding>();

        foreach (var table in catalog.Tables)
        {
            if (table.Kind != CatalogTableKind.Table)
            {
                // Temp tables, table variables, table types, and CLR TVF return shapes never
                // carry a real PRIMARY KEY story the same way a persisted base table does.
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

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly string sourcePath;
        private string _currentModule;
        private bool? _currentRoutineHasSetNocountOn;
        private int _currentRoutineLine;
        private int _currentRoutineColumn;

        public Visitor(string sourcePath)
        {
            this.sourcePath = sourcePath;
            _currentModule = sourcePath;
        }

        public List<StatementShapeFinding> Findings { get; } = [];

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            EnterRoutine(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.StartLine, node.StartColumn);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

        public override void ExplicitVisit(AlterProcedureStatement node)
        {
            EnterRoutine(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.StartLine, node.StartColumn);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node)
        {
            EnterRoutine(SchemaObjectNameHelper.Qualify(node.ProcedureReference.Name), node.StartLine, node.StartColumn);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

        public override void ExplicitVisit(CreateTriggerStatement node)
        {
            EnterRoutine(SchemaObjectNameHelper.Qualify(node.Name), node.StartLine, node.StartColumn);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

        public override void ExplicitVisit(AlterTriggerStatement node)
        {
            EnterRoutine(SchemaObjectNameHelper.Qualify(node.Name), node.StartLine, node.StartColumn);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node)
        {
            EnterRoutine(SchemaObjectNameHelper.Qualify(node.Name), node.StartLine, node.StartColumn);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

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
                    _currentModule,
                    sourcePath,
                    node.StartLine,
                    node.StartColumn,
                    "INSERT with no explicit column list - silently breaks if the target table's column order/count ever changes."));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.OrderByClause is { OrderByElements.Count: > 0 } orderBy)
            {
                if (orderBy.OrderByElements.FirstOrDefault(e => e.Expression is IntegerLiteral) is { } ordinalElement)
                {
                    Findings.Add(new StatementShapeFinding(
                        StatementShapeFindingKind.OrdinalOrderBy,
                        _currentModule,
                        sourcePath,
                        ordinalElement.StartLine,
                        ordinalElement.StartColumn,
                        "ORDER BY references a SELECT-list position by ordinal number - silently wrong if the SELECT list's own column order changes."));
                }
            }
            else if (node.TopRowFilter is not null)
            {
                Findings.Add(new StatementShapeFinding(
                    StatementShapeFindingKind.TopWithoutOrderBy,
                    _currentModule,
                    sourcePath,
                    node.TopRowFilter.StartLine,
                    node.TopRowFilter.StartColumn,
                    "TOP with no ORDER BY anywhere in the query - Microsoft's own documentation for TOP (Transact-SQL) states the rows returned are not guaranteed in this shape."));
            }

            if (node.SelectElements.Any(e => e is SelectStarExpression))
            {
                Findings.Add(new StatementShapeFinding(
                    StatementShapeFindingKind.BareSelectStar,
                    _currentModule,
                    sourcePath,
                    node.StartLine,
                    node.StartColumn,
                    "SELECT * - couples this query to the target's current column set.",
                    FindingConfidence.Low));
            }

            base.ExplicitVisit(node);
        }

        private void EnterRoutine(string qualifiedName, int line, int column)
        {
            _currentModule = qualifiedName;
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
                    _currentModule,
                    sourcePath,
                    _currentRoutineLine,
                    _currentRoutineColumn,
                    $"'{_currentModule}' never sets NOCOUNT ON - every DML statement it runs sends a client-visible rowcount message.",
                    FindingConfidence.Medium));
            }

            _currentRoutineHasSetNocountOn = null;
        }
    }
}
