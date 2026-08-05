using SilentScan.Core.Predicates;

namespace SilentScan.Core.Predicates.DynamicSqlValue;

/// <summary>
/// One rendered assembly, ready to reparse: the assembled text, the map back to real source
/// coordinates (built here and ONLY here - no other component constructs a
/// <see cref="DynamicSqlSegmentMap"/>), and the occurrences <see cref="DynamicSqlPipeline"/> needs
/// to classify a hole's syntactic position without re-parsing <see cref="InnerText"/> to find them.
/// </summary>
public sealed record RenderedScript(
    string InnerText,
    DynamicSqlSegmentMap SegmentMap,
    IReadOnlyList<PlaceholderOccurrence> Placeholders);

/// <summary>
/// Turns one <see cref="SqlTextValue.Expand"/>ed assembly into real, reparseable T-SQL text. The
/// single place that decides how a <see cref="FlatPiece.Hole"/> becomes text and the single place
/// that builds a <see cref="DynamicSqlSegmentMap"/> - replaces the old scanner's split-brain
/// design where <c>DynamicSqlSegmentMap</c> was built during folding but a SEPARATE component
/// (<c>NeutralElisionVariant</c>) re-derived its own line/column arithmetic on top for the
/// parse-failure fallback. See docs/dynamic-sql-rebuild-plan.md §2.
/// </summary>
public static class TemplateRenderer
{
    /// <summary>
    /// Renders every <see cref="FlatPiece.Lit"/> verbatim and every <see cref="FlatPiece.Hole"/>
    /// as its identifier-shaped <c>__silentscan_sym_LxCy__</c> token - EXCEPT
    /// <see cref="HoleKind.OptionalFragment"/>, which renders as a single space up front: no
    /// identifier-shaped token could ever sit legally in that grammar position, so there is no
    /// reason to attempt the token first and fall back only after a parse failure (unlike the old
    /// scanner's <c>NeutralElisionVariant</c>, which only ever ran reactively, after
    /// <c>TryParseAndClassify</c> observed a parse error).
    /// </summary>
    public static RenderedScript Render(IReadOnlyList<FlatPiece> assembly) => RenderCore(assembly, elideAllHoles: false);

    /// <summary>
    /// The fallback when <see cref="Render"/>'s token-rendered text fails to parse: every hole,
    /// regardless of <see cref="HoleKind"/>, renders as a single space instead of its token. A
    /// space can never fuse two adjacent literal fragments into a token that wasn't there in
    /// either the real runtime query or this elided one (T-SQL treats whitespace as a pure token
    /// separator everywhere outside a quoted literal/identifier), so extraction against the
    /// result can only ever under-report relative to the true runtime query, never fabricate a
    /// finding that depends on the elided content.
    /// </summary>
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

    /// <summary>Same token shape the old scanner used (<c>__silentscan_sym_LxCy__</c>) - an identifier ScriptDOM will accept anywhere a real identifier/value could sit, and that can never collide with a real deployed object name.</summary>
    private static string PlaceholderToken(int line, int column) => $"__silentscan_sym_L{line}C{column}__";
}
