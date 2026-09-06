using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting.Sarif;

public static class SarifReportWriter
{
    private const string ToolName = "SilentScan";

    private static readonly string ToolVersion =
        typeof(SarifReportWriter).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private const string LevelError = "error";
    private const string LevelWarning = "warning";
    private const string LevelNote = "note";
    private const string TopLevelBatchCallerLabel = "a top-level batch";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(ScanReport report)
    {
        var results = new List<SarifResult>();
        results.AddRange(report.Find<SargabilityFinding>("NonSargablePredicateScanner").Select(ToResult));
        results.AddRange(report.Find<TypedPredicateFinding>(nameof(TypedPredicateExtractor)).Select(ToResult));
        results.AddRange(report.Find<DynamicSqlFinding>("DynamicSqlScanner").Select(ToResult));
        results.AddRange(report.Find<ExpressionDerivedFinding>(nameof(TypedPredicateExtractor)).Select(ToResult));
        results.AddRange(report.Find<CollationConflictFinding>(nameof(TypedPredicateExtractor)).Select(ToResult));
        results.AddRange(report.Find<WriteLossFinding>(nameof(TypedPredicateExtractor)).Select(ToResult));
        results.AddRange(report.Find<TvfFenceFinding>("TvfFenceScanner").Select(ToResult));
        results.AddRange(report.Find<ScalarUdfFinding>("ScalarUdfScanner").Select(ToResult));
        results.AddRange(report.Find<ColumnCollationDriftFinding>("ColumnCollationDriftScanner").Select(ToResult));
        results.AddRange(report.Find<AnsiPaddingOffColumnFinding>("AnsiPaddingOffColumnScanner").Select(ToResult));
        results.AddRange(report.Find<CrossTableTypeDriftFinding>("CrossTableTypeDriftScanner").Select(ToResult));
        results.AddRange(report.Find<ProcCallArgumentMismatchFinding>("ProcCallArgumentMismatchScanner").Select(ToResult));
        results.AddRange(report.Find<TvfCallArgumentMismatchFinding>("TvfCallArgumentMismatchScanner").Select(ToResult));
        results.AddRange(report.Find<ProcCallTableValuedArgumentMismatchFinding>("ProcCallTableValuedArgumentMismatchScanner").Select(ToResult));
        results.AddRange(report.Find<SpExecuteSqlParameterMismatchFinding>("SpExecuteSqlParameterMismatchScanner").Select(ToResult));
        results.AddRange(report.Find<TemporalBoundaryPrecisionFinding>("NonSargablePredicateScanner").Select(ToResult));
        results.AddRange(report.Find<JsonIndexRewriteFinding>("NonSargablePredicateScanner").Select(ToResult));
        results.AddRange(report.Find<MaxTypedColumnFinding>("MaxTypedColumnScanner").Select(ToResult));
        results.AddRange(report.Find<ColumnstoreUnsupportedColumnTypeFinding>("ColumnstoreUnsupportedColumnTypeScanner").Select(ToResult));
        results.AddRange(report.Find<ExternalTableUnsupportedColumnTypeFinding>(nameof(ExternalTableUnsupportedColumnTypeScanner)).Select(ToResult));
        results.AddRange(report.Find<VectorLiteralConversionFinding>(nameof(VectorLiteralConversionScanner)).Select(ToResult));
        results.AddRange(report.Find<FullTextPredicateInAggregateFinding>(nameof(FullTextPredicateInAggregateScanner)).Select(ToResult));
        results.AddRange(report.Find<ChangeTrackingEncryptedPrimaryKeyFinding>(nameof(ChangeTrackingEncryptedPrimaryKeyScanner)).Select(ToResult));
        results.AddRange(report.Find<XmlSchemaCollectionDisallowedTypeFinding>(nameof(XmlSchemaCollectionDisallowedTypeScanner)).Select(ToResult));
        results.AddRange(report.Find<XmlSchemaCollectionMismatchFinding>(nameof(XmlSchemaCollectionMismatchScanner)).Select(ToResult));
        results.AddRange(report.Find<SelectiveXmlIndexValueColumnFinding>("SelectiveXmlIndexValueColumnScanner").Select(ToResult));
        results.AddRange(report.Find<OversizedParameterFinding>(nameof(TypedPredicateExtractor)).Select(ToResult));
        results.AddRange(report.Find<UnderLengthParameterFinding>(nameof(TypedPredicateExtractor)).Select(ToResult));
        results.AddRange(report.Find<AnsiPaddingMismatchFinding>(nameof(TypedPredicateExtractor)).Select(ToResult));
        results.AddRange(report.Find<CatchAllPredicateFinding>("CatchAllPredicateScanner").Select(ToResult));
        results.AddRange(report.Find<LocalVariablePredicateFinding>(nameof(TypedPredicateExtractor)).Select(ToResult));
        results.AddRange(report.Find<FilteredIndexParameterMismatchFinding>(nameof(TypedPredicateExtractor)).Select(ToResult));
        results.AddRange(report.Find<NotInNullableSubqueryFinding>("NotInNullableSubqueryScanner").Select(ToResult));
        results.AddRange(report.Find<NonUniqueUpdateSourceFinding>("NonUniqueUpdateSourceScanner").Select(ToResult));
        results.AddRange(report.Find<ForcedSerialFinding>("ForcedSerialScanner").Select(ToResult));
        results.AddRange(report.Find<UntrustedConstraintFinding>("UntrustedConstraintScanner").Select(ToResult));
        results.AddRange(report.Find<CascadingForeignKeyFinding>("CascadingForeignKeyScanner").Select(ToResult));
        results.AddRange(report.Find<MultiReferencedCteFinding>("MultiReferencedCteScanner").Select(ToResult));
        results.AddRange(report.Find<RecursiveCteAnchorTypeMismatchFinding>(nameof(RecursiveCteAnchorTypeMismatchScanner)).Select(ToResult));
        results.AddRange(report.Find<NestedViewDepthFinding>("NestedViewDepthScanner").Select(ToResult));
        results.AddRange(report.Find<PostExpansionJoinWidthFinding>("PostExpansionJoinWidthScanner").Select(ToResult));
        results.AddRange(report.Find<SelectStarViewFinding>("SelectStarViewScanner").Select(ToResult));
        results.AddRange(report.Find<UnparameterizedDynamicSqlFinding>("DynamicSqlScanner").Select(ToResult));
        results.AddRange(report.Find<NonPersistedComputedColumnFinding>("NonPersistedComputedColumnScanner").Select(ToResult));
        results.AddRange(report.Find<TempTableExecShapeFinding>("TempTableExecShapeScanner").Select(ToResult));
        results.AddRange(report.Find<ExecResultSetsShapeFinding>("ExecResultSetsShapeScanner").Select(ToResult));
        results.AddRange(report.Find<SelfReferencingDmlFinding>("SelfReferencingDmlScanner").Select(ToResult));
        results.AddRange(report.Find<PartialCompositeForeignKeyJoinFinding>("PartialCompositeForeignKeyJoinScanner").Select(ToResult));
        results.AddRange(report.Find<OuterJoinPredicateCollapseFinding>(nameof(OuterJoinPredicateCollapseScanner)).Select(ToResult));
        results.AddRange(report.Find<SetOptionFinding>("SetOptionScanner").Select(ToResult));
        results.AddRange(report.Find<TemporalTableHistoryIndexGapFinding>("TemporalTableHistoryIndexGapScanner").Select(ToResult));
        results.AddRange(report.Find<ModuleCompileFlagFinding>("ModuleCompileFlagScanner").Select(ToResult));
        results.AddRange(report.Find<WindowFrameFinding>("WindowFrameScanner").Select(ToResult));
        results.AddRange(report.Find<WindowFunctionArgumentFinding>("WindowFunctionArgumentScanner").Select(ToResult));
        results.AddRange(report.Find<StringSplitArgumentFinding>("StringSplitArgumentScanner").Select(ToResult));
        results.AddRange(report.Find<BoundedStringBuiltinTruncationFinding>("BoundedStringBuiltinTruncationScanner").Select(ToResult));
        results.AddRange(report.Find<BackupOptionConflictFinding>("BackupOptionConflictScanner").Select(ToResult));
        results.AddRange(report.Find<RestoreOptionConflictFinding>("RestoreOptionConflictScanner").Select(ToResult));
        results.AddRange(report.Find<ViewCheckOptionContradictionFinding>("ViewCheckOptionContradictionScanner").Select(ToResult));
        results.AddRange(report.Find<CreateDatabaseOptionConflictFinding>("CreateDatabaseOptionConflictScanner").Select(ToResult));
        results.AddRange(report.Find<GraphPseudoColumnAssignmentFinding>("GraphPseudoColumnAssignmentScanner").Select(ToResult));
        results.AddRange(report.Find<LegacyLobUtf8CollationFinding>("LegacyLobUtf8CollationScanner").Select(ToResult));
        results.AddRange(report.Find<LegacyLobConversionTargetFinding>("LegacyLobConversionTargetScanner").Select(ToResult));
        results.AddRange(report.Find<GroupByValidityFinding>("GroupByValidityScanner").Select(ToResult));
        results.AddRange(report.Find<WaitForFinding>("WaitForScanner").Select(ToResult));
        results.AddRange(report.Find<CursorCloseOnCommitFinding>("CursorCloseOnCommitScanner").Select(ToResult));
        results.AddRange(report.Find<ViewOrderingFinding>("ViewOrderingScanner").Select(ToResult));
        results.AddRange(report.Find<TransactionHygieneFinding>("TransactionHygieneScanner").Select(ToResult));
        results.AddRange(report.Find<CompositeIndexLeadingColumnFinding>("CompositeIndexLeadingColumnScanner").Select(ToResult));
        results.AddRange(report.Find<IndexHintFinding>("IndexHintScanner").Select(ToResult));
        results.AddRange(report.Find<SessionDateSettingFinding>("SessionDateSettingScanner").Select(ToResult));
        results.AddRange(report.Find<AmbiguousDateLiteralConversionFinding>("AmbiguousDateLiteralConversionScanner").Select(ToResult));
        results.AddRange(report.Find<CartesianJoinFinding>("CartesianJoinScanner").Select(ToResult));
        results.AddRange(report.Find<TruncateSwallowedFinding>("TruncateSwallowedScanner").Select(ToResult));
        results.AddRange(report.Find<UnindexedTempTableUsageFinding>("UnindexedTempTableUsageScanner").Select(ToResult));
        results.AddRange(report.Find<OutputParameterFinding>("OutputParameterScanner").Select(ToResult));
        results.AddRange(report.Find<DatabaseConfigurationFinding>("DatabaseConfigurationScanner").Select(ToResult));
        results.AddRange(report.Find<ParameterReassignmentPredicateFinding>("ParameterReassignmentPredicateScanner").Select(ToResult));
        results.AddRange(report.Find<CodeMetricFinding>("CodeMetricScanner").Select(ToResult));
        results.AddRange(report.Find<FormattingFinding>("FormattingScanner").Select(ToResult));
        results.AddRange(report.Find<NamingFinding>("NamingScanner").Select(ToResult));
        results.AddRange(report.Find<DeadCodeFinding>("DeadCodeScanner").Select(ToResult));
        results.AddRange(report.Find<DuplicationFinding>("DuplicationScanner").Select(ToResult));
        results.AddRange(report.Find<DeprecatedSyntaxFinding>("DeprecatedSyntaxScanner").Select(ToResult));
        results.AddRange(report.Find<StatementShapeFinding>("StatementShapeScanner").Select(ToResult));
        results.AddRange(report.Find<ControlFlowRiskFinding>("ControlFlowRiskScanner").Select(ToResult));
        results.AddRange(report.Find<SecurityFinding>("SecurityScanner").Select(ToResult));
        results.AddRange(report.Find<IndexDesignFinding>("IndexDesignScanner").Select(ToResult));
        results.AddRange(report.Find<ForcedParameterizationFinding>("ForcedParameterizationScanner").Select(ToResult));
        results.AddRange(report.Find<IdentityRangeFinding>("IdentityRangeScanner").Select(ToResult));
        results.AddRange(report.Find<FloatEqualityFinding>("FloatEqualityPredicateScanner").Select(ToResult));
        results.AddRange(report.Find<FloatOrderDependentAggregateFinding>("FloatOrderDependentAggregateScanner").Select(ToResult));
        results.AddRange(report.Find<DynamicDataMaskingFinding>(nameof(DynamicDataMaskingScanner)).Select(ToResult));
        results.AddRange(report.Find<AlwaysEncryptedOrderByFinding>("AlwaysEncryptedOrderByScanner").Select(ToResult));
        results.AddRange(report.Find<AlwaysEncryptedAssignmentMismatchFinding>(nameof(AlwaysEncryptedAssignmentMismatchScanner)).Select(ToResult));
        results.AddRange(report.Find<RestrictedImplicitAssignmentFinding>("RestrictedImplicitAssignmentScanner").Select(ToResult));
        results.AddRange(report.Find<RevertCookieTypeMismatchFinding>("RevertCookieTypeMismatchScanner").Select(ToResult));
        results.AddRange(report.Find<ForXmlExplicitInlineXsdFinding>("ForXmlExplicitInlineXsdScanner").Select(ToResult));
        results.AddRange(report.Find<AlwaysEncryptedKeyColumnFinding>("AlwaysEncryptedKeyColumnScanner").Select(ToResult));
        results.AddRange(report.Find<AlwaysEncryptedUnsupportedColumnFinding>("AlwaysEncryptedUnsupportedColumnScanner").Select(ToResult));
        results.AddRange(report.Find<AlterColumnSafetyFinding>("AlterColumnSafetyScanner").Select(ToResult));
        results.AddRange(report.Find<DropProtectedObjectFinding>("DropProtectedObjectScanner").Select(ToResult));
        results.AddRange(report.Find<OnlineRebuildLegacyLobFinding>("OnlineRebuildLegacyLobScanner").Select(ToResult));
        results.AddRange(report.Find<OperandComparabilityFinding>("OperandComparabilityScanner").Select(ToResult));
        results.AddRange(report.Find<VectorFunctionArgumentFinding>(nameof(VectorFunctionArgumentScanner)).Select(ToResult));
        results.AddRange(report.Find<SchemaWithRejectedTypeFinding>(nameof(SchemaWithRejectedTypeScanner)).Select(ToResult));
        results.AddRange(report.Find<ExecuteAtLargeObjectParameterFinding>(nameof(ExecuteAtLargeObjectParameterScanner)).Select(ToResult));
        results.AddRange(report.Find<MemoryOptimizedUnsupportedColumnTypeFinding>("MemoryOptimizedUnsupportedColumnTypeScanner").Select(ToResult));
        results.AddRange(report.Find<MemoryOptimizedUtf8CollationFinding>("MemoryOptimizedUtf8CollationScanner").Select(ToResult));
        results.AddRange(report.Find<NativelyCompiledUnsupportedBuiltinFinding>("NativelyCompiledUnsupportedBuiltinScanner").Select(ToResult));
        results.AddRange(report.Find<NativelyCompiledClrTypeFinding>("NativelyCompiledClrTypeScanner").Select(ToResult));
        results.AddRange(report.Find<NativelyCompiledErrorOutsideCatchFinding>("NativelyCompiledErrorOutsideCatchScanner").Select(ToResult));
        results.AddRange(report.Find<NativelyCompiledInterpretedCalleeFinding>("NativelyCompiledInterpretedCalleeScanner").Select(ToResult));
        results.AddRange(report.Find<MemoryOptimizedLedgerConflictFinding>("MemoryOptimizedLedgerConflictScanner").Select(ToResult));
        results.AddRange(report.Find<MemoryOptimizedUnsupportedIndexOptionFinding>("MemoryOptimizedUnsupportedIndexOptionScanner").Select(ToResult));
        results.AddRange(report.Find<MemoryOptimizedForeignKeyFinding>("MemoryOptimizedForeignKeyScanner").Select(ToResult));
        results.AddRange(report.Find<MemoryOptimizedSchemaOnlyDurabilityFinding>("MemoryOptimizedSchemaOnlyDurabilityScanner").Select(ToResult));
        results.AddRange(report.Find<QueryAntiPatternFinding>("QueryAntiPatternScanner").Select(ToResult));
        results.AddRange(report.Find<IndexCoverageFinding>("IndexCoverageScanner").Select(ToResult));
        results.AddRange(report.Find<TriggerCorrectnessFinding>("TriggerCorrectnessScanner").Select(ToResult));
        results.AddRange(report.Find<CrossModuleLockOrderFinding>("CrossModuleLockOrderScanner").Select(ToResult));
        results.AddRange(report.Find<TriggerRecursionCycleFinding>("TriggerRecursionCycleScanner").Select(ToResult));
        results.AddRange(report.Find<CheckConstraintFinding>("CheckConstraintScanner").Select(ToResult));
        results.AddRange(report.Find<DefaultNullableConstraintFinding>("DefaultNullableConstraintScanner").Select(ToResult));
        results.AddRange(report.Find<TryCastComputedColumnPredicateFinding>("TryCastComputedColumnPredicateScanner").Select(ToResult));
        results.AddRange(report.Find<StaleSelectStarViewFinding>("StaleSelectStarViewScanner").Select(ToResult));
        results.AddRange(report.Find<BareTopNoOrderByFinding>("BareTopNoOrderByScanner").Select(ToResult));
        results.AddRange(report.Find<StringConcatNullFinding>("StringConcatNullScanner").Select(ToResult));
        results.AddRange(report.Find<AggregateDivisionColumnstoreFinding>("AggregateDivisionColumnstoreScanner").Select(ToResult));
        results.AddRange(report.Find<SecurityPredicateIndexFinding>("SecurityPredicateIndexScanner").Select(ToResult));
        results.AddRange(report.Find<DanglingObjectReferenceFinding>("DanglingObjectReferenceScanner").Select(ToResult));
        results.AddRange(report.Find<TriggerOrderFinding>("TriggerOrderScanner").Select(ToResult));
        results.AddRange(report.Find<MissingStatisticsFinding>("MissingStatisticsScanner").Select(ToResult));
        results.AddRange(report.Find<FullTextIndexDdlFinding>("FullTextIndexDdlScanner").Select(ToResult));
        results.AddRange(report.Find<SemanticSearchFinding>("SemanticSearchScanner").Select(ToResult));

        var notifications = BuildParseHealthNotifications(report.ParseHealth);
        notifications.AddRange(BuildSkippedConstructNotifications(report.SkippedConstructSummary));
        notifications.AddRange(BuildDynamicSqlNotifications(report.DynamicSqlSummary));
        notifications.AddRange(BuildTypedPredicateNotifications(report.TypedPredicateSummary));

        var hasParseIssues = report.ParseHealth.Files.Any(f => f.Errors.Count > 0 || f.UnanalyzedBatches.Count > 0);
        var executionSuccessful = !hasParseIssues && report.SkippedConstructSummary.TotalCount == 0;
        var invocation = new SarifInvocation(executionSuccessful, notifications);

        var log = new SarifLog(
            "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
            "2.1.0",
            [new SarifRun(new SarifTool(new SarifDriver(ToolName, ToolVersion, InformationUri: RuleDocSite.IndexUrl, SarifRuleCatalog.AllRules)), results, [invocation])]);

        return JsonSerializer.Serialize(log, JsonOptions);
    }

