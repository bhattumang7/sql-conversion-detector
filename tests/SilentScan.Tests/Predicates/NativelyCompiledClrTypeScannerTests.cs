using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class NativelyCompiledClrTypeScannerTests
{
    private static IReadOnlyList<NativelyCompiledClrTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return NativelyCompiledClrTypeScanner.Scan(result, catalog);
    }

    [Fact]
    public void ClrTypeParameter_OnNativelyCompiledProcedure_Fires()
    {
        var findings = Scan(
            """
            CREATE TYPE dbo.GeoPoint EXTERNAL NAME GeoAssembly.[GeoPoint];
            GO
            CREATE PROCEDURE dbo.SavePoint
                @point dbo.GeoPoint
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @dummy INT = 1;
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.SavePoint", finding.ModuleQualifiedName);
        Assert.Equal(NativelyCompiledClrTypeKind.Parameter, finding.Kind);
        Assert.Equal("@point", finding.MemberName);
        Assert.Equal("dbo.GeoPoint", finding.TypeQualifiedName);
    }

    [Fact]
    public void ClrTypeLocalVariable_OnNativelyCompiledProcedure_Fires()
    {
        var findings = Scan(
            """
            CREATE TYPE dbo.GeoPoint EXTERNAL NAME GeoAssembly.[GeoPoint];
            GO
            CREATE PROCEDURE dbo.UsePoint
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @point dbo.GeoPoint;
            END;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("dbo.UsePoint", finding.ModuleQualifiedName);
        Assert.Equal(NativelyCompiledClrTypeKind.LocalVariable, finding.Kind);
        Assert.Equal("@point", finding.MemberName);
    }

    [Fact]
    public void ClrTypeParameter_OnOrdinaryInterpretedProcedure_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TYPE dbo.GeoPoint EXTERNAL NAME GeoAssembly.[GeoPoint];
            GO
            CREATE PROCEDURE dbo.SavePoint
                @point dbo.GeoPoint
            AS
            BEGIN
                DECLARE @dummy INT = 1;
            END;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void OrdinaryAliasTypeParameter_OnNativelyCompiledProcedure_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TYPE dbo.Money2 FROM DECIMAL(19,4) NOT NULL;
            GO
            CREATE PROCEDURE dbo.SaveAmount
                @amount dbo.Money2
            WITH NATIVE_COMPILATION, SCHEMABINDING
            AS
            BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english')
                DECLARE @dummy INT = 1;
            END;
            """);

        Assert.Empty(findings);
    }
}
