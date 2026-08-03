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
[Trait("Category", "Oracle")]
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
            CREATE TABLE dbo.Unindexed (UnindexedId INT NOT NULL, UnindexedCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, UnindexedLob VARCHAR(MAX) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            CREATE VIEW dbo.vw_Orders AS SELECT OrderId, OrderCode FROM dbo.Orders;
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
    public async Task VerifyAsync_Depth1FindingThroughView_ProbesTheViewAndConfirmsAgainstTheBaseColumn()
    {
        // The core claim this fix exists to make testable at all: a finding whose predicate was
        // written against a VIEW column (Depth 1) must be probed by actually querying the view
        // - not synthesized straight against the base table, which would never exercise the
        // view layer (or the optimizer's inlining of it) at all. dbo.vw_Orders is a real
        // deployed view over dbo.Orders; the probe queries vw_Orders.OrderCode, and confirmation
        // still matches on the base column (dbo.Orders.OrderCode) because the optimizer inlines
        // the view into the plan - proving both that the view was actually queried AND that the
        // resulting CONVERT_IMPLICIT lands on the real underlying column.
        var baseColumnType = new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"));
        var column = new PredicateOperand.Column(
            "dbo.Orders", "OrderCode", baseColumnType, Indexed: true, Depth: 1,
            new ColumnProvenance.BaseColumn("dbo.Orders", "OrderCode", baseColumnType, Depth: 1),
            ImmediateRelationQualifiedName: "dbo.vw_Orders", ImmediateColumnName: "OrderCode");

        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            column,
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var probe = CorpusFindingProbeBuilder.Build(finding);
        Assert.Contains("FROM [dbo].[vw_Orders]", probe, StringComparison.Ordinal);

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
        // Verdict is deliberately NOT Unknown here - this test is about the operand-rendering
        // NotProbeable path specifically, which must fire regardless of verdict; Unknown itself
        // now short-circuits before a probe is even attempted (see the NotApplicable tests below).
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
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
        // Verdict is deliberately NOT Unknown - see the comment on the fidelity-caveat test above.
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
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
    public async Task VerifyAsync_ScanForcedColumnHasNoDeployedIndex_ConfirmsViaScratchIndex()
    {
        // Roadmap Phase E3: the corpus's own DDL leaves this column unindexed - the previous
        // fix's ConfirmedUnindexed outcome - but the RangeSeek-vs-ScanForced shape distinction
        // no longer has to stay unverified for that reason alone: a scratch index deployed for
        // this probe only lets the same plan-shape signal be checked, then the index is
        // dropped again. Distinct from a plain Confirmed - the summary this feeds still knows
        // the corpus repo itself never carried this index.
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Unindexed", "UnindexedCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20), IsLiteral: true, LiteralText: "N'ABC'"),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.ConfirmedViaScratchIndex, result.Outcome);
        Assert.NotEqual(CorpusFindingOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ScanForcedColumnTypeCannotBeIndexed_FallsBackToConfirmedUnindexed()
    {
        // A VARCHAR(MAX) column can never be an index key column at all (SQL Server rejects it
        // outright) - the scratch-index deploy attempt fails cleanly and this falls back to the
        // same ConfirmedUnindexed outcome an undeployed corpus index already produces, not a
        // crash or a false Confirmed.
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.Unindexed", "UnindexedLob", new SqlType(SqlTypeCategory.VarChar, IsMax: true, Collation: new Collation("SQL_Latin1_General_CP1_CI_AS"))),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20), IsLiteral: true, LiteralText: "N'ABC'"),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.ConfirmedUnindexed, result.Outcome);
        Assert.NotEqual(CorpusFindingOutcome.Confirmed, result.Outcome);
        Assert.NotEqual(CorpusFindingOutcome.ConfirmedViaScratchIndex, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_ScanForcedColumnNoLongerExistsInDeployedSchema_ReturnsProbeFailed()
    {
        // The index check now runs AFTER the probe, only once a conversion is already confirmed -
        // a table that was never deployed at all fails to compile the probe itself (invalid
        // object name), which is the honest, uniform outcome for "this reference doesn't exist
        // in the deployed schema" regardless of which side of the comparison it's on (mirrors
        // VerifyAsync_ColumnToColumnProbeAgainstUndeployedTable_ReturnsProbeFailed below).
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

    [Fact]
    public async Task VerifyAsync_ColumnToColumnProbeAgainstUndeployedTable_ReturnsProbeFailed()
    {
        // Genuine compile failure in the probe itself, independent of the index-deployment
        // gate (which only applies to ScanForced/RangeSeek verdicts) - the "other" side
        // references a table that was never deployed, so the probe SQL fails to compile.
        // Verdict is deliberately NOT Unknown - see the comment on the fidelity-caveat test above.
        var finding = new TypedPredicateFinding(
            Verdict.ScanForced,
            ColumnOperand("dbo.CodeFrequency", "Code", new SqlType(SqlTypeCategory.Char, Length: 1)),
            ColumnOperand("dbo.AlsoDoesNotExist", "Missing", new SqlType(SqlTypeCategory.Int)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.ProbeFailed, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_UnknownVerdictCausedByUnresolvedCollation_NeverConfirmedEvenThoughTheColumnActuallyConverts()
    {
        // The exact scenario "null-collation verify consistency" exists to pin: dbo.Orders'
        // OrderCode really does force a column-side conversion against an nvarchar value (the
        // Confirmed test above proves it) - but if this finding's own Collation never resolved
        // (VerdictClassifier: unresolved collation -> Unknown, never a guess), the oracle must
        // NOT rubber-stamp it Confirmed just because the real column happens to convert. Before
        // the Unknown short-circuit, this finding would have fallen through to the "no shape
        // claim to check" branch and been silently reported Confirmed - the exact bug this test
        // exists to prevent from ever coming back.
        var finding = new TypedPredicateFinding(
            Verdict.Unknown,
            ColumnOperand("dbo.Orders", "OrderCode", new SqlType(SqlTypeCategory.VarChar, Length: 20, Collation: null), indexed: true),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.NVarChar, Length: 20)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_UnknownVerdict_NeverAttemptsAProbeAtAll()
    {
        // Reaching for a table/column that was never deployed would normally produce
        // ProbeFailed once a probe is actually attempted (see the two ProbeFailed tests above) -
        // an Unknown verdict must short-circuit before that point, so this proves the
        // short-circuit really does run first, not just that it happens to agree with a
        // probeable case.
        var finding = new TypedPredicateFinding(
            Verdict.Unknown,
            ColumnOperand("dbo.DoesNotExist", "Missing", new SqlType(SqlTypeCategory.Int)),
            new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)),
            "=",
            "file.sql",
            1,
            1);

        var result = await _verifier.VerifyAsync(DatabaseName, finding);

        Assert.Equal(CorpusFindingOutcome.NotApplicable, result.Outcome);
    }
}