    private static List<SarifNotification> BuildParseHealthNotifications(ParseHealthReport parseHealth)
    {
        var notifications = new List<SarifNotification>();

        foreach (var file in parseHealth.Files)
        {
            foreach (var error in file.Errors)
            {
                notifications.Add(new SarifNotification(
                    new SarifMessage($"Parse error in '{file.Path}': {error.Message}"),
                    LevelWarning,
                    [ToLocation(file.Path, error.Line, error.Column)]));
            }

            foreach (var unanalyzed in file.UnanalyzedBatches)
            {
                var what = unanalyzed.ObjectName is { } name
                    ? $"{DescribeUnanalyzedKind(unanalyzed.Kind)} '{name}'"
                    : "an unidentified object";
                notifications.Add(new SarifNotification(
                    new SarifMessage($"Batch in '{file.Path}' failed to parse and was dropped - {what} received zero analysis."),
                    LevelWarning,
                    [ToLocation(file.Path, unanalyzed.StartLine, startColumn: null)]));
            }
        }

        return notifications;
    }

    private static List<SarifNotification> BuildSkippedConstructNotifications(SkippedConstructSummary summary)
    {
        if (summary.TotalCount == 0)
        {
            return [];
        }

        var breakdown = string.Join(", ", summary.CountsByConstructKind
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}: {kv.Value}"));

        return [new SarifNotification(
            new SarifMessage($"{summary.TotalCount} construct(s) skipped during analysis ({breakdown}) - findings that would have come from these constructs are not represented in this report."),
            LevelWarning)];
    }

    private static List<SarifNotification> BuildDynamicSqlNotifications(DynamicSqlSummary summary)
    {
        var unanalyzedCount = summary.UnanalyzableCount + summary.InnerParseFailedCount + summary.PartiallyAnalyzedCount;
        if (unanalyzedCount == 0)
        {
            return [];
        }

        return [new SarifNotification(
            new SarifMessage($"{unanalyzedCount} of {summary.TotalCallSites} dynamic-SQL call site(s) could not be fully analyzed ({summary.UnanalyzableCount} unanalyzable, {summary.InnerParseFailedCount} inner-parse-failed, {summary.PartiallyAnalyzedCount} partially analyzed) - predicates inside those statements are not represented in this report."),
            LevelWarning)];
    }

    private static List<SarifNotification> BuildTypedPredicateNotifications(TypedPredicateSummary summary)
    {
        if (summary.UnknownCount == 0)
        {
            return [];
        }

        return [new SarifNotification(
            new SarifMessage($"{summary.UnknownCount} of {summary.TotalClassified} classified predicate(s) could not be resolved to a seek/scan verdict - their sargability is unknown, not confirmed clean."),
            LevelNote)];
    }

    private static SarifLocation ToLocation(string sourcePath, int line, int? startColumn) =>
        new(new SarifPhysicalLocation(new SarifArtifactLocation(ToUri(sourcePath)), new SarifRegion(line, startColumn)));

    private static string DescribeUnanalyzedKind(UnanalyzedObjectKind kind) => kind switch
    {
        UnanalyzedObjectKind.Procedure => "procedure",
        UnanalyzedObjectKind.View => "view",
        UnanalyzedObjectKind.Function => "function",
        UnanalyzedObjectKind.Trigger => "trigger",
        UnanalyzedObjectKind.Table => "table",
        _ => "object",
    };

    private static SarifResult ToResult(SargabilityFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.Tier1RuleId(finding.Kind), finding.Confidence);

        var isConfirmedIndexed = finding.Indexed == true;
        var level = isConfirmedIndexed && finding.Kind != SargabilityFindingKind.LikePatternNotLiteral ? LevelWarning : LevelNote;
        level = FloorLevelForConfidence(level, finding.Confidence);
        var detail = finding.Detail is null ? string.Empty : $" ({finding.Detail})";
        var indexNote = finding.TableQualifiedName is { } table
            ? $" [{table}.{finding.ColumnName}, indexed={IndexedDisplay(finding.Indexed)}]"
            : string.Empty;
        var message = $"Column '{finding.ColumnName}' is used in a non-sargable predicate{detail}.{indexNote}{DynamicSqlOriginNote(finding.DynamicSqlCallSite)}";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(TypedPredicateFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.VerdictRuleId(finding.Verdict), finding.Confidence);
        var baseLevel = finding.Verdict switch
        {
            Verdict.ScanForced => LevelError,
            Verdict.RangeSeek => LevelWarning,
            _ => LevelNote,
        };

        var level = finding.Column.Indexed == true ? baseLevel : DowngradeOneLevel(baseLevel);
        level = FloorLevelForConfidence(level, finding.Confidence);

