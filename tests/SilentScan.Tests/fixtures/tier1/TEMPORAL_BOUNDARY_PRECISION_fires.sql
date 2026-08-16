-- Source: Aaron Bertrand, "Bad Habits: Using BETWEEN" (sqlblog.org / mssqltips.com) - the
-- widely-cited, canonical write-up of exactly this bug: BETWEEN with an end-of-period literal
-- silently excludes rows whose value falls in the precision gap between the literal's own
-- fractional-second digits and the column's real declared precision. Oracle-confirmed directly
-- against this exact shape: a DATETIME2(7) row at 2024-12-31 23:59:59.9999999 is silently
-- dropped by this query's own WHERE clause.
CREATE TABLE dbo.Events
(
    EventId    INT NOT NULL PRIMARY KEY,
    OccurredAt DATETIME2(7) NOT NULL
);
GO

SELECT EventId
FROM dbo.Events
WHERE OccurredAt BETWEEN '2024-01-01' AND '2024-12-31 23:59:59.997';
