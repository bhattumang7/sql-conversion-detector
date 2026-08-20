using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-reference.md Appendix 8 "A CREATE INDEX with no ON clause on a partitioned
/// table auto-aligns itself" section's sibling entry - each member is one
/// <c>sys.dm_xe_map_values('forced_param_clause_skipped_reason_enum')</c> reason, oracle-confirmed
/// (2026-08-20) against the standing Docker instance rather than taken on the DMV's name: under
/// <c>ALTER DATABASE ... SET PARAMETERIZATION FORCED</c>, the engine still parameterizes the rest
/// of a statement (a plain equality predicate elsewhere in the same statement compiles to a real
/// shared <c>(@0 int)</c> plan) but leaves a literal sitting in one of these specific clause shapes
/// untouched - confirmed by inspecting the real cached PREPARED plan text after each probe
/// statement, not by reading the DMV's name alone. Consequence: a database explicitly configured
/// to stop per-literal ad-hoc plan-cache bloat still gets a fresh compiled plan per distinct
/// literal for exactly these shapes - silently defeating the setting for the app's own most
/// naturally-varying values (a search-box LIKE pattern, a pagination TOP/OFFSET count) while every
/// other predicate in the same query correctly shares one plan.
/// </summary>
public enum ForcedParameterizationFindingKind
{
    /// <summary>A <c>LIKE</c> predicate's pattern is a literal string, not a variable/parameter.
    /// Confirmed directly: under FORCED parameterization, <c>WHERE Id = 42 AND Name LIKE 'abc%'</c>
    /// compiles to <c>(@0 int) ... WHERE Id = @0 AND Name LIKE 'abc%'</c> - the equality
    /// parameterizes, the LIKE pattern does not. A workload varying only the search pattern (the
    /// overwhelmingly common real case) gets one fresh compile per distinct pattern regardless.</summary>
    LikePatternLiteral,

    /// <summary>A <c>TOP</c> row count, or an <c>OFFSET</c>/<c>FETCH NEXT</c> row count, is a
    /// literal. Confirmed directly: <c>OFFSET 5 ROWS FETCH NEXT 3 ROWS ONLY</c> and <c>TOP 5</c>
    /// both stay literal in the cached prepared plan while an unrelated equality predicate in the
    /// same statement parameterizes normally - a paginated query varying only its page size gets a
    /// fresh compile per distinct page size.</summary>
    TopOrPagingLiteral,

    /// <summary>A literal sits directly in the SELECT list (e.g. a literal tag/label column).
    /// Confirmed directly: <c>SELECT 'MarkerSelectList', Id FROM T WHERE Id = 1</c> keeps the
    /// select-list literal untouched while the WHERE-clause equality parameterizes.</summary>
    SelectListLiteral,

    /// <summary>A literal is a direct comparison operand inside a <c>HAVING</c> clause. Confirmed
    /// directly: <c>... GROUP BY Id HAVING COUNT(*) > 2</c> keeps the <c>2</c> literal while the
    /// statement's own WHERE-clause equality parameterizes.</summary>
    HavingLiteral,

    /// <summary>A literal appears inside a compound (non-bare) <c>ORDER BY</c> expression - e.g.
    /// <c>ORDER BY (Id + 100)</c>, confirmed directly to keep <c>100</c> literal. Deliberately
    /// excludes a BARE literal as the entire ORDER BY element (<c>ORDER BY 1</c>, the common
    /// ordinal-position idiom) - untested, structurally different (a small, finite, rarely-varying
    /// set of values), and not the shape this kind's own oracle probe confirmed.</summary>
    OrderByExpressionLiteral,

    /// <summary>A literal argument to a <c>TypeName::Method(...)</c> static call (CLR UDT/spatial
    /// types - <c>geography::Parse('POINT(1 1)')</c> and similar). Confirmed directly: the string
    /// argument stays literal while an unrelated literal-vs-literal comparison in the same
    /// statement still parameterizes (as two separate params, not one folded value - see <see
    /// cref="ConstantFoldableExpressionLiteral"/> for that distinct, milder behavior).</summary>
    DoubleColonCallArgumentLiteral,

    /// <summary>A <c>TABLESAMPLE</c> clause's sample size (percentage or row count) is a literal.
    /// Confirmed directly: <c>TABLESAMPLE (10 PERCENT)</c> keeps <c>10</c> literal while an
    /// unrelated equality predicate in the same statement parameterizes normally.</summary>
    TableSampleSizeLiteral,

