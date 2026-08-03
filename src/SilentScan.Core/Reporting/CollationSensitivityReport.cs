using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting;

/// <summary>
/// For a repo with no <c>declaredCollation</c> pinned in the corpus manifest, the flagship
/// varchar-vs-nvarchar rule is structurally unreachable: <see
/// cref="Rules.VerdictClassifier"/> only reports a verdict for a string-family column whose
/// collation is either explicit on the column itself or supplied by this hint, and
/// SQL_* vs Windows collations disagree on ScanForced vs RangeSeek for exactly that pair
/// (CLAUDE.md: "collation unknown and unpinned by the manifest -&gt; UNKNOWN. Never guess
/// silently.") - so an unpinned repo silently reports zero findings for its single most
/// important bug class rather than an honest "depends which collation family this database
/// actually uses." This scans the repo ONCE (catalog/lineage/extraction all run a single time,
/// with no collation hint at all) and re-classifies only the findings whose Unknown verdict was
/// caused by that missing collation, once per assumed collation family - so a study can say "N
/// findings if SQL_*, M findings if Windows" instead of a bare UNKNOWN that looks identical to
/// "we checked and there's nothing here."
/// </summary>
public sealed record CollationSensitivityReport(
    string SqlFamilyCollation,
    string WindowsFamilyCollation,
    TypedPredicateSummary UnderSqlFamilyAssumption,
    TypedPredicateSummary UnderWindowsFamilyAssumption)
{
    /// <summary>The representative SQL_* collation used for the "what if this repo's default collation is legacy" run.</summary>
    public const string DefaultSqlFamilyCollation = "SQL_Latin1_General_CP1_CI_AS";

    /// <summary>The representative Windows collation used for the "what if this repo's default collation is modern" run.</summary>
    public const string DefaultWindowsFamilyCollation = "Latin1_General_CI_AS";

    /// <summary>
    /// Only meaningful to run for a repo whose manifest entry has no declaredCollation - a repo
    /// that already has one doesn't need a sensitivity analysis, it has an answer. Re-parses
    /// nothing, and now (unlike the version this replaced, which ran the whole
    /// catalog/lineage/extraction pipeline twice) re-catalogs/re-resolves nothing either - one
    /// real scan, then a cheap per-finding re-classification for exactly the subset a collation
    /// assumption could actually change.
    /// </summary>
    public static CollationSensitivityReport Analyze(IReadOnlyList<SqlParseResult> parseResults) =>
        Analyze(parseResults, DefaultSqlFamilyCollation, DefaultWindowsFamilyCollation);

    public static CollationSensitivityReport Analyze(IReadOnlyList<SqlParseResult> parseResults, string sqlFamilyCollation, string windowsFamilyCollation)
    {
        // No manifest collation hint here on purpose: this method's whole premise is that none
        // is pinned. Any column with its OWN explicit/DDL-resolved collation is untouched by
        // either assumption below - only a genuinely unresolved one is reclassified.
        var report = ScanReportBuilder.BuildFromParseResults(parseResults, manifestDeclaredCollation: null);

        var underSqlFamily = ReclassifyUnderAssumedCollation(report.TypedFindings, sqlFamilyCollation, report.TypedPredicateSummary);
        var underWindowsFamily = ReclassifyUnderAssumedCollation(report.TypedFindings, windowsFamilyCollation, report.TypedPredicateSummary);

        return new CollationSensitivityReport(sqlFamilyCollation, windowsFamilyCollation, underSqlFamily, underWindowsFamily);
    }

    /// <summary>
    /// <paramref name="nonSeekPreservedFindings"/> is <see cref="ScanReport.TypedFindings"/> -
    /// every classified comparison EXCEPT SeekPreserved ones, which <see cref="ScanReportBuilder"/>
    /// already drops before a caller ever sees them. A SeekPreserved finding's collation was
    /// already resolved (that's WHY it seeks cleanly), so it can never be among the reclassified
    /// set - <paramref name="baseline"/>'s own SeekPreservedCount/TotalClassified/
    /// DistinctTotalClassified are reused as-is rather than recomputed, since nothing in this
    /// method's reclassification can change them (same population, same dedup keys - see
    /// <see cref="TypedPredicateFindingIdentity"/>'s own key shape, which is verdict-independent).
    /// </summary>
    private static TypedPredicateSummary ReclassifyUnderAssumedCollation(
        IReadOnlyList<TypedPredicateFinding> nonSeekPreservedFindings, string assumedCollationName, TypedPredicateSummary baseline)
    {
        var reclassified = nonSeekPreservedFindings.Select(f => Reclassify(f, assumedCollationName)).ToList();

        var unknown = reclassified.Count(f => f.Verdict == Verdict.Unknown);
        var rangeSeek = reclassified.Count(f => f.Verdict == Verdict.RangeSeek);
        var scanForced = reclassified.Count(f => f.Verdict == Verdict.ScanForced);
        var operandClash = reclassified.Count(f => f.Verdict == Verdict.OperandClash);

        var distinctRangeSeek = TypedFindingDeduplicator.Dedupe([.. reclassified.Where(f => f.Verdict == Verdict.RangeSeek)]).Count;
        var distinctScanForced = TypedFindingDeduplicator.Dedupe([.. reclassified.Where(f => f.Verdict == Verdict.ScanForced)]).Count;

        return new TypedPredicateSummary(
            baseline.TotalClassified, baseline.SeekPreservedCount, rangeSeek, scanForced, unknown, operandClash,
            distinctRangeSeek, distinctScanForced, baseline.DistinctTotalClassified);
    }

    /// <summary>
    /// Only an Unknown finding whose column type is string-family with a genuinely unresolved
    /// collation is a candidate - every other finding (including an Unknown for an unrelated
    /// reason, e.g. an out-of-model category) is returned unchanged. The OTHER operand's own
    /// collation, if it independently resolved to something real (an explicit COLLATE, a second
    /// real column), is left exactly as it was - the assumed collation only fills in what this
    /// scan could not otherwise determine, on the column side, matching
    /// <see cref="VerdictClassifier"/>'s own "only column-side conversion loses the seek" model.
    /// </summary>
    private static TypedPredicateFinding Reclassify(TypedPredicateFinding finding, string assumedCollationName)
    {
        if (finding.Verdict != Verdict.Unknown || finding.Column.Type is not { IsStringFamily: true, Collation: null } columnType)
        {
            return finding;
        }

        var otherIsLiteral = finding.OtherOperand is PredicateOperand.Value { IsLiteral: true };
        var otherType = finding.OtherOperand is PredicateOperand.Value value ? value.Type : ((PredicateOperand.Column)finding.OtherOperand).Type;
        var assumedColumnType = columnType with { Collation = new Collation(assumedCollationName) };

        var (verdict, unknownReason) = VerdictClassifier.ClassifyWithReason(assumedColumnType, otherType, otherIsLiteral, finding.Operator);
        return finding with { Verdict = verdict, UnknownReason = unknownReason };
    }
}
