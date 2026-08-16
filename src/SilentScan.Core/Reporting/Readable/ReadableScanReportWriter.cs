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
