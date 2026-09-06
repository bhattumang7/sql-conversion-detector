using Microsoft.Data.SqlClient;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Integration;

[Trait("Category", "Oracle")]
public sealed class XmlSchemaCollectionDisallowedTypeOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(XmlSchemaCollectionDisallowedTypeOracleTests);

    protected override string Ddl => "SELECT 1;";

    private static IReadOnlyList<XmlSchemaCollectionDisallowedTypeFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        return XmlSchemaCollectionDisallowedTypeScanner.Scan(result, catalog);
    }

    private async Task<SqlException> ExecuteExpectingFailureAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task NotationAsAttributeType_FailsToRegisterWithMsg9337_AndScannerFlagsIt()
    {
        const string Sql = """
            CREATE XML SCHEMA COLLECTION dbo.OrderSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:complexType name="Order">
                <xs:attribute name="Format" type="xs:NOTATION"/>
              </xs:complexType>
            </xs:schema>';
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(9337, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal(XmlSchemaCollectionDisallowedTypeKind.NotationType, finding.Kind);
    }

    [Fact]
    public async Task IdRefAsElementType_FailsToRegisterWithMsg6995_AndScannerFlagsIt()
    {
        const string Sql = """
            CREATE XML SCHEMA COLLECTION dbo.CustomerSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="CustomerRef" type="xs:IDREF"/>
            </xs:schema>';
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(6995, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal(XmlSchemaCollectionDisallowedTypeKind.IdOrIdRefType, finding.Kind);
    }

    [Fact]
    public async Task IdRefsAsElementType_FailsToRegisterWithMsg6995_AndScannerFlagsIt()
    {
        const string Sql = """
            CREATE XML SCHEMA COLLECTION dbo.RefsSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="RefsRoot" type="xs:IDREFS"/>
            </xs:schema>';
            """;

        var exception = await ExecuteExpectingFailureAsync(Sql);
        Assert.Equal(6995, exception.Number);

        var finding = Assert.Single(Scan(Sql));
        Assert.Equal(XmlSchemaCollectionDisallowedTypeKind.IdOrIdRefType, finding.Kind);
    }

    [Fact]
    public async Task IdRefsAsAttributeType_RegistersCleanly_AndScannerDoesNotFlagIt()
    {
        const string Sql = """
            CREATE XML SCHEMA COLLECTION dbo.RefsAttrSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:complexType name="MyType">
                <xs:attribute name="a" type="xs:IDREFS"/>
              </xs:complexType>
              <xs:element name="Root" type="MyType"/>
            </xs:schema>';
            """;

        await ExecuteAsync(Sql);

        Assert.Empty(Scan(Sql));
    }

    [Fact]
    public async Task IdAsAttributeType_RegistersCleanly_AndScannerDoesNotFlagIt()
    {
        const string Sql = """
            CREATE XML SCHEMA COLLECTION dbo.AttrSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:complexType name="MyType">
                <xs:attribute name="a" type="xs:ID"/>
              </xs:complexType>
              <xs:element name="Root" type="MyType"/>
            </xs:schema>';
            """;

        await ExecuteAsync(Sql);

        Assert.Empty(Scan(Sql));
    }

    [Fact]
    public async Task OrdinaryStringElementType_RegistersCleanly_AndScannerDoesNotFlagIt()
    {
        const string Sql = """
            CREATE XML SCHEMA COLLECTION dbo.StringSchema AS N'
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="Name" type="xs:string"/>
            </xs:schema>';
            """;

        await ExecuteAsync(Sql);

        Assert.Empty(Scan(Sql));
    }
}
