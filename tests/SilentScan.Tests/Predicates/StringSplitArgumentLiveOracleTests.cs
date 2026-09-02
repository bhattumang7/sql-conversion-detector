using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class StringSplitArgumentLiveOracleTests
{
    [Fact]
    public async Task LiveDeployment_TwoCharacterLiteralSeparator_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitTwoChar AS
            BEGIN
                SELECT value FROM STRING_SPLIT('a,,b', ',,');
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
        Assert.Equal(StringSplitArgumentFindingKind.SeparatorNotSingleCharacter, finding.Kind);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.Equal("',,'", finding.ArgumentText);
    }

    [Fact]
    public async Task LiveDeployment_ZeroLengthLiteralSeparator_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitEmpty AS
            BEGIN
                SELECT value FROM STRING_SPLIT('a,b', '');
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
        Assert.Equal(StringSplitArgumentFindingKind.SeparatorNotSingleCharacter, finding.Kind);
    }

    [Fact]
    public async Task LiveDeployment_NullLiteralSeparator_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitNull AS
            BEGIN
                SELECT value FROM STRING_SPLIT('a,b', NULL);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
        Assert.Equal(StringSplitArgumentFindingKind.SeparatorNotSingleCharacter, finding.Kind);
        Assert.Equal("NULL", finding.ArgumentText);
    }

    [Fact]
    public async Task LiveDeployment_ConcatenatedTwoCharacterLiteralSeparator_Fires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitConcat AS
            BEGIN
                SELECT value FROM STRING_SPLIT('a,b', ',' + ',');
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
        Assert.Equal(StringSplitArgumentFindingKind.SeparatorNotSingleCharacter, finding.Kind);
    }

    [Fact]
    public async Task LiveDeployment_SingleCharacterLiteralSeparator_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitClean AS
            BEGIN
                SELECT value FROM STRING_SPLIT('a,b', ',');
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
    }

    [Fact]
    public async Task LiveDeployment_SingleCharacterNationalLiteralSeparator_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitUnicodeClean AS
            BEGIN
                SELECT value FROM STRING_SPLIT(N'a,b', N',');
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
    }

    [Fact]
    public async Task LiveDeployment_NonLiteralVariableSeparator_DeclinesRatherThanGuessing()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitVariable AS
            BEGIN
                DECLARE @Sep NCHAR(1) = ',';
                SELECT value FROM STRING_SPLIT('a,b', @Sep);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
    }

    [Fact]
    public async Task LiveDeployment_ThreeArgumentFormWithTwoCharacterSeparator_StillFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitOrdinal AS
            BEGIN
                SELECT value, ordinal FROM STRING_SPLIT('a,b', ',,', 1);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        var finding = Assert.Single(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
        Assert.Equal(StringSplitArgumentFindingKind.SeparatorNotSingleCharacter, finding.Kind);
    }

    [Fact]
    public async Task LiveDeployment_SchemaQualifiedUserFunctionNamedStringSplit_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE FUNCTION dbo.STRING_SPLIT(@Input NVARCHAR(MAX), @Sep NVARCHAR(10))
            RETURNS @Result TABLE (value NVARCHAR(MAX))
            AS
            BEGIN
                INSERT INTO @Result (value) VALUES (@Input);
                RETURN;
            END
            GO
            CREATE PROCEDURE dbo.usp_StringSplitUserDefined AS
            BEGIN
                SELECT value FROM dbo.STRING_SPLIT('a,b', ',,');
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
    }

    [Fact]
    public async Task LiveDeployment_VarcharVariableInputArgument_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitVarcharInput AS
            BEGIN
                DECLARE @Input VARCHAR(50) = 'a,b';
                SELECT value FROM STRING_SPLIT(@Input, ',');
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
    }

    [Fact]
    public async Task LiveDeployment_EnableOrdinalZeroOrOneLiteral_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitOrdinalValid AS
            BEGIN
                SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 1);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
    }

    [Fact]
    public async Task LiveDeployment_EnableOrdinalNullLiteral_NeverFires()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitOrdinalNull AS
            BEGIN
                SELECT value FROM STRING_SPLIT('a,b', ',', NULL);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.Empty(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"));
    }

    [Fact]
    public async Task LiveDeployment_ThreeArgumentFormAgainstSqlServer2022Engine_NeverFiresVersionGate()
    {
        var report = await EngineAuthoritativeScan.ScanAsync(
            """
            CREATE PROCEDURE dbo.usp_StringSplitOrdinalOnCurrentEngine AS
            BEGIN
                SELECT value, ordinal FROM STRING_SPLIT('a,b', ',', 1);
            END
            """,
            minimumConfidence: FindingConfidence.Low);

        Assert.DoesNotContain(
            report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner"),
            f => f.Kind == StringSplitArgumentFindingKind.ThreeArgumentFormRequiresNewerEngine);
    }
}
