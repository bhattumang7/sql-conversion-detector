using System.Globalization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Lineage;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting.Readable;

/// <summary>
/// Turns a <see cref="ScanReport"/> into the human-facing report - the one a reader who does
/// not want to walk the JSON by hand actually reads. Every finding the JSON carries is here
/// too; what changes is the shape: findings are grouped by what is wrong with them, each group
/// is explained once in prose rather than once per row, and each row says where the predicate
/// is, which base column it lands on, whether that column is indexed, and which view layer
/// introduced the mismatch.
///
/// Sections appear only when they have something to report.
/// </summary>
public static class ReadableScanReportWriter
{
    /// <summary>Every finding table's first column header - a predicate's source location.</summary>
    private const string WhereHeader = "Where";

    /// <summary>Shared across every finding table that has one - avoids a repeated literal Sonar flags at 4+ occurrences.</summary>
    private const string ColumnHeader = "Column";

    /// <summary>Shared across every "we don't have a name for this" fallback string.</summary>
    private const string UnknownDisplay = "unknown";

    public static string Write(ScanReport report, string title, ReadableStyle style, string? pathBase = null, ReadableVerbosity verbosity = ReadableVerbosity.Brief) =>
        ReadableDocumentRenderer.Render(BuildDocument(report, title, pathBase, verbosity), style);

    /// <summary>
    /// Builds the document for a whole scan, title included. Corpus reports embed a repo's
    /// sections into a larger document instead - see <see cref="BuildSections"/>.
    /// </summary>
    public static ReadableDocument BuildDocument(ScanReport report, string title, string? pathBase = null, ReadableVerbosity verbosity = ReadableVerbosity.Brief)
    {
        List<ReadableBlock> blocks = [new ReadableBlock.Heading(1, title)];
        blocks.AddRange(BuildSections(report, 2, pathBase, verbosity));
        return new ReadableDocument(blocks);
    }

    /// <summary>
    /// The report body without a title, at a caller-chosen heading level, so one repo's report
    /// can sit under a corpus-wide heading without its sections outranking it.
    /// </summary>
    public static IReadOnlyList<ReadableBlock> BuildSections(ScanReport report, int headingLevel, string? pathBase = null, ReadableVerbosity verbosity = ReadableVerbosity.Brief)
    {
        ArgumentNullException.ThrowIfNull(report);

        var blocks = new List<ReadableBlock>();
        blocks.AddRange(Summary(report, headingLevel));
        blocks.AddRange(CollationConflicts(report, headingLevel, pathBase));
        blocks.AddRange(TypedSection(
            report, Verdict.ScanForced, headingLevel, pathBase,
            "Implicit conversions that force a scan",
            "The column side of these comparisons is converted, not the value side, so no index on the column can be seeked - the engine reads every row and converts it before it can compare. These are the findings this tool exists to find; the ones on an indexed column, inherited through a view layer, are listed first."));
        blocks.AddRange(TypedSection(
            report, Verdict.RangeSeek, headingLevel, pathBase,
            "Implicit conversions that degrade the seek",
            "The column still converts, but under a Windows collation the engine can bound the search with GetRangeThroughConvert - cheaper than the scan above, dearer than the seek the column would have had with matching types."));
        blocks.AddRange(ExpressionDerived(report, headingLevel, pathBase));
        blocks.AddRange(WriteLoss(report, headingLevel, pathBase));
        blocks.AddRange(Tier1(report, headingLevel, pathBase));
        blocks.AddRange(TvfFence(report, headingLevel, pathBase));
        blocks.AddRange(ScalarUdf(report, headingLevel, pathBase));
        blocks.AddRange(ColumnCollationDrift(report, headingLevel, pathBase));
        blocks.AddRange(CrossTableTypeDrift(report, headingLevel, pathBase));
        blocks.AddRange(ProcCallArgumentMismatch(report, headingLevel, pathBase));
        blocks.AddRange(TemporalBoundary(report, headingLevel, pathBase));
        blocks.AddRange(MaxTypedColumn(report, headingLevel, pathBase));
        blocks.AddRange(OversizedParameter(report, headingLevel, pathBase));
        blocks.AddRange(UnderLengthParameter(report, headingLevel, pathBase));
        blocks.AddRange(AnsiPaddingMismatch(report, headingLevel, pathBase));
        blocks.AddRange(CatchAllPredicate(report, headingLevel, pathBase));
        blocks.AddRange(LocalVariablePredicate(report, headingLevel, pathBase));
        blocks.AddRange(NotInNullableSubquery(report, headingLevel, pathBase));
        blocks.AddRange(NonUniqueUpdateSource(report, headingLevel, pathBase));
        blocks.AddRange(ForcedSerial(report, headingLevel, pathBase));
        blocks.AddRange(UntrustedConstraint(report, headingLevel, pathBase));
        blocks.AddRange(CascadingForeignKey(report, headingLevel, pathBase));
        blocks.AddRange(MultiReferencedCte(report, headingLevel, pathBase));
        blocks.AddRange(NestedViewDepth(report, headingLevel, pathBase));
        blocks.AddRange(PostExpansionJoinWidth(report, headingLevel, pathBase));
        blocks.AddRange(SelectStarView(report, headingLevel, pathBase));
        blocks.AddRange(PartialCompositeForeignKeyJoin(report, headingLevel, pathBase));
        blocks.AddRange(SetOption(report, headingLevel, pathBase));
        blocks.AddRange(UnparameterizedDynamicSql(report, headingLevel, pathBase));
        blocks.AddRange(TypedSection(
            report, Verdict.Unknown, headingLevel, pathBase,
            "Comparisons that could not be classified",
            "Something the verdict rules need was missing or ambiguous - most often a collation that no DDL in the scan pinned down. These are neither clean nor flagged; they are unanswered, and they are listed rather than dropped so the counts above cannot be read as covering them.",
            verbosity));
        blocks.AddRange(TypedSection(
            report, Verdict.OperandClash, headingLevel, pathBase,
            "Comparisons between genuinely incompatible types",
            "The oracle-probed type matrix confirms this exact type pair does not compile as a comparison at all (e.g. TIME vs a date-family type, or a GUID vs a string) - distinct from an unclassified comparison above: this one has a definitive answer, and the answer is that the comparison itself cannot run as written."));
        blocks.AddRange(DynamicSql(report, headingLevel, pathBase, verbosity));
        blocks.AddRange(ParseFailures(report, headingLevel, pathBase, verbosity));
        blocks.AddRange(SkippedConstructs(report, headingLevel));

        return blocks;
    }

    /// <summary>
    /// The pointer line a gated coverage/caveat section renders instead of its row table under
    /// <see cref="ReadableVerbosity.Brief"/> - always names the exact count (never "some", never
    /// omitted) so brief mode states less DETAIL, never a less honest COUNT.
    /// </summary>
    private static ReadableBlock.Paragraph BriefPointer(int count, string noun) =>
        new($"{Count(count, noun)} - not listed individually here; re-run with --verbosity full to see each one.");

