using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Forced-serial construct inventory" - fully syntax-only, no
/// <see cref="Catalog.DatabaseCatalog"/> needed: every trigger this scanner reports is visible from
/// the AST alone. Three independent, oracle-confirmed mechanisms
/// (<see cref="ForcedSerialFindingKind"/>), one visitor, one full-corpus pass - matching
/// <see cref="SetOptionScanner"/>'s own "one scanner, many Kind values" shape for a multi-sub-
/// trigger inventory stream.
/// </summary>
public static class ForcedSerialScanner
{
    private static readonly HashSet<string> NonParallelizableIntrinsicFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OBJECT_ID", "IDENT_CURRENT",
        "ERROR_NUMBER", "ERROR_MESSAGE", "ERROR_LINE", "ERROR_SEVERITY", "ERROR_STATE", "ERROR_PROCEDURE",
    };

    public static IReadOnlyList<ForcedSerialFinding> Scan(SqlParseResult parseResult)
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

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<ForcedSerialFinding> Findings { get; } = [];

        private readonly HashSet<string> _tableVariableNames = new(StringComparer.OrdinalIgnoreCase);

        private int _queryWithFromDepth;

        public override void ExplicitVisit(TSqlBatch node)
        {
            _tableVariableNames.Clear();
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeclareTableVariableStatement node)
        {
            _tableVariableNames.Add(node.Body.VariableName.Value);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InsertStatement node)
        {
            InspectDataModification(node.InsertSpecification);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            InspectDataModification(node.UpdateSpecification);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            InspectDataModification(node.DeleteSpecification);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            InspectDataModification(node.MergeSpecification);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeclareCursorStatement node)
        {
            InspectCursorDefinition(node.CursorDefinition, node.Name.Value);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SetVariableStatement node)
        {
            if (node.CursorDefinition is not null)
            {
                InspectCursorDefinition(node.CursorDefinition, node.Variable.Name);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            var hasFrom = node.FromClause is not null;
            if (hasFrom)
            {
                _queryWithFromDepth++;
            }

            base.ExplicitVisit(node);

            if (hasFrom)
            {
                _queryWithFromDepth--;
            }
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (_queryWithFromDepth > 0 && NonParallelizableIntrinsicFunctionNames.Contains(node.FunctionName.Value))
            {
                Findings.Add(new ForcedSerialFinding(
                    ForcedSerialFindingKind.NonParallelizableIntrinsic, sourcePath, sourcePath,
                    node.StartLine, node.StartColumn, DetailText: node.FunctionName.Value));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(GlobalVariableExpression node)
        {
            if (_queryWithFromDepth > 0 && string.Equals(node.Name, "@@TRANCOUNT", StringComparison.OrdinalIgnoreCase))
            {
                Findings.Add(new ForcedSerialFinding(
                    ForcedSerialFindingKind.NonParallelizableIntrinsic, sourcePath, sourcePath,
                    node.StartLine, node.StartColumn, DetailText: "@@TRANCOUNT"));
            }

            base.ExplicitVisit(node);
        }

        private void InspectDataModification(DataModificationSpecification spec)
        {
            var targetVariable = TableVariableName(spec.Target);
            var outputVariable = TableVariableName(spec.OutputIntoClause?.IntoTable);
            var variableName = targetVariable ?? outputVariable;
            if (variableName is null)
            {
                return;
            }

            Findings.Add(new ForcedSerialFinding(
                ForcedSerialFindingKind.TableVariableModification, sourcePath, sourcePath,
                spec.StartLine, spec.StartColumn, DetailText: variableName));
        }

        private string? TableVariableName(TableReference? tableReference) =>
            tableReference is VariableTableReference variableRef && _tableVariableNames.Contains(variableRef.Variable.Name)
                ? variableRef.Variable.Name
                : null;

        private void InspectCursorDefinition(CursorDefinition definition, string cursorName)
        {
            var kinds = definition.Options.Select(o => o.OptionKind).ToHashSet();

            // Oracle-confirmed (NonParallelPlanReason="NoParallelFastForwardCursor"): FAST_FORWARD
            // itself, or the equivalent bare FORWARD_ONLY READ_ONLY lacking an explicit
            // STATIC/KEYSET/DYNAMIC, forces the cursor's own defining query serial - the OPPOSITE
            // of "cursor without LOCAL FAST_FORWARD" as a risk shape. STATIC/KEYSET/DYNAMIC
            // cursors (with or without FORWARD_ONLY/READ_ONLY) were oracle-checked and do NOT
            // trigger this mechanism, so they are never matched here.
            var hasExplicitType = kinds.Contains(CursorOptionKind.Static) || kinds.Contains(CursorOptionKind.Keyset) || kinds.Contains(CursorOptionKind.Dynamic);
            var fires = kinds.Contains(CursorOptionKind.FastForward)
                || (kinds.Contains(CursorOptionKind.ForwardOnly) && kinds.Contains(CursorOptionKind.ReadOnly) && !hasExplicitType);

            if (!fires)
            {
                return;
            }

            Findings.Add(new ForcedSerialFinding(
                ForcedSerialFindingKind.FastForwardCursor, sourcePath, sourcePath,
                definition.StartLine, definition.StartColumn, DetailText: cursorName));
        }
    }
}
