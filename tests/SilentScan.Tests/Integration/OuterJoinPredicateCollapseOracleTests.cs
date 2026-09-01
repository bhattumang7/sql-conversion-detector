using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;
using SilentScan.Verify.Catalog;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class OuterJoinPredicateCollapseOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(OuterJoinPredicateCollapseOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Parent
        (
            Id   INT NOT NULL PRIMARY KEY,
            Flag INT NULL
        );
        GO
        CREATE TABLE dbo.Child
        (
            Id       INT NOT NULL PRIMARY KEY,
            ParentId INT NULL,
            Status   VARCHAR(10) NULL,
            Amount   INT NULL
        );
        GO
        CREATE TABLE dbo.Extra
        (
            Id      INT NOT NULL PRIMARY KEY,
            ChildId INT NULL
        );
        GO
        CREATE PROCEDURE dbo.P_LeftJoinUnguardedFires AS
        BEGIN
            SELECT p.Id FROM dbo.Parent p LEFT JOIN dbo.Child c ON c.ParentId = p.Id WHERE c.Status = 'X';
        END
        GO
        CREATE PROCEDURE dbo.P_LeftJoinNonNullSideNoFire AS
        BEGIN
            SELECT p.Id FROM dbo.Parent p LEFT JOIN dbo.Child c ON c.ParentId = p.Id WHERE p.Id > 0;
        END
        GO
        CREATE PROCEDURE dbo.P_LeftJoinGuardedOrIsNullNoFire AS
        BEGIN
            SELECT p.Id FROM dbo.Parent p LEFT JOIN dbo.Child c ON c.ParentId = p.Id WHERE c.Status = 'X' OR c.Status IS NULL;
        END
        GO
        CREATE PROCEDURE dbo.P_LeftJoinPredicateInSubsequentOnClauseNoFire AS
        BEGIN
            SELECT p.Id
            FROM dbo.Parent p
            LEFT JOIN dbo.Child c ON c.ParentId = p.Id
            LEFT JOIN dbo.Extra e ON e.ChildId = c.Id AND c.Status = 'X';
        END
        GO
        CREATE PROCEDURE dbo.P_RightJoinUnguardedFires AS
        BEGIN
            SELECT p.Id FROM dbo.Child c RIGHT JOIN dbo.Parent p ON c.ParentId = p.Id WHERE c.Status = 'X';
        END
        GO
        CREATE PROCEDURE dbo.P_FullOuterJoinUnguardedFires AS
        BEGIN
            SELECT p.Id FROM dbo.Parent p FULL OUTER JOIN dbo.Child c ON c.ParentId = p.Id WHERE c.Status = 'X';
        END
        GO
        CREATE PROCEDURE dbo.P_InnerJoinNoFire AS
        BEGIN
            SELECT p.Id FROM dbo.Parent p INNER JOIN dbo.Child c ON c.ParentId = p.Id WHERE c.Status = 'X';
        END
        GO
        CREATE PROCEDURE dbo.P_IsNullWrappedNoFire AS
        BEGIN
            SELECT p.Id FROM dbo.Parent p LEFT JOIN dbo.Child c ON c.ParentId = p.Id WHERE ISNULL(c.Status, 'X') = 'X';
        END
        GO
        CREATE PROCEDURE dbo.P_InPredicateFires AS
        BEGIN
            SELECT p.Id FROM dbo.Parent p LEFT JOIN dbo.Child c ON c.ParentId = p.Id WHERE c.Status IN ('X', 'Y');
        END
        GO
        CREATE PROCEDURE dbo.P_BetweenFires AS
        BEGIN
            SELECT p.Id FROM dbo.Parent p LEFT JOIN dbo.Child c ON c.ParentId = p.Id WHERE c.Amount BETWEEN 1 AND 10;
        END
        GO
        CREATE PROCEDURE dbo.P_LikeFires AS
        BEGIN
            SELECT p.Id FROM dbo.Parent p LEFT JOIN dbo.Child c ON c.ParentId = p.Id WHERE c.Status LIKE 'X%';
        END
        GO
        CREATE PROCEDURE dbo.P_UpdateFromFires AS
        BEGIN
            UPDATE p SET p.Flag = 1
            FROM dbo.Parent p LEFT JOIN dbo.Child c ON c.ParentId = p.Id
            WHERE c.Status = 'X';
        END
        GO
        CREATE PROCEDURE dbo.P_DeleteFromFires AS
        BEGIN
            DELETE p
            FROM dbo.Parent p LEFT JOIN dbo.Child c ON c.ParentId = p.Id
            WHERE c.Status = 'X';
        END
        GO
        """;

    private async Task<IReadOnlyList<OuterJoinPredicateCollapseFinding>> ScanAsync()
    {
        var connectionString = Options.BuildConnectionString(DatabaseName);
        var catalog = await new LiveCatalogReader(connectionString).ReadAsync();
        var moduleResult = await new LiveModuleReader(connectionString).ReadAsync();

        var findings = new List<OuterJoinPredicateCollapseFinding>();
        foreach (var module in moduleResult.Modules)
        {
            var parseResult = SqlScriptParser.ParseText(module.QualifiedName, module.Definition, module.UsesQuotedIdentifier, catalog.CompatibilityLevel);
            findings.AddRange(OuterJoinPredicateCollapseScanner.Scan(parseResult, catalog));
        }

        return findings;
    }

    private async Task<IReadOnlyList<OuterJoinPredicateCollapseFinding>> FindingsForAsync(string procedureNameFragment)
    {
        var findings = await ScanAsync();
        return [.. findings.Where(f => f.SourcePath.Contains(procedureNameFragment, StringComparison.OrdinalIgnoreCase))];
    }

    [Fact]
    public async Task LeftJoinUnguardedPredicateOnNullSupplyingSide_Fires()
    {
        var findings = await FindingsForAsync("P_LeftJoinUnguardedFires");

        var finding = Assert.Single(findings);
        Assert.Equal(OuterJoinPredicateCollapseKind.LeftOuterJoin, finding.Kind);
        Assert.Equal("Status", finding.ColumnName);
        Assert.Contains("Child", finding.NullSupplyingTableQualifiedName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeftJoinPredicateOnNonNullSide_DoesNotFire()
    {
        var findings = await FindingsForAsync("P_LeftJoinNonNullSideNoFire");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task LeftJoinPredicateGuardedByOrIsNull_DoesNotFire()
    {
        var findings = await FindingsForAsync("P_LeftJoinGuardedOrIsNullNoFire");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task PredicateInSubsequentJoinsOnClause_DoesNotFire()
    {
        var findings = await FindingsForAsync("P_LeftJoinPredicateInSubsequentOnClauseNoFire");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task RightJoinUnguardedPredicateOnNullSupplyingSide_Fires()
    {
        var findings = await FindingsForAsync("P_RightJoinUnguardedFires");

        var finding = Assert.Single(findings);
        Assert.Equal(OuterJoinPredicateCollapseKind.RightOuterJoin, finding.Kind);
        Assert.Contains("Child", finding.NullSupplyingTableQualifiedName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullOuterJoinUnguardedPredicate_Fires()
    {
        var findings = await FindingsForAsync("P_FullOuterJoinUnguardedFires");

        var finding = Assert.Single(findings);
        Assert.Equal(OuterJoinPredicateCollapseKind.FullOuterJoin, finding.Kind);
    }

    [Fact]
    public async Task InnerJoinPredicate_NeverFires()
    {
        var findings = await FindingsForAsync("P_InnerJoinNoFire");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task IsNullWrappedNullSupplyingColumn_DoesNotFire()
    {
        var findings = await FindingsForAsync("P_IsNullWrappedNoFire");

        Assert.Empty(findings);
    }

    [Fact]
    public async Task InPredicateOnNullSupplyingSide_Fires()
    {
        var findings = await FindingsForAsync("P_InPredicateFires");

        Assert.Single(findings);
    }

    [Fact]
    public async Task BetweenPredicateOnNullSupplyingSide_Fires()
    {
        var findings = await FindingsForAsync("P_BetweenFires");

        Assert.Single(findings);
    }

    [Fact]
    public async Task LikePredicateOnNullSupplyingSide_Fires()
    {
        var findings = await FindingsForAsync("P_LikeFires");

        Assert.Single(findings);
    }

    [Fact]
    public async Task UpdateFromWithUnguardedPredicate_Fires()
    {
        var findings = await FindingsForAsync("P_UpdateFromFires");

        Assert.Single(findings);
    }

    [Fact]
    public async Task DeleteFromWithUnguardedPredicate_Fires()
    {
        var findings = await FindingsForAsync("P_DeleteFromFires");

        Assert.Single(findings);
    }
}
