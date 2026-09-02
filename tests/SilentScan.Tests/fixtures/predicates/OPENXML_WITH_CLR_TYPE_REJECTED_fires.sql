DECLARE @docHandle INT;
DECLARE @xml VARCHAR(1000) = '<Root><Shape/></Root>';
EXEC sp_xml_preparedocument @docHandle OUTPUT, @xml;

SELECT *
FROM OPENXML(@docHandle, '/Root/Shape', 1)
WITH (Boundary geometry);
