-- Same general principle as FUNCTION_WRAPPED_COLUMN_case_when_test_fires.sql (Microsoft Q&A,
-- Erland Sommarskog: "CASE expressions are not sargable" -
-- https://learn.microsoft.com/en-us/answers/questions/960508/is-case-when-then-sargable),
-- applied to the other column position a CASE can wrap: the THEN/ELSE VALUE rather than the
-- WHEN test. No dedicated measured repro for this specific column position was found, but it
-- is the same construct the WHEN-test fixture's own citation covers, and this fixture covers a
-- distinct branch of Tier-1's own detection code (FindAnyColumn's THEN/ELSE search). The WHEN
-- test is deliberately column-free (a fixed discriminator, not a comparison against any table
-- column) so this fixture isolates the THEN/ELSE search path from the WHEN-test search path,
-- which already has its own dedicated fixture pair above.
CREATE TABLE dbo.Orders
(
    Id             INT NOT NULL PRIMARY KEY,
    DomesticStatus VARCHAR(20) NOT NULL,
    IntlStatus     VARCHAR(20) NOT NULL
);
GO
CREATE INDEX IX_Orders_DomesticStatus ON dbo.Orders(DomesticStatus);
GO

DECLARE @RunIsDomestic BIT = 1;

SELECT Id
FROM dbo.Orders
WHERE (CASE WHEN @RunIsDomestic = 1 THEN DomesticStatus ELSE IntlStatus END) = 'Active';
