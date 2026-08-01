using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;

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

    /// <summary>A column resolved (however many view layers deep) to a real base table column.</summary>
    public sealed record Column(string TableQualifiedName, string ColumnName, SqlType? Type, bool Indexed, int Depth, ColumnProvenance Provenance) : PredicateOperand;

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
    /// </summary>
    public sealed record Value(SqlType? Type, bool IsLiteral = false, string? LiteralText = null) : PredicateOperand;
}
