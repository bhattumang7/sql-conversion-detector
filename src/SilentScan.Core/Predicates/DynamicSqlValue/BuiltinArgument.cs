using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>
/// One already-resolved builtin call argument - resolved by WHATEVER walks the expression tree
/// (a string sub-expression's own fold, or an integer sub-expression's own fold, done before
/// <see cref="BuiltinRegistry"/> is ever consulted; see docs/dynamic-sql-rebuild-plan.md Phase 3
/// for where that walk lives). <see cref="BuiltinRegistry"/> itself never parses T-SQL and never
/// decides how an argument resolves - it only pattern-matches the RESULT, exactly like the old
/// scanner's <c>TryFold*</c> methods pattern-matched ScriptDOM node shapes, just one abstraction
/// level higher.
/// </summary>
public abstract record BuiltinArgument
{
    /// <summary>A string argument that folded to a known, concrete value.</summary>
    public sealed record Text(string Value) : BuiltinArgument;

    /// <summary>An integer argument that folded to a known, concrete value (a length/start/code-point argument - LEFT/RIGHT/SUBSTRING/STR/CHAR/NCHAR).</summary>
    public sealed record Number(int Value) : BuiltinArgument;

    /// <summary>A typed-but-unknown value - <paramref name="Kind"/> is carried through a passthrough transfer (UPPER/LTRIM/SUBSTRING/...) unchanged, so the ORIGINAL reason this became a hole survives however many builtins it folds through.</summary>
    public sealed record Hole(SqlType Type, HoleKind Kind) : BuiltinArgument;

    /// <summary>
    /// Could not resolve to EITHER a concrete value or a typed hole - the containing call
    /// declines with this reason, unless its own spec can still use <paramref name="Type"/> (the
    /// ORIGINATING <see cref="Catalog.SqlType"/>-typed variable's own declared type, when the
    /// unresolved value came from one - e.g. a mixed literal+hole template built from a variable
    /// DECLAREd VARCHAR(200), or a Tainted value carrying its own <c>DeclaredType</c>). A builtin
    /// whose result type only depends on the source's TYPE, never its content (CAST/CONVERT's
    /// syntax-pinned target already gets this via a different path; LEFT/RIGHT/SUBSTRING/REPLICATE's
    /// own source-type passthrough is the one this actually widens) can still resolve from this
    /// alone. Null when no declared type was ever known for the value this came from - never
    /// guessed from unrelated context.
    /// </summary>
    public sealed record Unresolved(string Reason, SourceSpan Location, SqlType? Type = null) : BuiltinArgument;
}

/// <summary>One builtin invocation, arguments already resolved, ready for <see cref="BuiltinRegistry.Fold"/>.</summary>
public sealed record BuiltinCall(string FunctionName, IReadOnlyList<BuiltinArgument> Arguments, SourceSpan Site);

/// <summary>The result of folding one <see cref="BuiltinCall"/>.</summary>
public abstract record BuiltinFoldResult
{
    public sealed record Ok(IReadOnlyList<TemplatePiece> Pieces) : BuiltinFoldResult;

    public sealed record Fail(string Reason) : BuiltinFoldResult;

    public static Ok OkText(string value, SourceSpan site) => new([new TemplatePiece.Lit(value, site, PrefixLength: 0)]);

    public static Ok OkHole(SqlType type, SourceSpan site, HoleKind kind) => new([new TemplatePiece.Hole(type, site, kind)]);
}
