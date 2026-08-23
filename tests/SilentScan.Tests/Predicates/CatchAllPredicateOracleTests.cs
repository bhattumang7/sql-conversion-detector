using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class CatchAllPredicateOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(CatchAllPredicateOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Customers (Id INT NOT NULL, Region VARCHAR(20) NOT NULL);
        GO
        CREATE INDEX IX_Customers_Region ON dbo.Customers(Region);
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var seedCommand = new SqlCommand(
            """
            INSERT INTO dbo.Customers (Id, Region)
            SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   'R' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE STATISTICS dbo.Customers WITH FULLSCAN;
            """, connection);
        await seedCommand.ExecuteNonQueryAsync();
    }

    private async Task<string> CaptureRealExecutionPlanAsync(string probe)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var onCommand = new SqlCommand("SET STATISTICS XML ON;", connection))
        {
            await onCommand.ExecuteNonQueryAsync();
        }

        string planXml;
        await using (var probeCommand = new SqlCommand(probe, connection))
        await using (var reader = await probeCommand.ExecuteReaderAsync())
        {
            planXml = string.Empty;
            do
            {
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount == 1 && reader.GetFieldType(0) == typeof(string))
                    {
                        var value = reader.GetString(0);
                        if (value.Contains("ShowPlanXML", StringComparison.Ordinal))
                        {
                            planXml = value;
                        }
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        await using (var offCommand = new SqlCommand("SET STATISTICS XML OFF;", connection))
        {
            await offCommand.ExecuteNonQueryAsync();
        }

        Assert.NotEmpty(planXml);
        return planXml;
    }

    [Fact]
    public async Task BareEquality_Seeks()
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            "DECLARE @p VARCHAR(20) = 'R5'; SELECT Id FROM dbo.Customers WHERE Region = @p;");

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task CatchAllShape_NoRecompile_ForcesScan()
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            "DECLARE @p VARCHAR(20) = 'R5'; SELECT Id FROM dbo.Customers WHERE (Region = @p OR @p IS NULL);");

        Assert.Contains("PhysicalOp=\"Table Scan\"", planXml);
        Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task CatchAllShape_AbsorbedByEquivalentOuterPredicate_Seeks()
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            "DECLARE @p VARCHAR(20) = 'R5'; SELECT Id FROM dbo.Customers WHERE Region = @p AND (Region = @p OR @p IS NULL);");

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task CatchAllShape_WithOptionRecompile_RestoresTheSeek()
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            "DECLARE @p VARCHAR(20) = 'R5'; SELECT Id FROM dbo.Customers WHERE (Region = @p OR @p IS NULL) OPTION (RECOMPILE);");

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }
}
