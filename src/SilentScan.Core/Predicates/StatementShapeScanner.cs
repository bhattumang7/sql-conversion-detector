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
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);

    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<StatementShapeFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


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

            var hasEnforcedUniqueIndex = table.Indexes.Any(i => i.IsUnique && !i.IsFiltered && !i.IsDisabled);

            var detailText = hasEnforcedUniqueIndex
                ? $"Table '{table.QualifiedName}' has no PRIMARY KEY constraint - it can't participate in transactional replication or change tracking, both of which require a real primary key, even though it already has a UNIQUE index/constraint enforcing row uniqueness."
                : $"Table '{table.QualifiedName}' has no PRIMARY KEY constraint - no engine-enforced row uniqueness.";

            findings.Add(new StatementShapeFinding(
                StatementShapeFindingKind.TableWithNoPrimaryKey,
                table.QualifiedName,
                table.SourcePath,
                table.SourceLine,
                1,
                detailText,
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

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        private bool? _currentRoutineHasSetNocountOn;
        private int _currentRoutineLine;
        private int _currentRoutineColumn;
        private string? _currentRoutineModule;

        public List<StatementShapeFinding> Findings { get; } = [];

        private string CurrentModule(ModuleWalker walker) => walker.CurrentProcScope ?? sourcePath;

        public void OnEnterProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
        {
            if (node is ProcedureStatementBody)
            {
                EnterRoutine(node.StartLine, node.StartColumn, walker);
            }
        }

        public void OnLeaveProcedureOrFunctionBody(ProcedureStatementBodyBase node, ModuleWalker walker)
        {
            if (node is ProcedureStatementBody)
            {
                ExitRoutine();
            }
        }

        public void OnEnterTriggerBody(TriggerStatementBody node, ModuleWalker walker) =>
            EnterRoutine(node.StartLine, node.StartColumn, walker);

        public void OnLeaveTriggerBody(TriggerStatementBody node, ModuleWalker walker) => ExitRoutine();

        public void OnEnterPredicateSetStatement(PredicateSetStatement node, ModuleWalker walker)
        {
            if (_currentRoutineHasSetNocountOn == false
                && node.Options.HasFlag(SetOptions.NoCount)
                && node.IsOn)
            {
                _currentRoutineHasSetNocountOn = true;
            }
        }

        public void OnEnterInsertStatementScope(InsertStatement node, ModuleWalker walker)
        {
            var spec = node.InsertSpecification;
            if (spec is { Columns.Count: 0 })
            {
                Findings.Add(new StatementShapeFinding(
                    StatementShapeFindingKind.InsertWithoutColumnList,
                    CurrentModule(walker),
                    sourcePath,
                    node.StartLine,
                    node.StartColumn,
                    "INSERT with no explicit column list - silently breaks if the target table's column order/count ever changes."));
            }
        }

        public void OnEnterQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, ModuleWalker walker)
        {
            if (node.OrderByClause is { OrderByElements.Count: > 0 } orderBy
                && orderBy.OrderByElements.FirstOrDefault(e => e.Expression is IntegerLiteral) is { } ordinalElement)
            {
                Findings.Add(new StatementShapeFinding(
                    StatementShapeFindingKind.OrdinalOrderBy,
                    CurrentModule(walker),
                    sourcePath,
                    ordinalElement.StartLine,
                    ordinalElement.StartColumn,
                    "ORDER BY references a SELECT-list position by ordinal number - silently wrong if the SELECT list's own column order changes."));
            }

            if (node.SelectElements.Any(e => e is SelectStarExpression))
            {
                Findings.Add(new StatementShapeFinding(
                    StatementShapeFindingKind.BareSelectStar,
                    CurrentModule(walker),
                    sourcePath,
                    node.StartLine,
                    node.StartColumn,
                    "SELECT * - couples this query to the target's current column set.",
                    FindingConfidence.Low));
            }
        }

        private void EnterRoutine(int line, int column, ModuleWalker walker)
        {
            _currentRoutineModule = CurrentModule(walker);
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
