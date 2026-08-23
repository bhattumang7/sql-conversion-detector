using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class SchemaDependencyCheckConstraintSerialOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(SchemaDependencyCheckConstraintSerialOracleTests);

    protected override string Ddl => """
        CREATE FUNCTION dbo.fnIsValid (@x INT) RETURNS BIT AS
        BEGIN
            RETURN CASE WHEN @x > 0 THEN 1 ELSE 0 END;
        END;
        GO
        CREATE TABLE dbo.Orders (Id INT NOT NULL, Qty INT NOT NULL, CONSTRAINT CK_Orders_Qty CHECK (dbo.fnIsValid(Qty) = 1));
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var seedCommand = new SqlCommand(
            """
            INSERT INTO dbo.Orders (Id, Qty)
            SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 1
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE STATISTICS dbo.Orders WITH FULLSCAN;
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
    public async Task UpdateEvaluatingConstraint_ForcesSerialViaTheReferencedUdf()
    {
        var planXml = await CaptureRealExecutionPlanAsync("UPDATE dbo.Orders SET Qty = Qty + 1 OPTION (MAXDOP 0);");

        Assert.Contains("NonParallelPlanReason=\"TSQLUserDefinedFunctionsNotParallelizable\"", planXml);
        Assert.Contains("UserDefinedFunction FunctionName=\"[" + DatabaseName + "].[dbo].[fnIsValid]\"", planXml);
    }

    [Fact]
    public async Task PlainSelect_NeverForcesSerial()
    {
        var planXml = await CaptureRealExecutionPlanAsync("SELECT COUNT(*) FROM dbo.Orders OPTION (MAXDOP 0);");

        Assert.DoesNotContain("NonParallelPlanReason=\"TSQLUserDefinedFunctionsNotParallelizable\"", planXml);
    }
}
