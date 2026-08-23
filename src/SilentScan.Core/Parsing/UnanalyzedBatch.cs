namespace SilentScan.Core.Parsing;

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
