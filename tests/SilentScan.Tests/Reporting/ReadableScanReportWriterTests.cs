using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Reporting.Readable;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Reporting;

[Trait("Category", "Oracle")]
public sealed class ReadableScanReportWriterTests
{
    private const string LayeredSql = """
        CREATE TABLE dbo.Orders (
            OrderId INT NOT NULL PRIMARY KEY,
            OrderCode VARCHAR(20) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,
            Notes NVARCHAR(200) NULL
        );
        GO
        CREATE INDEX IX_Orders_OrderCode ON dbo.Orders (OrderCode);
        GO
        CREATE VIEW dbo.vw_OrdersInner AS SELECT OrderId, OrderCode, Notes FROM dbo.Orders;
        GO
        CREATE VIEW dbo.vw_OrdersOuter AS SELECT OrderId, OrderCode, Notes FROM dbo.vw_OrdersInner;
        GO
        CREATE PROCEDURE dbo.usp_FindOrder @Code NVARCHAR(20), @Notes VARCHAR(200)
        AS
        BEGIN
            SELECT OrderId FROM dbo.vw_OrdersOuter WHERE OrderCode = @Code;
            SELECT OrderId FROM dbo.Orders WHERE Notes = @Notes;
        END
        """;

    private static Task<ScanReport> Build(string sql, string? collation = null) =>
        EngineAuthoritativeScan.ScanAsync(sql, collation);

    private static string Render(ScanReport report) =>
        ReadableScanReportWriter.Write(report, "SilentScan - test", ReadableStyle.Text, verbosity: ReadableVerbosity.Full)
            .ReplaceLineEndings("\n");

