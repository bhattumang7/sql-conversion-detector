namespace SilentScan.Core.Reporting;

/// <summary>
/// Result of Pass 0: did every .sql file in a corpus parse cleanly under ScriptDOM.
/// This is the corpus dialect-sniffing signal from CLAUDE.md ("ScriptDOM parse success
/// rate >= 90% of files"), computed here rather than assumed.
/// </summary>
public sealed record ParseHealthReport(IReadOnlyList<FileParseHealth> Files)
{
    public int TotalFiles => Files.Count;

    public int FilesWithErrors => Files.Count(f => f.Errors.Count > 0);

    public double ParseSuccessRate => TotalFiles == 0 ? 1.0 : (double)(TotalFiles - FilesWithErrors) / TotalFiles;
}

/// <summary>
/// <paramref name="BatchCount"/> is the number of GO-separated batches that survived to parse
/// cleanly (docs/audit-remediation-plan.md Phase 4.4) - a file can have both a positive
/// BatchCount and a non-empty Errors list when only some of its batches failed.
/// </summary>
public sealed record FileParseHealth(string Path, IReadOnlyList<ParseErrorInfo> Errors, int BatchCount);

public sealed record ParseErrorInfo(int Line, int Column, int Number, string Message);
