using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

[Trait("Category", "Oracle")]
public sealed class DynamicSqlCrossCallEdgePipelineTests
{
    private static async Task<ScanReport> Scan(string sql, FindingConfidence minimumConfidence = FindingConfidence.High)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS", minimumConfidence: minimumConfidence);
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task SingleCallerLiteral_SeedsCalleeParameter_DynamicSqlAnalyzedAndScanForced()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Orders (Status varchar(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                EXEC dbo.usp_FindByStatus @Status = N'Active';
            END;
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Reason?.StartsWith("variable-not-in-scope", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task SingleCallerVariable_WithSingleUnconditionalLiteralAssignment_SeedsCalleeParameter_ScanForced()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Orders (Status varchar(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                DECLARE @v NVARCHAR(20) = N'Active';
                EXEC dbo.usp_FindByStatus @v;
            END;
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public async Task TwoCallersWithDifferentLiterals_BothAssembliesAnalyzed()
    {
        const string SharedDdl = """
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = ''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_CallerA AS BEGIN EXEC dbo.usp_FindByStatus @Status = N'Active'; END;
            """;

        var withOnlyCallerA = await Scan(SharedDdl);
        var withBothCallers = await Scan(SharedDdl + """

            GO
            CREATE PROCEDURE dbo.usp_CallerB AS BEGIN EXEC dbo.usp_FindByStatus @Status = N'Closed'; END;
            """);

        var onlyCallerACount = withOnlyCallerA.DynamicSqlFindings.Count(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        var bothCallersCount = withBothCallers.DynamicSqlFindings.Count(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);

        Assert.Equal(onlyCallerACount + 1, bothCallersCount);
        Assert.DoesNotContain(withBothCallers.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);

        Assert.Empty(withBothCallers.TypedFindings);
    }

    [Fact]
    public async Task NoKnownCaller_QuotedPlaceholderPosition_MediumConfidenceScanForced_ExcludedByDefault()
    {
        const string sql = """
            CREATE TABLE dbo.Orders (Status varchar(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            """;

        var defaultReport = await Scan(sql);
        Assert.DoesNotContain(defaultReport.TypedFindings, f => f.Column.ColumnName == "Status");

        var mediumReport = await Scan(sql, FindingConfidence.Medium);
        var finding = Assert.Single(mediumReport.TypedFindings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public async Task NoKnownCaller_ObjectIdentifierPosition_AnalyzedWithZeroFindings()
    {
        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_DropStagingTable @TableName SYSNAME AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'DROP TABLE ' + @TableName;
                EXEC(@sql);
            END;
            """);

        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        Assert.Empty(report.TypedFindings);
        Assert.Empty(report.Tier1Findings);
    }

    [Fact]
    public async Task NoKnownCaller_ObjectIdentifierPositionInsideFullStatementWithWhereClause_AnalyzedWithZeroFindings()
    {
        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_SkipChecks @SchemaName SYSNAME, @TableName SYSNAME AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName) + N' WHERE Col1 IS NULL';
                EXEC(@sql);
            END;
            """);

        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        Assert.Empty(report.TypedFindings);
        Assert.Empty(report.Tier1Findings);
    }

    [Fact]
    public async Task NoKnownCaller_PlaceholderBareInPredicateValuePosition_AnalyzedButNoFindingFabricated()
    {
        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_SkipChecks @SchemaName SYSNAME, @Flag SYSNAME AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM ' + QUOTENAME(@SchemaName) + N'.T WHERE Col1 = ' + @Flag;
                EXEC(@sql);
            END;
            """);

        var finding = Assert.Single(report.DynamicSqlFindings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, finding.Outcome);
        Assert.Empty(report.TypedFindings);
        Assert.Empty(report.Tier1Findings);
    }

    [Fact]
    public async Task NoKnownCaller_MixedIdentifierAndQuotedPlaceholdersInOneStatement_QuotedOnePredicateStillFolds()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Orders (Status VARCHAR(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_JoinAndCheck @LogTableName SYSNAME, @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) =
                    N'SELECT o.Status FROM dbo.Orders AS o CROSS JOIN ' + QUOTENAME(@LogTableName) +
                    N' AS lt WHERE o.Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            """, FindingConfidence.Medium);

        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public async Task NoKnownCaller_TwoStatementsOnlyOneHasAPlaceholder_SiblingStatementGetsOrdinaryExtraction()
    {
        var report = await Scan("""
            CREATE TABLE dbo.Customers (Name VARCHAR(20) NOT NULL, INDEX IX_Name (Name));
            GO
            CREATE PROCEDURE dbo.usp_TwoStatements @LogTableName SYSNAME AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) =
                    N'INSERT INTO ' + QUOTENAME(@LogTableName) + N' (Msg) VALUES (''x'');' +
                    N'SELECT Name FROM dbo.Customers WHERE Name = N''y'';';
                EXEC(@sql);
            END;
            """, FindingConfidence.Medium);

        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Name");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public async Task CallerPassesVariableNotLiteral_ResolvableTypeFoldsToSymbolicPlaceholder()
    {
        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = ''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller @IncomingStatus NVARCHAR(20) AS
            BEGIN
                EXEC dbo.usp_FindByStatus @Status = @IncomingStatus;
            END;
            """, FindingConfidence.Medium);

        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        Assert.Empty(report.TypedFindings);
    }

    [Fact]
    public async Task SingleCallerOmitsArgument_DefaultBehavesExactlyLikeALiteralArgument()
    {
        const string body = """
            CREATE TABLE dbo.Small (Code varchar(20) NOT NULL, INDEX IX_Small_Code (Code));
            GO
            CREATE PROCEDURE dbo.usp_Report @Table SYSNAME{0} AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM ' + @Table + N' WHERE Code = N''1''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                EXEC dbo.usp_Report{1};
            END;
            """;

        var viaDefault = await Scan(
            body.Replace("{0}", " = N'dbo.Small'", StringComparison.Ordinal).Replace("{1}", string.Empty, StringComparison.Ordinal),
            minimumConfidence: FindingConfidence.Low);
        var viaLiteralArgument = await Scan(
            body.Replace("{0}", string.Empty, StringComparison.Ordinal).Replace("{1}", " @Table = N'dbo.Small'", StringComparison.Ordinal),
            minimumConfidence: FindingConfidence.Low);

        static IReadOnlyList<string> TypedShape(Core.Reporting.ScanReport report) =>
            [.. report.TypedFindings.Select(f => $"{f.Column.TableQualifiedName}.{f.Column.ColumnName}:{f.Verdict}:{f.Confidence}")];

        static IReadOnlyList<string> DynamicShape(Core.Reporting.ScanReport report) =>
            [.. report.DynamicSqlFindings.Select(f => $"{f.Outcome}:{f.Reason}").OrderBy(s => s, StringComparer.Ordinal)];

        Assert.Contains(viaDefault.TypedFindings, f => f.Column.TableQualifiedName == "dbo.Small");
        Assert.Equal(TypedShape(viaLiteralArgument), TypedShape(viaDefault));
        Assert.Equal(DynamicShape(viaLiteralArgument), DynamicShape(viaDefault));
    }
}
