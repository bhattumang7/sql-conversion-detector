using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md "Hint and index-shape catalog checks": "Hint validity against the
/// catalog" - oracle-confirms both real behavioral claims <see cref="IndexHintFindingKind"/>
/// makes: a nonexistent index name is a hard compile error (Msg 308), and a real index hinted
/// with its own leading key column unbound anywhere degrades the forced access path from a clean
/// seek to a full scan - the hint REQUIRES this specific index rather than merely suggesting it,
/// so the optimizer cannot route around the missing leading-column bound the way it normally would.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class IndexHintOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(IndexHintOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Orders (Id INT NOT NULL, Region INT NOT NULL, Status INT NOT NULL);
        GO
        CREATE UNIQUE CLUSTERED INDEX CIX_Orders ON dbo.Orders(Id);
        GO
        CREATE NONCLUSTERED INDEX IX_Orders_Region ON dbo.Orders(Region);
        GO
        """;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // A tiny/empty table can seek trivially regardless of which index is forced - real
        // population plus fresh statistics is what makes the degradation actually observable,
        // the same trap this codebase's other plan-shape oracle tests already document.
        var seedRows = """
            INSERT INTO dbo.Orders (Id, Region, Status)
            SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 50, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 50
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE STATISTICS dbo.Orders WITH FULLSCAN;
            """;
        await new ScriptDeployer(Options).DeployAsync(seedRows, DatabaseName);
    }

    [Fact]
    public async Task HintNamingNonexistentIndex_RaisesMsg308()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT * FROM dbo.Orders WITH (INDEX(IX_DoesNotExist)) WHERE Id = 5;", connection);

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(308, exception.Number);
        Assert.Contains("does not exist", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoHint_PredicateOnClusteredKey_SeeksCleanly()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(DatabaseName, "SELECT * FROM dbo.Orders WHERE Id = 5;");

        Assert.Contains("PhysicalOp=\"Clustered Index Seek\"", planXml);
    }

    [Fact]
    public async Task HintForcesIndexWhoseLeadingColumnIsUnbound_DegradesToScan()
    {
        // The predicate is on Id (the clustered key), but the hint forces IX_Orders_Region, whose
        // own leading column (Region) is never referenced anywhere in this statement.
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(
            DatabaseName, "SELECT * FROM dbo.Orders WITH (INDEX(IX_Orders_Region)) WHERE Id = 5;");

        Assert.Contains("PhysicalOp=\"Index Scan\"", planXml);
        Assert.DoesNotContain("PhysicalOp=\"Index Seek\"", planXml);
    }

    [Fact]
    public async Task HintForcesIndexWhoseLeadingColumnIsBound_StaysASeek()
    {
        var planXml = await new PlanXmlCapture(Options).CaptureAsync(
            DatabaseName, "SELECT * FROM dbo.Orders WITH (INDEX(IX_Orders_Region)) WHERE Region = 5;");

        Assert.Contains("PhysicalOp=\"Index Seek\"", planXml);
    }
}