    /// <summary>A literal sits in a DML statement's own <c>OUTPUT</c> clause select list (e.g. a
    /// literal tag column alongside <c>inserted.Id</c>). Confirmed directly: an
    /// <c>INSERT ... OUTPUT inserted.Id, 'tag' VALUES (...)</c> keeps the OUTPUT-list literal
    /// untouched while the VALUES clause's own literals parameterize normally.</summary>
    DmlOutputListLiteral,

    /// <summary>A <c>CONVERT(type, expr, style)</c> call's style-code argument is a literal.
    /// Confirmed directly: <c>CONVERT(varchar, GETDATE(), 101)</c> keeps <c>101</c> literal.
    /// </summary>
    ConvertStyleCodeLiteral,

    /// <summary>A literal is passed directly as an argument to <c>CHECKSUM(...)</c>. Confirmed
    /// directly: <c>CHECKSUM('literal')</c> keeps the argument literal while an unrelated
    /// literal-vs-literal comparison in the same statement still parameterizes.</summary>
    CheckSumArgumentLiteral,

    /// <summary>A constant-foldable arithmetic expression against a column (e.g.
    /// <c>WHERE Id = 1 + 1008</c>) parameterizes as TWO separate parameters instead of one folded
    /// value - confirmed directly (<c>WHERE [Id]=(@1+@2)</c>, not the single <c>@0</c> a plain
    /// <c>Id = 1009</c> equality would produce). A real, distinct, milder effect from the other
    /// members of this enum: the statement IS still parameterized (no per-literal recompile), just
    /// less optimally folded - shipped at <see cref="Predicates.FindingConfidence.Low"/> as an
    /// informational curiosity, not a plan-cache-bloat claim.</summary>
    ConstantFoldableExpressionLiteral,

    /// <summary>A literal appears inside a <c>GROUP BY</c> expression - e.g.
    /// <c>GROUP BY (Id + 1)</c>, confirmed directly to keep the <c>1</c> literal in the cached
    /// plan while an unrelated <c>WHERE</c>-clause equality in the same statement parameterizes
    /// normally. Unlike <see cref="OrderByExpressionLiteral"/>, there is no bare-literal ordinal
    /// idiom to exclude: <c>GROUP BY 1</c> is not a valid ordinal reference in T-SQL at all (the
    /// engine rejects it, "Each GROUP BY expression must contain at least one column that is not
    /// an outer reference") - every literal-bearing GROUP BY expression is this one shape.</summary>
    GroupByExpressionLiteral,
}

/// <summary>
/// One finding type, one <see cref="Kind"/> discriminator - this codebase's established
/// shared-plumbing shape (<see cref="NamingFinding"/>/<see cref="ControlFlowRiskFinding"/>).
/// Syntax-only (no catalog, no oracle re-verification per finding - the mechanism itself was
/// oracle-confirmed once, per <see cref="ForcedParameterizationFindingKind"/>'s own doc comment),
/// but live-mode only by construction: every member is only meaningful when the target database
/// actually has <c>sys.databases.is_parameterization_forced = 1</c> (read once per scan by
/// live-only code in <c>SilentScan.Live</c>, never by this AST-only scanner itself, which has no
/// live catalog access of its own) - a file-mode scan has no way to
/// know that flag's real state, so this stream is always empty outside <c>SilentScan.Live</c>,
/// same live-only-merge pattern <see cref="Predicates.IndexDesignFinding"/> already established.
///
/// Confidence: <see cref="FindingConfidence.High"/> for every member except
/// <see cref="ForcedParameterizationFindingKind.ConstantFoldableExpressionLiteral"/> (<see
/// cref="FindingConfidence.Low"/> - a real but much milder effect, see that kind's own doc
/// comment) - every other member is a deterministic AST-shape fact whose consequence (a fresh
/// compile per distinct literal, defeating the very setting meant to prevent that) was directly
/// oracle-confirmed, not estimated.
///
/// Engine-version sensitivity: confirmed only against SQL Server 2022 CU23 (16.0.4236.2, the
/// standing local Docker instance); forced parameterization itself has existed since SQL Server
/// 2005 and these clause exclusions are long-standing, but no cross-version sweep was run - stated
/// here rather than assumed stable.
/// </summary>
public sealed record ForcedParameterizationFinding(
    ForcedParameterizationFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}