    private static IEnumerable<ReadableBlock> Summary(ScanReport report, int level)
    {
        var health = report.ParseHealth;
        var summary = report.TypedPredicateSummary;

        yield return new ReadableBlock.Heading(level, "Summary");

        var parsed = health.TotalFiles - health.FilesWithErrors;
        yield return new ReadableBlock.Paragraph(
            $"{Count(health.TotalFiles, "file")} scanned, {parsed} parsed cleanly ({Percent(health.ParseSuccessRate)}).");

        var counts = new List<IReadOnlyList<string>>();
        AddCount(counts, "Collation conflicts (query does not compile)", report.CollationConflictFindings.Count);
        AddCount(counts, "Implicit conversions forcing a scan", summary.ScanForcedCount, summary.DistinctScanForcedCount);
        AddCount(counts, "Implicit conversions degrading the seek", summary.RangeSeekCount, summary.DistinctRangeSeekCount);
        AddCount(counts, "Expression-derived columns in predicates", report.ExpressionDerivedFindings.Count);
        AddCount(counts, "INSERT/UPDATE assignments risking silent data loss", report.WriteLossFindings.Count);
        AddCount(counts, "Non-sargable predicate patterns", report.Tier1Findings.Count);
        AddCount(counts, "Multi-statement/CLR TVF references acting as optimization fences", report.TvfFenceFindings.Count);
        AddCount(counts, "Scalar UDF calls (per-row cost, non-sargable when predicate-context)", report.ScalarUdfFindings.Count);
        AddCount(counts, "Columns whose collation drifts from the database/tempdb default", report.ColumnCollationDriftFindings.Count);
        AddCount(counts, "Foreign-key column pairs whose types/collations drift", report.CrossTableTypeDriftFindings.Count);
        AddCount(counts, "EXEC call-site arguments risking silent data loss at the parameter boundary", report.ProcCallArgumentMismatchFindings.Count);
        AddCount(counts, "BETWEEN predicates silently excluding rows at an imprecise end-of-period boundary", report.TemporalBoundaryFindings.Count);
        AddCount(counts, "MAX-typed columns (can never be an index key)", report.MaxTypedColumnFindings.Count);
        AddCount(counts, "Predicates comparing a column against an oversized parameter/variable", report.OversizedParameterFindings.Count);
        AddCount(counts, "Predicates comparing a column against an under-length parameter/variable", report.UnderLengthParameterFindings.Count);
        AddCount(counts, "LIKE predicates that can never match a non-ANSI-padded column", report.AnsiPaddingMismatchFindings.Count);
        AddCount(counts, "Catch-all / kitchen-sink optional-filter predicates", report.CatchAllPredicateFindings.Count);
        AddCount(counts, "Predicates against a local variable (cardinality-estimate risk only)", report.LocalVariablePredicateFindings.Count);
        AddCount(counts, "NOT IN predicates over a nullable subquery column (correctness trap)", report.NotInNullableSubqueryFindings.Count);
        AddCount(counts, "UPDATE...FROM joins whose source carries no uniqueness guarantee", report.NonUniqueUpdateSourceFindings.Count);
        AddCount(counts, "Constructs that force a statement/query plan serial", report.ForcedSerialFindings.Count);
        AddCount(counts, "Untrusted FK/CHECK constraints", report.UntrustedConstraintFindings.Count);
        AddCount(counts, "Foreign keys with a cascading ON DELETE/UPDATE action", report.CascadingForeignKeyFindings.Count);
        AddCount(counts, "CTEs referenced 2+ times downstream of their own WITH clause", report.MultiReferencedCteFindings.Count);
        AddCount(counts, "Views/inline TVFs nested 2+ view/TVF layers deep", report.NestedViewDepthFindings.Count);
        AddCount(counts, "Queries whose expanded join width exceeds their written FROM/JOIN count", report.PostExpansionJoinWidthFindings.Count);
        AddCount(counts, "Consumers narrowing a nested SELECT * view's frozen column list", report.SelectStarViewFindings.Count);
        AddCount(counts, "JOINs matching some but not all of a composite foreign key's columns", report.PartialCompositeForeignKeyJoinFindings.Count);
        AddCount(counts, "SET options silently disabling a filtered index/indexed view the module touches", report.SetOptionFindings.Count);
        AddCount(counts, "Dynamic SQL call sites concatenating a proven-constant value instead of parameterizing it", report.UnparameterizedDynamicSqlFindings.Count);
        AddCount(counts, "Comparisons that could not be classified", summary.UnknownCount);
        AddCount(counts, "Comparisons between genuinely incompatible types", summary.OperandClashCount);
        AddCount(counts, "Dynamic SQL call sites not statically analyzable", report.DynamicSqlSummary.UnanalyzableCount + report.DynamicSqlSummary.InnerParseFailedCount);
        AddCount(counts, "Dynamic SQL call sites partially analyzed (a fragment was elided)", report.DynamicSqlSummary.PartiallyAnalyzedCount);
        AddCount(counts, "Files that failed to parse", health.FilesWithErrors);
        AddCount(counts, "Constructs skipped as out of scope", report.SkippedConstructSummary.TotalCount);

        if (counts.Count == 0)
        {
            yield return new ReadableBlock.Paragraph("No findings.");
        }
        else
        {
            yield return new ReadableBlock.Table(["What", "Occurrences", "Distinct"], counts);
        }

        // The base rate. A finding count with no denominator cannot be checked against anything,
        // and "N found" reads very differently against 60 comparisons than against 60,000.
        yield return new ReadableBlock.Paragraph(
            $"Base rate: {Count(summary.TotalClassified, "column comparison")} classified " +
            $"({summary.DistinctTotalClassified} distinct), of which {summary.SeekPreservedCount} keep their seek. " +
            "Seek-preserving comparisons are counted but not listed - there is nothing to act on.");
    }

    private static void AddCount(List<IReadOnlyList<string>> rows, string label, int occurrences, int? distinct = null)
    {
        if (occurrences == 0)
        {
            return;
        }

        rows.Add([
            label,
            occurrences.ToString(CultureInfo.InvariantCulture),
            distinct is { } d ? d.ToString(CultureInfo.InvariantCulture) : "-",
        ]);
    }

