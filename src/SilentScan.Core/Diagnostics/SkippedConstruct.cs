namespace SilentScan.Core.Diagnostics;

public enum AnalysisPass
{
    Catalog,
    Lineage,
    Predicates,
}

public sealed record SkippedConstruct(AnalysisPass Pass, string SourcePath, int Line, int Column, string ConstructKind, string Reason);

public sealed class SkipLedger
{
    private readonly List<SkippedConstruct> _entries = [];

    public IReadOnlyList<SkippedConstruct> Entries => _entries;

    public void Record(AnalysisPass pass, string sourcePath, int line, int column, string constructKind, string reason) =>
        _entries.Add(new SkippedConstruct(pass, sourcePath, line, column, constructKind, reason));

public void AddRange(IEnumerable<SkippedConstruct> entries) => _entries.AddRange(entries);
}
