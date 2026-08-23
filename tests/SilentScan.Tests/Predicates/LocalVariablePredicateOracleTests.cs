using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class LocalVariablePredicateOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(LocalVariablePredicateOracleTests);

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
            SELECT TOP (1900) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'Common'
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;

            INSERT INTO dbo.Customers (Id, Region)
            SELECT TOP (100) 1900 + ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
                   'Rare' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS VARCHAR(10))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;

            UPDATE STATISTICS dbo.Customers WITH FULLSCAN;
            """, connection);
        await seedCommand.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SniffedLiteral_VsDeclaredLocalVariable_ProduceMateriallyDifferentEstimates()
    {
        var capture = new PlanXmlCapture(Options);

        var literalPlan = await capture.CaptureAsync(
            DatabaseName, "SELECT Id FROM dbo.Customers WHERE Region = 'Common';");

        var localVariablePlan = await capture.CaptureAsync(
            DatabaseName, "DECLARE @p VARCHAR(20) = 'Common'; SELECT Id FROM dbo.Customers WHERE Region = @p;");

        var literalEstimate = ExtractEstimateRows(literalPlan);
        var localVariableEstimate = ExtractEstimateRows(localVariablePlan);

        Assert.True(literalEstimate > 1500, $"expected the literal probe's estimate to reflect the real skew (~1900), got {literalEstimate}.");
        Assert.True(
            localVariableEstimate < literalEstimate / 2,
            $"expected the local-variable probe's estimate ({localVariableEstimate}) to diverge materially from the literal probe's ({literalEstimate}) - if they're close, the estimator premise this finding relies on no longer holds.");
    }

    private static double ExtractEstimateRows(string planXml)
    {
        const string Marker = "EstimateRows=\"";
        var start = planXml.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "expected the plan XML to contain an EstimateRows attribute.");
        start += Marker.Length;
        var end = planXml.IndexOf('"', start);
        return double.Parse(planXml[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }
}