    private static IEnumerable<ReadableBlock> TypedSection(
        ScanReport report, Verdict verdict, int level, string? pathBase, string title, string explanation,
        ReadableVerbosity verbosity = ReadableVerbosity.Full)
    {
        var findings = report.TypedFindings.Where(f => f.Verdict == verdict).ToList();
        if (findings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"{title} ({findings.Count})");
        yield return new ReadableBlock.Paragraph(explanation);

        if (verbosity == ReadableVerbosity.Brief)
        {
            yield return BriefPointer(findings.Count, "comparison");
            yield break;
        }

        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Column type", "Compared with", "Indexed", "Introduced by"],
            [.. findings.Select(f => TypedRow(f, pathBase))]);
    }

    private static IReadOnlyList<string> TypedRow(TypedPredicateFinding finding, string? pathBase) =>
    [
        Where(finding.SourcePath, finding.Line, finding.DynamicSqlCallSite, pathBase, finding.Confidence),
        $"{finding.Column.TableQualifiedName}.{finding.Column.ColumnName}",
        DescribeType(finding.Column.Type),
        $"{finding.Operator} {DescribeOperand(finding.OtherOperand)}{(finding.UnknownReason is { } reason ? $" ({reason})" : string.Empty)}",
        DescribeIndexed(finding.Column),
        DescribeOrigin(finding.Column, pathBase),
    ];

    private static string DescribeIndexed(PredicateOperand.Column column)
    {
        if (!column.Indexed)
        {
            return "no";
        }

        return column.IndexName is { } indexName ? $"yes ({indexName})" : "yes";
    }

    private static IEnumerable<ReadableBlock> CollationConflicts(ScanReport report, int level, string? pathBase)
    {
        if (report.CollationConflictFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Collation conflicts ({report.CollationConflictFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "These comparisons put two explicitly different collations on either side, which SQL Server rejects at compile time (Msg 468) - the query does not run at all. That outranks any seek-versus-scan question, so they are listed first.");
        yield return new ReadableBlock.Table(
            [WhereHeader, "Left", "Right", "Operator"],
            [.. report.CollationConflictFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                $"{f.FirstTableQualifiedName}.{f.FirstColumnName} COLLATE {f.FirstCollationName}",
                $"{f.SecondTableQualifiedName}.{f.SecondColumnName} COLLATE {f.SecondCollationName}",
                f.Operator,
            })]);
    }

    private static IEnumerable<ReadableBlock> ExpressionDerived(ScanReport report, int level, string? pathBase)
    {
        if (report.ExpressionDerivedFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Expression-derived columns in predicates ({report.ExpressionDerivedFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "By the time these columns reach the predicate they are the result of an expression a view or function computed, not a stored column. An index on whatever feeds them cannot be seeked through that expression. " +
            "The ones that DO have a real index sitting underneath the expression - the cases actually worth rewriting the predicate for - are listed first.");
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Computed by", "Underlying base columns"],
            [.. report.ExpressionDerivedFindings
                .OrderByDescending(f => f.UnderlyingBaseColumns.Any(bc => bc.Indexed))
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                f.ColumnName,
                f.TransformationChain.Count == 0
                    ? UnknownDisplay
                    : string.Join(" <- ", f.TransformationChain.Select(site => DescribeTransformationSite(site, pathBase))),
                f.UnderlyingBaseColumns.Count == 0
                    ? "none traceable"
                    : string.Join(", ", f.UnderlyingBaseColumns.Select(bc => $"{bc.TableQualifiedName}.{bc.ColumnName}{(bc.Indexed ? " (indexed)" : string.Empty)}")),
            })]);
    }

    private static IEnumerable<ReadableBlock> WriteLoss(ScanReport report, int level, string? pathBase)
    {
        if (report.WriteLossFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"INSERT/UPDATE assignments risking silent data loss ({report.WriteLossFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "Each of these writes a value whose static type carries more information than its target column can hold - T-SQL rounds, truncates, or replaces the value with no error raised, so nothing here shows up as a failed statement. A case T-SQL itself refuses to run (a too-long string, an overflowing integer) is not listed - those already fail loudly on their own.");
        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Target type", "Source type", "Risk"],
            [.. report.WriteLossFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.TargetType.ToString(),
                f.SourceType.ToString(),
                DescribeWriteLossKind(f.Kind),
            })]);
    }

    private static string DescribeWriteLossKind(WriteLossKind kind) => kind switch
    {
        WriteLossKind.UnicodeToNonUnicodeReplacement => "Unicode characters outside the target's codepage become '?'",
        WriteLossKind.ApproximateToExactTruncation => "fractional part silently dropped",
        WriteLossKind.NumericScaleNarrowing => "digits past the target's scale silently rounded away",
        WriteLossKind.TemporalPrecisionLoss => "time-of-day silently dropped",
        _ => kind.ToString(),
    };

    private static IEnumerable<ReadableBlock> Tier1(ScanReport report, int level, string? pathBase)
    {
        if (report.Tier1Findings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Non-sargable predicate patterns ({report.Tier1Findings.Count})");
        yield return new ReadableBlock.Paragraph(
            "These are visible in the SQL text alone, with no type information needed: the column is not left bare on its side of the comparison, so an index on it cannot be seeked. Ones on a column confirmed to be indexed come first within each pattern.");

        foreach (var group in report.Tier1Findings
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key))
        {
            var ordered = group
                .OrderByDescending(f => f.Indexed == true)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ToList();

            yield return new ReadableBlock.Heading(level + 1, $"{Tier1Title(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Paragraph(Tier1Explanation(group.Key));
            yield return new ReadableBlock.Table(
                [WhereHeader, ColumnHeader, "Indexed", "Detail"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                    f.TableQualifiedName is { } table ? $"{table}.{f.ColumnName}" : f.ColumnName,
                    f.Indexed switch { true => "yes", false => "no", null => "unresolved" },
                    f.Detail ?? "-",
                })]);
        }
    }

    private static string Tier1Title(SargabilityFindingKind kind) => kind switch
    {
        SargabilityFindingKind.FunctionWrappedColumn => "Column wrapped in a function",
        SargabilityFindingKind.CastOrConvertOnColumn => "CAST/CONVERT applied to the column",
        SargabilityFindingKind.ColumnArithmetic => "Arithmetic on the column",
        SargabilityFindingKind.LeadingWildcardLike => "LIKE with a leading wildcard",
        SargabilityFindingKind.CaseFoldOnColumn => "UPPER/LOWER applied to the column",
        SargabilityFindingKind.DateFunctionOnColumn => "Date-part function applied to the column",
        SargabilityFindingKind.CharindexOrLeftOnColumn => "CHARINDEX/LEFT applied to the column",
        _ => "LIKE with a non-literal pattern",
    };

    private static string Tier1Explanation(SargabilityFindingKind kind) => kind switch
    {
        SargabilityFindingKind.FunctionWrappedColumn =>
            "The index stores the column's values, not the function's results, so the engine must compute the function for every row before it can compare.",
        SargabilityFindingKind.CastOrConvertOnColumn =>
            "Same as any other function on the column - the converted value is not what the index is ordered by. Converting the other side instead usually keeps the seek.",
        SargabilityFindingKind.ColumnArithmetic =>
            "The column is part of an expression rather than standing alone. Moving the arithmetic to the other side of the comparison usually restores the seek.",
        SargabilityFindingKind.LeadingWildcardLike =>
            "A pattern starting with % has no known prefix, and a b-tree can only seek on a prefix - the whole index or table is read.",
        SargabilityFindingKind.CaseFoldOnColumn =>
            "Oracle-verified: the wrap forces a scan regardless of the column's collation family - it is never a no-op for the PLAN, even when it's a no-op for the RESULT SET. See the finding's own Detail for whether this specific column's wrap is safe to delete or needs a real rewrite.",
        SargabilityFindingKind.DateFunctionOnColumn =>
            "Oracle-verified: the date-part function forces a per-row scan just like any other function wrap. Usually rewritable to a sargable literal date range (e.g. YEAR(col)=2024 becomes col >= '2024-01-01' AND col < '2025-01-01') that restores the seek.",
        SargabilityFindingKind.CharindexOrLeftOnColumn =>
            "See the finding's own Detail for whether this specific comparison is a prefix match with a real sargable rewrite (col LIKE 'x%'), or a genuine substring search with none.",
        _ =>
            "The pattern is a variable or expression, so the plan cannot be built around a known prefix; the engine has to assume the worst at compile time.",
    };

    private static IEnumerable<ReadableBlock> TvfFence(ScanReport report, int level, string? pathBase)
    {
        if (report.TvfFenceFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Multi-statement/CLR TVF references acting as optimization fences ({report.TvfFenceFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A multi-statement or CLR table-valued function's body is opaque to the optimizer: its result is materialized into a statistics-less worktable and the reference carries a fixed cardinality guess (1 row under the legacy CE, 100 under 2014+), which propagates into the surrounding plan's join order, join types and memory grant. The call site reads identically to a harmless inline TVF - only the catalog tells them apart. Correlated APPLY references and fences inherited invisibly through a view/TVF layer are listed first: no engine-version mitigation rescues either.");

        foreach (var group in report.TvfFenceFindings
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key))
        {
            var ordered = group
                .OrderByDescending(f => f.Depth)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ToList();

            yield return new ReadableBlock.Heading(level + 1, $"{TvfFenceTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Table(
                [WhereHeader, "Referenced", "Fence function", "Depth", "Origin", "Detail"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                    f.ReferencedObjectQualifiedName ?? "-",
                    f.FunctionQualifiedName is { } fn ? $"{fn} ({f.FunctionKind})" : "-",
                    f.Depth.ToString(CultureInfo.InvariantCulture),
                    f.OriginSourcePath is { } origin ? $"{Relative(origin, pathBase)}:{f.OriginLine.ToString(CultureInfo.InvariantCulture)}" : "-",
                    f.Kind == TvfFenceFindingKind.CorrelatedApply && f.CorrelatedOuterColumns is { Count: > 0 } cols
                        ? $"correlated on {string.Join(", ", cols)}"
                        : f.ReferenceFragmentText ?? "-",
                })]);
        }
    }

    private static string TvfFenceTitle(TvfFenceFindingKind kind) => kind switch
    {
        TvfFenceFindingKind.CorrelatedApply => "Correlated CROSS/OUTER APPLY (re-executes per outer row)",
        TvfFenceFindingKind.NestedUnderViewOrTvf => "Fence inherited through a view/TVF layer",
        TvfFenceFindingKind.FromOrJoin => "Direct FROM/JOIN reference",
        TvfFenceFindingKind.InsertExec => "INSERT ... EXEC (forced worktable materialization)",
        _ => "Standalone reference (fence present, nothing to poison)",
    };

    private static IEnumerable<ReadableBlock> ScalarUdf(ScanReport report, int level, string? pathBase)
    {
        if (report.ScalarUdfFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Scalar UDF calls ({report.ScalarUdfFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A scalar UDF executes once per row wherever it's called; pre-2019 (or on any engine when it proves non-inlineable) it also forces the whole plan serial. A predicate-context call additionally loses sargability, and a call reached through a view/iTVF's own expansion inherits the same cost invisibly at every consumer. Predicate-context and lineage-inherited calls are listed first; a call the engine itself inlines (2019+ FROID) is noted but ranked no higher than Unknown/NotInlineable ones.");

        foreach (var group in report.ScalarUdfFindings
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key))
        {
            var ordered = group
                .OrderByDescending(f => f.Depth)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ToList();

            yield return new ReadableBlock.Heading(level + 1, $"{ScalarUdfTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Table(
                [WhereHeader, "Function", "Context", "Inlineable", "Depth", "Origin", "Detail"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, f.DynamicSqlCallSite, pathBase, f.Confidence),
                    $"{f.FunctionQualifiedName} ({f.UdfKind})",
                    f.Context.ToString(),
                    ScalarUdfInlineabilityDisplay(f),
                    f.Depth.ToString(CultureInfo.InvariantCulture),
                    f.OriginSourcePath is { } origin ? $"{Relative(origin, pathBase)}:{f.OriginLine.ToString(CultureInfo.InvariantCulture)}" : "-",
                    ScalarUdfDetail(f),
                })]);
        }
    }

    private static IEnumerable<ReadableBlock> ColumnCollationDrift(ScanReport report, int level, string? pathBase)
    {
        if (report.ColumnCollationDriftFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Columns whose collation drifts from the default ({report.ColumnCollationDriftFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A conversion seed, not yet a comparison: this column's own collation differs from the database's default (or, for a temp table/table variable, from tempdb's own effective collation) - the classic setup for a future collation-conflict compile error or a forced-scan implicit conversion once a query actually compares it against something carrying the baseline collation.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "Column collation", "Baseline collation", "Object kind"],
            [.. report.ColumnCollationDriftFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ColumnCollationName,
                f.BaselineCollationName,
                f.IsTempObject ? "temp table/table variable" : "table",
            })]);
    }

    private static IEnumerable<ReadableBlock> CrossTableTypeDrift(ScanReport report, int level, string? pathBase)
    {
        if (report.CrossTableTypeDriftFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Foreign-key column pairs whose types drift ({report.CrossTableTypeDriftFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A conversion seed on a real foreign-key relationship: every JOIN that follows it risks the same column-side conversion the implicit-conversion stream classifies, whether or not any scanned query actually joins on it yet. Read live from sys.foreign_key_columns - always empty for a file-mode scan.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "Constraint", "Parent column", "Referenced column", "Collation differs"],
            [.. report.CrossTableTypeDriftFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ConstraintName,
                $"{f.ParentTableQualifiedName}.{f.ParentColumnName} ({f.ParentTypeDisplay})",
                $"{f.ReferencedTableQualifiedName}.{f.ReferencedColumnName} ({f.ReferencedTypeDisplay})",
                f.CollationDiffers.ToString(),
            })]);
    }

    private static IEnumerable<ReadableBlock> ProcCallArgumentMismatch(ScanReport report, int level, string? pathBase)
    {
        if (report.ProcCallArgumentMismatchFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"EXEC call-site argument mismatches ({report.ProcCallArgumentMismatchFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A real EXEC call site's caller-side variable has a declared type that risks silent data loss against the callee's own declared parameter type - an assignment-shaped conversion at parameter marshalling, not a predicate. This also primes the exact mismatched value for any comparison the callee's own body makes against a column using this parameter.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "Callee", "Parameter", "Caller variable", "Caller type", "Parameter type", "Risk"],
            [.. report.ProcCallArgumentMismatchFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.CalleeQualifiedName,
                f.FormalParameterName,
                f.CallerVariableName,
                f.CallerTypeDisplay,
                f.FormalParameterTypeDisplay,
                DescribeWriteLossKind(f.Kind),
            })]);
    }

    private static IEnumerable<ReadableBlock> TemporalBoundary(ScanReport report, int level, string? pathBase)
    {
        if (report.TemporalBoundaryFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"BETWEEN end-of-period boundary correctness bugs ({report.TemporalBoundaryFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A CORRECTNESS finding, not a sargability one - BETWEEN itself is perfectly sargable here. The upper bound literal has fewer fractional-second digits than the column's own declared TIME/DATETIME2/DATETIMEOFFSET precision, so rows whose value falls in that precision gap are silently excluded - oracle-confirmed directly (a DATETIME2(7) row at 23:59:59.9999999 is dropped by the classic '23:59:59.997' end-of-day literal). Rewrite as >= start AND < (start of the next period) instead, which has no precision gap to fall into.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "Column", "Column scale", "Boundary literal", "Literal fractional digits"],
            [.. report.TemporalBoundaryFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.ColumnScale.ToString(CultureInfo.InvariantCulture),
                f.BoundaryLiteralText,
                f.BoundaryLiteralFractionalDigits.ToString(CultureInfo.InvariantCulture),
            })]);
    }

    private static IEnumerable<ReadableBlock> MaxTypedColumn(ScanReport report, int level, string? pathBase)
    {
        if (report.MaxTypedColumnFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"MAX-typed columns ({report.MaxTypedColumnFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A structural catalog fact, not a comparison: VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX) columns can never be an index key column at all (SQL Server rejects them at CREATE INDEX time), so no predicate or join on them can ever seek, regardless of how they're used.");

        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Type"],
            [.. report.MaxTypedColumnFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.TypeDisplay,
            })]);
    }

    private static IEnumerable<ReadableBlock> OversizedParameter(ScanReport report, int level, string? pathBase)
    {
        if (report.OversizedParameterFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Predicates comparing a column against an oversized parameter ({report.OversizedParameterFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "Informational, not a plan-shape claim for this specific predicate - oracle-falsified that a bare equality predicate shows any memory-grant difference on its own. The risk is structural: the parameter/variable/expression on the other side is declared with a meaningfully longer length than the column, which risks memory-grant inflation once that value feeds a sort/hash operator elsewhere in the plan.");

        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Column length", "Other operand length"],
            [.. report.OversizedParameterFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.ColumnLength.ToString(CultureInfo.InvariantCulture),
                f.OtherOperandLength.ToString(CultureInfo.InvariantCulture),
            })]);
    }

    private static IEnumerable<ReadableBlock> UnderLengthParameter(ScanReport report, int level, string? pathBase)
    {
        if (report.UnderLengthParameterFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Predicates comparing a column against an under-length parameter ({report.UnderLengthParameterFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "The mirror of the oversized-parameter section above, but strictly worse: the parameter/variable/expression on the other side is declared SHORTER than the column - or with no explicit length at all (T-SQL defaults a length-less DECLARE/parameter to 1) - so the value is silently truncated before the predicate ever runs. Structural, not a per-instance proof (this pass never traces the variable's actual assigned value): it states the declared-length pairing risks truncation, the same honesty WriteLossFinding already applies to assignment-site truncation. Where the parameter feeds a LIKE pattern or a range bound, truncation changes what the comparison itself means, not just which exact value it excludes - marked in the Effect column.");

        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Column length", "Other operand length", "Operator", "Effect"],
            [.. report.UnderLengthParameterFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.ColumnLength.ToString(CultureInfo.InvariantCulture),
                f.IsImplicitDefault ? "none (defaults to 1)" : f.OtherOperandLength!.Value.ToString(CultureInfo.InvariantCulture),
                f.Operator,
                f.ChangesRangeOrPatternShape ? "changes pattern/range shape" : "truncates compared value",
            })]);
    }

    private static IEnumerable<ReadableBlock> AnsiPaddingMismatch(ScanReport report, int level, string? pathBase)
    {
        if (report.AnsiPaddingMismatchFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"LIKE predicates that can never match a non-ANSI-padded column ({report.AnsiPaddingMismatchFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A CORRECTNESS finding, not a plan-shape one: the column's own catalog flag (sys.columns.is_ansi_padded = 0) means trailing blanks are stripped at INSERT time, so the column can never store a value ending in whitespace at all. The LIKE pattern here has significant trailing whitespace, so this predicate can never match anything the column could ever contain - oracle-confirmed directly (real seeded rows) that a plain equality comparison is NOT affected the same way, since T-SQL trims trailing spaces for '=' regardless of padding; only LIKE, where a pattern's own trailing whitespace is never trimmed, shows the real difference.");

        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Pattern"],
            [.. report.AnsiPaddingMismatchFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.PatternLiteralText,
            })]);
    }

    private static IEnumerable<ReadableBlock> CatchAllPredicate(ScanReport report, int level, string? pathBase)
    {
        if (report.CatchAllPredicateFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Catch-all / kitchen-sink predicates ({report.CatchAllPredicateFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "The classic '(Col = @p OR @p IS NULL)' optional-filter idiom (Erland Sommarskog, \"Dynamic Search Conditions in T-SQL\") - one cached plan must stay correct for every NULL/non-NULL state of @p, typically forcing a scan regardless of what value a given call actually passes. Not a claim about what a specific already-compiled plan is doing right now - a structural risk report. Suppressed entirely (not merely downgraded) when the statement carries OPTION (RECOMPILE) or the procedure is WITH RECOMPILE, both of which let the optimizer see the real value on each call and fully resolve this risk. Rows on a confirmed-indexed column are listed first.");

        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Parameter", "Indexed"],
            [.. report.CatchAllPredicateFindings
                .OrderByDescending(f => f.Indexed)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.ParameterName,
                f.Indexed ? "yes" : "no",
            })]);
    }

    private static IEnumerable<ReadableBlock> LocalVariablePredicate(ScanReport report, int level, string? pathBase)
    {
        if (report.LocalVariablePredicateFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Predicates against a local variable, not a parameter ({report.LocalVariablePredicateFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "Purely informational, not a sargability claim: the predicate is still fully sargable and WILL seek if the column is indexed. The compared value came from a DECLARE'd local variable, not a formal parameter, so it is invisible to the cardinality estimator (Microsoft's own documented behavior - the optimizer falls back to the column's average-density statistic instead of a value-specific estimate). Whether a bad estimate actually matters depends on data-distribution facts this pass cannot see - listed for awareness, not as a proven defect. Suppressed entirely when the statement carries OPTION (RECOMPILE) or the procedure is WITH RECOMPILE, since a per-execution recompile lets the optimizer see the variable's real current value.");

        yield return new ReadableBlock.Table(
            [WhereHeader, ColumnHeader, "Variable", "Operator", "Indexed"],
            [.. report.LocalVariablePredicateFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                $"{f.TableQualifiedName}.{f.ColumnName}",
                f.VariableName,
                f.Operator,
                f.Indexed ? "yes" : "no",
            })]);
    }

    private static IEnumerable<ReadableBlock> NotInNullableSubquery(ScanReport report, int level, string? pathBase)
    {
        if (report.NotInNullableSubqueryFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"NOT IN over a nullable subquery column ({report.NotInNullableSubqueryFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "'x NOT IN (SELECT y FROM t)' where y is a nullable column - a three-valued-logic correctness trap, not a plan-shape one. The instant the subquery produces one NULL row, the whole predicate evaluates to UNKNOWN for every outer row, so the query silently returns ZERO rows instead of the expected anti-join result - independent of any index or plan choice. Never fires when the subquery column is NOT NULL, or when the subquery already filters it with an unconditional 'WHERE y IS NOT NULL'.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "Outer column", "Subquery column", "Indexed"],
            [.. report.NotInNullableSubqueryFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.OuterColumnName ?? "<expression>",
                $"{f.SubqueryTableQualifiedName}.{f.SubqueryColumnName}",
                f.SubqueryColumnIndexed ? "yes" : "no",
            })]);
    }

    private static IEnumerable<ReadableBlock> NonUniqueUpdateSource(ScanReport report, int level, string? pathBase)
    {
        if (report.NonUniqueUpdateSourceFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"UPDATE ... FROM without source uniqueness ({report.NonUniqueUpdateSourceFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "The joined source's own join columns carry no unique index/constraint - if a target row ever matches more than one source row, SQL Server silently picks a value from an unspecified one of them (plan-dependent, not guaranteed stable across executions). MERGE raises a hard error in this exact situation instead of picking silently. A structural defect, not a 'wrong for current data' one: no current duplicate has to exist for the statement to be unsafe, only the schema's own absence of a uniqueness guarantee. Never fires when the source's join columns are covered by a genuine unique index/constraint, or when the SET clause never reads from the non-unique source.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "Target", "Source", "Join columns", "SET columns"],
            [.. report.NonUniqueUpdateSourceFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.TargetTableQualifiedName,
                f.SourceTableQualifiedName,
                string.Join(", ", f.JoinColumnNames),
                string.Join(", ", f.SetColumnNames),
            })]);
    }

    private static IEnumerable<ReadableBlock> ForcedSerial(ScanReport report, int level, string? pathBase)
    {
        if (report.ForcedSerialFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Forced-serial constructs ({report.ForcedSerialFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "Three independent, oracle-confirmed constructs that force SQL Server to disable parallelism (effective MAXDOP 1) for the statement/query that contains them - a performance-cost finding, not a correctness one, since the result never changes, only its cost. A table-variable modification's forced-serial scope is the one containing statement, not the whole batch/procedure. A FAST_FORWARD cursor (or the equivalent bare FORWARD_ONLY READ_ONLY) forces its own defining query serial - the opposite of the common 'always use LOCAL FAST_FORWARD' fetch-overhead advice, which remains correct advice for a different reason. STATIC/KEYSET/DYNAMIC cursors do not trigger this.");

        foreach (var group in report.ForcedSerialFindings
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key))
        {
            var ordered = group.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();

            yield return new ReadableBlock.Heading(level + 1, $"{ForcedSerialTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Table(
                [WhereHeader, "Module", "Detail"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.ModuleQualifiedName,
                    f.DetailText ?? UnknownDisplay,
                })]);
        }
    }

    private static string ForcedSerialTitle(ForcedSerialFindingKind kind) => kind switch
    {
        ForcedSerialFindingKind.TableVariableModification => "Table variable modification",
        ForcedSerialFindingKind.FastForwardCursor => "FAST_FORWARD cursor",
        _ => "Non-parallelizable intrinsic",
    };

    private static IEnumerable<ReadableBlock> UntrustedConstraint(ScanReport report, int level, string? pathBase)
    {
        if (report.UntrustedConstraintFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Untrusted FK/CHECK constraints ({report.UntrustedConstraintFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A constraint the engine itself does not trust - almost always the result of a WITH NOCHECK re-enabling ALTER TABLE statement (the default there, the opposite of the default on the original ADD CONSTRAINT). The optimizer forfeits join-elimination and other constraint-based rewrites for every query touching it, and the constraint may not actually hold over existing rows. A disabled constraint is not reported - it's openly off, not silently weaker than it looks.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "Constraint", "Table", "Kind"],
            [.. report.UntrustedConstraintFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ConstraintName,
                f.TableQualifiedName,
                f.Kind == UntrustedConstraintFindingKind.ForeignKey ? "foreign key" : "CHECK constraint",
            })]);
    }

    private static IEnumerable<ReadableBlock> CascadingForeignKey(ScanReport report, int level, string? pathBase)
    {
        if (report.CascadingForeignKeyFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Cascading FK actions ({report.CascadingForeignKeyFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A foreign key with a non-NO_ACTION ON DELETE/ON UPDATE action - a single DML statement against the referenced table silently touches every dependent row in the child table too, with no visible predicate change at the call site. Purely informational: this states the fact, not a proven cost - how many rows and how often depends on data this pass cannot see.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "Constraint", "Parent", "Referenced", "Delete action", "Update action"],
            [.. report.CascadingForeignKeyFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ConstraintName,
                f.ParentTableQualifiedName,
                f.ReferencedTableQualifiedName,
                f.DeleteAction.ToString(),
                f.UpdateAction.ToString(),
            })]);
    }

    private static IEnumerable<ReadableBlock> MultiReferencedCte(ScanReport report, int level, string? pathBase)
    {
        if (report.MultiReferencedCteFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Multi-referenced CTEs ({report.MultiReferencedCteFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "SQL Server does not materialize a plain CTE once and reuse it - each reference downstream of the WITH clause independently re-runs the CTE's own defining query, confirmed directly against the oracle (a base table's own scan count doubled under a CTE referenced twice). A self-reference inside a recursive CTE's own body is never counted - that's the structurally mandated recursion mechanism, not optional re-invocation.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "CTE", "References"],
            [.. report.MultiReferencedCteFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.CteName,
                f.ReferenceCount.ToString(CultureInfo.InvariantCulture),
            })]);
    }

    private static IEnumerable<ReadableBlock> NestedViewDepth(ScanReport report, int level, string? pathBase)
    {
        if (report.NestedViewDepthFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Nested-view depth ({report.NestedViewDepthFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            $"A view/inline TVF nested {NestedViewDepthScanner.DepthThreshold}+ view/TVF layers deep before reaching a base table - structural depth, not a claim the query is currently slow. A change to a base table now has to be traced through multiple independent view layers before its blast radius is understood, and each layer is a place a SELECT */column-list mismatch or silent type widening can hide. Catalog/lineage-only, reported once per view regardless of whether any scanned query calls it.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "View", "Depth", "Chain", "Base tables"],
            [.. report.NestedViewDepthFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ViewQualifiedName,
                f.Depth.ToString(CultureInfo.InvariantCulture),
                string.Join(" -> ", f.Chain),
                string.Join(", ", f.BaseTables),
            })]);
    }

    private static IEnumerable<ReadableBlock> PostExpansionJoinWidth(ScanReport report, int level, string? pathBase)
    {
        if (report.PostExpansionJoinWidthFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Post-expansion join width ({report.PostExpansionJoinWidthFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "The written FROM/JOIN table count is meaningless when half the sources are views - the number that matters is the EXPANDED one, base tables after resolving every view/inline-TVF reference transitively. Ranked by the gap between written and expanded count. Deliberately makes no claim about a specific 'past N the optimizer gives up exhaustive search' threshold - that number is unconfirmed folklore, not yet oracle-verified on this engine.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "Written", "Expanded", "Inflating source(s)", "Unexpanded?"],
            [.. report.PostExpansionJoinWidthFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.WrittenCount.ToString(CultureInfo.InvariantCulture),
                f.ExpandedCount.ToString(CultureInfo.InvariantCulture),
                string.Join(", ", f.InflatingSources),
                f.PartiallyUnexpanded ? "yes" : "no",
            })]);
    }

    private static IEnumerable<ReadableBlock> SelectStarView(ScanReport report, int level, string? pathBase)
    {
        if (report.SelectStarViewFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"SELECT * inside a nested view/TVF ({report.SelectStarViewFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A view/inline TVF nested 1+ view/TVF layers deep whose own outermost SELECT is a bare or qualified * - its column list is frozen at CREATE/ALTER time and silently disagrees with the base table after any change, confirmed to survive even a live describe-only probe and real execution until sp_refreshview runs. Only listed here where a real consuming query explicitly selects a strict, named subset of the view's full column set - a consumer doing SELECT * from the view never narrows anything and is never matched.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "View", "View columns", "Consumer selects"],
            [.. report.SelectStarViewFindings.Select(f => new List<string>
            {
                Where(f.ConsumerSourcePath, f.ConsumerLine, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ViewQualifiedName,
                $"{f.ViewFullColumns.Count} ({string.Join(", ", f.ViewFullColumns)})",
                string.Join(", ", f.ConsumerSelectedColumns),
            })]);
    }

    private static IEnumerable<ReadableBlock> UnparameterizedDynamicSql(ScanReport report, int level, string? pathBase)
    {
        if (report.UnparameterizedDynamicSqlFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Concatenated values in dynamic SQL ({report.UnparameterizedDynamicSqlFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A value this scanner proved constant (CLAUDE.md's Tier A dynamic-SQL folding) was spliced into an EXEC/sp_executesql call's own SQL text via string concatenation, rather than authored as one fixed literal or passed through sp_executesql's own @params. Every distinct concatenated value compiles its own cached plan - real plan-cache pollution, oracle-confirmed. The 'EXEC(string), sp_executesql available' kind fires only on a genuine EXEC(string)/EXEC(@sql) call site and names the specific fix: switch to sp_executesql and pass the value as a real parameter.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "Kind"],
            [.. report.UnparameterizedDynamicSqlFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.Kind == UnparameterizedDynamicSqlFindingKind.ExecStringConcatenatesParameterizableValue
                    ? "EXEC(string), sp_executesql available"
                    : "Concatenated value in constant SQL",
            })]);
    }

    private static IEnumerable<ReadableBlock> PartialCompositeForeignKeyJoin(ScanReport report, int level, string? pathBase)
    {
        if (report.PartialCompositeForeignKeyJoinFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"JOINs matching part of a composite foreign key ({report.PartialCompositeForeignKeyJoinFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "A CORRECTNESS and plan defect, not a lost seek: this join equates some but not all of a real composite foreign key's column pairs, and the omitted column(s) are not covered anywhere else in the statement - a parent row can match more than one child row than the declared relationship allows, silently multiplying rows through the join. Reported at MEDIUM confidence by default: a narrower join can be a genuine, deliberate fan-out (e.g. joining every historical revision), which static analysis alone cannot always tell apart from a forgotten column - review each one rather than treating it as a certain bug.");

        yield return new ReadableBlock.Table(
            [WhereHeader, "Constraint", "Tables", "Matched columns", "Missing columns"],
            [.. report.PartialCompositeForeignKeyJoinFindings.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                f.ConstraintName,
                $"{f.ParentTableQualifiedName} -> {f.ReferencedTableQualifiedName}",
                string.Join(", ", f.MatchedColumnPairs.Select(p => $"{p.ParentColumnName}={p.ReferencedColumnName}")),
                string.Join(", ", f.MissingColumnPairs.Select(p => $"{p.ParentColumnName}={p.ReferencedColumnName}")),
            })]);
    }

    private static IEnumerable<ReadableBlock> SetOption(ScanReport report, int level, string? pathBase)
    {
        if (report.SetOptionFindings.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"SET options silently disabling a filtered index/indexed view ({report.SetOptionFindings.Count})");
        yield return new ReadableBlock.Paragraph(
            "QUOTED_IDENTIFIER OFF, ANSI_NULLS OFF, SET NUMERIC_ROUNDABORT ON, SET ANSI_WARNINGS OFF, and SET CONCAT_NULL_YIELDS_NULL OFF each independently make a filtered index or an indexed view unusable by the optimizer, silently falling back to a base-table/heap scan - none shows up in the query text as anything resembling a predicate, so the plan consequence is invisible at the call site. Oracle-confirmed directly (real seeded data, both a filtered index and an indexed view). Only reported when this module's own body was proven to touch a filtered index or an indexed view (directly, or through a referenced view however many layers down) - see each row's own touched object. SET ARITHABORT OFF was investigated and deliberately excluded: oracle-probed directly, it changed neither plan at all on this engine version/edition, contradicting the checklist's original premise that lumped all six options together.");

        foreach (var group in report.SetOptionFindings
            .GroupBy(f => f.Kind)
            .OrderBy(g => g.Key))
        {
            var ordered = group.OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column).ToList();

            yield return new ReadableBlock.Heading(level + 1, $"{SetOptionTitle(group.Key)} ({ordered.Count})");
            yield return new ReadableBlock.Table(
                [WhereHeader, "Module", "Touched object", "Kind"],
                [.. ordered.Select(f => new List<string>
                {
                    Where(f.SourcePath, f.Line, dynamicSqlCallSite: null, pathBase, f.Confidence),
                    f.ModuleQualifiedName,
                    f.TouchedObjectQualifiedName is { } touched
                        ? $"{touched}{(f.TouchedIndexName is { } idx ? $".{idx}" : string.Empty)}"
                        : UnknownDisplay,
                    f.TouchedIsIndexedView ? "indexed view" : "filtered index",
                })]);
        }
    }

    private static string SetOptionTitle(SetOptionFindingKind kind) => kind switch
    {
        SetOptionFindingKind.QuotedIdentifierOffBlocksIndexedFeature => "Module compiled under QUOTED_IDENTIFIER OFF",
        SetOptionFindingKind.AnsiNullsOffBlocksIndexedFeature => "Module compiled under ANSI_NULLS OFF",
        SetOptionFindingKind.NumericRoundabortOnBlocksIndexedFeature => "SET NUMERIC_ROUNDABORT ON",
        SetOptionFindingKind.AnsiWarningsOffBlocksIndexedFeature => "SET ANSI_WARNINGS OFF",
        _ => "SET CONCAT_NULL_YIELDS_NULL OFF",
    };

    private static string ScalarUdfTitle(ScalarUdfFindingKind kind) => kind switch
    {
        ScalarUdfFindingKind.PredicateInvocation => "Called in a predicate (non-sargable, per-row)",
        ScalarUdfFindingKind.NestedUnderViewOrTvf => "Reached through a view/iTVF layer",
        ScalarUdfFindingKind.SchemaDependency => "Called from a computed column/DEFAULT/CHECK constraint",
        _ => "Called outside a predicate (per-row, sargability unaffected)",
    };

    private static string ScalarUdfInlineabilityDisplay(ScalarUdfFinding finding) => finding.Inlineability switch
    {
        ScalarUdfInlineability.Inlineable => "yes (2019+ FROID)",
        ScalarUdfInlineability.NotInlineable => "no",
        _ => UnknownDisplay,
    };

    private static string ScalarUdfDetail(ScalarUdfFinding finding)
    {
        var parts = new List<string>();
        if (finding.InlineabilityBlocker is { Length: > 0 } blocker)
        {
            parts.Add(blocker);
        }

        if (finding.ConstantArgumentsNotFolded)
        {
            parts.Add("non-schemabound, literal arguments not constant-folded");
        }

        if (finding.UdfKind == ScalarUdfKind.Clr && finding.ClrDataAccess is { } dataAccess)
        {
            parts.Add(dataAccess ? "CLR, data access" : "CLR, no data access");
        }

        return parts.Count == 0 ? finding.ReferenceFragmentText ?? "-" : string.Join("; ", parts);
    }

    private static IEnumerable<ReadableBlock> DynamicSql(ScanReport report, int level, string? pathBase, ReadableVerbosity verbosity)
    {
        var unresolved = report.DynamicSqlFindings
            .Where(f => f.Outcome != DynamicSqlOutcome.AnalyzedLiteral)
            .ToList();

        if (unresolved.Count == 0)
        {
            yield break;
        }

        var summary = report.DynamicSqlSummary;
        yield return new ReadableBlock.Heading(level, $"Dynamic SQL not fully analyzed ({unresolved.Count})");
        yield return new ReadableBlock.Paragraph(
            $"{summary.AnalyzedCount} of {Count(summary.TotalCallSites, "dynamic SQL call site")} had a provably-constant argument and were analyzed like ordinary SQL. " +
            "The rest are listed here rather than counted as clean: whatever wasn't examined - the whole argument, or (for a partially-analyzed site) just the elided fragment - is never silently assumed safe.");

        if (verbosity == ReadableVerbosity.Brief)
        {
            yield return BriefPointer(unresolved.Count, "call site");
            yield break;
        }

        yield return new ReadableBlock.Table(
            [WhereHeader, "Outcome", "Reason"],
            [.. unresolved.Select(f => new List<string>
            {
                Where(f.SourcePath, f.Line, null, pathBase),
                DynamicSqlOutcomeLabel(f.Outcome),
                f.Reason ?? "-",
            })]);
    }

    private static string DynamicSqlOutcomeLabel(DynamicSqlOutcome outcome) => outcome switch
    {
        DynamicSqlOutcome.InnerParseFailed => "constant, but did not parse as T-SQL",
        DynamicSqlOutcome.PartiallyAnalyzed => "partially analyzed - an unresolvable fragment was elided",
        _ => "not provably constant",
    };

    private static IEnumerable<ReadableBlock> ParseFailures(ScanReport report, int level, string? pathBase, ReadableVerbosity verbosity)
    {
        var failed = report.ParseHealth.Files.Where(f => f.Errors.Count > 0).ToList();
        if (failed.Count == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Files with parse errors ({failed.Count})");
        yield return new ReadableBlock.Paragraph(
            "A batch containing a syntax error is dropped, not the whole file, so these files still contributed whatever else they contained. What the failing batches held was never analyzed - if the rate here is high, the files are likely not T-SQL at all.");

        if (verbosity == ReadableVerbosity.Brief)
        {
            yield return BriefPointer(failed.Count, "file");
            yield break;
        }

        yield return new ReadableBlock.Table(
            ["File", "Errors", "Batches kept", "First error"],
            [.. failed.Select(f => new List<string>
            {
                Relative(f.Path, pathBase),
                f.Errors.Count.ToString(CultureInfo.InvariantCulture),
                f.BatchCount.ToString(CultureInfo.InvariantCulture),
                $"line {f.Errors[0].Line.ToString(CultureInfo.InvariantCulture)}: {f.Errors[0].Message}",
            })]);
    }

    private static IEnumerable<ReadableBlock> SkippedConstructs(ScanReport report, int level)
    {
        var summary = report.SkippedConstructSummary;
        if (summary.TotalCount == 0)
        {
            yield break;
        }

        yield return new ReadableBlock.Heading(level, $"Constructs skipped as out of scope ({summary.TotalCount})");
        yield return new ReadableBlock.Paragraph(
            "Parsed, recognised, and deliberately not analyzed. They are neither findings nor evidence of cleanliness - they are the part of the scanned SQL this tool does not claim to cover.");
        yield return new ReadableBlock.Table(
            ["Construct", "Pass", "Count"],
            [.. report.SkippedConstructs
                .GroupBy(s => (s.ConstructKind, s.Pass))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key.ConstructKind, StringComparer.Ordinal)
                .Select(g => new List<string>
                {
                    g.Key.ConstructKind,
                    DescribePass(g.Key.Pass),
                    g.Count().ToString(CultureInfo.InvariantCulture),
                })]);
    }

    private static string DescribePass(AnalysisPass pass) => pass switch
    {
        AnalysisPass.Catalog => "catalog",
        AnalysisPass.Lineage => "lineage",
        _ => "predicates",
    };

    private static string Where(string sourcePath, int line, SourceSpan? dynamicSqlCallSite, string? pathBase, FindingConfidence confidence = FindingConfidence.High)
    {
        var location = $"{Relative(sourcePath, pathBase)}:{line.ToString(CultureInfo.InvariantCulture)}";

        // The call site is worth saying only when it is somewhere else: a finding remapped back
        // onto the EXEC line it came from would otherwise read "x.sql:69 (in dynamic SQL run at
        // x.sql:69)", which tells the reader nothing they cannot see.
        var withCallSite = dynamicSqlCallSite is { } span && (span.SourcePath != sourcePath || span.Line != line)
            ? $"{location} (in dynamic SQL run at {Relative(span.SourcePath, pathBase)}:{span.Line.ToString(CultureInfo.InvariantCulture)})"
            : location;

        // A finding resting on a dynamic-SQL fold that had to assume a value (a symbolic
        // placeholder) is never High - said plainly here rather than only in the JSON/SARIF, so a
        // reader of the human-facing report cannot mistake it for an ordinary, fully-proven one.
        return confidence == FindingConfidence.High ? withCallSite : $"{withCallSite} [{confidence.ToString().ToUpperInvariant()} CONFIDENCE]";
    }

    /// <summary>
    /// Trims the scan root off a path so the report reads as repo-relative. Only an exact
    /// directory-boundary prefix is trimmed - a base of <c>/src/app</c> must not turn
    /// <c>/src/application/x.sql</c> into <c>lication/x.sql</c>.
    /// </summary>
    private static string Relative(string path, string? pathBase)
    {
        if (string.IsNullOrEmpty(pathBase))
        {
            return path;
        }

        var normalizedBase = pathBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedBase.Length == 0 || !path.StartsWith(normalizedBase, StringComparison.Ordinal))
        {
            return path;
        }

        var remainder = path[normalizedBase.Length..];
        return remainder.Length > 0 && (remainder[0] == Path.DirectorySeparatorChar || remainder[0] == Path.AltDirectorySeparatorChar)
            ? remainder[1..]
            : path;
    }

    private static string DescribeTransformationSite(TransformationSite site, string? pathBase) =>
        site.SourcePath is null
            ? site.Description
            : $"{site.Description} at {Relative(site.SourcePath, pathBase)}:{site.Line.ToString(CultureInfo.InvariantCulture)}";

    private static string DescribeType(SqlType? type) => type?.ToString() ?? UnknownDisplay;

    private static string DescribeOperand(PredicateOperand operand) => operand switch
    {
        PredicateOperand.Column column => $"{column.TableQualifiedName}.{column.ColumnName} ({DescribeType(column.Type)})",
        PredicateOperand.Value { IsLiteral: true, LiteralText: { } text } value => $"{text} ({DescribeType(value.Type)})",
        PredicateOperand.Value value => DescribeType(value.Type),
        _ => UnknownDisplay,
    };

    /// <summary>
    /// Answers the question a reader has as soon as they see a finding on a column they never
    /// wrote a predicate against: which layer put the wrong type in front of the base column.
    /// Depth alone says a view is involved; the provenance origin says which file and line
    /// introduced the cast or expression.
    /// </summary>
    private static string DescribeOrigin(PredicateOperand.Column column, string? pathBase)
    {
        if (column.Depth == 0)
        {
            return "direct table predicate";
        }

        var layers = column.Depth == 1 ? "1 view layer" : $"{column.Depth.ToString(CultureInfo.InvariantCulture)} view layers";
        string via;
        if (column.ImmediateRelationQualifiedName is { } relation)
        {
            var columnSuffix = column.ImmediateColumnName is { } name ? $".{name}" : string.Empty;
            via = $" via {relation}{columnSuffix}";
        }
        else
        {
            via = string.Empty;
        }

        var origin = ProvenanceOrigin(column.Provenance) is { } site
            ? $", introduced at {Relative(site.Path, pathBase)}:{site.Line.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

        return $"{layers}{via}{origin}";
    }

    private static (string Path, int Line)? ProvenanceOrigin(ColumnProvenance provenance) => provenance switch
    {
        ColumnProvenance.Cast { OriginSourcePath: { } path } cast => (path, cast.OriginLine),
        ColumnProvenance.Cast cast => ProvenanceOrigin(cast.Inner),
        ColumnProvenance.Expression { OriginSourcePath: { } path } expression => (path, expression.OriginLine),
        ColumnProvenance.Expression expression => expression.Inputs.Select(ProvenanceOrigin).FirstOrDefault(o => o is not null),
        ColumnProvenance.Union union => union.Branches.Select(ProvenanceOrigin).FirstOrDefault(o => o is not null),
        _ => null,
    };

    /// <summary>
    /// Formats a rate as a percentage without going through "P1", whose invariant-culture form
    /// puts a space before the sign ("100.0 %") - correct for that culture, and wrong-looking in
    /// every report this tool writes.
    /// </summary>
    internal static string Percent(double rate) =>
        $"{(rate * 100).ToString("0.0", CultureInfo.InvariantCulture)}%";

    private static string Count(int value, string noun) =>
        $"{value.ToString(CultureInfo.InvariantCulture)} {noun}{(value == 1 ? string.Empty : "s")}";
}
