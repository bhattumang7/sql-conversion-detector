using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class ViewCheckOptionContradictionOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(ViewCheckOptionContradictionOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Orders
        (
            OrderId INT NOT NULL PRIMARY KEY,
            Amount  INT NOT NULL,
            Status  VARCHAR(10) NOT NULL
        );
        GO
        CREATE VIEW dbo.ActiveOrders
        AS
            SELECT OrderId, Amount, Status FROM dbo.Orders WHERE Amount > 10
        WITH CHECK OPTION;
        GO
        CREATE VIEW dbo.PlainOrders
        AS
            SELECT OrderId, Amount, Status FROM dbo.Orders WHERE Amount > 10;
        GO
        CREATE PROCEDURE dbo.P_InsertLiteralBelowRange AS
        BEGIN
            INSERT INTO dbo.ActiveOrders (OrderId, Amount, Status) VALUES (1, 5, 'A');
        END
        GO
        CREATE PROCEDURE dbo.P_InsertLiteralWithinRange AS
        BEGIN
            INSERT INTO dbo.ActiveOrders (OrderId, Amount, Status) VALUES (2, 50, 'A');
        END
        GO
        CREATE PROCEDURE dbo.P_UpdateLiteralBelowRange AS
        BEGIN
            UPDATE dbo.ActiveOrders SET Amount = 5 WHERE OrderId = 1;
        END
        GO
        CREATE PROCEDURE dbo.P_UpdateLiteralWithinRange AS
        BEGIN
            UPDATE dbo.ActiveOrders SET Amount = 50 WHERE OrderId = 1;
        END
        GO
        CREATE PROCEDURE dbo.P_InsertPlainViewNoCheckOption AS
        BEGIN
            INSERT INTO dbo.PlainOrders (OrderId, Amount, Status) VALUES (1, 5, 'A');
        END
        GO
        CREATE PROCEDURE dbo.P_InsertParameterNoFire AS
        BEGIN
            DECLARE @amt INT = 5;
            INSERT INTO dbo.ActiveOrders (OrderId, Amount, Status) VALUES (1, @amt, 'A');
        END
        GO
        """;

    private async Task<HashSet<string>> ProcedureNamesWithFindingsAsync()
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        var catalog = await new LiveCatalogReader(connectionString).ReadAsync();
        var moduleResult = await new LiveModuleReader(connectionString).ReadAsync();

        var parseResults = moduleResult.Modules
            .Select(m => SqlScriptParser.ParseText(m.QualifiedName, m.Definition, m.UsesQuotedIdentifier, catalog.CompatibilityLevel))
            .ToList();

        var (views, _) = ViewDefinitionExtractor.Extract(parseResults, catalog.DefaultCollation, catalog.TypeAliases, ledger: null);

        var findings = new List<ViewCheckOptionContradictionFinding>();
        foreach (var parseResult in parseResults)
        {
            findings.AddRange(ViewCheckOptionContradictionScanner.Scan(parseResult, catalog, views));
        }

        return [.. findings.Select(f => f.SourcePath)];
    }

    [Fact]
    public async Task InsertLiteralOutsideCheckOptionRange_Fires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.Contains(procedures, p => p.Contains("P_InsertLiteralBelowRange", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InsertLiteralWithinCheckOptionRange_DoesNotFire()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_InsertLiteralWithinRange", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateLiteralOutsideCheckOptionRange_Fires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.Contains(procedures, p => p.Contains("P_UpdateLiteralBelowRange", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateLiteralWithinCheckOptionRange_DoesNotFire()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_UpdateLiteralWithinRange", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ViewWithoutCheckOption_NeverFiresEvenThoughLiteralWouldViolateItsWhereClause()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_InsertPlainViewNoCheckOption", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InsertParameterValue_NeverFires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_InsertParameterNoFire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LiveDeployment_InsertOutsideCheckOptionRange_ActuallyFailsAtExecution()
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO dbo.ActiveOrders (OrderId, Amount, Status) VALUES (99, 1, 'A');";

        var exception = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(550, exception.Number);
    }
}
