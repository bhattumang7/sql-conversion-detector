using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Roadmap Phase E3: exercises <see cref="ExpressionDerivedVerifier"/> end-to-end against the
/// real oracle - closes the gap where <see cref="ExpressionDerivedFinding"/> had zero oracle
/// presence at all.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class ExpressionDerivedVerifierTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanExpressionDerivedVerifierTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;
    private readonly ExpressionDerivedVerifier _verifier;

    public ExpressionDerivedVerifierTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
        _verifier = new ExpressionDerivedVerifier(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, CustomerId INT NOT NULL, UnindexedFlag INT NOT NULL);
            GO
            CREATE INDEX IX_Orders_CustomerId ON dbo.Orders(CustomerId);
            GO
            CREATE VIEW dbo.vw_OrdersStr AS SELECT OrderId, CAST(CustomerId AS VARCHAR(20)) AS CustomerIdStr, CAST(UnindexedFlag AS VARCHAR(20)) AS FlagStr FROM dbo.Orders;
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private static ExpressionDerivedFinding Finding(
        string columnName, IReadOnlyList<UnderlyingBaseColumn> underlyingBaseColumns, string? predicateFragmentText, string? immediateRelation, string? alias = null) =>
        new(
            columnName, "file.sql", 1, 1,
            [new TransformationSite(null, 1, "CAST/CONVERT to VarChar")],
            underlyingBaseColumns,
            PredicateFragmentText: predicateFragmentText,
            ImmediateRelationQualifiedName: immediateRelation,
            ImmediateRelationAlias: alias);

    [Fact]
    public async Task VerifyAsync_ExpressionDerivedColumnOnIndexedUnderlyingColumn_ConfirmsNoIndexSeek()
    {
        var finding = Finding(
            "CustomerIdStr",
            [new UnderlyingBaseColumn("dbo.Orders", "CustomerId", Indexed: true)],
            "CustomerIdStr = '5'",
            "dbo.vw_OrdersStr");

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ExpressionDerivedOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_BareUnderlyingColumnQueriedDirectly_IsNotConfirmed()
    {
        // Negative control: querying the real base table's own column directly (bypassing the
        // CAST) DOES seek through its index - proves the plan-shape signal actually distinguishes
        // the expression-derived case from an ordinary indexed comparison.
        var finding = Finding(
            "CustomerId",
            [new UnderlyingBaseColumn("dbo.Orders", "CustomerId", Indexed: true)],
            "CustomerId = 5",
            "dbo.Orders");

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ExpressionDerivedOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_NoUnderlyingColumnIsIndexed_ReturnsConfirmedUnindexed()
    {
        var finding = Finding(
            "FlagStr",
            [new UnderlyingBaseColumn("dbo.Orders", "UnindexedFlag", Indexed: false)],
            "FlagStr = '1'",
            "dbo.vw_OrdersStr");

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ExpressionDerivedOutcome.UnindexedNotProbeable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_NoPredicateFragmentText_ReturnsNotProbeable()
    {
        var finding = Finding(
            "CustomerIdStr",
            [new UnderlyingBaseColumn("dbo.Orders", "CustomerId", Indexed: true)],
            predicateFragmentText: null,
            "dbo.vw_OrdersStr");

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ExpressionDerivedOutcome.NotProbeable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_NoImmediateRelation_ReturnsNotProbeable()
    {
        // Mirrors an inline derived table/CTE - no real, independently queryable object to
        // target, so this must not guess a fallback.
        var finding = Finding(
            "CustomerIdStr",
            [new UnderlyingBaseColumn("dbo.Orders", "CustomerId", Indexed: true)],
            "CustomerIdStr = '5'",
            immediateRelation: null);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ExpressionDerivedOutcome.NotProbeable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_RelationNoLongerInDeployedSchema_ReturnsProbeFailed()
    {
        var finding = Finding(
            "CustomerIdStr",
            [new UnderlyingBaseColumn("dbo.Orders", "CustomerId", Indexed: true)],
            "CustomerIdStr = '5'",
            "dbo.vw_DoesNotExist");

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(ExpressionDerivedOutcome.ProbeFailed, result.Outcome);
    }
}
