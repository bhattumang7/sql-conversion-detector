using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class TruncateSwallowedScannerTests
{
    private static IReadOnlyList<TruncateSwallowedFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return TruncateSwallowedScanner.Scan(result);
    }

    [Fact]
    public void EmptyCatch_Fires()
    {
        var findings = Scan("""
            BEGIN TRY
                TRUNCATE TABLE dbo.Foo;
            END TRY
            BEGIN CATCH
            END CATCH;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void CatchDoesUnrelatedWorkButNeverThrows_Fires()
    {
        var findings = Scan("""
            BEGIN TRY
                TRUNCATE TABLE dbo.Foo;
            END TRY
            BEGIN CATCH
                INSERT INTO dbo.ErrorLog (Message) VALUES (ERROR_MESSAGE());
            END CATCH;
            """);

        Assert.Single(findings);
    }

    [Fact]
    public void CatchWithThrow_NeverFires()
    {
        var findings = Scan("""
            BEGIN TRY
                TRUNCATE TABLE dbo.Foo;
            END TRY
            BEGIN CATCH
                THROW;
            END CATCH;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void CatchWithRaiserror_NeverFires()
    {
        var findings = Scan("""
            BEGIN TRY
                TRUNCATE TABLE dbo.Foo;
            END TRY
            BEGIN CATCH
                RAISERROR('failed', 16, 1);
            END CATCH;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void ThrowNestedInsideIf_StillNeverFires()
    {
        var findings = Scan("""
            BEGIN TRY
                TRUNCATE TABLE dbo.Foo;
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() = 4712
                BEGIN
                    THROW;
                END
            END CATCH;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NoTruncateInTry_NeverFires()
    {
        var findings = Scan("""
            BEGIN TRY
                SELECT 1;
            END TRY
            BEGIN CATCH
            END CATCH;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void TruncateOutsideAnyTry_NeverFires()
    {
        var findings = Scan("TRUNCATE TABLE dbo.Foo;");

        Assert.Empty(findings);
    }

    [Fact]
    public void NestedEmptyCatches_FiresOnceForInnerOnly()
    {
        var findings = Scan("""
            BEGIN TRY
                BEGIN TRY
                    TRUNCATE TABLE dbo.Foo;
                END TRY
                BEGIN CATCH
                END CATCH;
            END TRY
            BEGIN CATCH
            END CATCH;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void TripleNestedEmptyCatches_FiresOnceForInnermostOnly()
    {
        var findings = Scan("""
            BEGIN TRY
                BEGIN TRY
                    BEGIN TRY
                        TRUNCATE TABLE dbo.Foo;
                    END TRY
                    BEGIN CATCH
                    END CATCH;
                END TRY
                BEGIN CATCH
                END CATCH;
            END TRY
            BEGIN CATCH
            END CATCH;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(4, finding.Line);
    }

    [Fact]
    public void NestedInnerCatchRethrows_FiresOnceForOuterOnly()
    {
        var findings = Scan("""
            BEGIN TRY
                BEGIN TRY
                    TRUNCATE TABLE dbo.Foo;
                END TRY
                BEGIN CATCH
                    THROW;
                END CATCH;
            END TRY
            BEGIN CATCH
            END CATCH;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void NestedInnerCatchRaisesError_FiresOnceForOuterOnly()
    {
        var findings = Scan("""
            BEGIN TRY
                BEGIN TRY
                    TRUNCATE TABLE dbo.Foo;
                END TRY
                BEGIN CATCH
                    RAISERROR('failed', 16, 1);
                END CATCH;
            END TRY
            BEGIN CATCH
            END CATCH;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void NestedBothCatchesPropagate_NeverFires()
    {
        var findings = Scan("""
            BEGIN TRY
                BEGIN TRY
                    TRUNCATE TABLE dbo.Foo;
                END TRY
                BEGIN CATCH
                    THROW;
                END CATCH;
            END TRY
            BEGIN CATCH
                THROW;
            END CATCH;
            """);

        Assert.Empty(findings);
    }
}