    [Fact]
    public async Task ScanForcedFinding_CarriesLocationColumnIndexedAndTheLayerThatIntroducedIt()
    {
        var report = await Build(LayeredSql);
        var rendered = Render(report);

        var row = Assert.Single(
            rendered.Split('\n'),
            line => line.Contains("dbo.Orders.OrderCode", StringComparison.Ordinal) && line.Contains("dbo.usp_FindOrder:", StringComparison.Ordinal));

        Assert.Contains("Implicit conversions that force a scan (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("VarChar(20)", row, StringComparison.Ordinal);
        Assert.Contains("NVarChar(20)", row, StringComparison.Ordinal);
        Assert.Contains("yes", row, StringComparison.Ordinal);
        Assert.Contains("2 view layers via dbo.vw_OrdersOuter.OrderCode", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeekPreservedComparison_IsCountedInTheBaseRateButNeverListed()
    {
        var report = await Build(LayeredSql);
        var rendered = Render(report);

        Assert.Equal(1, report.TypedPredicateSummary.SeekPreservedCount);
        Assert.DoesNotContain("dbo.Orders.Notes", rendered, StringComparison.Ordinal);
        Assert.Contains("of which 1 keep their seek", rendered, StringComparison.Ordinal);
        Assert.Contains("2 column comparisons classified", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SectionsWithNothingToReport_AreOmitted()
    {
        var report = await Build(LayeredSql);
        var rendered = Render(report);

        Assert.DoesNotContain("Collation conflicts", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Non-sargable predicate patterns", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Files with parse errors", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Dynamic SQL that could not be analyzed", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFailures_AreListedWithTheirFirstError()
    {
        var parsed = SqlScriptParser.ParseText("broken.sql", "SELECT FROM WHERE ORDER;");
        Assert.True(parsed.HasErrors);
        var report = ScanReportBuilder.BuildFromParseResults([parsed], new DatabaseCatalog());

        var rendered = Render(report);

        Assert.Contains("Files with parse errors (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("broken.sql", rendered, StringComparison.Ordinal);
        Assert.Contains("line 1:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultVerbosity_IsBrief_ButNeverGatesARealFinding()
    {
        var report = await Build(LayeredSql);

        var rendered = ReadableScanReportWriter.Write(report, "SilentScan - test", ReadableStyle.Text)
            .ReplaceLineEndings("\n");

        var row = Assert.Single(
            rendered.Split('\n'),
            line => line.Contains("dbo.Orders.OrderCode", StringComparison.Ordinal) && line.Contains("dbo.usp_FindOrder:", StringComparison.Ordinal));

        Assert.Contains("Implicit conversions that force a scan (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("2 view layers via dbo.vw_OrdersOuter.OrderCode", row, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultVerbosity_IsBrief_ParseFailuresStateCountWithoutPerFileDetail()
    {
        var parsed = SqlScriptParser.ParseText("broken.sql", "SELECT FROM WHERE ORDER;");
        Assert.True(parsed.HasErrors);
        var report = ScanReportBuilder.BuildFromParseResults([parsed], new DatabaseCatalog());

        var rendered = ReadableScanReportWriter.Write(report, "SilentScan - test", ReadableStyle.Text)
            .ReplaceLineEndings("\n");

        Assert.Contains("Files with parse errors (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("re-run with --verbosity full", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("broken.sql", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("line 1:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FullVerbosity_RestoresParseFailurePerFileDetail()
    {
        var parsed = SqlScriptParser.ParseText("broken.sql", "SELECT FROM WHERE ORDER;");
        var report = ScanReportBuilder.BuildFromParseResults([parsed], new DatabaseCatalog());

        var rendered = ReadableScanReportWriter.Write(report, "SilentScan - test", ReadableStyle.Text, verbosity: ReadableVerbosity.Full)
            .ReplaceLineEndings("\n");

        Assert.Contains("broken.sql", rendered, StringComparison.Ordinal);
        Assert.Contains("line 1:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("re-run with --verbosity full", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void UnanalyzedObjects_DroppedBatchIsListedWithItsBestEffortIdentity()
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

        var rendered = Render(report);

        Assert.Contains("Unanalyzed objects - dropped batches (1)", rendered, StringComparison.Ordinal);
        Assert.Contains("mixed.sql", rendered, StringComparison.Ordinal);
        Assert.Contains("dbo.usp_Broken", rendered, StringComparison.Ordinal);
        Assert.Contains("procedure", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SectionsWithNothingToReport_OmitUnanalyzedObjects()
    {
        var report = await Build(LayeredSql);
        var rendered = Render(report);

        Assert.DoesNotContain("Unanalyzed objects", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonSargablePattern_IsExplainedOncePerPatternRatherThanPerRow()
    {
        const string Sql = """
            CREATE TABLE dbo.Users (Id INT NOT NULL, CreatedAt DATETIME NOT NULL, UpdatedAt DATETIME NOT NULL);
            GO
            CREATE PROCEDURE dbo.p @Year INT AS
            SELECT Id FROM dbo.Users WHERE YEAR(CreatedAt) = @Year AND YEAR(UpdatedAt) = @Year;
            """;

        var rendered = Render(await Build(Sql));

        Assert.Contains("Date-part function applied to the column (2)", rendered, StringComparison.Ordinal);
        var explanation = "Oracle-verified: the date-part function forces a per-row scan";
        Assert.Equal(1, CountOccurrences(rendered, explanation));
        Assert.Contains("CreatedAt", rendered, StringComparison.Ordinal);
        Assert.Contains("UpdatedAt", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkdownAndText_ReportTheSameFindings()
    {
        var report = await Build(LayeredSql);

        var text = ReadableScanReportWriter.Write(report, "t", ReadableStyle.Text);
        var markdown = ReadableScanReportWriter.Write(report, "t", ReadableStyle.Markdown);

        foreach (var expected in new[] { "dbo.Orders.OrderCode", "2 view layers via dbo.vw_OrdersOuter.OrderCode" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
            Assert.Contains(expected, markdown, StringComparison.Ordinal);
        }

        Assert.Contains("# t", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("# t", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FindingPaths_AreShownRelativeToTheScanRoot()
    {
        var report = ReportWithFindingAt(Path.Combine("/repo", "sql", "shop.sql"));

        var rendered = ReadableScanReportWriter.Write(report, "t", ReadableStyle.Text, "/repo");

        Assert.Contains(Path.Combine("sql", "shop.sql") + ":", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("/repo/sql", rendered.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void ScanRootThatIsOnlyATextualPrefix_IsNotTrimmed()
    {
        var report = ReportWithFindingAt("/src/application/shop.sql");

        var rendered = ReadableScanReportWriter.Write(report, "t", ReadableStyle.Text, "/src/app");

        Assert.Contains("/src/application/shop.sql:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("  lication/shop.sql", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionDerivedFindings_TheOnesWithARealIndexUnderneathComeFirst()
    {

        var indexed = new ExpressionDerivedFinding(
            "Col", "z_indexed.sql", 10, 1, [], [new UnderlyingBaseColumn("dbo.T1", "Col1", Indexed: true)]);
        var notIndexed = new ExpressionDerivedFinding(
            "Col", "a_notindexed.sql", 5, 1, [], [new UnderlyingBaseColumn("dbo.T2", "Col2", Indexed: false)]);

        var report = new ScanReport(
            new ParseHealthReport([]),
            [],
            [],
            [],
            [notIndexed, indexed],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
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

        var rendered = ReadableScanReportWriter.Write(report, "t", ReadableStyle.Text);

        Assert.True(
            rendered.IndexOf("z_indexed.sql:10", StringComparison.Ordinal) < rendered.IndexOf("a_notindexed.sql:5", StringComparison.Ordinal),
            "the finding with a real index underneath its expression must print before the one with none, regardless of source path order");
    }

    private static ScanReport ReportWithFindingAt(string sourcePath) => new(
        new ParseHealthReport([]),
            [],
        [new TypedPredicateFinding(
            Verdict.ScanForced,
            new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.VarChar), Indexed: true, Depth: 0, Provenance: null!),
            new PredicateOperand.Value(null),
            "=",
            sourcePath,
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
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
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

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static ScanReport Blank() => new(
        ParseHealth: new ParseHealthReport([]),
        Tier1Findings: [],
        TypedFindings: [],
        DynamicSqlFindings: [],
        ExpressionDerivedFindings: [],
        CollationConflictFindings: [],
        WriteLossFindings: [],
        TvfFenceFindings: [],
        ScalarUdfFindings: [],
        ColumnCollationDriftFindings: [],
        CrossTableTypeDriftFindings: [],
        ProcCallArgumentMismatchFindings: [],
        TemporalBoundaryFindings: [],
        MaxTypedColumnFindings: [],
        OversizedParameterFindings: [],
        UnderLengthParameterFindings: [],
        AnsiPaddingMismatchFindings: [],
        PartialCompositeForeignKeyJoinFindings: [],
        SetOptionFindings: [],
        CatchAllPredicateFindings: [],
        LocalVariablePredicateFindings: [],
        FilteredIndexParameterMismatchFindings: [],
        NotInNullableSubqueryFindings: [],
        NonUniqueUpdateSourceFindings: [],
        ForcedSerialFindings: [],
        UntrustedConstraintFindings: [],
        CascadingForeignKeyFindings: [],
        MultiReferencedCteFindings: [],
        NestedViewDepthFindings: [],
        PostExpansionJoinWidthFindings: [],
        SelectStarViewFindings: [],
        UnparameterizedDynamicSqlFindings: [],
        NonPersistedComputedColumnFindings: [],
        TempTableExecShapeFindings: [],
        SelfReferencingDmlFindings: [],
        TemporalTableHistoryIndexGapFindings: [],
        ModuleCompileFlagFindings: [],
        WindowFrameFindings: [],
        WaitForFindings: [],
        ViewOrderingFindings: [],
        TransactionHygieneFindings: [],
        CompositeIndexLeadingColumnFindings: [],
        IndexHintFindings: [],
        SessionDateSettingFindings: [],
        CartesianJoinFindings: [],
        UndersizedDeclarationFindings: [],
        TruncateSwallowedFindings: [],
        UnindexedTempTableUsageFindings: [],
        OutputParameterFindings: [],
        DatabaseConfigurationFindings: [],
        ParameterReassignmentPredicateFindings: [],
        CodeMetricFindings: [],
        FormattingFindings: [],
        NamingFindings: [],
        DeadCodeFindings: [],
        DuplicationFindings: [],
        DeprecatedSyntaxFindings: [],
        StatementShapeFindings: [],
        ControlFlowRiskFindings: [],
        SecurityFindings: [],
        IndexDesignFindings: [],
        IdentityRangeFindings: [],
        FloatEqualityFindings: [],
        QueryAntiPatternFindings: [],
        IndexCoverageFindings: [],
        TriggerCorrectnessFindings: [],
        CrossModuleLockOrderFindings: [],
        TriggerRecursionCycleFindings: [],
        CheckConstraintFindings: [],
        DefaultNullableConstraintFindings: [],
        TryCastComputedColumnPredicateFindings: [],
        StaleSelectStarViewFindings: [],
        BareTopNoOrderByFindings: [],
        StringConcatNullFindings: [],
        AggregateDivisionColumnstoreFindings: [],
        SecurityPredicateIndexFindings: [],
        DanglingObjectReferenceFindings: [],
        ForcedParameterizationFindings: [],
        ColumnstoreUnsupportedColumnTypeFindings: [],
        AlwaysEncryptedOrderByFindings: [],
        TriggerOrderFindings: [],
        MissingStatisticsFindings: [],
        OperandComparabilityFindings: [],
        MemoryOptimizedUnsupportedColumnTypeFindings: [],
        MemoryOptimizedUnsupportedIndexOptionFindings: [],
        MemoryOptimizedForeignKeyFindings: [],
        WindowFunctionArgumentFindings: [],
        SelectiveXmlIndexValueColumnFindings: [],
        FloatOrderDependentAggregateFindings: [],
        AlwaysEncryptedKeyColumnFindings: [],
        AlterColumnSafetyFindings: [],
        SkippedConstructs: [],
        SkippedConstructSummary: SkippedConstructSummary.From([]),
        TypedPredicateSummary: TypedPredicateSummary.From([]),
        DynamicSqlSummary: DynamicSqlSummary.From([]));

    private static IReadOnlyList<ReadableBlock> BuildBlocks(ScanReport report, string? pathBase = null, ReadableVerbosity verbosity = ReadableVerbosity.Full) =>
        ReadableScanReportWriter.BuildSections(report, 2, pathBase, verbosity);

    private static ReadableBlock.Table TableAfterHeading(IReadOnlyList<ReadableBlock> blocks, string headingContains)
    {
        var headingIndex = blocks.ToList().FindIndex(b => b is ReadableBlock.Heading h && h.Text.Contains(headingContains, StringComparison.Ordinal));
        Assert.True(headingIndex >= 0, $"No heading containing '{headingContains}' was found.");

        for (var i = headingIndex + 1; i < blocks.Count; i++)
        {
            if (blocks[i] is ReadableBlock.Table table)
            {
                return table;
            }

            if (blocks[i] is ReadableBlock.Heading)
            {
                break;
            }
        }

        throw new InvalidOperationException($"No table found after heading containing '{headingContains}'.");
    }

    private static List<ReadableBlock.Table> TablesUnderHeading(IReadOnlyList<ReadableBlock> blocks, string headingContains)
    {
        var list = blocks.ToList();
        var headingIndex = list.FindIndex(b => b is ReadableBlock.Heading h && h.Text.Contains(headingContains, StringComparison.Ordinal));
        Assert.True(headingIndex >= 0, $"No heading containing '{headingContains}' was found.");

        var headingLevel = ((ReadableBlock.Heading)list[headingIndex]).Level;
        var tables = new List<ReadableBlock.Table>();
        for (var i = headingIndex + 1; i < list.Count; i++)
        {
            if (list[i] is ReadableBlock.Heading h && h.Level <= headingLevel)
            {
                break;
            }

            if (list[i] is ReadableBlock.Table table)
            {
                tables.Add(table);
            }
        }

        return tables;
    }

    private static bool HasBlockAfterHeadingBeforeNext(IReadOnlyList<ReadableBlock> blocks, string headingContains, Func<ReadableBlock, bool> predicate)
    {
        var list = blocks.ToList();
        var headingIndex = list.FindIndex(b => b is ReadableBlock.Heading h && h.Text.Contains(headingContains, StringComparison.Ordinal));
        Assert.True(headingIndex >= 0, $"No heading containing '{headingContains}' was found.");

        for (var i = headingIndex + 1; i < list.Count; i++)
        {
            if (list[i] is ReadableBlock.Heading)
            {
                break;
            }

            if (predicate(list[i]))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void EmptyReport_OnlyEmitsTitleAndEmptySummary()
    {
        var document = ReadableScanReportWriter.BuildDocument(Blank(), "t");

        Assert.Collection(
            document.Blocks,
            b => Assert.Equal(new ReadableBlock.Heading(1, "t"), b),
            b => Assert.Equal(new ReadableBlock.Heading(2, "Summary"), b),
            b => Assert.IsType<ReadableBlock.Paragraph>(b),
            b => Assert.Equal(new ReadableBlock.Paragraph("No findings."), b),
            b => Assert.IsType<ReadableBlock.Paragraph>(b));
    }

    [Fact]
    public void Summary_ParseSuccessRate_RoundsToOneDecimalPlace()
    {
        var report = Blank() with
        {
            ParseHealth = new ParseHealthReport(
            [
                new FileParseHealth("a.sql", [], 1),
                new FileParseHealth("b.sql", [], 1),
                new FileParseHealth("c.sql", [new ParseErrorInfo(1, 1, 102, "bad")], 0),
            ]),
        };

        var blocks = BuildBlocks(report);
        var paragraph = blocks.OfType<ReadableBlock.Paragraph>().First(p => p.Text.Contains("scanned", StringComparison.Ordinal));

        Assert.Equal("3 files scanned, 2 parsed cleanly (66.7%).", paragraph.Text);
    }

    [Fact]
    public void Summary_SingularFileAndComparisonNouns_OmitTrailingS()
    {
        var report = Blank() with
        {
            ParseHealth = new ParseHealthReport([new FileParseHealth("a.sql", [], 1)]),
            TypedPredicateSummary = new TypedPredicateSummary(
                TotalClassified: 1, SeekPreservedCount: 1, RangeSeekCount: 0, ScanForcedCount: 0,
                UnknownCount: 0, OperandClashCount: 0, DistinctRangeSeekCount: 0, DistinctScanForcedCount: 0,
                DistinctTotalClassified: 1),
        };

        var blocks = BuildBlocks(report);
        var paragraphs = blocks.OfType<ReadableBlock.Paragraph>().ToList();

        Assert.Contains(paragraphs, p => p.Text == "1 file scanned, 1 parsed cleanly (100.0%).");
        Assert.Contains(
            paragraphs,
            p => p.Text == "Base rate: 1 column comparison classified (1 distinct), of which 1 keep their seek. " +
                "Seek-preserving comparisons are counted but not listed - there is nothing to act on.");
    }

    [Fact]
    public void Summary_OnlyOneNonZeroFindingKind_TableHasExactlyOneRowWithDashDistinct()
    {
        var report = Blank() with
        {
            DanglingObjectReferenceFindings =
            [
                new DanglingObjectReferenceFinding("dbo.usp_X", "procedure", "GoneTable", null, "a.sql", 5, 1),
            ],
        };

        var table = TableAfterHeading(BuildBlocks(report), "Summary");

        var row = Assert.Single(table.Rows);
        Assert.Equal("Reference to a nonexistent object", row[0]);
        Assert.Equal("1", row[1]);
        Assert.Equal("-", row[2]);
    }

    [Fact]
    public void Summary_DistinctCountsAreShownSeparatelyFromOccurrenceCounts()
    {
        var report = Blank() with
        {
            TypedPredicateSummary = new TypedPredicateSummary(
                TotalClassified: 3, SeekPreservedCount: 0, RangeSeekCount: 0, ScanForcedCount: 3,
                UnknownCount: 0, OperandClashCount: 0, DistinctRangeSeekCount: 0, DistinctScanForcedCount: 2,
                DistinctTotalClassified: 2),
        };

        var table = TableAfterHeading(BuildBlocks(report), "Summary");

        var row = Assert.Single(table.Rows, r => r[0] == "Implicit conversions forcing a scan");
        Assert.Equal("3", row[1]);
        Assert.Equal("2", row[2]);
    }

    [Fact]
    public void TypedSection_Brief_OmitsTableAndShowsPointerWithCount()
    {
        var column = new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.Int), Indexed: true, Depth: 0, Provenance: null!);
        var findings = new[]
        {
            new TypedPredicateFinding(Verdict.Unknown, column, new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)), "=", "a.sql", 1, 1),
            new TypedPredicateFinding(Verdict.Unknown, column, new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)), "=", "a.sql", 2, 1),
        };
        var report = Blank() with { TypedFindings = findings };

        var blocks = BuildBlocks(report, verbosity: ReadableVerbosity.Brief);

        Assert.False(HasBlockAfterHeadingBeforeNext(blocks, "Comparisons that could not be classified", b => b is ReadableBlock.Table));
        Assert.True(HasBlockAfterHeadingBeforeNext(
            blocks,
            "Comparisons that could not be classified",
            b => b is ReadableBlock.Paragraph p && p.Text == "2 comparisons - not listed individually here; re-run with --verbosity full to see each one."));
    }

    [Fact]
    public void TypedSection_ScanForcedIgnoresBriefVerbosity_AlwaysRendersFullTable()
    {
        var column = new PredicateOperand.Column("dbo.T", "Col", new SqlType(SqlTypeCategory.Int), Indexed: true, Depth: 0, Provenance: null!);
        var findings = new[]
        {
            new TypedPredicateFinding(Verdict.ScanForced, column, new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int)), "=", "a.sql", 1, 1),
        };
        var report = Blank() with { TypedFindings = findings };

        var blocks = BuildBlocks(report, verbosity: ReadableVerbosity.Brief);

        var table = TableAfterHeading(blocks, "Implicit conversions that force a scan");
        Assert.Equal("dbo.T.Col", Assert.Single(table.Rows)[1]);
    }

    [Fact]
    public void TypedRow_IndexedTriStateAndUnknownReason_AreRenderedDistinctly()
    {
        var indexedWithName = new PredicateOperand.Column("dbo.T", "A", new SqlType(SqlTypeCategory.Int), Indexed: true, Depth: 0, Provenance: null!, IndexName: "IX_A");
        var notIndexed = new PredicateOperand.Column("dbo.T", "B", new SqlType(SqlTypeCategory.Int), Indexed: false, Depth: 0, Provenance: null!);
        var unresolved = new PredicateOperand.Column("dbo.T", "C", new SqlType(SqlTypeCategory.Int), Indexed: null, Depth: 0, Provenance: null!);
        var literalValue = new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int), IsLiteral: true, LiteralText: "5");
        var plainValue = new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int));

        var findings = new[]
        {
            new TypedPredicateFinding(Verdict.Unknown, indexedWithName, literalValue, "=", "a.sql", 1, 1, UnknownReason: "collation missing"),
            new TypedPredicateFinding(Verdict.Unknown, notIndexed, plainValue, "=", "a.sql", 2, 1),
            new TypedPredicateFinding(Verdict.Unknown, unresolved, plainValue, "=", "a.sql", 3, 1),
        };
        var report = Blank() with { TypedFindings = findings };

        var table = TableAfterHeading(BuildBlocks(report), "Comparisons that could not be classified");

        Assert.Equal(3, table.Rows.Count);
        var byColumn = table.Rows.ToDictionary(r => r[1]);
        Assert.Equal("yes (IX_A)", byColumn["dbo.T.A"][4]);
        Assert.Equal("no", byColumn["dbo.T.B"][4]);
        Assert.Equal("unresolved", byColumn["dbo.T.C"][4]);
        Assert.Equal("= 5 (Int) (collation missing)", byColumn["dbo.T.A"][3]);
    }

    [Fact]
    public void DescribeOperand_NonLiteralValue_ShowsOnlyTypeNoLiteralText()
    {
        var column = new PredicateOperand.Column("dbo.T", "A", new SqlType(SqlTypeCategory.Int), Indexed: false, Depth: 0, Provenance: null!);
        var variableOperand = new PredicateOperand.Value(new SqlType(SqlTypeCategory.Int), IsLiteral: false, VariableName: "@p");
        var findings = new[]
        {
            new TypedPredicateFinding(Verdict.Unknown, column, variableOperand, "=", "a.sql", 1, 1),
        };
        var report = Blank() with { TypedFindings = findings };

        var table = TableAfterHeading(BuildBlocks(report), "Comparisons that could not be classified");

        Assert.Equal("= Int", Assert.Single(table.Rows)[3]);
    }

    [Fact]
    public void Where_HighConfidenceNoCallSite_HasNoAnnotation()
    {
        var finding = new WriteLossFinding(
            "dbo.T", "Col", WriteLossKind.NumericScaleNarrowing,
            new SqlType(SqlTypeCategory.Decimal), new SqlType(SqlTypeCategory.Decimal),
            "a.sql", 10, 1);
        var report = Blank() with { WriteLossFindings = [finding] };

        var table = TableAfterHeading(BuildBlocks(report), "Assignments risking silent data loss");

        Assert.Equal("a.sql:10", Assert.Single(table.Rows)[0]);
    }

    [Fact]
    public void Where_LowConfidence_AppendsConfidenceMarker()
    {
        var finding = new WriteLossFinding(
            "dbo.T", "Col", WriteLossKind.NumericScaleNarrowing,
            new SqlType(SqlTypeCategory.Decimal), new SqlType(SqlTypeCategory.Decimal),
            "a.sql", 10, 1, Confidence: FindingConfidence.Low);
        var report = Blank() with { WriteLossFindings = [finding] };

        var table = TableAfterHeading(BuildBlocks(report), "Assignments risking silent data loss");

        Assert.Equal("a.sql:10 [LOW CONFIDENCE]", Assert.Single(table.Rows)[0]);
    }

    [Fact]
    public void Where_DynamicSqlCallSiteDifferentFromFindingLocation_AnnotatesRunSite()
    {
        var throughDynamicSql = new WriteLossFinding(
            "dbo.T", "Col", WriteLossKind.NumericScaleNarrowing,
            new SqlType(SqlTypeCategory.Decimal), new SqlType(SqlTypeCategory.Decimal),
            "inner.sql", 3, 1, DynamicSqlCallSite: new SourceSpan("caller.sql", 40, 1));
        var report = Blank() with { WriteLossFindings = [throughDynamicSql] };

        var table = TableAfterHeading(BuildBlocks(report), "Assignments risking silent data loss");

        Assert.Equal("inner.sql:3 (in dynamic SQL run at caller.sql:40)", Assert.Single(table.Rows)[0]);
    }

    [Fact]
    public void Tier1_GroupsByKindAndListsIndexedRowsFirstWithinEachGroup()
    {
        var findings = new[]
        {
            new SargabilityFinding(SargabilityFindingKind.FunctionWrappedColumn, "Late", null, "z.sql", 1, 1, TableQualifiedName: "dbo.T", Indexed: false),
            new SargabilityFinding(SargabilityFindingKind.FunctionWrappedColumn, "Early", null, "a.sql", 1, 1, TableQualifiedName: "dbo.T", Indexed: true),
            new SargabilityFinding(SargabilityFindingKind.CastOrConvertOnColumn, "Cast1", null, "a.sql", 1, 1, TableQualifiedName: "dbo.T", Indexed: false),
        };
        var report = Blank() with { Tier1Findings = findings };

        var blocks = BuildBlocks(report);

        var subHeadings = blocks.OfType<ReadableBlock.Heading>().Where(h => h.Level == 3).Select(h => h.Text).ToList();
        Assert.Equal("Column wrapped in a function (2)", subHeadings[0]);
        Assert.Equal("CAST/CONVERT applied to the column (1)", subHeadings[1]);

        var functionWrappedTable = TablesUnderHeading(blocks, "Non-sargable predicate patterns")[0];
        Assert.Equal("Early", functionWrappedTable.Rows[0][1].Split('.').Last());
        Assert.Equal("Late", functionWrappedTable.Rows[1][1].Split('.').Last());
    }

    [Fact]
    public void CatchAllPredicate_IndexedRowsListedBeforeUnindexedRegardlessOfPathOrder()
    {
        var findings = new[]
        {
            new CatchAllPredicateFinding("dbo.T", "Unindexed", false, "@p1", "a.sql", 1, 1),
            new CatchAllPredicateFinding("dbo.T", "Indexed", true, "@p2", "z.sql", 1, 1),
        };
        var report = Blank() with { CatchAllPredicateFindings = findings };

        var table = TableAfterHeading(BuildBlocks(report), "Catch-all / kitchen-sink predicates");

        Assert.Equal("dbo.T.Indexed", table.Rows[0][1]);
        Assert.Equal("dbo.T.Unindexed", table.Rows[1][1]);
    }

    [Fact]
    public void LocalVariablePredicate_IndexedTriState_MapsToYesNoUnresolved()
    {
        var findings = new[]
        {
            new LocalVariablePredicateFinding("dbo.T", "A", true, 0, "@v1", "=", "a.sql", 1, 1),
            new LocalVariablePredicateFinding("dbo.T", "B", false, 0, "@v2", "=", "a.sql", 2, 1),
            new LocalVariablePredicateFinding("dbo.T", "C", null, 0, "@v3", "=", "a.sql", 3, 1),
        };
        var report = Blank() with { LocalVariablePredicateFindings = findings };

        var table = TableAfterHeading(BuildBlocks(report), "Predicates against a local variable");
        var byColumn = table.Rows.ToDictionary(r => r[1]);

        Assert.Equal("yes", byColumn["dbo.T.A"][4]);
        Assert.Equal("no", byColumn["dbo.T.B"][4]);
        Assert.Equal("unresolved", byColumn["dbo.T.C"][4]);
    }

    [Fact]
    public void UnderLengthParameter_ImplicitDefaultAndPatternShapeEffect_AreDescribedDistinctly()
    {
        var findings = new[]
        {
            new UnderLengthParameterFinding("dbo.T", "ImplicitDefault", 10, null, true, "=", false, "a.sql", 1, 1),
            new UnderLengthParameterFinding("dbo.T", "PatternShape", 10, 3, false, "LIKE", true, "a.sql", 2, 1),
            new UnderLengthParameterFinding("dbo.T", "PlainTruncation", 10, 3, false, "=", false, "a.sql", 3, 1),
        };
        var report = Blank() with { UnderLengthParameterFindings = findings };

        var table = TableAfterHeading(BuildBlocks(report), "under-length parameter");
        var byColumn = table.Rows.ToDictionary(r => r[1]);

        Assert.Equal("none (defaults to 1)", byColumn["dbo.T.ImplicitDefault"][3]);
        Assert.Equal("changes pattern/range shape", byColumn["dbo.T.PatternShape"][5]);
        Assert.Equal("truncates compared value", byColumn["dbo.T.PlainTruncation"][5]);
    }

    [Fact]
    public void MaxTypedColumn_SplitsMaxLengthAndLegacyLargeObjectIntoSeparateSections()
    {
        var findings = new[]
        {
            new MaxTypedColumnFinding("dbo.T", "BigVarchar", "VarChar(max)", "a.sql", 1, NonIndexableColumnFindingKind.MaxLength),
            new MaxTypedColumnFinding("dbo.T", "OldText", "Text", "a.sql", 2, NonIndexableColumnFindingKind.LegacyLargeObject),
        };
        var report = Blank() with { MaxTypedColumnFindings = findings };

        var blocks = BuildBlocks(report);
        var maxLengthTable = TableAfterHeading(blocks, "MAX-typed columns");
        var legacyTable = TableAfterHeading(blocks, "Legacy large-object columns");

        Assert.Equal("dbo.T.BigVarchar", Assert.Single(maxLengthTable.Rows)[1]);
        Assert.Equal("dbo.T.OldText", Assert.Single(legacyTable.Rows)[1]);
    }

    [Fact]
    public void TvfFence_CorrelatedApplyDetail_ShowsOuterColumnsInsteadOfFragmentText()
    {
        var correlated = new TvfFenceFinding(
            TvfFenceFindingKind.CorrelatedApply, null, "dbo.fn_X", null, "a.sql", 1, 1,
            Depth: 1, OriginSourcePath: "origin.sql", OriginLine: 9,
            CorrelatedOuterColumns: ["a", "b"], ReferenceFragmentText: "ignored fragment");
        var report = Blank() with { TvfFenceFindings = [correlated] };

        var table = TableAfterHeading(BuildBlocks(report), "Correlated CROSS/OUTER APPLY");

        var row = Assert.Single(table.Rows);
        Assert.Equal("correlated on a, b", row[5]);
        Assert.Equal("origin.sql:9", row[4]);
    }

    [Fact]
    public void TvfFence_NonCorrelatedKind_FallsBackToFragmentTextAndDashOrigin()
    {
        var standalone = new TvfFenceFinding(
            TvfFenceFindingKind.Standalone, null, "dbo.fn_Y", null, "a.sql", 1, 1,
            ReferenceFragmentText: "SELECT * FROM dbo.fn_Y()");
        var report = Blank() with { TvfFenceFindings = [standalone] };

        var table = TableAfterHeading(BuildBlocks(report), "Standalone reference");

        var row = Assert.Single(table.Rows);
        Assert.Equal("SELECT * FROM dbo.fn_Y()", row[5]);
        Assert.Equal("-", row[4]);
    }

    [Fact]
    public void ScalarUdfDetail_CombinesBlockerFoldingAndClrDataAccessInOrder()
    {
        var finding = new ScalarUdfFinding(
            ScalarUdfFindingKind.PredicateInvocation, "dbo.fn_Compute", "dbo.T", ScalarUdfKind.Clr,
            ScalarUdfInlineability.NotInlineable, "uses TRY_CATCH", false,
            true, true, ScalarUdfContext.Where, null,
            "a.sql", 1, 1);
        var report = Blank() with { ScalarUdfFindings = [finding] };

        var table = TableAfterHeading(BuildBlocks(report), "Called in a predicate");

        var row = Assert.Single(table.Rows);
        Assert.Equal("uses TRY_CATCH; non-schemabound, literal arguments not constant-folded; CLR, data access", row[6]);
        Assert.Equal("no", row[3]);
    }

    [Fact]
    public void ScalarUdfDetail_NoPartsFallsBackToReferenceFragmentText()
    {
        var finding = new ScalarUdfFinding(
            ScalarUdfFindingKind.ProjectionInvocation, "dbo.fn_Plain", "dbo.T", ScalarUdfKind.TSql,
            ScalarUdfInlineability.Inlineable, null, true,
            false, null, ScalarUdfContext.SelectList, null,
            "a.sql", 1, 1, ReferenceFragmentText: "dbo.fn_Plain(Col)");
        var report = Blank() with { ScalarUdfFindings = [finding] };

        var table = TableAfterHeading(BuildBlocks(report), "Called outside a predicate");

        var row = Assert.Single(table.Rows);
        Assert.Equal("dbo.fn_Plain(Col)", row[6]);
        Assert.Equal("yes (2019+ FROID)", row[3]);
    }

    [Fact]
    public void DescribeWriteLossKind_MapsEachKindToItsOwnRiskText()
    {
        var findings = new[]
        {
            new WriteLossFinding("dbo.T", "A", WriteLossKind.UnicodeToNonUnicodeReplacement, new SqlType(SqlTypeCategory.VarChar), new SqlType(SqlTypeCategory.NVarChar), "a.sql", 1, 1),
            new WriteLossFinding("dbo.T", "B", WriteLossKind.ApproximateToExactTruncation, new SqlType(SqlTypeCategory.Int), new SqlType(SqlTypeCategory.Float), "a.sql", 2, 1),
            new WriteLossFinding("dbo.T", "C", WriteLossKind.NumericScaleNarrowing, new SqlType(SqlTypeCategory.Decimal), new SqlType(SqlTypeCategory.Decimal), "a.sql", 3, 1),
            new WriteLossFinding("dbo.T", "D", WriteLossKind.TemporalPrecisionLoss, new SqlType(SqlTypeCategory.Date), new SqlType(SqlTypeCategory.DateTime2), "a.sql", 4, 1),
        };
        var report = Blank() with { WriteLossFindings = findings };

        var table = TableAfterHeading(BuildBlocks(report), "Assignments risking silent data loss");
        var byColumn = table.Rows.ToDictionary(r => r[1]);

        Assert.Equal("Unicode characters outside the target's codepage become '?'", byColumn["dbo.T.A"][4]);
        Assert.Equal("fractional part silently dropped", byColumn["dbo.T.B"][4]);
        Assert.Equal("digits past the target's scale silently rounded away", byColumn["dbo.T.C"][4]);
        Assert.Equal("time-of-day silently dropped", byColumn["dbo.T.D"][4]);
    }

    [Fact]
    public void IndexDesign_TableLevelKind_ShowsPlaceholderWhileOtherKindShowsUnnamed()
    {
        var findings = new[]
        {
            new IndexDesignFinding(IndexDesignFindingKind.UnindexedForeignKey, "dbo.T", null, "detail", "a.sql", 1),
            new IndexDesignFinding(IndexDesignFindingKind.DuplicateIndex, "dbo.T", null, "detail", "a.sql", 2),
        };
        var report = Blank() with { IndexDesignFindings = findings };

        var table = TableAfterHeading(BuildBlocks(report), "Physical/schema index design");

        var byKind = table.Rows.ToDictionary(r => r[1]);
        Assert.Equal("(table-level)", byKind["UnindexedForeignKey"][2]);
        Assert.Equal("<unnamed>", byKind["DuplicateIndex"][2]);
    }

    [Fact]
    public void DatabaseConfigurationFlagLabel_SpatialKind_InterpolatesAffectedObjectAndTargetLevel()
    {
        var finding = new DatabaseConfigurationFinding(
            DatabaseConfigurationFindingKind.SpatialPersistedComputedColumnDisabledOnCompatibilityLevelChange,
            "TestDb", AffectedObjectName: "dbo.T.GeoCol", Dependency: "geography", TargetCompatibilityLevel: 160);
        var report = Blank() with { DatabaseConfigurationFindings = [finding] };

        var table = TableAfterHeading(BuildBlocks(report), "Database-level configuration flags");

        Assert.Equal("dbo.T.GeoCol disabled at compatibility level 160 (geography)", Assert.Single(table.Rows)[1]);
    }

    [Fact]
    public void SkippedConstructs_GroupedByKindAndPass_OrderedByCountDescending()
    {
        var constructs = new[]
        {
            new SkippedConstruct(AnalysisPass.Catalog, "a.sql", 1, 1, "KindA", "reason"),
            new SkippedConstruct(AnalysisPass.Catalog, "a.sql", 2, 1, "KindA", "reason"),
            new SkippedConstruct(AnalysisPass.Lineage, "a.sql", 3, 1, "KindB", "reason"),
        };
        var report = Blank() with { SkippedConstructs = constructs, SkippedConstructSummary = SkippedConstructSummary.From(constructs) };

        var table = TableAfterHeading(BuildBlocks(report), "Constructs skipped as out of scope");

        Assert.Equal(["KindA", "catalog", "2"], table.Rows[0]);
        Assert.Equal(["KindB", "lineage", "1"], table.Rows[1]);
    }

    [Fact]
    public void DynamicSql_OnlyNonAnalyzedOutcomesAreCountedAndListed()
    {
        var findings = new[]
        {
            new DynamicSqlFinding("a.sql", 1, 1, DynamicSqlOutcome.AnalyzedLiteral, null),
            new DynamicSqlFinding("a.sql", 2, 1, DynamicSqlOutcome.Unanalyzable, "runtime value"),
            new DynamicSqlFinding("a.sql", 3, 1, DynamicSqlOutcome.PartiallyAnalyzed, null),
            new DynamicSqlFinding("a.sql", 4, 1, DynamicSqlOutcome.InnerParseFailed, null),
        };
        var report = Blank() with { DynamicSqlFindings = findings, DynamicSqlSummary = DynamicSqlSummary.From(findings) };

        var blocks = BuildBlocks(report);
        var heading = blocks.OfType<ReadableBlock.Heading>().First(h => h.Text.Contains("Dynamic SQL not fully analyzed", StringComparison.Ordinal));
        Assert.Equal("Dynamic SQL not fully analyzed (3)", heading.Text);

        var table = TableAfterHeading(blocks, "Dynamic SQL not fully analyzed");
        Assert.Equal(3, table.Rows.Count);
        Assert.DoesNotContain(table.Rows, r => r[0] == "a.sql:1");

        var byLine = table.Rows.ToDictionary(r => r[0]);
        Assert.Equal("not provably constant", byLine["a.sql:2"][1]);
        Assert.Equal("partially analyzed - an unresolvable fragment was elided", byLine["a.sql:3"][1]);
        Assert.Equal("constant, but did not parse as T-SQL", byLine["a.sql:4"][1]);
    }

    [Fact]
    public void DynamicSql_Brief_OmitsTableShowsPointer()
    {
        var findings = new[] { new DynamicSqlFinding("a.sql", 2, 1, DynamicSqlOutcome.Unanalyzable, "runtime value") };
        var report = Blank() with { DynamicSqlFindings = findings, DynamicSqlSummary = DynamicSqlSummary.From(findings) };

        var blocks = BuildBlocks(report, verbosity: ReadableVerbosity.Brief);

        Assert.False(HasBlockAfterHeadingBeforeNext(blocks, "Dynamic SQL not fully analyzed", b => b is ReadableBlock.Table));
        Assert.True(HasBlockAfterHeadingBeforeNext(
            blocks,
            "Dynamic SQL not fully analyzed",
            b => b is ReadableBlock.Paragraph p && p.Text.StartsWith("1 call site - not listed individually", StringComparison.Ordinal)));
    }
}
