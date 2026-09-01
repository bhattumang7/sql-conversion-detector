using Microsoft.Data.SqlClient;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class CheckConstraintPredicateContradictionOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(CheckConstraintPredicateContradictionOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Orders
        (
            OrderId INT NOT NULL PRIMARY KEY,
            Amount  INT NOT NULL,
            Qty     INT NOT NULL,
            Status  VARCHAR(10) NOT NULL
        );
        GO
        ALTER TABLE dbo.Orders ADD CONSTRAINT CK_Orders_Amount CHECK (Amount > 0 AND Amount < 1000);
        GO
        ALTER TABLE dbo.Orders WITH NOCHECK ADD CONSTRAINT CK_Orders_QtyUntrusted CHECK (Qty > 100);
        GO
        CREATE TABLE dbo.Notes
        (
            NoteId INT NOT NULL PRIMARY KEY,
            Body   INT NULL
        );
        GO
        ALTER TABLE dbo.Notes ADD CONSTRAINT CK_Notes_Body CHECK (Body > 0);
        GO
        CREATE PROCEDURE dbo.P_ContradictsAbove AS
        BEGIN
            SELECT OrderId FROM dbo.Orders WHERE Amount > 2000;
        END
        GO
        CREATE PROCEDURE dbo.P_WithinRange AS
        BEGIN
            SELECT OrderId FROM dbo.Orders WHERE Amount > 500;
        END
        GO
        CREATE PROCEDURE dbo.P_BetweenContradicts AS
        BEGIN
            SELECT OrderId FROM dbo.Orders WHERE Amount BETWEEN 2000 AND 3000;
        END
        GO
        CREATE PROCEDURE dbo.P_UntrustedCheckNoFire AS
        BEGIN
            SELECT OrderId FROM dbo.Orders WHERE Qty < 0;
        END
        GO
        CREATE PROCEDURE dbo.P_NotNullColumnQueriedForNull AS
        BEGIN
            SELECT OrderId FROM dbo.Orders WHERE Amount IS NULL;
        END
        GO
        CREATE PROCEDURE dbo.P_NullableCheckedColumnContradicts AS
        BEGIN
            SELECT NoteId FROM dbo.Notes WHERE Body < 0;
        END
        GO
        CREATE PROCEDURE dbo.P_NullableColumnQueriedForNull AS
        BEGIN
            SELECT NoteId FROM dbo.Notes WHERE Body IS NULL;
        END
        GO
        CREATE PROCEDURE dbo.P_ParameterNoFire AS
        BEGIN
            DECLARE @amt INT = -5;
            SELECT OrderId FROM dbo.Orders WHERE Amount > @amt;
        END
        GO
        CREATE PROCEDURE dbo.P_OrMixedBranchNoFire AS
        BEGIN
            SELECT OrderId FROM dbo.Orders WHERE Amount > 2000 OR Status = 'A';
        END
        GO
        CREATE PROCEDURE dbo.P_OrBothBranchesContradictFires AS
        BEGIN
            SELECT OrderId FROM dbo.Orders WHERE Amount > 2000 OR Amount < -100;
        END
        GO
        """;

    private async Task<IReadOnlyList<CheckConstraintPredicateContradictionFinding>> ScanAsync()
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        var catalog = await new LiveCatalogReader(connectionString).ReadAsync();
        var moduleResult = await new LiveModuleReader(connectionString).ReadAsync();

        var findings = new List<CheckConstraintPredicateContradictionFinding>();
        foreach (var module in moduleResult.Modules)
        {
            var parseResult = SqlScriptParser.ParseText(module.QualifiedName, module.Definition, module.UsesQuotedIdentifier, catalog.CompatibilityLevel);
            findings.AddRange(CheckConstraintPredicateContradictionScanner.Scan(parseResult, catalog));
        }

        return findings;
    }

    private async Task<HashSet<string>> ProcedureNamesWithFindingsAsync(CheckConstraintPredicateContradictionKind? kind = null)
    {
        var findings = await ScanAsync();
        return
        [
            .. findings
                .Where(f => kind is null || f.Kind == kind)
                .Select(f => f.SourcePath),
        ];
    }

    [Fact]
    public async Task LiteralAboveTrustedCheckUpperBound_Fires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync(CheckConstraintPredicateContradictionKind.CheckConstraintInterval);

        Assert.Contains(procedures, p => p.Contains("P_ContradictsAbove", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LiteralInsideTrustedCheckRange_DoesNotFire()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_WithinRange", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BetweenRangeOutsideTrustedCheck_Fires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync(CheckConstraintPredicateContradictionKind.CheckConstraintInterval);

        Assert.Contains(procedures, p => p.Contains("P_BetweenContradicts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UntrustedCheckConstraint_DoesNotFireEvenThoughLiteralWouldContradictIt()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_UntrustedCheckNoFire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NotNullColumnQueriedForNull_Fires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync(CheckConstraintPredicateContradictionKind.NotNullConstraint);

        Assert.Contains(procedures, p => p.Contains("P_NotNullColumnQueriedForNull", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NullableColumnWithTrustedCheck_LiteralOutsideIntervalStillFires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync(CheckConstraintPredicateContradictionKind.CheckConstraintInterval);

        Assert.Contains(procedures, p => p.Contains("P_NullableCheckedColumnContradicts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NullableColumnQueriedForNull_DoesNotFire()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_NullableColumnQueriedForNull", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ParameterComparison_NeverFires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_ParameterNoFire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OrWithOneNonContradictingBranch_DoesNotFire()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_OrMixedBranchNoFire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OrWithBothBranchesContradicting_Fires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync(CheckConstraintPredicateContradictionKind.CheckConstraintInterval);

        Assert.Contains(procedures, p => p.Contains("P_OrBothBranchesContradictFires", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RealCatalog_CheckConstraintTrustAndDisabledFlags_MatchWhatTheScannerRelaysOn()
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        var catalog = await new LiveCatalogReader(connectionString).ReadAsync();

        var amountCheck = Assert.Single(catalog.CheckConstraints, c => string.Equals(c.ConstraintName, "CK_Orders_Amount", StringComparison.OrdinalIgnoreCase));
        Assert.False(amountCheck.IsNotTrusted);
        Assert.False(amountCheck.IsDisabled);

        var qtyCheck = Assert.Single(catalog.CheckConstraints, c => string.Equals(c.ConstraintName, "CK_Orders_QtyUntrusted", StringComparison.OrdinalIgnoreCase));
        Assert.True(qtyCheck.IsNotTrusted);
        Assert.False(qtyCheck.IsDisabled);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_not_trusted, is_disabled FROM sys.check_constraints WHERE name = 'CK_Orders_QtyUntrusted';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
    }
}
