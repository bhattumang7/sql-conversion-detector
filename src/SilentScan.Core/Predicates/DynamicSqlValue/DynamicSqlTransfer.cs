using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>
/// Everything a leaf compiler needs besides the statement itself and the running state - bundled
/// so per-statement compile methods stay under the parameter-count gate rather than threading
/// several loose values through every call. <paramref name="Findings"/>/<paramref name="Scripts"/>
/// are the EXEC/sp_executesql call sites' own output - mutated only when a step runs with
/// <c>emit: true</c> (the CFG's dedicated final pass, once state has stabilized), never during
/// the suppressed fixpoint rounds, so a call site is recorded exactly once regardless of how many
/// rounds the fixpoint needed to converge.
/// </summary>
public sealed record TransferContext(
    Dictionary<string, SqlType> DeclaredTypes,
    string SourcePath,
    int Cap,
    DynamicSqlScope Scope,
    List<DynamicSqlFinding> Findings,
    List<DynamicSqlScript> Scripts,
    List<ProcedureOutputSummary> OutputSummaries,
    ProcCallGraph? CallGraph = null,
    IReadOnlyDictionary<(string ProcedureQualifiedName, string ParameterName), IReadOnlyList<string>>? OutputSummaryIndex = null,
    DatabaseCatalog? Catalog = null,
    ILiveRowValueFetcher? RowValueFetcher = null)
{
    public SourceSpan Span(TSqlFragment fragment) => new(SourcePath, fragment.StartLine, fragment.StartColumn);
}

/// <summary>
/// Compiles one non-control-flow <see cref="TSqlStatement"/> into a <see cref="DynamicSqlCfg"/>
/// leaf step. DECLARE and SET/SELECT-assignment are modeled precisely; every OTHER statement
/// kind falls through to <see cref="CompileHavocDefault"/> - the safe-by-construction default the
/// whole rebuild is built around (docs/dynamic-sql-rebuild-plan.md §4): a statement this class
/// does not (yet) understand degrades whatever it could have written to a typed
/// <see cref="HoleKind.HavocWrite"/> hole (or <see cref="SqlTextValue.Tainted"/> when no declared
/// type is known), rather than either crashing or silently leaving a stale value in place. Adding
/// precision for a new statement kind is an ADDITIVE case here, never a prerequisite for
/// soundness - CLAUDE.md's "leaving an edge case unimplemented is fine; faking it is not".
/// </summary>
public static class DynamicSqlTransfer
{
    /// <summary>
    /// Compiles one statement into a <see cref="DynamicSqlCfg"/> leaf step. <paramref name="context"/>.DeclaredTypes
    /// tracks a variable's own declared type separately from its current fold state, so a later
    /// havoc/taint can still recover it. <paramref name="activeGuards"/> is the STATIC list of
    /// enclosing IF predicate texts this statement is reached under (the THEN side only - see
    /// <see cref="DynamicSqlCfg.BuildIf"/>), forwarded only to <see cref="CompileExecute"/>, the
    /// one leaf kind that needs to know whether IT ITSELF is guarded when deciding how far to
    /// narrow a <see cref="SqlTextValue.GuardedAlternatives"/>-bearing value (see
    /// <see cref="EmitScriptsOrFinding"/>'s own doc comment).
    /// </summary>
    public static Action<Dictionary<string, SqlTextValue>, bool> CompileLeaf(TSqlStatement statement, IReadOnlyList<string> activeGuards, TransferContext context) => statement switch
    {
        DeclareVariableStatement declare => (state, _) => CompileDeclare(declare, context, state),
        SetVariableStatement set => (state, _) => CompileAssignment(set.Variable.Name, set.AssignmentKind, set.Expression, set.FunctionCallExists, set, context, state),
        SelectStatement select => (state, _) => CompileSelectAssignment(select, context, state),
        ExecuteStatement exec => (state, emit) => CompileExecute(exec, activeGuards, context, state, emit),
        ProcedureStatementBodyBase { StatementList: not null } procOrFunc => (_, emit) => CompileScopedBody(procOrFunc, context, emit),
        ProcedureStatementBodyBase => static (_, _) => { }, // a body-less declaration (CLR proc/function via EXTERNAL NAME, or an inline TVF whose body is a single RETURN expression) - nothing to walk
        TriggerStatementBody { StatementList: not null } trigger => (_, emit) => CompileTriggerBody(trigger, context, emit),
        TriggerStatementBody => static (_, _) => { },
        _ => CompileHavocDefault(statement, context),
    };

    /// <summary>
    /// A nested CREATE/ALTER PROCEDURE or FUNCTION body found INSIDE the scope currently being
    /// walked (matching on the shared <see cref="ProcedureStatementBodyBase"/> base, not the
    /// concrete CREATE-only statement type, catches the real-world "stub CREATE PROCEDURE ... AS
    /// RETURN 0, then ALTER PROCEDURE for the real body" pattern) - a fresh variable scope with
    /// its own qualified name recorded as the enclosing scope for any dynamic SQL call site found
    /// inside, mirroring <see cref="Catalog.CatalogBuilder"/>'s identical save/restore. Runs only
    /// in the CFG's dedicated final pass: it recurses into a WHOLE NESTED
    /// <see cref="DynamicSqlCfg.Solve"/>, which handles its own suppression internally, so running
    /// it during the OUTER scope's suppressed fixpoint rounds would do the nested scope's own
    /// work needlessly (and could double-report its findings/scripts once real emission runs).
    /// Formal-parameter seeding from a caller's own call-graph edge (<see cref="BuildParameterSeed"/>,
    /// mirroring the old scanner's own method of the same name) runs below whenever
    /// <see cref="TransferContext.CallGraph"/> is supplied - a parameter reference reports
    /// "variable-not-in-scope" only when no call graph was supplied at all.
    /// </summary>
    private static void CompileScopedBody(ProcedureStatementBodyBase procOrFunc, TransferContext context, bool emit)
    {
        if (!emit)
        {
            return;
        }

        var name = ProcedureOrFunctionName(procOrFunc);
        var qualifiedName = name is null ? null : SchemaObjectNameHelper.Qualify(name);
        var formalParameters = ProcedureOrFunctionParameters(procOrFunc);
        var nestedScope = qualifiedName is null ? context.Scope : new DynamicSqlScope(qualifiedName, context.Scope.TriggerTarget);
        var nestedContext = context with { Scope = nestedScope, DeclaredTypes = new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase) };

