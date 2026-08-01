namespace SilentScan.Core.Diagnostics;

/// <summary>Which of the four passes (CLAUDE.md) produced a <see cref="SkippedConstruct"/>.</summary>
public enum AnalysisPass
{
    Catalog,
    Lineage,
    Predicates,
}

/// <summary>
/// A construct one of the static-analysis passes saw but could not resolve - an unrecognized
/// statement kind, an unqualifiable column reference, an ambiguous alias, an unsupported
/// predicate node. Mirrors the dynamic-SQL bucket's honesty policy (CLAUDE.md dynamic SQL
/// policy: "never silently counted as clean"): nothing that reaches a pass is ever silently
/// dropped from the study's coverage accounting, even when it produces no finding.
/// </summary>
public sealed record SkippedConstruct(AnalysisPass Pass, string SourcePath, int Line, int Column, string ConstructKind, string Reason);

/// <summary>
/// Mutable accumulator threaded through a single pass's resolution. Not thread-safe by design -
/// each pass runs single-threaded per scan, matching every other accumulator in this codebase
/// (e.g. <see cref="Catalog.DatabaseCatalog"/>'s own mutable dictionary).
/// </summary>
public sealed class SkipLedger
{
    private readonly List<SkippedConstruct> _entries = [];

    public IReadOnlyList<SkippedConstruct> Entries => _entries;

    public void Record(AnalysisPass pass, string sourcePath, int line, int column, string constructKind, string reason) =>
        _entries.Add(new SkippedConstruct(pass, sourcePath, line, column, constructKind, reason));
}
