SELECT *
FROM OPENROWSET(BULK 'D:\data\import.csv', FORMAT = 'CSV')
WITH (Id INT, Payload SQL_VARIANT) AS Import;
