using System.Text.Json;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Sarif;
using SilentScan.Core.Rules;
using SilentScan.Core.TypeInference;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Reporting;

public sealed class SarifReportWriterCoverageTests
{
    private static JsonElement FirstResult(ScanReport report)
    {
        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);
        return document.RootElement.GetProperty("runs")[0].GetProperty("results")[0].Clone();
    }

    [Fact]
    public void Write_EmptyReport_ProducesToolDriverWiredToRuleCatalogAndRuleDocSite()
    {
        var report = TestScanReports.Build();

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);
        var root = document.RootElement;

        Assert.Equal("https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json", root.GetProperty("$schema").GetString());
        Assert.Equal("2.1.0", root.GetProperty("version").GetString());

        var driver = root.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver");
        Assert.Equal("SilentScan", driver.GetProperty("name").GetString());
        Assert.Equal(RuleDocSite.IndexUrl, driver.GetProperty("informationUri").GetString());
        Assert.True(Version.TryParse(driver.GetProperty("version").GetString(), out _));

        var rules = driver.GetProperty("rules");
        Assert.Equal(SarifRuleCatalog.AllRules.Count, rules.GetArrayLength());
        Assert.Contains(
            rules.EnumerateArray(),
            r => r.GetProperty("id").GetString() == SarifRuleCatalog.FloatEqualityRuleId
                 && r.GetProperty("helpUri").GetString() == RuleDocSite.Url(SarifRuleCatalog.FloatEqualityRuleId));

        Assert.Equal(0, root.GetProperty("runs")[0].GetProperty("results").GetArrayLength());
    }

    [Theory]
    [InlineData(FindingConfidence.High, "silentscan/predicates/float-equality", "error")]
    [InlineData(FindingConfidence.Medium, "silentscan/predicates/float-equality/medium-confidence", "note")]
    [InlineData(FindingConfidence.Low, "silentscan/predicates/float-equality/low-confidence", "note")]
    public void Write_FloatEqualityFindingAtEachConfidence_FloorsLevelButKeepsDistinctRuleIdPerConfidence(
        FindingConfidence confidence, string expectedRuleId, string expectedLevel)
    {
        var report = TestScanReports.Build(
            FloatEqualityFindings: [new FloatEqualityFinding("dbo.T", "Amount", "float", "test.sql", 1, 1, confidence)]);

        var result = FirstResult(report);

        Assert.Equal(expectedRuleId, result.GetProperty("ruleId").GetString());
        Assert.Equal(expectedLevel, result.GetProperty("level").GetString());
        Assert.Equal(expectedLevel == "error" ? "Proven" : "Advisory", result.GetProperty("properties").GetProperty("tier").GetString());
    }

    [Fact]
    public void Write_SargabilityFindingConfirmedIndexed_MapsToWarningUnlessLikePatternNotLiteral()
    {
        var indexed = new SargabilityFinding(SargabilityFindingKind.FunctionWrappedColumn, "Col", null, "test.sql", 1, 1, TableQualifiedName: "dbo.T", Indexed: true);
        var report = TestScanReports.Build(Tier1Findings: [indexed]);

        var result = FirstResult(report);
        Assert.Equal("warning", result.GetProperty("level").GetString());
    }

    [Fact]
    public void Write_SargabilityFindingLikePatternNotLiteralEvenWhenIndexed_MapsToNoteLevel()
    {
        var indexed = new SargabilityFinding(SargabilityFindingKind.LikePatternNotLiteral, "Col", null, "test.sql", 1, 1, TableQualifiedName: "dbo.T", Indexed: true);
        var report = TestScanReports.Build(Tier1Findings: [indexed]);

        var result = FirstResult(report);
        Assert.Equal("note", result.GetProperty("level").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Write_SargabilityFindingNotConfirmedIndexed_MapsToNoteLevel(bool? indexed)
    {
        var finding = new SargabilityFinding(SargabilityFindingKind.FunctionWrappedColumn, "Col", null, "test.sql", 1, 1, TableQualifiedName: "dbo.T", Indexed: indexed);
        var report = TestScanReports.Build(Tier1Findings: [finding]);

        var result = FirstResult(report);
        Assert.Equal("note", result.GetProperty("level").GetString());
    }

    [Fact]
    public void Write_SargabilityFindingMessage_IncludesDetailIndexNoteAndDynamicSqlOrigin()
    {
        var finding = new SargabilityFinding(
            SargabilityFindingKind.FunctionWrappedColumn,
            "Col",
            Detail: "wrapped in UPPER()",
            SourcePath: "test.sql",
            Line: 1,
            Column: 1,
            DynamicSqlCallSite: new SourceSpan("caller.sql", 9, 2),
            TableQualifiedName: "dbo.T",
            Indexed: true);
        var report = TestScanReports.Build(Tier1Findings: [finding]);

        var message = FirstResult(report).GetProperty("message").GetProperty("text").GetString()!;

        Assert.Contains("(wrapped in UPPER())", message, StringComparison.Ordinal);
        Assert.Contains("[dbo.T.Col, indexed=True]", message, StringComparison.Ordinal);
        Assert.Contains("(via dynamic SQL executed at caller.sql:9)", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_SargabilityFindingWithNoTable_OmitsIndexNoteFromMessage()
    {
        var finding = new SargabilityFinding(SargabilityFindingKind.ColumnArithmetic, "Col", Detail: null, SourcePath: "test.sql", Line: 1, Column: 1);
        var report = TestScanReports.Build(Tier1Findings: [finding]);

        var message = FirstResult(report).GetProperty("message").GetProperty("text").GetString()!;

        Assert.DoesNotContain("indexed=", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_BuildResult_OmitsStartColumnPropertyWhenNull()
    {
        var report = TestScanReports.Build(
            UntrustedConstraintFindings: [new UntrustedConstraintFinding(UntrustedConstraintFindingKind.ForeignKey, "FK_Test", "dbo.T", "test.sql", 4)]);

        var region = FirstResult(report).GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("region");

        Assert.Equal(4, region.GetProperty("startLine").GetInt32());
        Assert.False(region.TryGetProperty("startColumn", out _));
    }

    [Fact]
    public void Write_BuildResult_IncludesStartColumnPropertyWhenPresent()
    {
        var finding = new SargabilityFinding(SargabilityFindingKind.ColumnArithmetic, "Col", null, "test.sql", 4, 7);
        var report = TestScanReports.Build(Tier1Findings: [finding]);

        var region = FirstResult(report).GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("region");

        Assert.Equal(7, region.GetProperty("startColumn").GetInt32());
    }

    [Fact]
    public void Write_RootedSourcePath_EmitsFileUri()
    {
        var finding = new SargabilityFinding(SargabilityFindingKind.ColumnArithmetic, "Col", null, "/var/data/script.sql", 1, 1);
        var report = TestScanReports.Build(Tier1Findings: [finding]);

        var uri = FirstResult(report).GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString();

        Assert.Equal("file:///var/data/script.sql", uri);
    }

    [Fact]
    public void Write_RelativeSourcePathWithSpecialCharacters_EscapesEachSegmentIndependently()
    {
        var finding = new SargabilityFinding(SargabilityFindingKind.ColumnArithmetic, "Col", null, "dir with space/a b.sql", 1, 1);
        var report = TestScanReports.Build(Tier1Findings: [finding]);

        var uri = FirstResult(report).GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString();

        Assert.Equal("dir%20with%20space/a%20b.sql", uri);
    }

    [Fact]
    public void Write_RelativeSourcePathWithBackslashes_NormalizesToForwardSlashSegments()
    {
        var finding = new SargabilityFinding(SargabilityFindingKind.ColumnArithmetic, "Col", null, @"folder\nested\file.sql", 1, 1);
        var report = TestScanReports.Build(Tier1Findings: [finding]);

        var uri = FirstResult(report).GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString();

        Assert.Equal("folder/nested/file.sql", uri);
    }

    [Fact]
    public void Write_MultipleParseErrorsAcrossFiles_EmitsOneNotificationPerErrorWithLineAndColumn()
    {
        var parseHealth = new ParseHealthReport(
        [
            new FileParseHealth("a.sql", [new ParseErrorInfo(3, 8, 102, "Incorrect syntax near 'FROM'.")], 1),
            new FileParseHealth("b.sql", [new ParseErrorInfo(1, 1, 102, "Incorrect syntax near 'WHERE'.")], 1),
        ]);
        var report = TestScanReports.Build(ParseHealth: parseHealth);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);
        var notifications = document.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0].GetProperty("toolExecutionNotifications");

        Assert.Equal(2, notifications.GetArrayLength());
        var first = notifications[0];
        Assert.Equal("warning", first.GetProperty("level").GetString());
        Assert.Contains("a.sql", first.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
        Assert.Contains("Incorrect syntax near 'FROM'.", first.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
        var region = first.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("region");
        Assert.Equal(3, region.GetProperty("startLine").GetInt32());
        Assert.Equal(8, region.GetProperty("startColumn").GetInt32());
    }

    [Theory]
    [InlineData(UnanalyzedObjectKind.Procedure, "procedure")]
    [InlineData(UnanalyzedObjectKind.View, "view")]
    [InlineData(UnanalyzedObjectKind.Function, "function")]
    [InlineData(UnanalyzedObjectKind.Trigger, "trigger")]
    [InlineData(UnanalyzedObjectKind.Table, "table")]
    [InlineData(UnanalyzedObjectKind.Unidentified, "object")]
    public void Write_UnanalyzedBatchWithObjectName_DescribesKindByNoun(UnanalyzedObjectKind kind, string expectedNoun)
    {
        var parseHealth = new ParseHealthReport(
        [
            new FileParseHealth("mixed.sql", [], 2, [new UnanalyzedBatch("mixed.sql", 5, kind, "dbo.Widget")]),
        ]);
        var report = TestScanReports.Build(ParseHealth: parseHealth);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);
        var notification = document.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0].GetProperty("toolExecutionNotifications")[0];

        Assert.Equal($"Batch in 'mixed.sql' failed to parse and was dropped - {expectedNoun} 'dbo.Widget' received zero analysis.", notification.GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_UnanalyzedBatchWithNoObjectName_DescribesAnUnidentifiedObjectRegardlessOfKind()
    {
        var parseHealth = new ParseHealthReport(
        [
            new FileParseHealth("mixed.sql", [], 1, [new UnanalyzedBatch("mixed.sql", 5, UnanalyzedObjectKind.Procedure, null)]),
        ]);
        var report = TestScanReports.Build(ParseHealth: parseHealth);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);
        var notification = document.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0].GetProperty("toolExecutionNotifications")[0];

        Assert.Equal("Batch in 'mixed.sql' failed to parse and was dropped - an unidentified object received zero analysis.", notification.GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_UnanalyzedBatchNotification_OmitsStartColumnBecauseOnlyStartLineIsKnown()
    {
        var parseHealth = new ParseHealthReport(
        [
            new FileParseHealth("mixed.sql", [], 1, [new UnanalyzedBatch("mixed.sql", 5, UnanalyzedObjectKind.View, "dbo.V")]),
        ]);
        var report = TestScanReports.Build(ParseHealth: parseHealth);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);
        var notification = document.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0].GetProperty("toolExecutionNotifications")[0];
        var region = notification.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("region");

        Assert.Equal(5, region.GetProperty("startLine").GetInt32());
        Assert.False(region.TryGetProperty("startColumn", out _));
    }

    [Theory]
    [InlineData(DatabaseConfigurationFindingKind.PageVerifyNotChecksum, "warning", "PAGE_VERIFY is not CHECKSUM")]
    [InlineData(DatabaseConfigurationFindingKind.AutoShrinkOn, "warning", "AUTO_SHRINK is ON")]
    [InlineData(DatabaseConfigurationFindingKind.AutoCloseOn, "warning", "AUTO_CLOSE is ON")]
    [InlineData(DatabaseConfigurationFindingKind.TargetRecoveryTimeUnset, "warning", "TARGET_RECOVERY_TIME is 0")]
    [InlineData(DatabaseConfigurationFindingKind.QueryStoreNotReadWrite, "note", "Query Store is not actively running")]
    [InlineData(DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto, "note", "capture mode other than AUTO")]
    [InlineData(DatabaseConfigurationFindingKind.AutoCreateStatisticsOff, "warning", "AUTO_CREATE_STATISTICS is OFF")]
    [InlineData(DatabaseConfigurationFindingKind.AutoUpdateStatisticsOff, "warning", "AUTO_UPDATE_STATISTICS is OFF")]
    [InlineData(DatabaseConfigurationFindingKind.CompatibilityLevelBehindEngineDefault, "warning", "compatibility level is behind")]
    public void Write_DatabaseConfigurationFinding_MapsKindToDistinctLevelAndMessage(DatabaseConfigurationFindingKind kind, string expectedLevel, string expectedSubstring)
    {
        var finding = new DatabaseConfigurationFinding(kind, "TestDb");
        var report = TestScanReports.Build(DatabaseConfigurationFindings: [finding]);

        var result = FirstResult(report);

        Assert.Equal(expectedLevel, result.GetProperty("level").GetString());
        Assert.Contains(expectedSubstring, result.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
        Assert.Equal("TestDb", result.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());
    }

    [Fact]
    public void Write_SpatialPersistedComputedColumnDisabledFinding_InterpolatesAffectedObjectDependencyAndTargetLevel()
    {
        var finding = new DatabaseConfigurationFinding(
            DatabaseConfigurationFindingKind.SpatialPersistedComputedColumnDisabledOnCompatibilityLevelChange,
            "TestDb",
            AffectedObjectName: "dbo.Locations.GeoHash",
            Dependency: "a spatial index",
            TargetCompatibilityLevel: 160);
        var report = TestScanReports.Build(DatabaseConfigurationFindings: [finding]);

        var message = FirstResult(report).GetProperty("message").GetProperty("text").GetString()!;

        Assert.Contains("dbo.Locations.GeoHash depends on a spatial index", message, StringComparison.Ordinal);
        Assert.Contains("compatibility level 160", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_PlanGuideAltersOptimizationFinding_InterpolatesNameScopeAndHints()
    {
        var finding = new DatabaseConfigurationFinding(
            DatabaseConfigurationFindingKind.PlanGuideAltersOptimization,
            "TestDb",
            AffectedObjectName: "PG_Test",
            PlanGuideScopeType: "SQL",
            PlanGuideHints: "OPTION (RECOMPILE)");
        var report = TestScanReports.Build(DatabaseConfigurationFindings: [finding]);
        var result = FirstResult(report);

        var message = result.GetProperty("message").GetProperty("text").GetString()!;

        Assert.Equal("note", result.GetProperty("level").GetString());
        Assert.Contains("PG_Test", message, StringComparison.Ordinal);
        Assert.Contains("scope SQL", message, StringComparison.Ordinal);
        Assert.Contains("OPTION (RECOMPILE)", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ManyFindingCollectionsAtOnce_PreservesPerCollectionOrderingAndTotalCount()
    {
        var report = TestScanReports.Build(
            FloatEqualityFindings: [new FloatEqualityFinding("dbo.T", "A", "float", "test.sql", 1, 1)],
            DatabaseConfigurationFindings:
            [
                new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.AutoShrinkOn, "Db1"),
                new DatabaseConfigurationFinding(DatabaseConfigurationFindingKind.AutoCloseOn, "Db1"),
            ],
            UntrustedConstraintFindings: [new UntrustedConstraintFinding(UntrustedConstraintFindingKind.CheckConstraint, "CK_X", "dbo.T", "test.sql", 2)]);

        var sarif = SarifReportWriter.Write(report);
        using var document = JsonDocument.Parse(sarif);
        var results = document.RootElement.GetProperty("runs")[0].GetProperty("results");

        Assert.Equal(4, results.GetArrayLength());
        Assert.Contains("CHECK constraint on 'dbo.T'", results[0].GetProperty("message").GetProperty("text").GetString());
        Assert.Contains("AUTO_SHRINK is ON", results[1].GetProperty("message").GetProperty("text").GetString());
        Assert.Contains("AUTO_CLOSE is ON", results[2].GetProperty("message").GetProperty("text").GetString());
        Assert.Equal(SarifRuleCatalog.FloatEqualityRuleId, results[3].GetProperty("ruleId").GetString());
    }

    [Theory]
    [InlineData(SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature, "compiled under QUOTED_IDENTIFIER OFF")]
    [InlineData(SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature, "compiled under ANSI_NULLS OFF")]
    [InlineData(SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature, "SET NUMERIC_ROUNDABORT ON")]
    [InlineData(SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature, "SET ANSI_WARNINGS OFF")]
    [InlineData(SetOptionFindingKind.AnsiPaddingOffBlocksIndexedFeature, "SET ANSI_PADDING OFF")]
    [InlineData(SetOptionFindingKind.ConcatNullYieldsNullOffBlocksIndexedFeature, "SET CONCAT_NULL_YIELDS_NULL OFF")]
    public void Write_SetOptionFinding_MapsKindToDistinctMessageAndErrorLevel(SetOptionFindingKind kind, string expectedSubstring)
    {
        var finding = new SetOptionFinding(kind, "dbo.usp_Test", "test.sql", 1, 1);
        var report = TestScanReports.Build(SetOptionFindings: [finding]);

        var result = FirstResult(report);

        Assert.Equal("error", result.GetProperty("level").GetString());
        Assert.Contains(expectedSubstring, result.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_SetOptionFindingWithNoTouchedObject_OmitsTouchedObjectClause()
    {
        var finding = new SetOptionFinding(SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature, "dbo.usp_Test", "test.sql", 1, 1);
        var report = TestScanReports.Build(SetOptionFindings: [finding]);

        var message = FirstResult(report).GetProperty("message").GetProperty("text").GetString()!;

        Assert.DoesNotContain("touches", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_SetOptionFindingTouchingIndexedView_DescribesFeatureAsIndexedViewWithoutIndexSuffix()
    {
        var finding = new SetOptionFinding(
            SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature, "dbo.usp_Test", "test.sql", 1, 1,
            TouchedObjectQualifiedName: "dbo.vw_Indexed", TouchedIsIndexedView: true);
        var report = TestScanReports.Build(SetOptionFindings: [finding]);

        var message = FirstResult(report).GetProperty("message").GetProperty("text").GetString()!;

        Assert.Contains("touches indexed view 'dbo.vw_Indexed'", message, StringComparison.Ordinal);
        Assert.DoesNotContain("filtered index", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_SetOptionFindingTouchingFilteredIndexWithName_AppendsIndexNameSuffix()
    {
        var finding = new SetOptionFinding(
            SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature, "dbo.usp_Test", "test.sql", 1, 1,
            TouchedObjectQualifiedName: "dbo.T", TouchedIndexName: "IX_Filtered", TouchedIsIndexedView: false);
        var report = TestScanReports.Build(SetOptionFindings: [finding]);

        var message = FirstResult(report).GetProperty("message").GetProperty("text").GetString()!;

        Assert.Contains("touches filtered index 'dbo.T'.IX_Filtered", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ForcedSerialTableVariableModification_NamesTheTableVariableInMessage()
    {
        var finding = new ForcedSerialFinding(ForcedSerialFindingKind.TableVariableModification, "dbo.usp_Test", "test.sql", 1, 1, DetailText: "@Buffer");
        var report = TestScanReports.Build(ForcedSerialFindings: [finding]);

        Assert.Contains("writes to table variable '@Buffer'", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_ForcedSerialFastForwardCursor_NamesTheCursorInMessage()
    {
        var finding = new ForcedSerialFinding(ForcedSerialFindingKind.FastForwardCursor, "dbo.usp_Test", "test.sql", 1, 1, DetailText: "cur_orders");
        var report = TestScanReports.Build(ForcedSerialFindings: [finding]);

        Assert.Contains("cursor 'cur_orders' is FAST_FORWARD", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_ForcedSerialIntrinsicStartingWithDoubleAt_DoesNotAppendParenthesesSuffix()
    {
        var finding = new ForcedSerialFinding(ForcedSerialFindingKind.NonParallelizableIntrinsic, "dbo.usp_Test", "test.sql", 1, 1, DetailText: "@@ROWCOUNT");
        var report = TestScanReports.Build(ForcedSerialFindings: [finding]);

        var message = FirstResult(report).GetProperty("message").GetProperty("text").GetString()!;
        Assert.Contains("@@ROWCOUNT referenced inside a query", message, StringComparison.Ordinal);
        Assert.DoesNotContain("@@ROWCOUNT()", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ForcedSerialIntrinsicNotStartingWithDoubleAt_AppendsParenthesesSuffix()
    {
        var finding = new ForcedSerialFinding(ForcedSerialFindingKind.NonParallelizableIntrinsic, "dbo.usp_Test", "test.sql", 1, 1, DetailText: "NEWSEQUENTIALID");
        var report = TestScanReports.Build(ForcedSerialFindings: [finding]);

        Assert.Contains("NEWSEQUENTIALID() referenced inside a query", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_TempTableExecShapeColumnCountMismatch_MapsToErrorLevelWithBothCounts()
    {
        var finding = new TempTableExecShapeFinding(
            TempTableExecShapeFindingKind.ColumnCountMismatch, "#Buffer", "dbo.usp_Producer", 3, 2,
            ColumnName: null, ColumnPosition: null, TempColumnTypeDisplay: null, DescribedColumnTypeDisplay: null,
            WriteLoss: null, CallerScopeQualifiedName: null, SourcePath: "test.sql", Line: 1, Column: 1);
        var report = TestScanReports.Build(TempTableExecShapeFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal("error", result.GetProperty("level").GetString());
        Assert.Contains("targets 3 column(s) but the executed proc's real result set describes 2", result.GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_TempTableExecShapeColumnTypeMismatch_MapsToWarningLevelNamingPositionAndTypes()
    {
        var finding = new TempTableExecShapeFinding(
            TempTableExecShapeFindingKind.ColumnTypeMismatch, "#Buffer", "dbo.usp_Producer", 3, 3,
            ColumnName: "Amount", ColumnPosition: 2, TempColumnTypeDisplay: "int", DescribedColumnTypeDisplay: "decimal(10,2)",
            WriteLoss: WriteLossKind.NumericScaleNarrowing, CallerScopeQualifiedName: null, SourcePath: "test.sql", Line: 1, Column: 1);
        var report = TestScanReports.Build(TempTableExecShapeFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal("warning", result.GetProperty("level").GetString());
        Assert.Contains("position 2 ('Amount', int) receives decimal(10,2)", result.GetProperty("message").GetProperty("text").GetString());
        Assert.Contains("digits past the target's scale are silently rounded away", result.GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(CodeMetricFindingKind.LineTooLong, 120, 100, null, "Line is 120 characters long, which is greater than the 100 authorized.")]
    [InlineData(CodeMetricFindingKind.ModuleTooLong, 900, 500, null, "'dbo.usp_Test' has 900 lines, which is greater than the 500 authorized.")]
    [InlineData(CodeMetricFindingKind.NestingTooDeep, 6, 4, null, "Control flow nests 6 levels deep here, which is greater than the 4 authorized.")]
    [InlineData(CodeMetricFindingKind.TooManyConditionalOperators, 8, 5, null, "This condition chains 8 AND/OR operators, which is greater than the 5 authorized.")]
    [InlineData(CodeMetricFindingKind.TooManyCaseBranches, 20, 10, null, "This CASE expression has 20 WHEN branches, which is greater than the 10 authorized.")]
    [InlineData(CodeMetricFindingKind.CaseBranchTooLong, 15, 8, null, "This CASE WHEN branch spans 15 lines, which is greater than the 8 authorized.")]
    [InlineData(CodeMetricFindingKind.RoutineTooLong, 300, 150, "Procedure", "Procedure 'dbo.usp_Test' has 300 lines of code, which is greater than the 150 authorized.")]
    [InlineData(CodeMetricFindingKind.TooManyParameters, 25, 15, "Procedure", "Procedure 'dbo.usp_Test' has 25 parameters, which is greater than the 15 authorized.")]
    public void Write_CodeMetricFinding_MapsKindToExactMessageAndAlwaysNoteLevel(CodeMetricFindingKind kind, int measuredValue, int threshold, string? detailText, string expectedMessage)
    {
        var finding = new CodeMetricFinding(kind, "dbo.usp_Test", "test.sql", 1, 1, measuredValue, threshold, detailText, FindingConfidence.High);
        var report = TestScanReports.Build(CodeMetricFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal("note", result.GetProperty("level").GetString());
        Assert.Equal(expectedMessage, result.GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_FormattingMultipleDeclarationsOnSameLine_InterpolatesVariableNameFromDetailText()
    {
        var finding = new FormattingFinding(FormattingFindingKind.MultipleDeclarationsOnSameLine, "dbo.usp_Test", "test.sql", 1, 1, DetailText: "@Second");
        var report = TestScanReports.Build(FormattingFindings: [finding]);

        Assert.Equal(
            "'@Second' is declared on the same physical source line as the previous variable - declare each on its own line.",
            FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_FormattingMissingFileHeaderCommentDefaultArm_UsesFixedModuleCommentMessage()
    {
        var finding = new FormattingFinding(FormattingFindingKind.MissingFileHeaderComment, "dbo.usp_Test", "test.sql", 1, 1);
        var report = TestScanReports.Build(FormattingFindings: [finding]);

        Assert.Equal(
            "This module's own definition does not begin with a comment before its first real statement.",
            FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_FormattingTabCharacterUsed_UsesFixedTabMessage()
    {
        var finding = new FormattingFinding(FormattingFindingKind.TabCharacterUsed, "dbo.usp_Test", "test.sql", 1, 1);
        var report = TestScanReports.Build(FormattingFindings: [finding]);

        Assert.Contains("contains a tab character", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(DuplicationFindingKind.CommentedOutCode, null, "reparses as plausible T-SQL")]
    [InlineData(DuplicationFindingKind.DuplicatedStringLiteral, "'ACTIVE'", "String literal 'ACTIVE' -")]
    [InlineData(DuplicationFindingKind.SelfAssignment, "@x = @x", "\"@x = @x\" is assigned to itself")]
    [InlineData(DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison, "always false", "is always false regardless")]
    [InlineData(DuplicationFindingKind.CollapsibleNestedIf, null, "combine both conditions with AND")]
    [InlineData(DuplicationFindingKind.AllBranchesIdentical, null, "the structure itself is pointless")]
    public void Write_DuplicationFinding_MapsKindToDistinctMessageAndWarningLevelAtHighConfidence(DuplicationFindingKind kind, string? detailText, string expectedSubstring)
    {
        var finding = new DuplicationFinding(kind, "dbo.usp_Test", "test.sql", 1, 1, detailText, FindingConfidence.High);
        var report = TestScanReports.Build(DuplicationFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal("warning", result.GetProperty("level").GetString());
        Assert.Contains(expectedSubstring, result.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeadCodeFindingKind.UnreachableCode, null, "can never execute")]
    [InlineData(DeadCodeFindingKind.UnusedLabel, "MyLabel", "Label \"MyLabel\" is never the target of a GOTO")]
    [InlineData(DeadCodeFindingKind.UnusedLocalVariable, "@x", "Local variable \"@x\" is declared but never read")]
    [InlineData(DeadCodeFindingKind.UnusedParameter, "@p", "Parameter \"@p\" is never referenced")]
    [InlineData(DeadCodeFindingKind.RedundantJump, "NextStep", "GOTO NextStep jumps to the very next statement")]
    public void Write_DeadCodeFinding_MapsKindToDistinctMessageAndWarningLevel(DeadCodeFindingKind kind, string? detailText, string expectedSubstring)
    {
        var finding = new DeadCodeFinding(kind, "dbo.usp_Test", "test.sql", 1, 1, detailText, FindingConfidence.High);
        var report = TestScanReports.Build(DeadCodeFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal("warning", result.GetProperty("level").GetString());
        Assert.Contains(expectedSubstring, result.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CheckConstraintFindingKind.NullNotHandled, "no IS NULL/IS NOT NULL test")]
    [InlineData(CheckConstraintFindingKind.ConstraintOnIdentityColumn, "references the IDENTITY column")]
    public void Write_CheckConstraintFinding_MapsKindToDistinctMessageAndErrorLevel(CheckConstraintFindingKind kind, string expectedSubstring)
    {
        var finding = new CheckConstraintFinding(kind, "CK_Test", "dbo.T", "Col", "test.sql", 1);
        var report = TestScanReports.Build(CheckConstraintFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal("error", result.GetProperty("level").GetString());
        Assert.Contains(expectedSubstring, result.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TriggerCorrectnessFindingKind.MultiRowUnsafeSingleRowAssignment, "error")]
    [InlineData(TriggerCorrectnessFindingKind.MultiRowUnsafeKeyedDml, "error")]
    [InlineData(TriggerCorrectnessFindingKind.NoEarlyOutForEmptyInvocation, "note")]
    [InlineData(TriggerCorrectnessFindingKind.DirectRecursiveTrigger, "warning")]
    [InlineData(TriggerCorrectnessFindingKind.InsteadOfInsertFilteredNoRejectPath, "error")]
    [InlineData(TriggerCorrectnessFindingKind.UpdateFunctionWithoutValueComparison, "warning")]
    [InlineData(TriggerCorrectnessFindingKind.LogonTriggerHostNameGate, "error")]
    public void Write_TriggerCorrectnessFinding_MapsEachKindToItsOwnLevel(TriggerCorrectnessFindingKind kind, string expectedLevel)
    {
        var finding = new TriggerCorrectnessFinding(kind, "dbo.trg_Test", "test.sql", 1, 1, "detail", FindingConfidence.High);
        var report = TestScanReports.Build(TriggerCorrectnessFindings: [finding]);

        Assert.Equal(expectedLevel, FirstResult(report).GetProperty("level").GetString());
    }

    [Theory]
    [InlineData(TvfFenceFindingKind.CorrelatedApply, "error", "re-executes once per outer row")]
    [InlineData(TvfFenceFindingKind.NestedUnderViewOrTvf, "error", "inherits an optimization fence")]
    [InlineData(TvfFenceFindingKind.FromOrJoin, "warning", "the optimizer cannot see into its body")]
    [InlineData(TvfFenceFindingKind.InsertExec, "warning", "forces the procedure's entire result set to be spooled")]
    [InlineData(TvfFenceFindingKind.Standalone, "note", "nothing surrounds it")]
    public void Write_TvfFenceFinding_MapsKindToDistinctLevelAndMessage(TvfFenceFindingKind kind, string expectedLevel, string expectedSubstring)
    {
        var finding = new TvfFenceFinding(
            kind, FunctionQualifiedName: "dbo.fn_Test", ReferencedObjectQualifiedName: "dbo.vw_Test",
            FunctionKind: TableValuedFunctionKind.Inline, SourcePath: "test.sql", Line: 1, Column: 1,
            Confidence: FindingConfidence.High);
        var report = TestScanReports.Build(TvfFenceFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal(expectedLevel, result.GetProperty("level").GetString());
        Assert.Contains(expectedSubstring, result.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_TvfFenceCorrelatedApplyWithNoOuterColumns_RendersEmptyColumnListInsteadOfThrowing()
    {
        var finding = new TvfFenceFinding(
            TvfFenceFindingKind.CorrelatedApply, FunctionQualifiedName: "dbo.fn_Test", ReferencedObjectQualifiedName: null,
            FunctionKind: TableValuedFunctionKind.Inline, SourcePath: "test.sql", Line: 1, Column: 1,
            CorrelatedOuterColumns: null);
        var report = TestScanReports.Build(TvfFenceFindings: [finding]);

        Assert.Contains("correlated to  - the body", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(ScalarUdfFindingKind.PredicateInvocation, "error")]
    [InlineData(ScalarUdfFindingKind.NestedUnderViewOrTvf, "error")]
    [InlineData(ScalarUdfFindingKind.SchemaDependency, "warning")]
    [InlineData(ScalarUdfFindingKind.ProjectionInvocation, "note")]
    public void Write_ScalarUdfFinding_MapsKindToLevelWhenNotInlineable(ScalarUdfFindingKind kind, string expectedLevel)
    {
        var finding = BuildScalarUdfFinding(kind, ScalarUdfInlineability.NotInlineable);
        var report = TestScanReports.Build(ScalarUdfFindings: [finding]);

        Assert.Equal(expectedLevel, FirstResult(report).GetProperty("level").GetString());
    }

    [Fact]
    public void Write_ScalarUdfFindingInlineable_DowngradesPredicateInvocationFromErrorToWarning()
    {
        var finding = BuildScalarUdfFinding(ScalarUdfFindingKind.PredicateInvocation, ScalarUdfInlineability.Inlineable);
        var report = TestScanReports.Build(ScalarUdfFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal("warning", result.GetProperty("level").GetString());
        Assert.Contains("(inlined under SQL 2019+ FROID)", result.GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_ScalarUdfFindingNotInlineableWithBlockerReason_NamesTheBlockerInMessage()
    {
        var finding = BuildScalarUdfFinding(ScalarUdfFindingKind.PredicateInvocation, ScalarUdfInlineability.NotInlineable, inlineabilityBlocker: "TRY/CATCH");
        var report = TestScanReports.Build(ScalarUdfFindings: [finding]);

        Assert.Contains("(not inlineable: TRY/CATCH)", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(true, "[CLR, data access]")]
    [InlineData(false, "[CLR, no data access]")]
    [InlineData(null, "[CLR]")]
    public void Write_ScalarUdfFindingClrKind_AppendsDataAccessNoteBasedOnClrDataAccessValue(bool? clrDataAccess, string expectedSubstring)
    {
        var finding = BuildScalarUdfFinding(ScalarUdfFindingKind.PredicateInvocation, ScalarUdfInlineability.NotInlineable, udfKind: ScalarUdfKind.Clr, clrDataAccess: clrDataAccess);
        var report = TestScanReports.Build(ScalarUdfFindings: [finding]);

        Assert.Contains(expectedSubstring, FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_ScalarUdfFindingConstantArgumentsNotFolded_AppendsNonSchemaboundNote()
    {
        var finding = BuildScalarUdfFinding(ScalarUdfFindingKind.PredicateInvocation, ScalarUdfInlineability.NotInlineable, constantArgumentsNotFolded: true);
        var report = TestScanReports.Build(ScalarUdfFindings: [finding]);

        Assert.Contains("non-schemabound, so even literal arguments are not constant-folded", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    private static ScalarUdfFinding BuildScalarUdfFinding(
        ScalarUdfFindingKind kind,
        ScalarUdfInlineability inlineability,
        string? inlineabilityBlocker = null,
        ScalarUdfKind udfKind = ScalarUdfKind.TSql,
        bool? clrDataAccess = null,
        bool constantArgumentsNotFolded = false) => new(
            kind,
            "dbo.fn_Test",
            "dbo.vw_Consumer",
            udfKind,
            inlineability,
            inlineabilityBlocker,
            IsSchemaBound: true,
            constantArgumentsNotFolded,
            clrDataAccess,
            ScalarUdfContext.Where,
            SchemaDependencyKind: null,
            SourcePath: "test.sql",
            Line: 1,
            Column: 1);

    [Theory]
    [InlineData(MemoryOptimizedUnsupportedIndexOptionKind.ClusteredIndex, "rowstore CLUSTERED index")]
    [InlineData(MemoryOptimizedUnsupportedIndexOptionKind.IncludedColumns, "declares INCLUDE columns")]
    [InlineData(MemoryOptimizedUnsupportedIndexOptionKind.FilteredIndex, "is a filtered index (WHERE clause)")]
    public void Write_MemoryOptimizedUnsupportedIndexOptionFinding_MapsKindToDistinctMessage(MemoryOptimizedUnsupportedIndexOptionKind kind, string expectedSubstring)
    {
        var finding = new MemoryOptimizedUnsupportedIndexOptionFinding("dbo.T", "IX_Test", kind, "test.sql", 1);
        var report = TestScanReports.Build(MemoryOptimizedUnsupportedIndexOptionFindings: [finding]);

        Assert.Contains(expectedSubstring, FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(MemoryOptimizedForeignKeyFindingKind.CrossStorageForeignKey, "exactly one side is memory-optimized")]
    [InlineData(MemoryOptimizedForeignKeyFindingKind.ReferentialAction, "referential action other than NO ACTION")]
    public void Write_MemoryOptimizedForeignKeyFinding_MapsKindToDistinctMessage(MemoryOptimizedForeignKeyFindingKind kind, string expectedSubstring)
    {
        var finding = new MemoryOptimizedForeignKeyFinding("FK_Test", "dbo.Parent", "dbo.Child", kind, "test.sql", 1);
        var report = TestScanReports.Build(MemoryOptimizedForeignKeyFindings: [finding]);

        Assert.Contains(expectedSubstring, FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_MemoryOptimizedSchemaOnlyDurabilityFinding_MapsToDeploymentMessage()
    {
        var finding = new MemoryOptimizedSchemaOnlyDurabilityFinding("dbo.SessionCache", "test.sql", 1);
        var report = TestScanReports.Build(MemoryOptimizedSchemaOnlyDurabilityFindings: [finding]);

        Assert.Contains("DURABILITY = SCHEMA_ONLY", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(QueryAntiPatternFindingKind.TableVariableLowCompatEstimate, "error")]
    [InlineData(QueryAntiPatternFindingKind.CountStarVariableExistenceCheck, "error")]
    [InlineData(QueryAntiPatternFindingKind.NonAggregateHavingPredicate, "warning")]
    [InlineData(QueryAntiPatternFindingKind.MergeNonUniqueUsingSource, "error")]
    [InlineData(QueryAntiPatternFindingKind.RecursiveCteMissingMaxRecursion, "error")]
    [InlineData(QueryAntiPatternFindingKind.GroupingSetsCardinalityLimitExceeded, "error")]
    [InlineData(QueryAntiPatternFindingKind.GlobalCursorDeclaration, "warning")]
    public void Write_QueryAntiPatternFinding_MapsKindToItsOwnLevelBucket(QueryAntiPatternFindingKind kind, string expectedLevel)
    {
        var finding = new QueryAntiPatternFinding(kind, "test.sql", 1, 1, "detail", FindingConfidence.High);
        var report = TestScanReports.Build(QueryAntiPatternFindings: [finding]);

        Assert.Equal(expectedLevel, FirstResult(report).GetProperty("level").GetString());
    }

    [Theory]
    [InlineData(IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable, "warning")]
    [InlineData(IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization, "warning")]
    [InlineData(IndexDesignFindingKind.TimestampColumnNaming, "note")]
    [InlineData(IndexDesignFindingKind.HeapWithNonclusteredIndexes, "error")]
    public void Write_IndexDesignFinding_MapsKindToItsOwnLevelBucket(IndexDesignFindingKind kind, string expectedLevel)
    {
        var finding = new IndexDesignFinding(kind, "dbo.T", "IX_Test", "detail", "test.sql", 1);
        var report = TestScanReports.Build(IndexDesignFindings: [finding]);

        Assert.Equal(expectedLevel, FirstResult(report).GetProperty("level").GetString());
    }

    [Theory]
    [InlineData(ViewOrderingFindingKind.TopPercentOrderByNeverLimits, "warning", "TOP (100) PERCENT")]
    [InlineData(ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer, "note", "row-limiting TOP/OFFSET")]
    public void Write_ViewOrderingFinding_MapsKindToItsOwnLevelAndMessage(ViewOrderingFindingKind kind, string expectedLevel, string expectedSubstring)
    {
        var finding = new ViewOrderingFinding(kind, "dbo.vw_Test", "test.sql", 1, 1, FindingConfidence.High);
        var report = TestScanReports.Build(ViewOrderingFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal(expectedLevel, result.GetProperty("level").GetString());
        Assert.Contains(expectedSubstring, result.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CartesianJoinKind.ExplicitCrossJoin, "CROSS JOIN")]
    [InlineData(CartesianJoinKind.CommaJoin, "comma-join")]
    public void Write_CartesianJoinFinding_MapsKindToJoinShapeWording(CartesianJoinKind kind, string expectedSubstring)
    {
        var finding = new CartesianJoinFinding(kind, "dbo.A", "dbo.B", "test.sql", 1, 1);
        var report = TestScanReports.Build(CartesianJoinFindings: [finding]);

        Assert.Contains(expectedSubstring, FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(UnparameterizedDynamicSqlFindingKind.ExecStringConcatenatesParameterizableValue, "This EXEC(string) call concatenates")]
    [InlineData(UnparameterizedDynamicSqlFindingKind.ConcatenatedValueInConstantSql, "This dynamic SQL call concatenates")]
    public void Write_UnparameterizedDynamicSqlFinding_MapsKindToDistinctOpeningSentence(UnparameterizedDynamicSqlFindingKind kind, string expectedPrefix)
    {
        var finding = new UnparameterizedDynamicSqlFinding("test.sql", 1, 1, kind);
        var report = TestScanReports.Build(UnparameterizedDynamicSqlFindings: [finding]);

        Assert.StartsWith(expectedPrefix, FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(OperandComparabilityContext.Comparison, "compared with = in this predicate")]
    [InlineData(OperandComparabilityContext.In, "used in an IN list")]
    [InlineData(OperandComparabilityContext.Between, "used in a BETWEEN")]
    [InlineData(OperandComparabilityContext.NullIf, "used in a NULLIF")]
    [InlineData(OperandComparabilityContext.OrderBy, "referenced in this ORDER BY clause")]
    [InlineData(OperandComparabilityContext.GroupBy, "referenced in this GROUP BY clause")]
    [InlineData(OperandComparabilityContext.Distinct, "selected under SELECT DISTINCT")]
    public void Write_OperandComparabilityFinding_MapsContextToPositionWording(OperandComparabilityContext context, string expectedSubstring)
    {
        var finding = new OperandComparabilityFinding("dbo.T", "Col", "xml", OperandComparabilityFindingKind.Xml, context, "=", "test.sql", 1, 1);
        var report = TestScanReports.Build(OperandComparabilityFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal("error", result.GetProperty("level").GetString());
        Assert.Contains(expectedSubstring, result.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OperandComparabilityFindingKind.Xml, "the xml data type is not comparable")]
    [InlineData(OperandComparabilityFindingKind.LegacyLargeObject, "the text/ntext/image data type is not comparable")]
    [InlineData(OperandComparabilityFindingKind.Json, "the json data type is not comparable")]
    public void Write_OperandComparabilityFinding_MapsKindToTypeLabel(OperandComparabilityFindingKind kind, string expectedSubstring)
    {
        var finding = new OperandComparabilityFinding("dbo.T", "Col", "xml", kind, OperandComparabilityContext.Comparison, "=", "test.sql", 1, 1);
        var report = TestScanReports.Build(OperandComparabilityFindings: [finding]);

        Assert.Contains(expectedSubstring, FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(SessionDateSettingKind.DateFormat, "SET DATEFORMAT changes")]
    [InlineData(SessionDateSettingKind.DateFirst, "SET DATEFIRST changes")]
    public void Write_SessionDateSettingFinding_MapsKindToDistinctMessage(SessionDateSettingKind kind, string expectedSubstring)
    {
        var finding = new SessionDateSettingFinding(kind, "test.sql", 1, 1);
        var report = TestScanReports.Build(SessionDateSettingFindings: [finding]);

        Assert.Contains(expectedSubstring, FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(IndexHintFindingKind.IndexDoesNotExist, "error", "does not exist in the catalog")]
    [InlineData(IndexHintFindingKind.HintedIndexNotSeekable, "warning", "degrades the forced index to a full scan")]
    public void Write_IndexHintFinding_MapsKindToItsOwnLevelAndMessage(IndexHintFindingKind kind, string expectedLevel, string expectedSubstring)
    {
        var finding = new IndexHintFinding(kind, "dbo.T", "IX_Test", "Col", "test.sql", 1, 1);
        var report = TestScanReports.Build(IndexHintFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal(expectedLevel, result.GetProperty("level").GetString());
        Assert.Contains(expectedSubstring, result.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UnindexedTempTableUsageKind.JoinOperand, "joined")]
    [InlineData(UnindexedTempTableUsageKind.FilteredInWhere, "filtered by a WHERE predicate")]
    public void Write_UnindexedTempTableUsageFinding_MapsKindToUsageWordingAndUsesUsageLocationNotDeclarationLocation(UnindexedTempTableUsageKind kind, string expectedSubstring)
    {
        var finding = new UnindexedTempTableUsageFinding(kind, "#Buffer", "test.sql", DeclarationLine: 3, UsageLine: 9, UsageColumn: 4);
        var report = TestScanReports.Build(UnindexedTempTableUsageFindings: [finding]);

        var result = FirstResult(report);
        Assert.Contains(expectedSubstring, result.GetProperty("message").GetProperty("text").GetString()!, StringComparison.Ordinal);
        var region = result.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("region");
        Assert.Equal(9, region.GetProperty("startLine").GetInt32());
        Assert.Equal(4, region.GetProperty("startColumn").GetInt32());
    }

    [Theory]
    [InlineData(true, "holds this worker thread idle inside an open transaction")]
    [InlineData(false, "contributing to worker-pool exhaustion")]
    public void Write_WaitForFinding_MapsInsideTransactionFlagToDistinctMessage(bool isInsideTransaction, string expectedSubstring)
    {
        var finding = new WaitForFinding("test.sql", 1, 1, isInsideTransaction);
        var report = TestScanReports.Build(WaitForFindings: [finding]);

        Assert.Contains(expectedSubstring, FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_SelfReferencingDmlFindingThroughView_NamesTheViewInParentheses()
    {
        var finding = new SelfReferencingDmlFinding(SelfReferencingDmlFindingKind.ThroughView, "UPDATE", "dbo.T", "dbo.vw_T", "test.sql", 1, 1);
        var report = TestScanReports.Build(SelfReferencingDmlFindings: [finding]);

        Assert.Contains("(through view 'dbo.vw_T')", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_SelfReferencingDmlFindingDirectTableReference_OmitsViewParentheses()
    {
        var finding = new SelfReferencingDmlFinding(SelfReferencingDmlFindingKind.DirectTableReference, "UPDATE", "dbo.T", "dbo.T", "test.sql", 1, 1);
        var report = TestScanReports.Build(SelfReferencingDmlFindings: [finding]);

        Assert.DoesNotContain("through view", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_UntrustedConstraintFindingForeignKey_DescribesKindAsForeignKey()
    {
        var finding = new UntrustedConstraintFinding(UntrustedConstraintFindingKind.ForeignKey, "FK_Test", "dbo.T", "test.sql", 1);
        var report = TestScanReports.Build(UntrustedConstraintFindings: [finding]);

        Assert.Contains("(foreign key on 'dbo.T')", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_DanglingObjectReferenceWithSchema_PrefixesReferencedEntityWithSchema()
    {
        var finding = new DanglingObjectReferenceFinding("dbo.usp_Test", "Procedure", "Widget", "sales", "test.sql", 1, 1);
        var report = TestScanReports.Build(DanglingObjectReferenceFindings: [finding]);

        Assert.Contains("references 'sales.Widget'", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_DanglingObjectReferenceWithNoSchema_UsesBareEntityName()
    {
        var finding = new DanglingObjectReferenceFinding("dbo.usp_Test", "Procedure", "Widget", null, "test.sql", 1, 1);
        var report = TestScanReports.Build(DanglingObjectReferenceFindings: [finding]);

        Assert.Contains("references 'Widget'", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
        Assert.DoesNotContain("references 'sales", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_NotInNullableSubqueryFindingWithOuterColumnName_UsesTheColumnName()
    {
        var finding = new NotInNullableSubqueryFinding("CustomerId", "dbo.Orders", "CustomerId", false, "test.sql", 1, 1);
        var report = TestScanReports.Build(NotInNullableSubqueryFindings: [finding]);

        Assert.StartsWith("CustomerId NOT IN", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_NotInNullableSubqueryFindingWithNoOuterColumnName_FallsBackToExpressionPlaceholder()
    {
        var finding = new NotInNullableSubqueryFinding(null, "dbo.Orders", "CustomerId", false, "test.sql", 1, 1);
        var report = TestScanReports.Build(NotInNullableSubqueryFindings: [finding]);

        Assert.StartsWith("<expression> NOT IN", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_WriteLossFindingWithTable_PrefixesColumnWithTableName()
    {
        var finding = new WriteLossFinding("dbo.T", "Col", WriteLossKind.ApproximateToExactTruncation, new SqlType(SqlTypeCategory.Int), new SqlType(SqlTypeCategory.Float), "test.sql", 1, 1);
        var report = TestScanReports.Build(WriteLossFindings: [finding]);

        Assert.StartsWith("'dbo.T.Col'", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_WriteLossFindingWithNoTable_UsesBareColumnName()
    {
        var finding = new WriteLossFinding(null, "@Local", WriteLossKind.ApproximateToExactTruncation, new SqlType(SqlTypeCategory.Int), new SqlType(SqlTypeCategory.Float), "test.sql", 1, 1);
        var report = TestScanReports.Build(WriteLossFindings: [finding]);

        Assert.StartsWith("'@Local'", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_PostExpansionJoinWidthFindingPartiallyUnexpanded_AppendsCaveatNote()
    {
        var finding = new PostExpansionJoinWidthFinding("dbo.vw_Test", 2, 5, ["dbo.A", "dbo.B"], ["dbo.vw_Inner"], PartiallyUnexpanded: true, "test.sql", 1, 1);
        var report = TestScanReports.Build(PostExpansionJoinWidthFindings: [finding]);

        Assert.Contains("(partially unexpanded - the real count may be higher)", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Write_PostExpansionJoinWidthFindingFullyExpanded_OmitsCaveatNote()
    {
        var finding = new PostExpansionJoinWidthFinding("dbo.vw_Test", 2, 5, ["dbo.A", "dbo.B"], ["dbo.vw_Inner"], PartiallyUnexpanded: false, "test.sql", 1, 1);
        var report = TestScanReports.Build(PostExpansionJoinWidthFindings: [finding]);

        Assert.DoesNotContain("partially unexpanded", FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(true, false, "no explicit length (defaults to 1)", "the compared value is silently truncated")]
    [InlineData(false, false, "length 5", "the compared value is silently truncated")]
    [InlineData(false, true, "length 5", "truncation changes what the")]
    public void Write_UnderLengthParameterFinding_CombinesImplicitDefaultAndShapeChangeFlagsCorrectly(
        bool isImplicitDefault, bool changesShape, string expectedLengthPhrase, string expectedTailSubstring)
    {
        var finding = new UnderLengthParameterFinding("dbo.T", "Col", 10, isImplicitDefault ? null : 5, isImplicitDefault, "=", changesShape, "test.sql", 1, 1);
        var report = TestScanReports.Build(UnderLengthParameterFindings: [finding]);

        var message = FirstResult(report).GetProperty("message").GetProperty("text").GetString()!;
        Assert.Contains(expectedLengthPhrase, message, StringComparison.Ordinal);
        Assert.Contains(expectedTailSubstring, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NonIndexableColumnFindingKind.MaxLength, "MAX-typed columns can never be an index key column")]
    [InlineData(NonIndexableColumnFindingKind.LegacyLargeObject, "TEXT/NTEXT/IMAGE columns can never appear in any index")]
    public void Write_MaxTypedColumnFinding_MapsKindToDistinctMessage(NonIndexableColumnFindingKind kind, string expectedSubstring)
    {
        var finding = new MaxTypedColumnFinding("dbo.T", "Col", "varchar(max)", "test.sql", 1, kind);
        var report = TestScanReports.Build(MaxTypedColumnFindings: [finding]);

        Assert.Contains(expectedSubstring, FirstResult(report).GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(SelectiveXmlIndexValueColumnFindingKind.TooWide, "silentscan/catalog/selective-xml-index-value-column-too-wide", "Msg 6395")]
    [InlineData(SelectiveXmlIndexValueColumnFindingKind.LargeObject, "silentscan/catalog/selective-xml-index-value-column-large-object", "Msg 6391")]
    public void Write_SelectiveXmlIndexValueColumnFinding_MapsKindToDistinctRuleIdAndMessage(
        SelectiveXmlIndexValueColumnFindingKind kind, string expectedRuleId, string expectedMessageSubstring)
    {
        var finding = new SelectiveXmlIndexValueColumnFinding(
            "dbo.Orders", "SXI_Orders_Note", "SXI_Orders", "Note", "varchar(901)", "test.sql", 1, kind);
        var report = TestScanReports.Build(SelectiveXmlIndexValueColumnFindings: [finding]);

        var result = FirstResult(report);
        Assert.Equal(expectedRuleId, result.GetProperty("ruleId").GetString());
        Assert.Contains(expectedMessageSubstring, result.GetProperty("message").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(StatementShapeFindingKind.BareSelectStar, "note")]
    [InlineData(StatementShapeFindingKind.TableWithNoPrimaryKey, "warning")]
    public void Write_StatementShapeFinding_OnlyBareSelectStarIsDowngradedToNote(StatementShapeFindingKind kind, string expectedLevel)
    {
        var finding = new StatementShapeFinding(kind, "dbo.usp_Test", "test.sql", 1, 1, "detail", FindingConfidence.High);
        var report = TestScanReports.Build(StatementShapeFindings: [finding]);

        Assert.Equal(expectedLevel, FirstResult(report).GetProperty("level").GetString());
    }

    [Theory]
    [InlineData(ControlFlowRiskFindingKind.CursorFetchColumnCountMismatch, "error")]
    [InlineData(ControlFlowRiskFindingKind.EmptyCatchBlock, "error")]
    [InlineData(ControlFlowRiskFindingKind.CaseExpressionMissingElse, "error")]
    [InlineData(ControlFlowRiskFindingKind.NonDeterministicCaseInput, "error")]
    [InlineData(ControlFlowRiskFindingKind.TriggerEmitsOutput, "warning")]
    public void Write_ControlFlowRiskFinding_OnlyTheFourNamedKindsAreError(ControlFlowRiskFindingKind kind, string expectedLevel)
    {
        var finding = new ControlFlowRiskFinding(kind, "dbo.usp_Test", "test.sql", 1, 1, "detail", FindingConfidence.High);
        var report = TestScanReports.Build(ControlFlowRiskFindings: [finding]);

        Assert.Equal(expectedLevel, FirstResult(report).GetProperty("level").GetString());
    }

    [Theory]
    [InlineData(SecurityFindingKind.HardCodedIpAddress, "error")]
    [InlineData(SecurityFindingKind.WeakHashAlgorithm, "error")]
    [InlineData(SecurityFindingKind.HardCodedCredential, "warning")]
    public void Write_SecurityFinding_OnlyIpAddressAndWeakHashAreError(SecurityFindingKind kind, string expectedLevel)
    {
        var finding = new SecurityFinding(kind, "test.sql", 1, 1, "detail", FindingConfidence.High);
        var report = TestScanReports.Build(SecurityFindings: [finding]);

        Assert.Equal(expectedLevel, FirstResult(report).GetProperty("level").GetString());
    }

    [Theory]
    [InlineData(DeprecatedSyntaxFindingKind.TaskCommentTodo, "note")]
    [InlineData(DeprecatedSyntaxFindingKind.TaskCommentFixme, "note")]
    [InlineData(DeprecatedSyntaxFindingKind.NonAnsiComparisonOperator, "warning")]
    public void Write_DeprecatedSyntaxFinding_OnlyTaskCommentsAreDowngradedToNote(DeprecatedSyntaxFindingKind kind, string expectedLevel)
    {
        var finding = new DeprecatedSyntaxFinding(kind, "dbo.usp_Test", "test.sql", 1, 1, "detail", FindingConfidence.High);
        var report = TestScanReports.Build(DeprecatedSyntaxFindings: [finding]);

        Assert.Equal(expectedLevel, FirstResult(report).GetProperty("level").GetString());
    }

    [Fact]
    public void Write_CascadingForeignKeyWithBothActionsSet_JoinsBothActionsIntoMessage()
    {
        var finding = new CascadingForeignKeyFinding("FK_Test", "dbo.Parent", "dbo.Child", ReferentialAction.Cascade, ReferentialAction.SetNull, "test.sql", 1);
        var report = TestScanReports.Build(CascadingForeignKeyFindings: [finding]);

        var message = FirstResult(report).GetProperty("message").GetProperty("text").GetString()!;
        Assert.Contains("ON DELETE Cascade, ON UPDATE SetNull", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_CascadingForeignKeyWithOnlyDeleteActionSet_OmitsUpdateActionClause()
    {
        var finding = new CascadingForeignKeyFinding("FK_Test", "dbo.Parent", "dbo.Child", ReferentialAction.Cascade, ReferentialAction.NoAction, "test.sql", 1);
        var report = TestScanReports.Build(CascadingForeignKeyFindings: [finding]);

        var message = FirstResult(report).GetProperty("message").GetProperty("text").GetString()!;
        Assert.Contains("ON DELETE Cascade", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ON UPDATE", message, StringComparison.Ordinal);
    }
}
