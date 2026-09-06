using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class FullTextIndexDdlScannerTests
{
    private static IReadOnlyList<FullTextIndexDdlFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));

        var catalog = CatalogBuilder.Build([result]);
        return FullTextIndexDdlScanner.Scan(catalog);
    }

    [Fact]
    public void HexLanguageId_Invalid_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(200) NULL);
            CREATE FULLTEXT INDEX ON dbo.T(Body LANGUAGE 0x0F423F) KEY INDEX PK_T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(FullTextIndexDdlFindingKind.InvalidLanguageId, finding.Kind);
    }

    [Fact]
    public void HexLanguageId_KnownLcid_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(200) NULL);
            CREATE FULLTEXT INDEX ON dbo.T(Body LANGUAGE 0x409) KEY INDEX PK_T;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void NonNumericLanguageTerm_LeftUnchecked()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body NVARCHAR(200) NULL);
            CREATE FULLTEXT INDEX ON dbo.T(Body LANGUAGE English) KEY INDEX PK_T;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void TextptrComputedColumn_UnsupportedColumnType_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body TEXT NULL, Ptr AS (TEXTPTR(Body)));
            CREATE FULLTEXT INDEX ON dbo.T(Ptr) KEY INDEX PK_T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(FullTextIndexDdlFindingKind.UnsupportedColumnType, finding.Kind);
        Assert.Equal("Ptr", finding.ColumnName);
    }

    [Fact]
    public void JsonColumn_UnsupportedColumnType_Fires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body JSON NULL);
            CREATE FULLTEXT INDEX ON dbo.T(Body) KEY INDEX PK_T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(FullTextIndexDdlFindingKind.UnsupportedColumnType, finding.Kind);
        Assert.Equal("Body", finding.ColumnName);
    }

    [Fact]
    public void PersistedNonDeterministicComputedColumn_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(200) NULL, Checksummed AS (CONVERT(VARCHAR(20), CHECKSUM(Body))) PERSISTED);
            CREATE FULLTEXT INDEX ON dbo.T(Checksummed) KEY INDEX PK_T;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void DeterministicNonpersistedComputedColumn_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(200) NULL, Checksummed AS (CONVERT(VARCHAR(20), CHECKSUM(Body))));
            CREATE FULLTEXT INDEX ON dbo.T(Checksummed) KEY INDEX PK_T;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void SeededRand_TreatedAsDeterministic_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(200) NULL, Salted AS (Body + CONVERT(VARCHAR(20), RAND(1))));
            CREATE FULLTEXT INDEX ON dbo.T(Salted) KEY INDEX PK_T;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void GlobalVariableInComputedColumn_TreatedAsNonDeterministic()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(200) NULL, Stamped AS (Body + CONVERT(VARCHAR(20), @@SPID)));
            CREATE FULLTEXT INDEX ON dbo.T(Stamped) KEY INDEX PK_T;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(FullTextIndexDdlFindingKind.NonDeterministicComputedColumn, finding.Kind);
    }

    [Theory]
    [InlineData("FORMAT(Id, 'N')")]
    [InlineData("PARSENAME(Body, 1)")]
    [InlineData("CAST(SYSDATETIMEOFFSET() AT TIME ZONE 'UTC' AS VARCHAR(50))")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("CURRENT_DATE")]
    [InlineData("CURRENT_USER")]
    [InlineData("SESSION_USER")]
    [InlineData("SYSTEM_USER")]
    [InlineData("USER")]
    [InlineData("OBJECT_ID('sys.tables')")]
    [InlineData("OBJECT_NAME(Id)")]
    [InlineData("OBJECTPROPERTY(Id,'IsTable')")]
    [InlineData("OBJECTPROPERTYEX(Id,'BaseType')")]
    [InlineData("DB_ID()")]
    [InlineData("DB_NAME()")]
    [InlineData("DATABASEPROPERTY(DB_NAME(),'IsAutoClose')")]
    [InlineData("DATABASEPROPERTYEX(DB_NAME(),'Collation')")]
    [InlineData("SCHEMA_ID('dbo')")]
    [InlineData("SCHEMA_NAME(Id)")]
    [InlineData("COL_NAME(Id,1)")]
    [InlineData("COL_LENGTH('sys.tables','name')")]
    [InlineData("TYPE_ID('int')")]
    [InlineData("TYPE_NAME(Id)")]
    [InlineData("TYPEPROPERTY('int','precision')")]
    [InlineData("COLUMNPROPERTY(Id,'name','AllowsNull')")]
    [InlineData("INDEXPROPERTY(Id,'x','IndexDepth')")]
    [InlineData("FILEPROPERTY('master','IsPrimaryFile')")]
    [InlineData("ASSEMBLYPROPERTY('x','AssemblyCulture')")]
    [InlineData("COLLATIONPROPERTY('Latin1_General_CI_AS','CodePage')")]
    [InlineData("CONNECTIONPROPERTY('net_transport')")]
    [InlineData("SESSIONPROPERTY('ANSI_NULLS')")]
    [InlineData("SERVERPROPERTY('ProductVersion')")]
    [InlineData("USER_ID()")]
    [InlineData("USER_NAME()")]
    [InlineData("SUSER_ID()")]
    [InlineData("SUSER_NAME()")]
    [InlineData("SUSER_SID()")]
    [InlineData("SUSER_SNAME()")]
    [InlineData("IS_MEMBER('public')")]
    [InlineData("IS_ROLEMEMBER('public')")]
    [InlineData("IS_SRVROLEMEMBER('public')")]
    [InlineData("HAS_PERMS_BY_NAME('sys.tables','OBJECT','SELECT')")]
    [InlineData("HAS_DBACCESS('master')")]
    [InlineData("PERMISSIONS()")]
    [InlineData("APP_NAME()")]
    [InlineData("HOST_ID()")]
    [InlineData("HOST_NAME()")]
    [InlineData("PROGRAM_NAME()")]
    [InlineData("ORIGINAL_LOGIN()")]
    [InlineData("CONTEXT_INFO()")]
    [InlineData("SESSION_CONTEXT(N'x')")]
    [InlineData("CURRENT_TRANSACTION_ID()")]
    [InlineData("XACT_STATE()")]
    [InlineData("CURRENT_TIMEZONE()")]
    [InlineData("CURRENT_TIMEZONE_ID()")]
    [InlineData("CURRENT_REQUEST_ID()")]
    [InlineData("IDENT_CURRENT('sys.tables')")]
    [InlineData("IDENT_INCR('sys.tables')")]
    [InlineData("IDENT_SEED('sys.tables')")]
    [InlineData("SCOPE_IDENTITY()")]
    [InlineData("ROWCOUNT_BIG()")]
    [InlineData("GETANSINULL()")]
    [InlineData("PARSE('1' AS INT)")]
    [InlineData("TRY_PARSE('1' AS INT)")]
    [InlineData("DATENAME(MONTH, GETDATE())")]
    [InlineData("TRY_CAST(Body AS DATE)")]
    [InlineData("TRY_CONVERT(DATE, Body, 9)")]
    [InlineData("INDEX_COL('sys.tables',1,1)")]
    [InlineData("OBJECT_DEFINITION(Id)")]
    [InlineData("OBJECT_SCHEMA_NAME(Id)")]
    [InlineData("ORIGINAL_DB_NAME()")]
    [InlineData("DATABASE_PRINCIPAL_ID()")]
    [InlineData("DEFAULT_DOMAIN()")]
    [InlineData("LOGINPROPERTY('sa','IsLocked')")]
    [InlineData("STATS_DATE(1,1)")]
    [InlineData("APPLOCK_MODE('public','x')")]
    [InlineData("APPLOCK_TEST('public','x','Exclusive')")]
    [InlineData("COMPRESS(Body)")]
    [InlineData("DECOMPRESS(COMPRESS(Body))")]
    [InlineData("FORMATMESSAGE('%s', Body)")]
    [InlineData("PWDENCRYPT(Body)")]
    [InlineData("PWDCOMPARE(Body, PWDENCRYPT(Body))")]
    [InlineData("KEY_GUID('x')")]
    [InlineData("KEY_ID('x')")]
    [InlineData("KEY_NAME('00000000-0000-0000-0000-000000000000')")]
    [InlineData("CERTPROPERTY(Id,'ExpiryDate')")]
    [InlineData("ASYMKEY_ID('x')")]
    [InlineData("ASYMKEYPROPERTY(Id,'AlgorithmDesc')")]
    [InlineData("SYMKEYPROPERTY(Id,'Algorithm')")]
    [InlineData("SIGNBYASYMKEY(Id,Body)")]
    [InlineData("CHANGE_TRACKING_CURRENT_VERSION()")]
    [InlineData("CHANGE_TRACKING_MIN_VALID_VERSION(Id)")]
    [InlineData("VECTOR_DISTANCE('cosine', CAST('[1,2,3]' AS VECTOR(3)), CAST('[4,5,6]' AS VECTOR(3)))")]
    [InlineData("VECTOR_NORM(CAST('[1,2,3]' AS VECTOR(3)), 'norm2')")]
    [InlineData("VECTOR_NORMALIZE(CAST('[1,2,3]' AS VECTOR(3)), 'norm2')")]
    [InlineData("VECTORPROPERTY(CAST('[1,2,3]' AS VECTOR(3)), 'Dimensions')")]
    [InlineData("AI_GENERATE_EMBEDDINGS(Body USE MODEL dummy)")]
    public void NonDeterministicBuiltin_InNonpersistedComputedColumn_Fires(string expression)
    {
        var findings = Scan(
            $"""
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(200) NULL, Stamped AS ({expression}));
            CREATE FULLTEXT INDEX ON dbo.T(Stamped) KEY INDEX PK_T;
            """);

        Assert.Contains(findings, f => f.Kind == FullTextIndexDdlFindingKind.NonDeterministicComputedColumn && f.ColumnName == "Stamped");
    }

    [Theory]
    [InlineData("CAST(Body AS DATE)")]
    [InlineData("CONVERT(DATE, Body)")]
    [InlineData("CONVERT(DATE, Body, 0)")]
    [InlineData("CONVERT(DATE, Body, 9)")]
    [InlineData("CONVERT(DATE, Body, 113)")]
    public void CharToDateConversion_WithoutSafeStyle_TreatedAsNonDeterministic(string expression)
    {
        var findings = Scan(
            $"""
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(30) NULL, Stamped AS ({expression}));
            CREATE FULLTEXT INDEX ON dbo.T(Stamped) KEY INDEX PK_T;
            """);

        Assert.Contains(findings, f => f.Kind == FullTextIndexDdlFindingKind.NonDeterministicComputedColumn && f.ColumnName == "Stamped");
    }

    [Theory]
    [InlineData("CONVERT(DATE, Body, 101)")]
    [InlineData("CONVERT(DATE, Body, 112)")]
    [InlineData("CONVERT(DATE, Body, 120)")]
    public void CharToDateConversion_WithSafeStyle_TreatedAsDeterministic_NeverFires(string expression)
    {
        var findings = Scan(
            $"""
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(30) NULL, Stamped AS ({expression}));
            CREATE FULLTEXT INDEX ON dbo.T(Body) KEY INDEX PK_T;
            ALTER FULLTEXT INDEX ON dbo.T ADD (Stamped);
            """);

        Assert.DoesNotContain(findings, f => f.Kind == FullTextIndexDdlFindingKind.NonDeterministicComputedColumn && f.ColumnName == "Stamped");
    }

    [Fact]
    public void NumericToDateConversion_NegativeControl_NeverFires()
    {
        var findings = Scan(
            """
            CREATE TABLE dbo.T (Id INT NOT NULL PRIMARY KEY, Body VARCHAR(30) NULL, Stamped AS (CAST(Id AS DATETIME)));
            CREATE FULLTEXT INDEX ON dbo.T(Body) KEY INDEX PK_T;
            ALTER FULLTEXT INDEX ON dbo.T ADD (Stamped);
            """);

        Assert.DoesNotContain(findings, f => f.Kind == FullTextIndexDdlFindingKind.NonDeterministicComputedColumn && f.ColumnName == "Stamped");
    }

    [Fact]
    public void MoreThan1024IndexedColumns_Fires()
    {
        var columnNames = Enumerable.Range(1, 1025).Select(i => $"Col{i}").ToList();
        var columnDefinitions = string.Join(", ", columnNames.Select(c => $"{c} NVARCHAR(50) NULL"));
        var indexColumnList = string.Join(", ", columnNames);

        var findings = Scan(
            $"""
            CREATE TABLE dbo.Wide (Id INT NOT NULL PRIMARY KEY, {columnDefinitions});
            CREATE FULLTEXT INDEX ON dbo.Wide({indexColumnList}) KEY INDEX PK_Wide;
            """);

        var finding = Assert.Single(findings);
        Assert.Equal(FullTextIndexDdlFindingKind.TooManyIndexedColumns, finding.Kind);
        Assert.Equal("dbo.Wide", finding.TableQualifiedName);
        Assert.Null(finding.ColumnName);
    }

    [Fact]
    public void ExactlyMaxIndexedColumns_NeverFires()
    {
        var columnNames = Enumerable.Range(1, 1024).Select(i => $"Col{i}").ToList();
        var columnDefinitions = string.Join(", ", columnNames.Select(c => $"{c} NVARCHAR(50) NULL"));
        var indexColumnList = string.Join(", ", columnNames);

        var findings = Scan(
            $"""
            CREATE TABLE dbo.Wide (Id INT NOT NULL PRIMARY KEY, {columnDefinitions});
            CREATE FULLTEXT INDEX ON dbo.Wide({indexColumnList}) KEY INDEX PK_Wide;
            """);

        Assert.Empty(findings);
    }

    [Fact]
    public void UnresolvedTable_NeverFires()
    {
        var findings = Scan(
            """
            CREATE FULLTEXT INDEX ON dbo.NotATable(SomeColumn) KEY INDEX PK_NotATable;
            """);

        Assert.Empty(findings);
    }
}
