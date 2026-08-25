using System.CommandLine;

namespace SilentScan.Verify.Commands;

public static class VerifyRootCommand
{
    public static RootCommand Create()
    {
        var root = new RootCommand("silentscan-verify — deploys DDL to a disposable SQL Server and confirms findings against sys.columns and plan XML.");
        root.Subcommands.Add(GenerateTypeMatrixCommand.Create());
        return root;
    }
}
