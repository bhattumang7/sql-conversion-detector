using System.Text.Json;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Sarif;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Reporting;

[Trait("Category", "Oracle")]
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
        var expectedCount = report.Find<SargabilityFinding>("NonSargablePredicateScanner").Count + report.Find<TypedPredicateFinding>("TypedPredicateExtractor").Count + report.Find<DynamicSqlFinding>("DynamicSqlScanner").Count + report.Find<ExpressionDerivedFinding>("TypedPredicateExtractor").Count + report.Find<CollationConflictFinding>("TypedPredicateExtractor").Count + report.Find<WriteLossFinding>("TypedPredicateExtractor").Count
            + report.Find<TvfFenceFinding>("TvfFenceScanner").Count + report.Find<ScalarUdfFinding>("ScalarUdfScanner").Count + report.Find<ColumnCollationDriftFinding>("ColumnCollationDriftScanner").Count + report.Find<AnsiPaddingOffColumnFinding>("AnsiPaddingOffColumnScanner").Count + report.Find<CrossTableTypeDriftFinding>("CrossTableTypeDriftScanner").Count + report.Find<ProcCallArgumentMismatchFinding>("ProcCallArgumentMismatchScanner").Count + report.Find<TemporalBoundaryPrecisionFinding>("NonSargablePredicateScanner").Count
            + report.Find<MaxTypedColumnFinding>("MaxTypedColumnScanner").Count + report.Find<ColumnstoreUnsupportedColumnTypeFinding>("ColumnstoreUnsupportedColumnTypeScanner").Count + report.Find<OversizedParameterFinding>("TypedPredicateExtractor").Count + report.Find<UnderLengthParameterFinding>("TypedPredicateExtractor").Count + report.Find<AnsiPaddingMismatchFinding>("TypedPredicateExtractor").Count + report.Find<PartialCompositeForeignKeyJoinFinding>("PartialCompositeForeignKeyJoinScanner").Count + report.Find<SetOptionFinding>("SetOptionScanner").Count
            + report.Find<CatchAllPredicateFinding>("CatchAllPredicateScanner").Count + report.Find<LocalVariablePredicateFinding>("TypedPredicateExtractor").Count + report.Find<NotInNullableSubqueryFinding>("NotInNullableSubqueryScanner").Count + report.Find<NonUniqueUpdateSourceFinding>("NonUniqueUpdateSourceScanner").Count + report.Find<ForcedSerialFinding>("ForcedSerialScanner").Count
            + report.Find<UntrustedConstraintFinding>("UntrustedConstraintScanner").Count + report.Find<CascadingForeignKeyFinding>("CascadingForeignKeyScanner").Count + report.Find<MultiReferencedCteFinding>("MultiReferencedCteScanner").Count
            + report.Find<NestedViewDepthFinding>("NestedViewDepthScanner").Count + report.Find<PostExpansionJoinWidthFinding>("PostExpansionJoinWidthScanner").Count + report.Find<SelectStarViewFinding>("SelectStarViewScanner").Count
            + report.Find<OutputParameterFinding>("OutputParameterScanner").Count
            + report.Find<CodeMetricFinding>("CodeMetricScanner").Count + report.Find<FormattingFinding>("FormattingScanner").Count
            + report.Find<NamingFinding>("NamingScanner").Count + report.Find<DeadCodeFinding>("DeadCodeScanner").Count + report.Find<DuplicationFinding>("DuplicationScanner").Count + report.Find<DeprecatedSyntaxFinding>("DeprecatedSyntaxScanner").Count
            + report.Find<StatementShapeFinding>("StatementShapeScanner").Count
            + report.Find<ControlFlowRiskFinding>("ControlFlowRiskScanner").Count
            + report.Find<SecurityFinding>("SecurityScanner").Count
            + report.Find<CheckConstraintFinding>("CheckConstraintScanner").Count
            + report.Find<DefaultNullableConstraintFinding>("DefaultNullableConstraintScanner").Count
            + report.Find<TryCastComputedColumnPredicateFinding>("TryCastComputedColumnPredicateScanner").Count
            + report.Find<StaleSelectStarViewFinding>("StaleSelectStarViewScanner").Count
            + report.Find<BareTopNoOrderByFinding>("BareTopNoOrderByScanner").Count
            + report.Find<StringConcatNullFinding>("StringConcatNullScanner").Count
            + report.Find<AggregateDivisionColumnstoreFinding>("AggregateDivisionColumnstoreScanner").Count

            + report.Find<DatabaseConfigurationFinding>("DatabaseConfigurationScanner").Count;
        Assert.Equal(expectedCount, results.GetArrayLength());
        Assert.True(expectedCount > 0);
    }

    [Fact]
    public void Write_ScanForcedFinding_MapsToErrorLevel()
    {
        var report = TestScanReports.Build(TypedFindings: [new TypedPredicateFinding(
                Verdict.ScanForced,
                new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, Provenance: null!),
                new PredicateOperand.Value(null),
                "=",
                "test.sql",
                1,
                1)]);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("error", result.GetProperty("level").GetString());
        Assert.Equal("silentscan/verdict/scan-forced", result.GetProperty("ruleId").GetString());
        Assert.Equal("Proven", result.GetProperty("properties").GetProperty("tier").GetString());
    }

    [Fact]
    public void Write_ScanForcedFindingOnUnindexedColumn_DowngradesToWarningLevel()
    {
        var report = TestScanReports.Build(TypedFindings: [new TypedPredicateFinding(
                Verdict.ScanForced,
                new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar), Indexed: false, Depth: 0, Provenance: null!),
                new PredicateOperand.Value(null),
                "=",
                "test.sql",
                1,
                1)]);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("warning", result.GetProperty("level").GetString());
        Assert.Equal("Contextual", result.GetProperty("properties").GetProperty("tier").GetString());
    }

    [Fact]
    public void Write_ExpressionDerivedFinding_MapsToErrorLevelWithChainInMessage()
    {
        var report = TestScanReports.Build(ExpressionDerivedFindings: [new ExpressionDerivedFinding(
                "CustomerIdAgain",
                "test.sql",
                10,
                5,
                [new TransformationSite("vw_outer.sql", 3, "CAST/CONVERT to Int"), new TransformationSite("vw_inner.sql", 2, "CAST/CONVERT to VarChar(20)")],
                [new UnderlyingBaseColumn("dbo.Orders", "CustomerId", Indexed: true)])]);

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
        var report = TestScanReports.Build(ExpressionDerivedFindings: [new ExpressionDerivedFinding(
                "CustomerIdAgain",
                "test.sql",
                10,
                5,
                [new TransformationSite("vw_outer.sql", 3, "CAST/CONVERT to Int")],
                [new UnderlyingBaseColumn("dbo.Orders", "CustomerId", Indexed: false)])]);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("warning", result.GetProperty("level").GetString());
        Assert.Equal("Contextual", result.GetProperty("properties").GetProperty("tier").GetString());
    }

    [Fact]
    public void Write_DynamicSqlAnalyzedFinding_MapsToNoteLevel()
    {
        var report = TestScanReports.Build(DynamicSqlFindings: [new DynamicSqlFinding("test.sql", 3, 5, DynamicSqlOutcome.AnalyzedLiteral, Reason: null)]);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("note", result.GetProperty("level").GetString());
        Assert.Equal("silentscan/dynamic-sql/analyzed", result.GetProperty("ruleId").GetString());
        Assert.Equal("Advisory", result.GetProperty("properties").GetProperty("tier").GetString());
    }

    [Fact]
    public void Write_DynamicSqlUnanalyzableFinding_MapsToWarningLevelWithReasonInMessage()
    {
        var report = TestScanReports.Build(DynamicSqlFindings: [new DynamicSqlFinding("test.sql", 3, 5, DynamicSqlOutcome.Unanalyzable, "non-literal-argument")]);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("warning", result.GetProperty("level").GetString());
        Assert.Equal("silentscan/dynamic-sql/unanalyzable", result.GetProperty("ruleId").GetString());
        Assert.Contains("non-literal-argument", result.GetProperty("message").GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("Contextual", result.GetProperty("properties").GetProperty("tier").GetString());
    }

    [Fact]
    public void Write_DynamicSqlInnerParseFailedFinding_MapsToWarningLevelWithDistinctRuleId()
    {
        var report = TestScanReports.Build(DynamicSqlFindings: [new DynamicSqlFinding("test.sql", 3, 5, DynamicSqlOutcome.InnerParseFailed, "Incorrect syntax near '$$$'.")]);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal("warning", result.GetProperty("level").GetString());
        Assert.Equal("silentscan/dynamic-sql/inner-parse-failed", result.GetProperty("ruleId").GetString());
        Assert.Equal("Contextual", result.GetProperty("properties").GetProperty("tier").GetString());
    }

    [Fact]
    public void Write_TypedFindingViaDynamicSql_IncludesCallSiteInMessage()
    {
        var report = TestScanReports.Build(TypedFindings: [new TypedPredicateFinding(
                Verdict.ScanForced,
                new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, Provenance: null!),
                new PredicateOperand.Value(null),
                "=",
                "test.sql",
                5,
                7,
                new SourceSpan("test.sql", 4, 10))]);

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

        Assert.Equal(
            Enum.GetValues<DynamicSqlOutcome>().Length,
            Enum.GetValues<DynamicSqlOutcome>().Select(SarifRuleCatalog.DynamicSqlRuleId).Distinct().Count());
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryTier1FindingKind()
    {
        foreach (var kind in Enum.GetValues<SargabilityFindingKind>())
        {
            var ruleId = SarifRuleCatalog.Tier1RuleId(kind);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }

        Assert.Equal(
            Enum.GetValues<SargabilityFindingKind>().Length,
            Enum.GetValues<SargabilityFindingKind>().Select(SarifRuleCatalog.Tier1RuleId).Distinct().Count());
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryVerdict()
    {
        foreach (var verdict in Enum.GetValues<Verdict>())
        {
            var ruleId = SarifRuleCatalog.VerdictRuleId(verdict);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }

        Assert.Equal(
            Enum.GetValues<Verdict>().Length,
            Enum.GetValues<Verdict>().Select(SarifRuleCatalog.VerdictRuleId).Distinct().Count());
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryIndexDesignFindingKind()
    {
        foreach (var kind in Enum.GetValues<IndexDesignFindingKind>())
        {
            var ruleId = SarifRuleCatalog.IndexDesignRuleId(kind);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }

        Assert.Equal(
            Enum.GetValues<IndexDesignFindingKind>().Length,
            Enum.GetValues<IndexDesignFindingKind>().Select(SarifRuleCatalog.IndexDesignRuleId).Distinct().Count());
    }

    [Fact]
    public void Write_RuleCatalog_CoversEveryIdentityRangeFindingKind()
    {
        foreach (var kind in Enum.GetValues<IdentityRangeFindingKind>())
        {
            var ruleId = SarifRuleCatalog.IdentityRangeRuleId(kind);
            Assert.Contains(SarifRuleCatalog.AllRules, r => r.Id == ruleId);
        }

        Assert.Equal(
            Enum.GetValues<IdentityRangeFindingKind>().Length,
            Enum.GetValues<IdentityRangeFindingKind>().Select(SarifRuleCatalog.IdentityRangeRuleId).Distinct().Count());
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

        Assert.Equal(
            Enum.GetValues<TriggerCorrectnessFindingKind>().Length,
            Enum.GetValues<TriggerCorrectnessFindingKind>().Select(SarifRuleCatalog.TriggerCorrectnessRuleId).Distinct().Count());
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

        Assert.Equal(
            Enum.GetValues<CheckConstraintFindingKind>().Length,
            Enum.GetValues<CheckConstraintFindingKind>().Select(SarifRuleCatalog.CheckConstraintRuleId).Distinct().Count());
    }

    [Fact]
    public void Write_ParseError_EmitsToolExecutionNotification()
    {
        var parsed = SqlScriptParser.ParseText("broken.sql", "SELECT FROM WHERE ORDER;");
        Assert.True(parsed.HasErrors);
        var report = ScanReportBuilder.BuildFromParseResults([parsed], new DatabaseCatalog());

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var notifications = document.RootElement.GetProperty("runs")[0]
            .GetProperty("invocations")[0]
            .GetProperty("toolExecutionNotifications");

        Assert.True(notifications.GetArrayLength() >= 1);
        var messages = notifications.EnumerateArray().Select(n => n.GetProperty("message").GetProperty("text").GetString()).ToList();
        Assert.Contains(messages, m => m!.Contains("broken.sql", StringComparison.Ordinal));

        var invocation = document.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0];
        Assert.False(invocation.GetProperty("executionSuccessful").GetBoolean());
    }

    [Fact]
    public void Write_UnanalyzedBatch_EmitsDistinctToolExecutionNotificationNamingTheObject()
    {
        var script = string.Join('\n',
            "CREATE VIEW dbo.vw_First AS SELECT 1 AS X;",
            "GO",
            "CREATE PROCEDURE dbo.usp_Broken AS SELECT 1 FROM FROM;",
            "GO",
            "CREATE VIEW dbo.vw_Third AS SELECT 1 AS X;");
        var parsed = SqlScriptParser.ParseText("mixed.sql", script);
        Assert.Single(parsed.UnanalyzedBatches);
        var report = ScanReportBuilder.BuildFromParseResults([parsed], new DatabaseCatalog());

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var notifications = document.RootElement.GetProperty("runs")[0]
            .GetProperty("invocations")[0]
            .GetProperty("toolExecutionNotifications")
            .EnumerateArray()
            .ToList();

        Assert.Contains(
            notifications,
            n => n.GetProperty("message").GetProperty("text").GetString()!.Contains("dbo.usp_Broken", StringComparison.Ordinal));
        Assert.Contains(
            notifications,
            n => n.GetProperty("level").GetString() == "warning");
    }

    [Fact]
    public void Write_NoParseErrors_StillEmitsInvocationWithEmptyNotifications()
    {
        var report = ScanReportBuilder.BuildFromParseResults([], new DatabaseCatalog());

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var invocation = document.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0];
        Assert.True(invocation.GetProperty("executionSuccessful").GetBoolean());
        Assert.Equal(0, invocation.GetProperty("toolExecutionNotifications").GetArrayLength());
    }

    [Fact]
    public void Write_SkippedConstructs_EmitsWarningNotificationAndFlagsExecutionUnsuccessful()
    {
        var skipped = new SkippedConstruct(AnalysisPass.Predicates, "test.sql", 1, 1, "MERGE", "unsupported-syntax");
        var report = TestScanReports.Build(
            SkippedConstructs: [skipped],
            SkippedConstructSummary: SkippedConstructSummary.From([skipped]));

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var invocation = document.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0];
        Assert.False(invocation.GetProperty("executionSuccessful").GetBoolean());

        var notifications = invocation.GetProperty("toolExecutionNotifications").EnumerateArray().ToList();
        Assert.Contains(notifications, n =>
            n.GetProperty("message").GetProperty("text").GetString()!.Contains("1 construct(s) skipped", StringComparison.Ordinal)
            && n.GetProperty("level").GetString() == "warning");
    }

    [Fact]
    public void Write_UnanalyzableDynamicSql_EmitsWarningNotification()
    {
        var summary = DynamicSqlSummary.From([new DynamicSqlFinding("test.sql", 3, 5, DynamicSqlOutcome.Unanalyzable, "non-literal-argument")]);
        var report = TestScanReports.Build(DynamicSqlSummary: summary);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var notifications = document.RootElement.GetProperty("runs")[0]
            .GetProperty("invocations")[0]
            .GetProperty("toolExecutionNotifications")
            .EnumerateArray()
            .ToList();

        Assert.Contains(notifications, n =>
            n.GetProperty("message").GetProperty("text").GetString()!.Contains("dynamic-SQL call site(s) could not be fully analyzed", StringComparison.Ordinal)
            && n.GetProperty("level").GetString() == "warning");
    }

    [Fact]
    public void Write_AnalyzedDynamicSqlOnly_EmitsNoDynamicSqlNotification()
    {
        var summary = DynamicSqlSummary.From([new DynamicSqlFinding("test.sql", 3, 5, DynamicSqlOutcome.AnalyzedLiteral, Reason: null)]);
        var report = TestScanReports.Build(DynamicSqlSummary: summary);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var notifications = document.RootElement.GetProperty("runs")[0]
            .GetProperty("invocations")[0]
            .GetProperty("toolExecutionNotifications")
            .EnumerateArray()
            .ToList();

        Assert.DoesNotContain(notifications, n =>
            n.GetProperty("message").GetProperty("text").GetString()!.Contains("dynamic-SQL call site(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_UnknownTypedPredicates_EmitsNoteNotification()
    {
        var summary = TypedPredicateSummary.From([new TypedPredicateFinding(
            Verdict.Unknown,
            new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, Provenance: null!),
            new PredicateOperand.Value(null),
            "=",
            "test.sql",
            1,
            1)]);
        var report = TestScanReports.Build(TypedPredicateSummary: summary);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);

        var notifications = document.RootElement.GetProperty("runs")[0]
            .GetProperty("invocations")[0]
            .GetProperty("toolExecutionNotifications")
            .EnumerateArray()
            .ToList();

        Assert.Contains(notifications, n =>
            n.GetProperty("message").GetProperty("text").GetString()!.Contains("could not be resolved to a seek/scan verdict", StringComparison.Ordinal)
            && n.GetProperty("level").GetString() == "note");
    }

    [Fact]
    public void Write_NonPersistedComputedColumnCoveredByIndex_HedgesRecomputeClaim()
    {
        var report = TestScanReports.Build(NonPersistedComputedColumnFindings:
        [
            new NonPersistedComputedColumnFinding("dbo.Orders", "Total", "Qty * Price", IsCoveredByIndex: true, "test.sql", 1),
        ]);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);
        var results = document.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToList();

        var result = Assert.Single(results);
        var message = result.GetProperty("message").GetProperty("text").GetString()!;
        Assert.Contains("avoid the recompute", message, StringComparison.Ordinal);
        Assert.DoesNotContain("on every read that touches it", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_NonPersistedComputedColumnNotCoveredByIndex_KeepsUnconditionalRecomputeClaim()
    {
        var report = TestScanReports.Build(NonPersistedComputedColumnFindings:
        [
            new NonPersistedComputedColumnFinding("dbo.Orders", "Total", "Qty * Price", IsCoveredByIndex: false, "test.sql", 1),
        ]);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);
        var results = document.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToList();

        var result = Assert.Single(results);
        var message = result.GetProperty("message").GetProperty("text").GetString()!;
        Assert.Contains("on every read that touches it", message, StringComparison.Ordinal);
        Assert.DoesNotContain("avoid the recompute", message, StringComparison.Ordinal);
    }
}
