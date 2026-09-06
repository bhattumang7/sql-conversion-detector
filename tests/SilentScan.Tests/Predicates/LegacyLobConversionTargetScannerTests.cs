using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class LegacyLobConversionTargetScannerTests
{
    private static IReadOnlyList<LegacyLobConversionTargetFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return LegacyLobConversionTargetScanner.Scan(result);
    }

    [Fact]
    public void Convert_ToNText_WithSupplementaryCharacterAwareCollation_Fires()
    {
        var findings = Scan("SELECT CONVERT(ntext, Name) COLLATE Latin1_General_100_CI_AI_SC FROM dbo.Customer;");

        var finding = Assert.Single(findings);
        Assert.Equal("Latin1_General_100_CI_AI_SC", finding.CollationName);
    }

    [Fact]
    public void Cast_ToText_WithUtf8Collation_Fires()
    {
        var findings = Scan("SELECT CAST(Description AS text) COLLATE Latin1_General_100_CI_AS_SC_UTF8 FROM dbo.Product;");

        Assert.Single(findings);
    }

    [Fact]
    public void TryCast_ToNText_WithSupplementaryCharacterAwareCollation_Fires()
    {
        var findings = Scan("SELECT TRY_CAST(Description AS ntext) COLLATE Latin1_General_100_CI_AI_SC FROM dbo.Product;");

        Assert.Single(findings);
    }

    [Fact]
    public void TryConvert_ToText_WithUtf8Collation_Fires()
    {
        var findings = Scan("SELECT TRY_CONVERT(text, Description) COLLATE Latin1_General_100_CI_AS_SC_UTF8 FROM dbo.Product;");

        Assert.Single(findings);
    }

    [Fact]
    public void Cast_ToNText_WithOrdinaryCollation_NegativeControl_DoesNotFire()
    {
        var findings = Scan("SELECT CAST(Name AS ntext) COLLATE Latin1_General_100_CI_AI FROM dbo.Customer;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Cast_ToNVarcharMax_WithSupplementaryCharacterAwareCollation_NegativeControl_DoesNotFire()
    {
        var findings = Scan("SELECT CAST(Name AS nvarchar(max)) COLLATE Latin1_General_100_CI_AI_SC FROM dbo.Customer;");

        Assert.Empty(findings);
    }

    [Fact]
    public void Cast_ToNText_WithNoCollateClause_NegativeControl_DoesNotFire()
    {
        var findings = Scan("SELECT CAST(Name AS ntext) FROM dbo.Customer;");

        Assert.Empty(findings);
    }
}
