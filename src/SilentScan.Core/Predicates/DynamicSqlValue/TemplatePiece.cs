using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

public abstract record TemplatePiece
{
    public sealed record Lit(string Text, SourceSpan Origin, int PrefixLength) : TemplatePiece;

    public sealed record Hole(SqlType Type, SourceSpan Origin, HoleKind Kind) : TemplatePiece;

    public sealed record Choice(int GuardId, IReadOnlyList<SqlTextValue.Template> Alternatives) : TemplatePiece;
}

public abstract record FlatPiece
{
    public sealed record Lit(string Text, SourceSpan Origin, int PrefixLength) : FlatPiece;

    public sealed record Hole(SqlType Type, SourceSpan Origin, HoleKind Kind) : FlatPiece;

    public static FlatPiece From(TemplatePiece piece) => piece switch
    {
        TemplatePiece.Lit l => new Lit(l.Text, l.Origin, l.PrefixLength),
        TemplatePiece.Hole h => new Hole(h.Type, h.Origin, h.Kind),
        TemplatePiece.Choice => throw new InvalidOperationException(
            "A Choice piece must be resolved by SqlTextValue.Expand before flattening - this indicates Expand has a bug, not caller error."),
        _ => throw new InvalidOperationException($"Unhandled {nameof(TemplatePiece)} subtype: {piece.GetType().Name}"),
    };
}
