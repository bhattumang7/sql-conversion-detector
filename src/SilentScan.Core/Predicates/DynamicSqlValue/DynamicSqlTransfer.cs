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
    DatabaseCatalog? Catalog = null)
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
            if (argument is null || argument.FormalParameterIsOutput)
            {
                // No matching actual argument (a default value applies) or an OUTPUT parameter
                // (flows the other direction) - nothing to seed, left unseeded.
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
        var cfg = new DynamicSqlCfg(context.SourcePath, context.Cap, (s, activeGuards) => CompileLeaf(s, activeGuards, nestedContext));
        cfg.Solve(trigger.StatementList!.Statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));
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

            var folded = ExpressionEvaluator.Fold(element.Value, state, context.SourcePath, context.Cap);
            state[name] = folded with { DeclaredType = declaredType };
        }
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

        var rhs = ExpressionEvaluator.Fold(expression, state, context.SourcePath, context.Cap);

        if (kind == AssignmentKind.AddEquals)
        {
            var existing = state.TryGetValue(name, out var existingValue) ? existingValue : HavocOrTaint("variable-not-in-scope", span, declaredType);
            state[name] = SqlTextValue.Concat(existing, rhs) with { DeclaredType = declaredType };
            return;
        }

        state[name] = rhs with { DeclaredType = declaredType };
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
            if (!TryCompileSelectAssignmentFromSingleKnownTable(select, context, state))
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

        var declaredType = context.DeclaredTypes.GetValueOrDefault(setVar.Variable.Name);
        state[setVar.Variable.Name] = spliced with { DeclaredType = declaredType };
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
            combined = SqlTextValue.Concat(combined, ExpressionEvaluator.Fold(element, state, context.SourcePath, context.Cap));
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

        var query = ExpressionEvaluator.Fold(statementArg, state, context.SourcePath, context.Cap);
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

        var folded = ExpressionEvaluator.Fold(paramsArg, state, context.SourcePath, context.Cap);
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

        var assemblies = SqlTextValue.Expand((SqlTextValue.Template)widened, context.Cap);

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
    /// OUTPUT, plus any other variable the call site happens to mention - this scanner does not
    /// model what an arbitrary called procedure does internally, so any variable named in the
    /// call could come back holding something other than what was folded for it. Scoped to the
    /// variables THIS call actually mentions (T-SQL locals have no aliasing) rather than every
    /// tracked variable - one this call never references cannot have been mutated by it.
    /// </summary>
    private static void TaintReferencedVariables(ExecuteStatement node, TransferContext context, Dictionary<string, SqlTextValue> state)
    {
        var seeded = SeedKnownOutputArguments(node, context, state);
        var span = context.Span(node);
        var collector = new ReferencedVariableCollector();
        node.Accept(collector);
        foreach (var name in collector.Names.Where(n => state.ContainsKey(n) && !seeded.Contains(n)))
        {
            state[name] = HavocOrTaint("unsupported-execute-form", span, context.DeclaredTypes.GetValueOrDefault(name));
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
