using SilentScan.Core.Parsing;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Regression coverage for the explicit-COLLATE sargability rules, all oracle-verified directly
/// against Docker SQL Server before implementation (compile-only SHOWPLAN_XML probes, per
/// CLAUDE.md's verification discipline) - and now oracle-confirmed inline per test too, so the
/// pre-implementation spike can't silently drift from what the shipped code actually classifies:
///
/// Rule 1 (<c>col COLLATE X</c>, X differs from the column's own real collation): compiles to
/// an explicit CONVERT applied to the column itself - structurally identical to
/// <c>CAST(col AS ...)</c>, reported through the same ExpressionDerivedFinding channel
/// (<see cref="ScalarExpressionResolver"/>/<c>ApplyExplicitCollate</c>). When X matches the
/// column's real collation, the engine elides the CONVERT entirely (a single clean Index Seek,
/// no CONVERT anywhere) - correctly not reported.
///
/// Rule 2 (<c>col = literal COLLATE X</c>, X differs from the column's own real collation):
/// an explicit COLLATE clause has the highest T-SQL coercibility precedence, so the COLUMN
/// (not the literal) gets CONVERT_IMPLICIT even though nothing about the column's own syntax
/// changed - ScanForced, never RangeSeek (the dynamic-range-seek optimization is cross-
/// category-only, never observed for a same-category collation mismatch in any probed shape).
/// Deliberately narrow: this only fires for a genuine literal on the other side
/// (<see cref="Rules.VerdictClassifier"/>'s otherIsLiteral parameter) - a real column or a
/// CAST/CONVERT/function result inheriting a column's collation carries "implicit"
/// coercibility instead (same tier as a real column), which risks a Msg 468 compile-error
/// conflict rather than a silent convert; that distinction was not oracle-verified here and is
/// left Unknown rather than guessed.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class ExplicitCollatePipelineTests : OracleTestFixture
{
    private const string DifferingCollateColumnSql = """
        CREATE TABLE dbo.Customers (
            Code varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_Code (Code));
        GO
        SELECT 1 FROM dbo.Customers WHERE Code COLLATE Latin1_General_CI_AS = 'x';
        """;

    private const string DifferingCollateLiteralSql = """
        CREATE TABLE dbo.CustomersLiteral (
            Code varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_Code (Code));
        GO
        SELECT 1 FROM dbo.CustomersLiteral WHERE Code = 'x' COLLATE Latin1_General_CI_AS;
        """;

    private const string MatchingCollateColumnSql = """
        CREATE TABLE dbo.CustomersMatchingCollate (
            Code varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_Code (Code));
        GO
        SELECT 1 FROM dbo.CustomersMatchingCollate WHERE Code COLLATE SQL_Latin1_General_CP1_CI_AS = 'x';
        """;

    private const string MatchingCollateLiteralSql = """
        CREATE TABLE dbo.CustomersLiteralMatchingCollate (
            Code varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            INDEX IX_Code (Code));
        GO
        SELECT 1 FROM dbo.CustomersLiteralMatchingCollate WHERE Code = 'x' COLLATE SQL_Latin1_General_CP1_CI_AS;
        """;

    protected override string DatabaseNameSeed => nameof(ExplicitCollatePipelineTests);

    protected override string Ddl => string.Join(
        "\nGO\n", DifferingCollateColumnSql, DifferingCollateLiteralSql, MatchingCollateColumnSql, MatchingCollateLiteralSql);

    private static ScanReport Scan(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("collate.sql", sql);
        var report = ScanReportBuilder.BuildFromParseResults([parseResult], "SQL_Latin1_General_CP1_CI_AS");
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task ColumnWithDifferingExplicitCollate_ReportsExpressionDerivedFinding_OracleConfirmed()
    {
        var report = Scan(DifferingCollateColumnSql);

        var finding = Assert.Single(report.ExpressionDerivedFindings);
        Assert.Equal("Code", finding.ColumnName);
        var underlying = Assert.Single(finding.UnderlyingBaseColumns);
        Assert.Equal("dbo.Customers", underlying.TableQualifiedName);
        Assert.True(underlying.Indexed);
        Assert.Empty(report.TypedFindings);

        // ExpressionDerivedFinding carries no Verdict of its own to feed
        // PipelineOracleVerification's TypedPredicateFinding-shaped verifier - the claim under
        // test is narrower and syntactic: an explicit COLLATE clause on the column produces a
        // CONVERT applied to Code itself, exactly like CAST(Code AS ...) would. Confirm that
        // directly against the plan XML rather than forcing this through the typed-finding path.
        //
        // This is a plain (explicit) CONVERT, not a CONVERT_IMPLICIT - oracle-captured plan XML
        // shows <Convert ... Implicit="0"> wrapping the Code ColumnReference here (the literal
        // side gets Implicit="1" instead, harmlessly, since it has no bearing on seekability).
        // ConvertImplicitDetector.FindColumnConversions only matches Implicit="1", by design (it
        // exists to confirm CONVERT_IMPLICIT-driven ScanForced/RangeSeek verdicts) - it would
        // wrongly report nothing for this genuinely-confirmed-but-differently-shaped case, so
        // this checks the Convert/ColumnReference relationship directly instead.
        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(
            DatabaseName, "SELECT 1 FROM dbo.Customers WHERE Code COLLATE Latin1_General_CI_AS = 'x';");
        Assert.Contains(
            System.Xml.Linq.XDocument.Parse(planXml).Descendants().Where(e => e.Name.LocalName == "Convert"),
            convert => convert.Descendants().Any(e =>
                e.Name.LocalName == "ColumnReference"
                && (string?)e.Attribute("Table") == "[Customers]"
                && (string?)e.Attribute("Column") == "Code"));
    }

    [Fact]
    public async Task ColumnWithMatchingExplicitCollate_IsANoOp_ProducesNoFinding_OracleConfirmed()
    {
        const string probe = "SELECT 1 FROM dbo.CustomersMatchingCollate WHERE Code COLLATE SQL_Latin1_General_CP1_CI_AS = 'x';";
        var report = Scan(MatchingCollateColumnSql);

        Assert.Empty(report.ExpressionDerivedFindings);
        var summary = report.TypedPredicateSummary;
        Assert.Equal(1, summary.SeekPreservedCount);
        Assert.Empty(report.TypedFindings);

        // The claim is that the COLLATE clause matching the column's real collation makes the
        // engine elide the CONVERT entirely - confirm there is genuinely no column-side
        // CONVERT_IMPLICIT anywhere in the plan, not just that our own classifier says so.
        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = SilentScan.Verify.Oracle.ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.DoesNotContain(conversions, c => string.Equals(c.Column, "Code", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LiteralWithDifferingExplicitCollate_ForcesColumnScanForced_OracleConfirmed()
    {
        var report = Scan(DifferingCollateLiteralSql);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);

        var results = await PipelineOracleVerification.VerifyAsync(Options, DatabaseName, [finding]);
        PipelineOracleVerification.AssertAllConfirmed(results);
    }

    [Fact]
    public async Task LiteralWithMatchingExplicitCollate_IsANoOp_SeekPreserved_OracleConfirmed()
    {
        const string probe = "SELECT 1 FROM dbo.CustomersLiteralMatchingCollate WHERE Code = 'x' COLLATE SQL_Latin1_General_CP1_CI_AS;";
        var report = Scan(MatchingCollateLiteralSql);

        Assert.Empty(report.TypedFindings);
        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);

        var planXml = await new SilentScan.Verify.Oracle.PlanXmlCapture(Options).CaptureAsync(DatabaseName, probe);
        var conversions = SilentScan.Verify.Oracle.ConvertImplicitDetector.FindColumnConversions(planXml);
        Assert.DoesNotContain(conversions, c => string.Equals(c.Column, "Code", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ColumnVsColumnDifferingCollations_NoExplicitCollateAnywhere_ReportsCollationConflict()
    {
        // Two real columns with genuinely different native collations and no explicit COLLATE
        // clause anywhere: real SQL Server refuses to even compile this (Msg 468, "Cannot
        // resolve the collation conflict") - not a seek-loss verdict at all, so this is a
        // dedicated CollationConflictFinding rather than a routine Unknown TypedPredicateFinding.
        // A statement that fails to compile has no plan XML to capture, so there is nothing for
        // the plan-based oracle to confirm here - the "compile fails" claim itself was the thing
        // oracle-verified during the original spike (see class doc), not something a per-test
        // SHOWPLAN_XML probe can re-check (SHOWPLAN_XML compilation would fail identically).
        var report = Scan("""
            CREATE TABLE dbo.LocalCustomers (
                Email varchar(100) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
                INDEX IX_Email (Email));
            GO
            CREATE TABLE dbo.VendorCustomers (
                Email varchar(100) COLLATE Latin1_General_CI_AS NOT NULL);
            GO
            SELECT 1
            FROM dbo.LocalCustomers l
            INNER JOIN dbo.VendorCustomers v ON l.Email = v.Email;
            """);

        Assert.Empty(report.TypedFindings);
        var conflict = Assert.Single(report.CollationConflictFindings);
        Assert.Equal("dbo.LocalCustomers", conflict.FirstTableQualifiedName);
        Assert.Equal("Email", conflict.FirstColumnName);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", conflict.FirstCollationName);
        Assert.Equal("dbo.VendorCustomers", conflict.SecondTableQualifiedName);
        Assert.Equal("Email", conflict.SecondColumnName);
        Assert.Equal("Latin1_General_CI_AS", conflict.SecondCollationName);
    }

    [Fact]
    public void ConvertResultInheritingColumnCollation_VsDifferentlyCollatedColumn_IsOperandClash()
    {
        // A CAST/CONVERT result with no COLLATE clause of its own inherits its source column's
        // collation AND that column's coercibility tier ("implicit", per official T-SQL rules -
        // the same tier a real column carries). Oracle-verified directly (Docker SQL Server):
        // this exact shape does not compile at all (Msg 468, "Cannot resolve the collation
        // conflict between SQL_Latin1_General_CP1_CI_AS and Latin1_General_CI_AS") - identically
        // to two real columns with differing collations and no CONVERT anywhere. This used to be
        // reported as Unknown (an admitted, unverified guess); it is now a confirmed compile
        // failure. There is no plan XML for a statement that fails to compile, so nothing for a
        // per-test SHOWPLAN_XML probe to confirm beyond the sqlcmd compile failure itself.
        var report = Scan("""
            CREATE TABLE dbo.T (Code nvarchar(20) COLLATE Latin1_General_CI_AS NOT NULL, INDEX IX_Code (Code));
            GO
            CREATE TABLE dbo.Raw (Value varchar(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL);
            GO
            SELECT 1 FROM dbo.T, dbo.Raw WHERE Code = CONVERT(nvarchar(20), Value);
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Code");
        Assert.Equal(Verdict.OperandClash, finding.Verdict);
    }

    [Fact]
    public void CrossCategoryColumnVsColumn_DifferingCollations_ReportsCollationConflict()
    {
        // The gap this fix closes: CHAR vs VARCHAR is a different type CATEGORY from each other,
        // but a genuine collation mismatch does not care about category - oracle-verified
        // directly (Docker SQL Server): CHAR column vs VARCHAR column with differing collations
        // raises Msg 468 identically to two same-category columns. Before this fix,
        // TryRecordCollationConflict's category-equality gate let this fall through to the
        // type-pair matrix, which reports Char|VarChar's same-collation cell (SeekPreserved) -
        // a compile error reported as clean.
        var report = Scan("""
            CREATE TABLE dbo.CharSide (Code char(10) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL, INDEX IX_Code (Code));
            GO
            CREATE TABLE dbo.VarCharSide (Code varchar(10) COLLATE Latin1_General_CI_AS NOT NULL);
            GO
            SELECT 1 FROM dbo.CharSide c INNER JOIN dbo.VarCharSide v ON c.Code = v.Code;
            """);

        Assert.Empty(report.TypedFindings);
        var conflict = Assert.Single(report.CollationConflictFindings);
        Assert.Equal("dbo.CharSide", conflict.FirstTableQualifiedName);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", conflict.FirstCollationName);
        Assert.Equal("dbo.VarCharSide", conflict.SecondTableQualifiedName);
        Assert.Equal("Latin1_General_CI_AS", conflict.SecondCollationName);
    }
}
