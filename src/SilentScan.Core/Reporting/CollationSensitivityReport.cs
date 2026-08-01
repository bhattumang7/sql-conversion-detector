using SilentScan.Core.Parsing;

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
/// actually uses." This re-runs the scan under each collation-family assumption and reports
/// both, so a study can say "N findings if SQL_*, M findings if Windows" instead of a bare
/// UNKNOWN that looks identical to "we checked and there's nothing here."
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
    /// nothing; runs <see cref="ScanReportBuilder.BuildFromParseResults"/> twice over the same
    /// already-parsed files, once per assumption.
    /// </summary>
    public static CollationSensitivityReport Analyze(IReadOnlyList<SqlParseResult> parseResults) =>
        Analyze(parseResults, DefaultSqlFamilyCollation, DefaultWindowsFamilyCollation);

    public static CollationSensitivityReport Analyze(IReadOnlyList<SqlParseResult> parseResults, string sqlFamilyCollation, string windowsFamilyCollation)
    {
        var underSqlFamily = ScanReportBuilder.BuildFromParseResults(parseResults, sqlFamilyCollation).TypedPredicateSummary;
        var underWindowsFamily = ScanReportBuilder.BuildFromParseResults(parseResults, windowsFamilyCollation).TypedPredicateSummary;

        return new CollationSensitivityReport(sqlFamilyCollation, windowsFamilyCollation, underSqlFamily, underWindowsFamily);
    }
}
