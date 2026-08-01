using System.CommandLine;

namespace SilentScan.Verify.Commands;

/// <summary>
/// Root command for the verification tool (CLAUDE.md SilentScan.Verify): deploys corpus DDL
/// to the Docker SQL Server oracle and confirms SCAN_FORCED/RANGE_SEEK findings via
/// CONVERT_IMPLICIT in plan XML.
/// </summary>
public static class VerifyRootCommand
{
    public static RootCommand Create()
    {
        var root = new RootCommand("silentscan-verify — deploys corpus DDL to a disposable SQL Server and confirms findings against sys.columns and plan XML.");
        root.Subcommands.Add(VerifyCorpusCommand.Create());
        root.Subcommands.Add(GenerateTypeMatrixCommand.Create());
        return root;
    }
}
