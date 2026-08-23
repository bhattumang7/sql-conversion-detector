using System.CommandLine;

namespace SilentScan.Bench.Commands;

public static class BenchRootCommand
{
    public static RootCommand Create() =>
        new("silentscan-bench — measures the logical-read/CPU cost of index-killing implicit conversions at scale.")
        {
            RunCommand.Create(),
        };
}
