namespace SilentScan.Core.Reporting;

/// <summary>
/// Result of Pass 0: did every .sql file in a corpus parse cleanly under ScriptDOM.
/// This is the corpus dialect-sniffing signal from CLAUDE.md ("ScriptDOM parse success
/// rate >= 90% of files"), computed here rather than assumed.
/// </summary>
public sealed record ParseHealthReport(IReadOnlyList<FileParseHealth> Files)
{
    /// <summary>CLAUDE.md's own corpus-admission threshold: "ScriptDOM parse success >= 90% of files". Exposed here so every consumer of this rate (currently `scan-corpus`/`verify-corpus`) checks against the SAME number CLAUDE.md documents, rather than each hand-rolling its own.</summary>
    public const double MinimumAcceptableParseSuccessRate = 0.90;

    public int TotalFiles => Files.Count;

    public int FilesWithErrors => Files.Count(f => f.Errors.Count > 0);

    public double ParseSuccessRate => TotalFiles == 0 ? 1.0 : (double)(TotalFiles - FilesWithErrors) / TotalFiles;

    /// <summary>
    /// CLAUDE.md's corpus dialect-sniffing criterion, finally actually consulted (an audit
    /// finding: this rate was computed and displayed, but nothing ever gated on it - a repo
    /// whose SQL was mostly a different dialect entirely would scan exactly as "successfully"
    /// as a clean one). True for an empty file set - nothing to fail sniffing on.
    /// </summary>
    public bool PassesDialectSniffing => ParseSuccessRate >= MinimumAcceptableParseSuccessRate;
}

/// <summary>
/// <paramref name="BatchCount"/> is the number of GO-separated batches that survived to parse
/// cleanly (docs/audit-remediation-plan.md Phase 4.4) - a file can have both a positive
/// BatchCount and a non-empty Errors list when only some of its batches failed.
/// </summary>
public sealed record FileParseHealth(string Path, IReadOnlyList<ParseErrorInfo> Errors, int BatchCount);

public sealed record ParseErrorInfo(int Line, int Column, int Number, string Message);
