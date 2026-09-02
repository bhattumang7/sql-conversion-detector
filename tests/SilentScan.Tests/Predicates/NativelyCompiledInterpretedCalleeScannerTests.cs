using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class NativelyCompiledInterpretedCalleeScannerTests
{
    private static IReadOnlyList<NativelyCompiledInterpretedCalleeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return NativelyCompiledInterpretedCalleeScanner.Scan(result, catalog);
    }

    [Fact]
    public void ExecInterpretedProcedure_FromNativelyCompiledProcedure_Fires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.LogAudit
            AS
            BEGIN
                INSERT INTO dbo.AuditLog (Message) VALUES (N'audited');
            END;
            GO
            CREATE PROCEDURE dbo.SaveOrder
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                EXEC dbo.LogAudit;
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.SaveOrder", finding.ModuleQualifiedName);
        Assert.Equal(NativelyCompiledInterpretedCalleeKind.ExecutedProcedure, finding.Kind);
        Assert.Equal("dbo.LogAudit", finding.CalleeQualifiedName);
    }

    [Fact]
    public void CallInterpretedScalarFunction_FromNativelyCompiledProcedure_Fires()
    {
        var findings = Scan(
            """
            CREATE FUNCTION dbo.ComputeTax(@amount DECIMAL(19,4))
            RETURNS DECIMAL(19,4)
            AS
            BEGIN
                RETURN @amount * 0.1;
            END;
            GO
            CREATE PROCEDURE dbo.SaveOrder
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @tax DECIMAL(19,4) = dbo.ComputeTax(100);
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.SaveOrder", finding.ModuleQualifiedName);
        Assert.Equal(NativelyCompiledInterpretedCalleeKind.CalledFunction, finding.Kind);
        Assert.Equal("dbo.ComputeTax", finding.CalleeQualifiedName);
    }

    [Fact]
    public void ExecNativelyCompiledProcedure_FromNativelyCompiledProcedure_NeverFires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.LogAudit
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @dummy INT = 1;
            END;
            GO
            CREATE PROCEDURE dbo.SaveOrder
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                EXEC dbo.LogAudit;
            END;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExecUnknownProcedure_FromNativelyCompiledProcedure_NeverFires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.SaveOrder
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                EXEC dbo.UnknownProc;
            END;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ExecInterpretedProcedure_FromOrdinaryInterpretedProcedure_NeverFires()
    {
        var findings = Scan(
            """
            CREATE PROCEDURE dbo.LogAudit
            AS
            BEGIN
                INSERT INTO dbo.AuditLog (Message) VALUES (N'audited');
            END;
            GO
            CREATE PROCEDURE dbo.SaveOrder
            AS
            BEGIN
                EXEC dbo.LogAudit;
            END;
            """);

        Assert.Empty(findings);
    }
}
