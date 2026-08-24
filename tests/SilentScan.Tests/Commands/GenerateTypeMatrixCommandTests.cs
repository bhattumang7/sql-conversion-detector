using SilentScan.Verify.Commands;

namespace SilentScan.Tests.Commands;

public sealed class GenerateTypeMatrixCommandTests
{
    [Fact]
    public void Create_HasNameAndOutputOptionWithCheckedInDefault()
    {
        var command = GenerateTypeMatrixCommand.Create();

        Assert.Equal("generate-type-matrix", command.Name);
        var outputOption = Assert.Single(command.Options, o => o.Name == "--output");
        var defaultValue = outputOption.GetDefaultValue();
        Assert.Equal("src/SilentScan.Core/Rules/TypePairMatrix.json", defaultValue);
    }
}
