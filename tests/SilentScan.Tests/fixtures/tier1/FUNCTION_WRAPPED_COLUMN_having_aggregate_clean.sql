-- docs/audit-remediation-plan.md Phase 3.1: hand-authored to test our own detector, not
-- sourced from a specific real-world incident (the sargability principle is well-established -
-- see the YEAR()/Brent Ozar citation on the sibling _fires.sql fixture - but this fixture
-- exists to pin a distinct, narrower claim: an AGGREGATE function wrapping a column in HAVING
-- is not the same kind of non-sargable wrap a scalar function is. HAVING SUM(Qty) > 5 has no
-- alternative "unwrapped" form the way WHERE YEAR(SomeDate) = 2018 does (there is no per-row
-- Qty to seek on once GROUP BY has aggregated it) - flagging it as FunctionWrappedColumn was a
-- confirmed false positive, verified against the live scanner before this fixture was written.
-- Must NOT fire.
CREATE TABLE dbo.OrderLines
(
    OrderId INT NOT NULL,
    Qty     INT NOT NULL
);
GO
CREATE INDEX IX_OrderLines_Qty ON dbo.OrderLines(Qty);
GO

SELECT OrderId
FROM dbo.OrderLines
GROUP BY OrderId
HAVING SUM(Qty) > 5;
