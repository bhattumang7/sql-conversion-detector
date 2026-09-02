using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class NativelyCompiledErrorOutsideCatchScannerTests
{
    private static IReadOnlyList<NativelyCompiledErrorOutsideCatchFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return NativelyCompiledErrorOutsideCatchScanner.Scan(result);
    }

    [Fact]
    public void ErrorNumberCall_OutsideAnyTryCatch_Fires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.LogLastError
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @lastError INT = ERROR_NUMBER();
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.LogLastError", finding.ModuleQualifiedName);
        Assert.Equal("ERROR_NUMBER", finding.FunctionName);
    }

    [Fact]
    public void ErrorNumberCall_InTryBlock_Fires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.LogLastError
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @lastError INT;
                BEGIN TRY
                    SET @lastError = ERROR_NUMBER();
                END TRY
                BEGIN CATCH
                    SET @lastError = -1;
                END CATCH
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("ERROR_NUMBER", finding.FunctionName);
    }

    [Fact]
    public void ErrorNumberCall_InCatchBlock_NeverFires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.LogLastError
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @lastError INT;
                BEGIN TRY
                    SET @lastError = 1;
                END TRY
                BEGIN CATCH
                    SET @lastError = ERROR_NUMBER();
                END CATCH
            END;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ErrorNumberCall_AfterCatchBlockEnds_Fires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.LogLastError
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @lastError INT;
                BEGIN TRY
                    SET @lastError = 1;
                END TRY
                BEGIN CATCH
                    SET @lastError = ERROR_NUMBER();
                END CATCH
                SET @lastError = ERROR_NUMBER();
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.LogLastError", finding.ModuleQualifiedName);
    }

    [Fact]
    public void ErrorNumberCall_InOrdinaryInterpretedProcedure_NeverFires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.LogLastError
            AS
            BEGIN
                DECLARE @lastError INT = ERROR_NUMBER();
            END;
            """);

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("ERROR_MESSAGE()")]
    [InlineData("ERROR_SEVERITY()")]
    [InlineData("ERROR_STATE()")]
    [InlineData("ERROR_LINE()")]
    public void OtherErrorFunctions_OutsideCatchBlock_Fire(string expression)
    {
        var findings = Scan(
            $"""
            CREATE PROCEDURE dbo.LogLastError
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @lastError SQL_VARIANT = {expression};
            END;
            """);

        Assert.Single(findings);
    }
}
