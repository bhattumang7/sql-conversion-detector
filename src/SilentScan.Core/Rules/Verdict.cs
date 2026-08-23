namespace SilentScan.Core.Rules;

public enum Verdict
{
    SeekPreserved,
    RangeSeek,
    ScanForced,
    Unknown,

    OperandClash,
}
