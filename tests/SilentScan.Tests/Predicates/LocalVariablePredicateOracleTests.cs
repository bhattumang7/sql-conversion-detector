using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Local-variable predicates" - oracle-confirms the general
/// mechanism once (per this session's own precedent - not per finding): comparing a column to a
/// value the optimizer can see at compile time (a sniffed/literal formal-parameter value) versus
/// a value it genuinely cannot (a <c>DECLARE</c>'d local variable, whose runtime value is opaque
/// to the compiler even though it is a compile-time-fixed value for that one compile) produces
/// materially different cardinality estimates against a skewed column - this is exactly the
/// "invisible to estimator" premise <see cref="LocalVariablePredicateFinding"/>'s own doc comment
/// states.
///
/// Unlike the catch-all-predicate stream's oracle (<see cref="CatchAllPredicateOracleTests"/>),
/// this is a genuinely COMPILE-TIME phenomenon - the local-variable estimate is a fixed density
/// guess baked in at compile time, not something that only reveals itself at execution - so the
/// existing compile-only <see cref="PlanXmlCapture"/> (<c>SET SHOWPLAN_XML ON</c>) is the right
/// tool here, unlike for RECOMPILE.
/// </summary>
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

        // The literal is compile-time visible, so the optimizer can use the real histogram step
        // for 'Common' (1900 of 2000 rows) - a large, accurate estimate.
        var literalPlan = await capture.CaptureAsync(
            DatabaseName, "SELECT Id FROM dbo.Customers WHERE Region = 'Common';");

        // The DECLARE'd local variable's value is opaque to the compiler even though it is fixed
        // for this one compile, so the optimizer falls back to a generic density guess instead of
        // the real histogram step - producing a materially different (much smaller) estimate for
        // the exact same underlying value.
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
        // The relevant RelOp is the first (outermost) EstimateRows in the plan - the Index
        // Seek/Scan operator on Customers, which is what the predicate's selectivity drives.
        const string Marker = "EstimateRows=\"";
        var start = planXml.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "expected the plan XML to contain an EstimateRows attribute.");
        start += Marker.Length;
        var end = planXml.IndexOf('"', start);
        return double.Parse(planXml[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }
}
