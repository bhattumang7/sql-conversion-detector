using SilentScan.Core.Catalog;
using SilentScan.Verify;
using SilentScan.Verify.Oracle;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class TypeMatrixGeneratorTests
{
    private static readonly SqlServerOptions Options = SqlServerOptions.LocalDocker;

    [Fact]
    public async Task GenerateAsync_IntVsReal_ColumnConverts()
    {
        var generator = new TypeMatrixGenerator(Options);
        var numericSubset = new (SqlTypeCategory Category, string Syntax)[]
        {
            (SqlTypeCategory.Int, "INT"),
            (SqlTypeCategory.Real, "REAL"),
        };

        var (entries, serverVersion) = await generator.GenerateAsync(numericSubset, [], [], []);

        Assert.False(string.IsNullOrWhiteSpace(serverVersion));
        var intVsReal = Assert.Single(entries, e => e.ColumnCategory == SqlTypeCategory.Int && e.OtherCategory == SqlTypeCategory.Real);
        Assert.True(intVsReal.ColumnConverts);
        Assert.False(intVsReal.CompileFailed);

        var realVsInt = Assert.Single(entries, e => e.ColumnCategory == SqlTypeCategory.Real && e.OtherCategory == SqlTypeCategory.Int);
        Assert.False(realVsInt.ColumnConverts);
    }

    [Fact]
    public async Task GenerateAsync_TimeVsDate_CompileFailed()
    {
        var generator = new TypeMatrixGenerator(Options);
        var dateTimeSubset = new (SqlTypeCategory Category, string Syntax)[]
        {
            (SqlTypeCategory.Time, "TIME"),
            (SqlTypeCategory.Date, "DATE"),
        };

        var (entries, _) = await generator.GenerateAsync([], dateTimeSubset, [], []);

        var timeVsDate = Assert.Single(entries, e => e.ColumnCategory == SqlTypeCategory.Time && e.OtherCategory == SqlTypeCategory.Date);
        Assert.True(timeVsDate.CompileFailed);
        Assert.False(timeVsDate.ColumnConverts);
    }

    [Fact]
    public async Task GenerateAsync_StringPair_KeyedByCollation_RangeSeekOnlyUnderWindowsCollation()
    {
        var generator = new TypeMatrixGenerator(Options);
        var stringSubset = new (SqlTypeCategory Category, string Syntax)[]
        {
            (SqlTypeCategory.VarChar, "VARCHAR(40)"),
            (SqlTypeCategory.NVarChar, "NVARCHAR(40)"),
        };

        var (entries, _) = await generator.GenerateAsync([], [], stringSubset, TypeMatrixGenerator.Collations);

        var sqlCollationEntry = Assert.Single(entries, e =>
            e.ColumnCategory == SqlTypeCategory.VarChar && e.OtherCategory == SqlTypeCategory.NVarChar && e.CollationName == "SQL_Latin1_General_CP1_CI_AS");
        Assert.True(sqlCollationEntry.ColumnConverts);
        Assert.False(sqlCollationEntry.DynamicRangeSeekAvailable);

        var windowsCollationEntry = Assert.Single(entries, e =>
            e.ColumnCategory == SqlTypeCategory.VarChar && e.OtherCategory == SqlTypeCategory.NVarChar && e.CollationName == "Latin1_General_CI_AS");
        Assert.True(windowsCollationEntry.ColumnConverts);
        Assert.True(windowsCollationEntry.DynamicRangeSeekAvailable);
    }

    [Fact]
    public async Task GenerateAsync_TimestampColumnVsVarbinaryValue_ColumnConverts()
    {
        var generator = new TypeMatrixGenerator(Options);

        var (entries, _) = await generator.GenerateAsync([], [], [], [], binaryFamily: TypeMatrixGenerator.BinaryFamily);

        var timestampVsVarBinary = Assert.Single(entries, e => e.ColumnCategory == SqlTypeCategory.Timestamp && e.OtherCategory == SqlTypeCategory.VarBinary);
        Assert.False(timestampVsVarBinary.CompileFailed);
        Assert.False(timestampVsVarBinary.ColumnConverts);

        var varBinaryVsTimestamp = Assert.Single(entries, e => e.ColumnCategory == SqlTypeCategory.VarBinary && e.OtherCategory == SqlTypeCategory.Timestamp);
        Assert.True(varBinaryVsTimestamp.ColumnConverts);
    }

    [Fact]
    public async Task GenerateAsync_BinaryColumnVsVarbinaryValue_SameComparisonTypeNeitherConverts()
    {
        var generator = new TypeMatrixGenerator(Options);

        var (entries, _) = await generator.GenerateAsync([], [], [], [], binaryFamily: TypeMatrixGenerator.BinaryFamily);

        var binaryVsVarBinary = Assert.Single(entries, e => e.ColumnCategory == SqlTypeCategory.Binary && e.OtherCategory == SqlTypeCategory.VarBinary);
        Assert.False(binaryVsVarBinary.CompileFailed);
        Assert.False(binaryVsVarBinary.ColumnConverts);

        var varBinaryVsBinary = Assert.Single(entries, e => e.ColumnCategory == SqlTypeCategory.VarBinary && e.OtherCategory == SqlTypeCategory.Binary);
        Assert.False(varBinaryVsBinary.ColumnConverts);
    }
}
