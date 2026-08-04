using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Rules;

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
    /// before this capability existed. <paramref name="outputSummaries"/>, when supplied, lets an
    /// ordinary `EXEC dbo.SomeProc @out = @var OUTPUT` seed the CALLER's own `@var` from a prior
    /// scan of the callee's body (keyed by (callee qualified name, callee's own OUTPUT formal
    /// parameter name), see <see cref="ProcedureOutputSummary"/>) instead of blanket-tainting it -
    /// null (the default) leaves that OUTPUT binding tainted exactly like before this capability
    /// existed. Every scan (with or without summaries supplied) records its OWN procedures'
    /// OUTPUT-parameter results on <see cref="DynamicSqlExtractionResult.OutputSummaries"/>, which
    /// is how a caller assembles the index this parameter consumes in a later pass.
    /// </summary>
    public static DynamicSqlExtractionResult Scan(
        SqlParseResult parseResult,
        DynamicSqlScope? enclosingScope = null,
        ProcCallGraph? callGraph = null,
        IReadOnlyDictionary<(string ProcedureQualifiedName, string ParameterName), IReadOnlyList<string>>? outputSummaries = null)
    {
        var visitor = new Visitor(parseResult.SourcePath, enclosingScope ?? DynamicSqlScope.None, callGraph, outputSummaries);
        if (parseResult.Fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                visitor.WalkScope(batch.Statements);
            }
        }

        return new DynamicSqlExtractionResult(visitor.Findings, visitor.Scripts, visitor.OutputSummaries);
    }

    /// <param name="SourcePath">The file this literal segment's real source text came from - or, for a placeholder, the file the unfoldable value was found in.</param>
    /// <param name="StartLine">1-based line the segment's real source text starts at (or, for a placeholder, the value's origin).</param>
    /// <param name="StartColumn">1-based column the segment's real source text starts at (or, for a placeholder, the value's origin).</param>
    /// <param name="PrefixLength">Raw characters before a literal's content (1 for <c>'</c>, 2 for <c>N'</c>) - always 0 for a placeholder, which has no quote prefix of its own.</param>
    /// <param name="Value">The segment's own text as it appears in the assembled string - the literal's already-unescaped value, or a placeholder's synthesized token.</param>
    /// <param name="PlaceholderType">
    /// Null for every ordinary literal segment (all of them, today). Non-null marks this segment
    /// as a SYNTHESIZED token standing in for a value this scanner could prove has a declared
    /// type but could not prove constant (an uninitialized DECLARE, a proc parameter with no/an
    /// ambiguous known caller) - <see cref="Value"/> still holds real text (a
    /// <c>__silentscan_sym_...__</c> token, never containing a quote), so the assembled string
    /// stays syntactically valid T-SQL; there is simply no real source text underneath this one
    /// segment. <see cref="Visitor.TryFlatten"/> is the single place this field is checked.
    /// </param>
    private readonly record struct LiteralSegment(string SourcePath, int StartLine, int StartColumn, int PrefixLength, string Value, SqlType? PlaceholderType = null);

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

    /// <summary>Emitted at every site that hits <see cref="MaxAssembliesPerVariable"/> - one shared constant so the reason string can't drift between the union, cross-concat, and argument-combination cap sites.</summary>
    private const string CardinalityCapReason = "diverges-across-if-branches:cardinality-cap";

    private readonly record struct FoldAttempt(IReadOnlyList<IReadOnlyList<LiteralSegment>>? Assemblies, string? Reason, SourceSpan? Location)
    {
        public bool Success => Assemblies is not null;

        public static FoldAttempt Ok(IReadOnlyList<IReadOnlyList<LiteralSegment>> assemblies) => new(assemblies, null, null);

        public static FoldAttempt OkSingle(IReadOnlyList<LiteralSegment> segments) => Ok([segments]);

        public static FoldAttempt Fail(string reason, SourceSpan location) => new(null, reason, location);
    }

    /// <summary>
    /// One IF's own predicate, rendered to canonical T-SQL text via <see
    /// cref="FragmentTextRenderer"/> (ScriptDOM's own generator, not raw source slicing - the same
    /// technique <see cref="Rules.FragmentTextRenderer"/> already uses elsewhere), paired with the
    /// fold outcome that predicate's THEN branch produced for one variable. Lets a LATER,
    /// syntactically unrelated IF site recover that outcome when its own guard renders to the
    /// identical text - see <see cref="Visitor.ResolveGuardedAlternatives"/>.
    /// </summary>
    private readonly record struct GuardedAlternative(string GuardText, FoldState State);

    private sealed class FoldState
    {
        public IReadOnlyList<IReadOnlyList<LiteralSegment>>? Assemblies { get; private init; }

        public string? TaintReason { get; private init; }

        public SourceSpan? TaintLocation { get; private init; }

        /// <summary>
        /// Every guard under which this variable is PROVABLY a different, known value than this
        /// state's own (possibly tainted) one - see <see cref="GuardedAlternative"/>. Never
        /// consulted by ordinary reads (<see cref="Visitor.TryFoldVariableReference"/> only ever
        /// looks at <see cref="Assemblies"/>/<see cref="TaintReason"/>) - only by <see
        /// cref="Visitor.HandleIf"/> seeding a THEN branch whose own guard matches one of these.
        /// </summary>
        public IReadOnlyList<GuardedAlternative>? GuardedAlternatives { get; private init; }

        public static FoldState Constant(IReadOnlyList<IReadOnlyList<LiteralSegment>> assemblies) => new() { Assemblies = assemblies };

        public static FoldState ConstantSingle(IReadOnlyList<LiteralSegment> segments) => Constant([segments]);

        public static FoldState Tainted(string reason, SourceSpan location) => new() { TaintReason = reason, TaintLocation = location };

        public FoldState WithGuardedAlternatives(IReadOnlyList<GuardedAlternative>? alternatives) =>
            alternatives is null
                ? this
                : new FoldState { Assemblies = Assemblies, TaintReason = TaintReason, TaintLocation = TaintLocation, GuardedAlternatives = alternatives };
    }

    private sealed class Visitor(
        string sourcePath,
        DynamicSqlScope initialScope,
        ProcCallGraph? callGraph,
        IReadOnlyDictionary<(string ProcedureQualifiedName, string ParameterName), IReadOnlyList<string>>? outputSummaryIndex)
    {

        private DynamicSqlScope _scope = initialScope;

        public List<DynamicSqlFinding> Findings { get; } = [];

        public List<DynamicSqlScript> Scripts { get; } = [];

        public List<ProcedureOutputSummary> OutputSummaries { get; } = [];

        /// <summary>
        /// Set while <see cref="ControlFlowGraph.Solve"/>'s (or <see cref="HandleWhile"/>'s own)
        /// fixpoint is still converging - a block or loop iteration whose steps include an EXEC
        /// can run several times as predecessor/prior-iteration states change round to round, but
        /// a Finding/Script/OutputSummary must be produced exactly once, from the FINAL converged
        /// state. Every place that would otherwise call Findings.Add/Scripts.Add/
        /// OutputSummaries.Add directly goes through <see cref="AddFinding"/>/<see
        /// cref="AddScript"/> (or checks this flag itself, for <see
        /// cref="RecordOutputParameterSummaries"/>) so provisional rounds are silent and only the
        /// designated final pass actually reports anything. Always false outside a fixpoint (the
        /// ordinary recursive walk runs each statement exactly once already, so it never needs
        /// this at all).
        /// </summary>
        private bool _suppressEmission;

        private void AddFinding(DynamicSqlFinding finding)
        {
            if (!_suppressEmission)
            {
                Findings.Add(finding);
            }
        }

        private void AddScript(DynamicSqlScript script)
        {
            if (!_suppressEmission)
            {
                Scripts.Add(script);
            }
        }

        /// <summary>Walks a fresh variable scope (a batch, or a proc/function body) in source order, returning the final fold state of every variable this scope declared - <see cref="WalkScopedBody"/>'s own caller needs this to read back an OUTPUT parameter's final value.</summary>
        public Dictionary<string, FoldState> WalkScope(IList<TSqlStatement> statements, IReadOnlyDictionary<string, FoldState>? initialSeed = null)
        {
            var folded = new Dictionary<string, FoldState>(StringComparer.OrdinalIgnoreCase);
            if (initialSeed is not null)
            {
                foreach (var (name, state) in initialSeed)
                {
                    folded[name] = state;
                }
            }

            // A GOTO/label anywhere in scope used to disable folding for the ENTIRE scope
            // outright - sound, but a strictly looser bound than the language actually allows.
            // ControlFlowGraph.Solve models the jump as a real edge instead, so only the
            // variables actually caught in genuine control-flow divergence pay for it.
            if (ContainsGotoOrLabel(statements))
            {
                return new ControlFlowGraph(this).Solve(statements, folded);
            }

            WalkStatements(statements, folded, foldingEnabled: true);
            return folded;
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
            var folded = WalkScope(statements, seed);

            if (qualifiedName is not null && formalParameters is { Count: > 0 })
            {
                RecordOutputParameterSummaries(qualifiedName, formalParameters, folded);
            }

            _scope = previousScope;
        }

        /// <summary>
        /// An OUTPUT-declared formal parameter is just an ordinary local variable inside the
        /// body - whatever this scan proved it holds by the end of the body (via the exact same
        /// SET/SELECT-assignment/branch-merge machinery every other tracked variable goes
        /// through) IS the value the procedure returns through it. Recorded only when provably
        /// constant, exactly like every other seed/summary this scanner produces - a parameter
        /// this scan could not fold gets no entry at all, never a guessed one.
        /// </summary>
        private void RecordOutputParameterSummaries(string qualifiedName, IList<ProcedureParameter> formalParameters, Dictionary<string, FoldState> folded)
        {
            foreach (var formal in formalParameters)
            {
                if (formal.Modifier != ParameterModifier.Output)
                {
                    continue;
                }

                if (!_suppressEmission && folded.TryGetValue(formal.VariableName.Value, out var state) && state.Assemblies is { } assemblies)
                {
                    // An assembly containing a placeholder is not a proven value - skip it rather
                    // than publishing a fabricated string as this OUTPUT parameter's summary. If
                    // every assembly is a placeholder, no summary is published at all (matching
                    // this method's own contract: no entry at all, never a guessed one), not an
                    // empty-but-present one that could be mistaken for "proven to be empty".
                    var values = assemblies.Select(TryFlatten).Where(v => v is not null).Select(v => v!).Distinct(StringComparer.Ordinal).ToList();
                    if (values.Count > 0)
                    {
                        OutputSummaries.Add(new ProcedureOutputSummary(qualifiedName, formal.VariableName.Value, values));
                    }
                }
            }
        }

        /// <summary>
        /// Seeds a proc body's own formal parameters as constant-foldable when the call graph
        /// saw exactly one caller passing a string literal for that parameter - see
        /// <see cref="ProcCallGraph.SingleCallSiteFor"/> for why "exactly one call site THIS
        /// SCAN saw" is the only case a single value can be trusted at all. A parameter with
        /// zero call sites is left unseeded entirely (falls back to today's plain
        /// "variable-not-in-scope" if referenced - unchanged behavior, not a regression). A
        /// parameter seen at MULTIPLE call sites, or passed something other than a string
        /// literal at its one call site, is explicitly tainted with its own reason rather than
        /// silently falling through to the generic "variable-not-in-scope" a caller-blind scan
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
                // with no known value. When its declared type resolves, seeds a symbolic
                // placeholder of that type (see SeedSymbolicOrTaint) rather than a bare taint -
                // T-SQL's own type contract for the proc guarantees the runtime value really is
                // of this type, so this is soundness-preserving, not a guess. Only when the type
                // itself can't resolve (a CREATE TYPE ... FROM alias this scan can't look up
                // without a catalog) does this fall back to an honest, specific taint reason
                // rather than the generic "variable-not-in-scope" a caller-blind
                // VariableReference lookup would otherwise report.
                foreach (var formal in formalParameters)
                {
                    seed[formal.VariableName.Value] = SeedSymbolicOrTaint(formal, "procedure-parameter:no-known-call-site");
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

        /// <summary>
        /// A token this scanner invents to stand in for a value it could not prove constant -
        /// derived from the placeholder's OWN ORIGIN (path is carried separately on the segment;
        /// this covers line/column), never a counter: HandleWhile and ControlFlowGraph.Solve
        /// replay the same statements many times over a fixpoint, and a counter-based id would
        /// renumber on every round, tainting at the cardinality cap and breaking deterministic
        /// output. A regular T-SQL identifier containing no quote, so it can never break out of
        /// a string literal it's concatenated into.
        /// </summary>
        private static string PlaceholderToken(int startLine, int startColumn) => $"__silentscan_sym_L{startLine}C{startColumn}__";

        /// <summary>
        /// Seeds <paramref name="formal"/> as a symbolic placeholder of its own declared type when
        /// that type resolves - built-in types (varchar, int, ...) always do, since
        /// <see cref="DynamicSqlScanner"/> runs before <see cref="Catalog.CatalogBuilder"/> and so
        /// has no catalog to resolve a CREATE TYPE ... FROM alias through; only that alias case
        /// falls back to <paramref name="taintReasonIfUnresolvable"/>. CLAUDE.md's "never guess":
        /// an unresolvable type means genuinely nothing is known, not even a shape, so it must
        /// stay a plain taint, not a placeholder claiming a type this scanner couldn't actually
        /// determine.
        /// </summary>
        private FoldState SeedSymbolicOrTaint(ProcedureParameter formal, string taintReasonIfUnresolvable) =>
            SeedSymbolicOrTaint(formal, formal.DataType, taintReasonIfUnresolvable);

        /// <summary>Same policy as the <see cref="ProcedureParameter"/> overload, generalized to any declaring fragment (a DECLARE element has no formal-parameter concept at all, but the same "resolvable type -> placeholder, else taint" rule applies identically).</summary>
        private FoldState SeedSymbolicOrTaint(TSqlFragment declaringSite, DataTypeReference dataType, string taintReasonIfUnresolvable)
        {
            var location = Span(declaringSite);
            var type = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null);
            if (type is null)
            {
                return FoldState.Tainted(taintReasonIfUnresolvable, location);
            }

            var token = PlaceholderToken(location.Line, location.Column);
            return FoldState.ConstantSingle([new LiteralSegment(location.SourcePath, location.Line, location.Column, PrefixLength: 0, token, type)]);
        }

        private void SeedFromSingleEdge(ProcCallEdge edge, IList<ProcedureParameter> formalParameters, Dictionary<string, FoldState> seed)
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
                    : SeedSymbolicOrTaint(formal, "parameter-not-seeded:non-literal-caller");
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
        private void SeedFromMultipleEdges(IReadOnlyList<ProcCallEdge> edges, IList<ProcedureParameter> formalParameters, Dictionary<string, FoldState> seed)
        {
            foreach (var formal in formalParameters)
            {
                seed[formal.VariableName.Value] = SeedOneParameterFromMultipleEdges(edges, formal);
            }
        }

        private FoldState SeedOneParameterFromMultipleEdges(IReadOnlyList<ProcCallEdge> edges, ProcedureParameter formal)
        {
            var paramName = formal.VariableName.Value;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var assemblies = new List<IReadOnlyList<LiteralSegment>>();

            foreach (var edge in edges)
            {
                var argument = edge.Arguments.FirstOrDefault(
                    a => string.Equals(a.FormalParameterName, paramName, StringComparison.OrdinalIgnoreCase));
                if (argument is null || argument.FormalParameterIsOutput || argument.LiteralArgument is not { } literalArgument)
                {
                    // At least one known caller can't supply a literal - the parameter's true
                    // value set genuinely includes something this scan can't pin down, so the
                    // whole parameter folds to a symbolic placeholder of its own declared type
                    // (same treatment Change 3 gives a single unseeded caller) rather than the
                    // literals collected from OTHER callers so far, which would overstate what is
                    // actually known.
                    return SeedSymbolicOrTaint(formal, "parameter-not-seeded:non-literal-caller");
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
                    // An unrecognized statement kind (PRINT, RAISERROR, INSERT/UPDATE/DELETE,
                    // WAITFOR, THROW, DBCC, ...) can only ever WRITE a scalar local through one
                    // of a small, closed set of T-SQL mechanisms this switch doesn't otherwise
                    // model: the legacy "quirky update" (UPDATE ... SET @v = col), cursor
                    // FETCH INTO, and RECEIVE's own SELECT-list variable targets. T-SQL locals
                    // cannot alias, so any OTHER mention of a variable inside a statement of this
                    // kind - a WHERE clause, a PRINT argument, a RAISERROR format arg - is
                    // necessarily a READ, not a write, and must not disturb its folded state.
                    // Taints exactly the variables one of those write mechanisms names (never a
                    // blanket sweep of every mention), and leaves every other tracked variable,
                    // and every statement with no write mechanism at all, completely untouched.
                    TaintWrittenVariables(folded, statement, "unsupported-statement-in-scope");
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
                    // No initializer at all - genuinely no value assigned, ever, up to this
                    // point. When the declared type resolves, this is still a symbolic
                    // placeholder rather than a bare taint: the variable's declared type is a
                    // hard T-SQL guarantee regardless of whether it was ever assigned (an
                    // uninitialized local's type doesn't change), so the same "known shape,
                    // unknown value" treatment applies.
                    folded[name] = SeedSymbolicOrTaint(element, element.DataType, "no-initializer");
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
                    folded[name] = FoldState.Tainted(existing?.TaintReason ?? "variable-not-in-scope", existing?.TaintLocation ?? Span(site));
                    return;
                }

                if (!rhs.Success)
                {
                    folded[name] = FoldState.Tainted(rhs.Reason!, rhs.Location!.Value);
                    return;
                }

                folded[name] = TryCartesianConcat(existing.Assemblies, rhs.Assemblies!, out var combined)
                    ? FoldState.Constant(combined)
                    : FoldState.Tainted(CardinalityCapReason, Span(site));
                return;
            }

            folded[name] = rhs.Success
                ? FoldState.Constant(rhs.Assemblies!)
                : FoldState.Tainted(rhs.Reason!, rhs.Location!.Value);
        }

        private void HandleIf(IfStatement ifStatement, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            var guardText = FragmentTextRenderer.Render(ifStatement.Predicate);

            var thenSeed = ResolveGuardedAlternatives(folded, guardText);
            var thenDict = new Dictionary<string, FoldState>(thenSeed, StringComparer.OrdinalIgnoreCase);
            WalkStatements(NormalizeToStatementList(ifStatement.ThenStatement), thenDict, foldingEnabled);

            var elseDict = new Dictionary<string, FoldState>(folded, StringComparer.OrdinalIgnoreCase);
            if (ifStatement.ElseStatement is not null)
            {
                WalkStatements(NormalizeToStatementList(ifStatement.ElseStatement), elseDict, foldingEnabled);
            }

            MergeUnioningDivergent(folded, thenDict, elseDict, ifStatement, "diverges-across-if-branches", guardText);
        }

        /// <summary>
        /// Seeds a THEN branch about to run under a KNOWN-true <paramref name="guardText"/>: any
        /// variable whose current state was itself produced by an earlier IF/ELSE-IF chain (<see
        /// cref="MergeUnioningDivergent"/>) recording a <see cref="GuardedAlternative"/> for this
        /// EXACT same guard text is resolved to that recorded outcome instead of whatever the
        /// general (possibly tainted) merge left behind - the "IF cond SET @sql=... ; ... ; IF
        /// cond EXEC(@sql)" pattern, where the second IF's own guard proves the first IF's THEN
        /// branch is exactly the path that ran. Deliberately exact text equality, not
        /// implication - a weaker or differently-shaped second guard (even one a human could prove
        /// implies the first) is left unresolved rather than guessed, per this scanner's
        /// soundness-first policy. Returns <paramref name="folded"/> itself, unchanged, when
        /// nothing matches - the overwhelmingly common case - rather than always cloning.
        /// </summary>
        private static Dictionary<string, FoldState> ResolveGuardedAlternatives(Dictionary<string, FoldState> folded, string guardText)
        {
            Dictionary<string, FoldState>? resolved = null;
            foreach (var (key, state) in folded)
            {
                if (state.GuardedAlternatives is not { } alternatives)
                {
                    continue;
                }

                var match = alternatives
                    .Where(a => string.Equals(a.GuardText, guardText, StringComparison.Ordinal))
                    .Select(a => (GuardedAlternative?)a)
                    .FirstOrDefault();
                if (match is { } alternative)
                {
                    resolved ??= new Dictionary<string, FoldState>(folded, StringComparer.OrdinalIgnoreCase);
                    resolved[key] = alternative.State;
                }
            }

            return resolved ?? folded;
        }

        /// <summary>
        /// Solves the loop body as a genuine fixpoint instead of pre-tainting every variable it
        /// assigns before even walking it: <c>Header_0 = loop entry state</c>,
        /// <c>Header_{n+1} = merge(loop entry, body applied to Header_n)</c>, iterated until
        /// stable (guaranteed - the assembly-set lattice is monotonic and bounded by
        /// <see cref="MaxAssembliesPerVariable"/>, past which a variable that keeps growing new
        /// values every iteration widens straight to taint, same absorbing-taint semantics as
        /// every other merge). A variable the body never touches keeps its entry value from
        /// round zero, unioned with itself forever after - reference-stable, so it converges
        /// immediately rather than needing the full round count. A variable the body assigns the
        /// SAME literal every iteration converges to that one value once the union stops
        /// growing, typically within two or three rounds. The fixpoint IS the state after the
        /// loop exits (having run zero, one, or many times) - nothing further to merge in
        /// afterward. EXEC/OUTPUT-summary emission is suppressed during the search (each
        /// candidate round is provisional) and replayed exactly once, from the converged header,
        /// once the search settles - <see cref="_suppressEmission"/>.
        /// </summary>
        private void HandleWhile(WhileStatement whileStatement, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            var bodyStatements = NormalizeToStatementList(whileStatement.Statement);
            var loopEntry = new Dictionary<string, FoldState>(folded, StringComparer.OrdinalIgnoreCase);

            var wasSuppressed = _suppressEmission;
            _suppressEmission = true;

            var header = new Dictionary<string, FoldState>(loopEntry, StringComparer.OrdinalIgnoreCase);
            const int maxIterations = MaxAssembliesPerVariable + 2;
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                var bodyResult = new Dictionary<string, FoldState>(header, StringComparer.OrdinalIgnoreCase);
                WalkStatements(bodyStatements, bodyResult, foldingEnabled);

                var nextHeader = MergeInto(loopEntry, bodyResult, "while-loop-body");
                var converged = StatesEqual(nextHeader, header);
                header = nextHeader;
                if (converged)
                {
                    break;
                }
            }

            _suppressEmission = wasSuppressed;
            if (!wasSuppressed)
            {
                // Reproduces the exact same fold results (header is now a fixpoint, so applying
                // the body to it again changes nothing) - this time with emission enabled.
                WalkStatements(bodyStatements, new Dictionary<string, FoldState>(header, StringComparer.OrdinalIgnoreCase), foldingEnabled);
            }

            folded.Clear();
            foreach (var (key, value) in header)
            {
                folded[key] = value;
            }
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
        /// An IF's THEN/ELSE or a TRY/CATCH's TRY/CATCH are each exactly one of two mutually
        /// exclusive, fully-determined outcomes - unlike a WHILE body (<see cref="HandleWhile"/>,
        /// which solves its own genuine fixpoint since a loop can run zero, one, or many times).
        /// When BOTH branches independently folded a touched variable to a constant assembly set, the real
        /// value after the statement is PROVABLY one of the two branches' own assemblies, so this
        /// unions them (deduplicated, cardinality-capped - see <see cref="TryUnionAssemblies"/>)
        /// instead of tainting - the optional-filter accumulation pattern this scanner previously
        /// declined outright (CLAUDE.md dynamic SQL policy). A variable only one branch actually
        /// assigned differently still merges here (reference-inequality against the
        /// pre-statement state decides "touched") - only a variable BOTH branches leave
        /// bit-for-bit unchanged from <paramref name="folded"/> is skipped entirely.
        /// </summary>
        private void MergeUnioningDivergent(
            Dictionary<string, FoldState> folded, Dictionary<string, FoldState> branchA, Dictionary<string, FoldState> branchB, TSqlStatement owner, string reason, string? guardText = null)
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

                var merged = MergeOne(a, b, reason, location);
                folded[key] = guardText is not null && a is not null
                    ? merged.WithGuardedAlternatives(CombineGuardedAlternatives(guardText, before, a, b))
                    : merged;
            }
        }

        /// <summary>Bounds a variable's own accumulated <see cref="GuardedAlternative"/> list the same defensive way <see cref="MaxAssembliesPerVariable"/> bounds assembly sets - a proc with many sequential guarded IFs stops growing new entries rather than never terminating; losing the newest entry only means one later guard site stays unresolved, never a soundness break.</summary>
        private const int MaxGuardedAlternatives = 16;

        private static List<GuardedAlternative>? CombineGuardedAlternatives(string guardText, FoldState? before, FoldState branchAOutcome, FoldState? branchB)
        {
            List<GuardedAlternative> combined = [];
            if (before?.GuardedAlternatives is { } beforeAlternatives)
            {
                combined.AddRange(beforeAlternatives);
            }

            if (branchB?.GuardedAlternatives is { } branchBAlternatives)
            {
                combined.AddRange(branchBAlternatives);
            }

            combined.Add(new GuardedAlternative(guardText, branchAOutcome));
            return combined.Count <= MaxGuardedAlternatives ? combined : null;
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
        /// A full two-way state merge (every key either side has, not just the ones already
        /// known to be "touched") - unlike <see cref="MergeUnioningDivergent"/>'s own touched-set
        /// restriction (safe there because both its inputs are always full clones of the SAME
        /// pre-statement dictionary), <see cref="HandleWhile"/>'s fixpoint and
        /// <see cref="ControlFlowGraph"/>'s own predecessor merges combine states that may never
        /// have shared a common ancestor dictionary at all, so every key must be considered. A
        /// key both sides trace back to the SAME <see cref="FoldState"/> object (no divergence
        /// through this path) is kept as-is rather than needlessly re-wrapped through <see
        /// cref="MergeOne"/> - <see cref="StatesEqual"/>'s own convergence check depends on this
        /// to detect a genuine fixpoint via reference equality instead of a deep value compare.
        /// </summary>
        private static Dictionary<string, FoldState> MergeInto(Dictionary<string, FoldState> a, Dictionary<string, FoldState> b, string reason)
        {
            var result = new Dictionary<string, FoldState>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(a.Keys, StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(b.Keys);

            foreach (var key in keys)
            {
                var av = a.GetValueOrDefault(key);
                var bv = b.GetValueOrDefault(key);
                result[key] = ReferenceEquals(av, bv) ? av! : MergeOne(av, bv, reason, default);
            }

            return result;
        }

        /// <summary>Reference-equality state comparison - sound as a fixpoint convergence check specifically because <see cref="MergeInto"/> preserves a key's existing object reference whenever both sides already agreed, so "nothing changed" and "still reference-equal" coincide exactly.</summary>
        private static bool StatesEqual(Dictionary<string, FoldState> a, Dictionary<string, FoldState> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            foreach (var (key, value) in a)
            {
                if (!b.TryGetValue(key, out var otherValue) || !ReferenceEquals(value, otherValue))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The one place an assembly is ever flattened to a plain string - every other flattening
        /// site in this file must go through here rather than concatenating <see cref="LiteralSegment.Value"/>
        /// directly. Null whenever any segment is a placeholder (<see cref="LiteralSegment.PlaceholderType"/>
        /// non-null): a placeholder's <see cref="LiteralSegment.Value"/> is a synthesized token,
        /// never a value this scanner is entitled to treat as the real one, so every caller that
        /// needs an actual runtime value (a @params declaration, a LEN, a pure-function argument)
        /// must handle this failing rather than silently flattening a fabricated string.
        /// </summary>
        private static string? TryFlatten(IReadOnlyList<LiteralSegment> assembly) =>
            assembly.Any(s => s.PlaceholderType is not null) ? null : string.Concat(assembly.Select(s => s.Value));

        /// <summary>
        /// The dedupe identity of an assembly - concatenated text for an ordinary (placeholder-free)
        /// assembly, same as before this field existed (<see cref="TryFlatten"/> succeeds). A
        /// placeholder segment instead contributes its type name bracketed in a control character
        /// that cannot appear in real SQL text: the bracket matters, not just the type name,
        /// because an unbracketed type name could collide with an unrelated literal assembly whose
        /// own text happens to match it (a literal "prefixnvarchar" versus "prefix" plus a
        /// placeholder of type nvarchar), and the token TEXT itself can't be used either (it would
        /// let two DIFFERENT placeholder origins collide, since the token depends only on source
        /// position, not on what's actually unknown). Two placeholders of the SAME type at the
        /// same position in the key correctly collapse to one assembly (an unknown nvarchar value
        /// here is an unknown nvarchar value here, regardless of which DECLARE it came from); two
        /// of DIFFERENT types do not.
        /// </summary>
        private static string AssemblyDedupeKey(IReadOnlyList<LiteralSegment> assembly) =>
            TryFlatten(assembly) ?? string.Concat(assembly.Select(s => s.PlaceholderType is { } type ? $"\u0001{type}\u0001" : s.Value));

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
                if (!seen.Add(AssemblyDedupeKey(assembly)))
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
                    if (!seen.Add(AssemblyDedupeKey(merged)))
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
        /// references cannot have been mutated by it. An OUTPUT argument this scan already
        /// PROVED the callee always assigns a constant to (<see cref="SeedKnownOutputArguments"/>)
        /// is seeded with that value instead of tainted - the one case this scanner CAN see
        /// through what an arbitrary called procedure does internally.
        /// </summary>
        private void TaintExecuteMutatedVariables(ExecuteStatement node, Dictionary<string, FoldState> folded)
        {
            var seeded = SeedKnownOutputArguments(node, folded);
            TaintReferencedVariables(folded, node, "unsupported-execute-form", seeded);
        }

        /// <summary>
        /// Matches this exact EXEC call site to its own <see cref="ProcCallGraph"/> edge (built
        /// separately, before dynamic-SQL scanning even starts, from the same argument-to-formal
        /// matching every input-parameter seed already relies on) and seeds any OUTPUT argument
        /// whose callee formal parameter has a known <see cref="ProcedureOutputSummary"/> - the
        /// callee's target must have resolved to a cataloged procedure (an edge exists at all)
        /// AND this scan must have already proved that specific OUTPUT parameter constant in an
        /// earlier pass (see Reporting.ScanReportBuilder's fixed-point loop). Returns the set
        /// of caller variable names seeded, so the caller can exclude them from the blanket taint
        /// every other OUTPUT/return-value argument on this same call still needs.
        /// </summary>
        private HashSet<string> SeedKnownOutputArguments(ExecuteStatement node, Dictionary<string, FoldState> folded)
        {
            var seeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (callGraph is null || outputSummaryIndex is null)
            {
                return seeded;
            }

            var edge = callGraph.EdgeAt(Span(node));
            if (edge is null)
            {
                return seeded;
            }

            foreach (var argument in edge.Arguments)
            {
                if (!argument.FormalParameterIsOutput || argument.CallerVariableName is not { } callerVariable)
                {
                    continue;
                }

                if (!outputSummaryIndex.TryGetValue((edge.CalleeQualifiedName, argument.FormalParameterName), out var values))
                {
                    continue;
                }

                IReadOnlyList<IReadOnlyList<LiteralSegment>> assemblies =
                    [.. values.Select(v => (IReadOnlyList<LiteralSegment>)[new LiteralSegment(sourcePath, node.StartLine, node.StartColumn, PrefixLength: 0, v)])];
                folded[callerVariable] = FoldState.Constant(assemblies);
                seeded.Add(callerVariable);
            }

            return seeded;
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
                    AddFinding(Unanalyzable(node, attempt.Reason!));
                    return;
                }

                if (!TryCartesianConcat(assemblies, attempt.Assemblies!, out var next))
                {
                    AddFinding(Unanalyzable(node, CardinalityCapReason));
                    return;
                }

                assemblies = next;
            }

            foreach (var assembly in assemblies)
            {
                AddScript(BuildScript(node, assembly, parameterDeclarationText: null, argumentBindings: null));
            }
        }

        private void HandleSpExecuteSql(ExecutableProcedureReference procRef, ExecuteStatement node, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            if (procRef.Parameters.Count == 0)
            {
                AddFinding(Unanalyzable(node, "non-literal-argument"));
                return;
            }

            var statementArg = ResolveNamedOrPositionalArgument(procRef.Parameters, index: 0, "@stmt", "@statement");
            if (statementArg is null)
            {
                AddFinding(Unanalyzable(node, "non-literal-argument"));
                return;
            }

            var queryAttempt = TryFoldExpression(statementArg, folded, foldingEnabled);
            if (!queryAttempt.Success)
            {
                AddFinding(Unanalyzable(node, queryAttempt.Reason!));
                return;
            }

            var parameterDeclarationText = ResolveParameterDeclarationText(procRef, folded, foldingEnabled);
            var argumentBindings = ResolveArgumentBindings(procRef);
            foreach (var assembly in queryAttempt.Assemblies!)
            {
                AddScript(BuildScript(node, assembly, parameterDeclarationText, argumentBindings));
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

            return TryFlatten(attempt.Assemblies[0]);
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
            List<PlaceholderOccurrence>? occurrences = null;
            foreach (var segment in segments)
            {
                if (segment.PlaceholderType is { } placeholderType)
                {
                    var innerStart = segmentMap.AppendPlaceholder(segment.SourcePath, segment.StartLine, segment.StartColumn, segment.Value);
                    occurrences ??= [];
                    occurrences.Add(new PlaceholderOccurrence(
                        innerStart, segment.Value.Length, placeholderType,
                        new SourceSpan(segment.SourcePath, segment.StartLine, segment.StartColumn)));
                }
                else
                {
                    segmentMap.AppendLiteral(segment.SourcePath, segment.StartLine, segment.StartColumn, segment.PrefixLength, segment.Value);
                }
            }

            // Any placeholder segment means this ONE assembly rests on an assumption, not proven
            // source text - Medium, regardless of where the placeholder ends up sitting in the
            // reparsed statement. The pipeline's own position classifier (quoted-literal vs
            // object-identifier vs neither) decides separately whether any finding is even
            // emitted from this script at all; this field only ever needs to be a safe upper
            // bound, not the final word on what gets reported.
            var confidence = occurrences is null ? FindingConfidence.High : FindingConfidence.Medium;

            return new DynamicSqlScript(CallSite(node), segmentMap.InnerText, segmentMap, parameterDeclarationText, _scope, argumentBindings, confidence, occurrences);
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

                // A block comment sitting directly between two `+` concatenation operators
                // (`'a' + /* comment */ + 'b'`) parses not as three-term concatenation but as
                // `'a' + (+'b')` - confirmed directly against the parsed tree: the second `+` is
                // a UnaryExpression wrapping 'b', not a second BinaryExpression operand. Unary
                // plus is semantically a no-op for a string operand (it does not exist as a real
                // T-SQL operator on strings; this is purely how the parser resolves the token),
                // so folding through to the inner expression is exact, not an approximation.
                case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary:
                    return TryFoldExpression(unary.Expression, folded, foldingEnabled);

                case FunctionCall { FunctionName.Value: var functionName } quoteNameCall
                    when string.Equals(functionName, FnQuoteName, StringComparison.OrdinalIgnoreCase):
                    return TryFoldQuoteName(quoteNameCall, folded, foldingEnabled);

                case FunctionCall { FunctionName.Value: var functionName } charCall
                    when string.Equals(functionName, FnChar, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(functionName, FnNChar, StringComparison.OrdinalIgnoreCase):
                    return TryFoldCharOrNChar(charCall, functionName, folded, foldingEnabled);

                // ISNULL(a, b): whenever this scanner successfully folds `a` at all, that value is
                // PROVABLY non-NULL - a variable folds to Constant assemblies only by tracing a
                // real literal/DECLARE/SET chain, and a bare `SET @x = NULL` fails to fold (no
                // NullLiteral case anywhere in this switch) rather than being silently treated as
                // some placeholder value. ISNULL therefore always evaluates to `a` whenever `a`
                // folds, regardless of `b` - `b` is never even inspected, exactly mirroring
                // CoalesceExpression below.
                case FunctionCall { FunctionName.Value: var functionName } isNullCall
                    when string.Equals(functionName, FnIsNull, StringComparison.OrdinalIgnoreCase) && isNullCall.Parameters.Count == 2:
                    return TryFoldExpression(isNullCall.Parameters[0], folded, foldingEnabled);

                // COALESCE(a, b, ...): same "a successfully-folded expression is provably non-NULL"
                // argument as ISNULL above - the result is always `a`'s value whenever `a` folds,
                // so later arguments are never inspected. NULLIF is deliberately NOT handled here:
                // unlike ISNULL/COALESCE it can produce a genuine NULL even when its first argument
                // folds (when the two arguments compare equal), which this scanner has no
                // LiteralSegment representation for - it falls through to the generic
                // "non-literal-expression:other" refusal below rather than being guessed at.
                case CoalesceExpression { Expressions.Count: > 0 } coalesce:
                    return TryFoldExpression(coalesce.Expressions[0], folded, foldingEnabled);

                case FunctionCall { FunctionName.Value: var functionName } builderCall
                    when WhitelistedStringBuilders.Contains(functionName):
                    return TryFoldStringBuilder(builderCall, functionName, folded, foldingEnabled);

                case FunctionCall { FunctionName.Value: var functionName }
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
                    return TryFoldLeftOrRight(leftCall.Parameters, FnLeft, leftCall, folded, foldingEnabled);

                case RightFunctionCall rightCall:
                    return TryFoldLeftOrRight(rightCall.Parameters, FnRight, rightCall, folded, foldingEnabled);

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
                return FoldAttempt.Fail("variable-not-in-scope", Span(variableRef));
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
                : FoldAttempt.Fail(CardinalityCapReason, Span(binary));
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
            FnUpper, FnLower, FnLtrim, FnRtrim, FnSubstring, FnReplace,
        };

        // Named once so a function's identity is a single S1192-clean source of truth across the
        // dispatch switch, WhitelistedStringBuilders, and PlaceholderTypeTransfer below, rather
        // than the same literal typed out at each site independently.
        private const string FnUpper = "UPPER";
        private const string FnLower = "LOWER";
        private const string FnLtrim = "LTRIM";
        private const string FnRtrim = "RTRIM";
        private const string FnSubstring = "SUBSTRING";
        private const string FnReplace = "REPLACE";
        private const string FnLeft = "LEFT";
        private const string FnRight = "RIGHT";
        private const string FnQuoteName = "QUOTENAME";
        private const string FnChar = "CHAR";
        private const string FnNChar = "NCHAR";
        private const string FnIsNull = "ISNULL";

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

        /// <summary>
        /// Per-builtin knowledge of how a call transforms a symbolic (placeholder) input's TYPE -
        /// consulted only by <see cref="TryTransferPlaceholderThroughFunction"/>, when the source
        /// argument couldn't be folded to a real value at all. Every entry preserves category and
        /// collation (never widens what's actually known); none of them ever change collation.
        /// UPPER/LOWER/LTRIM/RTRIM return the exact input type unchanged - oracle-verified
        /// (<c>UPPER(CAST('a' AS varchar(10)))</c> stays <c>varchar(10)</c>, per
        /// SQL_VARIANT_PROPERTY against the Docker oracle, not widened to <c>varchar(max)</c> or
        /// narrowed to <c>varchar(1)</c>). SUBSTRING/LEFT/RIGHT can only shorten, never change
        /// category/collation - the existing oracle direction-control fixture for placeholders
        /// already proves verdicts are category-driven, not length-driven, so carrying the input's
        /// own declared length through here (rather than the true, unknowable runtime length)
        /// never affects the seek/scan verdict this scanner exists to compute. REPLACE cannot
        /// change its SOURCE argument's own type either way, regardless of what the pattern/
        /// replacement turn out to be - <see cref="TryFoldReplace"/> only ever consults this table
        /// for its source argument, never pattern/replacement, so those still refuse as before
        /// when they're not themselves literal. A function absent from this table has no known
        /// type effect and the placeholder fold declines with the same
        /// <c>symbolic-value-in-function-argument</c> reason it always has.
        /// </summary>
        private static readonly Dictionary<string, Func<SqlType, SqlType>> PlaceholderTypeTransfer = new(StringComparer.OrdinalIgnoreCase)
        {
            [FnUpper] = t => t,
            [FnLower] = t => t,
            [FnLtrim] = t => t,
            [FnRtrim] = t => t,
            [FnSubstring] = t => t,
            [FnLeft] = t => t,
            [FnRight] = t => t,
            [FnReplace] = t => t,

            // QUOTENAME always returns nvarchar(258) regardless of the input's own type/length -
            // oracle-verified (SQL_VARIANT_PROPERTY MaxLength = 516 bytes = 258 UTF-16 code
            // units), the one entry here that does NOT preserve the input type.
            [FnQuoteName] = _ => new SqlType(SqlTypeCategory.NVarChar, Length: 258),
        };

        /// <summary>
        /// The general mechanism that closes "a placeholder folded through a known function still
        /// refuses": when <paramref name="source"/>'s own fold is a PURE placeholder (a single
        /// assembly holding nothing but one placeholder segment - never a value this function
        /// could actually run on, per <see cref="TryFlattenArgumentValues"/>'s own reasoning),
        /// this function's TYPE effect on that placeholder is looked up in
        /// <see cref="PlaceholderTypeTransfer"/> (or supplied directly via
        /// <paramref name="explicitTargetType"/> for CAST/CONVERT, whose target type is already
        /// pinned by the call site's own syntax) rather than the whole fold refusing outright.
        /// Returns null - not a <see cref="FoldAttempt"/> - whenever <paramref name="source"/>
        /// isn't a pure placeholder at all (it folded to a real value, failed outright, or folded
        /// to more than one assembly - the multi-assembly case, e.g. a variable set differently
        /// across IF branches where one branch is symbolic, is deliberately left unhandled here
        /// and still refuses via the ordinary <see cref="TryFoldOverArgumentCombinations"/> path,
        /// exactly as before this method existed - not a regression, an edge case intentionally
        /// left for a future pass rather than faked), signalling "handle this the normal way" to
        /// the caller. A MIXED assembly (literal text alongside a placeholder, e.g.
        /// <c>'prefix' + @sym</c>) is likewise not pure and falls through to null, so it still
        /// refuses exactly as before.
        /// </summary>
        private FoldAttempt? TryTransferPlaceholderThroughFunction(
            ScalarExpression source, string functionKey, TSqlFragment site,
            Dictionary<string, FoldState> folded, bool foldingEnabled, SqlType? explicitTargetType = null)
        {
            var attempt = TryFoldExpression(source, folded, foldingEnabled);
            if (!attempt.Success || attempt.Assemblies!.Count != 1 || attempt.Assemblies[0].Count != 1)
            {
                return null;
            }

            if (attempt.Assemblies[0][0].PlaceholderType is not { } inputType)
            {
                return null;
            }

            var transferred = explicitTargetType
                ?? (PlaceholderTypeTransfer.TryGetValue(functionKey, out var transfer) ? transfer(inputType) : null);
            if (transferred is null)
            {
                return FoldAttempt.Fail("symbolic-value-in-function-argument", Span(source));
            }

            var token = PlaceholderToken(site.StartLine, site.StartColumn);
            return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, site.StartLine, site.StartColumn, PrefixLength: 0, token, transferred)]);
        }

        private FoldAttempt TryFoldStringBuilder(FunctionCall functionCall, string functionName, Dictionary<string, FoldState> folded, bool foldingEnabled) =>
            functionName.ToUpperInvariant() switch
            {
                FnUpper or FnLower => TryFoldCaseConversion(functionCall, functionName, folded, foldingEnabled),
                FnLtrim or FnRtrim => TryFoldTrim(functionCall, functionName, folded, foldingEnabled),
                FnSubstring => TryFoldSubstring(functionCall, folded, foldingEnabled),
                FnReplace => TryFoldReplace(functionCall, folded, foldingEnabled),
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

            if (TryTransferPlaceholderThroughFunction(functionCall.Parameters[0], functionName, functionCall, folded, foldingEnabled) is { } transferred)
            {
                return transferred;
            }

            return TryFoldOverArgumentCombinations([functionCall.Parameters[0]], folded, foldingEnabled, functionCall, values =>
            {
                var input = values[0];
                if (!IsSafeToCaseConvert(input))
                {
                    return FoldAttempt.Fail("non-literal-expression:case-conversion-collation-sensitive", Span(functionCall));
                }

                var converted = string.Equals(functionName, FnUpper, StringComparison.OrdinalIgnoreCase)
                    ? input.ToUpperInvariant()
                    : input.ToLowerInvariant();
                return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, converted)]);
            });
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

            if (TryTransferPlaceholderThroughFunction(functionCall.Parameters[0], functionName, functionCall, folded, foldingEnabled) is { } transferred)
            {
                return transferred;
            }

            return TryFoldOverArgumentCombinations([functionCall.Parameters[0]], folded, foldingEnabled, functionCall, values =>
            {
                var trimmed = string.Equals(functionName, FnLtrim, StringComparison.OrdinalIgnoreCase)
                    ? values[0].TrimStart(' ')
                    : values[0].TrimEnd(' ');
                return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, trimmed)]);
            });
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

            if (!TryFoldIntegerLiteral(parameters[1], folded, foldingEnabled, out var length))
            {
                return FoldAttempt.Fail("non-literal-expression:function-call-argument-diverges", Span(parameters[1]));
            }

            if (length < 0)
            {
                return FoldAttempt.Fail("non-literal-expression:negative-length", Span(site));
            }

            if (TryTransferPlaceholderThroughFunction(parameters[0], functionName, site, folded, foldingEnabled) is { } transferred)
            {
                return transferred;
            }

            return TryFoldOverArgumentCombinations([parameters[0]], folded, foldingEnabled, site, values =>
            {
                var input0 = values[0];
                var clampedLength = Math.Min(length, input0.Length);
                var result = string.Equals(functionName, FnLeft, StringComparison.OrdinalIgnoreCase)
                    ? input0[..clampedLength]
                    : input0[^clampedLength..];
                return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, site.StartLine, site.StartColumn, PrefixLength: 0, result)]);
            });
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

            if (!TryFoldIntegerLiteral(functionCall.Parameters[1], folded, foldingEnabled, out var start)
                || !TryFoldIntegerLiteral(functionCall.Parameters[2], folded, foldingEnabled, out var length))
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

            if (TryTransferPlaceholderThroughFunction(functionCall.Parameters[0], FnSubstring, functionCall, folded, foldingEnabled) is { } transferred)
            {
                return transferred;
            }

            return TryFoldOverArgumentCombinations([functionCall.Parameters[0]], folded, foldingEnabled, functionCall, values =>
            {
                var input0 = values[0];
                if (start > input0.Length)
                {
                    return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, string.Empty)]);
                }

                var clampedLength = Math.Min(length, input0.Length - (start - 1));
                var result = input0.Substring(start - 1, clampedLength);
                return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, result)]);
            });
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

            // Only the SOURCE argument is eligible for a placeholder-type transfer - REPLACE
            // cannot change its source's own type regardless of pattern/replacement, but if
            // EITHER of those is itself unresolvable the ordinary combinations path below still
            // refuses correctly, since this early-return only fires on the source argument.
            if (TryTransferPlaceholderThroughFunction(functionCall.Parameters[0], FnReplace, functionCall, folded, foldingEnabled) is { } transferred)
            {
                return transferred;
            }

            return TryFoldOverArgumentCombinations(
                [functionCall.Parameters[0], functionCall.Parameters[1], functionCall.Parameters[2]], folded, foldingEnabled, functionCall,
                values =>
                {
                    var (source, pattern, replacement) = (values[0], values[1], values[2]);
                    if (pattern.Length == 0)
                    {
                        // SQL Server's own behavior for an empty search pattern is not something
                        // this scanner has verified against the oracle, and .NET's string.Replace
                        // throws outright for an empty oldValue - declines rather than guessing.
                        return FoldAttempt.Fail("non-literal-expression:replace-empty-pattern", Span(functionCall));
                    }

                    var ordinalResult = source.Replace(pattern, replacement, StringComparison.Ordinal);
                    var caseInsensitiveResult = source.Replace(pattern, replacement, StringComparison.OrdinalIgnoreCase);
                    if (!string.Equals(ordinalResult, caseInsensitiveResult, StringComparison.Ordinal))
                    {
                        return FoldAttempt.Fail("non-literal-expression:replace-collation-sensitive", Span(functionCall));
                    }

                    return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, ordinalResult)]);
                });
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

            // Unlike every other entry point into TryTransferPlaceholderThroughFunction, CAST/
            // CONVERT's target type is already pinned by the call site's own syntax - no registry
            // lookup needed or wanted; explicitTargetType overrides it directly.
            if (TryTransferPlaceholderThroughFunction(source, functionKey: string.Empty, site, folded, foldingEnabled, explicitTargetType: targetType) is { } transferred)
            {
                return transferred;
            }

            return TryFoldOverArgumentCombinations([source], folded, foldingEnabled, site, values =>
            {
                var input = values[0];
                var result = !targetType.IsMax && targetType.Length is { } length && input.Length > length
                    ? input[..length]
                    : input;
                return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, site.StartLine, site.StartColumn, PrefixLength: 0, result)]);
            });
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
                    return FoldAttempt.Fail(CardinalityCapReason, Span(expression));
                }

                union = merged;
            }

            return FoldAttempt.Ok(union);
        }

        /// <summary>
        /// Folds an integer-valued argument (e.g. LEFT/RIGHT/SUBSTRING's length or start
        /// position) - a bare literal, +/- of two such foldable integers (the
        /// <c>LEN(@x) - LEN(@y)</c> shape a "strip the trailing delimiter" idiom produces), or
        /// <c>LEN(...)</c> over a string this scanner already folded constant (a single value
        /// only - if the string itself carries more than one possible assembly from an upstream
        /// branch merge, its LENGTH is equally ambiguous, so this declines rather than picking
        /// one). Anything else (a plain variable, an unsupported function, a column reference) is
        /// declined rather than guessed - this scanner tracks only string variable values, never
        /// numeric ones.
        /// </summary>
        private bool TryFoldIntegerLiteral(ScalarExpression expression, Dictionary<string, FoldState> folded, bool foldingEnabled, out int value)
        {
            switch (expression)
            {
                case IntegerLiteral literal when int.TryParse(literal.Value, out value):
                    return true;

                case ParenthesisExpression paren:
                    return TryFoldIntegerLiteral(paren.Expression, folded, foldingEnabled, out value);

                // A negative literal (e.g. the -1 in LEFT(@x, -1)) is NOT its own literal shape -
                // ScriptDom parses the sign as a UnaryExpression wrapping an ordinary
                // IntegerLiteral (confirmed via the parsed tree, not assumed), the same way a
                // negative NumericLiteral would be. Positive's explicit '+' sign is handled the
                // same way for symmetry, even though it never changes the value.
                case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } unary
                    when TryFoldIntegerLiteral(unary.Expression, folded, foldingEnabled, out var innerValue):
                    value = -innerValue;
                    return true;

                case UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary:
                    return TryFoldIntegerLiteral(unary.Expression, folded, foldingEnabled, out value);

                case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add or BinaryExpressionType.Subtract } binary
                    when TryFoldIntegerLiteral(binary.FirstExpression, folded, foldingEnabled, out var left)
                        && TryFoldIntegerLiteral(binary.SecondExpression, folded, foldingEnabled, out var right):
                    value = binary.BinaryExpressionType == BinaryExpressionType.Add ? left + right : left - right;
                    return true;

                case FunctionCall { FunctionName.Value: var functionName } lenCall
                    when string.Equals(functionName, "LEN", StringComparison.OrdinalIgnoreCase) && lenCall.Parameters.Count == 1:
                    return TryFoldLenArgument(lenCall.Parameters[0], folded, foldingEnabled, out value);

                default:
                    value = 0;
                    return false;
            }
        }

        /// <summary>
        /// Oracle-verified: <c>LEN</c> trims TRAILING spaces before counting (unlike
        /// <c>DATALENGTH</c>, which this scanner does not fold) - <c>.NET</c>'s
        /// <see cref="string.TrimEnd(char[])"/> over the space character matches exactly. Requires
        /// the inner string to fold to exactly one possible value - see the caller's own doc
        /// comment for why an ambiguous input string makes the length equally ambiguous.
        /// </summary>
        private bool TryFoldLenArgument(ScalarExpression argument, Dictionary<string, FoldState> folded, bool foldingEnabled, out int value)
        {
            var attempt = TryFoldExpression(argument, folded, foldingEnabled);
            if (!attempt.Success || attempt.Assemblies!.Count != 1 || TryFlatten(attempt.Assemblies[0]) is not { } flattened)
            {
                // A placeholder's LEN is not a number - this scanner does not know the real
                // value, so it cannot know its length either.
                value = 0;
                return false;
            }

            value = flattened.TrimEnd(' ').Length;
            return true;
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

            // QUOTENAME's return type (nvarchar(258)) never depends on the delimiter argument, so
            // this transfers on the input alone regardless of whether a delimiter was even given.
            if (TryTransferPlaceholderThroughFunction(functionCall.Parameters[0], FnQuoteName, functionCall, folded, foldingEnabled) is { } transferred)
            {
                return transferred;
            }

            var arguments = functionCall.Parameters.Count == 2
                ? new[] { functionCall.Parameters[0], functionCall.Parameters[1] }
                : new[] { functionCall.Parameters[0] };

            return TryFoldOverArgumentCombinations(arguments, folded, foldingEnabled, functionCall, values =>
            {
                var input = values[0];
                var delimiterText = values.Count == 2 ? values[1] : null;

                var quoted = QuoteName(input, delimiterText);
                if (quoted is null)
                {
                    // Oracle-verified: QUOTENAME itself returns SQL NULL for an input over 128
                    // characters or an unrecognized delimiter - concatenating NULL propagates
                    // NULL through the whole @sql build, a materially different runtime outcome
                    // this scanner has no NULL-tracking representation for. Failing the fold
                    // (rather than silently treating it as an empty/unwrapped string) is honest.
                    return FoldAttempt.Fail("non-literal-expression:quotename-null-result", Span(functionCall));
                }

                return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, quoted)]);
            });
        }

        /// <summary>
        /// NCHAR(n)/CHAR(n) are pure, oracle-verified constant-value functions whenever their sole
        /// integer argument folds to a literal: NCHAR(n) for n in [0, 65535] yields the Unicode
        /// nchar(1) code point, CHAR(n) for n in [0, 255] yields the single-byte char(1) code
        /// point (both ranges and the NULL-outside-range behavior confirmed against the Docker
        /// oracle via DATALENGTH/SQL_VARIANT_PROPERTY - CHAR(0) is NOT null, matching neither
        /// function's documented range being open at zero). A NULL result has no LiteralSegment
        /// representation this scanner can propagate, so an out-of-range argument fails the fold
        /// rather than guessing - same policy as QUOTENAME's own null-result case above. Closes
        /// the single most common blocker in the pinned corpus: <c>NCHAR(13) + NCHAR(10)</c>
        /// building a CRLF constant that then taints everything concatenated with it.
        /// </summary>
        private FoldAttempt TryFoldCharOrNChar(FunctionCall functionCall, string functionName, Dictionary<string, FoldState> folded, bool foldingEnabled)
        {
            var isNational = string.Equals(functionName, FnNChar, StringComparison.OrdinalIgnoreCase);
            if (functionCall.Parameters.Count != 1
                || !TryFoldIntegerLiteral(functionCall.Parameters[0], folded, foldingEnabled, out var codePoint))
            {
                return FailNonLiteralExpression(functionCall);
            }

            var maxCodePoint = isNational ? 65535 : 255;
            if (codePoint is < 0 || codePoint > maxCodePoint)
            {
                return FoldAttempt.Fail("non-literal-expression:char-out-of-range", Span(functionCall));
            }

            var value = ((char)codePoint).ToString();
            return FoldAttempt.OkSingle([new LiteralSegment(sourcePath, functionCall.StartLine, functionCall.StartColumn, PrefixLength: 0, value)]);
        }

        /// <summary>
        /// Folds one string-builder function argument to its set of possible concrete string
        /// values - every assembly must itself flatten to a real value (<see cref="TryFlatten"/>),
        /// since a placeholder has no real value this pure function could actually be applied to
        /// (CAST/SUBSTRING/REPLACE etc. can change the type, truncate, or destroy the token
        /// outright, and none of this scanner's accepted EXEC positions ever need a placeholder's
        /// TYPE to survive a function call) - the whole argument fails with its own reason the
        /// moment any one assembly is a placeholder, mirroring <see
        /// cref="TryFoldOverArgumentCombinations"/>'s own "decline the whole fold, not just one
        /// possibility" policy one level up.
        /// </summary>
        private bool TryFlattenArgumentValues(
            ScalarExpression argument, Dictionary<string, FoldState> folded, bool foldingEnabled, out List<string> values, out FoldAttempt failure)
        {
            var attempt = TryFoldExpression(argument, folded, foldingEnabled);
            if (!attempt.Success)
            {
                values = [];
                failure = attempt;
                return false;
            }

            var result = new List<string>(attempt.Assemblies!.Count);
            foreach (var assembly in attempt.Assemblies)
            {
                // A placeholder argument has no real value this pure function could actually be
                // applied to - CAST/SUBSTRING/REPLACE etc. can change the type, truncate, or
                // destroy the token outright, and none of this scanner's accepted EXEC positions
                // ever need the placeholder's TYPE to survive a function call, so there is
                // nothing to gain by trying to fold one through.
                if (TryFlatten(assembly) is not { } flattened)
                {
                    values = [];
                    failure = FoldAttempt.Fail("symbolic-value-in-function-argument", Span(argument));
                    return false;
                }

                result.Add(flattened);
            }

            values = result;
            failure = default;
            return true;
        }

        /// <summary>
        /// Folds every string-builder function's argument(s) and cross-products across whichever
        /// ones carry more than one possible assembly (a variable set by divergent IF branches
        /// upstream - the shape a WHERE-clause accumulator's REPLACE/CAST/QUOTENAME step sits
        /// downstream of), calling <paramref name="perCombination"/> once per combination of
        /// concrete argument values and unioning the results - the same technique <see
        /// cref="TryCartesianConcat"/> already uses for concatenation, generalized to an arbitrary
        /// per-function transform. A single argument is just a cross product of size one, so every
        /// caller goes through this uniformly rather than a separate single-value fast path.
        ///
        /// Declines the WHOLE fold, not just one combination, the moment <paramref
        /// name="perCombination"/> fails for any single combination: an assembly SET means "one of
        /// these is what really happens at runtime" - if one combination would itself hit a
        /// different runtime outcome (a negative LEFT/RIGHT length, a QUOTENAME NULL result, a
        /// REPLACE collation divergence), this scanner cannot represent "sometimes these N
        /// strings, sometimes something else" in its model, so honesty requires declining
        /// entirely rather than silently dropping that one possibility from the set.
        /// </summary>
        private FoldAttempt TryFoldOverArgumentCombinations(
            ScalarExpression[] stringArguments,
            Dictionary<string, FoldState> folded,
            bool foldingEnabled,
            TSqlFragment site,
            Func<IReadOnlyList<string>, FoldAttempt> perCombination)
        {
            var argumentValueSets = new List<IReadOnlyList<string>>(stringArguments.Length);
            foreach (var argument in stringArguments)
            {
                if (!TryFlattenArgumentValues(argument, folded, foldingEnabled, out var values, out var failure))
                {
                    return failure;
                }

                argumentValueSets.Add(values);
            }

            IEnumerable<IReadOnlyList<string>> combinations = [Array.Empty<string>()];
            foreach (var valueSet in argumentValueSets)
            {
                combinations = combinations.SelectMany(prefix => valueSet.Select(value => (IReadOnlyList<string>)[.. prefix, value]));
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var results = new List<IReadOnlyList<LiteralSegment>>();
            foreach (var combination in combinations)
            {
                var attempt = perCombination(combination);
                if (!attempt.Success)
                {
                    return attempt;
                }

                // perCombination only ever builds a new LiteralSegment from the already-flattened
                // REAL strings in argumentValueSets above - it can never introduce a placeholder,
                // so this is always non-null.
                var value = TryFlatten(attempt.Assemblies![0])!;
                if (!seen.Add(value))
                {
                    continue;
                }

                if (results.Count == MaxAssembliesPerVariable)
                {
                    return FoldAttempt.Fail(CardinalityCapReason, Span(site));
                }

                results.Add([new LiteralSegment(sourcePath, site.StartLine, site.StartColumn, PrefixLength: 0, value)]);
            }

            return FoldAttempt.Ok(results);
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
        /// <summary>
        /// Taints only the variables a statement of an otherwise-unmodeled kind could actually
        /// have WRITTEN, per <see cref="WrittenVariableCollector"/>'s closed set of T-SQL write
        /// mechanisms - unlike <see cref="TaintReferencedVariables"/>, which taints every mention
        /// (read or write alike) and is reserved for constructs (EXEC of an uncataloged
        /// procedure) where this scanner genuinely cannot bound what got mutated. A statement
        /// this collector finds nothing in (the common case: PRINT, RAISERROR, INSERT/UPDATE/
        /// DELETE/MERGE with no quirky-update variable target, WAITFOR, THROW, DBCC, ...) leaves
        /// <paramref name="folded"/> byte-for-bit unchanged.
        /// </summary>
        private void TaintWrittenVariables(Dictionary<string, FoldState> folded, TSqlFragment fragment, string reason)
        {
            var location = Span(fragment);
            var collector = new WrittenVariableCollector();
            fragment.Accept(collector);
            foreach (var name in collector.Names.Where(folded.ContainsKey))
            {
                folded[name] = FoldState.Tainted(reason, location);
            }
        }

        private void TaintReferencedVariables(Dictionary<string, FoldState> folded, TSqlFragment fragment, string reason, HashSet<string>? exclude = null)
        {
            var location = Span(fragment);
            var collector = new ReferencedVariableCollector();
            fragment.Accept(collector);
            foreach (var name in collector.Names)
            {
                if (exclude is not null && exclude.Contains(name))
                {
                    continue;
                }

                if (folded.ContainsKey(name))
                {
                    folded[name] = FoldState.Tainted(reason, location);
                }
            }
        }

        private static IList<TSqlStatement> NormalizeToStatementList(TSqlStatement statement) =>
            statement is BeginEndBlockStatement block ? block.StatementList.Statements : [statement];

        /// <summary>
        /// Whether GOTO/LABEL appears anywhere WITHIN THIS SAME SCOPE - deliberately NOT a plain
        /// <see cref="GotoLabelDetector"/> sweep over the whole fragment subtree, which would
        /// also find one buried inside a NESTED procedure/function/trigger body sharing this
        /// batch. That nested body is a wholly separate scope with its own upcoming <see
        /// cref="WalkScope"/> call (and its own, independent goto/label determination) -
        /// wrongly triggering control-flow-graph mode for the OUTER scope over an inner scope's
        /// label doesn't just cost precision, it makes the outer scope's <c>ProcedureStatementBodyBase</c>
        /// leaf step re-invoke a full NESTED <see cref="ControlFlowGraph.Solve"/> from inside
        /// this scope's own fixpoint search, corrupting the shared <see cref="_suppressEmission"/>
        /// flag between the two (reproduced directly: a single proc containing a label emitted
        /// its own EXEC's finding three times over). Recurses into BEGIN/END, IF/WHILE/TRY-CATCH -
        /// the constructs that share THIS scope - and stops at a nested scope boundary, mirroring
        /// exactly the traversal <see cref="ControlFlowGraph.PreRegisterLabels"/>/
        /// <see cref="ControlFlowGraph.BuildSequence"/> already use for the same reason.
        /// </summary>
        private static bool ContainsGotoOrLabel(IList<TSqlStatement> statements)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case GoToStatement or LabelStatement:
                        return true;

                    case BeginEndBlockStatement block when ContainsGotoOrLabel(block.StatementList.Statements):
                        return true;

                    case IfStatement ifStatement
                        when ContainsGotoOrLabel(NormalizeToStatementList(ifStatement.ThenStatement))
                            || (ifStatement.ElseStatement is not null && ContainsGotoOrLabel(NormalizeToStatementList(ifStatement.ElseStatement))):
                        return true;

                    case WhileStatement whileStatement when ContainsGotoOrLabel(NormalizeToStatementList(whileStatement.Statement)):
                        return true;

                    case TryCatchStatement tryCatch
                        when ContainsGotoOrLabel(tryCatch.TryStatements.Statements) || ContainsGotoOrLabel(tryCatch.CatchStatements.Statements):
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

        /// <summary>
        /// Every variable name mentioned anywhere in a fragment, read or written alike - the
        /// sound upper bound <see cref="TaintReferencedVariables"/> taints against for a
        /// statement whose own semantics this scanner doesn't model at all: a plain mention
        /// (e.g. inside a WHERE clause or PRINT) is not provably a write, but it is the only
        /// variables that possibly could be one, since T-SQL locals cannot alias.
        /// </summary>
        private sealed class ReferencedVariableCollector : TSqlFragmentVisitor
        {
            public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(VariableReference node) => Names.Add(node.Name);
        }

        /// <summary>
        /// The closed set of ways a T-SQL statement kind this scanner doesn't otherwise model
        /// can WRITE a scalar local, used by <see cref="TaintWrittenVariables"/>:
        /// <see cref="AssignmentSetClause.Variable"/> (the legacy "quirky update" - <c>UPDATE t
        /// SET @v = col, col = expr</c> - reachable from both an ordinary UPDATE's own SET list
        /// and a MERGE action's UPDATE SET list, since ScriptDom's grammar models both with the
        /// same node type), <see cref="FetchCursorStatement.IntoVariables"/> (<c>FETCH ... INTO
        /// @a, @b</c>), and <see cref="SelectSetVariable"/> as it appears inside a
        /// <see cref="ReceiveStatement"/>'s own SELECT-list targets (<c>RECEIVE @v = column ...
        /// FROM queue</c>) - a plain column reference on either side of any of these is a read,
        /// never collected.
        /// </summary>
        private sealed class WrittenVariableCollector : TSqlFragmentVisitor
        {
            public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(AssignmentSetClause node)
            {
                if (node.Variable is not null)
                {
                    Names.Add(node.Variable.Name);
                }
            }

            public override void Visit(FetchCursorStatement node)
            {
                if (node.IntoVariables is null)
                {
                    return;
                }

                foreach (var variable in node.IntoVariables)
                {
                    Names.Add(variable.Name);
                }
            }

            public override void Visit(SelectSetVariable node) => Names.Add(node.Variable.Name);
        }

        /// <summary>
        /// Replaces "a GOTO/label anywhere disables folding for the whole scope" with a real
        /// basic-block control-flow graph, solved by fixpoint - GOTO/labels become ordinary
        /// edges instead of a bail-out. <see cref="Visitor.HandleWhile"/>'s own loop-body
        /// fixpoint is a separate, always-on improvement (a WHILE needs no nested GOTO/LABEL to
        /// benefit from it) - here, a WHILE with no nested goto/label of its OWN is simply one
        /// opaque step in the graph that calls it exactly as it would outside control-flow-graph
        /// mode entirely.
        ///
        /// IF/WHILE/TRY-CATCH are decomposed into their own blocks ONLY when their own subtree
        /// contains a nested GOTO or LABEL (<see cref="ContainsGotoOrLabelInSubtree"/>) - the
        /// (overwhelming majority) that don't are left as a single opaque step reusing <see
        /// cref="Visitor.HandleIf"/>/<see cref="Visitor.HandleWhile"/>/
        /// <see cref="Visitor.HandleTryCatch"/> completely unchanged, so this pays its own
        /// traversal cost only on the actual path to or from a label, never on ordinary
        /// branching that happens to share a procedure body with one.
        ///
        /// The fold lattice (a variable's assembly SET, taint as the absorbing bottom) is
        /// monotonic and bounded (union only grows a set, capped at
        /// <see cref="MaxAssembliesPerVariable"/>, past which it taints) - the fixpoint below is
        /// therefore GUARANTEED to converge, <see cref="MaxRounds"/> is a generous bound on a
        /// pathological graph size, not a heuristic cutoff that could leave the answer wrong.
        /// </summary>
        private sealed class ControlFlowGraph(Visitor visitor)
        {
            private const int MaxRounds = 50;

            private sealed class Block
            {
                public List<Action<Dictionary<string, FoldState>>> Steps { get; } = [];

                public List<int> Successors { get; } = [];
            }

            private readonly List<Block> _blocks = [];
            private readonly Dictionary<string, int> _labelBlocks = new(StringComparer.OrdinalIgnoreCase);

            private int NewBlock()
            {
                _blocks.Add(new Block());
                return _blocks.Count - 1;
            }

            private static string NormalizeLabel(string labelValue) => labelValue.TrimEnd(':');

            private static bool ContainsGotoOrLabelInSubtree(TSqlFragment fragment)
            {
                var detector = new GotoLabelDetector();
                fragment.Accept(detector);
                return detector.Found;
            }

            public Dictionary<string, FoldState> Solve(IList<TSqlStatement> statements, Dictionary<string, FoldState> initialSeed)
            {
                PreRegisterLabels(statements);
                var entryBlock = NewBlock();
                var exitBlocks = new List<int>();
                // BuildSequence already appends its own final block to exitBlocks whenever it
                // completes reachably - the return value only matters to a RECURSIVE caller
                // (BuildIf/BuildWhile/BuildTryCatch chaining into what follows); the top-level
                // scope has no such caller, so the value itself is unused here.
                BuildSequence(statements, entryBlock, exitBlocks, new Stack<(int Header, int After)>());

                var predecessors = BuildPredecessors();
                var outStates = new Dictionary<string, FoldState>?[_blocks.Count];

                RunFixpoint(entryBlock, initialSeed, predecessors, outStates);
                RunFinalEmissionPass(entryBlock, initialSeed, predecessors, outStates);

                // The scope's own "final" state (what WalkScopedBody reads an OUTPUT parameter
                // back from) is the merge of every block execution can actually fall off the end
                // at - RETURN and an unconditional GOTO each already end their block with no
                // fallthrough successor, so they contribute nothing here, matching that a
                // procedure exiting through either one never reaches its own implicit end.
                return MergeExitStates(exitBlocks, outStates);
            }

            private List<int>[] BuildPredecessors()
            {
                var predecessors = new List<int>[_blocks.Count];
                for (var i = 0; i < _blocks.Count; i++)
                {
                    predecessors[i] = [];
                }

                for (var i = 0; i < _blocks.Count; i++)
                {
                    foreach (var successor in _blocks[i].Successors)
                    {
                        predecessors[successor].Add(i);
                    }
                }

                return predecessors;
            }

            /// <summary>Merges block <paramref name="index"/>'s entry state and runs its steps - the one computation shared by the fixpoint loop and the final emission-enabled pass. Null means the block has no known entry state yet (an unreached predecessor this round).</summary>
            private Dictionary<string, FoldState>? ComputeBlockOutput(
                int index, int entryBlock, Dictionary<string, FoldState> initialSeed, List<int>[] predecessors, Dictionary<string, FoldState>?[] outStates)
            {
                var merged = MergeEntry(index, entryBlock, initialSeed, predecessors, outStates);
                if (merged is null)
                {
                    return null;
                }

                var working = new Dictionary<string, FoldState>(merged, StringComparer.OrdinalIgnoreCase);
                foreach (var step in _blocks[index].Steps)
                {
                    step(working);
                }

                return working;
            }

            private void RunFixpoint(int entryBlock, Dictionary<string, FoldState> initialSeed, List<int>[] predecessors, Dictionary<string, FoldState>?[] outStates)
            {
                visitor._suppressEmission = true;
                for (var round = 0; round < MaxRounds; round++)
                {
                    var changed = false;
                    for (var i = 0; i < _blocks.Count; i++)
                    {
                        var working = ComputeBlockOutput(i, entryBlock, initialSeed, predecessors, outStates);
                        if (working is null)
                        {
                            continue;
                        }

                        if (outStates[i] is null || !StatesEqual(outStates[i]!, working))
                        {
                            changed = true;
                        }

                        outStates[i] = working;
                    }

                    if (!changed && round > 0)
                    {
                        break;
                    }
                }
            }

            /// <summary>
            /// States are stable now - re-run once more with emission enabled. Same inputs,
            /// same steps, so this reproduces the exact same outputs; the only difference is
            /// that EXEC/output-summary side effects are no longer suppressed.
            /// </summary>
            private void RunFinalEmissionPass(int entryBlock, Dictionary<string, FoldState> initialSeed, List<int>[] predecessors, Dictionary<string, FoldState>?[] outStates)
            {
                visitor._suppressEmission = false;
                for (var i = 0; i < _blocks.Count; i++)
                {
                    var working = ComputeBlockOutput(i, entryBlock, initialSeed, predecessors, outStates);
                    if (working is null)
                    {
                        continue;
                    }

                    outStates[i] = working;
                }
            }

            private static Dictionary<string, FoldState> MergeExitStates(List<int> exitBlocks, Dictionary<string, FoldState>?[] outStates)
            {
                Dictionary<string, FoldState>? finalState = null;
                foreach (var exitBlock in exitBlocks)
                {
                    if (outStates[exitBlock] is not { } exitState)
                    {
                        continue;
                    }

                    finalState = finalState is null
                        ? new Dictionary<string, FoldState>(exitState, StringComparer.OrdinalIgnoreCase)
                        : MergeInto(finalState, exitState, "diverges-in-control-flow-graph");
                }

                return finalState ?? new Dictionary<string, FoldState>(StringComparer.OrdinalIgnoreCase);
            }

            private static Dictionary<string, FoldState>? MergeEntry(
                int block, int entryBlock, Dictionary<string, FoldState> initialSeed, List<int>[] predecessors, Dictionary<string, FoldState>?[] outStates)
            {
                Dictionary<string, FoldState>? merged = block == entryBlock
                    ? new Dictionary<string, FoldState>(initialSeed, StringComparer.OrdinalIgnoreCase)
                    : null;

                foreach (var predecessor in predecessors[block])
                {
                    if (outStates[predecessor] is not { } predecessorState)
                    {
                        continue;
                    }

                    merged = merged is null
                        ? new Dictionary<string, FoldState>(predecessorState, StringComparer.OrdinalIgnoreCase)
                        : MergeInto(merged, predecessorState, "diverges-in-control-flow-graph");
                }

                return merged;
            }

            private void PreRegisterLabels(IList<TSqlStatement> statements)
            {
                foreach (var statement in statements)
                {
                    switch (statement)
                    {
                        case LabelStatement label:
                            _labelBlocks[NormalizeLabel(label.Value)] = NewBlock();
                            break;

                        case BeginEndBlockStatement block:
                            PreRegisterLabels(block.StatementList.Statements);
                            break;

                        case IfStatement ifStatement:
                            PreRegisterLabels(NormalizeToStatementList(ifStatement.ThenStatement));
                            if (ifStatement.ElseStatement is not null)
                            {
                                PreRegisterLabels(NormalizeToStatementList(ifStatement.ElseStatement));
                            }

                            break;

                        case WhileStatement whileStatement:
                            PreRegisterLabels(NormalizeToStatementList(whileStatement.Statement));
                            break;

                        case TryCatchStatement tryCatch:
                            PreRegisterLabels(tryCatch.TryStatements.Statements);
                            PreRegisterLabels(tryCatch.CatchStatements.Statements);
                            break;
                    }
                }
            }

            /// <summary>
            /// Links <paramref name="statements"/> into the block graph starting at <paramref
            /// name="current"/>, returning the block execution falls through to afterward, or
            /// null when this sequence can never fall through (ends in RETURN, an unconditional
            /// GOTO, or every branch of its last construct exits some other way). Every reached
            /// dead end (a RETURN, or simply running out of statements) is appended to <paramref
            /// name="exitBlocks"/> - <see cref="Solve"/> merges all of them for the scope's own
            /// final state.
            /// </summary>
            private int? BuildSequence(IList<TSqlStatement> statements, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack)
            {
                // `reachable` tracks whether `current` is actually reachable via fallthrough from
                // the PREVIOUS statement - false right after a GOTO/RETURN/BREAK/CONTINUE (or a
                // nested construct that never falls through). Statements can still follow one in
                // well-formed T-SQL (most commonly a LABEL some other jump targets), so this must
                // keep scanning rather than stop outright - an early return here would leave
                // every statement after it, including the very label an earlier GOTO jumps to,
                // never linked into the graph at all (reproduced directly: an unconditional GOTO
                // straight to a label immediately followed by the EXEC left that EXEC's own
                // block permanently unreached, silently reporting no finding and no script at
                // all instead of the fold it should have produced).
                var reachable = true;
                foreach (var statement in statements)
                {
                    if (!reachable)
                    {
                        // Dead code unless a label here makes it reachable via GOTO instead -
                        // either way, resume building into a fresh, currently-unlinked block so a
                        // label buried inside still gets its own contents populated correctly,
                        // without spuriously wiring it as a successor of whatever came before.
                        current = NewBlock();
                        reachable = true;
                    }

                    switch (statement)
                    {
                        case LabelStatement label:
                            var labelBlock = _labelBlocks[NormalizeLabel(label.Value)];
                            _blocks[current].Successors.Add(labelBlock);
                            current = labelBlock;
                            break;

                        case GoToStatement goTo:
                            _blocks[current].Successors.Add(_labelBlocks[goTo.LabelName.Value]);
                            reachable = false;
                            break;

                        case ReturnStatement:
                            exitBlocks.Add(current);
                            reachable = false;
                            break;

                        case BeginEndBlockStatement block:
                            var afterBlock = BuildSequence(block.StatementList.Statements, current, exitBlocks, loopStack);
                            if (afterBlock is null)
                            {
                                reachable = false;
                            }
                            else
                            {
                                current = afterBlock.Value;
                            }

                            break;

                        case IfStatement ifStatement when ContainsGotoOrLabelInSubtree(ifStatement):
                            current = BuildIf(ifStatement, current, exitBlocks, loopStack);
                            break;

                        case WhileStatement whileStatement when ContainsGotoOrLabelInSubtree(whileStatement):
                            current = BuildWhile(whileStatement, current, exitBlocks, loopStack);
                            break;

                        case TryCatchStatement tryCatch when ContainsGotoOrLabelInSubtree(tryCatch):
                            current = BuildTryCatch(tryCatch, current, exitBlocks, loopStack);
                            break;

                        // The (overwhelming majority) IF/WHILE/TRY-CATCH whose own subtree has
                        // no nested GOTO/LABEL - a single opaque step reusing the existing,
                        // already-correct recursive handler unchanged.
                        case IfStatement ifOpaque:
                            _blocks[current].Steps.Add(folded => visitor.HandleIf(ifOpaque, folded, foldingEnabled: true));
                            break;

                        case WhileStatement whileOpaque:
                            _blocks[current].Steps.Add(folded => visitor.HandleWhile(whileOpaque, folded, foldingEnabled: true));
                            break;

                        case TryCatchStatement tryCatchOpaque:
                            _blocks[current].Steps.Add(folded => visitor.HandleTryCatch(tryCatchOpaque, folded, foldingEnabled: true));
                            break;

                        case BreakStatement when loopStack.Count > 0:
                            _blocks[current].Successors.Add(loopStack.Peek().After);
                            reachable = false;
                            break;

                        case ContinueStatement when loopStack.Count > 0:
                            _blocks[current].Successors.Add(loopStack.Peek().Header);
                            reachable = false;
                            break;

                        default:
                            var captured = statement;
                            _blocks[current].Steps.Add(folded => visitor.WalkStatement(captured, folded, foldingEnabled: true));
                            break;
                    }
                }

                if (!reachable)
                {
                    return null;
                }

                exitBlocks.Add(current);
                return current;
            }

            private int BuildIf(IfStatement ifStatement, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack)
            {
                var thenEntry = NewBlock();
                _blocks[current].Successors.Add(thenEntry);
                var thenExit = BuildSequence(NormalizeToStatementList(ifStatement.ThenStatement), thenEntry, exitBlocks, loopStack);

                int? elseExit;
                if (ifStatement.ElseStatement is not null)
                {
                    var elseEntry = NewBlock();
                    _blocks[current].Successors.Add(elseEntry);
                    elseExit = BuildSequence(NormalizeToStatementList(ifStatement.ElseStatement), elseEntry, exitBlocks, loopStack);
                }
                else
                {
                    // No ELSE: the condition being false falls straight through from the IF's
                    // own block, exactly like HandleIf's untouched-else-clone does today.
                    elseExit = current;
                }

                var join = NewBlock();
                if (thenExit is { } te)
                {
                    _blocks[te].Successors.Add(join);
                }

                if (elseExit is { } ee)
                {
                    _blocks[ee].Successors.Add(join);
                }

                return join;
            }

            private int BuildWhile(WhileStatement whileStatement, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack)
            {
                var header = NewBlock();
                _blocks[current].Successors.Add(header);

                var bodyEntry = NewBlock();
                var after = NewBlock();
                _blocks[header].Successors.Add(bodyEntry);
                _blocks[header].Successors.Add(after);

                loopStack.Push((header, after));
                var bodyExit = BuildSequence(NormalizeToStatementList(whileStatement.Statement), bodyEntry, exitBlocks, loopStack);
                loopStack.Pop();

                if (bodyExit is { } be)
                {
                    _blocks[be].Successors.Add(header);
                }

                return after;
            }

            private int BuildTryCatch(TryCatchStatement tryCatch, int current, List<int> exitBlocks, Stack<(int Header, int After)> loopStack)
            {
                var tryEntry = NewBlock();
                var catchEntry = NewBlock();
                _blocks[current].Successors.Add(tryEntry);

                // CATCH only runs if TRY throws mid-way, so how far TRY got is unknowable - an
                // edge straight from the PRE-TRY block, never from any point inside TRY itself,
                // matching HandleTryCatch's own "CATCH starts from the pre-TRY state" today.
                _blocks[current].Successors.Add(catchEntry);

                var tryExit = BuildSequence(tryCatch.TryStatements.Statements, tryEntry, exitBlocks, loopStack);
                var catchExit = BuildSequence(tryCatch.CatchStatements.Statements, catchEntry, exitBlocks, loopStack);

                var join = NewBlock();
                if (tryExit is { } tex)
                {
                    _blocks[tex].Successors.Add(join);
                }

                if (catchExit is { } cex)
                {
                    _blocks[cex].Successors.Add(join);
                }

                return join;
            }
        }
    }
}

/// <summary>Everything <see cref="DynamicSqlScanner.Scan"/> found in one parsed file: definite unanalyzable findings, and candidate scripts ready for <see cref="DynamicSqlPipeline"/> to reparse.</summary>
public sealed record DynamicSqlExtractionResult(
    IReadOnlyList<DynamicSqlFinding> Findings,
    IReadOnlyList<DynamicSqlScript> AnalyzableScripts,
    IReadOnlyList<ProcedureOutputSummary> OutputSummaries);
