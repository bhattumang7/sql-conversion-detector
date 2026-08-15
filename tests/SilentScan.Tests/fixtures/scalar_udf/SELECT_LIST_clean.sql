-- Near-miss for SELECT_LIST_fires.sql: an inline TVF referenced in FROM reads identically to a
-- scalar UDF call syntactically (schema.name(args)), but it is never in the scalar-UDF catalog
-- registry (TryGetScalarUdfInfo), so it must never be mistaken for one here - that distinction
-- belongs entirely to the MSTVF-as-fence stream instead.
CREATE FUNCTION dbo.itvf_ActiveUsers()
RETURNS TABLE
AS
RETURN (SELECT Id, DisplayName FROM dbo.Users WHERE Reputation > 0);
GO
CREATE TABLE dbo.Users
(
    Id INT NOT NULL PRIMARY KEY,
    DisplayName NVARCHAR(40) NOT NULL,
    Reputation INT NOT NULL
);
GO
SELECT Id, DisplayName FROM dbo.itvf_ActiveUsers();
