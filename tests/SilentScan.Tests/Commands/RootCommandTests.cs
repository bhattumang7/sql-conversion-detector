using SilentScan.Bench.Commands;
using SilentScan.Cli.Commands;
using SilentScan.Verify.Commands;

namespace SilentScan.Tests.Commands;

public sealed class RootCommandTests
{
    [Fact]
    public void VerifyRootCommand_Create_HasDescription()
    {
        var command = VerifyRootCommand.Create();

        Assert.Contains("silentscan-verify", command.Description);
    }

    [Fact]
    public void BenchRootCommand_Create_HasDescription()
    {
        var command = BenchRootCommand.Create();

        Assert.Contains("silentscan-bench", command.Description);
    }

    [Fact]
    public void RulesDocCommand_Create_HasNameAndOutputOptions()
    {
        var command = RulesDocCommand.Create();

        Assert.Equal("rules-doc", command.Name);
        Assert.Contains(command.Options, o => o.Name == "--output");
        Assert.Contains(command.Options, o => o.Name == "--rules-dir");
    }

    [Fact]
    public void ScanDbCommand_Create_HasNameAndExpectedOptions()
    {
        var command = ScanDbCommand.Create();

        Assert.Equal("scan-db", command.Name);
        Assert.Contains(command.Arguments, a => a.Name == "connection-string");
        Assert.Contains(command.Options, o => o.Name == "--format");
        Assert.Contains(command.Options, o => o.Name == "--confidence");
        Assert.Contains(command.Options, o => o.Name == "--verbosity");
        Assert.Contains(command.Options, o => o.Name == "--output");
        Assert.Contains(command.Options, o => o.Name == "--plan-cache-evidence");
        Assert.Contains(command.Options, o => o.Name == "--fetch-sql-from-tables");
    }

    [Fact]
    public void ScanCorpusLiveCommand_Create_HasNameAndExpectedOptions()
    {
        var command = ScanCorpusLiveCommand.Create();

        Assert.Equal("scan-corpus-live", command.Name);
        Assert.Contains(command.Options, o => o.Name == "--manifest");
        Assert.Contains(command.Options, o => o.Name == "--clones-root");
        Assert.Contains(command.Options, o => o.Name == "--format");
        Assert.Contains(command.Options, o => o.Name == "--confidence");
        Assert.Contains(command.Options, o => o.Name == "--verbosity");
        Assert.Contains(command.Options, o => o.Name == "--output");
    }
}