        var depthNote = DescribeDepth(finding.Column.Depth);
        var indexNote = DescribeIndexNote(finding.Column);
        var reasonNote = finding.UnknownReason is { } reason ? $" [{reason}]" : string.Empty;
        var snippetNote = finding.PredicateFragmentText is { } snippet ? $" - `{snippet}`" : string.Empty;
        var message = $"{finding.Verdict}: '{finding.Column.TableQualifiedName}.{finding.Column.ColumnName}'{indexNote}{depthNote}{reasonNote}.{DynamicSqlOriginNote(finding.DynamicSqlCallSite)}{snippetNote}";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.ColumnPosition);
    }

    private static SarifResult ToResult(ExpressionDerivedFinding finding)
    {
        var chain = string.Join(" <- ", finding.TransformationChain.Select(DescribeTransformationSite));
        var underlying = finding.UnderlyingBaseColumns.Count == 0
            ? "no traceable base column"
            : string.Join(", ", finding.UnderlyingBaseColumns.Select(bc => $"{bc.TableQualifiedName}.{bc.ColumnName}{(bc.Indexed ? " (indexed)" : " (not indexed)")}"));
        var message = $"Column '{finding.ColumnName}' is a computed expression by the time it reaches this predicate ({chain}); underlying: {underlying}.{DynamicSqlOriginNote(finding.DynamicSqlCallSite)}";

        var anyUnderlyingIndexed = finding.UnderlyingBaseColumns.Any(bc => bc.Indexed);
        var level = anyUnderlyingIndexed ? LevelError : DowngradeOneLevel(LevelError);
        level = FloorLevelForConfidence(level, finding.Confidence);
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ExpressionDerivedRuleId, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.ColumnPosition);
    }

    private static SarifResult ToResult(CollationConflictFinding finding)
    {

        var message = $"Collation conflict: '{finding.FirstTableQualifiedName}.{finding.FirstColumnName}' (COLLATE {finding.FirstCollationName}) {finding.Operator} '{finding.SecondTableQualifiedName}.{finding.SecondColumnName}' (COLLATE {finding.SecondCollationName}) does not compile.{DynamicSqlOriginNote(finding.DynamicSqlCallSite)}";
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CollationConflictRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.ColumnPosition);
    }

    private static SarifResult ToResult(ColumnCollationDriftFinding finding)
    {

        var kindNote = finding.IsTempObject ? "tempdb's effective" : "the database's default";
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' (COLLATE {finding.ColumnCollationName}) differs from {kindNote} collation (COLLATE {finding.BaselineCollationName}) - a conversion seed for any future comparison against a column/literal carrying that collation.";
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ColumnCollationDriftRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(AnsiPaddingOffColumnFinding finding)
    {

        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' has ANSI_PADDING OFF in its own catalog state (sys.columns.is_ansi_padded = 0) - every write into it silently strips trailing blanks/zero bytes regardless of the writing session's own ANSI_PADDING setting.";
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ColumnAnsiPaddingOffRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(CrossTableTypeDriftFinding finding)
    {

        var message = $"FK '{finding.ConstraintName}': '{finding.ParentTableQualifiedName}.{finding.ParentColumnName}' ({finding.ParentTypeDisplay}) references '{finding.ReferencedTableQualifiedName}.{finding.ReferencedColumnName}' ({finding.ReferencedTypeDisplay}) - the types differ{(finding.CollationDiffers ? " (collation differs)" : string.Empty)}.";
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CrossTableTypeDriftRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(ProcCallArgumentMismatchFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ProcCallArgumentMismatchRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var callerLabel = finding.CallerScopeQualifiedName ?? TopLevelBatchCallerLabel;
        var message = finding.IsOutputWriteback
            ? $"EXEC '{finding.CalleeQualifiedName}': OUTPUT parameter '{finding.FormalParameterName}' ({finding.FormalParameterTypeDisplay}) writes its final value back into '{finding.CallerExpressionDisplay}' ({finding.CallerTypeDisplay}) in {callerLabel} - {DescribeWriteLossKind(finding.Kind)}."
            : $"EXEC '{finding.CalleeQualifiedName}': parameter '{finding.FormalParameterName}' ({finding.FormalParameterTypeDisplay}) receives '{finding.CallerExpressionDisplay}' ({finding.CallerTypeDisplay}) from {callerLabel} - {DescribeWriteLossKind(finding.Kind)}.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(TvfCallArgumentMismatchFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TvfCallArgumentMismatchRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var callerLabel = finding.CallerScopeQualifiedName ?? TopLevelBatchCallerLabel;
        var message = $"'{finding.CalleeQualifiedName}': parameter '{finding.FormalParameterName}' ({finding.FormalParameterTypeDisplay}) receives '{finding.CallerExpressionDisplay}' ({finding.CallerTypeDisplay}) from {callerLabel} - {DescribeWriteLossKind(finding.Kind)}.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ProcCallTableValuedArgumentMismatchFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ProcCallTableValuedArgumentMismatchRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var callerLabel = finding.CallerScopeQualifiedName ?? TopLevelBatchCallerLabel;
        var message = $"EXEC '{finding.CalleeQualifiedName}': table-valued parameter '{finding.FormalParameterName}' ({finding.TableTypeQualifiedName}) column '{finding.ColumnName}' ({finding.ColumnTypeDisplay}) was populated with '{finding.CallerExpressionDisplay}' ({finding.CallerTypeDisplay}) in {callerLabel} - {DescribeWriteLossKind(finding.Kind)}.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(SpExecuteSqlParameterMismatchFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SpExecuteSqlParameterMismatchRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var callerLabel = finding.CallerScopeQualifiedName ?? TopLevelBatchCallerLabel;
        var message = finding.IsOutputWriteback
            ? $"sp_executesql: declared OUTPUT parameter '{finding.ParameterName}' ({finding.DeclaredParameterTypeDisplay}) writes its final value back into '{finding.CallerExpressionDisplay}' ({finding.CallerTypeDisplay}) in {callerLabel} - {DescribeWriteLossKind(finding.Kind)}."
            : $"sp_executesql: declared parameter '{finding.ParameterName}' ({finding.DeclaredParameterTypeDisplay}) receives '{finding.CallerExpressionDisplay}' ({finding.CallerTypeDisplay}) from {callerLabel} - {DescribeWriteLossKind(finding.Kind)}.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(TemporalBoundaryPrecisionFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TemporalBoundaryPrecisionRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' (scale {finding.ColumnScale}) is compared with BETWEEN against upper bound '{finding.BoundaryLiteralText}' ({finding.BoundaryLiteralFractionalDigits} fractional digit(s)) - rows in the precision gap are silently excluded. Rewrite as >= start AND < (start of the next period).";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(JsonIndexRewriteFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.JsonIndexRewriteEligibleRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = $"JSON_VALUE({finding.ColumnName}, '{finding.JsonPath}') = ... stays a scan even though '{finding.TableQualifiedName}.{finding.ColumnName}' has a JSON index - rewrite as JSON_CONTAINS({finding.ColumnName}, value, '{finding.JsonPath}') = 1 so the JSON index is seekable.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(MaxTypedColumnFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.MaxTypedColumnRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = finding.Kind == NonIndexableColumnFindingKind.LegacyLargeObject
            ? $"'{finding.TableQualifiedName}.{finding.ColumnName}' is declared {finding.TypeDisplay} - TEXT/NTEXT/IMAGE columns can never appear in any index at all, not even as an INCLUDE column, so no predicate/join on it can ever seek and it can never be covered."
            : $"'{finding.TableQualifiedName}.{finding.ColumnName}' is declared {finding.TypeDisplay} - MAX-typed columns can never be an index key column, so no predicate/join on it can ever seek.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(FullTextIndexDdlFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.FullTextIndexDdlRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var location = finding.ColumnName is { } columnName ? $"'{finding.TableQualifiedName}.{columnName}'" : $"'{finding.TableQualifiedName}'";
        var message = finding.Kind switch
        {
            FullTextIndexDdlFindingKind.UnsupportedColumnType =>
                $"Full-text index on {location} indexes a column declared {finding.Detail} - not a character-based, XML, JSON, image, or varbinary(max) type, so CREATE FULLTEXT INDEX fails (Msg 7670).",
            FullTextIndexDdlFindingKind.InvalidLanguageId =>
                $"Full-text index on {location} specifies {finding.Detail} - not an LCID SQL Server's full-text language resources cover, so CREATE FULLTEXT INDEX fails (Msg 7696).",
            FullTextIndexDdlFindingKind.NonDeterministicComputedColumn =>
                $"Full-text index on {location} indexes a {finding.Detail} - CREATE FULLTEXT INDEX fails (Msg 9928).",
            FullTextIndexDdlFindingKind.TooManyIndexedColumns =>
                $"Full-text index on {location} lists {finding.Detail} - exceeds the full-text index column limit.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled FullTextIndexDdlFindingKind."),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(SemanticSearchFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SemanticSearchRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var location = finding.ColumnName is { } columnName ? $"'{finding.TableQualifiedName}.{columnName}'" : $"'{finding.TableQualifiedName}'";
        var message = finding.Kind switch
        {
            SemanticSearchFindingKind.TableNotSemanticFullTextIndexed =>
                $"Semantic search function on {location} - {finding.Detail}, so the call fails (Msg 41202).",
            SemanticSearchFindingKind.ColumnNotSemanticFullTextIndexed =>
                $"Semantic search function on {location} - {finding.Detail}, so the call fails (Msg 41203).",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled SemanticSearchFindingKind."),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ColumnstoreUnsupportedColumnTypeFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ColumnstoreUnsupportedColumnTypeRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' is declared {finding.TypeDisplay} and participates in columnstore index '{finding.IndexName}' - this does not deploy (Msg 35343: a SQL_VARIANT column cannot participate in a columnstore index).";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(ExternalTableUnsupportedColumnTypeFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ExternalTableUnsupportedColumnTypeRuleId, finding.Confidence);
        var message = $"External table column '{finding.TableQualifiedName}.{finding.ColumnName}' is declared or resolves to {finding.TypeDisplay} - this type is not supported with external tables (Msg 46518/15877).";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(VectorLiteralConversionFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.VectorLiteralConversionRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind == VectorLiteralConversionFindingKind.ElementCountMismatch
            ? $"String literal '{finding.LiteralText}' converted to {finding.TargetTypeDisplay} has {finding.ActualElementCount} element(s), not {finding.DeclaredDimensions} - the vector dimensions do not match; the conversion fails at execution (Msg 42204)."
            : $"String literal '{finding.LiteralText}' converted to {finding.TargetTypeDisplay} contains a {finding.ElementKind} element - the JSON array must contain only numbers; the conversion fails at execution (Msg 13670).";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(FullTextPredicateInAggregateFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.FullTextPredicateInAggregateRuleId, finding.Confidence);
        var message = $"{finding.AggregateFunctionName}(...) nests a {finding.FullTextFunctionName} full-text predicate - full-text predicates cannot appear in an aggregate expression; the statement does not compile (Msg 30082).";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ChangeTrackingEncryptedPrimaryKeyFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ChangeTrackingEncryptedPrimaryKeyRuleId, finding.Confidence);
        var message = $"ENABLE CHANGE_TRACKING targets '{finding.TableQualifiedName}', whose primary key column '{finding.ColumnName}' is Always Encrypted - change tracking does not support an encrypted primary key column; the statement fails (Msg 22118).";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(XmlSchemaCollectionDisallowedTypeFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.XmlSchemaCollectionDisallowedTypeRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind == XmlSchemaCollectionDisallowedTypeKind.NotationType
            ? $"XML schema collection '{finding.SchemaCollectionQualifiedName}' uses the XML Schema type NOTATION - this type is not supported; the schema collection never registers (Msg 9337)."
            : $"XML schema collection '{finding.SchemaCollectionQualifiedName}' uses the built-in XML Schema type {finding.XsdTypeName} (or a type derived from it) as an element's type or an extension/restriction base - this is not permitted; the schema collection never registers (Msg 6995).";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(XmlSchemaCollectionMismatchFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.XmlSchemaCollectionMismatchRuleId, finding.Confidence);
        var message = $"'{finding.TargetVariableName}' (XML({finding.TargetSchemaCollectionName})) is assigned directly from '{finding.SourceVariableName}' (XML({finding.SourceSchemaCollectionName})) - implicit conversion between XML types constrained by different schema collections is not allowed; the statement does not compile (Msg 527).";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(SelectiveXmlIndexValueColumnFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SelectiveXmlIndexValueColumnRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = finding.Kind == SelectiveXmlIndexValueColumnFindingKind.LargeObject
            ? $"Secondary selective XML index '{finding.SecondaryIndexName}' on '{finding.TableQualifiedName}' over path '{finding.PathName}' (promoted as {finding.TypeDisplay} in selective XML index '{finding.PrimaryIndexName}') does not deploy (Msg 6391: promoted to a type invalid for use as a key column in a secondary selective XML index)."
            : $"Secondary selective XML index '{finding.SecondaryIndexName}' on '{finding.TableQualifiedName}' over path '{finding.PathName}' (promoted as {finding.TypeDisplay} in selective XML index '{finding.PrimaryIndexName}') does not deploy (Msg 6395: the maximum key length is 900 bytes).";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(MemoryOptimizedUnsupportedColumnTypeFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.MemoryOptimizedUnsupportedColumnTypeRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' is declared {finding.TypeDisplay} on memory-optimized table '{finding.TableQualifiedName}' - this type is not supported on a memory-optimized table at all (Msg 10794), so the statement does not deploy.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(MemoryOptimizedUtf8CollationFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.MemoryOptimizedUtf8CollationRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' is declared {finding.TypeDisplay} COLLATE {finding.CollationName} on memory-optimized table '{finding.TableQualifiedName}' - a UTF-8 collation on a char/varchar column is not supported on a memory-optimized table at all (Msg 12356), so the statement does not deploy.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(NativelyCompiledUnsupportedBuiltinFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NativelyCompiledUnsupportedBuiltinRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"Natively compiled module '{finding.ModuleQualifiedName}' calls {finding.FunctionName}(), which is not supported with natively compiled modules (Msg 10794), so the statement does not compile.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(NativelyCompiledClrTypeFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NativelyCompiledClrTypeRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var kindText = finding.Kind == NativelyCompiledClrTypeKind.Parameter ? "parameter" : "local variable";
        var message = $"Natively compiled module '{finding.ModuleQualifiedName}' declares {kindText} '{finding.MemberName}' typed {finding.TypeQualifiedName}, a CLR user-defined type, which is not supported with natively compiled modules (Msg 10794), so the statement does not compile.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(NativelyCompiledErrorOutsideCatchFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NativelyCompiledErrorOutsideCatchRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"Natively compiled module '{finding.ModuleQualifiedName}' calls {finding.FunctionName}() outside a CATCH block, which is not supported with natively compiled modules (Msg 10792), so the statement does not compile.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(NativelyCompiledInterpretedCalleeFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NativelyCompiledInterpretedCalleeRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = finding.Kind == NativelyCompiledInterpretedCalleeKind.ExecutedProcedure
            ? $"Natively compiled module '{finding.ModuleQualifiedName}' executes '{finding.CalleeQualifiedName}', which is not itself natively compiled - EXECUTE inside a natively compiled module only supports executing another natively compiled module (Msg 12342), so the statement does not compile."
            : $"Natively compiled module '{finding.ModuleQualifiedName}' calls '{finding.CalleeQualifiedName}', which is not itself natively compiled - only natively compiled modules can call other natively compiled modules (Msg 12344), so the statement does not compile.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(MemoryOptimizedLedgerConflictFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.MemoryOptimizedLedgerConflictRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"Table '{finding.TableQualifiedName}' specifies both MEMORY_OPTIMIZED = ON and LEDGER = ON - ledger tables are not supported with memory-optimized tables (Msg 12359), so the statement does not deploy.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(MemoryOptimizedUnsupportedIndexOptionFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.MemoryOptimizedUnsupportedIndexOptionRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = finding.Kind switch
        {
            MemoryOptimizedUnsupportedIndexOptionKind.ClusteredIndex => $"Index '{finding.IndexName}' on memory-optimized table '{finding.TableQualifiedName}' is a rowstore CLUSTERED index - not supported on a memory-optimized table (Msg 12317), so the statement does not deploy.",
            MemoryOptimizedUnsupportedIndexOptionKind.IncludedColumns => $"Index '{finding.IndexName}' on memory-optimized table '{finding.TableQualifiedName}' declares INCLUDE columns - not supported on a memory-optimized table (Msg 10664), so the statement does not deploy.",
            MemoryOptimizedUnsupportedIndexOptionKind.FilteredIndex => $"Index '{finding.IndexName}' on memory-optimized table '{finding.TableQualifiedName}' is a filtered index (WHERE clause) - not supported on a memory-optimized table (Msg 10794), so the statement does not deploy.",
            _ => $"Index '{finding.IndexName}' on memory-optimized table '{finding.TableQualifiedName}' uses an unsupported index option.",
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(MemoryOptimizedForeignKeyFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.MemoryOptimizedForeignKeyRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = finding.Kind switch
        {
            MemoryOptimizedForeignKeyFindingKind.CrossStorageForeignKey => $"Foreign key '{finding.ConstraintName}' spans '{finding.ParentTableQualifiedName}' and '{finding.ReferencedTableQualifiedName}', where exactly one side is memory-optimized - foreign keys between memory-optimized and non-memory-optimized tables are not supported (Msg 10778), so the constraint does not deploy.",
            MemoryOptimizedForeignKeyFindingKind.ReferentialAction => $"Foreign key '{finding.ConstraintName}' between memory-optimized tables '{finding.ParentTableQualifiedName}' and '{finding.ReferencedTableQualifiedName}' declares a referential action other than NO ACTION - not supported on a memory-optimized-to-memory-optimized foreign key (Msg 10794), so the constraint does not deploy.",
            _ => $"Foreign key '{finding.ConstraintName}' between '{finding.ParentTableQualifiedName}' and '{finding.ReferencedTableQualifiedName}' is not supported on a memory-optimized table.",
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(MemoryOptimizedSchemaOnlyDurabilityFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.MemoryOptimizedSchemaOnlyDurabilityRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"Memory-optimized table '{finding.TableQualifiedName}' is declared WITH (DURABILITY = SCHEMA_ONLY) - only its schema is persisted, so every row is lost on a server restart, failover, or database restore/attach, with no error or warning.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(OversizedParameterFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.OversizedParameterRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' (length {finding.ColumnLength}) is compared against a parameter/variable/expression declared with length {finding.OtherOperandLength} - risks memory-grant inflation if the value feeds a sort/hash operator.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(PartialCompositeForeignKeyJoinFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.PartialCompositeForeignKeyJoinRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var matched = string.Join(", ", finding.MatchedColumnPairs.Select(p => $"{p.ParentColumnName}={p.ReferencedColumnName}"));
        var missing = string.Join(", ", finding.MissingColumnPairs.Select(p => $"{p.ParentColumnName}={p.ReferencedColumnName}"));
        var message = $"FK '{finding.ConstraintName}': join between '{finding.ParentTableQualifiedName}' and '{finding.ReferencedTableQualifiedName}' matches [{matched}] but omits [{missing}] - a parent row can match more than one child row than the declared relationship allows.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(OuterJoinPredicateCollapseFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.OuterJoinPredicateCollapseRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var kindText = finding.Kind switch
        {
            OuterJoinPredicateCollapseKind.LeftOuterJoin => "LEFT OUTER JOIN",
            OuterJoinPredicateCollapseKind.RightOuterJoin => "RIGHT OUTER JOIN",
            _ => "FULL OUTER JOIN",
        };
        var message = $"WHERE compares '{finding.NullSupplyingTableQualifiedName}.{finding.ColumnName}', the null-supplying side of a {kindText}, with no OR ... IS NULL guard - unmatched rows are discarded, silently turning the {kindText} into an INNER JOIN.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(SetOptionFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SetOptionRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var touchedDisplay = DescribeTouchedObjectForSetOption(finding);
        var message = finding.Kind switch
        {
            SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature =>
                $"'{finding.ModuleQualifiedName}' was compiled under QUOTED_IDENTIFIER OFF{touchedDisplay}.",
            SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature =>
                $"'{finding.ModuleQualifiedName}' was compiled under ANSI_NULLS OFF{touchedDisplay}.",
            SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature =>
                $"'{finding.ModuleQualifiedName}': SET NUMERIC_ROUNDABORT ON{touchedDisplay}.",
            SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature =>
                $"'{finding.ModuleQualifiedName}': SET ANSI_WARNINGS OFF{touchedDisplay}.",
            SetOptionFindingKind.AnsiPaddingOffBlocksIndexedFeature =>
                $"'{finding.ModuleQualifiedName}': SET ANSI_PADDING OFF{touchedDisplay}.",
            _ => $"'{finding.ModuleQualifiedName}': SET CONCAT_NULL_YIELDS_NULL OFF{touchedDisplay}.",
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(UnderLengthParameterFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.UnderLengthParameterRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var otherLengthDisplay = finding.IsImplicitDefault ? "no explicit length (defaults to 1)" : $"length {finding.OtherOperandLength}";
        var shapeNote = finding.ChangesRangeOrPatternShape
            ? $" - truncation changes what the '{finding.Operator}' comparison itself means (a shorter pattern/bound), not just which exact value it excludes"
            : " - the compared value is silently truncated before the predicate ever runs, which can exclude rows that should match or match rows that shouldn't";
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' (length {finding.ColumnLength}) is compared against a parameter/variable/expression declared with {otherLengthDisplay}{shapeNote}.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(AnsiPaddingMismatchFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.AnsiPaddingMismatchRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' is a non-ANSI-padded column (trailing blanks stripped at INSERT) compared via LIKE against pattern {finding.PatternLiteralText}, whose trailing whitespace is significant - this predicate can never match any value the column could ever store.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(CatchAllPredicateFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CatchAllPredicateRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(finding.Indexed ? LevelWarning : LevelNote, finding.Confidence);
        var indexedNote = finding.Indexed ? string.Empty : " (would defeat an index if one existed - none is confirmed indexed today)";
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' = {finding.ParameterName} OR {finding.ParameterName} IS NULL{indexedNote} - one cached plan must stay correct for every NULL/non-NULL state of this parameter, typically forcing a scan.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(LocalVariablePredicateFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.LocalVariablePredicateRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' {finding.Operator} {finding.VariableName} - a DECLARE'd local, not a formal parameter, so its value is invisible to the cardinality estimator (falls back to average-density statistics). The predicate still seeks if the column is indexed; only the row-count estimate is at risk.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(FilteredIndexParameterMismatchFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.FilteredIndexParameterMismatchRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var operandKind = finding.IsFormalParameter ? "formal parameter" : "local variable";
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' {finding.Operator} {finding.VariableName} - filtered index '{finding.IndexName ?? "<unnamed>"}' filters this exact column against the literal {finding.FilterLiteralText}, but the optimizer can only match a filtered index against a query that restates its filter with a LITERAL, never a {operandKind}. This query can never use that index, no matter what value {finding.VariableName} holds at runtime - a compile-time limitation OPTION (RECOMPILE) does not fix.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ParameterReassignmentPredicateFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ParameterReassignmentPredicateRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' {finding.Operator} @{finding.ParameterName} - {finding.ParameterName} is a formal parameter reassigned at line {finding.ReassignmentLine} before this predicate runs, so the optimizer's compile-time sniffed value (the caller's original argument) is stale by the time this comparison executes. The predicate still seeks if the column is indexed; only the row-count estimate is at risk.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(CodeMetricFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CodeMetricRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = finding.Kind switch
        {
            CodeMetricFindingKind.LineTooLong =>
                $"Line is {finding.MeasuredValue} characters long, which is greater than the {finding.Threshold} authorized.",
            CodeMetricFindingKind.ModuleTooLong =>
                $"'{finding.ModuleQualifiedName}' has {finding.MeasuredValue} lines, which is greater than the {finding.Threshold} authorized.",
            CodeMetricFindingKind.RoutineTooLong =>
                $"{finding.DetailText} '{finding.ModuleQualifiedName}' has {finding.MeasuredValue} lines of code, which is greater than the {finding.Threshold} authorized.",
            CodeMetricFindingKind.TooManyParameters =>
                $"{finding.DetailText} '{finding.ModuleQualifiedName}' has {finding.MeasuredValue} parameters, which is greater than the {finding.Threshold} authorized.",
            CodeMetricFindingKind.NestingTooDeep =>
                $"Control flow nests {finding.MeasuredValue} levels deep here, which is greater than the {finding.Threshold} authorized.",
            CodeMetricFindingKind.TooManyConditionalOperators =>
                $"This condition chains {finding.MeasuredValue} AND/OR operators, which is greater than the {finding.Threshold} authorized.",
            CodeMetricFindingKind.TooManyCaseBranches =>
                $"This CASE expression has {finding.MeasuredValue} WHEN branches, which is greater than the {finding.Threshold} authorized.",
            _ =>
                $"This CASE WHEN branch spans {finding.MeasuredValue} lines, which is greater than the {finding.Threshold} authorized.",
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(FormattingFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.FormattingRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = finding.Kind switch
        {
            FormattingFindingKind.TabCharacterUsed =>
                "This line contains a tab character - replace it with spaces for consistent rendering across editors.",
            FormattingFindingKind.MultipleStatementsOnSameLine =>
                "This statement shares a physical source line with the previous one - put one statement per line.",
            FormattingFindingKind.MultipleDeclarationsOnSameLine =>
                $"'{finding.DetailText}' is declared on the same physical source line as the previous variable - declare each on its own line.",
            FormattingFindingKind.MissingBeginEndBlock =>
                "This conditional body is a single statement with no BEGIN...END - a later statement added here without braces silently falls outside the conditional.",
            FormattingFindingKind.SingleLineConditionalBody =>
                "This conditional body shares the same line as its own keyword with no BEGIN...END - easy to misread.",
            FormattingFindingKind.DanglingStatementAfterUnbracedBody =>
                "This statement is not actually part of the conditional/loop above it, even though its indentation makes it look like it is - the body above has no BEGIN...END.",
            FormattingFindingKind.IfImmediatelyFollowingPriorBlockEnd =>
                "This IF immediately follows the prior IF's own END on the same line - easy to misread as an ELSE IF continuation when it is really a separate, unconditional statement.",
            FormattingFindingKind.RedundantParentheses =>
                "These parentheses do not change grouping or precedence - remove them.",
            _ =>
                "This module's own definition does not begin with a comment before its first real statement.",
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ForcedParameterizationFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ForcedParameterizationRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(NamingFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NamingRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(DeadCodeFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.DeadCodeRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = finding.Kind switch
        {
            DeadCodeFindingKind.UnreachableCode =>
                "This statement can never execute - control flow always ends the routine before reaching it on every path.",
            DeadCodeFindingKind.UnusedLabel =>
                $"Label \"{finding.DetailText}\" is never the target of a GOTO anywhere in this routine.",
            DeadCodeFindingKind.UnusedLocalVariable =>
                $"Local variable \"{finding.DetailText}\" is declared but never read - only ever assigned, or never referenced at all.",
            DeadCodeFindingKind.UnusedParameter =>
                $"Parameter \"{finding.DetailText}\" is never referenced anywhere in the routine body.",
            DeadCodeFindingKind.RedundantJump =>
                $"GOTO {finding.DetailText} jumps to the very next statement - control flow would already go there.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled DeadCodeFindingKind."),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(DuplicationFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.DuplicationRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = finding.Kind switch
        {
            DuplicationFindingKind.CommentedOutCode =>
                "This comment's own content reparses as plausible T-SQL - remove the commented-out code or restore it.",
            DuplicationFindingKind.DuplicatedStringLiteral =>
                $"String literal {finding.DetailText} - define a constant or variable instead of repeating it.",
            DuplicationFindingKind.SingleIterationLoop =>
                "This WHILE loop's own body unconditionally exits on every path through the first iteration - it can never loop a second time.",
            DuplicationFindingKind.SelfAssignment =>
                $"\"{finding.DetailText}\" is assigned to itself - remove this no-op assignment or correct one side.",
            DuplicationFindingKind.IdenticalBinaryOperands =>
                $"The identical expression appears on both sides of \"{finding.DetailText}\" - correct one side or remove the redundant comparison.",
            DuplicationFindingKind.RepeatedUnaryOperator =>
                $"The \"{finding.DetailText}\" operator is applied twice in a row - simplify to a single application.",
            DuplicationFindingKind.NegatedComparisonAsOpposite =>
                $"Use the opposite operator (\"{finding.DetailText}\") instead of negating its complement.",
            DuplicationFindingKind.DuplicateSiblingCondition =>
                $"Condition \"{finding.DetailText}\" repeats an earlier sibling branch's own condition - this branch can never be reached.",
            DuplicationFindingKind.IdenticalBranchBodies =>
                "This branch's body is identical to another sibling branch's - either the conditional is partly pointless or a copy-paste mistake left this branch matching another.",
            DuplicationFindingKind.AllBranchesIdentical =>
                "Every branch of this conditional structure, including its ELSE, produces the same outcome - the structure itself is pointless.",
            DuplicationFindingKind.RedundantAndCondition =>
                $"This bound on \"{finding.DetailText}\" adds nothing once combined with a stricter sibling bound in the same AND-chain - remove it.",
            DuplicationFindingKind.MutuallyExclusiveAndCondition =>
                $"This bound on \"{finding.DetailText}\" can never hold at the same time as a sibling bound in the same AND-chain - the whole condition can never be true.",
            DuplicationFindingKind.CollapsibleNestedIf =>
                "This IF's entire body is a single nested IF with no ELSE at either level - combine both conditions with AND into one IF.",
            DuplicationFindingKind.NestedConditionalExpression =>
                $"This IIF call is nested inside another IIF's own {finding.DetailText} branch - extract it into an independent expression or statement.",
            DuplicationFindingKind.AlwaysTrueOrFalseLiteralComparison =>
                $"This comparison between two literal values is {finding.DetailText} regardless of any row's real data.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled DuplicationFindingKind."),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(DeprecatedSyntaxFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.DeprecatedSyntaxRuleId(finding.Kind), finding.Confidence);
        var baseLevel = finding.Kind is DeprecatedSyntaxFindingKind.TaskCommentTodo or DeprecatedSyntaxFindingKind.TaskCommentFixme
            ? LevelNote
            : LevelWarning;
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(StatementShapeFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.StatementShapeRuleId(finding.Kind), finding.Confidence);
        var baseLevel = finding.Kind == StatementShapeFindingKind.BareSelectStar ? LevelNote : LevelWarning;
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ControlFlowRiskFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ControlFlowRiskRuleId(finding.Kind), finding.Confidence);
        var baseLevel = finding.Kind is ControlFlowRiskFindingKind.CursorFetchColumnCountMismatch
            or ControlFlowRiskFindingKind.EmptyCatchBlock
            or ControlFlowRiskFindingKind.CaseExpressionMissingElse
            or ControlFlowRiskFindingKind.NonDeterministicCaseInput
            ? LevelError
            : LevelWarning;
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(SecurityFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SecurityRuleId(finding.Kind), finding.Confidence);
        var baseLevel = finding.Kind is SecurityFindingKind.HardCodedIpAddress or SecurityFindingKind.WeakHashAlgorithm
            ? LevelError
            : LevelWarning;
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(NotInNullableSubqueryFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NotInNullableSubqueryRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var outerColumnDisplay = finding.OuterColumnName ?? "<expression>";
        var message = $"{outerColumnDisplay} NOT IN (SELECT '{finding.SubqueryTableQualifiedName}.{finding.SubqueryColumnName}' ...) - the subquery column is nullable and unfiltered, so the whole predicate evaluates to UNKNOWN and silently returns zero rows the instant the data contains one NULL there, instead of the expected anti-join result.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(NonUniqueUpdateSourceFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NonUniqueUpdateSourceRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var joinColumns = string.Join(", ", finding.JoinColumnNames);
        var setColumns = string.Join(", ", finding.SetColumnNames);
        var message = $"UPDATE '{finding.TargetTableQualifiedName}' sets [{setColumns}] from '{finding.SourceTableQualifiedName}' joined on [{joinColumns}], which carries no unique index/constraint covering those columns - if a target row ever matches more than one source row, SQL Server silently picks a value from an unspecified one of them.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ForcedSerialFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ForcedSerialRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = finding.Kind switch
        {
            ForcedSerialFindingKind.TableVariableModification =>
                $"'{finding.ModuleQualifiedName}' writes to table variable '{finding.DetailText}' - this statement's own plan is forced serial (effective MAXDOP 1).",
            ForcedSerialFindingKind.FastForwardCursor =>
                $"'{finding.ModuleQualifiedName}': cursor '{finding.DetailText}' is FAST_FORWARD (or the equivalent bare FORWARD_ONLY READ_ONLY) - its own defining query plan is forced serial.",
            _ => $"'{finding.ModuleQualifiedName}': {finding.DetailText}{(finding.DetailText!.StartsWith("@@", StringComparison.Ordinal) ? string.Empty : "()")} referenced inside a query with a FROM clause forces that query's plan serial.",
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(SelfReferencingDmlFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SelfReferencingDmlRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var viaDisplay = finding.Kind == SelfReferencingDmlFindingKind.ThroughView
            ? $" (through view '{finding.ReadSideQualifiedName}')"
            : string.Empty;
        var message = $"{finding.StatementKind} on '{finding.TargetTableQualifiedName}' also reads from that same table{viaDisplay} - this forces extra defensive plan work (an Eager Spool or Sort the engine would not otherwise need) to guarantee every write sees a consistent read.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(UntrustedConstraintFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.UntrustedConstraintRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var kindDisplay = finding.Kind == UntrustedConstraintFindingKind.ForeignKey ? "foreign key" : "CHECK constraint";
        var message = $"'{finding.ConstraintName}' ({kindDisplay} on '{finding.TableQualifiedName}') is untrusted - the engine does not guarantee it holds over existing rows, and forfeits join-elimination/constraint-based query rewrites that assume it does.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(CheckConstraintFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CheckConstraintRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = finding.Kind switch
        {
            CheckConstraintFindingKind.NullNotHandled =>
                $"'{finding.ConstraintName}' (CHECK on '{finding.TableQualifiedName}.{finding.ColumnName}') has no IS NULL/IS NOT NULL test against '{finding.ColumnName}' anywhere in its predicate, and '{finding.ColumnName}' is nullable - a NULL value silently passes this constraint under SQL Server's three-valued logic, even though the constraint reads as if it forbids bad data.",
            CheckConstraintFindingKind.ConstraintOnIdentityColumn =>
                IdentityColumnCheckMessage(finding),
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled CheckConstraintFindingKind."),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(DefaultNullableConstraintFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.DefaultNullableConstraintRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' carries a DEFAULT constraint ({finding.DefaultDefinitionText}) but is still nullable - a caller supplying NULL explicitly for this column bypasses the default entirely, silently, with no error.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(TryCastComputedColumnPredicateFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TryCastComputedColumnPredicateRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' (a non-persisted computed column defined as '{finding.DefinitionText}', {finding.DefinitionLocation.SourcePath}:{finding.DefinitionLocation.Line}) is referenced in a predicate here - TRY_CAST makes this column non-deterministic, so it can never be PERSISTED or indexed, and this predicate can never seek through it.";

        return BuildResult(ruleId, level, message, finding.Location.SourcePath, finding.Location.Line, finding.Location.Column);
    }

    private static SarifResult ToResult(StaleSelectStarViewFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.StaleSelectStarViewRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var viewColumns = string.Join(", ", finding.ViewCompiledColumns);
        var tableColumns = string.Join(", ", finding.BaseTableCurrentColumns);
        var message = $"'{finding.ViewQualifiedName}' (SELECT * FROM '{finding.BaseTableQualifiedName}') has a compiled column list [{viewColumns}] that no longer matches '{finding.BaseTableQualifiedName}''s current columns [{tableColumns}] - a later ALTER TABLE ADD/DROP COLUMN never propagated to this view; if a drop and a later add shifted column identity, this view may be silently surfacing real data under a stale, wrong column label, not merely missing/adding a column.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(BareTopNoOrderByFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.BareTopNoOrderByRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = "TOP with no ORDER BY anywhere in this query - SQL Server does not guarantee which rows TOP returns, or their order, without an ORDER BY; the returned row set can change run to run with plan choice, parallelism, or statistics drift.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(StringConcatNullFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.StringConcatNullRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' is nullable and concatenated with + with no ISNULL/COALESCE guard - unlike CONCAT(), + propagates a single NULL operand to NULL for the whole expression, silently, with no error.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(AggregateDivisionColumnstoreFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.AggregateDivisionColumnstoreRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = $"{finding.AggregateFunctionName}(...) on '{finding.TableQualifiedName}' (backed by a columnstore index) contains a CASE-guarded division by a non-constant divisor - historically reported as unreliable under batch-mode/vectorized execution's own CASE-branch evaluation, unlike rowstore scalar evaluation.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(SecurityPredicateIndexFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SecurityPredicateIndexRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var columns = string.Join(", ", finding.FilteredColumns);
        var message = $"'{finding.TableQualifiedName}' is secured by RLS policy '{finding.PolicyQualifiedName}''s FILTER predicate '{finding.PredicateFunctionQualifiedName}', bound to column(s) {columns} - none of them leads an active index on this table, so this predicate is silently applied to every SELECT/UPDATE/DELETE against this table as a residual, per-row filter over a full scan rather than a seek.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(DanglingObjectReferenceFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.DanglingObjectReferenceRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var referencedName = finding.ReferencedSchemaName is { } schema ? $"{schema}.{finding.ReferencedEntityName}" : finding.ReferencedEntityName;
        var message = $"{finding.ModuleTypeDescription} '{finding.ModuleQualifiedName}' references '{referencedName}', which does not exist in the database right now - CREATE/ALTER succeeded because SQL Server defers name resolution, but any call that reaches this reference fails with Msg 208 (\"Invalid object name\").";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(CascadingForeignKeyFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CascadingForeignKeyRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var actions = string.Join(", ", new[]
        {
            finding.DeleteAction != ReferentialAction.NoAction ? $"ON DELETE {finding.DeleteAction}" : null,
            finding.UpdateAction != ReferentialAction.NoAction ? $"ON UPDATE {finding.UpdateAction}" : null,
        }.Where(a => a is not null));
        var message = $"'{finding.ConstraintName}' ({finding.ParentTableQualifiedName} -> {finding.ReferencedTableQualifiedName}) carries {actions} - a DML statement against the referenced table silently cascades to the parent's dependent rows too.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(TemporalTableHistoryIndexGapFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TemporalTableHistoryIndexGapRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var indexDisplay = finding.CurrentIndexName is null ? "an unnamed index" : $"'{finding.CurrentIndexName}'";
        var keyColumns = string.Join(", ", finding.KeyColumns);
        var message = $"{indexDisplay} on '{finding.CurrentTableQualifiedName}' ({keyColumns}) has no structurally matching index on its history table '{finding.HistoryTableQualifiedName}' - a FOR SYSTEM_TIME query that seeks the current side via this index degrades to a scan of the whole history table.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(ModuleCompileFlagFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ModuleCompileFlagRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = finding.Kind switch
        {
            ModuleCompileFlagFindingKind.RecompilesEveryCall =>
                $"'{finding.ModuleQualifiedName}' is authored WITH RECOMPILE - every call compiles a fresh plan and discards it, so this module's own cost never accumulates in the plan cache at all.",
            ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation =>
                $"'{finding.ModuleQualifiedName}' declares a RETURNS TABLE character column with no explicit COLLATE - its collation was baked in against the database's default collation at CREATE/ALTER time and will silently disagree with the database's collation after any future ALTER DATABASE ... COLLATE.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, null),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(WindowFrameFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.WindowFrameRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = finding.Kind switch
        {
            WindowFrameFindingKind.ExplicitRangeFrame =>
                "This window function uses an explicit RANGE frame - oracle-measured to cost materially more CPU at the Window Spool operator than the equivalent ROWS frame for the same logical boundary.",
            WindowFrameFindingKind.ImplicitDefaultRangeFrame =>
                "This window function has an ORDER BY but no explicit frame clause - T-SQL silently defaults this to a RANGE frame, oracle-confirmed to carry the same measured cost as writing RANGE explicitly.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, null),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(WindowFunctionArgumentFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.WindowFunctionArgumentRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = finding.Kind switch
        {
            WindowFunctionArgumentFindingKind.LagLeadNegativeOffset =>
                $"{finding.FunctionName}'s offset argument '{finding.ArgumentText}' constant-folds to a negative value - oracle-confirmed (Msg 8730) this fails the moment any row reaches the window function.",
            WindowFunctionArgumentFindingKind.PercentileOutOfRange =>
                $"{finding.FunctionName}'s percentile argument '{finding.ArgumentText}' constant-folds to a value outside [0, 1] - oracle-confirmed (Msg 8727) this fails the moment any row reaches the function.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, null),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(StringSplitArgumentFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.StringSplitArgumentRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = finding.Kind switch
        {
            StringSplitArgumentFindingKind.SeparatorNotSingleCharacter =>
                $"STRING_SPLIT's separator argument '{finding.ArgumentText}' is not exactly one character - oracle-confirmed (Msg 214) this call fails at compile/bind time.",
            StringSplitArgumentFindingKind.ArgumentTypeNotCharacter =>
                $"STRING_SPLIT's argument '{finding.ArgumentText}' has type {finding.DetailText}, not a character type - oracle-confirmed (Msg 8116) this call fails at compile/bind time.",
            StringSplitArgumentFindingKind.EnableOrdinalNotConstant =>
                $"STRING_SPLIT's enable_ordinal argument '{finding.ArgumentText}' is not a constant - oracle-confirmed (Msg 8748) enable_ordinal only supports constant values, not variables or columns.",
            StringSplitArgumentFindingKind.EnableOrdinalTypeNotInteger =>
                $"STRING_SPLIT's enable_ordinal argument '{finding.ArgumentText}' has type {finding.DetailText}, not int/bit - oracle-confirmed (Msg 8116) this call fails at compile/bind time.",
            StringSplitArgumentFindingKind.EnableOrdinalInvalidValue =>
                $"STRING_SPLIT's enable_ordinal argument '{finding.ArgumentText}' is not 0 or 1 - oracle-confirmed (Msg 4199) this call fails at bind time.",
            StringSplitArgumentFindingKind.ThreeArgumentFormRequiresNewerEngine =>
                $"STRING_SPLIT's 3-argument ordinality form is used against a connected engine reporting major version {finding.DetailText} - oracle-confirmed (Msg 8144, SQL Server 2019) the 3-argument form only exists from SQL Server 2022 (major version 16) onward.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, null),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(BoundedStringBuiltinTruncationFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.BoundedStringBuiltinTruncationRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = finding.Kind switch
        {
            BoundedStringBuiltinTruncationFindingKind.ReplicateResultTruncated =>
                $"REPLICATE's result constant-folds to {finding.ComputedLength} bytes, over the non-MAX-typed result's {finding.CapBytes}-byte cap - oracle-confirmed the excess is silently truncated away, with no error.",
            BoundedStringBuiltinTruncationFindingKind.ReplaceResultTruncated =>
                $"REPLACE's result constant-folds to {finding.ComputedLength} bytes, over the non-MAX-typed result's {finding.CapBytes}-byte cap - oracle-confirmed the excess is silently truncated away, with no error.",
            BoundedStringBuiltinTruncationFindingKind.SpaceResultTruncated =>
                $"SPACE's requested {finding.ComputedLength}-character result is over its fixed {finding.CapBytes}-byte cap - oracle-confirmed the excess is silently truncated away, with no error.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, null),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(BackupOptionConflictFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.BackupOptionConflictRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        const string message = "BACKUP DATABASE with both DIFFERENTIAL and COPY_ONLY always fails - a copy-only backup never registers as a differential base, so no differential can ever find a current backup to diff against (Msg 3035).";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(RestoreOptionConflictFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.RestoreOptionConflictRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = finding.Kind switch
        {
            RestoreOptionConflictKind.RecoveryAndNoRecovery => "RESTORE with both RECOVERY and NORECOVERY always fails - the two describe mutually exclusive end states for the database (Msg 3031).",
            RestoreOptionConflictKind.RecoveryAndStandby => "RESTORE with both RECOVERY and STANDBY always fails - the two describe mutually exclusive end states for the database (Msg 3031).",
            RestoreOptionConflictKind.NoRecoveryAndStandby => "RESTORE with both NORECOVERY and STANDBY always fails - the two describe mutually exclusive end states for the database (Msg 3031).",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, null),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(ViewCheckOptionContradictionFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ViewCheckOptionContradictionRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"This value for '{finding.ColumnName}' falls outside the range view '{finding.ViewQualifiedName}' allows through its own WHERE clause - the view was created WITH CHECK OPTION, so the engine always rejects this row (Msg 550).";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(CreateDatabaseOptionConflictFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CreateDatabaseOptionConflictRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        const string message = "CREATE DATABASE with both CONTAINMENT = PARTIAL and CATALOG_COLLATION always fails - the two are mutually exclusive on this engine (Msg 12845).";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(GraphPseudoColumnAssignmentFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.GraphPseudoColumnAssignmentRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"{finding.StatementKind} assigns '{finding.PseudoColumnName}' directly - it is a hidden, system-managed column on a SQL Graph node/edge table and always rejects a direct value.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(LegacyLobUtf8CollationFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.LegacyLobUtf8CollationRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"Column '{finding.ColumnName}' on {finding.TableQualifiedName} is {finding.TypeDisplay} with collation '{finding.CollationName}' - TEXT/NTEXT cannot carry a UTF-8 or supplementary-character-aware collation, so this CREATE/ALTER never compiles.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(LegacyLobConversionTargetFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.LegacyLobConversionTargetRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"Conversion targets {finding.TypeDisplay} with collation '{finding.CollationName}' - TEXT/NTEXT cannot carry a UTF-8 or supplementary-character-aware collation, so this statement never compiles.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(GroupByValidityFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.GroupByValidityRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var clause = finding.Kind switch
        {
            GroupByValidityFindingKind.Having => "HAVING clause",
            GroupByValidityFindingKind.OrderBy => "ORDER BY clause",
            _ => "select list",
        };
        var message = $"'{finding.ExpressionText}' in the {clause} is neither an aggregate function argument nor contained in the GROUP BY clause - this statement never compiles.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(WaitForFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.WaitForRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = finding.IsInsideTransaction
            ? "WAITFOR DELAY/TIME holds this worker thread idle inside an open transaction - locks held by that transaction stay held for the full delay/until-time too."
            : "WAITFOR DELAY/TIME holds this worker thread idle for the full delay/until-time, contributing to worker-pool exhaustion under load.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(CursorCloseOnCommitFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CursorCloseOnCommitRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var closer = finding.ClosedByRollback ? "ROLLBACK" : "COMMIT";
        var message = $"Cursor '{finding.CursorName}' (opened at line {finding.OpenLine}) was silently closed by CURSOR_CLOSE_ON_COMMIT ON when this {closer} ran at line {finding.ClosingStatementLine} - this FETCH fails at runtime with Msg 16917 (\"Cursor is not open\") unless the cursor is re-opened first.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.FetchLine, finding.FetchColumn);
    }

    private static SarifResult ToResult(TransactionHygieneFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TransactionHygieneRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = finding.Kind switch
        {
            TransactionHygieneFindingKind.ImplicitTransactionUnresolvedOnSomePath =>
                $"SET IMPLICIT_TRANSACTIONS ON silently opens a transaction at line {finding.BeginTransactionLine} with no matching BEGIN TRANSACTION - it reaches this point with no intervening COMMIT/ROLLBACK, leaving @@TRANCOUNT elevated by one on this path.",
            TransactionHygieneFindingKind.CommitAfterXactAbortDoomsTransaction =>
                $"COMMIT TRANSACTION here always fails: SET XACT_ABORT ON dooms the transaction opened at line {finding.BeginTransactionLine} the instant an error is caught by this CATCH block, and a doomed transaction cannot be committed (Msg 3930) - only ROLLBACK is possible.",
            _ =>
                $"BEGIN TRANSACTION at line {finding.BeginTransactionLine} reaches this point with no intervening COMMIT/ROLLBACK - @@TRANCOUNT is left elevated by one on this path, holding its locks until the session or connection pool eventually clears it.",
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.UnresolvedExitLine, finding.UnresolvedExitColumn);
    }

    private static SarifResult ToResult(OutputParameterFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.OutputParameterRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message =
            $"OUTPUT parameter '{finding.ParameterName}' is not assigned on this path - the caller's own variable is left completely unchanged (not reset to NULL), so a reused caller variable can silently read stale data from a previous call.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.UnresolvedExitLine, finding.UnresolvedExitColumn);
    }

    private static SarifResult ToResult(DatabaseConfigurationFinding finding)
    {

        var (ruleId, level, message) = finding.Kind switch
        {
            DatabaseConfigurationFindingKind.PageVerifyNotChecksum => (
                SarifRuleCatalog.DatabaseConfigurationRuleId(finding.Kind), LevelWarning,
                "PAGE_VERIFY is not CHECKSUM - silent storage-level page corruption can go undetected until a much later, harder-to-diagnose failure."),
            DatabaseConfigurationFindingKind.AutoShrinkOn => (
                SarifRuleCatalog.DatabaseConfigurationRuleId(finding.Kind), LevelWarning,
                "AUTO_SHRINK is ON - a well-known, severe anti-pattern: the engine repeatedly shrinks the file and the workload immediately re-grows it, causing constant fragmentation churn for no durable space saving."),
            DatabaseConfigurationFindingKind.AutoCloseOn => (
                SarifRuleCatalog.DatabaseConfigurationRuleId(finding.Kind), LevelWarning,
                "AUTO_CLOSE is ON - the database's connection/buffer-pool state is torn down after the last connection closes and rebuilt from scratch on the next one, adding real latency to whichever connection happens to be first."),
            DatabaseConfigurationFindingKind.TargetRecoveryTimeUnset => (
                SarifRuleCatalog.DatabaseConfigurationRuleId(finding.Kind), LevelWarning,
                "TARGET_RECOVERY_TIME is 0 (disabled) - indirect checkpoint is off, falling back to the legacy automatic-checkpoint mechanism instead of a bounded, predictable crash-recovery time; the engine's own modern default is 60 seconds."),
            DatabaseConfigurationFindingKind.QueryStoreNotReadWrite => (
                SarifRuleCatalog.DatabaseConfigurationRuleId(finding.Kind), LevelNote,
                "Query Store is not actively running (actual state is not READ_WRITE) - the engine's own built-in plan-regression/history diagnostic is unavailable for this database. Informational: whether Query Store should be on is a real operational choice, not a universal anti-pattern."),
            DatabaseConfigurationFindingKind.QueryStoreCaptureModeNotAuto => (
                SarifRuleCatalog.DatabaseConfigurationRuleId(finding.Kind), LevelNote,
                "Query Store is running with a capture mode other than AUTO - informational only: ALL is a real, deliberate choice some teams prefer for active troubleshooting, not a mistake."),
            DatabaseConfigurationFindingKind.AutoCreateStatisticsOff => (
                SarifRuleCatalog.DatabaseConfigurationRuleId(finding.Kind), LevelWarning,
                "AUTO_CREATE_STATISTICS is OFF - the optimizer can no longer create a missing single-column statistics object on demand, so a predicate against an unstatted column compiles against a guessed cardinality instead of a real histogram."),
            DatabaseConfigurationFindingKind.AutoUpdateStatisticsOff => (
                SarifRuleCatalog.DatabaseConfigurationRuleId(finding.Kind), LevelWarning,
                "AUTO_UPDATE_STATISTICS is OFF - statistics never refresh as the underlying data changes, so every plan compiled against them drifts further from reality the longer the database runs."),
            DatabaseConfigurationFindingKind.CompatibilityLevelBehindEngineDefault => (
                SarifRuleCatalog.DatabaseConfigurationRuleId(finding.Kind), LevelWarning,
                "The database's compatibility level is behind the connected engine instance's own current default (read live from the model system database) - it is silently kept on an older cardinality estimator and query-optimizer behavior nobody chose on purpose."),
            DatabaseConfigurationFindingKind.SpatialPersistedComputedColumnDisabledOnCompatibilityLevelChange => (
                SarifRuleCatalog.DatabaseConfigurationRuleId(finding.Kind), LevelWarning,
                $"{finding.AffectedObjectName} depends on {finding.Dependency}; SQL Server's own compatibility-change DMV reports that it will be disabled at compatibility level {finding.TargetCompatibilityLevel}."),
            DatabaseConfigurationFindingKind.PlanGuideAltersOptimization => (
                SarifRuleCatalog.DatabaseConfigurationRuleId(finding.Kind), LevelNote,
                $"Plan guide '{finding.AffectedObjectName}' is enabled (scope {finding.PlanGuideScopeType}) and carries hints '{finding.PlanGuideHints}' - informational: a real, deliberate operational tool, not a mistake by construction."),
            _ => throw new ArgumentOutOfRangeException(nameof(finding)),
        };

        return BuildResult(ruleId, FloorLevelForConfidence(level, finding.Confidence), message, finding.DatabaseName, 1, null);
    }

    private static SarifResult ToResult(CompositeIndexLeadingColumnFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CompositeIndexLeadingColumnRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var indexLabel = finding.IndexName ?? "(unnamed index)";
        var message =
            $"Index {indexLabel} on {finding.TableQualifiedName} is keyed ({string.Join(", ", finding.IndexKeyColumns)}) - this query constrains {finding.ViolatingColumnName} (key position {finding.ViolatingColumnPosition}) but never binds the leading key column {finding.IndexKeyColumns[0]} anywhere in the statement, and no other index on this table leads with {finding.ViolatingColumnName} either, so nothing here can seek {finding.ViolatingColumnName} through a real index.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(MissingStatisticsFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.MissingStatisticsRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message =
            $"{finding.TableQualifiedName}.{finding.ColumnName} is constrained here by a resolved predicate, but no statistic on {finding.TableQualifiedName} covers it (single-column, or leading key of a multi-column statistic) - and the connected database has AUTO_CREATE_STATISTICS turned off, so the engine cannot create one on its own.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(IndexHintFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.IndexHintRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind switch
        {
            IndexHintFindingKind.IndexDoesNotExist =>
                $"INDEX hint on {finding.TableQualifiedName} names '{finding.HintedIndexName}', which does not exist in the catalog - oracle-confirmed this is a hard compile error (Msg 308) every time this statement runs.",
            IndexHintFindingKind.HintedIndexNotSeekable =>
                $"INDEX hint on {finding.TableQualifiedName} forces index '{finding.HintedIndexName}', whose leading key column {finding.LeadingColumnName} is never bound anywhere in this statement - oracle-confirmed this degrades the forced index to a full scan instead of a seek.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, null),
        };
        var level = finding.Kind == IndexHintFindingKind.IndexDoesNotExist
            ? FloorLevelForConfidence(LevelError, finding.Confidence)
            : FloorLevelForConfidence(LevelWarning, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(SessionDateSettingFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SessionDateSettingRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind switch
        {
            SessionDateSettingKind.DateFormat =>
                "SET DATEFORMAT changes how a string date literal is interpreted for the rest of this session - oracle-confirmed the identical ambiguous literal resolves to a different date depending on which value was set first.",
            SessionDateSettingKind.DateFirst =>
                "SET DATEFIRST changes what DATEPART(weekday, ...) returns for the rest of this session - oracle-confirmed the identical date returns a different weekday ordinal depending on which value was set first.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, null),
        };
        return BuildResult(ruleId, FloorLevelForConfidence(LevelNote, finding.Confidence), message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(AmbiguousDateLiteralConversionFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.AmbiguousDateLiteralConversionRuleId, finding.Confidence);
        var message = $"'{finding.LiteralText}' is cast/converted to a date/time type with no explicit style - oracle-confirmed the identical ambiguous literal resolves to a different real date depending purely on the session's own DATEFORMAT/LANGUAGE, with no SET DATEFORMAT statement required in this module.";
        return BuildResult(ruleId, FloorLevelForConfidence(LevelNote, finding.Confidence), message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(CartesianJoinFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CartesianJoinRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind switch
        {
            CartesianJoinKind.AlwaysFalseInnerJoinPredicate =>
                $"The INNER JOIN between {finding.FirstTableQualifiedName} and {finding.SecondTableQualifiedName} has an ON predicate that provably never evaluates to TRUE - this join can never match any row.",
            CartesianJoinKind.JoinPredicateEmptyWithWhereClause =>
                $"The INNER JOIN between {finding.FirstTableQualifiedName} and {finding.SecondTableQualifiedName} has an ON predicate that, combined with the statement's WHERE clause, provably never holds at the same time - this join can never match any row.",
            CartesianJoinKind.ExplicitCrossJoin =>
                $"{finding.FirstTableQualifiedName} and {finding.SecondTableQualifiedName} are combined via a CROSS JOIN with no predicate anywhere in the statement connecting the two - a true cartesian product.",
            _ =>
                $"{finding.FirstTableQualifiedName} and {finding.SecondTableQualifiedName} are combined via a comma-join with no predicate anywhere in the statement connecting the two - a true cartesian product.",
        };
        return BuildResult(ruleId, FloorLevelForConfidence(LevelWarning, finding.Confidence), message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(TruncateSwallowedFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TruncateSwallowedRuleId, finding.Confidence);
        var message = "TRUNCATE TABLE inside a TRY block whose CATCH never THROWs/RAISERRORs - oracle-confirmed a TRUNCATE failure (e.g. an enforced FK reference, Msg 4712) is silently swallowed here with no error reaching the caller.";
        return BuildResult(ruleId, FloorLevelForConfidence(LevelWarning, finding.Confidence), message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(UnindexedTempTableUsageFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.UnindexedTempTableUsageRuleId(finding.Kind), finding.Confidence);
        var usageText = finding.Kind == UnindexedTempTableUsageKind.JoinOperand ? "joined" : "filtered by a WHERE predicate";
        var message = $"{finding.TempTableQualifiedName} is SELECT...INTO'd and later {usageText}, but no index was ever created on it - oracle-confirmed this forces a full scan of the temp table with no seek alternative possible.";
        return BuildResult(ruleId, FloorLevelForConfidence(LevelWarning, finding.Confidence), message, finding.SourcePath, finding.UsageLine, finding.UsageColumn);
    }

    private static SarifResult ToResult(ViewOrderingFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ViewOrderingRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind switch
        {

            ViewOrderingFindingKind.TopPercentOrderByNeverLimits =>
                $"'{finding.ObjectQualifiedName}' uses TOP (100) PERCENT ... ORDER BY - 100 PERCENT never excludes a row, so this ORDER BY exists only to satisfy T-SQL's view-ordering grammar rule and is not guaranteed to any consumer that doesn't apply its own ORDER BY.",

            ViewOrderingFindingKind.OrderByNotGuaranteedToConsumer =>
                $"'{finding.ObjectQualifiedName}' uses a row-limiting TOP/OFFSET ... ORDER BY - the ORDER BY does decide which rows survive, but the final output order is not guaranteed to a consumer that doesn't apply its own ORDER BY.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, null),
        };
        var baseLevel = finding.Kind == ViewOrderingFindingKind.TopPercentOrderByNeverLimits ? LevelWarning : LevelNote;
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(MultiReferencedCteFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.MultiReferencedCteRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"CTE '{finding.CteName}' is referenced {finding.ReferenceCount} times downstream of its own WITH clause - each reference independently re-runs the CTE's own defining query, SQL Server does not materialize it once and reuse it.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(RecursiveCteAnchorTypeMismatchFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.RecursiveCteAnchorTypeMismatchRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"Recursive CTE '{finding.CteName}' column '{finding.ColumnName}' resolves to {finding.RecursiveTypeDisplay} in the recursive member but {finding.AnchorTypeDisplay} in the anchor member - SQL Server rejects this at compile time (Msg 240).";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(NestedViewDepthFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NestedViewDepthRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var chain = string.Join(" -> ", finding.Chain);
        var message = $"'{finding.ViewQualifiedName}' nests {finding.Depth} view/TVF layers deep before reaching a base table: {chain} -> [{string.Join(", ", finding.BaseTables)}].";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(PostExpansionJoinWidthFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.PostExpansionJoinWidthRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var unexpandedNote = finding.PartiallyUnexpanded ? " (partially unexpanded - the real count may be higher)" : string.Empty;
        var message = $"'{finding.ModuleQualifiedName}' writes {finding.WrittenCount} FROM/JOIN reference(s) but expands to {finding.ExpandedCount} base table(s) via [{string.Join(", ", finding.InflatingSources)}]{unexpandedNote}.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(SelectStarViewFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SelectStarViewRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"'{finding.ViewQualifiedName}' (SELECT * at line {finding.ViewLine}, {finding.ViewFullColumns.Count} columns, {finding.ViewDepth} view/TVF layer(s) deep) is consumed here selecting only [{string.Join(", ", finding.ConsumerSelectedColumns)}] - the view's frozen column list forces the full width regardless.";

        return BuildResult(ruleId, level, message, finding.ConsumerSourcePath, finding.ConsumerLine, startColumn: null);
    }

    private static SarifResult ToResult(UnparameterizedDynamicSqlFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.UnparameterizedDynamicSqlRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = finding.Kind switch
        {
            UnparameterizedDynamicSqlFindingKind.ExecStringConcatenatesParameterizableValue =>
                "This EXEC(string) call concatenates a proven-constant value into its SQL text instead of passing it through sp_executesql's own @params - each distinct value compiles its own cached plan.",
            _ => "This dynamic SQL call concatenates a proven-constant value into its SQL text rather than a single fixed literal - each distinct value compiles its own cached plan.",
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(NonPersistedComputedColumnFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NonPersistedComputedColumnRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = finding.IsCoveredByIndex
            ? $"'{finding.TableQualifiedName}.{finding.ColumnName}' is a non-persisted computed column ({finding.DefinitionText}) - recomputed from the base row on every read served from the base table or from an index that doesn't store it; an index on this table already stores {finding.ColumnName}, so reads served through that index avoid the recompute, but only when the optimizer actually chooses it."
            : $"'{finding.TableQualifiedName}.{finding.ColumnName}' is a non-persisted computed column ({finding.DefinitionText}) - recomputed from the base row on every read that touches it.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(IndexDesignFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.IndexDesignRuleId(finding.Kind), finding.Confidence);

        var baseLevel = finding.Kind switch
        {
            IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable => LevelWarning,
            IndexDesignFindingKind.MonotonicClusteredKeyMissingSequentialOptimization => LevelWarning,
            IndexDesignFindingKind.TimestampColumnNaming => LevelNote,
            _ => LevelError,
        };
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(IdentityRangeFinding finding)
    {

        var baseLevel = finding.Kind == IdentityRangeFindingKind.IdentityRangeNearExhaustion ? LevelError : LevelNote;
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.IdentityRangeRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(FloatEqualityFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.FloatEqualityRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' ({finding.TypeDisplay}) is compared with = in this predicate - IEEE-754 floating-point representation error means two values a person would call the same number can compare unequal, silently returning the wrong rows.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(FloatOrderDependentAggregateFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.FloatOrderDependentAggregateRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' ({finding.TypeDisplay}) is passed to {finding.AggregateFunctionName}() - this aggregate's running result accumulates in an order that depends on plan shape, so the identical aggregate over identical data can return a different bit pattern across runs.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(DynamicDataMaskingFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.DynamicDataMaskingRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = finding.Kind == DynamicDataMaskingFindingKind.PredicateExposure
            ? $"'{finding.TableQualifiedName}.{finding.ColumnName}' (masked with {finding.MaskingFunctionName}()) is used in a {finding.ContextDescription} - the engine evaluates this against the real underlying value regardless of masking, letting a caller without UNMASK infer the real value through the result."
            : $"'{finding.TableQualifiedName}.{finding.ColumnName}' (masked with {finding.MaskingFunctionName}()) is used inside a {finding.ContextDescription} - for a caller without UNMASK the whole expression's result silently collapses to the masking function's fixed sentinel instead of a real computed value.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(AlwaysEncryptedOrderByFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.AlwaysEncryptedOrderByRuleId, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' ({finding.EncryptionTypeDisplay}) is referenced in this ORDER BY clause - an Always Encrypted column can never be sorted on; the statement does not compile.";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(AlwaysEncryptedAssignmentMismatchFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.AlwaysEncryptedAssignmentMismatchRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind == AlwaysEncryptedAssignmentMismatchKind.LiteralSource
            ? $"'{finding.TargetTableQualifiedName}.{finding.TargetColumnName}' ({finding.TargetEncryptionTypeDisplay}) is assigned a literal value directly - the server cannot encrypt a plaintext literal without a column-encryption-aware client; the statement does not compile."
            : $"'{finding.TargetTableQualifiedName}.{finding.TargetColumnName}' ({finding.TargetEncryptionTypeDisplay}) is assigned from '{finding.SourceTableQualifiedName}.{finding.SourceColumnName}' ({finding.SourceEncryptionTypeDisplay}) - the Always Encrypted state differs between source and target; the statement does not compile.";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(RestrictedImplicitAssignmentFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.RestrictedImplicitAssignmentRuleId, finding.Confidence);
        var message = $"'{finding.TargetVariableName}' ({finding.TargetTypeDisplay}) is assigned directly from '{finding.SourceVariableName}' ({finding.SourceTypeDisplay}) - no implicit conversion exists between these types; the statement does not compile.";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(RevertCookieTypeMismatchFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.RevertCookieTypeMismatchRuleId, finding.Confidence);
        var message = $"REVERT WITH COOKIE references '{finding.CookieVariableName}' ({finding.CookieTypeDisplay}), not varbinary(100) - the engine only accepts the fixed varbinary(100) cookie shape; the statement does not compile.";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ForXmlExplicitInlineXsdFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ForXmlExplicitInlineXsdRuleId, finding.Confidence);
        const string message = "FOR XML EXPLICIT is combined with XMLSCHEMA - this combination does not compile.";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(AlwaysEncryptedKeyColumnFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.AlwaysEncryptedKeyColumnRuleId, finding.Confidence);
        var objectKind = finding.Kind switch
        {
            AlwaysEncryptedKeyColumnKind.PrimaryKey => "PRIMARY KEY constraint",
            AlwaysEncryptedKeyColumnKind.UniqueConstraint => "UNIQUE constraint",
            AlwaysEncryptedKeyColumnKind.Statistics => "statistics object",
            _ => "index",
        };
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' is a key column of {objectKind} '{finding.ObjectName}' - the column is RANDOMIZED-encrypted with a column encryption key whose column master key was not declared with ENCLAVE_COMPUTATIONS, so it cannot be used as a key column in a constraint, index, or statistics; the statement does not deploy.";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(AlwaysEncryptedUnsupportedColumnFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.AlwaysEncryptedUnsupportedColumnRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind switch
        {
            AlwaysEncryptedUnsupportedColumnKind.UnsupportedDataType =>
                $"'{finding.TableQualifiedName}.{finding.ColumnName}' is declared ENCRYPTED WITH on data type {finding.TypeDisplay} - this data type is not supported for encryption; the statement does not deploy (Msg 33280).",
            AlwaysEncryptedUnsupportedColumnKind.IdentityColumn =>
                $"'{finding.TableQualifiedName}.{finding.ColumnName}' is an IDENTITY column declared ENCRYPTED WITH - an identity column must be unencrypted; the statement does not deploy (Msg 2749).",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled AlwaysEncryptedUnsupportedColumnKind."),
        };

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(AlterColumnSafetyFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.AlterColumnSafetyRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind switch
        {
            AlterColumnSafetyKind.PrecisionOrScaleNarrowing =>
                $"'{finding.TableQualifiedName}.{finding.ColumnName}' is narrowed from {finding.PreviousType} to {finding.NewType} - this fails at DDL time if an existing value no longer fits, or silently rounds away digits past the new scale if it does.",
            AlterColumnSafetyKind.IncompatibleFamilyConversion =>
                $"'{finding.TableQualifiedName}.{finding.ColumnName}' is retyped from {finding.PreviousType} to {finding.NewType} - there is no implicit conversion between the character and binary families, and ALTER COLUMN has no syntax to carry an explicit CONVERT; the statement does not compile.",
            AlterColumnSafetyKind.TemporalOffsetDropped =>
                $"'{finding.TableQualifiedName}.{finding.ColumnName}' is retyped from {finding.PreviousType} to {finding.NewType} - the UTC offset is silently dropped, keeping the local date/time digits unchanged rather than normalizing to UTC.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled AlterColumnSafetyKind."),
        };

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(DropProtectedObjectFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.DropProtectedObjectRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind switch
        {
            DropProtectedObjectKind.SchemaNotEmpty =>
                $"DROP SCHEMA '{finding.ObjectName}' fails (Msg 3729) because at least one object still references the schema - every object in the schema must be dropped or moved first.",
            DropProtectedObjectKind.FixedDatabaseRole =>
                $"DROP ROLE '{finding.ObjectName}' fails (Msg 15150) because it names one of the engine's fixed database roles, which can never be dropped.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled DropProtectedObjectKind."),
        };

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(OnlineRebuildLegacyLobFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.OnlineRebuildLegacyLobRuleId(finding.Kind), finding.Confidence);
        var statementLabel = finding.Kind switch
        {
            OnlineRebuildLegacyLobKind.AlterTableRebuild => "ALTER TABLE ... REBUILD WITH (ONLINE = ON)",
            OnlineRebuildLegacyLobKind.AlterIndexAllRebuild => "ALTER INDEX ALL ... REBUILD WITH (ONLINE = ON)",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled OnlineRebuildLegacyLobKind."),
        };
        var message = $"{statementLabel} on '{finding.TableQualifiedName}' fails (Msg 2725) because column '{finding.ColumnName}' is {finding.TypeDisplay} - text, ntext, image, and FILESTREAM columns can never be carried through an online index rebuild.";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(OperandComparabilityFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.OperandComparabilityRuleId(finding.Kind), finding.Confidence);
        var typeLabel = finding.Kind switch
        {
            OperandComparabilityFindingKind.Xml => "xml",
            OperandComparabilityFindingKind.Json => "json",
            OperandComparabilityFindingKind.Spatial => "geometry/geography",
            _ => "text/ntext/image",
        };
        var positionText = finding.Context switch
        {
            OperandComparabilityContext.Comparison => $"compared with {finding.OperatorText} in this predicate",
            OperandComparabilityContext.In => "used in an IN list",
            OperandComparabilityContext.Between => "used in a BETWEEN",
            OperandComparabilityContext.NullIf => "used in a NULLIF",
            OperandComparabilityContext.OrderBy => "referenced in this ORDER BY clause",
            OperandComparabilityContext.GroupBy => "referenced in this GROUP BY clause",
            OperandComparabilityContext.Distinct => "selected under SELECT DISTINCT",
            OperandComparabilityContext.PartitionBy => "referenced in this window function's PARTITION BY clause",
            _ => "used in a comparison",
        };
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' ({finding.TypeDisplay}) is {positionText} - the {typeLabel} data type is not comparable here; the statement does not compile.";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(VectorFunctionArgumentFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.VectorFunctionArgumentRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind == VectorFunctionArgumentFindingKind.DimensionMismatch
            ? $"{finding.FunctionName}'s two vector arguments declare different dimensions ({finding.TypeDisplay} vs {finding.OtherTypeDisplay}) - the vector dimensions do not match; the call fails at execution for every row (Msg 42204)."
            : $"{finding.FunctionName}'s {finding.ArgumentDescription} is {finding.TypeDisplay}, not a VECTOR(n) value - the statement does not compile (Msg 8116).";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(SchemaWithRejectedTypeFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SchemaWithRejectedTypeRuleId(finding.Kind), finding.Confidence);
        var clauseText = finding.Kind == SchemaWithRejectedTypeKind.OpenXmlClrType ? "OPENXML ... WITH" : "OPENROWSET(BULK ...) inline-schema WITH";
        var message = $"{clauseText} schema column '{finding.ColumnName}' is declared {finding.TypeDisplay} - this clause's fixed type gate always rejects this type.";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ExecuteAtLargeObjectParameterFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ExecuteAtLargeObjectParameterRuleId(finding.Kind), finding.Confidence);
        var message = finding.Kind == ExecuteAtLargeObjectParameterFindingKind.CrashesSession
            ? $"Parameter @{finding.VariableName} ({finding.TypeDisplay}) is passed to EXECUTE (...) AT - a large-object-typed parameter here crashes the connection with an internal assertion failure (Msg 3624), not a clean error."
            : $"Parameter @{finding.VariableName} ({finding.TypeDisplay}) is passed to EXECUTE (...) AT - the xml data type is not supported as a parameter to remote calls (Msg 9512).";

        return BuildResult(ruleId, LevelError, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(TriggerOrderFinding finding)
    {

        var triggerList = string.Join(", ", finding.UnorderedTriggerNames);
        var message = $"'{finding.TableQualifiedName}' has {finding.UnorderedTriggerNames.Count} AFTER {finding.EventTypeDescription} triggers with no sp_settriggerorder pin between them ({triggerList}) - their relative firing order is undefined by the engine.";
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TriggerOrderRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(QueryAntiPatternFinding finding)
    {

        var baseLevel = finding.Kind switch
        {
            QueryAntiPatternFindingKind.TableVariableLowCompatEstimate => LevelError,
            QueryAntiPatternFindingKind.CountStarVariableExistenceCheck => LevelError,
            QueryAntiPatternFindingKind.NonAggregateHavingPredicate => LevelWarning,

            QueryAntiPatternFindingKind.MergeNonUniqueUsingSource => LevelError,
            QueryAntiPatternFindingKind.RecursiveCteMissingMaxRecursion => LevelError,
            QueryAntiPatternFindingKind.GroupingSetsCardinalityLimitExceeded => LevelError,
            QueryAntiPatternFindingKind.GroupingArgumentNotInGroupByList => LevelError,
            _ => LevelWarning,
        };
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.QueryAntiPatternRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(IndexCoverageFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.IndexCoverageRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}' via index '{finding.IndexName ?? "<unnamed>"}' ({string.Join(", ", finding.IndexKeyColumns)}) does not cover ({string.Join(", ", finding.UncoveredColumns)}) - a matched row needs a Key/RID Lookup back to the base table.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(TriggerCorrectnessFinding finding)
    {

        var baseLevel = finding.Kind switch
        {
            TriggerCorrectnessFindingKind.MultiRowUnsafeSingleRowAssignment => LevelError,
            TriggerCorrectnessFindingKind.MultiRowUnsafeKeyedDml => LevelError,
            TriggerCorrectnessFindingKind.NoEarlyOutForEmptyInvocation => LevelNote,
            TriggerCorrectnessFindingKind.DirectRecursiveTrigger => LevelWarning,
            TriggerCorrectnessFindingKind.InsteadOfInsertFilteredNoRejectPath => LevelError,
            TriggerCorrectnessFindingKind.UpdateFunctionWithoutValueComparison => LevelWarning,
            TriggerCorrectnessFindingKind.LogonTriggerHostNameGate => LevelError,
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled TriggerCorrectnessFindingKind."),
        };
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TriggerCorrectnessRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(CrossModuleLockOrderFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CrossModuleLockOrderRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var first = finding.FirstTableFirstOrdering;
        var second = finding.SecondTableFirstOrdering;
        var message =
            $"'{first.ProcedureQualifiedName}' ({first.SourcePath}:{first.FirstWriteLine}) writes '{finding.FirstTableQualifiedName}' then '{finding.SecondTableQualifiedName}' (line {first.SecondWriteLine}) inside an explicit transaction, but " +
            $"'{second.ProcedureQualifiedName}' ({second.SourcePath}:{second.SecondWriteLine}) writes them in the opposite order ('{finding.SecondTableQualifiedName}' at line {second.SecondWriteLine} then '{finding.FirstTableQualifiedName}' at line {second.FirstWriteLine}) - the textbook cross-session deadlock shape.";

        return BuildResult(ruleId, level, message, first.SourcePath, first.ProcedureLine, startColumn: null);
    }

    private static SarifResult ToResult(TriggerRecursionCycleFinding finding)
    {

        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TriggerRecursionCycleRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var firstHop = finding.Hops[0];
        var cycle = string.Join(" -> ", finding.CycleTableQualifiedNames) + " -> " + finding.CycleTableQualifiedNames[0];
        var message = $"Trigger recursion cycle across tables: {cycle} - '{firstHop.TriggerQualifiedName}' ({firstHop.SourcePath}:{firstHop.WriteLine}) writes '{firstHop.ToTableQualifiedName}', and the cycle closes back to '{firstHop.FromTableQualifiedName}' through {finding.Hops.Count} trigger hop(s), live-confirmed reachable while the server's own 'nested triggers' option is on.";

        return BuildResult(ruleId, level, message, firstHop.SourcePath, firstHop.TriggerLine, startColumn: null);
    }

    private static SarifResult ToResult(TempTableExecShapeFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TempTableExecShapeRuleId(finding.Kind), finding.Confidence);

        if (finding.Kind == TempTableExecShapeFindingKind.ColumnCountMismatch)
        {

            var level = FloorLevelForConfidence(LevelError, finding.Confidence);
            var message = $"INSERT INTO {finding.TempTableQualifiedName} EXEC {finding.ExecutedProcQualifiedName}: the INSERT targets {finding.TempTableDeclaredColumnCount} column(s) but the executed proc's real result set describes {finding.DescribedColumnCount} - this raises a hard error (Msg 213/8164) every time it runs.";
            return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
        }

        var typeLevel = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var typeMessage = $"INSERT INTO {finding.TempTableQualifiedName} EXEC {finding.ExecutedProcQualifiedName}: position {finding.ColumnPosition} ('{finding.ColumnName}', {finding.TempColumnTypeDisplay}) receives {finding.DescribedColumnTypeDisplay} from the executed proc's real result set - {DescribeWriteLossKind(finding.WriteLoss!.Value)}.";
        return BuildResult(ruleId, typeLevel, typeMessage, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ExecResultSetsShapeFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ExecResultSetsShapeRuleId(finding.Kind), finding.Confidence);

        if (finding.Kind == ExecResultSetsShapeFindingKind.ColumnCountMismatch)
        {

            var level = FloorLevelForConfidence(LevelError, finding.Confidence);
            var message = $"EXEC {finding.ExecutedProcQualifiedName} WITH RESULT SETS declares {finding.DeclaredColumnCount} column(s) but the executed proc's real result set describes {finding.DescribedColumnCount} - this raises a hard error (Msg 11537) every time it runs.";
            return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
        }

        var typeLevel = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var typeMessage = $"EXEC {finding.ExecutedProcQualifiedName} WITH RESULT SETS: position {finding.ColumnPosition} ('{finding.ColumnName}', {finding.DeclaredColumnTypeDisplay}) receives {finding.DescribedColumnTypeDisplay} from the executed proc's real result set - {DescribeWriteLossKind(finding.WriteLoss!.Value)}.";
        return BuildResult(ruleId, typeLevel, typeMessage, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(WriteLossFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.WriteLossRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var target = finding.TableQualifiedName is { } table ? $"{table}.{finding.ColumnName}" : finding.ColumnName;
        var message = $"'{target}' ({finding.TargetType}) is assigned a {finding.SourceType} value - {DescribeWriteLossKind(finding.Kind)}.{DynamicSqlOriginNote(finding.DynamicSqlCallSite)}";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.ColumnPosition);
    }

    private static SarifResult ToResult(TvfFenceFinding finding)
    {

        var level = finding.Kind switch
        {
            TvfFenceFindingKind.CorrelatedApply or TvfFenceFindingKind.NestedUnderViewOrTvf => LevelError,
            TvfFenceFindingKind.FromOrJoin or TvfFenceFindingKind.InsertExec => LevelWarning,
            TvfFenceFindingKind.Standalone => LevelNote,
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled TvfFenceFindingKind."),
        };
        level = FloorLevelForConfidence(level, finding.Confidence);
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TvfFenceRuleId(finding.Kind), finding.Confidence);

        var message = finding.Kind switch
        {
            TvfFenceFindingKind.CorrelatedApply =>
                $"'{finding.FunctionQualifiedName}' ({finding.FunctionKind}) is CROSS/OUTER APPLYed with an argument correlated to {string.Join(", ", finding.CorrelatedOuterColumns ?? [])} - the body re-executes once per outer row; interleaved execution does not rescue this.",
            TvfFenceFindingKind.NestedUnderViewOrTvf =>
                $"'{finding.ReferencedObjectQualifiedName}' inherits an optimization fence from '{finding.FunctionQualifiedName}' ({finding.FunctionKind}) {finding.Depth} layer(s) down, introduced at {finding.OriginSourcePath}:{finding.OriginLine}.",
            TvfFenceFindingKind.FromOrJoin =>
                $"'{finding.FunctionQualifiedName}' ({finding.FunctionKind}) is referenced directly in FROM/JOIN - the optimizer cannot see into its body and estimates a fixed row count.",
            TvfFenceFindingKind.InsertExec =>
                $"INSERT ... EXEC '{finding.ReferencedObjectQualifiedName}' forces the procedure's entire result set to be spooled to a worktable before insertion.",
            TvfFenceFindingKind.Standalone =>
                $"'{finding.FunctionQualifiedName}' ({finding.FunctionKind}) is referenced standalone - the fence is real but nothing surrounds it for the fixed estimate to poison.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled TvfFenceFindingKind."),
        };
        message += DynamicSqlOriginNote(finding.DynamicSqlCallSite);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(ScalarUdfFinding finding)
    {

        var level = finding.Kind switch
        {
            ScalarUdfFindingKind.PredicateInvocation or ScalarUdfFindingKind.NestedUnderViewOrTvf => LevelError,
            ScalarUdfFindingKind.SchemaDependency => LevelWarning,
            ScalarUdfFindingKind.ProjectionInvocation => LevelNote,
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled ScalarUdfFindingKind."),
        };

        if (finding.Inlineability == ScalarUdfInlineability.Inlineable)
        {
            level = DowngradeOneLevel(level);
        }

        level = FloorLevelForConfidence(level, finding.Confidence);
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ScalarUdfRuleId(finding.Kind), finding.Confidence);

        var inlineNote = finding.Inlineability switch
        {
            ScalarUdfInlineability.Inlineable => " (inlined under SQL 2019+ FROID)",
            ScalarUdfInlineability.NotInlineable => finding.InlineabilityBlocker is { } blocker ? $" (not inlineable: {blocker})" : " (not inlineable)",
            _ => string.Empty,
        };
        var clrNote = finding.UdfKind == ScalarUdfKind.Clr
            ? finding.ClrDataAccess switch { true => " [CLR, data access]", false => " [CLR, no data access]", _ => " [CLR]" }
            : string.Empty;
        var foldNote = finding.ConstantArgumentsNotFolded ? " - non-schemabound, so even literal arguments are not constant-folded" : string.Empty;

        var message = finding.Kind switch
        {
            ScalarUdfFindingKind.PredicateInvocation =>
                $"Scalar UDF '{finding.FunctionQualifiedName}' is called in a {finding.Context} predicate - per-row execution, non-sargable{inlineNote}{clrNote}{foldNote}.",
            ScalarUdfFindingKind.NestedUnderViewOrTvf =>
                $"'{finding.ReferencedObjectQualifiedName}' inherits scalar UDF '{finding.FunctionQualifiedName}' {finding.Depth} layer(s) down, introduced at {finding.OriginSourcePath}:{finding.OriginLine} ({finding.Context}).",
            ScalarUdfFindingKind.SchemaDependency =>
                $"'{finding.ReferencedObjectQualifiedName}' has a {finding.SchemaDependencyKind} whose definition calls scalar UDF '{finding.FunctionQualifiedName}' - every query touching the table pays this cost{inlineNote}{clrNote}.",
            ScalarUdfFindingKind.ProjectionInvocation =>
                $"Scalar UDF '{finding.FunctionQualifiedName}' is called in {finding.Context} - per-row execution{inlineNote}{clrNote}{foldNote}.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled ScalarUdfFindingKind."),
        };
        message += DynamicSqlOriginNote(finding.DynamicSqlCallSite);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(DynamicSqlFinding finding)
    {
        var ruleId = SarifRuleCatalog.DynamicSqlRuleId(finding.Outcome);
        var level = finding.Outcome == DynamicSqlOutcome.AnalyzedLiteral ? LevelNote : LevelWarning;

        var message = finding.Outcome switch
        {
            DynamicSqlOutcome.AnalyzedLiteral =>
                "Dynamic SQL call with a provably-constant argument; its contents were reparsed and analyzed like static SQL.",
            DynamicSqlOutcome.InnerParseFailed =>
                $"Dynamic SQL call's argument was provably constant but did not parse as T-SQL ({finding.Reason}).",
            DynamicSqlOutcome.PartiallyAnalyzed =>
                "Dynamic SQL call's argument contained a whole optional clause/fragment of unknown content; the surrounding query structure was analyzed, but that fragment was not.",
            _ => $"Dynamic SQL call's argument could not be statically analyzed ({finding.Reason}).",
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static string DescribeTouchedObjectForSetOption(SetOptionFinding finding)
    {
        if (finding.TouchedObjectQualifiedName is not { } touched)
        {
            return string.Empty;
        }

        var featureKind = finding.TouchedIsIndexedView ? "indexed view" : "filtered index";
        var indexSuffix = finding.TouchedIndexName is { } idx ? $".{idx}" : string.Empty;
        return $" - touches {featureKind} '{touched}'{indexSuffix}";
    }

    private static string IdentityColumnCheckMessage(CheckConstraintFinding finding)
    {
        var prefix = $"'{finding.ConstraintName}' (CHECK on '{finding.TableQualifiedName}.{finding.ColumnName}') references the IDENTITY column '{finding.ColumnName}' directly - the identity counter advances even on a failed insert (Msg 547), so the value it reserves for a rejected row is never reused.";

        return finding.ThresholdDirection switch
        {
            IdentityCheckThresholdDirection.Increasing =>
                $"{prefix} Every insert whose auto-generated identity value doesn't yet satisfy this predicate fails deterministically, consuming the identity counter on each failed attempt, until the counter catches up and failures silently stop forever.",
            IdentityCheckThresholdDirection.Decreasing =>
                $"{prefix} Inserts succeed only while the identity counter is still below this threshold; once the counter passes it, every subsequent insert fails deterministically and permanently, with no code change and no way for that direction to ever pass again.",
            _ =>
                $"{prefix} Because the counter keeps consuming values on both successful and failed inserts, whether and when this predicate ends up permanently satisfied, permanently failing, or neither depends entirely on the predicate's own shape - it is not guaranteed to eventually stop mattering.",
        };
    }

    private static string FloorLevelForConfidence(string level, FindingConfidence confidence) =>
        confidence == FindingConfidence.High ? level : LevelNote;

    private static string DowngradeOneLevel(string level) => level switch
    {
        LevelError => LevelWarning,
        LevelWarning => LevelNote,
        _ => LevelNote,
    };

    private const string TierProven = "Proven";
    private const string TierContextual = "Contextual";
    private const string TierAdvisory = "Advisory";

    private static string DetermineTier(string level) => level switch
    {
        LevelError => TierProven,
        LevelWarning => TierContextual,
        _ => TierAdvisory,
    };

    private static string IndexedDisplay(bool? indexed) => indexed is { } value ? value.ToString() : "unknown";

    private static string DynamicSqlOriginNote(SourceSpan? callSite) =>
        callSite is { } span ? $" (via dynamic SQL executed at {span.SourcePath}:{span.Line})" : string.Empty;

    private static string DescribeTransformationSite(TransformationSite site) =>
        site.SourcePath is null ? site.Description : $"{site.Description} at {site.SourcePath}:{site.Line}";

    private static string DescribeWriteLossKind(WriteLossKind kind) => kind switch
    {
        WriteLossKind.UnicodeToNonUnicodeReplacement => "characters outside the target collation's codepage are silently replaced with '?'",
        WriteLossKind.ApproximateToExactTruncation => "the fractional part is silently dropped",
        WriteLossKind.NumericScaleNarrowing => "digits past the target's scale are silently rounded away",
        WriteLossKind.TemporalPrecisionLoss => "the time-of-day component is silently dropped",
        WriteLossKind.LengthTruncation => "characters/bytes past the target's declared length are silently dropped",
        WriteLossKind.TemporalScaleNarrowing => "fractional-second digits past the target's declared scale are silently rounded away",
        WriteLossKind.TemporalOffsetDropped => "the UTC offset is silently dropped, keeping the local date/time digits unchanged",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled WriteLossKind."),
    };

    private static string DescribeIndexNote(PredicateOperand.Column column) => column.Indexed switch
    {
        true => column.IndexName is { } indexName ? $", indexed ({indexName})" : ", indexed",
        false => ", not indexed",
        null => ", indexed status unresolved",
    };

    private static string DescribeDepth(int depth)
    {
        if (depth == 0)
        {
            return string.Empty;
        }

        var layerWord = depth == 1 ? "layer" : "layers";
        return $" (inherited through {depth} view {layerWord})";
    }

    private static SarifResult BuildResult(string ruleId, string level, string message, string sourcePath, int line, int? startColumn) =>
        new(
            ruleId,
            level,
            new SarifMessage(message),
            [new SarifLocation(new SarifPhysicalLocation(new SarifArtifactLocation(ToUri(sourcePath)), new SarifRegion(line, startColumn)))],
            new SarifResultProperties(DetermineTier(level)));

    private static string ToUri(string sourcePath)
    {
        var normalized = sourcePath.Replace('\\', '/');
        if (Path.IsPathRooted(sourcePath))
        {
            return new Uri(normalized).AbsoluteUri;
        }

        return string.Join('/', normalized.Split('/').Select(Uri.EscapeDataString));
    }
}
