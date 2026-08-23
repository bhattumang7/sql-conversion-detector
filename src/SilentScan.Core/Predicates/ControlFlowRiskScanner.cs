using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class ControlFlowRiskScanner
{
    public static IReadOnlyList<ControlFlowRiskFinding> Scan(SqlParseResult parseResult)
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
        public List<ControlFlowRiskFinding> Findings { get; } = [];

        private string _moduleName = "(batch)";
        private bool _inTrigger;

        private readonly Dictionary<string, int?> _cursorColumnCounts = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<SelectStatement> _cursorDefiningSelects = new(ReferenceEqualityComparer.Instance);

        private static readonly HashSet<string> NonDeterministicFunctionNames =
            new(StringComparer.OrdinalIgnoreCase) { "NEWID", "RAND", "CRYPT_GEN_RANDOM" };

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            EnterRoutine(QualifiedName(node.ProcedureReference.Name), isTrigger: false);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

        public override void ExplicitVisit(AlterProcedureStatement node)
        {
            EnterRoutine(QualifiedName(node.ProcedureReference.Name), isTrigger: false);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node)
        {
            EnterRoutine(QualifiedName(node.ProcedureReference.Name), isTrigger: false);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

        public override void ExplicitVisit(CreateTriggerStatement node)
        {
            EnterRoutine(QualifiedName(node.Name), isTrigger: true);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

        public override void ExplicitVisit(AlterTriggerStatement node)
        {
            EnterRoutine(QualifiedName(node.Name), isTrigger: true);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node)
        {
            EnterRoutine(QualifiedName(node.Name), isTrigger: true);
            base.ExplicitVisit(node);
            ExitRoutine();
        }

        public override void ExplicitVisit(DeclareCursorStatement node)
        {
            _cursorColumnCounts[node.Name.Value] = TryCountSelectColumns(node.CursorDefinition.Select);

            if (node.CursorDefinition.Select is { } cursorSelect)
            {
                _cursorDefiningSelects.Add(cursorSelect);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FetchCursorStatement node)
        {
            var name = node.Cursor?.Name?.Value;
            if (name is not null
                && _cursorColumnCounts.TryGetValue(name, out var declaredCount)
                && declaredCount is { } declared
                && node.IntoVariables is { Count: > 0 } intoVariables
                && intoVariables.Count != declared)
            {
                Findings.Add(new ControlFlowRiskFinding(
                    ControlFlowRiskFindingKind.CursorFetchColumnCountMismatch,
                    _moduleName, sourcePath, node.StartLine, node.StartColumn,
                    $"FETCH INTO lists {intoVariables.Count} variable(s) but cursor '{name}' selects " +
                    $"{declared} column(s) - this FETCH always fails at runtime (Msg 16924).",
                    FindingConfidence.High));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TryCatchStatement node)
        {
            if (node.CatchStatements.Statements.Count == 0)
            {
                Findings.Add(new ControlFlowRiskFinding(
                    ControlFlowRiskFindingKind.EmptyCatchBlock,
                    _moduleName, sourcePath, node.StartLine, node.StartColumn,
                    "This CATCH block has no statements at all - every error reaching it is silently swallowed.",
                    FindingConfidence.High));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (_inTrigger && !IsAssignmentOnlySelect(node) && !_cursorDefiningSelects.Contains(node))
            {
                Findings.Add(new ControlFlowRiskFinding(
                    ControlFlowRiskFindingKind.TriggerEmitsOutput,
                    _moduleName, sourcePath, node.StartLine, node.StartColumn,
                    "A SELECT with a real result set inside a trigger sends output back to whatever connection fired the DML, not the application that issued it.",
                    FindingConfidence.Medium));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(PrintStatement node)
        {
            if (_inTrigger)
            {
                Findings.Add(new ControlFlowRiskFinding(
                    ControlFlowRiskFindingKind.TriggerEmitsOutput,
                    _moduleName, sourcePath, node.StartLine, node.StartColumn,
                    "A PRINT inside a trigger sends a message back to whatever connection fired the DML, not the application that issued it.",
                    FindingConfidence.Medium));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TableHint node)
        {
            if (node.HintKind is TableHintKind.NoLock or TableHintKind.ReadUncommitted)
            {
                Findings.Add(new ControlFlowRiskFinding(
                    ControlFlowRiskFindingKind.DirtyReadIsolationHint,
                    _moduleName, sourcePath, node.StartLine, node.StartColumn,
                    "NOLOCK/READUNCOMMITTED allows dirty reads of uncommitted, possibly-rolled-back data, and can miss or double-count rows during a concurrent page split.",
                    FindingConfidence.Low));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SetTransactionIsolationLevelStatement node)
        {
            if (node.Level == IsolationLevel.ReadUncommitted)
            {
                Findings.Add(new ControlFlowRiskFinding(
                    ControlFlowRiskFindingKind.DirtyReadIsolationHint,
                    _moduleName, sourcePath, node.StartLine, node.StartColumn,
                    "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED allows dirty reads for the rest of the session.",
                    FindingConfidence.Low));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(GlobalVariableExpression node)
        {
            if (string.Equals(node.Name, "@@IDENTITY", StringComparison.OrdinalIgnoreCase))
            {
                Findings.Add(new ControlFlowRiskFinding(
                    ControlFlowRiskFindingKind.LegacyIdentityIntrinsic,
                    _moduleName, sourcePath, node.StartLine, node.StartColumn,
                    "@@IDENTITY returns the last identity value inserted in this SESSION across ANY table/scope, including one inserted by a trigger fired as a side effect - prefer SCOPE_IDENTITY() unless that broader semantics is specifically wanted.",
                    FindingConfidence.Medium));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(GoToStatement node)
        {
            Findings.Add(new ControlFlowRiskFinding(
                ControlFlowRiskFindingKind.GotoUsage,
                _moduleName, sourcePath, node.StartLine, node.StartColumn,
                "GOTO makes control flow harder to follow, and this codebase's own dead-code analysis " +
                "already declines its entire reachability check for any routine that contains one.",
                FindingConfidence.High));

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SimpleCaseExpression node)
        {
            if (node.ElseExpression is null)
            {
                Findings.Add(new ControlFlowRiskFinding(
                    ControlFlowRiskFindingKind.CaseExpressionMissingElse,
                    _moduleName, sourcePath, node.StartLine, node.StartColumn,
                    "This simple CASE has no ELSE - an input value matching none of the WHEN values " +
                    "silently evaluates to NULL, no error raised.",
                    FindingConfidence.High));
            }

            if (ContainsNonDeterministicCall(node.InputExpression) is { } functionName)
            {
                Findings.Add(new ControlFlowRiskFinding(
                    ControlFlowRiskFindingKind.NonDeterministicCaseInput,
                    _moduleName, sourcePath, node.StartLine, node.StartColumn,
                    $"{functionName}() as a CASE input is re-evaluated separately for each WHEN " +
                    "comparison, not once - every WHEN branch is effectively unreachable and this " +
                    "always falls through to ELSE (or NULL, with no ELSE).",
                    FindingConfidence.High));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            ReportDuplicatedArguments(node.Parameters?.Select(p => p.ParameterValue) ?? []);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (!string.Equals(node.FunctionName?.Value, "FORMATMESSAGE", StringComparison.OrdinalIgnoreCase))
            {
                ReportDuplicatedArguments(node.Parameters ?? []);
            }

            base.ExplicitVisit(node);
        }

        private static string QualifiedName(SchemaObjectName name) =>
            name.SchemaIdentifier is { } schema
                ? $"{schema.Value}.{name.BaseIdentifier.Value}"
                : name.BaseIdentifier.Value;

        private void EnterRoutine(string name, bool isTrigger)
        {
            _moduleName = name;
            _inTrigger = isTrigger;
            _cursorColumnCounts.Clear();
            _cursorDefiningSelects.Clear();
        }

        private void ExitRoutine()
        {
            _inTrigger = false;
            _cursorColumnCounts.Clear();
            _cursorDefiningSelects.Clear();
        }

private static string? ContainsNonDeterministicCall(ScalarExpression expression)
        {
            var finder = new NonDeterministicCallFinder();
            expression.Accept(finder);
            return finder.FoundFunctionName;
        }

        private sealed class NonDeterministicCallFinder : TSqlFragmentVisitor
        {
            public string? FoundFunctionName { get; private set; }

            public override void ExplicitVisit(FunctionCall node)
            {
                if (FoundFunctionName is null
                    && node.FunctionName?.Value is { } name
                    && NonDeterministicFunctionNames.Contains(name))
                {
                    FoundFunctionName = name.ToUpperInvariant();
                }

                base.ExplicitVisit(node);
            }
        }

        private void ReportDuplicatedArguments(IEnumerable<ScalarExpression?> arguments)
        {
            var nonLiteral = arguments.Where(a => a is not null and not Literal).Cast<ScalarExpression>().ToList();

            for (var i = 0; i < nonLiteral.Count; i++)
            {
                for (var j = i + 1; j < nonLiteral.Count; j++)
                {
                    if (string.Equals(
                        FragmentTextRenderer.Render(nonLiteral[i]),
                        FragmentTextRenderer.Render(nonLiteral[j]),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        Findings.Add(new ControlFlowRiskFinding(
                            ControlFlowRiskFindingKind.DuplicatedCallArgument,
                            _moduleName, sourcePath, nonLiteral[j].StartLine, nonLiteral[j].StartColumn,
                            "This argument is structurally identical to another argument in the same call - verify this isn't a copy-paste mistake naming the wrong parameter.",
                            FindingConfidence.Medium));

                        break;
                    }
                }
            }
        }

private static bool IsAssignmentOnlySelect(SelectStatement node)
        {
            if (node.Into is not null)
            {
                return true;
            }

            return node.QueryExpression is QuerySpecification { SelectElements: { Count: > 0 } elements }
                && elements.All(e => e is SelectSetVariable);
        }

private static int? TryCountSelectColumns(SelectStatement select) =>
            select.QueryExpression is QuerySpecification { SelectElements: { Count: > 0 } elements }
            && elements.All(e => e is SelectScalarExpression)
                ? elements.Count
                : null;
    }
}
