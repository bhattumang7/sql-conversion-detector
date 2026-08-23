using SilentScan.Core.Parsing;

namespace SilentScan.Core.Reporting;

public sealed record ParseHealthReport(IReadOnlyList<FileParseHealth> Files)
{
public const double MinimumAcceptableParseSuccessRate = 0.90;

    public int TotalFiles => Files.Count;

    public int FilesWithErrors => Files.Count(f => f.Errors.Count > 0);

    public double ParseSuccessRate => TotalFiles == 0 ? 1.0 : (double)(TotalFiles - FilesWithErrors) / TotalFiles;

public bool PassesDialectSniffing => ParseSuccessRate >= MinimumAcceptableParseSuccessRate;
}

public sealed record FileParseHealth(
    string Path,
    IReadOnlyList<ParseErrorInfo> Errors,
    int BatchCount,
    IReadOnlyList<UnanalyzedBatch> UnanalyzedBatches)
{
    public FileParseHealth(string Path, IReadOnlyList<ParseErrorInfo> Errors, int BatchCount)
        : this(Path, Errors, BatchCount, [])
    {
    }
}

public sealed record ParseErrorInfo(int Line, int Column, int Number, string Message);
