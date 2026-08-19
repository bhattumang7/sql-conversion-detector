using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Catch-all / kitchen-sink predicates" sibling: "parameter
/// overwritten before use in a predicate" (sniffing-defeat). Oracle-confirms the general
/// mechanism once (the same "confirm the premise directly, not per finding" precedent
/// <see cref="LocalVariablePredicateOracleTests"/> already established): a stored procedure's
/// cached plan is compiled against the ARGUMENT the caller actually supplied (parameter
/// sniffing) - reassigning that same parameter inside the body before a later predicate use does
/// NOT recompile anything or change what was sniffed. The predicate's own row-count estimate
/// still reflects the ORIGINAL sniffed argument, even though the predicate executes against the
/// NEW, reassigned value - exactly the staleness <see cref="ParameterReassignmentPredicateFinding"/>
/// exists to report.
///
/// A genuinely compile-time phenomenon, like <see cref="LocalVariablePredicateOracleTests"/> (not
/// like the catch-all stream's own RECOMPILE finding, which needed real execution) - parameter
/// sniffing for a stored-procedure EXEC is fully visible to the compile-only
/// <c>SET SHOWPLAN_XML ON</c> probe <see cref="PlanXmlCapture"/> already uses.
/// </summary>
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

        // Control: calling usp_FindDirect with the common value sniffs 'Common' AND the predicate
        // genuinely runs against 'Common' - the real histogram step applies, a large estimate.
        var directPlan = await capture.CaptureAsync(DatabaseName, "EXEC dbo.usp_FindDirect @p = 'Common';");

        // The body reassigns @p to a value with ZERO real rows before the predicate runs - if the
        // plan were built against the value the predicate ACTUALLY compares, the estimate would be
        // the near-zero "value never seen" density guess. Reassignment happens BEFORE the predicate
        // executes, but sniffing happened at compile time against the ORIGINAL 'Common' argument -
        // the compiled plan cannot see the reassignment at all.
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
}
