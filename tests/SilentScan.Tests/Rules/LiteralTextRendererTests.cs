using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Rules;

namespace SilentScan.Tests.Rules;

public sealed class LiteralTextRendererTests
{
    private static Literal ParseLiteral(string expressionSql)
    {
        var parser = new TSql160Parser(true);
        using var reader = new StringReader($"SELECT {expressionSql};");
        var fragment = parser.Parse(reader, out var errors);
        Assert.Empty(errors);

        var script = (TSqlScript)fragment;
        var select = (SelectStatement)script.Batches[0].Statements[0];
        var spec = (QuerySpecification)select.QueryExpression;
        var scalar = (SelectScalarExpression)spec.SelectElements[0];
        return Assert.IsType<Literal>(scalar.Expression, exactMatch: false);
    }

    [Fact]
    public void Render_NationalStringLiteral_AddsNPrefixAndQuotes()
    {
        Assert.Equal("N'hello'", LiteralTextRenderer.Render(ParseLiteral("N'hello'")));
    }

    [Fact]
    public void Render_StringLiteral_AddsQuotes()
    {
        Assert.Equal("'hello'", LiteralTextRenderer.Render(ParseLiteral("'hello'")));
    }

    [Fact]
    public void Render_StringLiteralWithEmbeddedQuote_DoublesIt()
    {
        Assert.Equal("'it''s'", LiteralTextRenderer.Render(ParseLiteral("'it''s'")));
    }

    [Fact]
    public void Render_NationalStringLiteralWithEmbeddedQuote_DoublesIt()
    {
        Assert.Equal("N'it''s'", LiteralTextRenderer.Render(ParseLiteral("N'it''s'")));
    }

    [Fact]
    public void Render_IntegerLiteral_ReturnsVerbatim()
    {
        Assert.Equal("123", LiteralTextRenderer.Render(ParseLiteral("123")));
    }

    [Fact]
    public void Render_DecimalLiteral_ReturnsVerbatim()
    {
        Assert.Equal("1.5", LiteralTextRenderer.Render(ParseLiteral("1.5")));
    }

    [Fact]
    public void Render_MoneyLiteral_KeepsDollarSign()
    {
        Assert.Equal("$5.00", LiteralTextRenderer.Render(ParseLiteral("$5.00")));
    }

    [Fact]
    public void Render_BinaryLiteral_KeepsHexPrefix()
    {
        Assert.Equal("0x1A2B", LiteralTextRenderer.Render(ParseLiteral("0x1A2B")));
    }

    [Fact]
    public void Render_RealLiteral_ReturnsVerbatim()
    {
        Assert.Equal("1.5e10", LiteralTextRenderer.Render(ParseLiteral("1.5e10")));
    }

    [Fact]
    public void Render_NullLiteral_ReturnsNull()
    {
        Assert.Null(LiteralTextRenderer.Render(ParseLiteral("NULL")));
    }
}
