SELECT *
FROM OPENROWSET(BULK 'D:\data\import.csv', FORMAT = 'CSV')
WITH (Id INT, Path hierarchyid) AS Import;
