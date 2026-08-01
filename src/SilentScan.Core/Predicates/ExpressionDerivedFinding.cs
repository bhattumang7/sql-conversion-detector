using SilentScan.Core.Lineage;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A predicate compares a column that isn't really a column by the time the query sees it -
/// somewhere between here and the base table, a CAST/CONVERT or other expression (in this
/// statement's own derived table, or baked into an upstream view/TVF's SELECT list) replaced
/// it with a computed value. No comparison against a computed expression can use an index
/// seek, regardless of what type it lands on or what it's compared against - this is why it's
/// reported separately from <see cref="Rules.Verdict"/>, which is about type-precedence
/// mismatches between two otherwise-real operands, not this.
/// </summary>
public sealed record ExpressionDerivedFinding(
    string ColumnName,
    string SourcePath,
    int Line,
    int ColumnPosition,
    IReadOnlyList<TransformationSite> TransformationChain,
    IReadOnlyList<UnderlyingBaseColumn> UnderlyingBaseColumns);

/// <summary>A real base table column found underneath an expression-derived provenance chain, and whether it's indexed (which is what makes the finding worth fixing).</summary>
public sealed record UnderlyingBaseColumn(string TableQualifiedName, string ColumnName, bool Indexed);
