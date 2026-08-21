namespace SilentScan.Core.Parsing;

/// <summary>
/// A GO-separated batch that ScriptDOM silently dropped from <c>TSqlScript.Batches</c> because
/// it contained a syntax error - not a finding, and not verdict-bearing: purely a coverage
/// signal that an object may have received zero analysis. <see cref="ObjectName"/> is a
/// best-effort read of the batch's own raw text (see <see cref="DroppedBatchObjectSniffer"/>)
/// and is <see langword="null"/> whenever that read isn't confident - never a guessed name.
/// </summary>
public sealed record UnanalyzedBatch(string SourcePath, int StartLine, UnanalyzedObjectKind Kind, string? ObjectName);

public enum UnanalyzedObjectKind
{
    Unidentified,
    Procedure,
    View,
    Function,
    Trigger,
    Table,
}
