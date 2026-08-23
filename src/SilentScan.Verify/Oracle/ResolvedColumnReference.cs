namespace SilentScan.Verify.Oracle;

public sealed record ResolvedColumnReference(
    string? Database,
    string? Schema,
    string? Table,
    string? Column);
