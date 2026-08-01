using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Finds EXEC(@sql)/EXEC('...')/sp_executesql call sites (CLAUDE.md dynamic SQL policy) and
/// attempts to prove each argument is constant:
/// <list type="bullet">
/// <item>Tier A: the argument is already a literal, or a concatenation of bare literals.</item>
/// <item>Tier B: sp_executesql's own params-declaration argument gives exact parameter types.</item>
/// <item>Tier C: the argument traces back through a straight-line chain of
/// DECLARE/SET assignments with no intervening branch, loop, GOTO, or function call.</item>
/// </list>
/// A site that's provably constant is reassembled into a <see cref="DynamicSqlScript"/> for
/// <see cref="DynamicSqlPipeline"/> to reparse and analyze. Everything else is reported as
/// <see cref="DynamicSqlOutcome.Unanalyzable"/> immediately, with a specific reason for why
/// straight-line reasoning broke down - never silently treated as clean, and never guessed.
/// </summary>
public static class DynamicSqlScanner
{
    public static DynamicSqlExtractionResult Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        if (parseResult.Fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                visitor.WalkScope(batch.Statements);
            }
        }

        return new DynamicSqlExtractionResult(visitor.Findings, visitor.Scripts);
    }

    private readonly record struct LiteralSegment(string SourcePath, int StartLine, int StartColumn, int PrefixLength, string Value);

    private readonly record struct FoldAttempt(IReadOnlyList<LiteralSegment>? Segments, string? Reason, SourceSpan? Location)
    {
        public bool Success => Segments is not null;

        public static FoldAttempt Ok(IReadOnlyList<LiteralSegment> segments) => new(segments, null, null);

        public static FoldAttempt Fail(string reason, SourceSpan location) => new(null, reason, location);
    }

    private sealed class FoldState
    {
        public IReadOnlyList<LiteralSegment>? Segments { get; private init; }

        public string? TaintReason { get; private init; }

        public SourceSpan? TaintLocation { get; private init; }

        public static FoldState Constant(IReadOnlyList<LiteralSegment> segments) => new() { Segments = segments };

        public static FoldState Tainted(string reason, SourceSpan location) => new() { TaintReason = reason, TaintLocation = location };
    }

    private sealed class Visitor(string sourcePath)
    {
        private static readonly IReadOnlyDictionary<string, SqlType?> NoDeclaredParameters = new Dictionary<string, SqlType?>();

        public List<DynamicSqlFinding> Findings { get; } = [];

        public List<DynamicSqlScript> Scripts { get; } = [];

        /// <summary>Walks a fresh variable scope (a batch, or a proc/function body) in source order.</summary>
        public void WalkScope(IList<TSqlStatement> statements)
        {
            var folded = new Dictionary<string, FoldState>(StringComparer.OrdinalIgnoreCase);
            var foldingEnabled = !ContainsGotoOrLabel(statements);
            WalkStatements(statements, folded, foldingEnabled);
        }

        private void WalkStatements(IList<TSqlStatement> statements, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            foreach (var statement in statements)
            {
                WalkStatement(statement, folded, foldingEnabled);
            }
        }

        private void WalkStatement(TSqlStatement statement, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            switch (statement)
            {
                // ProcedureStatementBodyBase is the shared base of CREATE/ALTER/CREATE OR
                // ALTER PROCEDURE and CREATE/ALTER/CREATE OR ALTER FUNCTION alike - matching
                // on it (rather than each concrete statement type) is what catches the real-
                // world "stub CREATE PROCEDURE ... AS RETURN 0, then ALTER PROCEDURE for the
                // real body" pattern (seen verbatim in the First Responder Kit corpus repo),
                // which a CreateProcedureStatement-only match would silently never walk into.
                case ProcedureStatementBodyBase { StatementList: not null } procOrFunc:
                    WalkScope(procOrFunc.StatementList.Statements);
                    break;

                case ProcedureStatementBodyBase:
                    // A body-less declaration (CLR proc/function via EXTERNAL NAME, or an
                    // inline TVF whose body is a single RETURN expression, not a
                    // StatementList) - nothing to walk.
                    break;

                // Same reasoning for CREATE/ALTER/CREATE OR ALTER TRIGGER.
                case TriggerStatementBody { StatementList: not null } trigger:
                    WalkScope(trigger.StatementList.Statements);
                    break;

                case TriggerStatementBody:
                    break;

                case BeginEndBlockStatement block:
                    WalkStatements(block.StatementList.Statements, folded, foldingEnabled);
                    break;

                case DeclareVariableStatement declare:
                    HandleDeclare(declare, folded, foldingEnabled);
                    break;

                case SetVariableStatement set:
                    HandleSet(set, folded, foldingEnabled);
                    break;

                case SelectStatement select:
                    HandleSelectAssignments(select, folded, foldingEnabled);
                    break;

                case IfStatement ifStatement:
                    HandleIf(ifStatement, folded, foldingEnabled);
                    break;

                case WhileStatement whileStatement:
                    HandleWhile(whileStatement, folded, foldingEnabled);
                    break;

                case TryCatchStatement tryCatch:
                    HandleTryCatch(tryCatch, folded, foldingEnabled);
                    break;

                case ExecuteStatement execute:
                    HandleExecute(execute, folded, foldingEnabled);
                    break;

                case GoToStatement or LabelStatement:
                    // Already accounted for via ContainsGotoOrLabel at scope entry.
                    break;

                default:
                    // An unrecognized statement kind might mutate a tracked variable through a
                    // mechanism this scanner doesn't model (OUTPUT INTO, cursor FETCH INTO,
                    // RECEIVE ... INTO, ...). Precision-first: taint everything currently
                    // tracked rather than risk folding through a stale value.
                    TaintAll(folded, statement, "unsupported-statement-in-scope");
                    break;
            }
        }

        private void HandleDeclare(DeclareVariableStatement declare, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            foreach (var element in declare.Declarations)
            {
                var name = element.VariableName.Value;
                if (element.Value is null)
                {
                    folded[name] = FoldState.Tainted("no-initializer", Span(element));
                    continue;
                }

                var attempt = TryFoldExpression(element.Value, folded, foldingEnabled);
                folded[name] = attempt.Success
                    ? FoldState.Constant(attempt.Segments!)
                    : FoldState.Tainted(attempt.Reason!, attempt.Location!.Value);
            }
        }

        private void HandleSet(SetVariableStatement set, Dictionary<string, FoldState> folded, bool foldingEnabled) =>
            AssignVariable(set.Variable.Name, set.AssignmentKind, set.Expression, set.FunctionCallExists, set, folded, foldingEnabled);

        /// <summary>
        /// Handles <c>SELECT @x = expr[, @y = expr2, ...]</c>, the other common way T-SQL
        /// assigns local variables. Only the "pure assignment" shape - no FROM clause, every
        /// select element a variable assignment - is trustworthy: a FROM clause makes the
        /// assigned value data- and row-order-dependent, and a mix of real columns alongside
        /// an assignment is the same problem in miniature. Either way, the variables actually
        /// assigned are tainted rather than silently left at a stale value.
        /// </summary>
        private void HandleSelectAssignments(SelectStatement select, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (select.QueryExpression is QuerySpecification { FromClause: null } spec
                && spec.SelectElements.Count > 0
                && spec.SelectElements.All(e => e is SelectSetVariable))
            {
                foreach (var element in spec.SelectElements.Cast<SelectSetVariable>())
                {
                    AssignVariable(element.Variable.Name, element.AssignmentKind, element.Expression, functionCallExists: false, element, folded, foldingEnabled);
                }

                return;
            }

            foreach (var name in CollectSelectSetVariableNames(select))
            {
                folded[name] = FoldState.Tainted("select-assignment-not-pure", Span(select));
            }
        }

        private void AssignVariable(
            string name, AssignmentKind kind, ScalarExpression expression, bool functionCallExists, TSqlFragment site, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (functionCallExists || kind is not (AssignmentKind.Equals or AssignmentKind.AddEquals))
            {
                folded[name] = FoldState.Tainted("unsupported-assignment", Span(site));
                return;
            }

            var rhs = TryFoldExpression(expression, folded, foldingEnabled);

            if (kind == AssignmentKind.AddEquals)
            {
                if (!folded.TryGetValue(name, out var existing) || existing.Segments is null)
                {
                    folded[name] = FoldState.Tainted(existing?.TaintReason ?? "undeclared-variable", existing?.TaintLocation ?? Span(site));
                    return;
                }

                folded[name] = rhs.Success
                    ? FoldState.Constant([.. existing.Segments, .. rhs.Segments!])
                    : FoldState.Tainted(rhs.Reason!, rhs.Location!.Value);
                return;
            }

            folded[name] = rhs.Success
                ? FoldState.Constant(rhs.Segments!)
                : FoldState.Tainted(rhs.Reason!, rhs.Location!.Value);
        }

        private void HandleIf(IfStatement ifStatement, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            var thenDict = new Dictionary<string, FoldState>(folded, StringComparer.OrdinalIgnoreCase);
            WalkStatements(NormalizeToStatementList(ifStatement.ThenStatement), thenDict, foldingEnabled);

            var elseDict = new Dictionary<string, FoldState>(folded, StringComparer.OrdinalIgnoreCase);
            if (ifStatement.ElseStatement is not null)
            {
                WalkStatements(NormalizeToStatementList(ifStatement.ElseStatement), elseDict, foldingEnabled);
            }

            MergeTaintingDivergent(folded, thenDict, elseDict, ifStatement, "diverges-across-if-branches");
        }

        private void HandleWhile(WhileStatement whileStatement, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            var bodyDict = new Dictionary<string, FoldState>(folded, StringComparer.OrdinalIgnoreCase);
            WalkStatements(NormalizeToStatementList(whileStatement.Statement), bodyDict, foldingEnabled);

            // A while body may run zero, one, or many times, so nothing it touches can be
            // trusted after the loop - but statements *inside* the body (already walked into
            // bodyDict above, which is where any EXEC inside the loop was evaluated) may still
            // fold using the state as of loop entry, since that's valid on the first iteration.
            MergeTaintingDivergent(folded, bodyDict, folded, whileStatement, "while-loop-body");
        }

        private void HandleTryCatch(TryCatchStatement tryCatch, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            var tryDict = new Dictionary<string, FoldState>(folded, StringComparer.OrdinalIgnoreCase);
            WalkStatements(tryCatch.TryStatements.Statements, tryDict, foldingEnabled);

            // CATCH only runs if TRY throws mid-way, so how far TRY got is unknowable - CATCH
            // starts from the pre-TRY state, not tryDict, however far WalkStatements got.
            var catchDict = new Dictionary<string, FoldState>(folded, StringComparer.OrdinalIgnoreCase);
            WalkStatements(tryCatch.CatchStatements.Statements, catchDict, foldingEnabled);

            MergeTaintingDivergent(folded, tryDict, catchDict, tryCatch, "diverges-across-try-catch");
        }

        /// <summary>
        /// Because <paramref name="branchA"/>/<paramref name="branchB"/> start as shallow
        /// clones of <paramref name="folded"/>, an entry a branch never touched still holds
        /// the exact same <see cref="FoldState"/> reference as in <paramref name="folded"/> -
        /// so reference (in)equality alone tells us whether either branch could have changed a
        /// variable, with no content comparison needed. Any real divergence taints; this
        /// deliberately never tries to prove two branches produced textually-equal values.
        /// </summary>
        private void MergeTaintingDivergent(
            Dictionary<string, FoldState> folded, Dictionary<string, FoldState> branchA, Dictionary<string, FoldState> branchB, TSqlStatement owner, string reason)
        {
            var touched = new HashSet<string>(branchA.Keys, StringComparer.OrdinalIgnoreCase);
            touched.UnionWith(branchB.Keys);

            foreach (var key in touched)
            {
                var before = folded.GetValueOrDefault(key);
                if (!ReferenceEquals(before, branchA.GetValueOrDefault(key)) || !ReferenceEquals(before, branchB.GetValueOrDefault(key)))
                {
                    folded[key] = FoldState.Tainted(reason, Span(owner));
                }
            }
        }

        private void HandleExecute(ExecuteStatement node, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            switch (node.ExecuteSpecification.ExecutableEntity)
            {
                case ExecutableStringList stringList:
                    HandleStringList(stringList, node, folded, foldingEnabled);
                    break;

                case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var name } procRef
                    when string.Equals(name, "sp_executesql", StringComparison.OrdinalIgnoreCase):
                    HandleSpExecuteSql(procRef, node, folded, foldingEnabled);
                    break;
            }
        }

        private void HandleStringList(ExecutableStringList stringList, ExecuteStatement node, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            // ExecutableStringList.Strings is never empty for a successfully parsed
            // ExecuteStatement - EXEC() with no argument is a syntax error, not a valid
            // zero-element call.
            var segments = new List<LiteralSegment>();
            foreach (var element in stringList.Strings)
            {
                var attempt = TryFoldExpression(element, folded, foldingEnabled);
                if (!attempt.Success)
                {
                    Findings.Add(Unanalyzable(node, attempt.Reason!));
                    return;
                }

                segments.AddRange(attempt.Segments!);
            }

            Scripts.Add(BuildScript(node, segments, NoDeclaredParameters));
        }

        private void HandleSpExecuteSql(ExecutableProcedureReference procRef, ExecuteStatement node, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (procRef.Parameters.Count == 0)
            {
                Findings.Add(Unanalyzable(node, "non-literal-argument"));
                return;
            }

            var queryAttempt = TryFoldExpression(procRef.Parameters[0].ParameterValue, folded, foldingEnabled);
            if (!queryAttempt.Success)
            {
                Findings.Add(Unanalyzable(node, queryAttempt.Reason!));
                return;
            }

            Scripts.Add(BuildScript(node, queryAttempt.Segments!, ResolveDeclaredParameters(procRef, folded, foldingEnabled)));
        }

        /// <summary>
        /// sp_executesql's optional second argument declares its parameters' exact types
        /// (Tier B) - e.g. <c>N'@DisplayName nvarchar(40)'</c>. Missing, unfoldable, or
        /// unparseable falls back to no declared types rather than guessing; predicates
        /// against an undeclared parameter simply resolve to Unknown, same as any other
        /// unresolvable operand.
        /// </summary>
        private IReadOnlyDictionary<string, SqlType?> ResolveDeclaredParameters(ExecutableProcedureReference procRef, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (procRef.Parameters.Count < 2)
            {
                return NoDeclaredParameters;
            }

            var attempt = TryFoldExpression(procRef.Parameters[1].ParameterValue, folded, foldingEnabled);
            if (!attempt.Success)
            {
                return NoDeclaredParameters;
            }

            var declarationText = string.Concat(attempt.Segments!.Select(s => s.Value));
            return DynamicSqlParameterDeclarations.TryParse(declarationText) ?? NoDeclaredParameters;
        }

        private DynamicSqlScript BuildScript(ExecuteStatement node, IReadOnlyList<LiteralSegment> segments, IReadOnlyDictionary<string, SqlType?> declaredParameters)
        {
            var segmentMap = new DynamicSqlSegmentMap();
            foreach (var segment in segments)
            {
                segmentMap.AppendLiteral(segment.SourcePath, segment.StartLine, segment.StartColumn, segment.PrefixLength, segment.Value);
            }

            return new DynamicSqlScript(CallSite(node), segmentMap.InnerText, segmentMap, declaredParameters);
        }

        /// <summary>
        /// Attempts to prove <paramref name="expression"/> constant: a bare literal, a
        /// variable whose own value is already known-constant, string concatenation
        /// (<c>+</c>) of foldable operands, or a foldable expression in parentheses. Anything
        /// else (a function call, a column reference, an unsupported operator, a variable that
        /// couldn't be pinned down) fails with a specific reason and the location responsible.
        /// </summary>
        private FoldAttempt TryFoldExpression(ScalarExpression expression, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            switch (expression)
            {
                case StringLiteral literal:
                    var prefixLength = literal.IsNational ? 2 : 1;
                    return FoldAttempt.Ok([new LiteralSegment(sourcePath, literal.StartLine, literal.StartColumn, prefixLength, literal.Value)]);

                case VariableReference variableRef:
                    if (!foldingEnabled)
                    {
                        return FoldAttempt.Fail("goto-or-label-in-scope", Span(variableRef));
                    }

                    if (!folded.TryGetValue(variableRef.Name, out var state))
                    {
                        return FoldAttempt.Fail("undeclared-variable", Span(variableRef));
                    }

                    return state.Segments is not null
                        ? FoldAttempt.Ok(state.Segments)
                        : FoldAttempt.Fail(state.TaintReason!, state.TaintLocation!.Value);

                case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } binary:
                    var left = TryFoldExpression(binary.FirstExpression, folded, foldingEnabled);
                    if (!left.Success)
                    {
                        return left;
                    }

                    var right = TryFoldExpression(binary.SecondExpression, folded, foldingEnabled);
                    if (!right.Success)
                    {
                        return right;
                    }

                    return FoldAttempt.Ok([.. left.Segments!, .. right.Segments!]);

                case ParenthesisExpression paren:
                    return TryFoldExpression(paren.Expression, folded, foldingEnabled);

                default:
                    return FoldAttempt.Fail("non-literal-expression", Span(expression));
            }
        }

        private void TaintAll(Dictionary<string, FoldState> folded, TSqlStatement statement, string reason)
        {
            var location = Span(statement);
            foreach (var key in folded.Keys.ToList())
            {
                folded[key] = FoldState.Tainted(reason, location);
            }
        }

        private static IList<TSqlStatement> NormalizeToStatementList(TSqlStatement statement) =>
            statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];

        private static bool ContainsGotoOrLabel(IList<TSqlStatement> statements)
        {
            var detector = new GotoLabelDetector();
            foreach (var statement in statements)
            {
                statement.Accept(detector);
                if (detector.Found)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> CollectSelectSetVariableNames(TSqlFragment fragment)
        {
            var collector = new SelectSetVariableCollector();
            fragment.Accept(collector);
            return collector.Names;
        }

        private SourceSpan CallSite(ExecuteStatement node) => Span(node);

        private DynamicSqlFinding Unanalyzable(ExecuteStatement node, string reason) =>
            new(sourcePath, node.StartLine, node.StartColumn, DynamicSqlOutcome.Unanalyzable, reason);

        private SourceSpan Span(TSqlFragment fragment) => new(sourcePath, fragment.StartLine, fragment.StartColumn);

        private sealed class GotoLabelDetector : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void Visit(GoToStatement node) => Found = true;

            public override void Visit(LabelStatement node) => Found = true;
        }

        private sealed class SelectSetVariableCollector : TSqlFragmentVisitor
        {
            public List<string> Names { get; } = [];

            public override void Visit(SelectSetVariable node) => Names.Add(node.Variable.Name);
        }
    }
}

/// <summary>Everything <see cref="DynamicSqlScanner.Scan"/> found in one parsed file: definite unanalyzable findings, and candidate scripts ready for <see cref="DynamicSqlPipeline"/> to reparse.</summary>
public sealed record DynamicSqlExtractionResult(
    IReadOnlyList<DynamicSqlFinding> Findings,
    IReadOnlyList<DynamicSqlScript> AnalyzableScripts);
