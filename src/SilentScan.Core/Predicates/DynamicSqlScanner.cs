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

    /// <summary>
    /// One statically-provable constant value a folded expression could assemble to - a single
    /// concatenation chain of literal segments. Plural assemblies (see <see cref="FoldAttempt"/>/
    /// <see cref="FoldState"/>) exist because a control-flow join point (IF/TRY-CATCH) can leave a
    /// variable with more than one PROVABLY POSSIBLE constant value - the optional-filter
    /// accumulation pattern (<c>IF @a IS NOT NULL SET @sql = @sql + N' AND col = @a'</c>) is
    /// exactly this: after the IF, @sql is EITHER the unmodified value OR the appended one, never
    /// anything else, so both are analyzed rather than the whole site declining as unknowable.
    /// </summary>
    private const int MaxAssembliesPerVariable = 32;

    private readonly record struct FoldAttempt(IReadOnlyList<IReadOnlyList<LiteralSegment>>? Assemblies, string? Reason, SourceSpan? Location)
    {
        public bool Success => Assemblies is not null;

        public static FoldAttempt Ok(IReadOnlyList<IReadOnlyList<LiteralSegment>> assemblies) => new(assemblies, null, null);

        public static FoldAttempt OkSingle(IReadOnlyList<LiteralSegment> segments) => Ok([segments]);

        public static FoldAttempt Fail(string reason, SourceSpan location) => new(null, reason, location);
    }

    private sealed class FoldState
    {
        public IReadOnlyList<IReadOnlyList<LiteralSegment>>? Assemblies { get; private init; }

        public string? TaintReason { get; private init; }

        public SourceSpan? TaintLocation { get; private init; }

        public static FoldState Constant(IReadOnlyList<IReadOnlyList<LiteralSegment>> assemblies) => new() { Assemblies = assemblies };

        public static FoldState ConstantSingle(IReadOnlyList<LiteralSegment> segments) => Constant([segments]);

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
            var seed = new Dictionary<string, FoldState>(StringComparer.OrdinalIgnoreCase);

            if (edges.Count == 0)
            {
                // No call site THIS SCAN saw at all (application code, an unparsed caller, a
                // synonym this scan didn't resolve) - the parameter genuinely IS declared, just
                // with no known value. Seeds an honest, specific taint reason rather than
                // returning null and falling through to the generic "undeclared-variable" a
                // caller-blind VariableReference lookup would otherwise report - that reason is
                // misleading here: the variable IS declared, as a parameter, there is simply no
                // known caller to learn its value from.
                foreach (var formal in formalParameters)
                {
                    seed[formal.VariableName.Value] = FoldState.Tainted("procedure-parameter:no-known-call-site", Span(formal));
                }

                return seed;
            }

            if (edges.Count == 1)
            {
                SeedFromSingleEdge(edges[0], formalParameters, seed);
                return seed;
            }

            SeedFromMultipleEdges(edges, formalParameters, seed);
            return seed;
        }

        private static void SeedFromSingleEdge(ProcCallEdge edge, IList<ProcedureParameter> formalParameters, Dictionary<string, FoldState> seed)
        {
            foreach (var formal in formalParameters)
            {
                var paramName = formal.VariableName.Value;
                var argument = edge.Arguments.FirstOrDefault(
                    a => string.Equals(a.FormalParameterName, paramName, StringComparison.OrdinalIgnoreCase));
                if (argument is null || argument.FormalParameterIsOutput)
                {
                    // No matching actual argument (a default value applies) or an OUTPUT
                    // parameter (flows the other direction) - nothing to seed, left unseeded.
                    continue;
                }

                seed[paramName] = argument.LiteralArgument is { } literalArgument
                    ? FoldState.ConstantSingle([new LiteralSegment(
                        literalArgument.SourcePath, literalArgument.StartLine, literalArgument.StartColumn,
                        literalArgument.PrefixLength, literalArgument.Value)])
                    : FoldState.Tainted("parameter-not-seeded:non-literal-caller", edge.CallSite);
            }
        }

        /// <summary>
        /// Set-valued seeding across every known caller (roadmap "trace provably-constant dynamic
        /// SQL across proc-call edges", extended beyond a single call site): when EVERY edge
        /// calling this proc supplies a literal argument for a given formal parameter, the
        /// parameter's true runtime value is provably one of those literals - seeded as the
        /// assembly SET, composing with the same branch-fold machinery an IF/TRY-CATCH divergence
        /// merge already uses, never a guess about which caller's value applies at this
        /// particular invocation. If even ONE caller can't supply a literal for this parameter (a
        /// variable/expression argument, an OUTPUT parameter, or no matching argument at all - a
        /// default value this scan doesn't track), the whole parameter stays tainted rather than
        /// partially seeded from a subset of callers - a taint at even one call site means the
        /// parameter's true value set is unknown, not merely wider than what the literals show.
        /// </summary>
        private static void SeedFromMultipleEdges(IReadOnlyList<ProcCallEdge> edges, IList<ProcedureParameter> formalParameters, Dictionary<string, FoldState> seed)
        {
            foreach (var paramName in formalParameters.Select(formal => formal.VariableName.Value))
            {
                seed[paramName] = SeedOneParameterFromMultipleEdges(edges, paramName);
            }
        }

        private static FoldState SeedOneParameterFromMultipleEdges(IReadOnlyList<ProcCallEdge> edges, string paramName)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var assemblies = new List<IReadOnlyList<LiteralSegment>>();

            foreach (var edge in edges)
            {
                var argument = edge.Arguments.FirstOrDefault(
                    a => string.Equals(a.FormalParameterName, paramName, StringComparison.OrdinalIgnoreCase));
                if (argument is null || argument.FormalParameterIsOutput || argument.LiteralArgument is not { } literalArgument)
                {
                    return FoldState.Tainted("parameter-not-seeded:non-literal-caller", edge.CallSite);
                }

                if (!seen.Add(literalArgument.Value))
                {
                    continue;
                }

                if (assemblies.Count == MaxAssembliesPerVariable)
                {
                    return FoldState.Tainted("parameter-not-seeded:cardinality-cap", edge.CallSite);
                }

                assemblies.Add([new LiteralSegment(
                    literalArgument.SourcePath, literalArgument.StartLine, literalArgument.StartColumn,
                    literalArgument.PrefixLength, literalArgument.Value)]);
            }

            // Reached only when every edge supplied a genuine literal argument for this
            // parameter - assemblies is never empty here (the loop above returns early the
            // moment any edge lacks one), so this is always a real constant set, never a guess.
            return FoldState.Constant(assemblies);
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
                    // RECEIVE ... INTO, ...). T-SQL locals cannot alias, so a statement can only
                    // ever assign a variable it names literally - taints exactly the variables
                    // this statement mentions (a strict superset of what it could have written)
                    // rather than every variable currently tracked. An INSERT/UPDATE/PRINT/etc.
                    // that never mentions @Where cannot have changed @Where, so @Where survives.
                    TaintReferencedVariables(folded, statement, "unsupported-statement-in-scope");
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
                    ? FoldState.Constant(attempt.Assemblies!)
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
                if (!folded.TryGetValue(name, out var existing) || existing.Assemblies is null)
                {
                    folded[name] = FoldState.Tainted(existing?.TaintReason ?? "undeclared-variable", existing?.TaintLocation ?? Span(site));
                    return;
                }

                if (!rhs.Success)
                {
                    folded[name] = FoldState.Tainted(rhs.Reason!, rhs.Location!.Value);
                    return;
                }

                folded[name] = TryCartesianConcat(existing.Assemblies, rhs.Assemblies!, out var combined)
                    ? FoldState.Constant(combined)
                    : FoldState.Tainted("diverges-across-if-branches:cardinality-cap", Span(site));
                return;
            }

            folded[name] = rhs.Success
                ? FoldState.Constant(rhs.Assemblies!)
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

            MergeUnioningDivergent(folded, thenDict, elseDict, ifStatement, "diverges-across-if-branches");
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

            MergeUnioningDivergent(folded, tryDict, catchDict, tryCatch, "diverges-across-try-catch");
        }

        /// <summary>
        /// Because <paramref name="branchA"/>/<paramref name="branchB"/> start as shallow
        /// clones of <paramref name="folded"/>, an entry a branch never touched still holds
        /// the exact same <see cref="FoldState"/> reference as in <paramref name="folded"/> -
        /// so reference (in)equality alone tells us whether either branch could have changed a
        /// variable, with no content comparison needed. Any real divergence taints; this
        /// deliberately never tries to prove two branches produced textually-equal values. Used
        /// ONLY by <see cref="HandleWhile"/>'s own post-loop merge: a loop body can run zero,
        /// one, or many times, which is NOT reducible to "one of these two known branch outcomes"
        /// the way an IF's THEN/ELSE or a TRY/CATCH's TRY/CATCH are - <see
        /// cref="MergeUnioningDivergent"/> is what those two use instead.
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

        /// <summary>
        /// An IF's THEN/ELSE or a TRY/CATCH's TRY/CATCH are each exactly one of two mutually
        /// exclusive, fully-determined outcomes - unlike a WHILE body (<see
        /// cref="MergeTaintingDivergent"/>), which can run zero, one, or many times. When BOTH
        /// branches independently folded a touched variable to a constant assembly set, the real
        /// value after the statement is PROVABLY one of the two branches' own assemblies, so this
        /// unions them (deduplicated, cardinality-capped - see <see cref="TryUnionAssemblies"/>)
        /// instead of tainting - the optional-filter accumulation pattern this scanner previously
        /// declined outright (CLAUDE.md dynamic SQL policy). A variable only one branch actually
        /// assigned differently still merges here (reference-inequality against the
        /// pre-statement state decides "touched", exactly as <see
        /// cref="MergeTaintingDivergent"/> already did) - only a variable BOTH branches leave
        /// bit-for-bit unchanged from <paramref name="folded"/> is skipped entirely.
        /// </summary>
        private void MergeUnioningDivergent(
            Dictionary<string, FoldState> folded, Dictionary<string, FoldState> branchA, Dictionary<string, FoldState> branchB, TSqlStatement owner, string reason)
        {
            var touched = new HashSet<string>(branchA.Keys, StringComparer.OrdinalIgnoreCase);
            touched.UnionWith(branchB.Keys);

            var location = Span(owner);
            foreach (var key in touched)
            {
                var before = folded.GetValueOrDefault(key);
                var a = branchA.GetValueOrDefault(key);
                var b = branchB.GetValueOrDefault(key);

                if (ReferenceEquals(before, a) && ReferenceEquals(before, b))
                {
                    continue;
                }

                folded[key] = MergeOne(a, b, reason, location);
            }
        }

        /// <summary>
        /// Merges one variable's two branch outcomes: both constant unions (capped and
        /// deduplicated by concatenated text, so byte-identical assemblies from each branch
        /// collapse to one entry rather than reporting the same defect twice); exactly one side
        /// tainted propagates THAT side's own reason, since an unrelated fold failure inside one
        /// branch is not "divergence" - a branch whose own function call the folder can't handle
        /// must not get relabeled with the generic divergence reason just because the OTHER
        /// branch happened to fold cleanly. Anything else (both tainted, or a variable one branch
        /// never even declared) falls back to the generic divergence reason, matching this
        /// scanner's pre-existing behavior for those rarer shapes.
        /// </summary>
        private static FoldState MergeOne(FoldState? a, FoldState? b, string reason, SourceSpan location)
        {
            if (a?.Assemblies is { } assembliesA && b?.Assemblies is { } assembliesB)
            {
                return TryUnionAssemblies(assembliesA, assembliesB, out var union)
                    ? FoldState.Constant(union)
                    : FoldState.Tainted($"{reason}:cardinality-cap", location);
            }

            // Both sides already tainted with the IDENTICAL reason (e.g. a proc with many
            // sequential optional filters keeps re-tainting with the same cardinality-cap reason
            // at every later branch once the cap first triggers) - propagate that shared reason
            // rather than relabeling it with the generic divergence reason below, which would
            // otherwise mask WHY every later branch is unanalyzable behind a less specific label.
            if (a?.TaintReason is { } sharedReason && a.TaintLocation is { } sharedLocation && b?.TaintReason == sharedReason)
            {
                return FoldState.Tainted(sharedReason, sharedLocation);
            }

            if (a is { Assemblies: null, TaintReason: { } reasonA, TaintLocation: { } locationA } && b?.Assemblies is not null)
            {
                return FoldState.Tainted(reasonA, locationA);
            }

            if (b is { Assemblies: null, TaintReason: { } reasonB, TaintLocation: { } locationB } && a?.Assemblies is not null)
            {
                return FoldState.Tainted(reasonB, locationB);
            }

            return FoldState.Tainted(reason, location);
        }

        /// <summary>
        /// Deduplicates (by concatenated text) and caps the union of two branches' own assembly
        /// sets at <see cref="MaxAssembliesPerVariable"/> - a real bound against a proc with many
        /// independent optional filters (ten sequential IFs can produce up to 2^10 = 1024
        /// combinations), not a tuned-for-recall limit.
        /// </summary>
        private static bool TryUnionAssemblies(
            IReadOnlyList<IReadOnlyList<LiteralSegment>> a,
            IReadOnlyList<IReadOnlyList<LiteralSegment>> b,
            out IReadOnlyList<IReadOnlyList<LiteralSegment>> union)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<IReadOnlyList<LiteralSegment>>();
            foreach (var assembly in a.Concat(b))
            {
                if (!seen.Add(string.Concat(assembly.Select(s => s.Value))))
                {
                    continue;
                }

                if (result.Count == MaxAssembliesPerVariable)
                {
                    union = [];
                    return false;
                }

                result.Add(assembly);
            }

            union = result;
            return true;
        }

        /// <summary>
        /// Cross-products two assembly sets for string concatenation (<c>SET @sql = @sql +
        /// '...'</c>, where either side may already carry multiple possible values from an
        /// earlier branch merge - the exact shape ten sequential optional-filter IFs produce,
        /// each concatenating onto an already-divergent @sql) - deduplicated and capped the same
        /// way <see cref="TryUnionAssemblies"/> is, since this is the identical cardinality bound
        /// reached through concatenation instead of a single merge point.
        /// </summary>
        private static bool TryCartesianConcat(
            IReadOnlyList<IReadOnlyList<LiteralSegment>> left,
            IReadOnlyList<IReadOnlyList<LiteralSegment>> right,
            out IReadOnlyList<IReadOnlyList<LiteralSegment>> combined)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<IReadOnlyList<LiteralSegment>>();
            foreach (var l in left)
            {
                foreach (var r in right)
                {
                    List<LiteralSegment> merged = [.. l, .. r];
                    if (!seen.Add(string.Concat(merged.Select(s => s.Value))))
                    {
                        continue;
                    }

                    if (result.Count == MaxAssembliesPerVariable)
                    {
                        combined = [];
                        return false;
                    }

                    result.Add(merged);
                }
            }

            combined = result;
            return true;
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
        /// with OUTPUT - this scanner does not model what an arbitrary called procedure does
        /// internally, so any variable named in the call could come back holding something
        /// other than what was folded for it. Scoped to the variables this EXEC actually
        /// mentions (same no-aliasing argument as <see cref="TaintReferencedVariables"/>) rather
        /// than every variable currently tracked - an unrelated variable this call never
        /// references cannot have been mutated by it.
        /// </summary>
        private void TaintExecuteMutatedVariables(ExecuteStatement node, Dictionary<string, FoldState> folded)
        {
            TaintReferencedVariables(folded, node, "unsupported-execute-form");
        }

        private void HandleStringList(ExecutableStringList stringList, ExecuteStatement node, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            // ExecutableStringList.Strings is never empty for a successfully parsed
            // ExecuteStatement - EXEC() with no argument is a syntax error, not a valid
            // zero-element call. Starts from the single empty assembly and cross-concatenates
            // each argument's own assembly set in turn - EXEC('a', @x, 'b') concatenates all
            // three pieces in order regardless of how many possible values @x itself carries.
            IReadOnlyList<IReadOnlyList<LiteralSegment>> assemblies = [[]];
            foreach (var element in stringList.Strings)
            {
                var attempt = TryFoldExpression(element, folded, foldingEnabled);
                if (!attempt.Success)
                {
                    Findings.Add(Unanalyzable(node, attempt.Reason!));
                    return;
                }

                if (!TryCartesianConcat(assemblies, attempt.Assemblies!, out var next))
                {
                    Findings.Add(Unanalyzable(node, "diverges-across-if-branches:cardinality-cap"));
                    return;
                }

                assemblies = next;
            }

            foreach (var assembly in assemblies)
            {
                Scripts.Add(BuildScript(node, assembly, parameterDeclarationText: null, argumentBindings: null));
            }
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

            var parameterDeclarationText = ResolveParameterDeclarationText(procRef, folded, foldingEnabled);
            var argumentBindings = ResolveArgumentBindings(procRef);
            foreach (var assembly in queryAttempt.Assemblies!)
            {
                Scripts.Add(BuildScript(node, assembly, parameterDeclarationText, argumentBindings));
            }
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
            if (!attempt.Success || attempt.Assemblies!.Count != 1)
            {
                // A @params declaration text that itself carries more than one possible value
                // (from an upstream branch merge) is a rare compound shape - falls back to null
                // exactly like an unfoldable one, rather than guessing which assembly applies.
                return null;
            }

            return string.Concat(attempt.Assemblies[0].Select(s => s.Value));
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
                    return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, literal.StartLine, literal.StartColumn, prefixLength, literal.Value)]);

                case VariableReference variableRef:
                    return TryFoldVariableReference(variableRef, folded, foldingEnabled);

                case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } binary:
                    return TryFoldConcatenation(binary, folded, foldingEnabled);

                case ParenthesisExpression paren:
                    return TryFoldExpression(paren.Expression, folded, foldingEnabled);

                case FunctionCall { FunctionName.Value: var functionName } quoteNameCall
                    when string.Equals(functionName, "QUOTENAME", StringComparison.OrdinalIgnoreCase):
                    return TryFoldQuoteName(quoteNameCall, folded, foldingEnabled);

                case FunctionCall { FunctionName.Value: var functionName } builderCall
                    when WhitelistedStringBuilders.Contains(functionName):
                    return TryFoldStringBuilder(builderCall, functionName, folded, foldingEnabled);

                case FunctionCall { FunctionName.Value: var functionName } nonDeterministicCall
                    when NonDeterministicFunctions.Contains(functionName):
                    // A distinct reason from the generic ":function-call" below - NEWID()/
                    // GETDATE()/RAND() aren't unimplemented, they're genuinely unknowable at
                    // compile time regardless of how much folding this scanner ever grows, so
                    // the study can state that plainly rather than lumping it with real gaps.
                    return FoldAttempt.Fail("non-deterministic-function", Span(expression));

                // LEFT/RIGHT are NOT parsed as an ordinary FunctionCall the way UPPER/LOWER/
                // LTRIM/RTRIM/SUBSTRING are - ScriptDom gives them their own dedicated node types
                // (LeftFunctionCall/RightFunctionCall, confirmed via reflection over the parsed
                // tree - CLAUDE.md "verify against the real oracle/parser, never assume"), the
                // same way it gives CAST/CONVERT their own CastCall/ConvertCall rather than a
                // generic FunctionCall. Handled as their own cases rather than folded into
                // WhitelistedStringBuilders's FunctionCall-only dispatch above.
                case LeftFunctionCall leftCall:
                    return TryFoldLeftOrRight(leftCall.Parameters, "LEFT", leftCall, folded, foldingEnabled);

                case RightFunctionCall rightCall:
                    return TryFoldLeftOrRight(rightCall.Parameters, "RIGHT", rightCall, folded, foldingEnabled);

                case CastCall castCall:
                    return TryFoldCastOrConvert(castCall.Parameter, castCall.DataType, castCall, folded, foldingEnabled);

                case ConvertCall convertCall:
                    return TryFoldCastOrConvert(convertCall.Parameter, convertCall.DataType, convertCall, folded, foldingEnabled);

                case SimpleCaseExpression or SearchedCaseExpression or IIfCall:
                    return TryFoldConditional(expression, folded, foldingEnabled);

                default:
                    return FailNonLiteralExpression(expression);
            }
        }

        private FoldAttempt TryFoldVariableReference(VariableReference variableRef, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (!foldingEnabled)
            {
                return FoldAttempt.Fail("goto-or-label-in-scope", Span(variableRef));
            }

            if (!folded.TryGetValue(variableRef.Name, out var state))
            {
                return FoldAttempt.Fail("undeclared-variable", Span(variableRef));
            }

            return state.Assemblies is not null
                ? FoldAttempt.Ok(state.Assemblies)
                : FoldAttempt.Fail(state.TaintReason!, state.TaintLocation!.Value);
        }

        private FoldAttempt TryFoldConcatenation(BinaryExpression binary, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
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

            return TryCartesianConcat(left.Assemblies!, right.Assemblies!, out var concatenated)
                ? FoldAttempt.Ok(concatenated)
                : FoldAttempt.Fail("diverges-across-if-branches:cardinality-cap", Span(binary));
        }

        /// <summary>
        /// Every string-builder function this scanner folds besides QUOTENAME that ScriptDom
        /// still parses as an ordinary <see cref="FunctionCall"/>. UPPER/LOWER need a per-input
        /// guard (<see cref="IsSafeToCaseConvert"/>) rather than being unconditionally safe -
        /// oracle-verified: the Turkish-family "dotless I" case mapping genuinely differs by
        /// collation for the specific 'i'/'I' pair, even though every other ASCII letter's case
        /// mapping does not. REPLACE's own case-sensitivity depends on the CALLER's collation the
        /// same way (oracle-verified: <c>REPLACE('AbcABC','abc','X')</c> differs under CI vs CS
        /// collation) - rather than a per-character static guard, <see cref="TryFoldReplace"/>
        /// evaluates both an ordinal and an ordinal-ignore-case replace and folds only when they
        /// agree, which is exactly the "does ANY plausible collation change the answer" question
        /// IsSafeToCaseConvert answers by construction instead. CHARINDEX/PATINDEX have the same
        /// collation dependency but return a position, not a string - out of scope for THIS table,
        /// which only ever produces string segments. LEFT/RIGHT are deliberately NOT here - see
        /// the dedicated <see cref="LeftFunctionCall"/>/<see cref="RightFunctionCall"/> cases in
        /// <see cref="TryFoldExpression"/>.
        /// </summary>
        private static readonly HashSet<string> WhitelistedStringBuilders = new(StringComparer.OrdinalIgnoreCase)
        {
            "UPPER", "LOWER", "LTRIM", "RTRIM", "SUBSTRING", "REPLACE",
        };

        /// <summary>
        /// Every builtin this scanner declines to fold purely because its result is genuinely
        /// unknowable at compile time (not because folding it is unimplemented) - reported with
        /// its own <c>non-deterministic-function</c> reason in <see cref="TryFoldExpression"/> so
        /// the study can state that plainly. <c>RAND()</c> with a literal seed argument IS
        /// deterministic in T-SQL, but this scanner does not special-case that rare shape - a
        /// seeded RAND still declines via this set, just like an unseeded one.
        /// </summary>
        private static readonly HashSet<string> NonDeterministicFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "NEWID", "NEWSEQUENTIALID", "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME",
            "SYSDATETIMEOFFSET", "RAND", "CHECKSUM", "BINARY_CHECKSUM",
        };

        private FoldAttempt TryFoldStringBuilder(FunctionCall functionCall, string functionName, Dictionary<string, FoldState> folded, bool foldingEnabled) =>
            functionName.ToUpperInvariant() switch
            {
                "UPPER" or "LOWER" => TryFoldCaseConversion(functionCall, functionName, folded, foldingEnabled),
                "LTRIM" or "RTRIM" => TryFoldTrim(functionCall, functionName, folded, foldingEnabled),
                "SUBSTRING" => TryFoldSubstring(functionCall, folded, foldingEnabled),
                "REPLACE" => TryFoldReplace(functionCall, folded, foldingEnabled),
                _ => FailNonLiteralExpression(functionCall),
            };

        /// <summary>
        /// Oracle-verified (Turkish_CI_AS vs Latin1_General_CI_AS): every ASCII letter EXCEPT
        /// 'i'/'I' case-converts identically across every SQL Server collation; 'i'/'I' is the one
        /// pair whose mapping genuinely differs (<c>UPPER('i')</c> is 'İ' under a Turkish-family
        /// collation, 'I' everywhere else - the well-known "Turkish I problem", real in SQL
        /// Server too, not just other platforms). This scanner has no collation context at all at
        /// the point it runs, so an input containing 'i'/'I' or any non-ASCII character declines
        /// the fold rather than guessing which mapping the real target collation would apply.
        /// </summary>
        private FoldAttempt TryFoldCaseConversion(FunctionCall functionCall, string functionName, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (functionCall.Parameters.Count != 1)
            {
                return FailNonLiteralExpression(functionCall);
            }

            if (!TryFoldSingleAssemblyArgument(functionCall.Parameters[0], folded, foldingEnabled, out var attempt, out var input))
            {
                return attempt;
            }

            if (!IsSafeToCaseConvert(input!))
            {
                return FoldAttempt.Fail("non-literal-expression:case-conversion-collation-sensitive", Span(functionCall));
            }

            var converted = string.Equals(functionName, "UPPER", StringComparison.OrdinalIgnoreCase)
                ? input!.ToUpperInvariant()
                : input!.ToLowerInvariant();

            return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, converted)]);
        }

        private static bool IsSafeToCaseConvert(string input) => input.All(c => c is not ('i' or 'I') && c <= 127);

        /// <summary>
        /// Oracle-verified: SQL Server's LTRIM/RTRIM trim ONLY the space character (0x20) - a tab
        /// or other whitespace is left untouched, unlike .NET's parameterless Trim()/TrimStart()/
        /// TrimEnd(), which strip every Unicode whitespace character - so this trims ' '
        /// explicitly rather than using the parameterless overload. The two-argument SQL 2022+
        /// overload (a custom trim-character set) is out of scope - declines via the parameter
        /// count check below, same as any other unsupported call shape.
        /// </summary>
        private FoldAttempt TryFoldTrim(FunctionCall functionCall, string functionName, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (functionCall.Parameters.Count != 1)
            {
                return FailNonLiteralExpression(functionCall);
            }

            if (!TryFoldSingleAssemblyArgument(functionCall.Parameters[0], folded, foldingEnabled, out var attempt, out var input))
            {
                return attempt;
            }

            var trimmed = string.Equals(functionName, "LTRIM", StringComparison.OrdinalIgnoreCase)
                ? input!.TrimStart(' ')
                : input!.TrimEnd(' ');

            return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, trimmed)]);
        }

        /// <summary>
        /// Oracle-verified: <c>LEFT</c>/<c>RIGHT</c> with a length at or beyond the input's own
        /// length simply return the whole string (no padding) - matches .NET's own clamped
        /// slicing once the length is capped at the input's length. A negative length raises Msg
        /// 536 ("Invalid length parameter") on the real server rather than returning anything -
        /// the real EXEC would never even reach this dynamic SQL text on that path, a materially
        /// different runtime outcome this scanner has no representation for, so it declines
        /// rather than guessing at a runtime error. The length argument must itself be a literal
        /// integer - this scanner tracks only STRING variable values, never numeric ones, so a
        /// length carried in a variable is declined, not guessed.
        /// </summary>
        private FoldAttempt TryFoldLeftOrRight(
            IList<ScalarExpression> parameters, string functionName, TSqlFragment site, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (parameters.Count != 2)
            {
                return FoldAttempt.Fail("non-literal-expression:other", Span(site));
            }

            if (!TryFoldSingleAssemblyArgument(parameters[0], folded, foldingEnabled, out var attempt, out var input))
            {
                return attempt;
            }

            if (!TryFoldIntegerLiteral(parameters[1], out var length))
            {
                return FoldAttempt.Fail("non-literal-expression:function-call-argument-diverges", Span(parameters[1]));
            }

            if (length < 0)
            {
                return FoldAttempt.Fail("non-literal-expression:negative-length", Span(site));
            }

            var input0 = input!;
            var clampedLength = Math.Min(length, input0.Length);
            var result = string.Equals(functionName, "LEFT", StringComparison.OrdinalIgnoreCase)
                ? input0[..clampedLength]
                : input0[^clampedLength..];

            return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, site.StartLine, site.StartColumn, PrefixLength: 0, result)]);
        }

        /// <summary>
        /// Oracle-verified: <c>SUBSTRING(s, start, length)</c> clamps a <c>length</c>-beyond-the-
        /// end down to whatever remains (matching .NET's own clamped slicing), and a <c>start</c>
        /// beyond the input's length returns an empty string rather than
        /// erroring. A negative length raises Msg 536 exactly like LEFT/RIGHT (declined, not
        /// guessed - see <see cref="TryFoldLeftOrRight"/>). A start position below 1 IS real,
        /// defined T-SQL behavior (oracle-verified: the requested window still clips against the
        /// string's actual bounds) but is rare enough in real dynamic-SQL construction that this
        /// scanner declines it rather than adding the extra below-1 clipping arithmetic for a
        /// shape that essentially never appears outside adversarial input. Both start and length
        /// must be literal integers, for the same reason as <see cref="TryFoldLeftOrRight"/>'s
        /// length argument.
        /// </summary>
        private FoldAttempt TryFoldSubstring(FunctionCall functionCall, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (functionCall.Parameters.Count != 3)
            {
                return FailNonLiteralExpression(functionCall);
            }

            if (!TryFoldSingleAssemblyArgument(functionCall.Parameters[0], folded, foldingEnabled, out var attempt, out var input))
            {
                return attempt;
            }

            if (!TryFoldIntegerLiteral(functionCall.Parameters[1], out var start) || !TryFoldIntegerLiteral(functionCall.Parameters[2], out var length))
            {
                return FoldAttempt.Fail("non-literal-expression:function-call-argument-diverges", Span(functionCall));
            }

            if (length < 0)
            {
                return FoldAttempt.Fail("non-literal-expression:negative-length", Span(functionCall));
            }

            if (start < 1)
            {
                return FoldAttempt.Fail("non-literal-expression:substring-start-below-one", Span(functionCall));
            }

            var input0 = input!;
            if (start > input0.Length)
            {
                return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, string.Empty)]);
            }

            var clampedLength = Math.Min(length, input0.Length - (start - 1));
            var result = input0.Substring(start - 1, clampedLength);

            return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, result)]);
        }

        /// <summary>
        /// REPLACE(source, pattern, replacement) folds when both a strictly-ordinal replace and
        /// an ordinal-IGNORE-CASE replace produce the IDENTICAL result string - if neither
        /// definition of "matches" changes the answer, no collation this scanner has never seen
        /// (SQL_* vs Windows, case-sensitive vs case-insensitive, even a Turkish-family one) can
        /// produce a THIRD answer either, since every one of them falls somewhere between these
        /// two extremes of how aggressively "equal" characters are matched. This is the general
        /// form of the same guarantee <see cref="IsSafeToCaseConvert"/> gives UPPER/LOWER by a
        /// per-character check instead - oracle-verified root fact either way:
        /// <c>REPLACE('AbcABC','abc','X')</c> differs under CI vs CS collation.
        /// </summary>
        private FoldAttempt TryFoldReplace(FunctionCall functionCall, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (functionCall.Parameters.Count != 3)
            {
                return FailNonLiteralExpression(functionCall);
            }

            if (!TryFoldSingleAssemblyArgument(functionCall.Parameters[0], folded, foldingEnabled, out var sourceAttempt, out var source))
            {
                return sourceAttempt;
            }

            if (!TryFoldSingleAssemblyArgument(functionCall.Parameters[1], folded, foldingEnabled, out var patternAttempt, out var pattern))
            {
                return patternAttempt;
            }

            if (!TryFoldSingleAssemblyArgument(functionCall.Parameters[2], folded, foldingEnabled, out var replacementAttempt, out var replacement))
            {
                return replacementAttempt;
            }

            if (pattern!.Length == 0)
            {
                // SQL Server's own behavior for an empty search pattern is not something this
                // scanner has verified against the oracle, and .NET's string.Replace throws
                // outright for an empty oldValue - declines rather than guessing either way.
                return FoldAttempt.Fail("non-literal-expression:replace-empty-pattern", Span(functionCall));
            }

            var ordinalResult = source!.Replace(pattern, replacement, StringComparison.Ordinal);
            var caseInsensitiveResult = source.Replace(pattern, replacement, StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(ordinalResult, caseInsensitiveResult, StringComparison.Ordinal))
            {
                return FoldAttempt.Fail("non-literal-expression:replace-collation-sensitive", Span(functionCall));
            }

            return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, ordinalResult)]);
        }

        /// <summary>
        /// CAST/CONVERT folds only onto a VARCHAR(n)/NVARCHAR(n) (or MAX) target - the one target
        /// family whose rendering of an already-string source is pinned down (oracle-verified:
        /// silently truncates an over-length value, no error). CHAR/NCHAR (blank-padding,
        /// oracle-verified: <c>CAST('ab' AS char(5))</c> is <c>'ab   '</c>) and every non-string
        /// target (int/date/decimal/...) each have their own rendering algorithm this scanner has
        /// no verified implementation of - declined rather than guessing a format, per CLAUDE.md.
        /// CONVERT's optional style argument is therefore irrelevant here too: style only affects
        /// non-string-family renderings this fold never attempts.
        /// </summary>
        private FoldAttempt TryFoldCastOrConvert(
            ScalarExpression source, DataTypeReference dataType, TSqlFragment site, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            var targetType = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null);
            if (targetType is not { Category: SqlTypeCategory.VarChar or SqlTypeCategory.NVarChar })
            {
                return FoldAttempt.Fail("non-literal-expression:cast-target-not-pinned", Span(site));
            }

            if (!TryFoldSingleAssemblyArgument(source, folded, foldingEnabled, out var attempt, out var input))
            {
                return attempt;
            }

            var result = !targetType.IsMax && targetType.Length is { } length && input!.Length > length
                ? input[..length]
                : input!;

            return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, site.StartLine, site.StartColumn, PrefixLength: 0, result)]);
        }

        /// <summary>
        /// CASE/IIF folds by UNIONING every branch's own fold result - never by evaluating the
        /// discriminator/condition, which this scanner has no boolean-predicate machinery for at
        /// all (a WHEN clause is an arbitrary expression, not a value this scanner tracks). Since
        /// which branch actually runs is unknown, the true result is PROVABLY one of the folded
        /// branches - the same "known set of possible values" the IF/TRY-CATCH branch merges
        /// already use, composing naturally with the assembly-set lattice rather than needing its
        /// own merge rule. Requires an ELSE (a bare CASE with no matching WHEN and no ELSE returns
        /// SQL NULL, which this scanner's assembly-set model has no representation for at all -
        /// silently omitting that outcome from the union would be unsound, not merely imprecise,
        /// so it declines instead) and requires every branch to fold - one unfoldable branch
        /// taints the whole expression, matching every other all-or-nothing fold in this scanner.
        /// </summary>
        private FoldAttempt TryFoldConditional(ScalarExpression expression, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            var (valueExpressions, elseExpression) = expression switch
            {
                SimpleCaseExpression simpleCase => (simpleCase.WhenClauses.Select(w => w.ThenExpression), simpleCase.ElseExpression),
                SearchedCaseExpression searchedCase => (searchedCase.WhenClauses.Select(w => w.ThenExpression), searchedCase.ElseExpression),
                IIfCall iif => (new[] { iif.ThenExpression }, iif.ElseExpression),
                _ => (Enumerable.Empty<ScalarExpression>(), null),
            };

            if (elseExpression is null)
            {
                return FoldAttempt.Fail("non-literal-expression:conditional", Span(expression));
            }

            IReadOnlyList<IReadOnlyList<LiteralSegment>> union = [];
            var first = true;
            foreach (var branch in valueExpressions.Append(elseExpression))
            {
                var attempt = TryFoldExpression(branch, folded, foldingEnabled);
                if (!attempt.Success)
                {
                    return FoldAttempt.Fail("non-literal-expression:conditional", Span(expression));
                }

                if (first)
                {
                    union = attempt.Assemblies!;
                    first = false;
                    continue;
                }

                if (!TryUnionAssemblies(union, attempt.Assemblies!, out var merged))
                {
                    return FoldAttempt.Fail("diverges-across-if-branches:cardinality-cap", Span(expression));
                }

                union = merged;
            }

            return FoldAttempt.Ok(union);
        }

        /// <summary>Folds a bare integer literal argument (e.g. LEFT/RIGHT/SUBSTRING's length or start position) - this scanner tracks only string variable values, so anything other than a literal (a variable, an expression) is declined rather than guessed.</summary>
        private static bool TryFoldIntegerLiteral(ScalarExpression expression, out int value)
        {
            switch (expression)
            {
                case IntegerLiteral literal when int.TryParse(literal.Value, out value):
                    return true;

                case ParenthesisExpression paren:
                    return TryFoldIntegerLiteral(paren.Expression, out value);

                // A negative literal (e.g. the -1 in LEFT(@x, -1)) is NOT its own literal shape -
                // ScriptDom parses the sign as a UnaryExpression wrapping an ordinary
                // IntegerLiteral (confirmed via the parsed tree, not assumed), the same way a
                // negative NumericLiteral would be. Positive's explicit '+' sign is handled the
                // same way for symmetry, even though it never changes the value.
                case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } unary
                    when TryFoldIntegerLiteral(unary.Expression, out var innerValue):
                    value = -innerValue;
                    return true;

                case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary:
                    return TryFoldIntegerLiteral(unary.Expression, out value);

                default:
                    value = 0;
                    return false;
            }
        }

        /// <summary>
        /// The classic <c>SET @sql = 'SELECT * FROM ' + QUOTENAME(@table)</c> pattern, where
        /// @table already folded constant via Tier C, previously stopped dead at the function
        /// call even though QUOTENAME's escaping is a pure, collation-independent lexical
        /// operation with only one possible result - see <see cref="TryFoldReplace"/> for the
        /// collation-SENSITIVE sibling case (REPLACE), folded by a different mechanism entirely.
        /// </summary>
        private FoldAttempt TryFoldQuoteName(FunctionCall functionCall, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (functionCall.Parameters.Count is < 1 or > 2)
            {
                return FailNonLiteralExpression(functionCall);
            }

            if (!TryFoldSingleAssemblyArgument(functionCall.Parameters[0], folded, foldingEnabled, out var inputAttempt, out var input))
            {
                return inputAttempt;
            }

            string? delimiterText = null;
            if (functionCall.Parameters.Count == 2
                && !TryFoldSingleAssemblyArgument(functionCall.Parameters[1], folded, foldingEnabled, out var delimiterAttempt, out delimiterText))
            {
                return delimiterAttempt;
            }

            var quoted = QuoteName(input!, delimiterText);
            if (quoted is null)
            {
                // Oracle-verified: QUOTENAME itself returns SQL NULL for an input over 128
                // characters or an unrecognized delimiter - concatenating NULL propagates NULL
                // through the whole @sql build, a materially different runtime outcome this
                // scanner has no NULL-tracking representation for. Failing the fold (rather than
                // silently treating it as an empty/unwrapped string) is the honest call.
                return FoldAttempt.Fail("non-literal-expression:quotename-null-result", Span(functionCall));
            }

            return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, quoted)]);
        }

        /// <summary>
        /// Folds one function-call argument that must resolve to exactly ONE concrete value to
        /// be usable inside a string-builder function (QUOTENAME and, roadmap, its sibling
        /// whitelisted builders) - an argument itself carrying multiple possible assemblies (a
        /// variable set by divergent IF branches upstream) is a rare compound shape this scanner
        /// declines rather than cross-producing through the partial-NULL edge cases a function
        /// like QUOTENAME can hit per-combination.
        /// </summary>
        private bool TryFoldSingleAssemblyArgument(
            ScalarExpression expression, Dictionary<string, FoldState> folded, bool foldingEnabled, out FoldAttempt attempt, out string? value)
        {
            attempt = TryFoldExpression(expression, folded, foldingEnabled);
            if (!attempt.Success)
            {
                value = null;
                return false;
            }

            if (attempt.Assemblies!.Count > 1)
            {
                attempt = FoldAttempt.Fail("non-literal-expression:function-call-argument-diverges", Span(expression));
                value = null;
                return false;
            }

            value = string.Concat(attempt.Assemblies[0].Select(s => s.Value));
            return true;
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
        /// <summary>
        /// CastCall/ConvertCall and SimpleCaseExpression/SearchedCaseExpression/IIfCall each have
        /// their own dedicated dispatch case earlier in <see cref="TryFoldExpression"/>'s switch
        /// (<see cref="TryFoldCastOrConvert"/>, <see cref="TryFoldConditional"/>) - every caller
        /// here either passes a <see cref="FunctionCall"/> directly (an arity/whitelist decline)
        /// or reaches this method's own <c>default</c> arm in <see cref="TryFoldExpression"/>,
        /// where those five types can never appear (already matched above). No case for them
        /// remains here on purpose.
        /// </summary>
        private FoldAttempt FailNonLiteralExpression(ScalarExpression expression) => expression switch
        {
            FunctionCall => FoldAttempt.Fail("non-literal-expression:function-call", Span(expression)),
            ColumnReferenceExpression => FoldAttempt.Fail("non-literal-expression:column-reference", Span(expression)),
            ScalarSubquery => FoldAttempt.Fail("non-literal-expression:subquery", Span(expression)),
            // Reaches here only for a BinaryExpressionType other than Add (Subtract, Multiply,
            // BitwiseAnd, ...) - Add is folded in TryFoldExpression itself; every other operator
            // on a dynamic SQL text expression is a distinct, rarer shape from a plain unhandled
            // leaf node, worth its own bucket rather than "other".
            BinaryExpression => FoldAttempt.Fail("non-literal-expression:unsupported-operator", Span(expression)),
            _ => FoldAttempt.Fail("non-literal-expression:other", Span(expression)),
        };

        /// <summary>
        /// Taints every currently-tracked variable this <paramref name="fragment"/> textually
        /// names - a sound upper bound on what an unmodeled construct could have written,
        /// because T-SQL locals have no aliasing: nothing can assign to a variable without
        /// naming it directly. A variable this fragment never mentions cannot have changed, so
        /// it is left exactly as folded so far.
        /// </summary>
        private void TaintReferencedVariables(Dictionary<string, FoldState> folded, TSqlFragment fragment, string reason)
        {
            var location = Span(fragment);
            var collector = new ReferencedVariableCollector();
            fragment.Accept(collector);
            foreach (var name in collector.Names)
            {
                if (folded.ContainsKey(name))
                {
                    folded[name] = FoldState.Tainted(reason, location);
                }
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

        /// <summary>
        /// Every variable name mentioned anywhere in a fragment, read or written alike - the
        /// sound upper bound <see cref="TaintReferencedVariables"/> taints against. Broader than
        /// <see cref="AssignedVariableCollector"/> on purpose: that collector proves a definite
        /// write (SET/SELECT-assign only, used to pre-taint a loop body before walking it), while
        /// this one bounds a POSSIBLE write for a statement whose own semantics this scanner
        /// doesn't model at all - a plain mention (e.g. inside a WHERE clause or PRINT) is not
        /// provably a write, but it is the only variables that possibly could be one.
        /// </summary>
        private sealed class ReferencedVariableCollector : TSqlFragmentVisitor
        {
            public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(VariableReference node) => Names.Add(node.Name);
        }
    }
}

/// <summary>Everything <see cref="DynamicSqlScanner.Scan"/> found in one parsed file: definite unanalyzable findings, and candidate scripts ready for <see cref="DynamicSqlPipeline"/> to reparse.</summary>
public sealed record DynamicSqlExtractionResult(
    IReadOnlyList<DynamicSqlFinding> Findings,
    IReadOnlyList<DynamicSqlScript> AnalyzableScripts);
