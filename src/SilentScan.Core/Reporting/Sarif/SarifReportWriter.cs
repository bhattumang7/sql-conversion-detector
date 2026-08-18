using System.Text.Json;
using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting.Sarif;

/// <summary>
/// Converts a <see cref="ScanReport"/> to SARIF 2.1.0 JSON (CLAUDE.md: "SARIF export so the
/// tool doubles as a CI gate later"). Rule IDs and levels are stable across runs so CI
/// baselining/suppression works.
/// </summary>
public static class SarifReportWriter
{
    private const string ToolName = "SilentScan";

    // Read from the assembly's own version (Directory.Build.props' <Version>) rather than a
    // hardcoded literal - a hardcoded string silently stops tracking the tool's actual version
    // the moment someone forgets to update it by hand, which defeats SARIF's whole purpose of
    // letting CI baselining/suppression key off driver.version.
    private static readonly string ToolVersion =
        typeof(SarifReportWriter).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private const string LevelError = "error";
    private const string LevelWarning = "warning";
    private const string LevelNote = "note";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(ScanReport report)
    {
        var results = new List<SarifResult>();
        results.AddRange(report.Tier1Findings.Select(ToResult));
        results.AddRange(report.TypedFindings.Select(ToResult));
        results.AddRange(report.DynamicSqlFindings.Select(ToResult));
        results.AddRange(report.ExpressionDerivedFindings.Select(ToResult));
        results.AddRange(report.CollationConflictFindings.Select(ToResult));
        results.AddRange(report.WriteLossFindings.Select(ToResult));
        results.AddRange(report.TvfFenceFindings.Select(ToResult));
        results.AddRange(report.ScalarUdfFindings.Select(ToResult));
        results.AddRange(report.ColumnCollationDriftFindings.Select(ToResult));
        results.AddRange(report.CrossTableTypeDriftFindings.Select(ToResult));
        results.AddRange(report.ProcCallArgumentMismatchFindings.Select(ToResult));
        results.AddRange(report.TemporalBoundaryFindings.Select(ToResult));
        results.AddRange(report.MaxTypedColumnFindings.Select(ToResult));
        results.AddRange(report.OversizedParameterFindings.Select(ToResult));
        results.AddRange(report.UnderLengthParameterFindings.Select(ToResult));
        results.AddRange(report.AnsiPaddingMismatchFindings.Select(ToResult));
        results.AddRange(report.CatchAllPredicateFindings.Select(ToResult));
        results.AddRange(report.LocalVariablePredicateFindings.Select(ToResult));
        results.AddRange(report.FilteredIndexParameterMismatchFindings.Select(ToResult));
        results.AddRange(report.NotInNullableSubqueryFindings.Select(ToResult));
        results.AddRange(report.NonUniqueUpdateSourceFindings.Select(ToResult));
        results.AddRange(report.ForcedSerialFindings.Select(ToResult));
        results.AddRange(report.UntrustedConstraintFindings.Select(ToResult));
        results.AddRange(report.CascadingForeignKeyFindings.Select(ToResult));
        results.AddRange(report.MultiReferencedCteFindings.Select(ToResult));
        results.AddRange(report.NestedViewDepthFindings.Select(ToResult));
        results.AddRange(report.PostExpansionJoinWidthFindings.Select(ToResult));
        results.AddRange(report.SelectStarViewFindings.Select(ToResult));
        results.AddRange(report.UnparameterizedDynamicSqlFindings.Select(ToResult));
        results.AddRange(report.NonPersistedComputedColumnFindings.Select(ToResult));
        results.AddRange(report.TempTableExecShapeFindings.Select(ToResult));
        results.AddRange(report.SelfReferencingDmlFindings.Select(ToResult));
        results.AddRange(report.PartialCompositeForeignKeyJoinFindings.Select(ToResult));
        results.AddRange(report.SetOptionFindings.Select(ToResult));
        results.AddRange(report.TemporalTableHistoryIndexGapFindings.Select(ToResult));
        results.AddRange(report.ModuleCompileFlagFindings.Select(ToResult));
        results.AddRange(report.WindowFrameFindings.Select(ToResult));
        results.AddRange(report.WaitForFindings.Select(ToResult));
        results.AddRange(report.ViewOrderingFindings.Select(ToResult));
        results.AddRange(report.TransactionHygieneFindings.Select(ToResult));
        results.AddRange(report.CompositeIndexLeadingColumnFindings.Select(ToResult));
        results.AddRange(report.IndexHintFindings.Select(ToResult));
        results.AddRange(report.SessionDateSettingFindings.Select(ToResult));
        results.AddRange(report.CartesianJoinFindings.Select(ToResult));
        results.AddRange(report.UndersizedDeclarationFindings.Select(ToResult));
        results.AddRange(report.TruncateSwallowedFindings.Select(ToResult));
        results.AddRange(report.UnindexedTempTableUsageFindings.Select(ToResult));
        results.AddRange(report.OutputParameterFindings.Select(ToResult));
        results.AddRange(report.DatabaseConfigurationFindings.Select(ToResult));
        results.AddRange(report.ParameterReassignmentPredicateFindings.Select(ToResult));
        results.AddRange(report.CodeMetricFindings.Select(ToResult));
        results.AddRange(report.FormattingFindings.Select(ToResult));
        results.AddRange(report.NamingFindings.Select(ToResult));
        results.AddRange(report.DeadCodeFindings.Select(ToResult));
        results.AddRange(report.DuplicationFindings.Select(ToResult));
        results.AddRange(report.DeprecatedSyntaxFindings.Select(ToResult));
        results.AddRange(report.StatementShapeFindings.Select(ToResult));
        results.AddRange(report.ControlFlowRiskFindings.Select(ToResult));
        results.AddRange(report.SecurityFindings.Select(ToResult));
        results.AddRange(report.IndexDesignFindings.Select(ToResult));
        results.AddRange(report.IdentityRangeFindings.Select(ToResult));
        results.AddRange(report.FloatEqualityFindings.Select(ToResult));
        results.AddRange(report.QueryAntiPatternFindings.Select(ToResult));
        results.AddRange(report.IndexCoverageFindings.Select(ToResult));
        results.AddRange(report.TriggerCorrectnessFindings.Select(ToResult));
        results.AddRange(report.CrossModuleLockOrderFindings.Select(ToResult));
        results.AddRange(report.TriggerRecursionCycleFindings.Select(ToResult));
        results.AddRange(report.CheckConstraintFindings.Select(ToResult));
        results.AddRange(report.DefaultNullableConstraintFindings.Select(ToResult));
        results.AddRange(report.TryCastComputedColumnPredicateFindings.Select(ToResult));
        results.AddRange(report.StaleSelectStarViewFindings.Select(ToResult));
        results.AddRange(report.BareTopNoOrderByFindings.Select(ToResult));
        results.AddRange(report.StringConcatNullFindings.Select(ToResult));
        results.AddRange(report.AggregateDivisionColumnstoreFindings.Select(ToResult));
        results.AddRange(report.SecurityPredicateIndexFindings.Select(ToResult));

        // No public repository exists for this project yet, so informationUri (optional in
        // the SARIF spec) is omitted rather than pointed at a URL that doesn't resolve.
        var log = new SarifLog(
            "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/master/Schemata/sarif-schema-2.1.0.json",
            "2.1.0",
            [new SarifRun(new SarifTool(new SarifDriver(ToolName, ToolVersion, InformationUri: null, SarifRuleCatalog.AllRules)), results)]);

        return JsonSerializer.Serialize(log, JsonOptions);
    }

