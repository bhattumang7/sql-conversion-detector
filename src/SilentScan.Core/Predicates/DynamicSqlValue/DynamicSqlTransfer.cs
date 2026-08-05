using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>Everything a leaf compiler needs besides the statement itself and the running state - bundled so per-statement compile methods stay under the parameter-count gate rather than threading three loose values through every call.</summary>
public sealed record TransferContext(Dictionary<string, SqlType> DeclaredTypes, string SourcePath, int Cap)
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
        _ => CompileHavocDefault(statement, context),
    };

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
