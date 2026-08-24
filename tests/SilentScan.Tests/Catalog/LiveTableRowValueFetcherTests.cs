using Microsoft.Data.SqlClient;
using SilentScan.Live.Catalog;

namespace SilentScan.Tests.Catalog;

public sealed class LiveTableRowValueFetcherTests
{
    private const string PlaceholderConnectionString =
        "Server=localhost;Database=UnitTestPlaceholder;User Id=test;Password=test;TrustServerCertificate=true;Connect Timeout=1;";

    [Theory]
    [InlineData("")]
    [InlineData("WidgetWithNoSchemaPrefix")]
    public void TryFetchDistinctValues_TableNameWithoutASchemaQualifier_ReturnsNullWithoutTouchingTheConnection(
        string tableQualifiedName)
    {
        using var connection = new SqlConnection(PlaceholderConnectionString);
        var fetcher = new LiveTableRowValueFetcher(connection);

        var result = fetcher.TryFetchDistinctValues(tableQualifiedName, "Code", [], maxRows: 10);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryFetchDistinctValues_NonPositiveMaxRows_ReturnsNullWithoutTouchingTheConnection(int maxRows)
    {
        using var connection = new SqlConnection(PlaceholderConnectionString);
        var fetcher = new LiveTableRowValueFetcher(connection);

        var result = fetcher.TryFetchDistinctValues("dbo.Widget", "Code", [], maxRows);

        Assert.Null(result);
    }
}
