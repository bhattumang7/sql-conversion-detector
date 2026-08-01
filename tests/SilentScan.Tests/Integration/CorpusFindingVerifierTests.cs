using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;
using SilentScan.Verify;
using SilentScan.Verify.Deployment;
using SilentScan.Verify.Oracle;

namespace SilentScan.Tests.Integration;

/// <summary>
/// Exercises <see cref="CorpusFindingVerifier"/> end-to-end against the real oracle, locking
/// in the formal Verify pass wired into `silentscan-verify verify-corpus` (CLAUDE.md
/// Verification workflow: "for each SCAN_FORCED finding, execute a parameterized probe ...
/// and confirm CONVERT_IMPLICIT-on-column").
/// </summary>
public sealed class CorpusFindingVerifierTests : IAsyncLifetime
{
    private const string DatabaseName = "SilentScanFindingVerifierTest";

    private readonly SqlServerOptions _options = SqlServerOptions.LocalDocker;
    private readonly DatabaseProvisioner _provisioner;
    private readonly CorpusFindingVerifier _verifier;

    public CorpusFindingVerifierTests()
    {
        _provisioner = new DatabaseProvisioner(_options);
        _verifier = new CorpusFindingVerifier(_options);
    }

    public async Task InitializeAsync()
    {
        await _provisioner.CreateFreshAsync(DatabaseName);
        await new ScriptDeployer(_options).DeployAsync(
            """
            CREATE TABLE dbo.CodeFrequency (Code CHAR(1) NOT NULL PRIMARY KEY);
            GO
            CREATE TABLE dbo.Orders (OrderId INT NOT NULL PRIMARY KEY, OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE INDEX IX_Orders_OrderCode ON dbo.Orders(OrderCode);
            GO
            CREATE TABLE dbo.Customers (CustomerId INT NOT NULL PRIMARY KEY, CustomerCode VARCHAR(20) COLLATE Latin1_General_CI_AS NOT NULL);
            GO
            CREATE INDEX IX_Customers_CustomerCode ON dbo.Customers(CustomerCode);
            GO
            """,
            DatabaseName);
    }

    public async Task DisposeAsync() =>
        await _provisioner.DropIfExistsAsync(DatabaseName);

    private static PredicateOperand.Column ColumnOperand(string table, string column, SqlType type, bool indexed = false) =>
        new(table, column, type, indexed, Depth: 0, new ColumnProvenance.BaseColumn(table, column, type));

    [Fact]
    public async Task VerifyAsync_CharColumnVsIntLiteral_ConfirmsColumnSideConversion()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.CodeFrequency", "Code", new SqlType(SqlTypeCategory.Char, Length: 1)),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)),
            "<>",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_SqlCollationVarcharColumnVsNVarcharValue_ConfirmsColumnSideConversion()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_WindowsCollationVarcharColumnVsNVarcharValue_RangeSeekVerdict_IsConfirmed()
    {
        // docs/audit-remediation-plan.md Phase 5.1: a Windows-collation column genuinely
        // produces the dynamic-range-seek plan shape a RangeSeek verdict predicts - verified
        // directly against the real engine (GetRangeThroughConvert present, Index Seek used).
        var finding = new TypedPredicateFinding(
            Verdict.RangeSeek,
            ColumnOperand("dbo.Customers", "CustomerCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("Latin1_General_CI_AS")), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_SqlCollationColumn_RangeSeekVerdictButPlanIsActuallyScanForced_IsNotConfirmed()
    {
        // Same column-side conversion as VerifyAsync_SqlCollationVarcharColumnVsNVarcharValue_
        // ConfirmsColumnSideConversion, but with a RangeSeek verdict attached instead of
        // ScanForced - a SQL_* collation plan never shows the dynamic-seek machinery a
        // RangeSeek verdict predicts, so this must NOT be confirmed even though the column
        // does convert. Proves conversion presence alone is no longer sufficient to confirm a
        // RangeSeek/ScanForced finding (audit finding C1).
        var finding = new TypedPredicateFinding(
            Verdict.RangeSeek,
            ColumnOperand("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_WindowsCollationColumn_ScanForcedVerdictButPlanIsActuallyRangeSeek_IsNotConfirmed()
    {
        // The mirror-image mismatch: a Windows-collation column DOES seek via
        // GetRangeThroughConvert, so a ScanForced verdict against it is wrong and must not be
        // confirmed just because the column happened to convert.
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Customers", "CustomerCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("Latin1_General_CI_AS")), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_LiteralOperand_ProbesTheReconstructedLiteralNotAVariable()
    {
        // docs/audit-remediation-plan.md Phase 5.2, audit finding C2: end-to-end proof that a
        // literal-sourced finding's probe actually uses the finding's IsLiteral/LiteralText
        // fields, not a same-typed DECLARE - both still confirm here (same collation family
        // conclusion either way for this pair), but this locks in that the literal path is
        // wired all the way through Verify, not just present on the finding record.
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS")), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 5), IsLiteral: true, LiteralText: "N'Alice'"),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_LiteralOperandThatCannotBeReconstructed_ReturnsNotProbeableWithFidelityCaveat()
    {
        var finding = new TypedPredicateFinding(
            Verdict.Unknown,
            ColumnOperand("dbo.Orders", "OrderId", new SqlType(SqlTypeCategory.Int), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int), IsLiteral: true, LiteralText: null),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotProbeable, result.Outcome);
        Assert.Contains("misrepresent probe fidelity", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_IntColumnVsBigIntValue_SameFamilyWidening_IsNotConfirmed()
    {
        // A static classifier bug would have called this ScanForced before the same-family
        // widening fix; the oracle must show no column-side conversion regardless of what
        // verdict is passed in, since the probe only cares about the real plan.
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Orders", "OrderId", new SqlType(SqlTypeCategory.Int), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.BigInt)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotConfirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ColumnVsColumnAcrossTables_ConfirmsColumnSideConversion()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.CodeFrequency", "Code", new SqlType(SqlTypeCategory.Char, Length: 1)),
            ColumnOperand("dbo.Orders", "OrderId", new SqlType(SqlTypeCategory.Int)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_UnprobeableOtherOperandType_ReturnsNotProbeable()
    {
        var finding = new TypedPredicateFinding(
            Verdict.Unknown,
            ColumnOperand("dbo.CodeFrequency", "Code", new SqlType(SqlTypeCategory.Char, Length: 1)),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.UserDefined)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotProbeable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ColumnNoLongerExistsInDeployedSchema_ReturnsProbeFailed()
    {
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.DoesNotExist", "Missing", new SqlType(SqlTypeCategory.Int)),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.ProbeFailed, result.Outcome);
    }
}
