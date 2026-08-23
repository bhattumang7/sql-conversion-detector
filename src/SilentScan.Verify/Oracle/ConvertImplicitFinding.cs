namespace SilentScan.Verify.Oracle;

public sealed record ConvertImplicitFinding(
    string? Database,
    string? Schema,
    string? Table,
    string? Column,
    string ConvertedToDataType,
    bool RangeSeekBound);
