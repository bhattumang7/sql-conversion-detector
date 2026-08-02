-- Near-miss sibling of FUNCTION_WRAPPED_COLUMN_case_then_value_fires.sql: the sargable rewrite
-- that splits into the two exhaustive @RunIsDomestic branches, leaving DomesticStatus/IntlStatus
-- each unwrapped in their own branch. Must NOT fire.
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
WHERE (@RunIsDomestic = 1 AND DomesticStatus = 'Active')
   OR (@RunIsDomestic = 0 AND IntlStatus = 'Active');
