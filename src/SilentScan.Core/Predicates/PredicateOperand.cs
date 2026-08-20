using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

/// <summary>One side of a `colRef &lt;op&gt; other` comparison, typed for the verdict engine (CLAUDE.md Pass 3).</summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(Column), "Column")]
[JsonDerivedType(typeof(Value), "Value")]
public abstract record PredicateOperand
{
    private PredicateOperand()
    {
    }

    /// <summary>
    /// A column resolved (however many view layers deep) to a real base table column.
    /// <paramref name="TableQualifiedName"/>/<paramref name="ColumnName"/> always name the
    /// ultimate physical table, even when <paramref name="Depth"/> is nonzero.
    /// <paramref name="ImmediateRelationQualifiedName"/>/<paramref name="ImmediateColumnName"/>
    /// instead name the object literally referenced in the predicate's own FROM clause - the
    /// same thing when Depth is 0, a view/TVF's own name and exposed column name when it's not.
    /// Null when the predicate reads a base table/CTE/derived table directly (Depth 0 - nothing
    /// to route differently) or when a real view/TVF layer's own qualified name wasn't resolvable.
    /// The Verify oracle uses these to compile a probe against what the source actually queried,
    /// rather than always querying the base table directly and silently skipping the view layer
    /// a depth&gt;=1 finding claims to be inherited through. <paramref name="IndexName"/> is the
    /// name of the specific index whose leading key column is this one (<see
    /// cref="Catalog.CatalogTable.FindIndexedColumn"/>), when <paramref name="Indexed"/> is true -
    /// null when the index itself is unnamed (SQL Server allows an unnamed inline
    /// <c>PRIMARY KEY</c>/<c>UNIQUE</c> constraint to synthesize its own system name that this
    /// tool never sees) or, naturally, whenever <paramref name="Indexed"/> is false.
    /// </summary>
    public sealed record Column(
        string TableQualifiedName,
        string ColumnName,
        SqlType? Type,
        bool? Indexed,
        int Depth,
        ColumnProvenance Provenance,
        string? ImmediateRelationQualifiedName = null,
        string? ImmediateColumnName = null,
        string? IndexName = null) : PredicateOperand;

    /// <summary>
    /// A literal, parameter/variable, or non-column expression - typed if we could, untyped
    /// (null) if not. <paramref name="LiteralText"/> is populated only when this Value came
    /// from an actual source-code literal AND that literal could be rendered back to valid SQL
    /// text (docs/audit-remediation-plan.md Phase 5.2) - it lets an oracle probe reconstruct
    /// the original literal comparison exactly, rather than substitute a same-typed variable the
    /// optimizer can constant-fold differently. <paramref name="IsLiteral"/> is true whenever
    /// the source operand was a literal at all, even if LiteralText ended up null (a literal
    /// kind the renderer doesn't cover) - callers that need probe fidelity distinguish "no
    /// literal to begin with" (a parameter/variable; probing with one is already exactly
    /// equivalent) from "was a literal, couldn't render it" (probing with a variable would be a
    /// silent fidelity loss) using this flag, not LiteralText's nullability alone.
    /// <paramref name="VariableName"/>/<paramref name="IsFormalParameter"/> are populated only
    /// when this Value came from a genuine <c>VariableReference</c> (docs/detection-checklist.md
    /// Tier 2 "Local-variable predicates") - null/false for every other source (literal,
    /// subquery, function call, ...). <paramref name="IsFormalParameter"/> distinguishes a real
    /// <c>CREATE PROCEDURE</c>/function parameter (or an <c>sp_executesql</c> parameter) from a
    /// plain <c>DECLARE</c>d local - only the latter is invisible to the cardinality estimator's
    /// parameter-sniffing path.
    /// </summary>
    public sealed record Value(
        SqlType? Type, bool IsLiteral = false, string? LiteralText = null,
        string? VariableName = null, bool IsFormalParameter = false) : PredicateOperand;
}
