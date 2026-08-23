using SilentScan.Core.Predicates;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

public sealed record RenderedScript(
    string InnerText,
    DynamicSqlSegmentMap SegmentMap,
    IReadOnlyList<PlaceholderOccurrence> Placeholders);

public static class TemplateRenderer
{
public static RenderedScript Render(IReadOnlyList<FlatPiece> assembly) => RenderCore(assembly, elideAllHoles: false);

public static RenderedScript RenderElided(IReadOnlyList<FlatPiece> assembly) => RenderCore(assembly, elideAllHoles: true);

    private static RenderedScript RenderCore(IReadOnlyList<FlatPiece> assembly, bool elideAllHoles)
    {
        var map = new DynamicSqlSegmentMap();
        var placeholders = new List<PlaceholderOccurrence>();

        foreach (var piece in assembly)
        {
            switch (piece)
            {
                case FlatPiece.Lit lit:
                    map.AppendLiteral(lit.Origin.SourcePath, lit.Origin.Line, lit.Origin.Column, lit.PrefixLength, lit.Text);
                    break;

                case FlatPiece.Hole hole:
                    var renderAsSpace = elideAllHoles || hole.Kind == HoleKind.OptionalFragment;
                    var token = renderAsSpace ? " " : PlaceholderToken(hole.Origin.Line, hole.Origin.Column);
                    var innerStart = map.AppendPlaceholder(hole.Origin.SourcePath, hole.Origin.Line, hole.Origin.Column, token);
                    if (!renderAsSpace)
                    {
                        placeholders.Add(new PlaceholderOccurrence(innerStart, token.Length, hole.Type, hole.Origin));
                    }

                    break;
            }
        }

        return new RenderedScript(map.InnerText, map, placeholders);
    }

private static string PlaceholderToken(int line, int column) => $"__silentscan_sym_L{line}C{column}__";
}
