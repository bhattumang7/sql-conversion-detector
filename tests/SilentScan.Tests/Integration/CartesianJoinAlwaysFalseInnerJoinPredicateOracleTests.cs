using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class CartesianJoinAlwaysFalseInnerJoinPredicateOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(CartesianJoinAlwaysFalseInnerJoinPredicateOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Orders
        (
            OrderId INT NOT NULL PRIMARY KEY,
            Amount  INT NOT NULL,
            Status  VARCHAR(10) NOT NULL
        );
        GO
        CREATE TABLE dbo.OrderDetails
        (
            OrderId INT NOT NULL,
            Note    VARCHAR(50) NULL
        );
        GO
        CREATE PROCEDURE dbo.P_LiteralAlwaysFalse AS
        BEGIN
            SELECT o.OrderId FROM dbo.Orders o INNER JOIN dbo.OrderDetails d ON 1 = 0;
        END
        GO
        CREATE PROCEDURE dbo.P_ColumnContradiction AS
        BEGIN
            SELECT o.OrderId FROM dbo.Orders o INNER JOIN dbo.OrderDetails d ON o.Amount > 2000 AND o.Amount < -100;
        END
        GO
        CREATE PROCEDURE dbo.P_RealJoinKey_NoFire AS
        BEGIN
            SELECT o.OrderId FROM dbo.Orders o INNER JOIN dbo.OrderDetails d ON o.OrderId = d.OrderId;
        END
        GO
        CREATE PROCEDURE dbo.P_AlwaysTrue_NoFire AS
        BEGIN
            SELECT o.OrderId FROM dbo.Orders o INNER JOIN dbo.OrderDetails d ON 1 = 1;
        END
        GO
        CREATE PROCEDURE dbo.P_LeftOuterAlwaysFalse_NoFire AS
        BEGIN
            SELECT o.OrderId FROM dbo.Orders o LEFT JOIN dbo.OrderDetails d ON 1 = 0;
        END
        GO
        CREATE PROCEDURE dbo.P_RealKeyPlusContradiction_Fires AS
        BEGIN
            SELECT o.OrderId FROM dbo.Orders o INNER JOIN dbo.OrderDetails d ON o.OrderId = d.OrderId AND 1 = 0;
        END
        GO
        CREATE PROCEDURE dbo.P_RealKeyOrContradiction_NoFire AS
        BEGIN
            SELECT o.OrderId FROM dbo.Orders o INNER JOIN dbo.OrderDetails d ON o.OrderId = d.OrderId OR 1 = 0;
        END
        GO
        CREATE PROCEDURE dbo.P_ParameterComparison_NoFire AS
        BEGIN
            DECLARE @x INT = 1;
            SELECT o.OrderId FROM dbo.Orders o INNER JOIN dbo.OrderDetails d ON @x = 1 AND @x = 2;
        END
        GO
        """;

    private async Task<IReadOnlyList<CartesianJoinFinding>> ScanAsync()
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        var catalog = await new LiveCatalogReader(connectionString).ReadAsync();
        var moduleResult = await new LiveModuleReader(connectionString).ReadAsync();

        var findings = new List<CartesianJoinFinding>();
        foreach (var module in moduleResult.Modules)
        {
            var parseResult = SqlScriptParser.ParseText(module.QualifiedName, module.Definition, module.UsesQuotedIdentifier, catalog.CompatibilityLevel);
            findings.AddRange(CartesianJoinScanner.Scan(parseResult, catalog));
        }

        return findings;
    }

    private async Task<HashSet<string>> ProcedureNamesWithFindingsAsync(CartesianJoinKind? kind = null)
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
    public async Task LiteralAlwaysFalsePredicate_Fires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync(CartesianJoinKind.AlwaysFalseInnerJoinPredicate);

        Assert.Contains(procedures, p => p.Contains("P_LiteralAlwaysFalse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SingleColumnContradiction_Fires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync(CartesianJoinKind.AlwaysFalseInnerJoinPredicate);

        Assert.Contains(procedures, p => p.Contains("P_ColumnContradiction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RealJoinKey_DoesNotFire()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_RealJoinKey_NoFire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AlwaysTruePredicate_DoesNotFireThisRule()
    {
        var procedures = await ProcedureNamesWithFindingsAsync(CartesianJoinKind.AlwaysFalseInnerJoinPredicate);

        Assert.DoesNotContain(procedures, p => p.Contains("P_AlwaysTrue_NoFire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LeftOuterJoinWithAlwaysFalsePredicate_DoesNotFire()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_LeftOuterAlwaysFalse_NoFire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RealJoinKeyAndedWithContradiction_StillFires()
    {
        var procedures = await ProcedureNamesWithFindingsAsync(CartesianJoinKind.AlwaysFalseInnerJoinPredicate);

        Assert.Contains(procedures, p => p.Contains("P_RealKeyPlusContradiction_Fires", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RealJoinKeyOredWithContradiction_DoesNotFire()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_RealKeyOrContradiction_NoFire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ParameterComparisonContradiction_DoesNotFire()
    {
        var procedures = await ProcedureNamesWithFindingsAsync();

        Assert.DoesNotContain(procedures, p => p.Contains("P_ParameterComparison_NoFire", StringComparison.OrdinalIgnoreCase));
    }
}
