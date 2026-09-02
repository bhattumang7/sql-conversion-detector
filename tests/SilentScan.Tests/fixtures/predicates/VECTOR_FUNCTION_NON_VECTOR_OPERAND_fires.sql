CREATE TABLE dbo.Embedding
(
    EmbeddingId INT NOT NULL PRIMARY KEY,
    RawVector   VARCHAR(4000) NOT NULL,
    Query       VECTOR(3) NOT NULL
);
GO
SELECT EmbeddingId
FROM dbo.Embedding
WHERE VECTOR_DISTANCE('cosine', RawVector, Query) < 0.2;
