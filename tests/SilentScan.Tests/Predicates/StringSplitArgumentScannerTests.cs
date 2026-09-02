using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class StringSplitArgumentScannerTests
{
    private static IReadOnlyList<StringSplitArgumentFinding> Scan(string sql, int? engineMajorVersion = 16)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        catalog.EngineMajorVersion = engineMajorVersion;
        return StringSplitArgumentScanner.Scan(result, catalog);
    }

    [Fact]
    public void ThreeArgumentForm_EngineMajorVersionBelowSqlServer2022_Fires()
    {
        var findings = Scan(
            """
            SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 1);
            """,
            engineMajorVersion: 15);

        var finding = Assert.Single(findings);
        Assert.Equal(StringSplitArgumentFindingKind.ThreeArgumentFormRequiresNewerEngine, finding.Kind);
        Assert.Equal("15", finding.DetailText);
    }

    [Fact]
    public void ThreeArgumentForm_EngineMajorVersionAtSqlServer2022_NeverFires()
    {
        var findings = Scan(
            """
            SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 1);
            """,
            engineMajorVersion: 16);

        Assert.Empty(findings);
    }

    [Fact]
    public void ThreeArgumentForm_EngineMajorVersionUnknown_NeverFires()
    {
        var findings = Scan(
            """
            SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 1);
            """,
            engineMajorVersion: null);

        Assert.Empty(findings);
    }

    [Fact]
    public void ThreeArgumentForm_EngineMajorVersionBelowSqlServer2022_FiresRegardlessOfEnableOrdinalValue()
    {
        var findings = Scan(
            """
            SELECT value FROM STRING_SPLIT('a,b', ',', NULL);
            """,
            engineMajorVersion: 15);

        var finding = Assert.Single(findings);
        Assert.Equal(StringSplitArgumentFindingKind.ThreeArgumentFormRequiresNewerEngine, finding.Kind);
    }

    [Fact]
    public void TwoArgumentForm_EngineMajorVersionBelowSqlServer2022_NeverFires()
    {
        var findings = Scan(
            """
            SELECT value FROM STRING_SPLIT('a,b', ',');
            """,
            engineMajorVersion: 15);

        Assert.Empty(findings);
    }

    [Fact]
    public void IntVariableInputArgument_Fires()
    {
        var findings = Scan(
            """
            DECLARE @Id INT = 12345;
            SELECT value FROM STRING_SPLIT(@Id, ',');
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(StringSplitArgumentFindingKind.ArgumentTypeNotCharacter, finding.Kind);
        Assert.Equal("Int", finding.DetailText);
    }

    [Fact]
    public void DatetimeVariableSeparatorArgument_Fires()
    {
        var findings = Scan(
            """
            DECLARE @Sep DATETIME = '2020-01-01';
            SELECT value FROM STRING_SPLIT('a,b', @Sep);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(StringSplitArgumentFindingKind.ArgumentTypeNotCharacter, finding.Kind);
    }

    [Fact]
    public void IntegerLiteralInputArgument_Fires()
    {
        var findings = Scan(
            """
            SELECT value FROM STRING_SPLIT(12345, ',');
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(StringSplitArgumentFindingKind.ArgumentTypeNotCharacter, finding.Kind);
    }

    [Fact]
    public void VarcharVariableInputArgument_NeverFires()
    {
        var findings = Scan(
            """
            DECLARE @Input VARCHAR(50) = 'a,b';
            SELECT value FROM STRING_SPLIT(@Input, ',');
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void EnableOrdinalFromVariable_Fires()
    {
        var findings = Scan(
            """
            DECLARE @Flag BIT = 1;
            SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', @Flag);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(StringSplitArgumentFindingKind.EnableOrdinalNotConstant, finding.Kind);
    }

    [Fact]
    public void EnableOrdinalStringLiteral_Fires()
    {
        var findings = Scan(
            """
            SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', '1');
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(StringSplitArgumentFindingKind.EnableOrdinalTypeNotInteger, finding.Kind);
    }

    [Fact]
    public void EnableOrdinalOutOfRangeInteger_Fires()
    {
        var findings = Scan(
            """
            SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 3);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(StringSplitArgumentFindingKind.EnableOrdinalInvalidValue, finding.Kind);
    }

    [Fact]
    public void EnableOrdinalNegativeInteger_Fires()
    {
        var findings = Scan(
            """
            SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', -1);
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(StringSplitArgumentFindingKind.EnableOrdinalInvalidValue, finding.Kind);
    }

    [Fact]
    public void EnableOrdinalZeroOrOneLiteral_NeverFires()
    {
        var findings = Scan(
            """
            SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 1);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void EnableOrdinalNullLiteral_NeverFires()
    {
        var findings = Scan(
            """
            SELECT value FROM STRING_SPLIT('a,b', ',', NULL);
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void EnableOrdinalConstantArithmeticExpression_NeverFires()
    {
        var findings = Scan(
            """
            SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 1 + 0);
            """);

        Assert.Empty(findings);
    }
}
