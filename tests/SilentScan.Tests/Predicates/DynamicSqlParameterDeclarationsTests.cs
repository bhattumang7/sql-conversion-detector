using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// Tier B of CLAUDE.md's dynamic SQL policy: sp_executesql's second argument declares its
/// parameters' exact types, parsed here by reusing ScriptDOM's own stored-procedure parameter
/// grammar rather than hand-rolling one.
/// </summary>
public sealed class DynamicSqlParameterDeclarationsTests
{
    [Fact]
    public void TryParse_SingleParameter_ResolvesDeclaredType()
    {
        var declared = DynamicSqlParameterDeclarations.TryParse("@DisplayName nvarchar(40)");

        Assert.NotNull(declared);
        Assert.True(declared.TryGetValue("@DisplayName", out var type));
        Assert.Equal(SqlTypeCategory.NVarChar, type!.Category);
    }

    [Fact]
    public void TryParse_MultipleParametersWithOutput_ResolvesAll()
    {
        var declared = DynamicSqlParameterDeclarations.TryParse("@Id int, @Name varchar(50) OUTPUT");

        Assert.NotNull(declared);
        Assert.Equal(2, declared.Count);
        Assert.Equal(SqlTypeCategory.Int, declared["@Id"]!.Category);
        Assert.Equal(SqlTypeCategory.VarChar, declared["@Name"]!.Category);
    }

    [Fact]
    public void TryParse_CaseInsensitiveParameterNameLookup()
    {
        var declared = DynamicSqlParameterDeclarations.TryParse("@DisplayName nvarchar(40)");

        Assert.NotNull(declared);
        Assert.True(declared.ContainsKey("@displayname"));
    }

    [Fact]
    public void TryParse_MalformedDeclaration_ReturnsNull()
    {
        var declared = DynamicSqlParameterDeclarations.TryParse("this is not a parameter list $$$");

        Assert.Null(declared);
    }

    [Fact]
    public void TryParse_EmptyDeclaration_ReturnsEmptyDictionary()
    {
        var declared = DynamicSqlParameterDeclarations.TryParse(string.Empty);

        Assert.NotNull(declared);
        Assert.Empty(declared);
    }
}
