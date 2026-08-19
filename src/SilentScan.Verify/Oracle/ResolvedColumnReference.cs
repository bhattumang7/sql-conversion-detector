namespace SilentScan.Verify.Oracle;

/// <summary>
/// One <c>&lt;ColumnReference&gt;</c> element anywhere in a plan XML - the engine's own real
/// algebrizer answer for what a column reference binds to, independent of any conversion. A
/// non-null <see cref="Table"/> means a genuine table column (a local variable/parameter also
/// serializes as <c>&lt;ColumnReference&gt;</c>, but with no <c>Table</c> attribute - see <see
/// cref="ConvertImplicitFinding"/>'s own doc comment for the same distinction).
/// </summary>
public sealed record ResolvedColumnReference(
    string? Database,
    string? Schema,
    string? Table,
    string? Column);
