using Microsoft.Data.SqlClient;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 1 "Join predicate incomplete vs. the backing foreign key" -
/// a runtime row-count behavior, not a query-plan one (like <see
/// cref="TemporalBoundaryPrecisionOracleTests"/>, not the compile-only SHOWPLAN_XML oracle the
/// implicit-conversion streams use): proves the actual row-multiplication mechanism directly,
/// with real seeded data - two revisions of the same order, and one order line tied to only one
/// of them. Version-insensitive: row multiplication from a partial equality join is pure
/// relational algebra, unaffected by CE version, interleaved execution, or UDF inlining, so a
/// single oracle run stands for every engine version this tool targets.
/// </summary>
public sealed class PartialCompositeForeignKeyJoinOracleTests : OracleTestFixture
{
    protected override string DatabaseNameSeed => nameof(PartialCompositeForeignKeyJoinOracleTests);

    protected override string Ddl => """
        CREATE TABLE dbo.Orders (OrderId INT NOT NULL, RevisionId INT NOT NULL, Total DECIMAL(10,2) NOT NULL, CONSTRAINT PK_Orders PRIMARY KEY (OrderId, RevisionId));
        CREATE TABLE dbo.OrderLines (LineId INT IDENTITY PRIMARY KEY, OrderId INT NOT NULL, RevisionId INT NOT NULL, Sku NVARCHAR(50) NOT NULL,
            CONSTRAINT FK_OrderLines_Orders FOREIGN KEY (OrderId, RevisionId) REFERENCES dbo.Orders(OrderId, RevisionId));
        """;

    [Fact]
    public async Task PartialCompositeJoin_FansOneChildRowOutAcrossEveryParentRevision_ButFullCompositeJoinDoesNot()
    {
        await using var connection = new SqlConnection(Options.BuildConnectionString(DatabaseName));
        await connection.OpenAsync();

        await using (var seedCommand = new SqlCommand(
            """
            INSERT INTO dbo.Orders (OrderId, RevisionId, Total) VALUES (1, 1, 100.00), (1, 2, 150.00);
            INSERT INTO dbo.OrderLines (OrderId, RevisionId, Sku) VALUES (1, 1, 'WIDGET-A');
            """, connection))
        {
            await seedCommand.ExecuteNonQueryAsync();
        }

        await using (var partialJoinCommand = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId;", connection))
        {
            var partialCount = (int)(await partialJoinCommand.ExecuteScalarAsync())!;
            Assert.Equal(2, partialCount);
        }

        await using (var fullCompositeJoinCommand = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.OrderLines ol JOIN dbo.Orders o ON ol.OrderId = o.OrderId AND ol.RevisionId = o.RevisionId;", connection))
        {
            var fullCount = (int)(await fullCompositeJoinCommand.ExecuteScalarAsync())!;
            Assert.Equal(1, fullCount);
        }
    }
}