        var seed = qualifiedName is not null && formalParameters is { Count: > 0 }
            ? BuildParameterSeed(qualifiedName, formalParameters, nestedContext)
            : new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase);
        SeedBatchDeclaredVariables(procOrFunc.StatementList!.Statements, nestedContext, seed);

        var cfg = new DynamicSqlCfg(context.SourcePath, context.Cap, (s, activeGuards) => CompileLeaf(s, activeGuards, nestedContext));
        var folded = cfg.Solve(procOrFunc.StatementList!.Statements, seed);

        if (qualifiedName is not null && formalParameters is { Count: > 0 })
        {
            RecordOutputParameterSummaries(qualifiedName, formalParameters, folded, nestedContext);
        }
    }

    /// <summary>
    /// Seeds a proc body's own formal parameters as constant-foldable when the call graph saw
    /// exactly one caller passing a string literal for that parameter - see
    /// <see cref="ProcCallGraph.SingleCallSiteFor"/> for why "exactly one call site THIS SCAN
    /// saw" is the only case a single value can be trusted at all. No <see cref="TransferContext.CallGraph"/>
    /// at all (the common case in isolated/unit-tested scans) seeds nothing - every parameter
    /// simply reports "variable-not-in-scope" if referenced, same as today. A parameter with
    /// ZERO call sites this scan saw is still seeded as a typed hole (its declared type is a
    /// real T-SQL guarantee regardless of who calls it) rather than left fully untracked - the
    /// old scanner's <c>SeedSymbolicOrTaint</c>. Every branch here mirrors the old scanner's
    /// <c>BuildParameterSeed</c>/<c>SeedFromSingleEdge</c>/<c>SeedFromMultipleEdges</c> exactly.
    /// </summary>
    private static Dictionary<string, SqlTextValue> BuildParameterSeed(string qualifiedName, IList<ProcedureParameter> formalParameters, TransferContext context)
    {
        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase);
        if (context.CallGraph is null)
        {
            return seed;
        }

        var edges = context.CallGraph.EdgesCalling(qualifiedName).ToList();
        if (edges.Count == 0)
        {
            foreach (var formal in formalParameters)
            {
                seed[formal.VariableName.Value] = SeedSymbolicOrTaint(formal, "procedure-parameter:no-known-call-site", context);
            }

            return seed;
        }

        if (edges.Count == 1)
        {
            SeedFromSingleEdge(edges[0], formalParameters, seed, context);
            return seed;
        }

        foreach (var formal in formalParameters)
        {
            seed[formal.VariableName.Value] = SeedFromMultipleEdges(edges, formal, context);
        }

        return seed;
    }

    private static SqlTextValue SeedSymbolicOrTaint(ProcedureParameter formal, string taintReasonIfUnresolvable, TransferContext context)
    {
        var location = context.Span(formal);
        var type = SqlTypeReferenceResolver.Resolve(formal.DataType, columnCollation: null);
        return type is null
            ? new SqlTextValue.Tainted(taintReasonIfUnresolvable, location)
            : new SqlTextValue.Template([new TemplatePiece.Hole(type, location, HoleKind.UntypedParameter)]) { DeclaredType = type };
    }

    private static void SeedFromSingleEdge(ProcCallEdge edge, IList<ProcedureParameter> formalParameters, Dictionary<string, SqlTextValue> seed, TransferContext context)
    {
        foreach (var formal in formalParameters)
        {
            var paramName = formal.VariableName.Value;
            var argument = edge.Arguments.FirstOrDefault(a => string.Equals(a.FormalParameterName, paramName, StringComparison.OrdinalIgnoreCase));
            if (argument is null)
            {
                // No matching actual argument - the formal's own DEFAULT value applies (T-SQL
                // requires a parameter default to be a constant expression, never referencing
                // another parameter, so folding it against the state built so far is exact, not
                // an approximation). Previously left the key OUT of `seed` entirely ("nothing to
                // seed") - which reads as "never declared" to every later reference, a real bug
                // (surfaced as spurious "variable-not-in-scope" findings for a genuinely-declared,
                // just not literally-passed, parameter) - every other unresolved-but-real
                // parameter in this file always seeds SOMETHING (a typed Hole or Tainted), never
                // leaves the key absent, and a defaulted parameter deserves the same treatment.
                var declaredType = SqlTypeReferenceResolver.Resolve(formal.DataType, columnCollation: null);
                seed[paramName] = formal.Value is { } defaultExpression
                    ? ExpressionEvaluator.Fold(defaultExpression, seed, context.SourcePath, context.Cap, context.Catalog) with { DeclaredType = declaredType }
                    : SeedSymbolicOrTaint(formal, "parameter-not-seeded:default-value-applies", context);
                continue;
            }

            if (argument.FormalParameterIsOutput)
            {
                // Flows the other direction (the callee WRITES to it) - its value before any
                // write is genuinely unknown, never "not in scope" if read before being set.
                seed[paramName] = SeedSymbolicOrTaint(formal, "parameter-not-seeded:output-argument", context);
                continue;
            }

            seed[paramName] = argument.LiteralArgument is { } literalArgument
                ? new SqlTextValue.Template([new TemplatePiece.Lit(
                    literalArgument.Value, new SourceSpan(literalArgument.SourcePath, literalArgument.StartLine, literalArgument.StartColumn), literalArgument.PrefixLength)])
                : SeedSymbolicOrTaint(formal, "parameter-not-seeded:non-literal-caller", context);
        }
    }

    /// <summary>
    /// When EVERY edge calling this proc supplies a literal argument for a given formal
    /// parameter, the parameter's true runtime value is provably one of those literals - a
    /// <see cref="TemplatePiece.Choice"/> under an empty guard (no single predicate governs
    /// "which caller"), composing with the same <see cref="SqlTextValue.Widen"/> cap every other
    /// divergence uses. If even ONE caller can't supply a literal, the whole parameter falls back
    /// to <see cref="SeedSymbolicOrTaint"/> rather than partially seeding from a subset - a taint
    /// at even one call site means the true value set is unknown, not merely wider than what the
    /// literals show.
    /// </summary>
    private static SqlTextValue SeedFromMultipleEdges(IReadOnlyList<ProcCallEdge> edges, ProcedureParameter formal, TransferContext context)
    {
        var paramName = formal.VariableName.Value;
        var declaredType = SqlTypeReferenceResolver.Resolve(formal.DataType, columnCollation: null);
        var at = context.Span(formal);
        SqlTextValue combined = new SqlTextValue.Tainted("parameter-not-seeded:cardinality-cap", at) { DeclaredType = declaredType };
        var first = true;

        foreach (var edge in edges)
        {
            var argument = edge.Arguments.FirstOrDefault(a => string.Equals(a.FormalParameterName, paramName, StringComparison.OrdinalIgnoreCase));
            if (argument is null || argument.FormalParameterIsOutput || argument.LiteralArgument is not { } literalArgument)
            {
                return SeedSymbolicOrTaint(formal, "parameter-not-seeded:non-literal-caller", context);
            }

            var literalValue = new SqlTextValue.Template([new TemplatePiece.Lit(
                literalArgument.Value, new SourceSpan(literalArgument.SourcePath, literalArgument.StartLine, literalArgument.StartColumn), literalArgument.PrefixLength)])
            { DeclaredType = declaredType };
            combined = first ? literalValue : SqlTextValue.Join(combined, literalValue, guardText: string.Empty, context.Cap, at);
            first = false;
        }

        // Reached only when every edge supplied a genuine literal argument - the loop above
        // returns early the moment any edge lacks one, so `combined` is a real seeded value here,
        // never the placeholder Tainted this method started with (unless Join's own cardinality
        // cap collapsed it, whose reason string is deliberately identical to that placeholder).
        return combined;
    }

    private static void CompileTriggerBody(TriggerStatementBody trigger, TransferContext context, bool emit)
    {
        if (!emit)
        {
            return;
        }

        var nestedScope = new DynamicSqlScope(SchemaObjectNameHelper.Qualify(trigger.Name), trigger.TriggerObject.Name);
        var nestedContext = context with { Scope = nestedScope, DeclaredTypes = new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase) };
        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase);
        SeedBatchDeclaredVariables(trigger.StatementList!.Statements, nestedContext, seed);
        var cfg = new DynamicSqlCfg(context.SourcePath, context.Cap, (s, activeGuards) => CompileLeaf(s, activeGuards, nestedContext));
        cfg.Solve(trigger.StatementList!.Statements, seed);
    }

    /// <summary>
    /// T-SQL's own DECLARE is a compile-time, BATCH-scoped construct - a variable declared inside
    /// only ONE branch of an IF/ELSE IF chain is still perfectly legal to reference from a SIBLING
    /// branch that never runs that DECLARE (it is simply NULL there, exactly like referencing a
    /// no-initializer DECLARE before its own line runs), even though only one branch's own path
    /// through this CFG ever visits the node that declares it. Without this, a sibling branch's
    /// own reference had NO entry in `state` at all and reported the misleading
    /// "variable-not-in-scope" - the same label a genuinely undeclared-anywhere variable gets,
    /// even though this shape compiles fine in real SQL Server (found auditing a real production
    /// database: several dozen call sites across a handful of large IF/ELSE-IF-chain-shaped procs,
    /// none an actual undeclared-variable bug). Pre-seeding every DECLARE found ANYWHERE in the
    /// batch - regardless of which branch it sits under - as an UninitializedDeclare hole before
    /// the CFG solver ever runs closes this: the branch that actually reaches its own DECLARE
    /// still overwrites this with its real value (an initializer, or the identical hole again),
    /// and every OTHER branch now correctly sees a typed-but-unknown value instead of a bare
    /// taint. TryAdd only - never overwrites an already-seeded name (a formal parameter, seeded
    /// separately and never colliding with a local's name in valid T-SQL, but defensive either way).
    /// </summary>
    internal static void SeedBatchDeclaredVariables(IList<TSqlStatement> statements, TransferContext context, Dictionary<string, SqlTextValue> seed)
    {
        var collector = new BatchDeclaredVariableCollector();
        foreach (var statement in statements)
        {
            statement.Accept(collector);
        }

        foreach (var (name, element) in collector.Declarations)
        {
            var declaredType = SqlTypeReferenceResolver.Resolve(element.DataType, columnCollation: null);
            if (declaredType is null)
            {
                continue;
            }

            context.DeclaredTypes.TryAdd(name, declaredType);
            seed.TryAdd(name, new SqlTextValue.Template([new TemplatePiece.Hole(declaredType, context.Span(element), HoleKind.UninitializedDeclare)]) { DeclaredType = declaredType });
        }
    }

    /// <summary>
    /// Collects every DECLARE found ANYWHERE in a batch/proc body, regardless of IF/BEGIN/TRY
    /// nesting - the default (un-overridden) ExplicitVisit already recurses into every container,
    /// so this never needs to enumerate specific statement kinds itself. Deliberately does NOT
    /// descend into a NESTED CREATE/ALTER PROCEDURE/FUNCTION body (a real, if rare, T-SQL shape -
    /// see CompileScopedBody's own handling of it) - that inner body is a SEPARATE batch with its
    /// own separate variable scope, so a DECLARE inside it must never leak into the OUTER batch's
    /// own seeding.
    /// </summary>
    private sealed class BatchDeclaredVariableCollector : TSqlFragmentVisitor
    {
        public List<(string Name, DeclareVariableElement Element)> Declarations { get; } = [];

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var element in node.Declarations)
            {
                Declarations.Add((element.VariableName.Value, element));
            }
        }

        public override void ExplicitVisit(ProcedureStatementBodyBase node)
        {
            // A nested proc/function body is its own separate batch/scope - stop here.
        }

        public override void ExplicitVisit(TriggerStatementBody node)
        {
            // Same reasoning as ProcedureStatementBodyBase above.
        }
    }

    // Only ProcedureStatementBody's parameters are ever reachable from a call graph (built from
    // EXEC ... call sites, never from a function invocation) - a function's own parameters are
    // returned as null here, not [], matching the old scanner's identical distinction.
    private static SchemaObjectName? ProcedureOrFunctionName(ProcedureStatementBodyBase procOrFunc) => procOrFunc switch
    {
        ProcedureStatementBody proc => proc.ProcedureReference.Name,
        FunctionStatementBody func => func.Name,
        _ => null,
    };

    private static IList<ProcedureParameter>? ProcedureOrFunctionParameters(ProcedureStatementBodyBase procOrFunc) =>
        procOrFunc is ProcedureStatementBody proc ? proc.Parameters : null;

    /// <summary>
    /// An OUTPUT-declared formal parameter is just an ordinary local variable inside the body -
    /// whatever this scan proved it holds by the end of the body (via the exact same SET/SELECT-
    /// assignment/branch-merge machinery every other tracked variable goes through) IS the value
    /// the procedure returns through it. An assembly resting on a hole is not a proven value, so
    /// it is excluded rather than publishing a fabricated string; if EVERY assembly rests on a
    /// hole, no summary is published at all - this scanner's standing "no entry at all, never a
    /// guessed one" contract for every seed/summary it produces.
    /// </summary>
    private static void RecordOutputParameterSummaries(string qualifiedName, IList<ProcedureParameter> formalParameters, Dictionary<string, SqlTextValue> folded, TransferContext context)
    {
        foreach (var formal in formalParameters)
        {
            if (formal.Modifier != ParameterModifier.Output
                || !folded.TryGetValue(formal.VariableName.Value, out var value)
                || value is not SqlTextValue.Template template)
            {
                continue;
            }

            var widened = SqlTextValue.Widen(template, context.Cap, context.Span(formal));
            if (widened is not SqlTextValue.Template widenedTemplate)
            {
                continue;
            }

            var values = SqlTextValue.Expand(widenedTemplate, context.Cap)
                .Where(assembly => !SqlTextValue.ContainsHole(assembly))
                .Select(assembly => string.Concat(assembly.OfType<FlatPiece.Lit>().Select(l => l.Text)))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (values.Count > 0)
            {
                context.OutputSummaries.Add(new ProcedureOutputSummary(qualifiedName, formal.VariableName.Value, values));
            }
        }
    }

    private static void CompileDeclare(DeclareVariableStatement declare, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        foreach (var element in declare.Declarations)
        {
            var name = element.VariableName.Value;
            var declaredType = SqlTypeReferenceResolver.Resolve(element.DataType, columnCollation: null);
            if (declaredType is not null)
            {
                context.DeclaredTypes[name] = declaredType;
            }

            var site = context.Span(element);
            if (element.Value is null or NullLiteral)
            {
                // No initializer at all, OR an explicit `= NULL` - either way, genuinely no VALUE
                // is known up to this point, but the declared type is a hard T-SQL guarantee
                // regardless of whether it was ever assigned a non-NULL value.
                state[name] = declaredType is { } type
                    ? new SqlTextValue.Template([new TemplatePiece.Hole(type, site, HoleKind.UninitializedDeclare)]) { DeclaredType = type }
                    : new SqlTextValue.Tainted("no-initializer", site);
                continue;
            }

            state[name] = FoldByDeclaredType(element.Value, declaredType, context, state, site);
        }
    }

    /// <summary>
    /// A declared-INTEGER-family variable's initializer/assignment folds through
    /// <see cref="ExpressionEvaluator.FoldInteger"/> first - the only fold path that actually
    /// evaluates <c>+</c>/<c>-</c> arithmetically - rather than the general (string-
    /// concatenation-flavored) <see cref="ExpressionEvaluator.Fold"/>, whose own
    /// <c>BinaryExpression</c> handling treats <c>+</c> as concatenation: routing an int
    /// assignment through it would silently produce "51" for <c>@j = @i + 1</c> where
    /// <c>@i</c> is 5, not the correct 6. A successful integer fold is stored as an ordinary
    /// literal-text <see cref="TemplatePiece.Lit"/> - the same shape a string literal already
    /// uses - so <see cref="ExpressionEvaluator.FoldInteger"/>'s own (newly added) variable-
    /// reference case can read it straight back for a LATER statement, closing the chain (e.g.
    /// <c>DECLARE @start INT = 5; ... SUBSTRING(@s, @start, 10)</c>). Every other declared type,
    /// and any int-typed initializer <see cref="ExpressionEvaluator.FoldInteger"/> itself
    /// declines (a column reference, an unmodeled function, ...), falls back to the general fold
    /// exactly as before this existed - a strict widening, never a behavior change for anything
    /// that already resolved.
    /// </summary>
    private static SqlTextValue FoldByDeclaredType(
        ScalarExpression expression, SqlType? declaredType, TransferContext context, Dictionary<string, SqlTextValue> state, SourceSpan site)
    {
        if (declaredType is { Category: SqlTypeCategory.TinyInt or SqlTypeCategory.SmallInt or SqlTypeCategory.Int or SqlTypeCategory.BigInt }
            && ExpressionEvaluator.FoldInteger(expression, state, context.SourcePath, context.Cap, out var value))
        {
            return new SqlTextValue.Template([new TemplatePiece.Lit(value.ToString(System.Globalization.CultureInfo.InvariantCulture), site, PrefixLength: 0)])
                with { DeclaredType = declaredType };
        }

        if (expression is ScalarSubquery { QueryExpression: QuerySpecification { FromClause: not null } } subquery
            && TryFoldScalarSubqueryFromSingleKnownTable(subquery, context, state) is { } fetched)
        {
            return fetched with { DeclaredType = declaredType };
        }

        var folded = ExpressionEvaluator.Fold(expression, state, context.SourcePath, context.Cap, context.Catalog);
        return folded with { DeclaredType = declaredType };
    }

    /// <summary>
    /// The <c>DECLARE @x TYPE = (SELECT col FROM t WHERE ...)</c> / <c>SET @x = (SELECT col FROM
    /// t WHERE ...)</c> counterpart of <see cref="TryCompileSelectAssignmentFromSingleKnownTable"/>
    /// - same "single catalog-known table, no JOIN, one selected scalar expression" recognition
    /// and the same <see cref="TryFetchLiveScalar"/> live-fetch splice, just reached from a
    /// <see cref="ScalarSubquery"/> expression node instead of a <c>SELECT @var = ...</c>
    /// statement. This is the shape <see cref="ExpressionEvaluator.Fold"/>'s own
    /// <c>ScalarSubquery</c> case declines outright as <c>non-literal-expression:sql-loaded-from-table</c>
    /// - real production code overwhelmingly uses a scalar-subquery initializer for this pattern,
    /// not the `SELECT @var = col FROM t` form, so leaving this case out would have made
    /// <c>--fetch-sql-from-tables</c> a no-op against it. Returns null - falling back to
    /// <see cref="ExpressionEvaluator.Fold"/>'s ordinary decline - for a JOIN, more than one
    /// select element, a non-scalar/non-column-spliceable select expression, or when no fetcher
    /// was supplied (every corpus/file-mode scan).
    /// </summary>
    private static SqlTextValue? TryFoldScalarSubqueryFromSingleKnownTable(ScalarSubquery subquery, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        if (context.Catalog is not { } catalog
            || subquery.QueryExpression is not QuerySpecification { FromClause.TableReferences: [NamedTableReference namedTable] } spec
            || spec.SelectElements is not [SelectScalarExpression { Expression: { } expression }])
        {
            return null;
        }

        var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(namedTable.SchemaObject));
        if (catalog.Find(qualifiedName) is not { } table
            || TryFoldWithColumnSplice(expression, table, state, context) is not { } spliced)
        {
            return null;
        }

        return TryFetchLiveScalar(expression, table, spec.WhereClause, context) ?? spliced;
    }

    private static void CompileAssignment(
        string name, AssignmentKind kind, ScalarExpression? expression, bool functionCallExists, TSqlFragment site,
        TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        var declaredType = context.DeclaredTypes.GetValueOrDefault(name);
        var span = context.Span(site);

        if (functionCallExists || kind is not (AssignmentKind.Equals or AssignmentKind.AddEquals))
        {
            state[name] = HavocOrTaint("unsupported-assignment", span, declaredType);
            return;
        }

        // SetVariableStatement.Expression is null when the RHS is a shape ScriptDOM models in a
        // sibling property instead (SET @c = CURSOR FOR ..., SET @x = <identifier>, ...) - none
        // fold, so this taints rather than risking a null-reference fold below.
        if (expression is null)
        {
            state[name] = HavocOrTaint("unsupported-assignment", span, declaredType);
            return;
        }

        if (kind == AssignmentKind.AddEquals)
        {
            var existing = state.TryGetValue(name, out var existingValue) ? existingValue : HavocOrTaint("variable-not-in-scope", span, declaredType);

            // `@i += expr` on an INTEGER-family variable means arithmetic addition, not string
            // concatenation - only reachable when BOTH the existing value and this statement's
            // own RHS are themselves already-resolved integers (see FoldByDeclaredType's own
            // reasoning); anything else falls through to the ordinary text-concatenation path
            // exactly as before, unchanged.
            if (declaredType is { Category: SqlTypeCategory.TinyInt or SqlTypeCategory.SmallInt or SqlTypeCategory.Int or SqlTypeCategory.BigInt }
                && ExpressionEvaluator.TryLiteralAsInteger(existing, out var existingInt)
                && ExpressionEvaluator.FoldInteger(expression, state, context.SourcePath, context.Cap, out var addValue))
            {
                state[name] = new SqlTextValue.Template([new TemplatePiece.Lit((existingInt + addValue).ToString(System.Globalization.CultureInfo.InvariantCulture), span, PrefixLength: 0)])
                    with { DeclaredType = declaredType };
                return;
            }

            var rhs = ExpressionEvaluator.Fold(expression, state, context.SourcePath, context.Cap, context.Catalog);
            state[name] = SqlTextValue.Concat(existing, rhs) with { DeclaredType = declaredType };
            return;
        }

        state[name] = FoldByDeclaredType(expression, declaredType, context, state, span);
    }

    /// <summary>
    /// The one place a value degrades to a hole-or-taint by declared type: a typed
    /// <see cref="HoleKind.HavocWrite"/> hole when <paramref name="declaredType"/> is known (this
    /// scanner could not prove WHAT the statement wrote, but T-SQL's own DECLARE guarantees WHAT
    /// TYPE it must be, regardless), <see cref="SqlTextValue.Tainted"/> only when even that much is
    /// unknown. Every unmodeled-write site in this class (assignment shapes this scanner does not
    /// fold, an EXEC this scanner cannot see through, a non-pure SELECT-assignment, an unseen
    /// variable) goes through here so "known type, unknown value" never masquerades as "nothing
    /// known at all" - the CLAUDE.md soundness contract is about the VALUE, never the type, which
    /// is a hard compile-time fact independent of whatever this scanner could trace.
    /// </summary>
    private static SqlTextValue HavocOrTaint(string reason, SourceSpan span, SqlType? declaredType) =>
        declaredType is { } type
            ? new SqlTextValue.Template([new TemplatePiece.Hole(type, span, HoleKind.HavocWrite)]) { DeclaredType = type }
            : new SqlTextValue.Tainted(reason, span);

    /// <summary>
    /// <c>SELECT @x = @x + expr FROM t [...]</c> - the running-total/"quirky update" idiom (real
    /// corpus shape: SQL-Server-First-Responder-Kit's sp_Blitz.sql accumulates a placeholder count
    /// this way) - is NOT an arbitrary rewrite of @x the way <see cref="HavocOrTaint"/>'s general
    /// "unmodeled write" case has to assume. T-SQL evaluates this per matching row as
    /// <c>@x := @x + expr(row)</c> in sequence, so @x's FINAL value is always its OWN prior value
    /// with zero or more unknown fragments appended - NEVER something unrelated to what @x already
    /// held (zero rows leaves @x completely unchanged; one or more rows only ever extends it).
    /// Recognizing this narrow shape and modeling it as <see cref="SqlTextValue.Concat"/>(existing,
    /// unknown-appended-hole) instead of a fresh unconstrained Hole is what lets a big literal
    /// prefix @x already held survive this statement - the general path (assigning THAT prefix a
    /// context-free <see cref="HoleKind.HavocWrite"/> hole) would otherwise discard it entirely,
    /// even though every execution of THIS specific shape provably keeps it intact. Deliberately
    /// narrow: only the SAME variable appearing as the LEFTMOST leaf of a chain of top-level
    /// additions (the idiom's actual shape everywhere it's been seen, including the common
    /// three-or-more-term form <c>@x = @x + col + ', '</c>, which T-SQL's left-associative parse
    /// nests as <c>(@x + col) + ', '</c> - @x is still the untouched base, just no longer the
    /// immediate <see cref="BinaryExpression.FirstExpression"/> of the outermost node, hence the
    /// walk down <see cref="IsLeftmostSelfReference"/> rather than a single direct match) -
    /// anything else (the variable on the right, nested inside a function call, multiple assigned
    /// variables) falls back to the caller's own general havoc, never a guess.
    /// </summary>
    private static bool TryCompileSelfReferentialAppend(SelectStatement select, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        if (select.QueryExpression is not QuerySpecification { SelectElements: [SelectSetVariable { AssignmentKind: AssignmentKind.Equals } setVar] }
            || setVar.Expression is not BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } binary
            || !IsLeftmostSelfReference(binary, setVar.Variable.Name)
            || !state.TryGetValue(setVar.Variable.Name, out var existing))
        {
            return false;
        }

        var declaredType = context.DeclaredTypes.GetValueOrDefault(setVar.Variable.Name);
        var appended = HavocOrTaint("select-assignment-not-pure", context.Span(binary), declaredType);
        state[setVar.Variable.Name] = SqlTextValue.Concat(existing, appended) with { DeclaredType = declaredType };
        return true;
    }

    /// <summary>
    /// True when <paramref name="expression"/> is <paramref name="name"/> itself, or a top-level
    /// addition whose own left operand is (recursively) the same thing - i.e. <paramref name="name"/>
    /// is the leftmost leaf of a left-associative <c>+</c> chain, with everything else in the chain
    /// appended somewhere to its right. Stops descending the instant a non-Add node is reached, so
    /// this never crosses into a different operator (e.g. <c>@x - y + z</c> does not match: the
    /// leftmost leaf of THAT chain's Add is the whole <c>@x - y</c> subtraction, not @x alone).
    /// </summary>
    private static bool IsLeftmostSelfReference(ScalarExpression expression, string name) => expression switch
    {
        VariableReference variable => string.Equals(variable.Name, name, StringComparison.OrdinalIgnoreCase),
        BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add, FirstExpression: { } left } => IsLeftmostSelfReference(left, name),
        _ => false,
    };

    /// <summary>
    /// <c>SELECT @x = expr[, @y = expr2, ...]</c>, the other common way T-SQL assigns local
    /// variables. Only the "pure assignment" shape - no FROM/WHERE/HAVING/TOP, every select
    /// element a variable assignment - is trustworthy: a FROM clause makes the assigned value
    /// data- and row-order-dependent, a materially different (and un-foldable, CLAUDE.md: corpus
    /// DML is never executed) outcome. Any other SELECT shape leaves every variable it assigns
    /// tainted rather than silently keeping a stale value.
    /// </summary>
    private static void CompileSelectAssignment(SelectStatement select, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        var assignedNames = new SelectSetVariableCollector();
        select.Accept(assignedNames);
        if (assignedNames.Names.Count == 0)
        {
            return;
        }

        var span = context.Span(select);
        if (select.QueryExpression is not QuerySpecification { FromClause: null, WhereClause: null, HavingClause: null, TopRowFilter: null } spec
            || spec.SelectElements.Count == 0
            || !spec.SelectElements.All(e => e is SelectSetVariable))
        {
            if (!TryCompileSelectAssignmentFromSingleKnownTable(select, context, state)
                && !TryCompileSelfReferentialAppend(select, context, state))
            {
                foreach (var name in assignedNames.Names)
                {
                    state[name] = HavocOrTaint("select-assignment-not-pure", span, context.DeclaredTypes.GetValueOrDefault(name));
                }
            }

            return;
        }

        foreach (var element in spec.SelectElements.Cast<SelectSetVariable>())
        {
            CompileAssignment(element.Variable.Name, element.AssignmentKind, element.Expression, functionCallExists: false, select, context, state);
        }
    }

    /// <summary>
    /// <c>SELECT @var = expr FROM table [WHERE ...]</c> is unconditionally row-dependent (a
    /// genuinely different value depending which row wins - CLAUDE.md: corpus DML never
    /// executes), but when the FROM clause names EXACTLY ONE catalog-known table (no JOIN), the
    /// SELECT has exactly one <c>@var = expr</c> element, and that expression's own shape is
    /// nothing but literals/variables concatenated with that SAME table's own columns,
    /// the expression's STRUCTURAL SHAPE is fully known even though the concrete row is not -
    /// each column reference splices in as a typed <see cref="HoleKind.RowDependentColumn"/> hole
    /// (the column's own catalog type is a hard fact; its per-row VALUE is what's unknown), the
    /// same "known shape, unknown value" case an uninitialized DECLARE or an unseeded proc
    /// parameter already gets. Returns false - leaving <paramref name="state"/> untouched, so the
    /// caller falls back to the ordinary blanket taint - for any shape this can't structurally
    /// recognize (no catalog supplied, a JOIN, more than one assigned variable, a function call/
    /// subquery/unknown column inside the expression): never a guess.
    /// </summary>
    private static bool TryCompileSelectAssignmentFromSingleKnownTable(SelectStatement select, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        if (context.Catalog is not { } catalog
            || select.QueryExpression is not QuerySpecification { FromClause.TableReferences: [NamedTableReference namedTable] } spec
            || spec.SelectElements.Count != 1
            || spec.SelectElements[0] is not SelectSetVariable { AssignmentKind: AssignmentKind.Equals, Expression: { } expression } setVar)
        {
            return false;
        }

        var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.Qualify(namedTable.SchemaObject));
        if (catalog.Find(qualifiedName) is not { } table)
        {
            return false;
        }

        if (TryFoldWithColumnSplice(expression, table, state, context) is not { } spliced)
        {
            return false;
        }

        spliced = TryFetchLiveScalar(expression, table, spec.WhereClause, context) ?? spliced;

        var declaredType = context.DeclaredTypes.GetValueOrDefault(setVar.Variable.Name);
        state[setVar.Variable.Name] = spliced with { DeclaredType = declaredType };
        return true;
    }

    /// <summary>
    /// <c>--fetch-sql-from-tables</c>'s own splice: when the selected expression is a BARE
    /// column reference (not a concatenation - a concatenated shape has no single scalar value to
    /// fetch), a live fetch through <see cref="TransferContext.RowValueFetcher"/> reads up to
    /// <see cref="TransferContext.Cap"/> real distinct values instead of leaving it a
    /// <see cref="HoleKind.RowDependentColumn"/> hole - whatever literal-equality conjuncts the
    /// WHERE clause offers (<see cref="TryExtractLiteralEqualityKeys"/>) narrow the fetch on the
    /// database side, best-effort (an OR, a non-literal comparison, or no WHERE at all just means
    /// fewer/no conjuncts are pushed down, never a decline - every real candidate value the
    /// column could hold is still a genuine possibility this scanner has no way to rule out
    /// statically). Exactly one distinct value splices in as a plain literal; more than one
    /// becomes a <see cref="TemplatePiece.Choice"/> (via <see cref="SqlTextValue.Join"/>, the
    /// SAME mechanism an IF/ELSE branch's own divergence already uses), so every fetched
    /// candidate is analyzed independently rather than guessing which one a given call actually
    /// selects - the engine's own existing cardinality cap (<see cref="TransferContext.Cap"/>)
    /// gracefully degrades an oversized fan-out to a typed hole exactly as it already does for
    /// every other source of divergence. Returns null (falling back to the ordinary
    /// RowDependentColumn hole) only for: no fetcher supplied (the default, always, for every
    /// corpus/file-mode scan), a non-bare-column expression, or a failed/empty fetch.
    /// </summary>
    private static SqlTextValue.Template? TryFetchLiveScalar(ScalarExpression expression, CatalogTable table, WhereClause? whereClause, TransferContext context)
    {
        if (context.RowValueFetcher is not { } fetcher
            || expression is not ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: > 0 } selectedIdentifiers } selectedColumnRef
            || table.FindColumn(selectedIdentifiers[^1].Value) is not { } selectedColumn)
        {
            return null;
        }

        var equalityKeys = TryExtractLiteralEqualityKeys(whereClause, table);
        var fetchedValues = fetcher.TryFetchDistinctValues(table.QualifiedName, selectedColumn.Name, equalityKeys, context.Cap);
        if (fetchedValues is not { Count: > 0 })
        {
            return null;
        }

        var site = context.Span(selectedColumnRef);
        var guardText = $"live-fetch:{table.QualifiedName}.{selectedColumn.Name}";
        SqlTextValue combined = new SqlTextValue.Template([new TemplatePiece.Lit(fetchedValues[0], site, PrefixLength: 0)]);
        for (var i = 1; i < fetchedValues.Count; i++)
        {
            var next = new SqlTextValue.Template([new TemplatePiece.Lit(fetchedValues[i], site, PrefixLength: 0)]);
            combined = SqlTextValue.Join(combined, next, guardText, context.Cap, site);
        }

        return combined as SqlTextValue.Template;
    }

    /// <summary>
    /// Best-effort: collects every `Column = literal` conjunct from a WHERE clause that this
    /// pass can statically recognize (every column belonging to <paramref name="table"/>),
    /// skipping - never declining outright - anything it can't push down (an OR branch, a
    /// non-equality comparison, a comparison against a variable/expression rather than a
    /// literal). An empty list (no WHERE at all, or nothing usable in it) means no filter is
    /// statically known - the caller still fetches every distinct value in the column, since
    /// every one of them is a real candidate this scanner has no static way to exclude.
    /// </summary>
    private static List<(string Column, string LiteralValue)> TryExtractLiteralEqualityKeys(WhereClause? whereClause, CatalogTable table)
    {
        var keys = new List<(string, string)>();
        if (whereClause?.SearchCondition is { } condition)
        {
            CollectEqualityKeys(condition, table, keys);
        }

        return keys;
    }

    private static void CollectEqualityKeys(BooleanExpression expression, CatalogTable table, List<(string Column, string LiteralValue)> keys)
    {
        switch (expression)
        {
            case BooleanParenthesisExpression paren:
                CollectEqualityKeys(paren.Expression, table, keys);
                break;

            case BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } and:
                CollectEqualityKeys(and.FirstExpression, table, keys);
                CollectEqualityKeys(and.SecondExpression, table, keys);
                break;

            case BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals } cmp:
                if (!TryAddEqualityKey(cmp.FirstExpression, cmp.SecondExpression, table, keys))
                {
                    TryAddEqualityKey(cmp.SecondExpression, cmp.FirstExpression, table, keys);
                }

                break;

            default:
                // OR, inequality, a function call, a comparison against something other than a
                // literal, ... - not pushed down, but not a reason to decline the fetch entirely.
                break;
        }
    }

    private static bool TryAddEqualityKey(ScalarExpression columnSide, ScalarExpression literalSide, CatalogTable table, List<(string Column, string LiteralValue)> keys)
    {
        if (columnSide is not ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: > 0 } identifiers } colRef
            || table.FindColumn(identifiers[^1].Value) is null)
        {
            return false;
        }

        var literalText = literalSide switch
        {
            StringLiteral s => s.Value,
            IntegerLiteral i => i.Value,
            _ => null,
        };

        if (literalText is null)
        {
            return false;
        }

        keys.Add((colRef.MultiPartIdentifier.Identifiers[^1].Value, literalText));
        return true;
    }

    /// <summary>
    /// The narrow expression folder <see cref="TryCompileSelectAssignmentFromSingleKnownTable"/>
    /// needs: literals, variables (via <paramref name="state"/>), and a concatenation tree of
    /// those - PLUS a bare/qualified reference to one of <paramref name="table"/>'s own columns,
    /// which <see cref="ExpressionEvaluator.Fold"/> itself always declines
    /// ("non-literal-expression:column-reference", correctly, everywhere else this scanner folds
    /// an expression - a column reference has no meaning outside this ONE single-known-table
    /// context). Deliberately does not delegate to <see cref="ExpressionEvaluator"/> at all: this
    /// is a much narrower grammar (no function calls, no CAST) matching exactly the corpus shape
    /// this gap targets, not a general-purpose column-aware evaluator.
    /// </summary>
    private static SqlTextValue? TryFoldWithColumnSplice(ScalarExpression expression, CatalogTable table, Dictionary<string, SqlTextValue> state, TransferContext context) => expression switch
    {
        StringLiteral literal => new SqlTextValue.Template([new TemplatePiece.Lit(literal.Value, context.Span(literal), literal.IsNational ? 2 : 1)]),
        ParenthesisExpression paren => TryFoldWithColumnSplice(paren.Expression, table, state, context),
        VariableReference variableRef => state.GetValueOrDefault(variableRef.Name),
        ColumnReferenceExpression { MultiPartIdentifier.Identifiers: { Count: > 0 } identifiers } colRef
            when table.FindColumn(identifiers[^1].Value) is { Type: { } columnType }
            => new SqlTextValue.Template([new TemplatePiece.Hole(columnType, context.Span(colRef), HoleKind.RowDependentColumn)]),
        BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } binary
            when TryFoldWithColumnSplice(binary.FirstExpression, table, state, context) is { } left
                && TryFoldWithColumnSplice(binary.SecondExpression, table, state, context) is { } right
            => SqlTextValue.Concat(left, right),
        _ => null,
    };

    /// <summary>
    /// EXEC('...')/EXEC(@sql), sp_executesql, and every other EXEC form. The first two produce
    /// this call site's own <see cref="DynamicSqlScript"/>(s)/<see cref="DynamicSqlFinding"/> -
    /// pure functions of the CURRENT state, so their whole body is skipped during the suppressed
    /// fixpoint rounds (<paramref name="emit"/> false): neither writes any variable another
    /// statement could depend on, so nothing observable is lost by not running them early. Any
    /// OTHER EXEC form (an ordinary stored-procedure call) has no such guarantee - this scanner
    /// cannot see what an arbitrary called procedure does internally, so every variable the call
    /// site itself mentions is tainted (state mutation, not emission) UNLESS
    /// <see cref="SeedKnownOutputArguments"/> can seed it first from a callee's own
    /// already-proven-constant OUTPUT parameter (<see cref="TransferContext.OutputSummaryIndex"/>) -
    /// the blanket taint is always a safe fallback, seeding is purely additive precision on top.
    /// </summary>
    private static void CompileExecute(ExecuteStatement node, IReadOnlyList<string> activeGuards, TransferContext context, Dictionary<string, SqlTextValue> state, bool emit)
    {
        switch (node.ExecuteSpecification.ExecutableEntity)
        {
            case ExecutableStringList stringList:
                if (emit)
                {
                    CompileStringList(stringList, node, activeGuards, context, state);
                }

                break;

            case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var name } procRef
                when string.Equals(name, "sp_executesql", StringComparison.OrdinalIgnoreCase):
                if (emit)
                {
                    CompileSpExecuteSql(procRef, node, activeGuards, context, state);
                }

                break;

            default:
                TaintReferencedVariables(node, context, state);
                break;
        }
    }

    private static void CompileStringList(ExecutableStringList stringList, ExecuteStatement node, IReadOnlyList<string> activeGuards, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        SqlTextValue combined = new SqlTextValue.Template([]);
        foreach (var element in stringList.Strings)
        {
            combined = SqlTextValue.Concat(combined, ExpressionEvaluator.Fold(element, state, context.SourcePath, context.Cap, context.Catalog));
            if (combined is SqlTextValue.Tainted)
            {
                break;
            }
        }

        EmitScriptsOrFinding(combined, node, activeGuards, context, parameterDeclarationText: null, argumentBindings: null);
    }

    /// <summary>Every named execute-parameter beyond @stmt/@params (e.g. <c>@P = @Code</c>) whose value is a bare variable reference - captured unconditionally, since a NESTED dynamic-SQL pass may need it to seed one of ITS OWN parameters from this outer script's own declared type (see <see cref="DynamicSqlScript.ArgumentBindings"/>'s own doc comment).</summary>
    private static readonly HashSet<string> ReservedArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "@stmt", "@statement", "@params", "@parameters",
    };

    private static void CompileSpExecuteSql(ExecutableProcedureReference procRef, ExecuteStatement node, IReadOnlyList<string> activeGuards, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        if (procRef.Parameters.Count == 0)
        {
            context.Findings.Add(Unanalyzable(node, context, "non-literal-argument"));
            return;
        }

        var statementArg = ResolveNamedOrPositionalArgument(procRef.Parameters, index: 0, "@stmt", "@statement");
        if (statementArg is null)
        {
            context.Findings.Add(Unanalyzable(node, context, "non-literal-argument"));
            return;
        }

        var query = ExpressionEvaluator.Fold(statementArg, state, context.SourcePath, context.Cap, context.Catalog);
        var parameterDeclarationText = ResolveParameterDeclarationText(procRef, state, context);
        var argumentBindings = ResolveArgumentBindings(procRef);
        EmitScriptsOrFinding(query, node, activeGuards, context, parameterDeclarationText, argumentBindings);
    }

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

    /// <summary>sp_executesql's optional second argument declares its parameters' exact types (Tier B) - missing, unfoldable, or multi-valued falls back to null rather than guessing. Returned as raw text, not parsed here - <see cref="DynamicSqlScript.ParameterDeclarationText"/>'s own doc comment explains why parsing is deferred to <see cref="DynamicSqlPipeline"/>, where a real catalog exists.</summary>
    private static string? ResolveParameterDeclarationText(ExecutableProcedureReference procRef, Dictionary<string, SqlTextValue> state, TransferContext context)
    {
        var paramsArg = ResolveNamedOrPositionalArgument(procRef.Parameters, index: 1, "@params", "@parameters");
        if (paramsArg is null)
        {
            return null;
        }

        var folded = ExpressionEvaluator.Fold(paramsArg, state, context.SourcePath, context.Cap, context.Catalog);
        if (folded is not SqlTextValue.Template template)
        {
            return null;
        }

        var widened = SqlTextValue.Widen(template, context.Cap, context.Span(paramsArg));
        if (widened is not SqlTextValue.Template widenedTemplate)
        {
            return null;
        }

        var assemblies = SqlTextValue.Expand(widenedTemplate, context.Cap);
        if (assemblies.Count != 1 || SqlTextValue.ContainsHole(assemblies[0]))
        {
            // A @params declaration text with more than one possible value, or one resting on a
            // hole, is a rare compound shape - falls back to null exactly like an unfoldable one,
            // rather than guessing which value applies.
            return null;
        }

        return string.Concat(assemblies[0].OfType<FlatPiece.Lit>().Select(l => l.Text));
    }

    /// <summary>
    /// sp_executesql's own @stmt/@params arguments can be passed by name (order-independent),
    /// distinct from - and never confused with - the very common pattern where @stmt/@params
    /// ARE positional but LATER arguments are named after the query's own declared parameters:
    /// the presence of ANY named argument does not mean every argument is named, so this always
    /// tries a formal-name match first and falls back to positional regardless of what other
    /// arguments in the call happen to be named.
    /// </summary>
    private static ScalarExpression? ResolveNamedOrPositionalArgument(IList<ExecuteParameter> parameters, int index, params ReadOnlySpan<string> formalNames)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Variable is { } variable && formalNames.Contains(variable.Name, StringComparer.OrdinalIgnoreCase))
            {
                return parameter.ParameterValue;
            }
        }

        return index < parameters.Count ? parameters[index].ParameterValue : null;
    }

    /// <summary>
    /// When <paramref name="value"/> is a real value, emits one script per surviving assembly
    /// (existing behavior). When <paramref name="activeGuards"/> - the static list of IF
    /// predicates this EXEC is itself reached under (see <see cref="CompileLeaf"/>'s own doc
    /// comment) - contains an EXACT match for one of <paramref name="value"/>'s own
    /// <see cref="SqlTextValue.GuardedAlternatives"/> tags, that ONE alternative's value is used
    /// in place of <paramref name="value"/> outright: the consuming EXEC's OWN guard proves which
    /// branch produced it, so this is strictly MORE precise than the un-narrowed value, whether
    /// that value is a live <see cref="TemplatePiece.Choice"/>/typed hole or
    /// <see cref="SqlTextValue.Tainted"/>. Absent a match: for a <see cref="SqlTextValue.Tainted"/>
    /// value, <paramref name="activeGuards"/> being NON-empty means the consuming EXEC IS itself
    /// guarded, just by something this value's own tags don't account for - soundness-first
    /// exact-text matching (never implication) means nothing here is PROVABLY the branch that
    /// ran, so this declines with the tainted reason rather than guessing (the gap
    /// <c>Scan_GuardedSetThenDifferentGuardExec_UnresolvableAliasType_StaysTainted</c> covers).
    /// <paramref name="activeGuards"/> EMPTY (an unconditional EXEC) falls back to the old
    /// scanner's <c>TryEmitGuardedAlternativeScripts</c> policy: try EVERY alternative in turn,
    /// since nothing about this call site rules any of them out - if the overall value is unknown
    /// but ONE specific branch's own known text can still be recovered, that is real, usable
    /// signal, not a decline. A live (non-Tainted) value with no active-guard match simply emits
    /// as-is (existing behavior, unaffected either way) - it was already a usable answer on its
    /// own, narrowing is a bonus, never a precondition for reporting it. Matches the old
    /// scanner's exact policy: if ANY alternative yields a script, the site is reported as
    /// analyzed (via those scripts) and NOT also reported Unanalyzable.
    /// </summary>
    private static void EmitScriptsOrFinding(
        SqlTextValue value, ExecuteStatement node, IReadOnlyList<string> activeGuards, TransferContext context, string? parameterDeclarationText, IReadOnlyDictionary<string, string>? argumentBindings)
    {
        if (TryNarrowByActiveGuard(value, activeGuards) is { } narrowed)
        {
            TryEmitFromValue(narrowed, node, context, parameterDeclarationText, argumentBindings);
            return;
        }

        if (value is SqlTextValue.Tainted tainted)
        {
            if (activeGuards.Count > 0)
            {
                context.Findings.Add(Unanalyzable(node, context, tainted.Reason));
                return;
            }

            var recovered = false;
            foreach (var alternative in tainted.GuardedAlternatives ?? [])
            {
                recovered |= TryEmitFromValue(alternative.Value, node, context, parameterDeclarationText, argumentBindings);
            }

            if (!recovered)
            {
                context.Findings.Add(Unanalyzable(node, context, tainted.Reason));
            }

            return;
        }

        // A live value whose expansion would be absurdly large (see MaxExpandedPieceTotal's doc
        // comment - a real 280KB proc produced one totalling tens of millions of pieces, an OOM
        // if materialized) is declined HERE, with its own honest finding, rather than inside
        // TryEmitFromValue's boolean - this is the one call site whose false would otherwise be
        // silent, and CLAUDE.md's dynamic SQL policy is "never silently counted as clean".
        if (SqlTextValue.Widen(value, context.Cap, context.Span(node)) is SqlTextValue.Template widenedForSizing
            && SqlTextValue.ExpandedPieceTotal(widenedForSizing) > SqlTextValue.MaxExpandedPieceTotal)
        {
            context.Findings.Add(Unanalyzable(node, context, SqlTextValue.ExpansionSizeCapReason));
            return;
        }

        TryEmitFromValue(value, node, context, parameterDeclarationText, argumentBindings);
    }

    private static SqlTextValue.Template? TryNarrowByActiveGuard(SqlTextValue value, IReadOnlyList<string> activeGuards)
    {
        if (activeGuards.Count == 0 || value.GuardedAlternatives is not { Count: > 0 } alternatives)
        {
            return null;
        }

        return alternatives.Where(alternative => activeGuards.Contains(alternative.GuardText, StringComparer.Ordinal))
            .Select(alternative => alternative.Value)
            .FirstOrDefault();
    }

    private static bool TryEmitFromValue(
        SqlTextValue value, ExecuteStatement node, TransferContext context, string? parameterDeclarationText, IReadOnlyDictionary<string, string>? argumentBindings)
    {
        var site = context.Span(node);
        var widened = SqlTextValue.Widen(value, context.Cap, site);
        if (widened is SqlTextValue.Tainted)
        {
            return false;
        }

        var widenedTemplate = (SqlTextValue.Template)widened;
        if (SqlTextValue.ExpandedPieceTotal(widenedTemplate) > SqlTextValue.MaxExpandedPieceTotal)
        {
            // Defensive twin of EmitScriptsOrFinding's own pre-check (which owns the finding for
            // the main-value path): this guards every OTHER caller - the guarded-alternatives
            // recovery loop, the narrowed-by-active-guard path - so no route into Expand can
            // materialize an absurd expansion. Declining one oversized alternative is just
            // "not recovered", the same false every other unusable alternative returns.
            return false;
        }

        var assemblies = SqlTextValue.Expand(widenedTemplate, context.Cap);

        // Two independent branches (or two proc-call-graph callers, two loop unrollings, ...)
        // frequently agree on the exact same rendered SQL text even though their own Template
        // pieces carry different source positions and so are never StructurallyEqual - a
        // duplicate-position record identity that means nothing to a report reader, who only
        // ever sees rendered InnerText. Deduping HERE (at the one place text is actually
        // rendered), rather than teaching every Join/Choice site to also compare rendered output,
        // keeps every upstream provenance-preserving comparison exact while still guaranteeing
        // the SAME defect is never reported twice under one EXEC just because two paths agreed.
        var seenText = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in assemblies)
        {
            var rendered = TemplateRenderer.Render(assembly);
            if (!seenText.Add(rendered.InnerText))
            {
                continue;
            }

            var confidence = SqlTextValue.ContainsHole(assembly) ? FindingConfidence.Medium : FindingConfidence.High;
            context.Scripts.Add(new DynamicSqlScript(
                CallSite(node, context), rendered.InnerText, rendered.SegmentMap, parameterDeclarationText,
                context.Scope, argumentBindings, confidence,
                rendered.Placeholders.Count > 0 ? rendered.Placeholders : null));
        }

        return true;
    }

    /// <summary>
    /// Taints the return-value variable (<c>EXEC @rc = proc</c>) and every argument passed with
    /// OUTPUT - this scanner does not model what an arbitrary called procedure does internally,
    /// so either of those COULD come back holding something other than what was folded for it. A
    /// plain (non-OUTPUT) argument is a genuine T-SQL call-by-VALUE - the callee cannot write
    /// back through it no matter what it does internally, so it is never tainted here (was
    /// previously blanket-tainted along with everything else the call site merely mentioned,
    /// needlessly declining a variable the callee could not possibly have touched - a real
    /// production pattern: EXEC @RC = dbo.SomeHelper @sql, @otherArg OUTPUT used @sql only to
    /// PASS dynamic SQL text INTO the helper, never to receive anything back). Any executable
    /// entity shape other than an ordinary procedure reference (rare) falls back to the old
    /// blanket "every referenced variable could have been written" default, matching this
    /// project's general "unmodeled construct stays conservative" philosophy.
    /// </summary>
    private static void TaintReferencedVariables(ExecuteStatement node, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        var seeded = SeedKnownOutputArguments(node, context, state);
        var span = context.Span(node);
        foreach (var name in CollectWritableVariableNames(node).Where(n => state.ContainsKey(n) && !seeded.Contains(n)))
        {
            state[name] = HavocOrTaint("unsupported-execute-form", span, context.DeclaredTypes.GetValueOrDefault(name));
        }
    }

    private static IEnumerable<string> CollectWritableVariableNames(ExecuteStatement node)
    {
        if (node.ExecuteSpecification.Variable is { } returnStatusVariable)
        {
            yield return returnStatusVariable.Name;
        }

        if (node.ExecuteSpecification.ExecutableEntity is ExecutableProcedureReference { Parameters: { } parameters })
        {
            foreach (var parameter in parameters)
            {
                if (parameter is { IsOutput: true, ParameterValue: VariableReference outputVariable })
                {
                    yield return outputVariable.Name;
                }
            }

            yield break;
        }

        var collector = new ReferencedVariableCollector();
        node.Accept(collector);
        foreach (var name in collector.Names)
        {
            yield return name;
        }
    }

    /// <summary>
    /// Matches this exact EXEC call site to its own <see cref="ProcCallGraph"/> edge and seeds
    /// any OUTPUT argument whose callee formal parameter has a known
    /// <see cref="ProcedureOutputSummary"/> (see <see cref="TransferContext.OutputSummaryIndex"/>'s
    /// own doc comment - the caller's fixed-point loop across the whole scan feeds this forward)
    /// instead of leaving it for the blanket taint every other OUTPUT/return-value argument on
    /// this same call still needs. Returns the set of caller variable names seeded, so
    /// <see cref="TaintReferencedVariables"/> can exclude them - the one case this scanner CAN see
    /// through what an arbitrary called procedure does internally.
    /// </summary>
    private static HashSet<string> SeedKnownOutputArguments(ExecuteStatement node, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        var seeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (context.CallGraph is null || context.OutputSummaryIndex is null)
        {
            return seeded;
        }

        var edge = context.CallGraph.EdgeAt(context.Span(node));
        if (edge is null)
        {
            return seeded;
        }

        var span = context.Span(node);
        foreach (var argument in edge.Arguments)
        {
            if (!argument.FormalParameterIsOutput
                || argument.CallerVariableName is not { } callerVariable
                || !context.OutputSummaryIndex.TryGetValue((edge.CalleeQualifiedName, argument.FormalParameterName), out var values))
            {
                continue;
            }

            SqlTextValue combined = new SqlTextValue.Template([new TemplatePiece.Lit(values[0], span, PrefixLength: 0)]);
            foreach (var value in values.Skip(1))
            {
                combined = SqlTextValue.Join(combined, new SqlTextValue.Template([new TemplatePiece.Lit(value, span, PrefixLength: 0)]), guardText: string.Empty, context.Cap, span);
            }

            state[callerVariable] = combined;
            seeded.Add(callerVariable);
        }

        return seeded;
    }

    private sealed class ReferencedVariableCollector : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(VariableReference node) => Names.Add(node.Name);
    }

    private static DynamicSqlFinding Unanalyzable(ExecuteStatement node, TransferContext context, string reason)
    {
        var span = context.Span(node);
        return new DynamicSqlFinding(span.SourcePath, span.Line, span.Column, DynamicSqlOutcome.Unanalyzable, reason);
    }

    private static SourceSpan CallSite(ExecuteStatement node, TransferContext context) => context.Span(node);

    private sealed class SelectSetVariableCollector : TSqlFragmentVisitor
    {
        public List<string> Names { get; } = [];

        public override void Visit(SelectSetVariable node) => Names.Add(node.Variable.Name);
    }

    /// <summary>
    /// The safe default for any statement kind not explicitly modeled above: every variable this
    /// statement could possibly WRITE (never a read - reading cannot change a value) degrades to
    /// a typed <see cref="HoleKind.HavocWrite"/> hole when its declared type is known, or
    /// <see cref="SqlTextValue.Tainted"/> otherwise. Sound by construction: a NEW T-SQL statement
    /// kind ScriptDOM adds tomorrow, or one this rebuild simply hasn't modeled precisely yet, is
    /// conservative automatically rather than silently mis-tracking a value it could not see.
    /// </summary>
    private static Action<Dictionary<string, SqlTextValue>, bool> CompileHavocDefault(TSqlStatement statement, TransferContext context)
    {
        var collector = new WrittenVariableCollector();
        statement.Accept(collector);
        if (collector.Names.Count == 0)
        {
            return static (_, _) => { };
        }

        var span = context.Span(statement);
        return (state, _) =>
        {
            foreach (var name in collector.Names)
            {
                state[name] = HavocOrTaint("unsupported-statement-in-scope", span, context.DeclaredTypes.GetValueOrDefault(name));
            }
        };
    }

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
}
