using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Lineage;

/// <summary>
/// Where a view/TVF output column's type ultimately comes from (CLAUDE.md Pass 2):
/// BaseColumn | Expression | Cast | Unknown, plus Union for UNION/UNION ALL branches
/// which must record every branch's provenance rather than collapsing them.
/// </summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(BaseColumn), "BaseColumn")]
[JsonDerivedType(typeof(Declared), "Declared")]
[JsonDerivedType(typeof(Cast), "Cast")]
[JsonDerivedType(typeof(Expression), "Expression")]
[JsonDerivedType(typeof(Union), "Union")]
[JsonDerivedType(typeof(Unknown), "Unknown")]
public abstract record ColumnProvenance
{
    private ColumnProvenance()
    {
    }

    /// <summary>
    /// A direct passthrough of a physical table column, resolved through however many view
    /// layers sit between. Depth counts those layers (0 = the predicate reads the table
    /// directly; N = N views/TVFs sit between) - CLAUDE.md's "depth" finding field.
    /// </summary>
    public sealed record BaseColumn(string TableQualifiedName, string ColumnName, SqlType? Type, int Depth = 0) : ColumnProvenance;

    /// <summary>
    /// A declared type that isn't traced further - e.g. a multi-statement TVF's RETURNS
    /// TABLE(...) column. TableQualifiedName is the TVF's own qualified name (not a real
    /// catalog table - CLAUDE.md never guesses an index for it, so predicates against a
    /// Declared column are always Indexed=false), carried so Pass 3 can still classify a
    /// verdict for it (docs/audit-remediation-plan.md Phase 4.2) rather than treating it as an
    /// untyped, unreportable value the way an Expression with no inferred type is.
    /// </summary>
    public sealed record Declared(SqlType Type, string? TableQualifiedName = null, int Depth = 0) : ColumnProvenance;

    /// <summary>
    /// An explicit CAST/CONVERT to a named type, wrapping <see cref="Inner"/> - the wrapped
    /// expression's own provenance, so a chain of CASTs stacked across several view layers
    /// stays walkable end-to-end instead of going opaque at the first CAST. Origin is where
    /// the CAST itself appears - CLAUDE.md's "origin: file/line of the layer that introduced
    /// the mismatch (e.g., the CAST inside vw_X)" - distinct from the predicate's own location.
    /// </summary>
    public sealed record Cast(SqlType ExplicitType, ColumnProvenance Inner, string? OriginSourcePath = null, int OriginLine = 0, int Depth = 0) : ColumnProvenance;

    /// <summary>
    /// Any other scalar expression (function call, arithmetic, CASE, ...) or a literal.
    /// InferredType is null when we didn't attempt to type it. Inputs holds the provenance of
    /// every column reference found anywhere inside the expression (empty for a literal) -
    /// this is what lets a predicate see past a view's `UPPER(col)` or `col1 + col2` in its
    /// SELECT list down to the real base column(s) underneath.
    /// </summary>
    public sealed record Expression(SqlType? InferredType, IReadOnlyList<ColumnProvenance> Inputs, string? OriginSourcePath = null, int OriginLine = 0, int Depth = 0) : ColumnProvenance;

    /// <summary>
    /// A UNION/UNION ALL/EXCEPT/INTERSECT output column. CLAUDE.md: "record ALL branch
    /// types - the mixed-branch case is itself a finding," so branches are kept, not merged.
    /// </summary>
    public sealed record Union(IReadOnlyList<ColumnProvenance> Branches) : ColumnProvenance;

    /// <summary>Could not be resolved; never guess (CLAUDE.md precision discipline).</summary>
    public sealed record Unknown(string Reason) : ColumnProvenance;
}