    private static SarifResult ToResult(SargabilityFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.Tier1RuleId(finding.Kind), finding.Confidence);

        // A syntactic pattern is only worth a reader's full attention when it's confirmed on a
        // real, leading-key-indexed column - one where there was an actual seek to lose. finding
        // .Indexed is null (unresolved) far more often than it's false (resolved-and-confirmed-
        // unindexed) in real corpora, so both demote the same way: only Indexed == true keeps
        // the kind's normal severity. Without this, every syntactic hit on an unindexed or
        // unresolvable column reported at the same "warning" level as a genuine index-defeating
        // one - the single largest source of unranked noise this pass produced.
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

        // Mirrors the Tier-1 downgrade below: a ScanForced/RangeSeek verdict on a column with no
        // evidence it's indexed cost nothing extra beyond the conversion itself - there was no
        // seek to lose. Every corpus finding this tool has actually produced against real-world
        // repos has been on an unindexed column (an audit finding), so without this every one of
        // them reported at "error" regardless of whether an index was ever in play.
        var level = finding.Column.Indexed ? baseLevel : DowngradeOneLevel(baseLevel);
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

        // Same indexed-based downgrade as every other verdict-bearing finding kind: an
        // expression-derived column with no indexed base column underneath it isn't costing an
        // otherwise-available seek.
        var anyUnderlyingIndexed = finding.UnderlyingBaseColumns.Any(bc => bc.Indexed);
        var level = anyUnderlyingIndexed ? LevelError : DowngradeOneLevel(LevelError);
        level = FloorLevelForConfidence(level, finding.Confidence);
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ExpressionDerivedRuleId, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.ColumnPosition);
    }

    private static SarifResult ToResult(CollationConflictFinding finding)
    {
        // Always error, regardless of whether either column is indexed - this isn't a
        // sargability downgrade candidate the way an ordinary verdict or expression-derived
        // finding is; the query does not compile at all (oracle-verified: SQL Server Msg 468),
        // which outranks every seek-versus-scan concern.
        var message = $"Collation conflict: '{finding.FirstTableQualifiedName}.{finding.FirstColumnName}' (COLLATE {finding.FirstCollationName}) {finding.Operator} '{finding.SecondTableQualifiedName}.{finding.SecondColumnName}' (COLLATE {finding.SecondCollationName}) does not compile.{DynamicSqlOriginNote(finding.DynamicSqlCallSite)}";
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CollationConflictRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.ColumnPosition);
    }

    private static SarifResult ToResult(ColumnCollationDriftFinding finding)
    {
        // Informational, not error/warning - this is a seed, not yet a comparison that's
        // actually happening; no seek was lost and nothing failed to compile.
        var kindNote = finding.IsTempObject ? "tempdb's effective" : "the database's default";
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' (COLLATE {finding.ColumnCollationName}) differs from {kindNote} collation (COLLATE {finding.BaselineCollationName}) - a conversion seed for any future comparison against a column/literal carrying that collation.";
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ColumnCollationDriftRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(CrossTableTypeDriftFinding finding)
    {
        // Informational, not error/warning - a seed on a foreign key relationship, not yet a
        // query that actually joins on it.
        var message = $"FK '{finding.ConstraintName}': '{finding.ParentTableQualifiedName}.{finding.ParentColumnName}' ({finding.ParentTypeDisplay}) references '{finding.ReferencedTableQualifiedName}.{finding.ReferencedColumnName}' ({finding.ReferencedTypeDisplay}) - the types differ{(finding.CollationDiffers ? " (collation differs)" : string.Empty)}.";
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CrossTableTypeDriftRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(ProcCallArgumentMismatchFinding finding)
    {
        // Same severity treatment as a WriteLossFinding - warning, not error/downgraded-by-index,
        // since "is this indexed" has no bearing on a silent-data-loss assignment.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ProcCallArgumentMismatchRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var callerLabel = finding.CallerScopeQualifiedName ?? "a top-level batch";
        var message = $"EXEC '{finding.CalleeQualifiedName}': parameter '{finding.FormalParameterName}' ({finding.FormalParameterTypeDisplay}) receives '{finding.CallerVariableName}' ({finding.CallerTypeDisplay}) from {callerLabel} - {DescribeWriteLossKind(finding.Kind)}.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(TemporalBoundaryPrecisionFinding finding)
    {
        // Error, not warning/note - unlike a sargability finding, this is a live correctness
        // bug (silently dropped rows), oracle-confirmed, not a "worth investigating" signal.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TemporalBoundaryPrecisionRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' (scale {finding.ColumnScale}) is compared with BETWEEN against upper bound '{finding.BoundaryLiteralText}' ({finding.BoundaryLiteralFractionalDigits} fractional digit(s)) - rows in the precision gap are silently excluded. Rewrite as >= start AND < (start of the next period).";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(MaxTypedColumnFinding finding)
    {
        // Informational, not error/warning - a structural catalog fact, not evidence of an
        // actual predicate/join comparison against it. It can never be an index key column,
        // but that's an inherent property, not something a query newly triggered.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.MaxTypedColumnRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' is declared {finding.TypeDisplay} - MAX-typed columns can never be an index key column, so no predicate/join on it can ever seek.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: 1);
    }

    private static SarifResult ToResult(OversizedParameterFinding finding)
    {
        // Warning, not error - oracle-falsified that a bare equality predicate shows any memory-
        // grant difference; this is a structural report of a length mismatch, not a plan-shape
        // claim for this specific predicate. Not downgraded by indexed-ness either: the risk is
        // about the value's declared size feeding a sort/hash operator, unrelated to seek loss.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.OversizedParameterRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' (length {finding.ColumnLength}) is compared against a parameter/variable/expression declared with length {finding.OtherOperandLength} - risks memory-grant inflation if the value feeds a sort/hash operator.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(PartialCompositeForeignKeyJoinFinding finding)
    {
        // Warning, not error/downgraded-by-index - "is this indexed" has no bearing here; the
        // defect is row multiplication, not a lost seek. Not error either: unlike a collation
        // conflict (a compile failure) or a temporal-boundary drop (a proven row-count gap this
        // scanner can directly demonstrate per-finding), whether the omission is a real bug or a
        // deliberate fan-out is genuinely ambiguous to static analysis alone (see the finding's
        // own doc comment) - reflected in its default Medium confidence, not just its SARIF level.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.PartialCompositeForeignKeyJoinRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var matched = string.Join(", ", finding.MatchedColumnPairs.Select(p => $"{p.ParentColumnName}={p.ReferencedColumnName}"));
        var missing = string.Join(", ", finding.MissingColumnPairs.Select(p => $"{p.ParentColumnName}={p.ReferencedColumnName}"));
        var message = $"FK '{finding.ConstraintName}': join between '{finding.ParentTableQualifiedName}' and '{finding.ReferencedTableQualifiedName}' matches [{matched}] but omits [{missing}] - a parent row can match more than one child row than the declared relationship allows.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(SetOptionFinding finding)
    {
        // Error, not warning/downgraded-by-index: unlike a sargability finding, this isn't "worth
        // investigating" - ModuleReachableObjectWalker already proved the module touches a real
        // filtered index or indexed view, so the SET option genuinely disables a plan feature
        // this exact module would otherwise use, oracle-confirmable per docs/detection-
        // checklist.md's own note on the compile-only SHOWPLAN_XML mechanism.
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
            _ => $"'{finding.ModuleQualifiedName}': SET CONCAT_NULL_YIELDS_NULL OFF{touchedDisplay}.",
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
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

    private static SarifResult ToResult(UnderLengthParameterFinding finding)
    {
        // Warning, same severity tier as OversizedParameterFinding and WriteLossFinding's own
        // identical class of concern (a write/comparison silently narrowing a value with no
        // error raised) - this pass never traces the variable's actual assigned value, so it
        // cannot claim truncation DID happen for a specific query, only that the declared-length
        // pairing risks it. Not downgraded by indexed-ness: the risk is about the compared VALUE
        // being truncated, unrelated to seek loss.
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
        // Error, not warning - stronger than every other structural report in this section: a
        // non-padded column can NEVER store a value ending in whitespace at all (stripped at
        // INSERT time), so a pattern with significant trailing whitespace can never match
        // anything the column could ever contain. Not a conditional risk dependent on an unknown
        // runtime value like OversizedParameterFinding/UnderLengthParameterFinding - a provably
        // always-false predicate, the same certainty tier TemporalBoundaryPrecisionFinding's own
        // oracle-confirmed row-drop gets.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.AnsiPaddingMismatchRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' is a non-ANSI-padded column (trailing blanks stripped at INSERT) compared via LIKE against pattern {finding.PatternLiteralText}, whose trailing whitespace is significant - this predicate can never match any value the column could ever store.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(CatchAllPredicateFinding finding)
    {
        // Warning, downgraded when not confirmed indexed (matches Tier1Findings' own downgrade
        // convention) - there was no seek to lose if the column was never indexed in the first
        // place, even though the catch-all shape itself is real either way.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CatchAllPredicateRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(finding.Indexed ? LevelWarning : LevelNote, finding.Confidence);
        var indexedNote = finding.Indexed ? string.Empty : " (would defeat an index if one existed - none is confirmed indexed today)";
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' = {finding.ParameterName} OR {finding.ParameterName} IS NULL{indexedNote} - one cached plan must stay correct for every NULL/non-NULL state of this parameter, typically forcing a scan.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(LocalVariablePredicateFinding finding)
    {
        // Note, not warning/error: purely informational per the finding's own doc comment - the
        // predicate is still fully sargable, only the row-count ESTIMATE is at risk, and this
        // pass has no way to know whether that estimate actually matters for real data.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.LocalVariablePredicateRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' {finding.Operator} {finding.VariableName} - a DECLARE'd local, not a formal parameter, so its value is invisible to the cardinality estimator (falls back to average-density statistics). The predicate still seeks if the column is indexed; only the row-count estimate is at risk.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(FilteredIndexParameterMismatchFinding finding)
    {
        // Error, not Note: unlike LocalVariablePredicateFinding/ParameterReassignmentPredicateFinding
        // above (a cardinality-ESTIMATE risk only, predicate still seeks), this is a real access-
        // path defect - the filtered index is oracle-confirmed genuinely unusable for this query
        // shape, not merely mis-estimated.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.FilteredIndexParameterMismatchRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var operandKind = finding.IsFormalParameter ? "formal parameter" : "local variable";
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' {finding.Operator} {finding.VariableName} - filtered index '{finding.IndexName ?? "<unnamed>"}' filters this exact column against the literal {finding.FilterLiteralText}, but the optimizer can only match a filtered index against a query that restates its filter with a LITERAL, never a {operandKind}. This query can never use that index, no matter what value {finding.VariableName} holds at runtime - a compile-time limitation OPTION (RECOMPILE) does not fix.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ParameterReassignmentPredicateFinding finding)
    {
        // Note, not warning/error: purely informational per the finding's own doc comment - the
        // predicate is still fully sargable, only the row-count ESTIMATE (built from the now-stale
        // sniffed value) is at risk, the identical certainty tier LocalVariablePredicateFinding uses.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.ParameterReassignmentPredicateRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' {finding.Operator} @{finding.ParameterName} - {finding.ParameterName} is a formal parameter reassigned at line {finding.ReassignmentLine} before this predicate runs, so the optimizer's compile-time sniffed value (the caller's original argument) is stale by the time this comparison executes. The predicate still seeks if the column is indexed; only the row-count estimate is at risk.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(CodeMetricFinding finding)
    {
        // Note, not warning/error: a pure maintainability/readability signal - no query result or
        // plan is ever affected by any of these eight metrics.
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
        // Note, not warning/error, for every kind except the two visual-ambiguity ones (a
        // dangling statement or a misread ELSE IF) - a pure readability/maintainability signal,
        // no query result or plan is ever affected. The ambiguity kinds still stay Note-tier: the
        // STATEMENT'S OWN behavior is unaffected either way, only a future edit relying on the
        // misleading visual shape is at risk (the same reasoning CodeMetricFinding already
        // established for this class of Tier 4 finding).
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

    private static SarifResult ToResult(NamingFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NamingRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(DeadCodeFinding finding)
    {
        // Warning, not error - a structural/maintainability risk, not itself a proof of a wrong
        // result (the same tier ForcedSerialFinding/FormattingFinding use), even for the
        // structurally-provable High-confidence kinds (unreachable code, unused label, redundant
        // jump): the flagged code's own current behavior is unaffected either way.
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
        // Warning, not error - a structural/maintainability risk, not itself a proof of a wrong
        // result, matching DeadCodeFinding's own tier: the flagged code's current behavior is
        // unaffected either way, even for the structurally-unambiguous High-confidence kinds.
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
        // Note level for the two purely informational, workflow-tracking kinds (TODO/FIXME - not
        // a defect at all); Warning for everything else - a real syntax/behavior risk, but not
        // itself proof of a wrong result the way the EqualsNullComparison/NotEqualsNullComparison
        // kinds' own High confidence already signals through the confidence-based level floor.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.DeprecatedSyntaxRuleId(finding.Kind), finding.Confidence);
        var baseLevel = finding.Kind is DeprecatedSyntaxFindingKind.TaskCommentTodo or DeprecatedSyntaxFindingKind.TaskCommentFixme
            ? LevelNote
            : LevelWarning;
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(StatementShapeFinding finding)
    {
        // Note level for the purely-advisory BareSelectStar kind (Low confidence by
        // construction); Warning for everything else - a real correctness/maintainability risk,
        // never itself proof of a wrong result.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.StatementShapeRuleId(finding.Kind), finding.Confidence);
        var baseLevel = finding.Kind == StatementShapeFindingKind.BareSelectStar ? LevelNote : LevelWarning;
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ControlFlowRiskFinding finding)
    {
        // Error for the structurally-unambiguous, hard-fact kinds (a cursor FETCH that always fails
        // at runtime; a CATCH block that swallows every error with zero statements; a simple CASE
        // with no ELSE, which silently returns NULL; a non-deterministic CASE input, oracle-confirmed
        // to make every WHEN branch effectively unreachable) - the same "provably-wrong-outcome" tier
        // NotInNullableSubqueryFinding/TempTableExecShapeFinding use. Warning for everything else,
        // including GotoUsage (a maintainability risk, not itself a provably wrong outcome) - a real,
        // well-documented risk, never itself proof of a wrong result in isolation.
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
        // Error for the two structurally-unambiguous, hard-fact kinds (a hardcoded non-benign IP
        // address; a HASHBYTES call naming a weak algorithm outright) - the same tier
        // ControlFlowRiskFinding uses for its own hard-fact kinds. Warning for the sharper but
        // context-dependent/name-based kinds, since none of these is a provable vulnerability in
        // isolation - this pass never traces as far as an actual external-input boundary.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SecurityRuleId(finding.Kind), finding.Confidence);
        var baseLevel = finding.Kind is SecurityFindingKind.HardCodedIpAddress or SecurityFindingKind.WeakHashAlgorithm
            ? LevelError
            : LevelWarning;
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(NotInNullableSubqueryFinding finding)
    {
        // Error, not warning - a data-correctness bug, the same certainty tier
        // AnsiPaddingMismatchFinding/TemporalBoundaryPrecisionFinding get: not a conditional risk
        // dependent on an unknown runtime value, but a query that returns the wrong RESULT SET
        // right now, for this exact code, the instant the underlying data contains one NULL in
        // the subquery column. Never downgraded by indexed-ness - there is no seek/scan angle to
        // this finding at all.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NotInNullableSubqueryRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var outerColumnDisplay = finding.OuterColumnName ?? "<expression>";
        var message = $"{outerColumnDisplay} NOT IN (SELECT '{finding.SubqueryTableQualifiedName}.{finding.SubqueryColumnName}' ...) - the subquery column is nullable and unfiltered, so the whole predicate evaluates to UNKNOWN and silently returns zero rows the instant the data contains one NULL there, instead of the expected anti-join result.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(NonUniqueUpdateSourceFinding finding)
    {
        // Error, not warning - the absence of a uniqueness guarantee is itself the full, provable
        // defect (no current data has to be inspected or assumed), the same "structural, not
        // data-dependent" framing PartialCompositeForeignKeyJoinFinding uses. Never downgraded by
        // indexed-ness - there is no seek/scan angle to this finding at all.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NonUniqueUpdateSourceRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var joinColumns = string.Join(", ", finding.JoinColumnNames);
        var setColumns = string.Join(", ", finding.SetColumnNames);
        var message = $"UPDATE '{finding.TargetTableQualifiedName}' sets [{setColumns}] from '{finding.SourceTableQualifiedName}' joined on [{joinColumns}], which carries no unique index/constraint covering those columns - if a target row ever matches more than one source row, SQL Server silently picks a value from an unspecified one of them.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(ForcedSerialFinding finding)
    {
        // Warning, not error: a performance-cost finding, not a correctness one - forced-serial
        // execution never changes the result, only its cost, the same "structural risk" tier
        // CatchAllPredicateFinding/SetOptionFinding get rather than the provably-wrong-result
        // Error tier NotInNullableSubqueryFinding/NonUniqueUpdateSourceFinding get.
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
        // Warning, not error: a performance-cost finding, not a correctness one - the same
        // "structural risk, not provably-wrong-result" tier ForcedSerialFinding/CatchAllPredicateFinding
        // use. Never names one specific operator (spool or sort) in the message - the oracle
        // confirmed both mechanisms occur depending on statement shape, see this finding's own
        // doc comment.
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
        // Warning, not error: real optimizer forfeiture (join elimination / constraint-based
        // rewrites), but the finding itself proves no wrong result on its own - the same
        // "structural risk, not provably-wrong-result" tier ForcedSerialFinding/SetOptionFinding
        // get, not the Error tier a correctness finding gets.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.UntrustedConstraintRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var kindDisplay = finding.Kind == UntrustedConstraintFindingKind.ForeignKey ? "foreign key" : "CHECK constraint";
        var message = $"'{finding.ConstraintName}' ({kindDisplay} on '{finding.TableQualifiedName}') is untrusted - the engine does not guarantee it holds over existing rows, and forfeits join-elimination/constraint-based query rewrites that assume it does.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(CheckConstraintFinding finding)
    {
        // Error, not warning - a data-correctness bug the same certainty tier
        // NotInNullableSubqueryFinding/AnsiPaddingMismatchFinding get, not the "structural risk,
        // not provably-wrong-result" Warning tier UntrustedConstraintFinding itself uses: both
        // kinds here are unconditional, oracle-confirmed engine mechanics with no workload
        // dependence - see CheckConstraintFinding's own doc comment for the evidence.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CheckConstraintRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = finding.Kind switch
        {
            CheckConstraintFindingKind.NullNotHandled =>
                $"'{finding.ConstraintName}' (CHECK on '{finding.TableQualifiedName}.{finding.ColumnName}') has no IS NULL/IS NOT NULL test against '{finding.ColumnName}' anywhere in its predicate, and '{finding.ColumnName}' is nullable - a NULL value silently passes this constraint under SQL Server's three-valued logic, even though the constraint reads as if it forbids bad data.",
            CheckConstraintFindingKind.ConstraintOnIdentityColumn =>
                $"'{finding.ConstraintName}' (CHECK on '{finding.TableQualifiedName}.{finding.ColumnName}') references the IDENTITY column '{finding.ColumnName}' directly - every insert whose auto-generated identity value doesn't yet satisfy this predicate fails deterministically (Msg 547), consuming the identity counter on each failed attempt, until the counter catches up and failures silently stop forever.",
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled CheckConstraintFindingKind."),
        };

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(DefaultNullableConstraintFinding finding)
    {
        // Warning, not error - see DefaultNullableConstraintFinding's own doc comment: a DEFAULT
        // is an insert-convenience feature, not a data-integrity guarantee the schema claims to
        // enforce, unlike CheckConstraintFinding.NullNotHandled.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.DefaultNullableConstraintRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' carries a DEFAULT constraint ({finding.DefaultDefinitionText}) but is still nullable - a caller supplying NULL explicitly for this column bypasses the default entirely, silently, with no error.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(TryCastComputedColumnPredicateFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TryCastComputedColumnPredicateRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' (a non-persisted computed column defined as '{finding.DefinitionText}', {finding.DefinitionSourcePath}:{finding.DefinitionLine}) is referenced in a predicate here - TRY_CAST makes this column non-deterministic, so it can never be PERSISTED or indexed, and this predicate can never seek through it.";

        return BuildResult(ruleId, level, message, finding.PredicateSourcePath, finding.PredicateLine, finding.PredicateColumn);
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
        // Warning at High confidence, floored to Note here since BareTopNoOrderByFinding ships at
        // Medium - see its own doc comment: the nondeterminism mechanism is a certain, documented
        // engine fact, but whether any real caller depends on the returned row set is workload
        // intent this pass cannot see.
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
        // Note, not Warning - see AggregateDivisionColumnstoreFinding's own doc comment: shipped
        // as a structural risk flag only, Low confidence, after a genuine but unsuccessful
        // live-reproduction attempt against this tool's own standing engine build.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.AggregateDivisionColumnstoreRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = $"{finding.AggregateFunctionName}(...) on '{finding.TableQualifiedName}' (backed by a columnstore index) contains a CASE-guarded division by a non-constant divisor - historically reported as unreliable under batch-mode/vectorized execution's own CASE-branch evaluation, unlike rowstore scalar evaluation.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(SecurityPredicateIndexFinding finding)
    {
        // Warning, not Error - a structural risk flag (Medium confidence): the "no supporting
        // index on the predicate's own bound columns forces a scan+residual-filter" mechanism is
        // oracle-confirmed and unconditional, but the actual real-world cost is still workload-
        // dependent (table size, access frequency), the same tier
        // IndexDesignFindingKind.ColumnstoreIndexOnDmlTargetTable already uses for an analogous
        // exact-structural-precondition-but-workload-dependent-cost claim.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.SecurityPredicateIndexRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var columns = string.Join(", ", finding.FilteredColumns);
        var message = $"'{finding.TableQualifiedName}' is secured by RLS policy '{finding.PolicyQualifiedName}''s FILTER predicate '{finding.PredicateFunctionQualifiedName}', bound to column(s) {columns} - none of them leads an active index on this table, so this predicate is silently applied to every SELECT/UPDATE/DELETE against this table as a residual, per-row filter over a full scan rather than a seek.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(CascadingForeignKeyFinding finding)
    {
        // Note, not warning/error: purely informational per the finding's own doc comment - a
        // real, exact catalog fact, but no magnitude claim (how many rows, how often), the same
        // no-magnitude-claim tier LocalVariablePredicateFinding uses for its own reason.
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
        // Warning, not error: an oracle-confirmed real mechanism (the history-side branch of the
        // FOR SYSTEM_TIME UNION ALL scans without a matching index), but the oracle confirmation is
        // of the general mechanism, not a per-finding plan-XML probe against a real query site - the
        // same "structural risk, not provably-wrong-result" tier UntrustedConstraintFinding/
        // ForcedSerialFinding get, not the Error tier a correctness finding gets.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TemporalTableHistoryIndexGapRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var indexDisplay = finding.CurrentIndexName is null ? "an unnamed index" : $"'{finding.CurrentIndexName}'";
        var keyColumns = string.Join(", ", finding.KeyColumns);
        var message = $"{indexDisplay} on '{finding.CurrentTableQualifiedName}' ({keyColumns}) has no structurally matching index on its history table '{finding.HistoryTableQualifiedName}' - a FOR SYSTEM_TIME query that seeks the current side via this index degrades to a scan of the whole history table.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(ModuleCompileFlagFinding finding)
    {
        // Warning, not error: a real, structural cost/risk, not a proven-wrong-result claim - the
        // same "structural risk" tier SetOptionFinding/CascadingForeignKeyFinding use.
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
        // Warning, not error: a real, oracle-measured performance cost, not a proven-wrong-result
        // claim - the same "structural risk" tier ForcedSerialFinding/CatchAllPredicateFinding use.
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

    private static SarifResult ToResult(WaitForFinding finding)
    {
        // Warning, not error: a documented structural cost/risk, not a proven-wrong-result claim -
        // the same "structural risk" tier ForcedSerialFinding/CatchAllPredicateFinding use.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.WaitForRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = finding.IsInsideTransaction
            ? "WAITFOR DELAY/TIME holds this worker thread idle inside an open transaction - locks held by that transaction stay held for the full delay/until-time too."
            : "WAITFOR DELAY/TIME holds this worker thread idle for the full delay/until-time, contributing to worker-pool exhaustion under load.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(TransactionHygieneFinding finding)
    {
        // Warning, not error: a real, oracle-confirmed correctness/robustness defect, but the
        // same "structural risk" tier ForcedSerialFinding/WaitForFinding already use rather than
        // the LevelError tier reserved for a proven-wrong-RESULT claim (this finding's defect is
        // a leaked lock/session-state condition, not a wrong row set).
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.TransactionHygieneRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message =
            $"BEGIN TRANSACTION at line {finding.BeginTransactionLine} reaches this point with no intervening COMMIT/ROLLBACK - @@TRANCOUNT is left elevated by one on this path, holding its locks until the session or connection pool eventually clears it.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.UnresolvedExitLine, finding.UnresolvedExitColumn);
    }

    private static SarifResult ToResult(OutputParameterFinding finding)
    {
        // Warning, not error: same "structural risk, not a plan-shape claim" tier
        // TransactionHygieneFinding/ForcedSerialFinding already use - the actual harm depends on
        // whether a real caller reads the parameter's post-call value at all, which this pass
        // cannot observe.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.OutputParameterRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message =
            $"OUTPUT parameter '{finding.ParameterName}' is not assigned on this path - the caller's own variable is left completely unchanged (not reset to NULL), so a reused caller variable can silently read stale data from a previous call.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.UnresolvedExitLine, finding.UnresolvedExitColumn);
    }

    private static SarifResult ToResult(DatabaseConfigurationFinding finding)
    {
        // No file/line applies - this is a database-granularity fact, not a module/predicate one.
        // The database's own name stands in for a "location" so the SARIF result still carries an
        // artifactLocation, matching this writer's existing shape for every other finding.
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
            _ => throw new ArgumentOutOfRangeException(nameof(finding)),
        };

        return BuildResult(ruleId, FloorLevelForConfidence(level, finding.Confidence), message, finding.DatabaseName, 1, null);
    }

    private static SarifResult ToResult(CompositeIndexLeadingColumnFinding finding)
    {
        // Warning, not error: a provable structural (b-tree prefix) fact, but a per-index claim
        // ("this index cannot seek this query"), never an index-recommendation or overall-query-
        // is-slow claim - the same "structural risk" tier ForcedSerialFinding/WindowFrameFinding use.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CompositeIndexLeadingColumnRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var indexLabel = finding.IndexName ?? "(unnamed index)";
        var message =
            $"Index {indexLabel} on {finding.TableQualifiedName} is keyed ({string.Join(", ", finding.IndexKeyColumns)}) - this query constrains {finding.ViolatingColumnName} (key position {finding.ViolatingColumnPosition}) but never binds the leading key column {finding.IndexKeyColumns[0]} anywhere in the statement, and no other index on this table leads with {finding.ViolatingColumnName} either, so nothing here can seek {finding.ViolatingColumnName} through a real index.";

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

    private static SarifResult ToResult(CartesianJoinFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.CartesianJoinRuleId(finding.Kind), finding.Confidence);
        var kindText = finding.Kind == CartesianJoinKind.ExplicitCrossJoin ? "CROSS JOIN" : "comma-join";
        var message = $"{finding.FirstTableQualifiedName} and {finding.SecondTableQualifiedName} are combined via a {kindText} with no predicate anywhere in the statement connecting the two - a true cartesian product.";
        return BuildResult(ruleId, FloorLevelForConfidence(LevelWarning, finding.Confidence), message, finding.SourcePath, finding.Line, finding.Column);
    }

    private static SarifResult ToResult(UndersizedDeclarationFinding finding)
    {
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.UndersizedDeclarationRuleId(finding.Site), finding.Confidence);
        var message = $"{finding.QualifiedOrVariableName} is declared {finding.TypeDescription} - a length of {finding.Length} is almost always a truncated-from-a-larger-source mistake or a leftover placeholder.";
        return BuildResult(ruleId, FloorLevelForConfidence(LevelNote, finding.Confidence), message, finding.SourcePath, finding.Line, finding.Column);
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
            // Warning/High: oracle-confirmed the ordering is provably never guaranteed to a
            // consumer - the same "structural risk, high confidence" tier as e.g.
            // AnsiPaddingMismatchFinding's own LIKE case.
            ViewOrderingFindingKind.TopPercentOrderByNeverLimits =>
                $"'{finding.ObjectQualifiedName}' uses TOP (100) PERCENT ... ORDER BY - 100 PERCENT never excludes a row, so this ORDER BY exists only to satisfy T-SQL's view-ordering grammar rule and is not guaranteed to any consumer that doesn't apply its own ORDER BY.",
            // Note/Low: purely informational - this pass cannot see whether any real consumer
            // relies on the unguaranteed order, the same no-magnitude-claim tier
            // CascadingForeignKeyFinding uses for its own reason.
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
        // Warning, not error: a real, structural cost (each reference re-runs the CTE's own
        // query), but a performance-cost claim, not a correctness one - the same "structural
        // risk" tier ForcedSerialFinding/CatchAllPredicateFinding use.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.MultiReferencedCteRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"CTE '{finding.CteName}' is referenced {finding.ReferenceCount} times downstream of its own WITH clause - each reference independently re-runs the CTE's own defining query, SQL Server does not materialize it once and reuse it.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
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
        // Structural/plan-cache report, not a provably-wrong-result claim - same "informational,
        // but the underlying fact is exact" tier as CatchAllPredicateFinding/SetOptionFinding:
        // warning, floored by confidence, never downgraded by index-existence (irrelevant here).
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
        // Informational, not error/warning - a structural catalog fact (is_persisted = 0),
        // definitionally true independent of whether any scanned query touches the column,
        // same tier as MaxTypedColumnFinding's own structural report.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.NonPersistedComputedColumnRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelNote, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' is a non-persisted computed column ({finding.DefinitionText}) - recomputed from the base row on every read that touches it.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(IndexDesignFinding finding)
    {
        // Error for the structurally-provable, no-estimation kinds (both heap kinds, non-unique
        // clustered index, and the GUID/NEWID default - an exact DEFAULT-text match, not a
        // heuristic). Warning for the threshold-based wide-key kind, matching how this codebase
        // already floors severity by confidence elsewhere (FloorLevelForConfidence handles the
        // Medium-confidence downgrade for it on top of this).
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.IndexDesignRuleId(finding.Kind), finding.Confidence);
        // TimestampColumnNaming is a naming-only recommendation (Low confidence, informational) -
        // Note rather than Error/Warning, the same tier NonPersistedComputedColumnFinding uses for
        // an equally informational structural fact.
        var baseLevel = finding.Kind switch
        {
            IndexDesignFindingKind.WideClusteredKey => LevelWarning,
            // Both are the checklist's own "structural risk flag only, never a proven-cost claim"
            // kinds (Medium confidence) - Warning, the same tier WideClusteredKey uses for its own
            // threshold-based judgment call, not Error's "structurally-provable, no-estimation" tier.
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
        // IdentitySeedOrIncrementAnomaly is Low-confidence/informational by construction (see its
        // own doc comment) - Note. IdentityRangeNearExhaustion is a real, structurally-provable
        // approaching-failure condition (the next INSERT past the type's own range raises a hard
        // error) - Error, floored by confidence like every other stream.
        var baseLevel = finding.Kind == IdentityRangeFindingKind.IdentityRangeNearExhaustion ? LevelError : LevelNote;
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.IdentityRangeRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: null);
    }

    private static SarifResult ToResult(FloatEqualityFinding finding)
    {
        // A correctness claim (can silently return the wrong rows), not a performance one - Error,
        // the same tier NotInNullableSubqueryFinding/TempTableExecShapeFinding's column-count
        // mismatch use for "provably wrong outcome" findings.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.FloatEqualityRuleId, finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' ({finding.TypeDisplay}) is compared with = in this predicate - IEEE-754 floating-point representation error means two values a person would call the same number can compare unequal, silently returning the wrong rows.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(QueryAntiPatternFinding finding)
    {
        // TableVariableLowCompatEstimate and CountStarVariableExistenceCheck/
        // NonAggregateHavingPredicate are mechanically-confirmed hard facts about the connected
        // engine/optimizer, not magnitude estimates - Error. Every other kind is a real but
        // context-dependent risk (a deliberate GLOBAL cursor, a genuinely tiny RBAR loop, a stale
        // estimate that may never matter, a fan-out that may be intentional) - Warning.
        var baseLevel = finding.Kind switch
        {
            QueryAntiPatternFindingKind.TableVariableLowCompatEstimate => LevelError,
            QueryAntiPatternFindingKind.CountStarVariableExistenceCheck => LevelError,
            QueryAntiPatternFindingKind.NonAggregateHavingPredicate => LevelWarning,
            // MergeNonUniqueUsingSource/RecursiveCteMissingMaxRecursion are mechanically-confirmed
            // hard engine facts too (a real error the moment the shape is exercised), same tier as
            // the two above - every other new kind is a real but context-dependent risk.
            QueryAntiPatternFindingKind.MergeNonUniqueUsingSource => LevelError,
            QueryAntiPatternFindingKind.RecursiveCteMissingMaxRecursion => LevelError,
            _ => LevelWarning,
        };
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.QueryAntiPatternRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(baseLevel, finding.Confidence);

        return BuildResult(ruleId, level, finding.DetailText, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(IndexCoverageFinding finding)
    {
        // Oracle-confirmed hard fact (real Lookup="1" plan-XML marker for the non-covering shape) -
        // Error, floored by confidence like every other stream.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.IndexCoverageRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelError, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}' via index '{finding.IndexName ?? "<unnamed>"}' ({string.Join(", ", finding.IndexKeyColumns)}) does not cover ({string.Join(", ", finding.UncoveredColumns)}) - a matched row needs a Key/RID Lookup back to the base table.";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(TriggerCorrectnessFinding finding)
    {
        // MultiRowUnsafe* are oracle-confirmed silent-wrong-value engine facts, same tier as the
        // shipped WriteLossFinding stream - error. NoEarlyOutForEmptyInvocation is genuinely
        // advisory (Low confidence by construction) - note. DirectRecursiveTrigger only ever
        // fires once the gating live database option is confirmed on, a real mechanical fact once
        // gated - warning (not error: whether the recursive branch is ever actually reached at
        // runtime is real control-flow this pass cannot fully resolve, per its own doc comment).
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
        // A static deadlock-RISK claim, not a runtime guarantee (the finding's own doc comment
        // spells out everything real interleaving/row-granularity this pass cannot see) -
        // warning, floored by confidence like every other stream.
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
        // Same "static structural risk, gated on a live-confirmed engine setting" framing as
        // TriggerCorrectnessFindingKind.DirectRecursiveTrigger - warning, floored by confidence.
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
            // A hard runtime error every time this statement executes (Msg 213/8164), not a
            // silent defect - same "provably wrong outcome" tier as NotInNullableSubqueryFinding,
            // error rather than warning.
            var level = FloorLevelForConfidence(LevelError, finding.Confidence);
            var message = $"INSERT INTO {finding.TempTableQualifiedName} EXEC {finding.ExecutedProcQualifiedName}: the temp table declares {finding.TempTableDeclaredColumnCount} column(s) but the executed proc's real result set describes {finding.DescribedColumnCount} - this raises a hard error (Msg 213/8164) every time it runs.";
            return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, startColumn: finding.Column);
        }

        // Silent data loss at a call boundary, not a predicate - same tier as WriteLossFinding/
        // ProcCallArgumentMismatchFinding: always warning, never downgraded by index existence.
        var typeLevel = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var typeMessage = $"INSERT INTO {finding.TempTableQualifiedName} EXEC {finding.ExecutedProcQualifiedName}: position {finding.ColumnPosition} ('{finding.ColumnName}', {finding.TempColumnTypeDisplay}) receives {finding.DescribedColumnTypeDisplay} from the executed proc's real result set - {DescribeWriteLossKind(finding.WriteLoss!.Value)}.";
        return BuildResult(ruleId, typeLevel, typeMessage, finding.SourcePath, finding.Line, startColumn: finding.Column);
    }

    private static SarifResult ToResult(WriteLossFinding finding)
    {
        // Always warning, not error/downgraded-by-index the way a seek/scan finding is - "is
        // this column indexed" has no bearing on whether a write silently loses data.
        var ruleId = SarifRuleCatalog.RuleId(SarifRuleCatalog.WriteLossRuleId(finding.Kind), finding.Confidence);
        var level = FloorLevelForConfidence(LevelWarning, finding.Confidence);
        var message = $"'{finding.TableQualifiedName}.{finding.ColumnName}' ({finding.TargetType}) is assigned a {finding.SourceType} value - {DescribeWriteLossKind(finding.Kind)}.{DynamicSqlOriginNote(finding.DynamicSqlCallSite)}";

        return BuildResult(ruleId, level, message, finding.SourcePath, finding.Line, finding.ColumnPosition);
    }

    private static SarifResult ToResult(TvfFenceFinding finding)
    {
        // Correlated APPLY and a fence inherited invisibly through a view/TVF layer are the two
        // cases no engine-version mitigation and no text-matching tool rescue, so both stay at
        // error; a direct FROM/JOIN reference and INSERT...EXEC are real but ordinary fences
        // (warning); a standalone reference is genuine but has no surrounding plan to poison.
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
        // Predicate-context and reached-through-lineage are the two cases where the claim is
        // strongest (non-sargable AND per-row AND, pre-2019/non-inlineable, serial); a schema
        // dependency poisons every query touching the table but has no query-site call to point
        // at; a plain projection-context call is real per-row cost with no sargability impact.
        var level = finding.Kind switch
        {
            ScalarUdfFindingKind.PredicateInvocation or ScalarUdfFindingKind.NestedUnderViewOrTvf => LevelError,
            ScalarUdfFindingKind.SchemaDependency => LevelWarning,
            ScalarUdfFindingKind.ProjectionInvocation => LevelNote,
            _ => throw new ArgumentOutOfRangeException(nameof(finding), finding.Kind, "Unhandled ScalarUdfFindingKind."),
        };

        // A scalar UDF the engine itself inlines (2019+) dissolves into the calling plan at
        // compile time - real, but no longer the maximal claim the base level asserts.
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

    /// <summary>
    /// Floors a finding's computed level to <c>note</c> once its confidence drops below
    /// <see cref="FindingConfidence.High"/> - a finding resting on an assumption (a dynamic-SQL
    /// symbolic placeholder standing in for a value this scanner could not prove constant) never
    /// outranks one resting on real source text, regardless of what its own severity would
    /// otherwise compute to.
    /// </summary>
    private static string FloorLevelForConfidence(string level, FindingConfidence confidence) =>
        confidence == FindingConfidence.High ? level : LevelNote;

    private static string DowngradeOneLevel(string level) => level switch
    {
        LevelError => LevelWarning,
        LevelWarning => LevelNote,
        _ => LevelNote,
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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled WriteLossKind."),
    };

    private static string DescribeIndexNote(PredicateOperand.Column column)
    {
        if (!column.Indexed)
        {
            return ", not indexed";
        }

        return column.IndexName is { } indexName ? $", indexed ({indexName})" : ", indexed";
    }

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
            [new SarifLocation(new SarifPhysicalLocation(new SarifArtifactLocation(ToUri(sourcePath)), new SarifRegion(line, startColumn)))]);

    /// <summary>
    /// Emits a real <c>file://</c> URI for an absolute path, or a percent-encoded relative
    /// reference otherwise - not the previous ad-hoc "swap backslashes, escape spaces" scheme,
    /// which produced a scheme-less string like <c>/home/user/repo/file.sql</c> that strict
    /// SARIF consumers (GitHub code scanning included) reject as an invalid
    /// artifactLocation.uri, and left every other reserved URI character (<c>#</c>, <c>?</c>,
    /// <c>%</c> itself, ...) unescaped.
    /// </summary>
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
