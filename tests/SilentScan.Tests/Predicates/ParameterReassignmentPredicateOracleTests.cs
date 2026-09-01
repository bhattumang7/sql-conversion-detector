using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class ParameterReassignmentPredicateOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ParameterReassignmentPredicateOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Customers (Id INT NOT NULL, Region VARCHAR(20) NOT NULL);
        GO
        CREATE INDEX IX_Customers_Region ON dbo.Customers(Region);
        GO
        CREATE PROCEDURE dbo.usp_FindReassigned @p VARCHAR(20) AS
        BEGIN
            SET @p = 'Zzz_Never_Seeded';
            SELECT Id FROM dbo.Customers WHERE Region = @p;
        END
        GO
        CREATE PROCEDURE dbo.usp_FindDirect @p VARCHAR(20) AS
        BEGIN
            SELECT Id FROM dbo.Customers WHERE Region = @p;
        END
        GO
        CREATE PROCEDURE dbo.usp_FindConditionallyReassigned @p VARCHAR(20) AS
        BEGIN
            SELECT @p = Region FROM dbo.Customers WHERE Region = 'Zzz_Never_Seeded';
            SELECT Id FROM dbo.Customers WHERE Region = @p;
        END
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
    public async Task ParameterReassignedBeforePredicate_EstimateStillReflectsTheOriginalSniffedArgument()
    {
        var capture = new PlanXmlCapture(Options);

        var directPlan = await capture.CaptureAsync(DatabaseName, "EXEC dbo.usp_FindDirect @p = 'Common';");

        var reassignedPlan = await capture.CaptureAsync(DatabaseName, "EXEC dbo.usp_FindReassigned @p = 'Common';");

        var directEstimate = ExtractEstimateRows(directPlan);
        var reassignedEstimate = ExtractEstimateRows(reassignedPlan);

        Assert.True(directEstimate > 1500, $"expected the direct-use probe's estimate to reflect the real skew (~1900), got {directEstimate}.");
        Assert.True(
            reassignedEstimate > 1500,
            $"expected the reassigned-parameter probe's estimate ({reassignedEstimate}) to STILL reflect the original sniffed 'Common' argument (~1900), not the reassigned value's own near-zero density - this is the staleness ParameterReassignmentPredicateFinding exists to report.");
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

    [Fact]
    public async Task ConditionalNonAggregateReassignment_NeverMatchesAnyRow_LeavesParameterAtOriginalArgument_ScannerCorrectlyDoesNotFlag()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "EXEC dbo.usp_FindConditionallyReassigned @p = 'Common';",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rowCount = 0;
        while (await reader.ReadAsync())
        {
            rowCount++;
        }

        Assert.True(rowCount > 1500, $"expected the predicate at 'Common' to still see the original ~1900-row skew (the conditional SELECT never matched, so @p was never actually reassigned), got {rowCount} rows.");

        var findings = ScanConditionalReassignment(
            """
            CREATE PROCEDURE dbo.usp_FindConditionallyReassigned @p VARCHAR(20) AS
            BEGIN
                SELECT @p = Region FROM dbo.Customers WHERE Region = 'Zzz_Never_Seeded';
                SELECT Id FROM dbo.Customers WHERE Region = @p;
            END
            """);

        Assert.Empty(findings);
    }

    private static IReadOnlyList<ParameterReassignmentPredicateFinding> ScanConditionalReassignment(string sql)
    {
        var result = SqlScriptParser.ParseText(
            "test.sql",
            "CREATE TABLE dbo.Customers (Id INT NOT NULL, Region VARCHAR(20) NOT NULL, INDEX IX_Customers_Region (Region));\nGO\n" + sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return ParameterReassignmentPredicateScanner.Scan(result, catalog);
    }
}
