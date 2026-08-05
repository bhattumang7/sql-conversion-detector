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
    List<ProcedureOutputSummary> OutputSummaries)
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
    /// <summary>Compiles one statement into a <see cref="DynamicSqlCfg"/> leaf step. <paramref name="context"/>.DeclaredTypes tracks a variable's own declared type separately from its current fold state, so a later havoc/taint can still recover it.</summary>
    public static Action<Dictionary<string, SqlTextValue>, bool> CompileLeaf(TSqlStatement statement, TransferContext context) => statement switch
    {
        DeclareVariableStatement declare => (state, _) => CompileDeclare(declare, context, state),
        SetVariableStatement set => (state, _) => CompileAssignment(set.Variable.Name, set.AssignmentKind, set.Expression, set.FunctionCallExists, set, context, state),
        SelectStatement select => (state, _) => CompileSelectAssignment(select, context, state),
        ExecuteStatement exec => (state, emit) => CompileExecute(exec, context, state, emit),
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
    /// Formal-parameter seeding from a caller's own call-graph edge (the old scanner's
    /// <c>BuildParameterSeed</c>) is deferred - unseeded, a parameter reference simply reports
    /// "variable-not-in-scope" exactly like today's behavior whenever no call graph is supplied.
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

        var cfg = new DynamicSqlCfg(context.SourcePath, context.Cap, s => CompileLeaf(s, nestedContext));
        var folded = cfg.Solve(procOrFunc.StatementList!.Statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        if (qualifiedName is not null && formalParameters is { Count: > 0 })
        {
            RecordOutputParameterSummaries(qualifiedName, formalParameters, folded, nestedContext);
        }
    }

    private static void CompileTriggerBody(TriggerStatementBody trigger, TransferContext context, bool emit)
    {
        if (!emit)
        {
            return;
        }

        var nestedScope = new DynamicSqlScope(SchemaObjectNameHelper.Qualify(trigger.Name), trigger.TriggerObject.Name);
        var nestedContext = context with { Scope = nestedScope, DeclaredTypes = new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase) };
        var cfg = new DynamicSqlCfg(context.SourcePath, context.Cap, s => CompileLeaf(s, nestedContext));
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
            state[name] = new SqlTextValue.Tainted("unsupported-assignment", span) { DeclaredType = declaredType };
            return;
        }

        // SetVariableStatement.Expression is null when the RHS is a shape ScriptDOM models in a
        // sibling property instead (SET @c = CURSOR FOR ..., SET @x = <identifier>, ...) - none
        // fold, so this taints rather than risking a null-reference fold below.
        if (expression is null)
        {
            state[name] = new SqlTextValue.Tainted("unsupported-assignment", span) { DeclaredType = declaredType };
            return;
        }

        var rhs = ExpressionEvaluator.Fold(expression, state, context.SourcePath, context.Cap);

        if (kind == AssignmentKind.AddEquals)
        {
            var existing = state.TryGetValue(name, out var existingValue) ? existingValue : new SqlTextValue.Tainted("variable-not-in-scope", span);
            state[name] = SqlTextValue.Concat(existing, rhs) with { DeclaredType = declaredType };
            return;
        }

        state[name] = rhs with { DeclaredType = declaredType };
    }

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
            foreach (var name in assignedNames.Names)
            {
                state[name] = new SqlTextValue.Tainted("select-assignment-not-pure", span) { DeclaredType = context.DeclaredTypes.GetValueOrDefault(name) };
            }

            return;
        }

        foreach (var element in spec.SelectElements.Cast<SelectSetVariable>())
        {
            CompileAssignment(element.Variable.Name, element.AssignmentKind, element.Expression, functionCallExists: false, select, context, state);
        }
    }

    /// <summary>
    /// EXEC('...')/EXEC(@sql), sp_executesql, and every other EXEC form. The first two produce
    /// this call site's own <see cref="DynamicSqlScript"/>(s)/<see cref="DynamicSqlFinding"/> -
    /// pure functions of the CURRENT state, so their whole body is skipped during the suppressed
    /// fixpoint rounds (<paramref name="emit"/> false): neither writes any variable another
    /// statement could depend on, so nothing observable is lost by not running them early. Any
    /// OTHER EXEC form (an ordinary stored-procedure call) has no such guarantee - this scanner
    /// cannot see what an arbitrary called procedure does internally, so every variable the call
    /// site itself mentions is tainted, unconditionally and on every round (state mutation, not
    /// emission) - deferred: seeding a caller's own OUTPUT/return-value variable from a callee's
    /// own already-proven-constant OUTPUT parameter (the old scanner's cross-procedure
    /// ProcCallGraph/ProcedureOutputSummary machinery) is a precision improvement for a later
    /// increment, never a soundness requirement - the blanket taint here is always safe.
    /// </summary>
    private static void CompileExecute(ExecuteStatement node, TransferContext context, Dictionary<string, SqlTextValue> state, bool emit)
    {
        switch (node.ExecuteSpecification.ExecutableEntity)
        {
            case ExecutableStringList stringList:
                if (emit)
                {
                    CompileStringList(stringList, node, context, state);
                }

                break;

            case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var name } procRef
                when string.Equals(name, "sp_executesql", StringComparison.OrdinalIgnoreCase):
                if (emit)
                {
                    CompileSpExecuteSql(procRef, node, context, state);
                }

                break;

            default:
                TaintReferencedVariables(node, context, state);
                break;
        }
    }

    private static void CompileStringList(ExecutableStringList stringList, ExecuteStatement node, TransferContext context, Dictionary<string, SqlTextValue> state)
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

        EmitScriptsOrFinding(combined, node, context, parameterDeclarationText: null, argumentBindings: null);
    }

    /// <summary>Every named execute-parameter beyond @stmt/@params (e.g. <c>@P = @Code</c>) whose value is a bare variable reference - captured unconditionally, since a NESTED dynamic-SQL pass may need it to seed one of ITS OWN parameters from this outer script's own declared type (see <see cref="DynamicSqlScript.ArgumentBindings"/>'s own doc comment).</summary>
    private static readonly HashSet<string> ReservedArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "@stmt", "@statement", "@params", "@parameters",
    };

    private static void CompileSpExecuteSql(ExecutableProcedureReference procRef, ExecuteStatement node, TransferContext context, Dictionary<string, SqlTextValue> state)
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
        EmitScriptsOrFinding(query, node, context, parameterDeclarationText, argumentBindings);
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

    private static void EmitScriptsOrFinding(
        SqlTextValue value, ExecuteStatement node, TransferContext context, string? parameterDeclarationText, IReadOnlyDictionary<string, string>? argumentBindings)
    {
        if (value is SqlTextValue.Tainted tainted)
        {
            context.Findings.Add(Unanalyzable(node, context, tainted.Reason));
            return;
        }

        var site = context.Span(node);
        var widened = SqlTextValue.Widen(value, context.Cap, site);
        if (widened is SqlTextValue.Tainted taintedAfterWiden)
        {
            context.Findings.Add(Unanalyzable(node, context, taintedAfterWiden.Reason));
            return;
        }

        var assemblies = SqlTextValue.Expand((SqlTextValue.Template)widened, context.Cap);
        foreach (var assembly in assemblies)
        {
            var rendered = TemplateRenderer.Render(assembly);
            var confidence = SqlTextValue.ContainsHole(assembly) ? FindingConfidence.Medium : FindingConfidence.High;
            context.Scripts.Add(new DynamicSqlScript(
                CallSite(node, context), rendered.InnerText, rendered.SegmentMap, parameterDeclarationText,
                context.Scope, argumentBindings, confidence,
                rendered.Placeholders.Count > 0 ? rendered.Placeholders : null));
        }
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
        var span = context.Span(node);
        var collector = new ReferencedVariableCollector();
        node.Accept(collector);
        foreach (var name in collector.Names.Where(state.ContainsKey))
        {
            state[name] = new SqlTextValue.Tainted("unsupported-execute-form", span) { DeclaredType = context.DeclaredTypes.GetValueOrDefault(name) };
        }
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
                state[name] = context.DeclaredTypes.TryGetValue(name, out var type)
                    ? new SqlTextValue.Template([new TemplatePiece.Hole(type, span, HoleKind.HavocWrite)]) { DeclaredType = type }
                    : new SqlTextValue.Tainted("unsupported-statement-in-scope", span);
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
