using System.Text.Json;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Sarif;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

public sealed class SarifReportWriterTests
{
    [Fact]
    public async Task Write_MiniProjectFixture_ProducesValidSarifWithExpectedResultCount()
    {
        var projectDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "mini_project");
        var report = await EngineAuthoritativeScan.ScanFilesAsync(SqlFileDiscovery.EnumerateSqlFiles(projectDir));

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        Assert.Equal("2.1.0", document.RootElement.GetProperty("version").GetString());
        var results = document.RootElement.GetProperty("runs")[0].GetProperty("results");
        var expectedCount = report.Tier1Findings.Count + report.TypedFindings.Count + report.DynamicSqlFindings.Count + report.ExpressionDerivedFindings.Count + report.CollationConflictFindings.Count + report.WriteLossFindings.Count
            + report.TvfFenceFindings.Count + report.ScalarUdfFindings.Count + report.ColumnCollationDriftFindings.Count + report.CrossTableTypeDriftFindings.Count + report.ProcCallArgumentMismatchFindings.Count + report.TemporalBoundaryFindings.Count
            + report.MaxTypedColumnFindings.Count + report.OversizedParameterFindings.Count + report.UnderLengthParameterFindings.Count + report.AnsiPaddingMismatchFindings.Count + report.PartialCompositeForeignKeyJoinFindings.Count + report.SetOptionFindings.Count
            + report.CatchAllPredicateFindings.Count + report.LocalVariablePredicateFindings.Count + report.NotInNullableSubqueryFindings.Count + report.NonUniqueUpdateSourceFindings.Count + report.ForcedSerialFindings.Count
            + report.UntrustedConstraintFindings.Count + report.CascadingForeignKeyFindings.Count + report.MultiReferencedCteFindings.Count
            + report.NestedViewDepthFindings.Count + report.PostExpansionJoinWidthFindings.Count + report.SelectStarViewFindings.Count
            + report.OutputParameterFindings.Count
            + report.CodeMetricFindings.Count + report.FormattingFindings.Count
            + report.NamingFindings.Count + report.DeadCodeFindings.Count + report.DuplicationFindings.Count + report.DeprecatedSyntaxFindings.Count
            + report.StatementShapeFindings.Count
            + report.ControlFlowRiskFindings.Count
            + report.SecurityFindings.Count
            + report.CheckConstraintFindings.Count
            + report.DefaultNullableConstraintFindings.Count
            + report.TryCastComputedColumnPredicateFindings.Count
            + report.StaleSelectStarViewFindings.Count
            // DatabaseConfigurationFindings is live-mode only, but EngineAuthoritativeScan
            // genuinely runs the full LiveScanRunner pipeline (see its own doc comment) against a
            // real disposable database - and every such database has Query Store deliberately
            // turned back off by DatabaseProvisioner.CreateFreshAsync right after creation (real
            // Docker error-log spam otherwise), so a real QueryStoreNotReadWrite finding fires
            // here every time, not a bug in the new stream.
            + report.DatabaseConfigurationFindings.Count;
        Assert.Equal(expectedCount, results.GetArrayLength());
        Assert.True(expectedCount > 0);
    }

    [Fact]
    public void Write_ScanForcedFinding_MapsToErrorLevel()
    {
        var report = new ScanReport(
            new ParseHealthReport([]),
            [],
            [new TypedPredicateFinding(
                Verdict.ScanForced,
                new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, Provenance: null!),
                new PredicateOperand.Value(null),
                "=",
                "test.sql",
                1,
                1)],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            SkippedConstructSummary.From([]),
            TypedPredicateSummary.From([]),
            DynamicSqlSummary.From([]));

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("error", result.GetProperty("level").GetString());
        Assert.Equal("silentscan/verdict/scan-forced", result.GetProperty("ruleId").GetString());
    }

    [Fact]
    public void Write_ScanForcedFindingOnUnindexedColumn_DowngradesToWarningLevel()
    {
        // Every corpus finding this tool has actually produced against real-world repos has
        // been on a column with no evidence it's indexed - reporting all of them at "error"
        // regardless overstates the cost, since there was no seek to lose in the first place.
        var report = new ScanReport(
            new ParseHealthReport([]),
            [],
            [new TypedPredicateFinding(
                Verdict.ScanForced,
                new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar), Indexed: false, Depth: 0, Provenance: null!),
                new PredicateOperand.Value(null),
                "=",
                "test.sql",
                1,
                1)],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            SkippedConstructSummary.From([]),
            TypedPredicateSummary.From([]),
            DynamicSqlSummary.From([]));

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("warning", result.GetProperty("level").GetString());
    }

    [Fact]
    public void Write_ExpressionDerivedFinding_MapsToErrorLevelWithChainInMessage()
    {
        var report = new ScanReport(
            new ParseHealthReport([]),
            [],
            [],
            [],
            [new ExpressionDerivedFinding(
                "CustomerIdAgain",
                "test.sql",
                10,
                5,
                [new TransformationSite("vw_outer.sql", 3, "CAST/CONVERT to Int"), new TransformationSite("vw_inner.sql", 2, "CAST/CONVERT to VarChar(20)")],
                [new UnderlyingBaseColumn("dbo.Orders", "CustomerId", Indexed: true)])],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            SkippedConstructSummary.From([]),
            TypedPredicateSummary.From([]),
            DynamicSqlSummary.From([]));

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("error", result.GetProperty("level").GetString());
        Assert.Equal("silentscan/lineage/expression-derived-column", result.GetProperty("ruleId").GetString());
        var message = result.GetProperty("message").GetProperty("text").GetString();
        Assert.Contains("dbo.Orders.CustomerId (indexed)", message, StringComparison.Ordinal);
        Assert.Contains("vw_outer.sql:3", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ExpressionDerivedFindingWithNoIndexedUnderlyingColumn_DowngradesToWarningLevel()
    {
        var report = new ScanReport(
            new ParseHealthReport([]),
            [],
            [],
            [],
            [new ExpressionDerivedFinding(
                "CustomerIdAgain",
                "test.sql",
                10,
                5,
                [new TransformationSite("vw_outer.sql", 3, "CAST/CONVERT to Int")],
                [new UnderlyingBaseColumn("dbo.Orders", "CustomerId", Indexed: false)])],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            SkippedConstructSummary.From([]),
            TypedPredicateSummary.From([]),
            DynamicSqlSummary.From([]));

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("warning", result.GetProperty("level").GetString());
    }

    [Fact]
    public void Write_DynamicSqlAnalyzedFinding_MapsToNoteLevel()
    {
        var report = new ScanReport(
            new ParseHealthReport([]),
            [],
            [],
            [new DynamicSqlFinding("test.sql", 3, 5, DynamicSqlOutcome.AnalyzedLiteral, Reason: null)],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            SkippedConstructSummary.From([]),
            TypedPredicateSummary.From([]),
            DynamicSqlSummary.From([]));

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("note", result.GetProperty("level").GetString());
        Assert.Equal("silentscan/dynamic-sql/analyzed", result.GetProperty("ruleId").GetString());
    }

    [Fact]
    public void Write_DynamicSqlUnanalyzableFinding_MapsToWarningLevelWithReasonInMessage()
    {
        var report = new ScanReport(
            new ParseHealthReport([]),
            [],
            [],
            [new DynamicSqlFinding("test.sql", 3, 5, DynamicSqlOutcome.Unanalyzable, "non-literal-argument")],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            SkippedConstructSummary.From([]),
            TypedPredicateSummary.From([]),
            DynamicSqlSummary.From([]));

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("warning", result.GetProperty("level").GetString());
        Assert.Equal("silentscan/dynamic-sql/unanalyzable", result.GetProperty("ruleId").GetString());
        Assert.Contains("non-literal-argument", result.GetProperty("message").GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_DynamicSqlInnerParseFailedFinding_MapsToWarningLevelWithDistinctRuleId()
    {
        var report = new ScanReport(
            new ParseHealthReport([]),
            [],
            [],
            [new DynamicSqlFinding("test.sql", 3, 5, DynamicSqlOutcome.InnerParseFailed, "Incorrect syntax near '$$$'.")],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            SkippedConstructSummary.From([]),
            TypedPredicateSummary.From([]),
            DynamicSqlSummary.From([]));

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("warning", result.GetProperty("level").GetString());
        Assert.Equal("silentscan/dynamic-sql/inner-parse-failed", result.GetProperty("ruleId").GetString());
    }

    [Fact]
    public void Write_TypedFindingViaDynamicSql_IncludesCallSiteInMessage()
    {
        var report = new ScanReport(
            new ParseHealthReport([]),
            [],
            [new TypedPredicateFinding(
                Verdict.ScanForced,
                new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, Provenance: null!),
                new PredicateOperand.Value(null),
                "=",
                "test.sql",
                5,
                7,
                new SourceSpan("test.sql", 4, 10))],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            SkippedConstructSummary.From([]),
            TypedPredicateSummary.From([]),
            DynamicSqlSummary.From([]));

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var message = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("message").GetProperty("text").GetString();
        Assert.Contains("via dynamic SQL executed at test.sql:4", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryDynamicSqlOutcome()
    {
        foreach (var outcome in Enum.GetValues<DynamicSqlOutcome>())
        {
            var ruleId = SarifRuleCatalog.DynamicSqlRuleId(outcome);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryTier1FindingKind()
    {
        foreach (var kind in Enum.GetValues<SargabilityFindingKind>())
        {
            var ruleId = SarifRuleCatalog.Tier1RuleId(kind);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryVerdict()
    {
        foreach (var verdict in Enum.GetValues<Verdict>())
        {
            var ruleId = SarifRuleCatalog.VerdictRuleId(verdict);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryIndexDesignFindingKind()
    {
        foreach (var kind in Enum.GetValues<IndexDesignFindingKind>())
        {
            var ruleId = SarifRuleCatalog.IndexDesignRuleId(kind);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryIdentityRangeFindingKind()
    {
        foreach (var kind in Enum.GetValues<IdentityRangeFindingKind>())
        {
            var ruleId = SarifRuleCatalog.IdentityRangeRuleId(kind);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }
    }

    [Fact]
    public void Write_RuleCatalog_CoversFloatEqualityRuleId()
    {
        Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == SarifRuleCatalog.FloatEqualityRuleId);
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryTriggerCorrectnessFindingKind()
    {
        foreach (var kind in Enum.GetValues<TriggerCorrectnessFindingKind>())
        {
            var ruleId = SarifRuleCatalog.TriggerCorrectnessRuleId(kind);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }
    }

    [Fact]
    public void Write_RuleCatalog_CoversCrossModuleLockOrderRuleId()
    {
        Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == SarifRuleCatalog.CrossModuleLockOrderRuleId);
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryCheckConstraintFindingKind()
    {
        foreach (var kind in Enum.GetValues<CheckConstraintFindingKind>())
        {
            var ruleId = SarifRuleCatalog.CheckConstraintRuleId(kind);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }
    }
}
