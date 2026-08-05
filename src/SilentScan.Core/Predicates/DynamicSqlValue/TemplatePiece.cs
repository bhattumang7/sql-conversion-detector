using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>
/// One piece of a <see cref="SqlTextValue.Template"/>, in order. Immutable by construction (a
/// record hierarchy) - every operation in <see cref="SqlTextValue"/> returns a new value rather
/// than mutating one in place.
/// </summary>
public abstract record TemplatePiece
{
    /// <summary>
    /// Real source text, already decoded exactly like <see cref="DynamicSqlSegmentMap.AppendLiteral"/>
    /// expects (quotes unescaped). <paramref name="PrefixLength"/> is 0 for a piece that isn't
    /// itself a literal token's own content (e.g. a builtin's computed result still attributed to
    /// one source span) - only a genuine literal's opening <c>'</c>/<c>N'</c> carries 1 or 2.
    /// </summary>
    public sealed record Lit(string Text, SourceSpan Origin, int PrefixLength) : TemplatePiece;

    /// <summary>
    /// A value with a known <see cref="Catalog.SqlType"/> but unknown content - never an untyped
    /// unknown; if no type can be proven, the containing value becomes
    /// <see cref="SqlTextValue.Tainted"/> instead of a Hole. This mirrors
    /// <see cref="PlaceholderOccurrence"/>'s own non-nullable <c>Type</c>: only a typed unknown
    /// ever survives to become a placeholder in rendered text.
    /// </summary>
    public sealed record Hole(SqlType Type, SourceSpan Origin, HoleKind Kind) : TemplatePiece;

    /// <summary>
    /// Provable branch divergence, kept lazy (never expanded to concrete assemblies) until
    /// <see cref="SqlTextValue.Expand"/> runs. <paramref name="GuardText"/> is the canonical
    /// rendering of the IF predicate that produced this divergence - empty when produced by an
    /// ordinary control-flow join with no single controlling predicate (a loop back-edge, a
    /// TRY/CATCH boundary) - and is how a LATER <c>IF</c> testing the exact same predicate text
    /// re-correlates with an EARLIER one instead of nesting a redundant Choice inside a Choice.
    /// </summary>
    public sealed record Choice(string GuardText, IReadOnlyList<SqlTextValue.Template> Alternatives) : TemplatePiece;
}

/// <summary>
/// One piece of a fully-<see cref="SqlTextValue.Expand"/>ed assembly - a <see cref="TemplatePiece"/>
/// with every <see cref="TemplatePiece.Choice"/> already resolved to one alternative. What
/// <see cref="TemplateRenderer"/> actually renders.
/// </summary>
public abstract record FlatPiece
{
    public sealed record Lit(string Text, SourceSpan Origin, int PrefixLength) : FlatPiece;

    public sealed record Hole(SqlType Type, SourceSpan Origin, HoleKind Kind) : FlatPiece;

    /// <summary>Never called with a <see cref="TemplatePiece.Choice"/> - <see cref="SqlTextValue.Expand"/> resolves every Choice to one alternative's own pieces before this runs.</summary>
    public static FlatPiece From(TemplatePiece piece) => piece switch
    {
        TemplatePiece.Lit l => new Lit(l.Text, l.Origin, l.PrefixLength),
        TemplatePiece.Hole h => new Hole(h.Type, h.Origin, h.Kind),
        TemplatePiece.Choice => throw new InvalidOperationException(
            "A Choice piece must be resolved by SqlTextValue.Expand before flattening - this indicates Expand has a bug, not caller error."),
        _ => throw new InvalidOperationException($"Unhandled {nameof(TemplatePiece)} subtype: {piece.GetType().Name}"),
    };
}
