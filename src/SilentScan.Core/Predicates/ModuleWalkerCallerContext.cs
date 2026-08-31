using SilentScan.Core.Diagnostics;

namespace SilentScan.Core.Predicates;

public sealed record ModuleWalkerCallerContext(
    SkipLedger? Ledger,
    string? CurrentProcScope,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? CallerScopeByCalleeScope)
{
    public static readonly ModuleWalkerCallerContext None = new(null, null, null);
}
