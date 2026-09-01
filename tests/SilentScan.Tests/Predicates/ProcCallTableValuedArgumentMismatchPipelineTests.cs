using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ProcCallTableValuedArgumentMismatchPipelineTests
{
    [Fact]
    public async Task NumericScaleNarrowingIntoTvpColumn_SurfacesThroughLiveEngineAuthoritativePipeline()
    {
        const string sql = """
            CREATE TYPE dbo.tt_Amounts AS TABLE (Amount DECIMAL(10,2) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_ApplyAmounts @Amounts dbo.tt_Amounts READONLY
            AS
            BEGIN
                SELECT Amount FROM @Amounts;
            END
            GO
            CREATE PROCEDURE dbo.usp_Caller
            AS
            BEGIN
                DECLARE @rows dbo.tt_Amounts;
                INSERT INTO @rows VALUES (75.5678);
                EXEC dbo.usp_ApplyAmounts @Amounts = @rows;
            END
            """;

        var report = await EngineAuthoritativeScan.ScanAsync(sql);

        var finding = Assert.Single(report.Find<ProcCallTableValuedArgumentMismatchFinding>("ProcCallTableValuedArgumentMismatchScanner"));
        Assert.Equal("dbo.usp_Caller", finding.CallerScopeQualifiedName);
        Assert.Equal("dbo.usp_ApplyAmounts", finding.CalleeQualifiedName);
        Assert.Equal("@Amounts", finding.FormalParameterName);
        Assert.Equal("dbo.tt_Amounts", finding.TableTypeQualifiedName);
        Assert.Equal("Amount", finding.ColumnName);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
    }

    [Fact]
    public async Task UnicodeToNonUnicodeIntoTvpColumn_SurfacesThroughLiveEngineAuthoritativePipeline()
    {
        const string sql = """
            CREATE TYPE dbo.tt_Names AS TABLE (Name VARCHAR(20) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_ApplyNames @Names dbo.tt_Names READONLY
            AS
            BEGIN
                SELECT Name FROM @Names;
            END
            GO
            CREATE PROCEDURE dbo.usp_Caller
            AS
            BEGIN
                DECLARE @rows dbo.tt_Names;
                DECLARE @n NVARCHAR(20) = N'日本語abc';
                INSERT INTO @rows VALUES (@n);
                EXEC dbo.usp_ApplyNames @Names = @rows;
            END
            """;

        var report = await EngineAuthoritativeScan.ScanAsync(sql);

        var finding = Assert.Single(report.Find<ProcCallTableValuedArgumentMismatchFinding>("ProcCallTableValuedArgumentMismatchScanner"));
        Assert.Equal("Name", finding.ColumnName);
        Assert.Equal("@n", finding.CallerExpressionDisplay);
        Assert.Equal(WriteLossKind.UnicodeToNonUnicodeReplacement, finding.Kind);
    }

    [Fact]
    public async Task MatchingScaleIntoTvpColumn_NoFinding()
    {
        const string sql = """
            CREATE TYPE dbo.tt_Amounts AS TABLE (Amount DECIMAL(10,2) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_ApplyAmounts @Amounts dbo.tt_Amounts READONLY
            AS
            BEGIN
                SELECT Amount FROM @Amounts;
            END
            GO
            CREATE PROCEDURE dbo.usp_Caller
            AS
            BEGIN
                DECLARE @rows dbo.tt_Amounts;
                INSERT INTO @rows VALUES (75.56);
                EXEC dbo.usp_ApplyAmounts @Amounts = @rows;
            END
            """;

        var report = await EngineAuthoritativeScan.ScanAsync(sql);

        Assert.Empty(report.Find<ProcCallTableValuedArgumentMismatchFinding>("ProcCallTableValuedArgumentMismatchScanner"));
    }

    [Fact]
    public async Task LengthOverflowIntoTvpColumn_IsAHardEngineErrorNotASilentLoss_NoFinding()
    {
        const string sql = """
            CREATE TYPE dbo.tt_Names AS TABLE (Name VARCHAR(5) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_ApplyNames @Names dbo.tt_Names READONLY
            AS
            BEGIN
                SELECT Name FROM @Names;
            END
            GO
            CREATE PROCEDURE dbo.usp_Caller
            AS
            BEGIN
                DECLARE @rows dbo.tt_Names;
                INSERT INTO @rows VALUES ('this value is far longer than five characters');
                EXEC dbo.usp_ApplyNames @Names = @rows;
            END
            """;

        var report = await EngineAuthoritativeScan.ScanAsync(sql);

        Assert.Empty(report.Find<ProcCallTableValuedArgumentMismatchFinding>("ProcCallTableValuedArgumentMismatchScanner"));
    }
}
