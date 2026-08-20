using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Parsing;

public sealed class SqlTypeReferenceResolverTests
{
    private static DataTypeReference ParseColumnDataType(string dataTypeSql)
    {
        var parser = new TSql160Parser(true);
        using var reader = new StringReader($"CREATE TABLE dbo.T (Col {dataTypeSql});");
        var fragment = parser.Parse(reader, out var errors);
        Assert.Empty(errors);

        var script = (TSqlScript)fragment;
        var create = (CreateTableStatement)script.Batches[0].Statements[0];
        return create.Definition.ColumnDefinitions[0].DataType;
    }

    [Theory]
    [InlineData("sysname")]
    [InlineData("SYSNAME")]
    [InlineData("SysName")]
    public void Resolve_Sysname_ResolvesToNVarChar128(string spelling)
    {
        // Oracle-verified indirectly: sysname always parses as UserDataTypeReference, never a
        // built-in SqlDataTypeOption - confirmed against the real parser (docs/audit-
        // remediation-plan.md Phase 6.2, audit finding: "sysname (equivalent to nvarchar(128))
        // - pervasive in the admin-script repos this study targets").
        var type = SqlTypeReferenceResolver.Resolve(ParseColumnDataType(spelling), columnCollation: null);

        Assert.Equal(SqlTypeCategory.NVarChar, type!.Category);
        Assert.Equal(128, type.Length);
    }

    [Fact]
    public void Resolve_SysnameWithExplicitColumnCollation_CollationWins()
    {
        var type = SqlTypeReferenceResolver.Resolve(
            ParseColumnDataType("sysname"), new Identifier { Value = "Latin1_General_CI_AS" });

        Assert.Equal(SqlTypeCategory.NVarChar, type!.Category);
        Assert.Equal("Latin1_General_CI_AS", type.Collation!.Name);
    }

    [Fact]
    public void Resolve_UnknownUserDataType_NoAliasesProvided_ReturnsNull()
    {
        var type = SqlTypeReferenceResolver.Resolve(ParseColumnDataType("dbo.MyIntAlias"), columnCollation: null);

        Assert.Null(type);
    }

    [Fact]
    public void Resolve_UserDataType_MatchingCatalogedAlias_ResolvesToUnderlyingType()
    {
        // docs/audit-remediation-plan.md Phase 6.2: "catalog CREATE TYPE ... FROM aliases and
        // resolve through them."
        var aliases = new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.MyIntAlias"] = new SqlType(SqlTypeCategory.Int),
        };

        var type = SqlTypeReferenceResolver.Resolve(ParseColumnDataType("dbo.MyIntAlias"), columnCollation: null, aliases);

        Assert.Equal(SqlTypeCategory.Int, type!.Category);
    }

    [Fact]
    public void Resolve_UnqualifiedUserDataType_MatchesDefaultSchemaQualifiedAlias()
    {
        // An alias declared as dbo.MyStr and referenced bare as MyStr must still match - the
        // same default-schema qualification every other name lookup in this codebase applies.
        var aliases = new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.MyStr"] = new SqlType(SqlTypeCategory.VarChar, Length: 50),
        };

        var type = SqlTypeReferenceResolver.Resolve(ParseColumnDataType("MyStr"), columnCollation: null, aliases);

        Assert.Equal(SqlTypeCategory.VarChar, type!.Category);
        Assert.Equal(50, type.Length);
    }

    [Fact]
    public void Resolve_UserDataType_NotInAliasMap_ReturnsNullRatherThanGuessing()
    {
        var aliases = new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.SomeOtherAlias"] = new SqlType(SqlTypeCategory.Int),
        };

        var type = SqlTypeReferenceResolver.Resolve(ParseColumnDataType("dbo.MyIntAlias"), columnCollation: null, aliases);

        Assert.Null(type);
    }

    [Fact]
    public void Resolve_BuiltinType_StillWorksWithAliasesProvided()
    {
        var aliases = new Dictionary<string, SqlType>(StringComparer.OrdinalIgnoreCase);

        var type = SqlTypeReferenceResolver.Resolve(ParseColumnDataType("VARCHAR(40)"), columnCollation: null, aliases);

        Assert.Equal(SqlTypeCategory.VarChar, type!.Category);
        Assert.Equal(40, type.Length);
    }
}
