using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class NonParallelizableIntrinsicOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(NonParallelizableIntrinsicOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.BigTable (Id INT NOT NULL, Grp INT NOT NULL, Val VARCHAR(100) NOT NULL);
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var seedCommand = new SqlCommand(
            """
            INSERT INTO dbo.BigTable (Id, Grp, Val)
            SELECT TOP (200000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 100, REPLICATE('x', 50)
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE STATISTICS dbo.BigTable WITH FULLSCAN;
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

    [Theory]
    [InlineData("OBJECT_ID('dbo.BigTable') IS NOT NULL")]
    [InlineData("IDENT_CURRENT('dbo.BigTable') > 0")]
    [InlineData("ERROR_NUMBER() IS NULL")]
    [InlineData("ERROR_MESSAGE() IS NULL")]
    [InlineData("ERROR_LINE() IS NULL")]
    [InlineData("ERROR_SEVERITY() IS NULL")]
    [InlineData("ERROR_STATE() IS NULL")]
    [InlineData("ERROR_PROCEDURE() IS NULL")]
    [InlineData("@@TRANCOUNT >= 0")]
    public async Task ConfirmedIntrinsic_InsideQueryWithFrom_ForcesSerial(string predicate)
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            $"SELECT Grp, COUNT(*) FROM dbo.BigTable WHERE {predicate} GROUP BY Grp OPTION (MAXDOP 0);");

        Assert.Contains("NonParallelPlanReason=\"NonParallelizableIntrinsicFunction\"", planXml);
    }

    [Theory]
    [InlineData("@@ROWCOUNT >= 0")]
    [InlineData("SCOPE_IDENTITY() IS NULL OR SCOPE_IDENTITY() IS NOT NULL")]
    public async Task ExcludedIntrinsic_InsideQueryWithFrom_NeverForcesSerial(string predicate)
    {
        var planXml = await CaptureRealExecutionPlanAsync(
            $"SELECT Grp, COUNT(*) FROM dbo.BigTable WHERE {predicate} GROUP BY Grp OPTION (MAXDOP 0);");

        Assert.DoesNotContain("NonParallelPlanReason=\"NonParallelizableIntrinsicFunction\"", planXml);
    }
}
