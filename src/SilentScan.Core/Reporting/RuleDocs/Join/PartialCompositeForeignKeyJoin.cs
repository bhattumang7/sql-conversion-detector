using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Join;

internal static class PartialCompositeForeignKeyJoin
{
    public static string RuleId => SarifRuleCatalog.PartialCompositeForeignKeyJoinRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A composite foreign key exists specifically because no single column of the child table
            uniquely identifies its parent - the relationship is only meaningful when every column
            pair in the key is honored together. A classic example is an order-line table keyed to
            its order by (OrderId, LineNumber) referencing a header table's (OrderId,
            SomeLineTypeId) composite key, or a multi-tenant schema where every foreign key also
            carries TenantId alongside the "real" id column specifically so a query can't
            accidentally join across tenants. The database engine enforces the composite key as a
            unit at write time - a child row must match the parent on all of its columns together,
            or the FK rejects the insert. But nothing at read time forces a query's JOIN to honor
            all of those columns too. SQL Server will happily execute a JOIN that equates only one
            column of a two-or-more-column foreign key relationship; ScriptDom parses it, the
            optimizer plans it, and it returns rows.

            The columns left out of the JOIN condition are exactly the columns that were supposed
            to narrow the match down to the single correct parent row. Omitting them means the join
            predicate is weaker than the actual relationship the schema encodes, so one parent row
            can now match every child row that merely happens to share the one column that is
            still being compared - even rows that belong to a completely different order, tenant,
            or type under the full key. The result is silent row multiplication: a report that
            should show one line per order-line instead shows a cross-product blowup wherever the
            partial match fans out, and downstream aggregates (SUM, COUNT) are inflated by exactly
            the degree of that fan-out. Nothing in the query's own execution signals a problem - it
            runs, it returns a plausible-looking row shape, and the numbers are simply wrong.

            This is a correctness defect, not a missed-index performance one: the join predicate
            itself describes a broader relationship than the schema's own foreign key promises, so
            even a perfectly-indexed version of the same partial join still returns the wrong rows.
            """,
        HowToFixIt: """
            Add the missing column pair(s) to the JOIN's ON clause so every column of the composite
            foreign key is equated, matching the relationship the schema itself declares. If the
            query genuinely intends a broader match than the full key - for example deliberately
            joining across TenantId for an administrative cross-tenant report - that intent should
            be explicit in review and ideally documented at the call site, since it's
            indistinguishable from an accidental omission otherwise.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "A tenant-scoped composite foreign key joined on only one column",
                NoncompliantSql: """
                    CREATE TABLE dbo.Orders
                    (
                        TenantId INT NOT NULL,
                        OrderId  INT NOT NULL,
                        CONSTRAINT PK_Orders PRIMARY KEY (TenantId, OrderId)
                    );
                    CREATE TABLE dbo.OrderLines
                    (
                        TenantId  INT NOT NULL,
                        OrderId   INT NOT NULL,
                        LineId    INT NOT NULL,
                        Quantity  INT NOT NULL,
                        CONSTRAINT PK_OrderLines PRIMARY KEY (TenantId, LineId),
                        CONSTRAINT FK_OrderLines_Orders FOREIGN KEY (TenantId, OrderId)
                            REFERENCES dbo.Orders (TenantId, OrderId)
                    );

                    SELECT o.OrderId, SUM(ol.Quantity) AS TotalQuantity
                    FROM dbo.Orders AS o
                    JOIN dbo.OrderLines AS ol ON ol.OrderId = o.OrderId
                    GROUP BY o.OrderId;
                    """,
                NoncompliantExplanation: "The FK is (TenantId, OrderId), but the JOIN equates only OrderId - an OrderId that recurs across different tenants (a common pattern when OrderId is per-tenant sequential, not globally unique) matches every tenant's rows with that OrderId, silently inflating TotalQuantity.",
                CompliantSql: """
                    SELECT o.OrderId, SUM(ol.Quantity) AS TotalQuantity
                    FROM dbo.Orders AS o
                    JOIN dbo.OrderLines AS ol
                        ON ol.TenantId = o.TenantId AND ol.OrderId = o.OrderId
                    GROUP BY o.TenantId, o.OrderId;
                    """,
                CompliantExplanation: "Both columns of the composite foreign key are now equated, so each order's lines are matched only within the same tenant - exactly the relationship the FK declares."),
        ]);
}
