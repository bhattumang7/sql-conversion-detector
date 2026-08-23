using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

public sealed class TemplateRendererTests
{
    private static readonly SqlType NVarChar50 = new(SqlTypeCategory.NVarChar, Length: 50);

    [Fact]
    public void Render_SingleLiteral_MapsContentPositionBackToSource()
    {
        var origin = new SourceSpan("test.sql", 1, 6);
        var assembly = new FlatPiece[] { new FlatPiece.Lit("SELECT 1", origin, PrefixLength: 1) };

        var rendered = TemplateRenderer.Render(assembly);

        Assert.Equal("SELECT 1", rendered.InnerText);
        var span = rendered.SegmentMap.Map(innerLine: 1, innerColumn: 8);
        Assert.Equal("test.sql", span.SourcePath);
        Assert.Equal(1, span.Line);
        Assert.Equal(14, span.Column);
    }

    [Fact]
    public void Render_MultiLineLiteral_ResetsColumnAfterNewline()
    {
        var origin = new SourceSpan("test.sql", 5, 6);
        var text = "SELECT UserId\nFROM dbo.Users";
        var assembly = new FlatPiece[] { new FlatPiece.Lit(text, origin, PrefixLength: 1) };

        var rendered = TemplateRenderer.Render(assembly);

        var span = rendered.SegmentMap.Map(innerLine: 2, innerColumn: 6);
        Assert.Equal(6, span.Line);
        Assert.Equal(6, span.Column);
    }

    [Fact]
    public void Render_EscapedQuoteBeforeTarget_AccountsForRawWidening()
    {
        var origin = new SourceSpan("test.sql", 3, 1);
        const string value = "SELECT Name FROM T WHERE X = 'y' AND Z = 1";
        var assembly = new FlatPiece[] { new FlatPiece.Lit(value, origin, PrefixLength: 1) };

        var rendered = TemplateRenderer.Render(assembly);

        var zIndex = value.IndexOf("Z = 1", StringComparison.Ordinal);
        var span = rendered.SegmentMap.Map(innerLine: 1, innerColumn: zIndex + 1);
        var quotesBeforeTarget = value[..zIndex].Count(c => c == '\'');
        Assert.Equal(3, span.Line);
        Assert.Equal(2 + zIndex + quotesBeforeTarget, span.Column);
    }

    [Fact]
    public void Render_HoleWithIdentifierToken_ProducesPlaceholderOccurrenceAtItsOwnOrigin()
    {
        var literalOrigin = new SourceSpan("test.sql", 1, 6);
        var holeOrigin = new SourceSpan("test.sql", 9, 3);
        var assembly = new FlatPiece[]
        {
            new FlatPiece.Lit("SELECT ", literalOrigin, PrefixLength: 1),
            new FlatPiece.Hole(NVarChar50, holeOrigin, HoleKind.UninitializedDeclare),
        };

        var rendered = TemplateRenderer.Render(assembly);

        var occurrence = Assert.Single(rendered.Placeholders);
        Assert.Equal(NVarChar50, occurrence.Type);
        Assert.Equal(holeOrigin, occurrence.Origin);
        Assert.Equal("SELECT ".Length, occurrence.InnerStartOffset);
        Assert.Contains("__silentscan_sym_L9C3__", rendered.InnerText, StringComparison.Ordinal);

        var middleOfToken = rendered.SegmentMap.Map(innerLine: 1, innerColumn: occurrence.InnerStartOffset + 5);
        Assert.Equal(holeOrigin, middleOfToken);
    }

    [Fact]
    public void Render_OptionalFragmentHole_RendersAsSingleSpaceNotAToken()
    {
        var holeOrigin = new SourceSpan("test.sql", 4, 12);
        var assembly = new FlatPiece[]
        {
            new FlatPiece.Lit("SELECT * FROM T WHERE 1=1", new SourceSpan("test.sql", 1, 6), PrefixLength: 1),
            new FlatPiece.Hole(NVarChar50, holeOrigin, HoleKind.OptionalFragment),
        };

        var rendered = TemplateRenderer.Render(assembly);

        Assert.EndsWith(" ", rendered.InnerText, StringComparison.Ordinal);
        Assert.DoesNotContain("__silentscan_sym_", rendered.InnerText, StringComparison.Ordinal);

        Assert.Empty(rendered.Placeholders);
    }

    [Fact]
    public void RenderElided_EveryHoleKind_RendersAsSpaceRegardlessOfKind()
    {
        var assembly = new FlatPiece[]
        {
            new FlatPiece.Lit("SELECT ", new SourceSpan("test.sql", 1, 6), PrefixLength: 1),
            new FlatPiece.Hole(NVarChar50, new SourceSpan("test.sql", 9, 3), HoleKind.UntypedParameter),
            new FlatPiece.Lit(" FROM T", new SourceSpan("test.sql", 1, 40), PrefixLength: 1),
        };

        var rendered = TemplateRenderer.RenderElided(assembly);

        Assert.Equal("SELECT   FROM T", rendered.InnerText);
        Assert.Empty(rendered.Placeholders);
    }

    [Fact]
    public void Render_LiteralAfterHole_MapsToItsOwnOriginNotTheHoles()
    {
        var firstLiteralOrigin = new SourceSpan("test.sql", 1, 6);
        var holeOrigin = new SourceSpan("test.sql", 4, 12);
        var secondLiteralOrigin = new SourceSpan("test.sql", 1, 40);
        var assembly = new FlatPiece[]
        {
            new FlatPiece.Lit("SELECT ", firstLiteralOrigin, PrefixLength: 1),
            new FlatPiece.Hole(NVarChar50, holeOrigin, HoleKind.UntypedParameter),
            new FlatPiece.Lit(" FROM T", secondLiteralOrigin, PrefixLength: 1),
        };

        var rendered = TemplateRenderer.Render(assembly);

        var fIndex = rendered.InnerText.IndexOf("FROM", StringComparison.Ordinal);
        var span = rendered.SegmentMap.Map(innerLine: 1, innerColumn: fIndex + 1);

        Assert.Equal("test.sql", span.SourcePath);
        Assert.Equal(1, span.Line);
        Assert.Equal(41 + 1, span.Column);
    }
}
