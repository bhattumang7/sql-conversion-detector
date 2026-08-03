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
    /// <summary>
    /// <paramref name="enclosingScope"/> seeds the scope a call site found at the TOP of
    /// <paramref name="parseResult"/> is considered inside - null (the default) for an ordinary
    /// top-level file scan. <see cref="DynamicSqlPipeline"/> passes the outer script's own
    /// scope here when reparsing a NESTED dynamic SQL fragment, so scope propagation survives
    /// however many nesting levels deep a call site sits (the reparsed fragment itself never
    /// contains a CREATE PROCEDURE wrapper to discover the scope from directly).
    /// <paramref name="callGraph"/>, when supplied, lets a proc body's OWN formal parameters
    /// seed as constant-foldable when this scan saw exactly one caller passing a string literal
    /// for that parameter (roadmap "trace provably-constant dynamic SQL across proc-call
    /// edges") - null (the default) leaves every parameter reference unseeded, exactly like
    /// before this capability existed.
    /// </summary>
    public static DynamicSqlExtractionResult Scan(SqlParseResult parseResult, DynamicSqlScope? enclosingScope = null, ProcCallGraph? callGraph = null)
    {
        var visitor = new Visitor(parseResult.SourcePath, enclosingScope ?? DynamicSqlScope.None, callGraph);
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

    private sealed class Visitor(string sourcePath, DynamicSqlScope initialScope, ProcCallGraph? callGraph)
    {

        private DynamicSqlScope _scope = initialScope;

        public List<DynamicSqlFinding> Findings { get; } = [];

        public List<DynamicSqlScript> Scripts { get; } = [];

        /// <summary>Walks a fresh variable scope (a batch, or a proc/function body) in source order.</summary>
        public void WalkScope(IList<TSqlStatement> statements, IReadOnlyDictionary<string, FoldState>? initialSeed = null)
        {
            var folded = new Dictionary<string, FoldState>(StringComparer.OrdinalIgnoreCase);
            if (initialSeed is not null)
            {
                foreach (var (name, state) in initialSeed)
                {
                    folded[name] = state;
                }
            }

            var foldingEnabled = !ContainsGotoOrLabel(statements);
            WalkStatements(statements, folded, foldingEnabled);
        }

        /// <summary>A proc/function body's fresh variable scope, additionally recording its own qualified name as the enclosing scope for any dynamic SQL call site found inside - mirrors CatalogBuilder.VisitScopedBody's identical save/restore.</summary>
        private void WalkScopedBody(SchemaObjectName? name, IList<ProcedureParameter>? formalParameters, IList<TSqlStatement> statements)
        {
            var previousScope = _scope;
            string? qualifiedName = name is null ? null : SchemaObjectNameHelper.Qualify(name);
            _scope = qualifiedName is null ? _scope : new DynamicSqlScope(qualifiedName, _scope.TriggerTarget);

            var seed = qualifiedName is not null && formalParameters is { Count: > 0 }
                ? BuildParameterSeed(qualifiedName, formalParameters)
                : null;
            WalkScope(statements, seed);
            _scope = previousScope;
        }

        /// <summary>
        /// Seeds a proc body's own formal parameters as constant-foldable when the call graph
        /// saw exactly one caller passing a string literal for that parameter - see
        /// <see cref="ProcCallGraph.SingleCallSiteFor"/> for why "exactly one call site THIS
        /// SCAN saw" is the only case a single value can be trusted at all. A parameter with
        /// zero call sites is left unseeded entirely (falls back to today's plain
        /// "undeclared-variable" if referenced - unchanged behavior, not a regression). A
        /// parameter seen at MULTIPLE call sites, or passed something other than a string
        /// literal at its one call site, is explicitly tainted with its own reason rather than
        /// silently falling through to the generic "undeclared-variable" a caller-blind scan
        /// would report - CLAUDE.md's "never silently counted as clean" applies to the REASON a
        /// dynamic SQL site is unanalyzable exactly as much as to whether it is.
        /// </summary>
        private Dictionary<string, FoldState>? BuildParameterSeed(string qualifiedName, IList<ProcedureParameter> formalParameters)
        {
            if (callGraph is null)
            {
                return null;
            }

            var edges = callGraph.EdgesCalling(qualifiedName).ToList();
            if (edges.Count == 0)
            {
                return null;
            }

            var singleEdge = edges.Count == 1 ? edges[0] : null;
            var seed = new Dictionary<string, FoldState>(StringComparer.OrdinalIgnoreCase);
            foreach (var formal in formalParameters)
            {
                var paramName = formal.VariableName.Value;
                var location = Span(formal);

                if (singleEdge is null)
                {
                    seed[paramName] = FoldState.Tainted("parameter-not-seeded:multiple-call-sites", location);
                    continue;
                }

                var argument = singleEdge.Arguments.FirstOrDefault(
                    a => string.Equals(a.FormalParameterName, paramName, StringComparison.OrdinalIgnoreCase));
                if (argument is null || argument.FormalParameterIsOutput)
                {
                    // No matching actual argument (a default value applies) or an OUTPUT
                    // parameter (flows the other direction) - nothing to seed, left unseeded.
                    continue;
                }

                seed[paramName] = argument.LiteralArgument is { } literalArgument
                    ? FoldState.Constant([new LiteralSegment(
                        literalArgument.SourcePath, literalArgument.StartLine, literalArgument.StartColumn,
                        literalArgument.PrefixLength, literalArgument.Value)])
                    : FoldState.Tainted("parameter-not-seeded:non-literal-caller", singleEdge.CallSite);
            }

            return seed;
        }

        /// <summary>Same save/restore as <see cref="WalkScopedBody"/>, plus the trigger's own target table/view (null for a DDL/LOGON trigger, which has no inserted/deleted rowset at all) so a dynamic SQL call site inside the body can resolve inserted/deleted the same way it would statically.</summary>
        private void WalkTriggerBody(TriggerStatementBody trigger)
        {
            var previousScope = _scope;
            _scope = new DynamicSqlScope(SchemaObjectNameHelper.Qualify(trigger.Name), trigger.TriggerObject.Name);
            WalkScope(trigger.StatementList.Statements);
            _scope = previousScope;
        }

        private static SchemaObjectName? ProcedureOrFunctionName(ProcedureStatementBodyBase procOrFunc) => procOrFunc switch
        {
            ProcedureStatementBody proc => proc.ProcedureReference.Name,
            FunctionStatementBody func => func.Name,
            _ => null,
        };

        // Only ProcedureStatementBody's parameters are ever reachable from ProcCallGraph
        // (built from EXEC ... call sites, never from a function invocation) - a function's own
        // parameters are returned as null here rather than [], so BuildParameterSeed's
        // `formalParameters is { Count: > 0 }` guard skips the lookup entirely instead of
        // querying a call graph that could never have an edge for it anyway.
        private static IList<ProcedureParameter>? ProcedureOrFunctionParameters(ProcedureStatementBodyBase procOrFunc) =>
            procOrFunc is ProcedureStatementBody proc ? proc.Parameters : null;

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
                    WalkScopedBody(ProcedureOrFunctionName(procOrFunc), ProcedureOrFunctionParameters(procOrFunc), procOrFunc.StatementList.Statements);
                    break;

                case ProcedureStatementBodyBase:
                    // A body-less declaration (CLR proc/function via EXTERNAL NAME, or an
                    // inline TVF whose body is a single RETURN expression, not a
                    // StatementList) - nothing to walk.
                    break;

                // Same reasoning for CREATE/ALTER/CREATE OR ALTER TRIGGER.
                case TriggerStatementBody { StatementList: not null } trigger:
                    WalkTriggerBody(trigger);
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
            if (select.QueryExpression is QuerySpecification { FromClause: null, WhereClause: null, HavingClause: null, TopRowFilter: null } spec
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
            string name, AssignmentKind kind, ScalarExpression? expression, bool functionCallExists, TSqlFragment site, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (functionCallExists || kind is not (AssignmentKind.Equals or AssignmentKind.AddEquals))
            {
                folded[name] = FoldState.Tainted("unsupported-assignment", Span(site));
                return;
            }

            // SetVariableStatement.Expression / SelectSetVariable.Expression are null when the RHS
            // is a shape ScriptDOM models in a sibling property instead of Expression: SET @c =
            // CURSOR FOR ..., SET @x = IDENTITY(int,1,1), SET @x = <identifier>. None can be folded
            // into a dynamic SQL string; taint so any downstream EXEC reports Unanalyzable instead
            // of crashing inside TryFoldExpression's null path.
            if (expression is null)
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
            var bodyStatements = NormalizeToStatementList(whileStatement.Statement);
            var bodyDict = new Dictionary<string, FoldState>(folded, StringComparer.OrdinalIgnoreCase);

            // Any variable the loop body assigns ANYWHERE within it - even after an EXEC that
            // reads it in program order - cannot be trusted at that EXEC either: the value is
            // only "as of loop entry" on iteration 1, and this scanner walks the body exactly
            // once, so an EXEC folded against entry-state would silently analyze different SQL
            // than what runs on iteration 2+ while still reporting AnalyzedLiteral. Taint those
            // variables BEFORE walking so any EXEC inside the body referencing one comes out
            // Unanalyzable instead. A variable the body never assigns is untouched here and can
            // still fold normally using the state as of loop entry.
            var assignedInBody = CollectAssignedVariableNames(bodyStatements);
            foreach (var name in assignedInBody)
            {
                bodyDict[name] = FoldState.Tainted("while-loop-body-self-mutates", Span(whileStatement));
            }

            WalkStatements(bodyStatements, bodyDict, foldingEnabled);

            // A while body may run zero, one, or many times, so nothing it touches can be
            // trusted after the loop either.
            MergeTaintingDivergent(folded, bodyDict, folded, whileStatement, "while-loop-body");
        }

        private static HashSet<string> CollectAssignedVariableNames(IList<TSqlStatement> statements)
        {
            var collector = new AssignedVariableCollector();
            foreach (var statement in statements)
            {
                statement.Accept(collector);
            }

            return collector.Names;
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

                default:
                    // An ordinary `EXEC dbo.SomeProc @sql OUTPUT` (or `EXEC @rc = proc ...`) -
                    // this scanner has no visibility into what the called procedure does to an
                    // OUTPUT argument or the return-value variable. Without this, such a call
                    // fell through doing nothing at all (unlike every other unrecognized
                    // construct, which taints via the WalkStatement default case) - a variable
                    // later folded as "constant" could actually hold whatever the proc just
                    // wrote to it, and a subsequent EXEC(@sql) would analyze SQL that never
                    // runs while reporting AnalyzedLiteral. Taint every variable this call
                    // could plausibly have mutated (all OUTPUT/return-assigned arguments, or
                    // conservatively everything tracked when we can't tell which).
                    TaintExecuteMutatedVariables(node, folded);
                    break;
            }
        }

        /// <summary>
        /// Taints the return-value variable (<c>EXEC @rc = proc</c>) and every argument passed
        /// with OUTPUT, plus - conservatively, since this scanner does not model what an
        /// arbitrary called procedure does internally - every other variable currently tracked,
        /// matching the same fail-safe the WalkStatement default case already uses for any
        /// other statement kind it doesn't specifically model.
        /// </summary>
        private void TaintExecuteMutatedVariables(ExecuteStatement node, Dictionary<string, FoldState> folded)
        {
            TaintAll(folded, node, "unsupported-execute-form");
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

            Scripts.Add(BuildScript(node, segments, parameterDeclarationText: null, argumentBindings: null));
        }

        private void HandleSpExecuteSql(ExecutableProcedureReference procRef, ExecuteStatement node, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (procRef.Parameters.Count == 0)
            {
                Findings.Add(Unanalyzable(node, "non-literal-argument"));
                return;
            }

            var statementArg = ResolveNamedOrPositionalArgument(procRef.Parameters, index: 0, "@stmt", "@statement");
            if (statementArg is null)
            {
                Findings.Add(Unanalyzable(node, "non-literal-argument"));
                return;
            }

            var queryAttempt = TryFoldExpression(statementArg, folded, foldingEnabled);
            if (!queryAttempt.Success)
            {
                Findings.Add(Unanalyzable(node, queryAttempt.Reason!));
                return;
            }

            Scripts.Add(BuildScript(
                node,
                queryAttempt.Segments!,
                ResolveParameterDeclarationText(procRef, folded, foldingEnabled),
                ResolveArgumentBindings(procRef)));
        }

        /// <summary>
        /// Every named execute-parameter beyond @stmt/@params (e.g. <c>@P = @Code</c>) whose
        /// value is a bare variable reference - <see cref="DynamicSqlScript.ArgumentBindings"/>'s
        /// own doc comment explains why this is captured unconditionally, for every call
        /// regardless of nesting depth, rather than only when a caller is known to need it.
        /// </summary>
        private static Dictionary<string, string>? ResolveArgumentBindings(ExecutableProcedureReference procRef)
        {
            Dictionary<string, string>? bindings = null;
            foreach (var parameter in procRef.Parameters)
            {
                if (parameter.Variable is not { } formalName
                    || ReservedArgumentNames.Contains(formalName.Name)
                    || parameter.ParameterValue is not VariableReference valueVariable)
                {
                    continue;
                }

                bindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bindings[formalName.Name] = valueVariable.Name;
            }

            return bindings;
        }

        private static readonly HashSet<string> ReservedArgumentNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "@stmt", "@statement", "@params", "@parameters",
        };

        /// <summary>
        /// sp_executesql's optional second argument declares its parameters' exact types
        /// (Tier B) - e.g. <c>N'@DisplayName nvarchar(40)'</c>. Missing or unfoldable falls
        /// back to null rather than guessing. The raw text is returned as-is, not parsed here -
        /// see <see cref="DynamicSqlScript.ParameterDeclarationText"/> for why parsing is
        /// deferred to <see cref="DynamicSqlPipeline"/>, where a real catalog exists.
        /// </summary>
        private string? ResolveParameterDeclarationText(ExecutableProcedureReference procRef, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            var paramsArg = ResolveNamedOrPositionalArgument(procRef.Parameters, index: 1, "@params", "@parameters");
            if (paramsArg is null)
            {
                return null;
            }

            var attempt = TryFoldExpression(paramsArg, folded, foldingEnabled);
            if (!attempt.Success)
            {
                return null;
            }

            return string.Concat(attempt.Segments!.Select(s => s.Value));
        }

        /// <summary>
        /// sp_executesql's own @stmt/@params arguments can be passed by name
        /// (<c>EXEC sp_executesql @params = @paramDecl, @stmt = @sql</c>, order-independent) -
        /// the same T-SQL calling convention any procedure supports. Note this is distinct
        /// from - and must not be confused with - the very common pattern where @stmt/@params
        /// ARE positional but LATER arguments are named after the query's own declared
        /// parameters (<c>EXEC sp_executesql @sql, N'@DisplayName nvarchar(40)', @DisplayName
        /// = @x</c>): the presence of ANY named argument does not mean every argument is named,
        /// so this always tries a formal-name match first and falls back to positional
        /// regardless of what other arguments in the call happen to be named. Treating the
        /// argument list as purely positional would silently mis-assign the statement/params
        /// roles whenever @stmt/@params themselves are named or reordered - the params-
        /// declaration text would be parsed as if it were the SQL to execute (and vice versa),
        /// misreported as a parse failure instead of resolved correctly.
        /// </summary>
        private static ScalarExpression? ResolveNamedOrPositionalArgument(
            IList<ExecuteParameter> parameters, int index, params ReadOnlySpan<string> formalNames)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.Variable is { } variable
                    && formalNames.Contains(variable.Name, StringComparer.OrdinalIgnoreCase))
                {
                    return parameter.ParameterValue;
                }
            }

            return index < parameters.Count ? parameters[index].ParameterValue : null;
        }

        private DynamicSqlScript BuildScript(
            ExecuteStatement node,
            IReadOnlyList<LiteralSegment> segments,
            string? parameterDeclarationText,
            IReadOnlyDictionary<string, string>? argumentBindings)
        {
            var segmentMap = new DynamicSqlSegmentMap();
            foreach (var segment in segments)
            {
                segmentMap.AppendLiteral(segment.SourcePath, segment.StartLine, segment.StartColumn, segment.PrefixLength, segment.Value);
            }

            return new DynamicSqlScript(CallSite(node), segmentMap.InnerText, segmentMap, parameterDeclarationText, _scope, argumentBindings);
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

                case FunctionCall { FunctionName.Value: var functionName } quoteNameCall
                    when string.Equals(functionName, "QUOTENAME", StringComparison.OrdinalIgnoreCase):
                    return TryFoldQuoteName(quoteNameCall, folded, foldingEnabled);

                default:
                    return FailNonLiteralExpression(expression);
            }
        }

        /// <summary>
        /// <c>QUOTENAME</c> is the one function call this scanner ever folds (roadmap "fold
        /// high-volume string-builder functions in dynamic SQL, oracle-checked") - the classic
        /// <c>SET @sql = 'SELECT * FROM ' + QUOTENAME(@table)</c> pattern, where @table already
        /// folded constant via Tier C, previously stopped dead at the function call even though
        /// its result is fully determined. Chosen deliberately over other common builders like
        /// REPLACE: QUOTENAME's escaping is a pure lexical operation, collation-independent,
        /// where REPLACE's own case-sensitivity depends on the CALLER's collation (verified
        /// directly - REPLACE('AbcABC','abc','X') differs under CI vs CS collation, and this
        /// scanner has no catalog/collation available at the point it runs), so folding REPLACE
        /// soundly isn't possible without a real risk of silently guessing wrong.
        /// </summary>
        private FoldAttempt TryFoldQuoteName(FunctionCall functionCall, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (functionCall.Parameters.Count is < 1 or > 2)
            {
                return FailNonLiteralExpression(functionCall);
            }

            var inputAttempt = TryFoldExpression(functionCall.Parameters[0], folded, foldingEnabled);
            if (!inputAttempt.Success)
            {
                return inputAttempt;
            }

            string? delimiterText = null;
            if (functionCall.Parameters.Count == 2)
            {
                var delimiterAttempt = TryFoldExpression(functionCall.Parameters[1], folded, foldingEnabled);
                if (!delimiterAttempt.Success)
                {
                    return delimiterAttempt;
                }

                delimiterText = string.Concat(delimiterAttempt.Segments!.Select(s => s.Value));
            }

            var input = string.Concat(inputAttempt.Segments!.Select(s => s.Value));
            var quoted = QuoteName(input, delimiterText);
            if (quoted is null)
            {
                // Oracle-verified: QUOTENAME itself returns SQL NULL for an input over 128
                // characters or an unrecognized delimiter - concatenating NULL propagates NULL
                // through the whole @sql build, a materially different runtime outcome this
                // scanner has no NULL-tracking representation for. Failing the fold (rather than
                // silently treating it as an empty/unwrapped string) is the honest call.
                return FoldAttempt.Fail("non-literal-expression:quotename-null-result", Span(functionCall));
            }

            return FoldAttempt.Ok([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, quoted)]);
        }

        /// <summary>
        /// QUOTENAME's exact algorithm, oracle-verified against the live Docker instance rather
        /// than assumed from documentation alone: only the CLOSING delimiter character is ever
        /// escaped (doubled) inside the input - the opening one is left untouched even when it
        /// appears in the input (e.g. <c>QUOTENAME('ab[c')</c> stays <c>[ab[c]</c>, not
        /// <c>[ab[[c]</c>). A one-character delimiter outside the nine SQL Server recognizes, a
        /// multi-character delimiter, or an input over 128 characters all return null exactly
        /// as the real QUOTENAME returns SQL NULL for those - never a guessed fallback.
        /// </summary>
        private static string? QuoteName(string input, string? delimiter)
        {
            if (input.Length > 128)
            {
                return null;
            }

            var (open, close) = delimiter switch
            {
                null or "" or "[" or "]" => ('[', ']'),
                "(" or ")" => ('(', ')'),
                "<" or ">" => ('<', '>'),
                "{" or "}" => ('{', '}'),
                "'" => ('\'', '\''),
                "\"" => ('"', '"'),
                _ => ('\0', '\0'),
            };

            if (open == '\0')
            {
                return null;
            }

            var escaped = input.Replace(close.ToString(), new string(close, 2), StringComparison.Ordinal);
            return $"{open}{escaped}{close}";
        }

        /// <summary>
        /// Every ScalarExpression shape not handled directly in <see cref="TryFoldExpression"/>
        /// genuinely can't be constant-folded (a function call's return value, a column's actual
        /// data, a subquery's result aren't knowable at scan time) - but a single
        /// "non-literal-expression" reason used to collapse all of them into one bucket, giving a
        /// corpus study no way to tell "half our Unanalyzable dynamic SQL is UPPER()-wrapped ORM
        /// output" from "half is genuinely arbitrary". DynamicSqlSummary already groups
        /// Unanalyzable findings by this exact reason string, so a finer split here surfaces
        /// immediately in the study's own numbers - no summary-side change needed. Extracted from
        /// TryFoldExpression's own switch to keep that method's cognitive complexity bounded.
        /// </summary>
        private FoldAttempt FailNonLiteralExpression(ScalarExpression expression) => expression switch
        {
            FunctionCall => FoldAttempt.Fail("non-literal-expression:function-call", Span(expression)),
            ColumnReferenceExpression => FoldAttempt.Fail("non-literal-expression:column-reference", Span(expression)),
            ScalarSubquery => FoldAttempt.Fail("non-literal-expression:subquery", Span(expression)),
            SearchedCaseExpression or SimpleCaseExpression or IIfCall => FoldAttempt.Fail("non-literal-expression:conditional", Span(expression)),
            CastCall or ConvertCall => FoldAttempt.Fail("non-literal-expression:cast-or-convert", Span(expression)),
            // Reaches here only for a BinaryExpressionType other than Add (Subtract, Multiply,
            // BitwiseAnd, ...) - Add is folded in TryFoldExpression itself; every other operator
            // on a dynamic SQL text expression is a distinct, rarer shape from a plain unhandled
            // leaf node, worth its own bucket rather than "other".
            BinaryExpression => FoldAttempt.Fail("non-literal-expression:unsupported-operator", Span(expression)),
            _ => FoldAttempt.Fail("non-literal-expression:other", Span(expression)),
        };

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

        /// <summary>Every variable name a SET or SELECT-assignment statement anywhere in the visited subtree assigns - used to pre-taint a loop body's self-mutated variables before walking it.</summary>
        private sealed class AssignedVariableCollector : TSqlFragmentVisitor
        {
            public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(SetVariableStatement node) => Names.Add(node.Variable.Name);

            public override void Visit(SelectSetVariable node) => Names.Add(node.Variable.Name);
        }
    }
}

/// <summary>Everything <see cref="DynamicSqlScanner.Scan"/> found in one parsed file: definite unanalyzable findings, and candidate scripts ready for <see cref="DynamicSqlPipeline"/> to reparse.</summary>
public sealed record DynamicSqlExtractionResult(
    IReadOnlyList<DynamicSqlFinding> Findings,
    IReadOnlyList<DynamicSqlScript> AnalyzableScripts);
