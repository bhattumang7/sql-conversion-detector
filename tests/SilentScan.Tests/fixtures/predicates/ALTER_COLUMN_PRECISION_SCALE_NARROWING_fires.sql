-- Oracle-confirmed against the standing Docker instance: ALTER COLUMN narrowing a DECIMAL/NUMERIC
-- column's declared precision or scale below its current catalog value either fails outright
-- (Msg 8115, "Arithmetic overflow error converting numeric to data type numeric") when existing
-- data no longer fits, or silently rounds away digits past the new scale when it does fit.
CREATE TABLE dbo.Invoice
(
    InvoiceId INT NOT NULL PRIMARY KEY,
    Total     DECIMAL(10, 4) NOT NULL
);
GO
ALTER TABLE dbo.Invoice ALTER COLUMN Total DECIMAL(10, 2);
