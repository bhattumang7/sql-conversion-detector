using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class AmbiguousDateLiteralConversionScannerTests
{
    private static IReadOnlyList<AmbiguousDateLiteralConversionFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return AmbiguousDateLiteralConversionScanner.Scan(result);
    }

    [Fact]
    public void CastToDate_AmbiguousLiteral_Fires()
    {
        var findings = Scan("SELECT CAST('03/04/2026' AS date);");

        var finding = Assert.Single(findings);
        Assert.Equal("03/04/2026", finding.LiteralText);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("datetime")]
    [InlineData("datetime2")]
    [InlineData("smalldatetime")]
    [InlineData("datetimeoffset")]
    public void CastToEachDateFamilyType_Fires(string dataType)
    {
        var findings = Scan($"SELECT CAST('03/04/2026' AS {dataType});");

        Assert.Single(findings);
    }

    [Fact]
    public void ConvertWithNoStyle_AmbiguousLiteral_Fires()
    {
        var findings = Scan("SELECT CONVERT(date, '03/04/2026');");

        Assert.Single(findings);
    }

    [Fact]
    public void ConvertWithExplicitStyle_NeverFires()
    {
        var findings = Scan("SELECT CONVERT(date, '03/04/2026', 103);");

        Assert.Empty(findings);
    }

    [Fact]
    public void IsoFormatLiteral_NeverFires()
    {
        var findings = Scan("SELECT CAST('20260304' AS date);");

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("25/04/2026")]
    [InlineData("04/25/2026")]
    public void DayOutOfMonthRange_NotAmbiguous_NeverFires(string literal)
    {
        var findings = Scan($"SELECT CAST('{literal}' AS date);");

        Assert.Empty(findings);
    }

    [Fact]
    public void SameDayAndMonth_NoEffectiveAmbiguity_NeverFires()
    {
        var findings = Scan("SELECT CAST('05/05/2026' AS date);");

        Assert.Empty(findings);
    }

    [Fact]
    public void CastToNonDateType_NeverFires()
    {
        var findings = Scan("SELECT CAST('03/04/2026' AS varchar(20));");

        Assert.Empty(findings);
    }

    [Fact]
    public void CastOfNonLiteralExpression_NeverFires()
    {
        var findings = Scan("SELECT CAST(SomeColumn AS date) FROM dbo.T;");

        Assert.Empty(findings);
    }

    [Fact]
    public void DotSeparatedAmbiguousLiteral_Fires()
    {
        var findings = Scan("SELECT CAST('03.04.2026' AS date);");

        Assert.Single(findings);
    }
}
