using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class NativelyCompiledUnsupportedBuiltinScannerTests
{
    private static IReadOnlyList<NativelyCompiledUnsupportedBuiltinFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return NativelyCompiledUnsupportedBuiltinScanner.Scan(result);
    }

    [Fact]
    public void UnsupportedFunction_InNativelyCompiledProcedure_Fires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.NormalizeCode
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @code NVARCHAR(20) = UPPER(N'ab-12');
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.NormalizeCode", finding.ModuleQualifiedName);
        Assert.Equal("UPPER", finding.FunctionName);
    }

    [Fact]
    public void UnsupportedFunction_InOrdinaryInterpretedProcedure_NeverFires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.NormalizeCode
            AS
            BEGIN
                DECLARE @code NVARCHAR(20) = UPPER(N'ab-12');
            END;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SupportedFunction_InNativelyCompiledProcedure_NeverFires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.NormalizeAmount
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @amount FLOAT = ABS(-1.0);
            END;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void UnsupportedFunction_InNativelyCompiledScalarFunction_Fires()
    {
        var findings = Scan(
            """
            CREATE FUNCTION dbo.NormalizeCode(@code NVARCHAR(20))
            RETURNS NVARCHAR(20)
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                RETURN UPPER(@code);
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.NormalizeCode", finding.ModuleQualifiedName);
        Assert.Equal("UPPER", finding.FunctionName);
    }

    [Theory]
    [InlineData("LEFT(N'abc', 1)", "LEFT")]
    [InlineData("RIGHT(N'abc', 1)", "RIGHT")]
    public void LeftOrRightCall_InNativelyCompiledProcedure_Fires(string expression, string expectedFunctionName)
    {
        var findings = Scan(
            $"""
            CREATE PROCEDURE dbo.TrimCode
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @code NVARCHAR(20) = {expression};
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.TrimCode", finding.ModuleQualifiedName);
        Assert.Equal(expectedFunctionName, finding.FunctionName);
    }

    [Fact]
    public void LeftCall_InOrdinaryInterpretedProcedure_NeverFires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.TrimCode
            AS
            BEGIN
                DECLARE @code NVARCHAR(20) = LEFT(N'abc', 1);
            END;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void StringAgg_InNativelyCompiledProcedure_NeverFires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.SummarizeCodes
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @codes NVARCHAR(100);
                SELECT @codes = STRING_AGG(name, N',') FROM sys.objects;
            END;
            """);

        Assert.Empty(findings);
    }

    [Theory]
    [InlineData("VARBINARY(100)", "COMPRESS(N'a')", "COMPRESS")]
    [InlineData("VARBINARY(100)", "DECOMPRESS(0x00)", "DECOMPRESS")]
    [InlineData("INT", "CHECKSUM(N'a')", "CHECKSUM")]
    [InlineData("INT", "BINARY_CHECKSUM(N'a')", "BINARY_CHECKSUM")]
    [InlineData("SYSNAME", "PARSENAME(N'a.b', 1)", "PARSENAME")]
    [InlineData("NVARCHAR(128)", "APP_NAME()", "APP_NAME")]
    [InlineData("SYSNAME", "TYPE_NAME(56)", "TYPE_NAME")]
    [InlineData("SYSNAME", "COL_NAME(1, 1)", "COL_NAME")]
    [InlineData("NVARCHAR(255)", "FORMATMESSAGE(N'a')", "FORMATMESSAGE")]
    [InlineData("INT", "OBJECT_ID(N'a')", "OBJECT_ID")]
    [InlineData("SYSNAME", "OBJECT_NAME(1)", "OBJECT_NAME")]
    [InlineData("INT", "DB_ID()", "DB_ID")]
    [InlineData("SYSNAME", "DB_NAME()", "DB_NAME")]
    [InlineData("INT", "SCHEMA_ID()", "SCHEMA_ID")]
    [InlineData("SYSNAME", "SCHEMA_NAME()", "SCHEMA_NAME")]
    [InlineData("INT", "PERMISSIONS()", "PERMISSIONS")]
    [InlineData("INT", "HAS_PERMS_BY_NAME(N'a', N'DATABASE', N'SELECT')", "HAS_PERMS_BY_NAME")]
    [InlineData("SYSNAME", "CURRENT_TIMEZONE()", "CURRENT_TIMEZONE")]
    [InlineData("NUMERIC(38,0)", "IDENT_CURRENT(N'a')", "IDENT_CURRENT")]
    [InlineData("DATETIME", "STATS_DATE(1, 1)", "STATS_DATE")]
    [InlineData("INT", "OBJECTPROPERTY(1, N'a')", "OBJECTPROPERTY")]
    [InlineData("INT", "COLLATIONPROPERTY(N'a', N'a')", "COLLATIONPROPERTY")]
    [InlineData("INT", "FILE_ID(N'a')", "FILE_ID")]
    [InlineData("INT", "INDEXPROPERTY(1, N'a', N'a')", "INDEXPROPERTY")]
    public void UnsupportedMetadataOrCryptoFunction_InNativelyCompiledProcedure_Fires(string variableType, string expression, string expectedFunctionName)
    {
        var findings = Scan(
            $"""
            CREATE PROCEDURE dbo.NormalizeCode
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @x {variableType} = {expression};
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(expectedFunctionName, finding.FunctionName);
    }

    [Fact]
    public void MultipleUnsupportedCalls_AllFire()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.NormalizeCode
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @a NVARCHAR(20) = UPPER(N'a');
                DECLARE @b NVARCHAR(20) = LOWER(N'b');
            END;
            """);

        Assert.Equal(2, findings.Count);
        Assert.Equal(["UPPER", "LOWER"], findings.Select(f => f.FunctionName));
    }
}
