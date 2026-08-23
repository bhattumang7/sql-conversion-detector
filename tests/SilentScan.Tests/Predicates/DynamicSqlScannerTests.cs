using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Common;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlScannerTests
{
    [Fact]
    public void Scan_SelectAssignmentFromSingleKnownTableColumn_FoldsToSymbolicPlaceholder()
    {
        var result = ScanWithCatalog(
            "CREATE TABLE dbo.SpecializedProcedureTemplates (SpecializedArea VARCHAR(50) NOT NULL, TemplateProcessorProcedureName VARCHAR(200) NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Scratch @SpecializedArea VARCHAR(50) AS
            BEGIN
                DECLARE @TemplateProcessorProcedureName VARCHAR(200)
                SELECT @TemplateProcessorProcedureName = 'EXEC ' + TemplateProcessorProcedureName
                FROM dbo.SpecializedProcedureTemplates
                WHERE SpecializedArea = @SpecializedArea

                EXEC (@TemplateProcessorProcedureName)
            END
            """);

        Assert.Empty(result.Findings);
        Assert.Single(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableDeclaredOnlyInsideOneIfBranch_AnalyzesBothTheKnownAndUnknownPath()
    {
        var result = Scan("""
            IF @flag = 1
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'
            END

            EXEC (@sql)
            """);

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText.Contains("__silentscan_sym_", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ExecOfVariableDivergingAcrossIfElseIfBranches_AnalyzesEachBranchIndependently()
    {
        var result = Scan("""
            DECLARE @action VARCHAR(10) = 'X'
            DECLARE @table VARCHAR(50) = 'dbo.Foo'
            DECLARE @id VARCHAR(10) = '1'
            DECLARE @SQL NVARCHAR(MAX)

            EXEC dbo.SomeUnknownProc @SQL OUTPUT

            IF @action = 'X'
                SET @SQL = 'DELETE ' + @table
            ELSE IF @action = 'Y'
                SET @SQL = 'UPDATE ' + @table + ' SET col = val'

            SET @SQL = @SQL + ' WHERE id = ' + @id
            EXEC (@SQL)
            """);

        Assert.Empty(result.Findings);
        Assert.Equal(3, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText.Contains("DELETE dbo.Foo WHERE id = 1", StringComparison.Ordinal));
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText.Contains("UPDATE dbo.Foo SET col = val WHERE id = 1", StringComparison.Ordinal));
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText.Contains("__silentscan_sym_", StringComparison.Ordinal) && s.InnerText.Contains("WHERE id = 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_SubstringOfVariableWithLenOfSameVariableAsLength_TrimsLeadingLiteralPrefix()
    {
        var result = Scan("""
            DECLARE @Name VARCHAR(50)
            DECLARE @predicate VARCHAR(MAX) = ''
            SET @predicate = @predicate + 'ABCD' + @Name
            SET @predicate = SUBSTRING(@predicate, 4, LEN(@predicate))
            EXEC (@predicate)
            """);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^D__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringOfVariableWithLenOfSameVariableAsLength_FirstPieceShorterThanTrim_FoldsToDeclaredTypeHole()
    {
        var result = Scan("""
            DECLARE @Name VARCHAR(50)
            DECLARE @predicate VARCHAR(MAX) = ''
            SET @predicate = @predicate + 'AB' + @Name
            SET @predicate = SUBSTRING(@predicate, 4, LEN(@predicate))
            EXEC (@predicate)
            """);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringOfVariableFromOneToLenMinusConstant_TrimsTrailingLiteralSuffix()
    {
        var result = Scan("""
            DECLARE @Name VARCHAR(50)
            DECLARE @select VARCHAR(MAX) = ''
            SET @select = @select + @Name + 'ABC,'
            SET @select = SUBSTRING(@select, 1, LEN(@select) - 1)
            EXEC ('SELECT ' + @select)
            """);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^SELECT __silentscan_sym_L\d+C\d+__ABC$", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringOfVariableWithStartZeroToLen_TrimsExactlyOneTrailingCharacter()
    {
        var result = Scan("""
            DECLARE @Name VARCHAR(50)
            DECLARE @select VARCHAR(MAX) = ''
            SET @select = @select + @Name + 'ABC,'
            SET @select = SUBSTRING(@select, 0, LEN(@select))
            EXEC ('SELECT ' + @select)
            """);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^SELECT __silentscan_sym_L\d+C\d+__ABC$", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringOfVariableFromOneToLenMinusConstant_TrailingLiteralShorterThanTrim_FoldsToDeclaredTypeHole()
    {
        var result = Scan("""
            DECLARE @Name VARCHAR(50)
            DECLARE @select VARCHAR(MAX) = ''
            SET @select = @select + @Name + 'C'
            SET @select = SUBSTRING(@select, 1, LEN(@select) - 2)
            EXEC ('SELECT ' + @select)
            """);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^SELECT __silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringTrimOfVariableWithSeveralIndependentOptionalColumnGroups_TrimsThroughTheNestedChoice()
    {
        var result = Scan(
            "DECLARE @select VARCHAR(MAX) = ''; " +
            "IF @F1 = 1 BEGIN SET @select = @select + 'ColA,'; END; " +
            "IF @F2 = 1 BEGIN SET @select = @select + 'ColB,'; END; " +
            "IF LEN(@select) > 0 SET @select = SUBSTRING(@select, 1, LEN(@select) - 1); " +
            "EXEC('SELECT ' + @select);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("SELECT ColA", texts);
        Assert.Contains("SELECT ColB", texts);
        Assert.Contains("SELECT ColA,ColB", texts);

        Assert.DoesNotContain("SELECT ColA,", texts);
        Assert.DoesNotContain("SELECT ColB,", texts);
        Assert.DoesNotContain("SELECT ColA,ColB,", texts);
    }

    [Fact]
    public void Scan_SubstringTrimOfVariableAlreadyTaintedWithAGuardedAlternative_StillAnalyzesRatherThanBreakingParse()
    {
        var result = Scan(
            "DECLARE @select VARCHAR(MAX) = ''; " +
            "IF 1 = 1 BEGIN SET @select = @select + 'ColA,'; END " +
            "ELSE BEGIN SET @select = FORMAT(2, N'N'); END " +
            "IF LEN(@select) > 0 SET @select = SUBSTRING(@select, 1, LEN(@select) - 1); " +
            "EXEC('SELECT ' + @select);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT ColA", script.InnerText);
    }

    [Fact]
    public void Scan_ParameterValidatedAgainstLiteralWithRaiserrorAndReturnOnMismatch_NarrowsToThatLiteralAfterward()
    {
        var result = Scan(
            "CREATE PROCEDURE dbo.usp_Test (@SourceTable VARCHAR(50)) AS BEGIN " +
            "IF @SourceTable <> 'tblTripsActual' BEGIN " +
            "RAISERROR('bad value', 16, 1); RETURN -1; END " +
            "EXEC('SELECT * FROM ' + @SourceTable); " +
            "END");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT * FROM tblTripsActual", script.InnerText);
    }

    [Fact]
    public void Scan_ParameterValidatedAgainstLiteralWithLiteralOnTheLeft_StillNarrows()
    {
        var result = Scan(
            "CREATE PROCEDURE dbo.usp_Test (@SourceTable VARCHAR(50)) AS BEGIN " +
            "IF 'tblTripsActual' <> @SourceTable BEGIN " +
            "RAISERROR('bad value', 16, 1); RETURN -1; END " +
            "EXEC('SELECT * FROM ' + @SourceTable); " +
            "END");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT * FROM tblTripsActual", script.InnerText);
    }

    [Fact]
    public void Scan_InequalityGuardWithoutAnUnconditionalReturnInThen_DoesNotNarrow()
    {
        var result = Scan(
            "CREATE PROCEDURE dbo.usp_Test (@SourceTable VARCHAR(50)) AS BEGIN " +
            "IF @SourceTable <> 'tblTripsActual' BEGIN " +
            "PRINT 'unexpected value'; END " +
            "EXEC('SELECT * FROM ' + @SourceTable); " +
            "END");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("variable-not-in-scope", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

private sealed class FakeRowValueFetcher(IReadOnlyDictionary<(string Table, string Column, string KeyColumn, string KeyValue), IReadOnlyList<string>> filteredValues, IReadOnlyDictionary<(string Table, string Column), IReadOnlyList<string>>? unfilteredValues = null) : ILiveRowValueFetcher
    {
        public List<(string TableQualifiedName, string SelectColumn, IReadOnlyList<(string Column, string LiteralValue)> EqualityKeys, int MaxRows)> Calls { get; } = [];

        public IReadOnlyList<string>? TryFetchDistinctValues(
            string tableQualifiedName, string selectColumn, IReadOnlyList<(string Column, string LiteralValue)> equalityKeys, int maxRows)
        {
            Calls.Add((tableQualifiedName, selectColumn, equalityKeys, maxRows));

            if (equalityKeys.Count == 1
                && filteredValues.TryGetValue((tableQualifiedName, selectColumn, equalityKeys[0].Column, equalityKeys[0].LiteralValue), out var filtered))
            {
                return filtered.Take(maxRows).ToList();
            }

            if (equalityKeys.Count == 0 && unfilteredValues is not null
                && unfilteredValues.TryGetValue((tableQualifiedName, selectColumn), out var unfiltered))
            {
                return unfiltered.Take(maxRows).ToList();
            }

            return null;
        }
    }

    [Fact]
    public void Scan_SelectAssignmentPinnedByLiteralEqualityKey_WithFetcher_ResolvesSingleRealValue()
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", "CREATE TABLE dbo.Templates (SettingName VARCHAR(50) NOT NULL, Definition VARCHAR(MAX) NOT NULL);");
        Assert.False(ddlResult.HasErrors);
        var catalog = CatalogBuilder.Build([ddlResult]);

        var fetcher = new FakeRowValueFetcher(new Dictionary<(string, string, string, string), IReadOnlyList<string>>
        {
            [("dbo.Templates", "Definition", "SettingName", "ReportSql")] = ["SELECT * FROM dbo.Reports"],
        });

        var result = SqlScriptParser.ParseText("test.sql", """
            DECLARE @sql VARCHAR(MAX)
            SELECT @sql = Definition FROM dbo.Templates WHERE SettingName = 'ReportSql'
            EXEC (@sql)
            """);
        Assert.False(result.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]), catalog: catalog, rowValueFetcher: fetcher);

        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Equal("SELECT * FROM dbo.Reports", script.InnerText);
        Assert.Equal(FindingConfidence.High, script.Confidence);

        Assert.NotEmpty(fetcher.Calls);
        Assert.All(fetcher.Calls, call =>
        {
            Assert.Equal("dbo.Templates", call.TableQualifiedName);
            Assert.Equal("Definition", call.SelectColumn);
            Assert.Equal(("SettingName", "ReportSql"), Assert.Single(call.EqualityKeys));
        });
    }

    [Fact]
    public void Scan_SelectAssignmentWithoutFetcher_StillFoldsToSymbolicPlaceholder_NotAffectedByFeature()
    {
        var result = ScanWithCatalog(
            "CREATE TABLE dbo.Templates (SettingName VARCHAR(50) NOT NULL, Definition VARCHAR(MAX) NOT NULL);",
            """
            DECLARE @sql VARCHAR(MAX)
            SELECT @sql = Definition FROM dbo.Templates WHERE SettingName = 'ReportSql'
            EXEC (@sql)
            """);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Contains("__silentscan_sym_", script.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_SelectAssignmentWithFetcher_NoWhereClause_FetchesEveryDistinctValue()
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", "CREATE TABLE dbo.Templates (Definition VARCHAR(MAX) NOT NULL);");
        Assert.False(ddlResult.HasErrors);
        var catalog = CatalogBuilder.Build([ddlResult]);
        var fetcher = new FakeRowValueFetcher(
            new Dictionary<(string, string, string, string), IReadOnlyList<string>>(),
            new Dictionary<(string, string), IReadOnlyList<string>> { [("dbo.Templates", "Definition")] = ["SELECT * FROM dbo.Reports"] });

        var result = SqlScriptParser.ParseText("test.sql", """
            DECLARE @sql VARCHAR(MAX)
            SELECT @sql = Definition FROM dbo.Templates
            EXEC (@sql)
            """);
        Assert.False(result.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]), catalog: catalog, rowValueFetcher: fetcher);

        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Equal("SELECT * FROM dbo.Reports", script.InnerText);
        Assert.Contains(fetcher.Calls, call => call.EqualityKeys.Count == 0);
    }

    [Fact]
    public void Scan_SelectAssignmentWithFetcher_MultipleMatchingRows_AnalyzesEachCandidateIndependently()
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", "CREATE TABLE dbo.Templates (SettingName VARCHAR(50) NOT NULL, Definition VARCHAR(MAX) NOT NULL);");
        Assert.False(ddlResult.HasErrors);
        var catalog = CatalogBuilder.Build([ddlResult]);
        var fetcher = new FakeRowValueFetcher(new Dictionary<(string, string, string, string), IReadOnlyList<string>>
        {
            [("dbo.Templates", "Definition", "SettingName", "Ambiguous")] = ["SELECT A FROM dbo.T1", "SELECT B FROM dbo.T2"],
        });

        var result = SqlScriptParser.ParseText("test.sql", """
            DECLARE @sql VARCHAR(MAX)
            SELECT @sql = Definition FROM dbo.Templates WHERE SettingName = 'Ambiguous'
            EXEC (@sql)
            """);
        Assert.False(result.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]), catalog: catalog, rowValueFetcher: fetcher);

        Assert.Empty(extraction.Findings);
        Assert.Equal(2, extraction.AnalyzableScripts.Count);
        Assert.Contains(extraction.AnalyzableScripts, s => s.InnerText == "SELECT A FROM dbo.T1");
        Assert.Contains(extraction.AnalyzableScripts, s => s.InnerText == "SELECT B FROM dbo.T2");
    }

    [Fact]
    public void Scan_SelectAssignmentWithFetcher_NonLiteralWhereCondition_StillFetchesIgnoringThatCondition()
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", "CREATE TABLE dbo.Templates (SettingName VARCHAR(50) NOT NULL, Definition VARCHAR(MAX) NOT NULL);");
        Assert.False(ddlResult.HasErrors);
        var catalog = CatalogBuilder.Build([ddlResult]);
        var fetcher = new FakeRowValueFetcher(
            new Dictionary<(string, string, string, string), IReadOnlyList<string>>(),
            new Dictionary<(string, string), IReadOnlyList<string>> { [("dbo.Templates", "Definition")] = ["SELECT * FROM dbo.Reports"] });

        var result = SqlScriptParser.ParseText("test.sql", """
            DECLARE @name VARCHAR(50) = 'x'
            DECLARE @sql VARCHAR(MAX)
            SELECT @sql = Definition FROM dbo.Templates WHERE SettingName = @name
            EXEC (@sql)
            """);
        Assert.False(result.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]), catalog: catalog, rowValueFetcher: fetcher);

        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Equal("SELECT * FROM dbo.Reports", script.InnerText);
        Assert.Contains(fetcher.Calls, call => call.EqualityKeys.Count == 0);
    }

    [Fact]
    public void Scan_SelectAssignmentWithFetcher_ZeroRowsMatch_DeclinesRatherThanGuesses()
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", "CREATE TABLE dbo.Templates (SettingName VARCHAR(50) NOT NULL, Definition VARCHAR(MAX) NOT NULL);");
        Assert.False(ddlResult.HasErrors);
        var catalog = CatalogBuilder.Build([ddlResult]);
        var fetcher = new FakeRowValueFetcher(new Dictionary<(string, string, string, string), IReadOnlyList<string>>());

        var result = SqlScriptParser.ParseText("test.sql", """
            DECLARE @sql VARCHAR(MAX)
            SELECT @sql = Definition FROM dbo.Templates WHERE SettingName = 'DoesNotExist'
            EXEC (@sql)
            """);
        Assert.False(result.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]), catalog: catalog, rowValueFetcher: fetcher);

        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Contains("__silentscan_sym_", script.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_DeclareInitializerAsScalarSubqueryFromSingleKnownTable_WithFetcher_ResolvesRealValue()
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", "CREATE TABLE dbo.Templates (SettingName VARCHAR(50) NOT NULL, Definition VARCHAR(MAX) NOT NULL);");
        Assert.False(ddlResult.HasErrors);
        var catalog = CatalogBuilder.Build([ddlResult]);
        var fetcher = new FakeRowValueFetcher(
            new Dictionary<(string, string, string, string), IReadOnlyList<string>>(),
            new Dictionary<(string, string), IReadOnlyList<string>> { [("dbo.Templates", "Definition")] = ["SELECT * FROM dbo.Reports"] });

        var result = SqlScriptParser.ParseText("test.sql", """
            DECLARE @name VARCHAR(50) = 'x'
            DECLARE @sql VARCHAR(MAX) = (SELECT Definition FROM dbo.Templates WHERE SettingName = @name)
            EXEC (@sql)
            """);
        Assert.False(result.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]), catalog: catalog, rowValueFetcher: fetcher);

        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Equal("SELECT * FROM dbo.Reports", script.InnerText);
        Assert.Contains(fetcher.Calls, call => call.TableQualifiedName == "dbo.Templates" && call.SelectColumn == "Definition" && call.EqualityKeys.Count == 0);
    }

    [Fact]
    public void Scan_SetAssignmentAsScalarSubqueryFromSingleKnownTable_WithFetcher_ResolvesRealValue()
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", "CREATE TABLE dbo.Templates (SettingName VARCHAR(50) NOT NULL, Definition VARCHAR(MAX) NOT NULL);");
        Assert.False(ddlResult.HasErrors);
        var catalog = CatalogBuilder.Build([ddlResult]);
        var fetcher = new FakeRowValueFetcher(new Dictionary<(string, string, string, string), IReadOnlyList<string>>
        {
            [("dbo.Templates", "Definition", "SettingName", "ReportSql")] = ["SELECT * FROM dbo.Reports"],
        });

        var result = SqlScriptParser.ParseText("test.sql", """
            DECLARE @sql VARCHAR(MAX)
            SET @sql = (SELECT Definition FROM dbo.Templates WHERE SettingName = 'ReportSql')
            EXEC (@sql)
            """);
        Assert.False(result.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]), catalog: catalog, rowValueFetcher: fetcher);

        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Equal("SELECT * FROM dbo.Reports", script.InnerText);
    }

    [Fact]
    public void Scan_SetAssignmentAsScalarSubqueryFilteredByTheSameVariableItAssigns_ResolvesToATypedHoleNotADecline()
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", "CREATE TABLE dbo.tblTrips (Reservation INT NOT NULL, CoordinatedReservation INT NULL, TripDate DATETIME NOT NULL);");
        Assert.False(ddlResult.HasErrors);
        var catalog = CatalogBuilder.Build([ddlResult]);

        var result = SqlScriptParser.ParseText("test.sql", """
            CREATE PROCEDURE dbo.usp_Test (@Reservation INT, @TripDate DATETIME) AS
            BEGIN
                SET @Reservation = (SELECT CoordinatedReservation FROM dbo.tblTrips t WHERE t.Reservation = @Reservation AND TripDate = @TripDate);
                EXEC sp_executesql N'SELECT @Reservation', N'@Reservation INT', @Reservation;
            END
            """);
        Assert.False(result.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]), catalog: catalog, rowValueFetcher: null);

        Assert.DoesNotContain(extraction.Findings, f => f.Reason == "non-literal-expression:sql-loaded-from-table");
    }

    [Fact]
    public void Scan_SetAssignmentFromSingleKnownTableWhereClauseIsAnUnfoldableNestedSubquery_ResolvesToATypedHoleNotADecline()
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", """
            CREATE TABLE dbo.tblCoordinatingAgencies (AgencyRegisteredName VARCHAR(255) NOT NULL, DatabaseName VARCHAR(50) NOT NULL);
            CREATE TABLE dbo.tblCoordinatedTripAgencies (CoordinatedAgencyID INT NOT NULL, AgencyID INT NOT NULL);
            CREATE TABLE dbo.tblTrips (Reservation INT NOT NULL, CoordinatedProviderAgencyID INT NOT NULL, AgencyID INT NOT NULL);
            """);
        Assert.False(ddlResult.HasErrors);
        var catalog = CatalogBuilder.Build([ddlResult]);

        var result = SqlScriptParser.ParseText("test.sql", """
            CREATE PROCEDURE dbo.usp_Test (@Reservation INT) AS
            BEGIN
                DECLARE @DatabaseName VARCHAR(50);
                SET @DatabaseName = (
                    SELECT DatabaseName
                    FROM dbo.tblCoordinatingAgencies
                    WHERE AgencyRegisteredName = (
                        SELECT AgencyRegisteredName FROM dbo.tblCoordinatedTripAgencies cta
                        INNER JOIN dbo.tblTrips t ON cta.CoordinatedAgencyID = t.CoordinatedProviderAgencyID AND t.AgencyID = cta.AgencyID
                        WHERE t.Reservation = @Reservation
                    )
                );
                EXEC sp_executesql N'SELECT @DatabaseName', N'@DatabaseName VARCHAR(50)', @DatabaseName;
            END
            """);
        Assert.False(result.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]), catalog: catalog, rowValueFetcher: null);

        Assert.DoesNotContain(extraction.Findings, f => f.Reason == "non-literal-expression:sql-loaded-from-table");
    }

    [Fact]
    public void Scan_IfConditionOnACallerSeededBitmaskProvablyTrue_StillResolvesBothBranches()
    {
        var callGraph = new ProcCallGraph([new ProcCallEdge(
            null, "dbo.usp_Test", new SourceSpan("caller.sql", 1, 1),
            [new ProcCallArgument("@Bits", new SqlType(SqlTypeCategory.Int), false, null, true, new ProcCallLiteralArgument("2", "caller.sql", 1, 1, 0))])]);
        var result = SqlScriptParser.ParseText("test.sql", """
            CREATE PROCEDURE dbo.usp_Test (@Bits INT) AS
            BEGIN
                DECLARE @Mask INT = 2;
                IF (@Bits & @Mask) = @Mask BEGIN SET @x = 'A'; END ELSE BEGIN SET @x = 'B'; END
                EXEC('SELECT ' + @x);
            END
            """);
        Assert.False(result.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(result, callGraph: callGraph);

        Assert.Empty(extraction.Findings);
        Assert.Equal(2, extraction.AnalyzableScripts.Count);
        Assert.Contains(extraction.AnalyzableScripts, s => s.InnerText == "SELECT A");
        Assert.Contains(extraction.AnalyzableScripts, s => s.InnerText == "SELECT B");
    }

    [Fact]
    public void Scan_IfConditionWithNoCallerSeededVariableAtAll_NeverPrunesEvenWhenProvable()
    {
        var result = SqlScriptParser.ParseText("test.sql", """
            CREATE PROCEDURE dbo.usp_Test AS
            BEGIN
                DECLARE @mode INT = 0;
                IF @mode = 0 BEGIN SET @x = 'A'; END ELSE BEGIN SET @x = 'B'; END
                EXEC('SELECT ' + @x);
            END
            """);
        Assert.False(result.HasErrors);

        var extraction = DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]));

        Assert.Empty(extraction.Findings);
        var texts = extraction.AnalyzableScripts.Select(s => s.InnerText).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["SELECT A", "SELECT B"], texts);
    }

    private static DynamicSqlExtractionResult Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DynamicSqlScannerV2.Scan(result);
    }

    private static DynamicSqlExtractionResult ScanWithEmptyCallGraph(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]));
    }

    private static DynamicSqlExtractionResult ScanWithCatalog(string ddl, string sql)
    {
        var ddlResult = SqlScriptParser.ParseText("ddl.sql", ddl);
        Assert.False(ddlResult.HasErrors, string.Join("; ", ddlResult.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([ddlResult]);

        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DynamicSqlScannerV2.Scan(result, callGraph: new ProcCallGraph([]), catalog: catalog);
    }

    [Fact]
    public void Scan_ExecBuiltFromCursorFetchedVariables_TreatsFetchTargetsAsSymbolicPlaceholders()
    {
        var result = ScanWithEmptyCallGraph("""
            CREATE PROCEDURE dbo.usp_Scratch AS
            BEGIN
                DECLARE @ObjectName VARCHAR(128), @ColName VARCHAR(128), @SQL VARCHAR(500)
                DECLARE cur CURSOR FOR SELECT TableName, ColumnName FROM dbo.SomeCatalog
                OPEN cur
                FETCH NEXT FROM cur INTO @ObjectName, @ColName
                WHILE (@@FETCH_STATUS = 0)
                BEGIN
                    SET @SQL = 'UPDATE ' + @ObjectName + ' SET ' + @ColName + ' = NULL'
                    EXEC (@SQL)
                    FETCH NEXT FROM cur INTO @ObjectName, @ColName
                END
                CLOSE cur
                DEALLOCATE cur
            END
            """);

        Assert.Empty(result.Findings);
        Assert.NotEmpty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecBuiltFromCursorFetchedVariablesDeclaredWithExplicitNullInitializer_TreatsFetchTargetsAsSymbolicPlaceholders()
    {
        var result = ScanWithEmptyCallGraph("""
            CREATE PROCEDURE dbo.usp_Scratch AS
            BEGIN
                DECLARE @ObjectName VARCHAR(128) = NULL, @ColName VARCHAR(128) = NULL, @SQL VARCHAR(500)
                DECLARE cur CURSOR FOR SELECT TableName, ColumnName FROM dbo.SomeCatalog
                OPEN cur
                FETCH NEXT FROM cur INTO @ObjectName, @ColName
                WHILE (@@FETCH_STATUS = 0)
                BEGIN
                    SET @SQL = 'UPDATE ' + @ObjectName + ' SET ' + @ColName + ' = NULL'
                    EXEC (@SQL)
                    FETCH NEXT FROM cur INTO @ObjectName, @ColName
                END
                CLOSE cur
                DEALLOCATE cur
            END
            """);

        Assert.Empty(result.Findings);
        Assert.NotEmpty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfTableNameBuiltFromNewId_TreatsNewIdAsSymbolicPlaceholder()
    {
        var result = Scan("""
            DECLARE @tableName VARCHAR(50)
            SET @tableName = 'tbl_RILTemp_' + REPLACE(CAST(NEWID() AS VARCHAR(36)), '-', '')
            EXEC ('DROP TABLE ' + @tableName)
            """);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfVariableDeclaredOnlyInsideTryBlock_ReferencedInsideCatchBlock_FoldsToSymbolicPlaceholder()
    {
        var result = Scan("""
            BEGIN TRY
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'
                EXEC (@sql)
            END TRY
            BEGIN CATCH
                EXEC (@sql)
            END CATCH
            """);

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
    }

    [Fact]
    public void Scan_ExecInsideTryBlock_AnalyzesUsingOnlyTrySideState_UnaffectedByLaterCatchReassignment()
    {
        var result = Scan("""
            DECLARE @SQL NVARCHAR(MAX) = N'SELECT 1'
            BEGIN TRY
                EXEC (@SQL)
            END TRY
            BEGIN CATCH
                SET @SQL = N'SELECT 2'
            END CATCH
            """);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfLocallyDeclaredLiteralVariable_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfUndeclaredVariable_Unanalyzable()
    {
        var result = Scan("EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("variable-not-in-scope", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromFunctionCall_Unanalyzable()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = FORMAT(1, N'N'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecStringListAtLinkedServer_DeclinesRatherThanAnalyzingAgainstTheLocalCatalog()
    {
        var result = Scan("EXEC ('SELECT Id FROM dbo.Orders WHERE CAST(Id AS varchar) = ''7''') AT REMOTE1;");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("linked-server-execute-not-modeled", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromColumnReference_ReasonNamesColumnReference()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1' + SomeColumn; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:column-reference", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromScalarSubquery_ReasonNamesSqlLoadedFromTable()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = (SELECT TOP 1 SomeColumn FROM dbo.T); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:sql-loaded-from-table", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromFromLessScalarSubquery_UnwrapsAndFoldsTheInnerExpression()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = (SELECT CASE WHEN 1 = 1 THEN N'SELECT 1' ELSE N'SELECT 2' END); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["SELECT 1", "SELECT 2"], texts);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromMultiColumnFromLessSubquery_StillDeclinesAsSubquery()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = (SELECT 1 AS a, 2 AS b); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:subquery", finding.Reason);
    }

    [Theory]
    [InlineData("@@TRANCOUNT")]
    [InlineData("@@ROWCOUNT")]
    [InlineData("@@ERROR")]
    [InlineData("@@IDENTITY")]
    [InlineData("@@NESTLEVEL")]
    [InlineData("@@SPID")]
    [InlineData("@@FETCH_STATUS")]
    public void Scan_ExecOfVariableAssignedFromKnownSystemGlobalVariable_FoldsToEnvironmentDependentHole(string globalVariable)
    {
        var result = Scan($"DECLARE @sql NVARCHAR(MAX) = 'SELECT ' + CAST({globalVariable} AS VARCHAR); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^SELECT __silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromUnknownSystemGlobalVariable_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = @@VERSION; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:other", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromSubtraction_ReasonNamesUnsupportedOperator()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1' + (5 - 1); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:unsupported-operator", finding.Reason);
    }

    [Fact]
    public void Scan_SetCursorVariable_TaintsRatherThanCrashes()
    {
        var result = Scan(
            "DECLARE @c CURSOR; DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "SET @c = CURSOR FOR SELECT 1 AS x; SET @sql = N'SELECT 1'; EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableReassignedInsideIfBranch_BothBranchAssembliesAnalyzed()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "IF 1 = 1 BEGIN SET @sql = N'SELECT 2'; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 2");
    }

    [Fact]
    public void Scan_ExecOfVariableUntouchedByUnrelatedIfBranch_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @other INT = 0; " +
            "IF 1 = 1 BEGIN SET @other = 1; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableInProcContainingGoto_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE PROCEDURE dbo.usp_Test AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "GOTO Skip; " +
            "Skip: " +
            "EXEC(@sql); " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterLabelThatCannotReachTheExec_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE PROCEDURE dbo.usp_Purge AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'DELETE FROM dbo.Orders WHERE RegionCode = ''EMEA'''; " +
            "EXEC(@sql); " +
            "IF 1 = 0 GOTO Cleanup; " +
            "Cleanup: " +
            "RETURN; " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("DELETE FROM dbo.Orders WHERE RegionCode = 'EMEA'", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableReassignedIdenticallyOnEveryPathThroughABackwardJump_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE PROCEDURE dbo.usp_ReadWithRetry AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX); " +
            "Retry: " +
            "SET @sql = N'SELECT OrderId FROM dbo.Orders WHERE OrderCode = ''A100'''; " +
            "EXEC(@sql); " +
            "IF 1 = 0 GOTO Retry; " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT OrderId FROM dbo.Orders WHERE OrderCode = 'A100'", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedIdenticallyOnEveryLoopIteration_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE PROCEDURE dbo.usp_RefreshBuckets AS BEGIN " +
            "DECLARE @i INT = 1; " +
            "DECLARE @sql NVARCHAR(MAX); " +
            "WHILE @i <= 3 " +
            "BEGIN " +
            "    SET @sql = N'UPDATE dbo.Orders SET Refreshed = 1 WHERE StatusCode = ''OPEN'''; " +
            "    EXEC(@sql); " +
            "    SET @i = @i + 1; " +
            "END " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("UPDATE dbo.Orders SET Refreshed = 1 WHERE StatusCode = 'OPEN'", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfAccumulatedConcatenation_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 '; " +
            "SET @sql = @sql + N'WHERE 1 = 1'; " +
            "SET @sql += N' AND 2 = 2'; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1 WHERE 1 = 1 AND 2 = 2", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterWhileLoopThatAssignsTheSameLiteralEveryIteration_BothOutcomesAnalyzed()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @i INT = 0; " +
            "WHILE @i < 1 BEGIN SET @sql = N'SELECT 2'; SET @i += 1; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["SELECT 1", "SELECT 2"], texts);
    }

    [Fact]
    public void Scan_ExecInsideWhileLoopUsingPreLoopValue_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @i INT = 0; " +
            "WHILE @i < 1 BEGIN EXEC(@sql); SET @i += 1; END");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterTryCatchThatTouchesIt_BothOutcomeAssembliesAnalyzed()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "BEGIN TRY SET @sql = N'SELECT 2'; END TRY " +
            "BEGIN CATCH SET @sql = N'SELECT 3'; END CATCH " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 2");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 3");
    }

    [Fact]
    public void Scan_ExecOfVariableUntouchedByUnrelatedTryCatch_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @other INT = 0; " +
            "BEGIN TRY SET @other = 1; END TRY " +
            "BEGIN CATCH SET @other = 2; END CATCH " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterOrdinaryPlainSelect_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "SELECT 1; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterUnrecognizedStatementNotMentioningIt_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (Col INT); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "INSERT INTO dbo.T (Col) VALUES (1); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterUnrecognizedStatementMerelyReadingIt_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (Col NVARCHAR(MAX)); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "INSERT INTO dbo.T (Col) VALUES (@sql); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterPrintReferencingIt_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "PRINT @sql; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterQuirkyUpdateAssignsIt_FoldsToTypedHole()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (Col NVARCHAR(MAX)); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "UPDATE dbo.T SET @sql = Col; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAfterFetchIntoAssignsIt_FoldsToTypedHole()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (Col NVARCHAR(MAX)); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE cur CURSOR FOR SELECT Col FROM dbo.T; " +
            "OPEN cur; " +
            "FETCH NEXT FROM cur INTO @sql; " +
            "CLOSE cur; " +
            "DEALLOCATE cur; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfUnrelatedVariableAfterQuirkyUpdate_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (Col NVARCHAR(MAX)); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @other NVARCHAR(MAX); " +
            "UPDATE dbo.T SET @other = Col; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfUnrelatedVariableAfterUnrecognizedStatement_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (Col INT); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @other INT = 1; " +
            "INSERT INTO dbo.T (Col) VALUES (@other); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfPureSelectAssignment_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 '; " +
            "SELECT @sql = @sql + N'WHERE 1 = 1';" +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1 WHERE 1 = 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfMultiAssignmentSelect_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @a NVARCHAR(20); DECLARE @b NVARCHAR(20); " +
            "SELECT @a = N'SELECT ', @b = N'1'; " +
            "EXEC(@a + @b);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfSelectAssignmentWithFromClause_FoldsToTypedHole()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (Col NVARCHAR(50)); " +
            "DECLARE @sql NVARCHAR(MAX); " +
            "SELECT @sql = Col FROM dbo.T; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfSelectAssignmentMixedWithRealColumn_FoldsToTypedHole()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "SELECT @sql = N'SELECT 2', 1 AS RealColumn; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableWithNoInitializer_ResolvableTypeFoldsToSymbolicPlaceholder()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableWithNoInitializer_UnresolvableAliasType_StaysTainted()
    {
        var result = Scan("DECLARE @sql dbo.SqlTextType; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("no-initializer", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableReassignedInsideElseBranch_BothBranchAssembliesAnalyzed()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "IF 1 = 0 BEGIN SET @sql = N'SELECT 2'; END " +
            "ELSE BEGIN SET @sql = N'SELECT 3'; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 2");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 3");
    }

    [Fact]
    public void Scan_ExecOfVariableDeclaredOnlyInASiblingIfElseIfBranch_FoldsToTypedHole_NotVariableNotInScope()
    {
        var result = Scan("""
            DECLARE @kind INT = 1
            IF @kind = 1
            BEGIN
                DECLARE @sql NVARCHAR(MAX)
                SET @sql = N'SELECT 1'
                EXEC(@sql)
            END
            ELSE IF @kind = 2
            BEGIN
                EXEC(@sql)
            END
            """);

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText.Contains("__silentscan_sym_", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ExecOfConcatenationWhereLeftOperandUndeclared_Unanalyzable()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = @undeclared + N'x'; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("variable-not-in-scope", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfConcatenationWhereRightOperandUndeclared_Unanalyzable()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'x' + @undeclared; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("variable-not-in-scope", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_MultiStatementFunctionBody_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE FUNCTION dbo.udf_Test() RETURNS INT AS " +
            "BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "RETURN 1; " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecInAlterProcedureBody_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "ALTER PROCEDURE dbo.usp_Test AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecInCreateOrAlterProcedureBody_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE OR ALTER PROCEDURE dbo.usp_Test AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecInAlterFunctionBody_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "ALTER FUNCTION dbo.udf_Test() RETURNS INT AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "RETURN 1; " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecInCreateTriggerBody_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (Col INT);\n" +
            "GO\n" +
            "CREATE TRIGGER dbo.trg_Test ON dbo.T AFTER INSERT AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecInAlterTriggerBody_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (Col INT);\n" +
            "GO\n" +
            "ALTER TRIGGER dbo.trg_Test ON dbo.T AFTER INSERT AS BEGIN " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC(@sql); " +
            "END;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExternalClrProcedure_NoStatementListBody_DoesNotThrow()
    {
        var result = Scan("CREATE PROCEDURE dbo.usp_Test AS EXTERNAL NAME Assembly.Class.Method;");

        Assert.Empty(result.Findings);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_InlineTableValuedFunction_NoStatementListBody_DoesNotThrow()
    {
        var result = Scan("CREATE FUNCTION dbo.udf_Test() RETURNS TABLE AS RETURN (SELECT 1 AS X);");

        Assert.Empty(result.Findings);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfParenthesizedLiteral_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = (N'SELECT 1'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_SpExecuteSqlWithNoArguments_Unanalyzable()
    {
        var result = Scan("EXEC sp_executesql;");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-argument", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedWithSubtractEquals_FoldsToTypedHole()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; SET @sql -= N'x'; EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfStringLiteral_ProducesAnalyzableScript()
    {
        var result = Scan("EXEC('SELECT 1');");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfConcatenatedLiterals_ProducesAnalyzableScriptWithFoldedText()
    {
        var result = Scan("EXEC('SELECT ' + '1');");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfLiteralConcatenatedWithLocallyDeclaredVariable_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @x NVARCHAR(10) = N'x'; EXEC('SELECT ' + @x);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT x", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfConcatenationWithUndeclaredVariable_Unanalyzable()
    {
        var result = Scan("EXEC('SELECT ' + @x);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("variable-not-in-scope", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_SpExecuteSqlWithLocallyDeclaredLiteralVariable_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; EXEC sp_executesql @sql;");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_SpExecuteSqlWithUndeclaredVariable_Unanalyzable()
    {
        var result = Scan("EXEC sp_executesql @sql;");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("variable-not-in-scope", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_SpExecuteSqlWithLiteral_ProducesAnalyzableScript()
    {
        var result = Scan("EXEC sp_executesql N'SELECT 1';");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_NoExecuteStatements_NoFindings()
    {
        var result = Scan("SELECT 1;");

        Assert.Empty(result.Findings);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_RegularProcedureExec_NoFinding()
    {
        var result = Scan("EXEC dbo.usp_DoThing;");

        Assert.Empty(result.Findings);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableMutatedByPriorProcCallWithOutput_FoldsToTypedHole()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC dbo.BuildQuery @sql OUTPUT; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableUnrelatedToPriorProcCallWithOutput_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @other NVARCHAR(MAX); " +
            "EXEC dbo.BuildQuery @other OUTPUT; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableMutatedByProcCallWithReturnAssignment_PreservesKnownValue_NotAnOutputArgument()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @rc INT; " +
            "EXEC @rc = dbo.BuildQuery @sql; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariablePassedAsOutputArgument_FoldsToTypedHole()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "EXEC dbo.BuildQuery @sql OUTPUT; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecInsideWhileLoopThatSelfMutatesTheExecutedVariable_Unanalyzable()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @i INT = 0; " +
            "WHILE @i < 3 BEGIN EXEC(@sql); SET @sql += N' AND 1=1'; SET @i += 1; END");

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("while-loop-body:cardinality-cap", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfSelectAssignmentWithWhereClause_FoldsToTypedHole()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX); " +
            "DECLARE @flag BIT = 1; " +
            "SELECT @sql = N'SELECT 1' WHERE @flag = 1; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    private const string CalleeProcName = "dbo.usp_RunLookup";

    private static ProcCallGraph SingleCallerGraph(ProcCallArgument argument) =>
        new([new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 10, 5), [argument])]);

    private static ProcCallGraph SingleCallerGraph() =>
        new([new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 10, 5), [])]);

    private static DynamicSqlExtractionResult ScanWithCallGraph(string sql, ProcCallGraph callGraph)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DynamicSqlScannerV2.Scan(result, callGraph: callGraph);
    }

    [Fact]
    public void Scan_ProcParamSeededFromSingleCallerLiteral_ProducesAnalyzableScriptAndExternalCallerAlternative()
    {
        var literal = new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2);
        var graph = SingleCallerGraph(new ProcCallArgument("@Status", FormalParameterType: null, FormalParameterIsOutput: false, CallerVariableName: null, IsLiteral: true, literal));

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1 WHERE Status = 'Active'");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText != "SELECT 1 WHERE Status = 'Active'");
    }

    [Fact]
    public void Scan_ProcParamWithMultipleCallersPassingSameLiteral_StillAddsExternalCallerAlternative()
    {
        var literal = new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2);
        var argument = new ProcCallArgument("@Status", null, false, null, true, literal);
        var graph = new ProcCallGraph([
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 10, 5), [argument]),
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 20, 5), [argument]),
        ]);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1 WHERE Status = 'Active'");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText != "SELECT 1 WHERE Status = 'Active'");
    }

    [Fact]
    public void Scan_ProcParamWithMultipleCallersPassingDifferentLiterals_AllAssembliesAnalyzed()
    {
        var activeArgument = new ProcCallArgument(
            "@Status", null, false, null, true, new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2));
        var archivedArgument = new ProcCallArgument(
            "@Status", null, false, null, true, new ProcCallLiteralArgument("Archived", "caller.sql", 20, 30, PrefixLength: 2));
        var graph = new ProcCallGraph([
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 10, 5), [activeArgument]),
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 20, 5), [archivedArgument]),
        ]);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        Assert.Equal(3, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1 WHERE Status = 'Active'");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1 WHERE Status = 'Archived'");
    }

    [Fact]
    public void Scan_ProcParamWithMultipleCallers_OneCallerNonLiteral_FoldsToSymbolicPlaceholder()
    {
        var literalArgument = new ProcCallArgument(
            "@Status", null, false, null, true, new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2));
        var variableArgument = new ProcCallArgument("@Status", null, false, "@callerVar", IsLiteral: false, LiteralArgument: null);
        var graph = new ProcCallGraph([
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 10, 5), [literalArgument]),
            new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 20, 5), [variableArgument]),
        ]);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ProcParamWithManyCallersPassingDistinctLiterals_CardinalityCapExceeded_CollapsesOverflowToTypedHole()
    {
        var edges = Enumerable.Range(0, 40)
            .Select(i => new ProcCallEdge(
                null,
                CalleeProcName,
                new SourceSpan("caller.sql", 10 + i, 5),
                [new ProcCallArgument("@Status", null, false, null, true, new ProcCallLiteralArgument($"Status{i}", "caller.sql", 10 + i, 30, PrefixLength: 2))]))
            .ToList();

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            new ProcCallGraph(edges));

        Assert.Empty(result.Findings);
        Assert.Equal(8, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText.Contains("__silentscan_sym_", StringComparison.Ordinal));
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1 WHERE Status = 'Status39'");
    }

    private static DynamicSqlExtractionResult ScanWithCallGraphAndOutputSummaries(
        string sql, ProcCallGraph callGraph, IReadOnlyDictionary<(string, string), IReadOnlyList<string>> outputSummaries)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DynamicSqlScannerV2.Scan(result, callGraph: callGraph, outputSummaryIndex: outputSummaries);
    }

    [Fact]
    public void Scan_ExecWithKnownOutputSummary_SeedsCallerVariable_ProducesAnalyzableScript()
    {
        var sql =
            "DECLARE @select varchar(max);\n" +
            "EXEC dbo.usp_BuildSelectClause @kind = 1, @out = @select OUTPUT;\n" +
            "EXEC ('SELECT ' + @select + ' FROM T');";

        var outputArgument = new ProcCallArgument("@out", null, FormalParameterIsOutput: true, CallerVariableName: "@select", IsLiteral: false);
        var graph = new ProcCallGraph([new ProcCallEdge(null, "dbo.usp_BuildSelectClause", new SourceSpan("test.sql", 2, 1), [outputArgument])]);
        var summaries = new Dictionary<(string, string), IReadOnlyList<string>>
        {
            [("dbo.usp_BuildSelectClause", "@out")] = ["Col1, Col2"],
        };

        var result = ScanWithCallGraphAndOutputSummaries(sql, graph, summaries);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT Col1, Col2 FROM T", script.InnerText);
    }

    [Fact]
    public void Scan_ExecWithKnownOutputSummary_CalleeCasingDiffersFromCallGraphEdge_StillSeedsCallerVariable()
    {
        var sql =
            "DECLARE @select varchar(max);\n" +
            "EXEC DBO.USP_BUILDSELECTCLAUSE @kind = 1, @out = @select OUTPUT;\n" +
            "EXEC ('SELECT ' + @select + ' FROM T');";

        var outputArgument = new ProcCallArgument("@out", null, FormalParameterIsOutput: true, CallerVariableName: "@select", IsLiteral: false);
        var graph = new ProcCallGraph([new ProcCallEdge(null, "DBO.USP_BUILDSELECTCLAUSE", new SourceSpan("test.sql", 2, 1), [outputArgument])]);
        var summaries = new Dictionary<(string, string), IReadOnlyList<string>>(TableColumnKeyComparer.Instance)
        {
            [("dbo.usp_BuildSelectClause", "@out")] = ["Col1, Col2"],
        };

        var result = ScanWithCallGraphAndOutputSummaries(sql, graph, summaries);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT Col1, Col2 FROM T", script.InnerText);
    }

    [Fact]
    public void Scan_ExecWithOutputArgumentButNoKnownSummary_FoldsToTypedHole()
    {
        var sql =
            "DECLARE @select varchar(max);\n" +
            "EXEC dbo.usp_BuildSelectClause @kind = 1, @out = @select OUTPUT;\n" +
            "EXEC ('SELECT ' + @select + ' FROM T');";

        var outputArgument = new ProcCallArgument("@out", null, FormalParameterIsOutput: true, CallerVariableName: "@select", IsLiteral: false);
        var graph = new ProcCallGraph([new ProcCallEdge(null, "dbo.usp_BuildSelectClause", new SourceSpan("test.sql", 2, 1), [outputArgument])]);

        var result = ScanWithCallGraphAndOutputSummaries(sql, graph, new Dictionary<(string, string), IReadOnlyList<string>>());

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.StartsWith("SELECT __silentscan_sym_", script.InnerText, StringComparison.Ordinal);
        Assert.EndsWith(" FROM T", script.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_ExecWithUnrelatedVariableAlongsideKnownOutputSummary_SeedsOutputBindingAndFoldsRcToTypedHole()
    {
        var sql =
            "DECLARE @select varchar(max);\n" +
            "DECLARE @rc int;\n" +
            "EXEC @rc = dbo.usp_BuildSelectClause @kind = 1, @out = @select OUTPUT;\n" +
            "DECLARE @sql varchar(max) = N'SELECT ' + @select + N' FROM T WHERE rc = ' + CAST(@rc AS varchar(10));\n" +
            "EXEC (@sql);";

        var outputArgument = new ProcCallArgument("@out", null, FormalParameterIsOutput: true, CallerVariableName: "@select", IsLiteral: false);
        var graph = new ProcCallGraph([new ProcCallEdge(null, "dbo.usp_BuildSelectClause", new SourceSpan("test.sql", 3, 1), [outputArgument])]);
        var summaries = new Dictionary<(string, string), IReadOnlyList<string>>
        {
            [("dbo.usp_BuildSelectClause", "@out")] = ["Col1"],
        };

        var result = ScanWithCallGraphAndOutputSummaries(sql, graph, summaries);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.StartsWith("SELECT Col1 FROM T WHERE rc = __silentscan_sym_", script.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_ProcParamWithSingleNonLiteralCaller_FoldsToSymbolicPlaceholder()
    {
        var argument = new ProcCallArgument("@Status", null, false, "@callerVar", IsLiteral: false, LiteralArgument: null);
        var graph = SingleCallerGraph(argument);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ProcParamWithSingleNonLiteralCaller_UnresolvableAliasType_StaysTainted()
    {
        var argument = new ProcCallArgument("@Status", null, false, "@callerVar", IsLiteral: false, LiteralArgument: null);
        var graph = SingleCallerGraph(argument);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status dbo.StatusCodeType AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("parameter-not-seeded:non-literal-caller", finding.Reason);
    }

    [Fact]
    public void Scan_ProcParamWithNoKnownCallers_ResolvableTypeFoldsToSymbolicPlaceholder()
    {
        var graph = new ProcCallGraph([]);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
        Assert.Matches(@"^SELECT 1 WHERE Status = '__silentscan_sym_L\d+C\d+__'$", script.InnerText);
    }

    [Fact]
    public void Scan_ProcParamWithNoKnownCallers_UnresolvableAliasTypeStaysTainted()
    {
        var graph = new ProcCallGraph([]);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status dbo.StatusCodeType AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("procedure-parameter:no-known-call-site", finding.Reason);
    }

    [Fact]
    public void Scan_OutputParamNeverSeededFromTheCallerSArgument_ButStillFoldsToASymbolicPlaceholder()
    {
        var literal = new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2);
        var argument = new ProcCallArgument("@Status", null, FormalParameterIsOutput: true, null, true, literal);
        var graph = SingleCallerGraph(argument);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) OUTPUT AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
        Assert.Matches(@"^SELECT 1 WHERE Status = '__silentscan_sym_L\d+C\d+__'$", script.InnerText);
    }

    [Fact]
    public void Scan_ProcParamOmittedByCallerRelyingOnItsOwnLiteralDefault_SeedsFromThatDefaultAndWidens()
    {
        var graph = SingleCallerGraph();
        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) = N'Active' AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1 WHERE Status = 'Active'");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText != "SELECT 1 WHERE Status = 'Active'");
    }

    [Fact]
    public void Scan_ProcParamOmittedByCallerWithNoDefaultDeclared_FoldsToSymbolicPlaceholder()
    {
        var graph = SingleCallerGraph();
        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
        Assert.Matches(@"^SELECT 1 WHERE Status = '__silentscan_sym_L\d+C\d+__'$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfQuoteNameOnLiteral_DefaultBracketDelimiter_FoldsToBracketedText()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = 'SELECT * FROM ' + QUOTENAME(N'Orders'); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT * FROM [Orders]", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfQuoteNameOnFoldedVariable_TierC_FoldsToBracketedText()
    {
        var result = Scan(
            "DECLARE @table VARCHAR(50) = 'Orders'; " +
            "DECLARE @sql VARCHAR(MAX) = 'SELECT * FROM ' + QUOTENAME(@table); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT * FROM [Orders]", script.InnerText);
    }

    private static DynamicSqlExtractionResult ScanQuoteName(string quoteNameExpression) =>
        Scan($"DECLARE @sql NVARCHAR(MAX) = {quoteNameExpression}; EXEC(@sql);");

    [Fact]
    public void Scan_QuoteNameOnLiteral_EmbeddedCloseBracket_EscapesByDoubling()
    {
        var result = ScanQuoteName("QUOTENAME(N'ab]c')");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("[ab]]c]", script.InnerText);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_EmbeddedOpenBracket_NeverEscaped()
    {
        var result = ScanQuoteName("QUOTENAME(N'ab[c')");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("[ab[c]", script.InnerText);
    }

private static string AsSqlStringLiteral(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [Theory]
    [InlineData("'", "ab'c", "'ab''c'")]
    [InlineData("\"", "ab\"c", "\"ab\"\"c\"")]
    [InlineData("(", "ab)c", "(ab))c)")]    [InlineData("<", "ab>c", "<ab>>c>")]
    [InlineData("{", "ab}c", "{ab}}c}")]
    public void Scan_QuoteNameOnLiteral_RecognizedDelimiter_MatchesOracleEscaping(string delimiter, string inputWithEmbeddedCloseChar, string expected)
    {
        var result = ScanQuoteName(
            $"QUOTENAME({AsSqlStringLiteral(inputWithEmbeddedCloseChar)}, {AsSqlStringLiteral(delimiter)})");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(expected, script.InnerText);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_UnrecognizedDelimiter_UnanalyzableWithNullResultReason()
    {
        var result = ScanQuoteName("QUOTENAME(N'abc', N'x')");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:quotename-null-result", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_MultiCharacterDelimiter_UnanalyzableWithNullResultReason()
    {
        var result = ScanQuoteName("QUOTENAME(N'abc', N'ab')");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:quotename-null-result", finding.Reason);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_InputOver128Characters_UnanalyzableWithNullResultReason()
    {
        var result = ScanQuoteName($"QUOTENAME(N'{new string('a', 129)}')");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:quotename-null-result", finding.Reason);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_Input128Characters_Folds()
    {
        var input = new string('a', 128);
        var result = ScanQuoteName($"QUOTENAME(N'{input}')");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal($"[{input}]", script.InnerText);
    }

    [Fact]
    public void Scan_QuoteNameOnLiteral_EmptyDelimiter_DefaultsToBrackets()
    {
        var result = ScanQuoteName("QUOTENAME(N'abc', N'')");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("[abc]", script.InnerText);
    }

    [Fact]
    public void Scan_QuoteNameOnColumnReference_FoldsToTypedHole()
    {
        var result = ScanQuoteName("QUOTENAME(SomeColumn)");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Contains("__silentscan_sym_", script.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_QuoteNameWithThreeArguments_UnanalyzableAsFunctionCall()
    {
        var result = ScanQuoteName("QUOTENAME(N'a', N'[', N'extra')");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
    }

    [Fact]
    public void Scan_DbNameConcatenatedIntoDynamicSql_FoldsToTypedHole()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = 'SELECT * FROM ' + DB_NAME() + '.dbo.Orders'; EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Contains("__silentscan_sym_", script.InnerText, StringComparison.Ordinal);
        Assert.Contains("SELECT * FROM ", script.InnerText, StringComparison.Ordinal);
        Assert.Contains(".dbo.Orders", script.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_OriginalLoginProducesTypedHole()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = ORIGINAL_LOGIN(); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_UnmodeledScalarUdfWithCatalogKnownReturnType_FoldsToTypedHole()
    {
        var result = ScanWithCatalog(
            "CREATE FUNCTION dbo.udf_FormatCode(@raw VARCHAR(50)) RETURNS VARCHAR(50) AS BEGIN RETURN @raw END;",
            "DECLARE @sql NVARCHAR(MAX) = 'SELECT * FROM dbo.Orders WHERE Code = ''' + dbo.udf_FormatCode(@SomeParam) + ''''; EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Contains("__silentscan_sym_", script.InnerText, StringComparison.Ordinal);
        Assert.Contains("SELECT * FROM dbo.Orders WHERE Code = '", script.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_UnmodeledScalarUdfWithoutSchemaQualification_StillResolvesAgainstDefaultDboSchema()
    {
        var result = ScanWithCatalog(
            "CREATE FUNCTION dbo.udf_Unqualified(@raw VARCHAR(50)) RETURNS VARCHAR(50) AS BEGIN RETURN @raw END;",
            "DECLARE @sql NVARCHAR(MAX) = udf_Unqualified(@SomeParam); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_CallToFunctionNotInCatalog_StillDeclinesAsUnrecognized()
    {
        var result = ScanWithCatalog(
            "CREATE TABLE dbo.Unrelated (Id INT NOT NULL);",
            "DECLARE @sql NVARCHAR(MAX) = dbo.udf_NeverDefined(@SomeParam); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
    }

    [Fact]
    public void Scan_UnmodeledScalarUdfWithNoCatalogSupplied_DeclinesRatherThanGuessing()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = dbo.udf_FormatCode(@SomeParam); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromUpperOnAsciiLiteral_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = UPPER(N'select 1'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromReverseOnLiteral_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = REVERSE(N'1 TCELES'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromLowerOnAsciiLiteral_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = LOWER(N'SELECT 1'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("select 1", script.InnerText);
    }

    [Theory]
    [InlineData("UPPER(N'select id')")]    [InlineData("UPPER(N'SELECT Id')")]    [InlineData("LOWER(N'select ID')")]
    public void Scan_CaseConversionOnInputContainingI_Declines_TurkishCollationAmbiguity(string expression)
    {
        var result = Scan($"DECLARE @sql NVARCHAR(MAX) = {expression}; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:case-conversion-collation-sensitive", finding.Reason);
    }

    [Fact]
    public void Scan_CaseConversionOnNonAsciiInput_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = UPPER(N'Ä'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:case-conversion-collation-sensitive", finding.Reason);
    }

    [Fact]
    public void Scan_LtrimOnSpacePaddedLiteral_TrimsOnlySpace_NotTab()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = LTRIM(N'  " + '\t' + "x'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("\tx", script.InnerText);
    }

    [Fact]
    public void Scan_RtrimOnSpacePaddedLiteral_TrimsOnlySpace_NotTab()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = RTRIM(N'x" + '\t' + "  '); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("x\t", script.InnerText);
    }

    [Fact]
    public void Scan_LeftWithinBounds_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = LEFT(N'abcdef', 3); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("abc", script.InnerText);
    }

    [Fact]
    public void Scan_LeftLengthBeyondInput_ClampsToWholeString()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = LEFT(N'abc', 10); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("abc", script.InnerText);
    }

    [Fact]
    public void Scan_RightWithinBounds_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = RIGHT(N'abcdef', 3); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("def", script.InnerText);
    }

    [Fact]
    public void Scan_LeftWithNegativeLength_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = LEFT(N'abc', -1); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:negative-length", finding.Reason);
    }

    [Fact]
    public void Scan_LeftWithLengthCarriedInIntVariable_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @n INT = 3; DECLARE @sql NVARCHAR(MAX) = LEFT(N'abcdef', @n); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("abc", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringWithinBounds_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', 2, 3); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("bcd", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringLengthBeyondInput_ClampsToRemainder()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', 2, 100); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("bcdef", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringStartBeyondInput_FoldsToEmptyString()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'X' + SUBSTRING(N'abcdef', 10, 5); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("X", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringWithNegativeLength_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', 2, -1); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:negative-length", finding.Reason);
    }

    [Fact]
    public void Scan_SubstringWithStartBelowOne_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', -2, 5); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:substring-start-below-one", finding.Reason);
    }

    [Fact]
    public void Scan_SubstringWithStartCarriedInIntVariable_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @n INT = 2; DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', @n, 3); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("bcd", script.InnerText);
    }

    [Fact]
    public void Scan_LeftOnFoldedVariable_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @table VARCHAR(50) = 'OrdersTable'; " +
            "DECLARE @sql VARCHAR(MAX) = 'SELECT * FROM ' + LEFT(@table, 6); " +
            "EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT * FROM Orders", script.InnerText);
    }

    [Fact]
    public void Scan_IntVariableChainThroughArithmetic_ResolvesCorrectSum_NotStringConcat()
    {
        var result = Scan(
            "DECLARE @i INT = 5; DECLARE @j INT = @i + 1; " +
            "DECLARE @sql NVARCHAR(MAX) = LEFT(N'abcdefg', @j); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("abcdef", script.InnerText);
    }

    [Fact]
    public void Scan_IntVariableAddEquals_ResolvesArithmeticSum_NotStringConcat()
    {
        var result = Scan(
            "DECLARE @i INT = 5; SET @i += 2; " +
            "DECLARE @sql NVARCHAR(MAX) = LEFT(N'abcdefg', @i); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("abcdefg", script.InnerText);
    }

    [Fact]
    public void Scan_IntVariableFromUnfoldableExpression_StillDeclines_NotAGuess()
    {
        var result = Scan(
            "CREATE TABLE dbo.T (N INT NOT NULL); " +
            "DECLARE @n INT; SELECT @n = N FROM dbo.T; " +
            "DECLARE @sql NVARCHAR(MAX) = LEFT(N'abcdef', @n); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call-argument-diverges", finding.Reason);
    }

    [Fact]
    public void Scan_ReplaceWithNoCaseAmbiguity_TierC_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = REPLACE(N'a-b-c', N'-', N'_'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("a_b_c", script.InnerText);
    }

    [Fact]
    public void Scan_ReplaceWhereOrdinalAndCaseInsensitiveDisagree_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = REPLACE(N'AbcABC', N'abc', N'X'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:replace-collation-sensitive", finding.Reason);
    }

    [Fact]
    public void Scan_ReplaceWithEmptyPattern_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = REPLACE(N'abc', N'', N'x'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:replace-empty-pattern", finding.Reason);
    }

    [Fact]
    public void Scan_ReplaceWithWrongArgumentCount_UnanalyzableAsFunctionCall()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = REPLACE(N'abc', N'a'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
    }

    [Fact]
    public void Scan_ReplaceOnVariableDivergedAcrossIfBranches_CrossProductsIntoUnionedAssemblies()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 '; " +
            "IF 1 = 1 SET @sql = @sql + N'AND t.col = ''$X$'' '; " +
            "SET @sql = REPLACE(@sql, '$X$', 'Y'); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["SELECT 1 ", "SELECT 1 AND t.col = 'Y' "], texts);
    }

    [Fact]
    public void Scan_ReplaceOnVariableDivergedAcrossIfBranches_DeclinesWholeFoldIfAnyCombinationCollationDiverges()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'AbcABC'; " +
            "IF 1 = 1 SET @sql = N'plain'; " +
            "SET @sql = REPLACE(@sql, 'abc', 'X'); " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:replace-collation-sensitive", finding.Reason);
    }

    [Fact]
    public void Scan_ReplaceWithReplicateAsReplacementArgument_FoldsCompletely()
    {
        var result = Scan(
            "DECLARE @Path NVARCHAR(MAX) = N'C:\\Backup''s\\file.bak'; " +
            "DECLARE @sql NVARCHAR(MAX) = REPLACE(@Path, N'''', REPLICATE(N'''', 2)); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("C:\\Backup''s\\file.bak", script.InnerText);
        Assert.Equal(FindingConfidence.High, script.Confidence);
    }

    [Fact]
    public void Scan_FunctionArgumentWithChoiceEmbeddedAmongLiteralPieces_CrossProductsAcrossBothBranches()
    {
        var result = Scan(
            "DECLARE @FileListParamSQL NVARCHAR(4000) = N'INSERT INTO t (a, b'; " +
            "IF @MajorVersion >= 13 BEGIN SET @FileListParamSQL += N', SnapshotUrl'; END; " +
            "SET @FileListParamSQL += N')' + NCHAR(13) + NCHAR(10); " +
            "SET @FileListParamSQL += N'EXEC (''RESTORE {Path}'')'; " +
            "DECLARE @sql NVARCHAR(MAX) = REPLACE(@FileListParamSQL, N'{Path}', N'known'); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(
            [
                "INSERT INTO t (a, b)\r\nEXEC ('RESTORE known')",
                "INSERT INTO t (a, b, SnapshotUrl)\r\nEXEC ('RESTORE known')",
            ],
            texts);
    }

    [Fact]
    public void Scan_ChainedReplaceCallsEachSplicingAHole_SubsequentReplaceStillFoldsAroundExistingHoles()
    {
        var result = Scan(
            "DECLARE @DatabaseName NVARCHAR(128); " +            "DECLARE @SchemaName NVARCHAR(128); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT * FROM @@@Database@@@.@@@Schema@@@.T'; " +
            "SET @sql = REPLACE(@sql, N'@@@Database@@@', @DatabaseName); " +
            "SET @sql = REPLACE(@sql, N'@@@Schema@@@', @SchemaName); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
        Assert.Matches(@"^SELECT \* FROM __silentscan_sym_L\d+C\d+__\.__silentscan_sym_L\d+C\d+__\.T$", script.InnerText);
    }

    [Fact]
    public void Scan_ChainedReplaceCalls_CollationSensitiveSegmentStillDeclinesWithSpecificReason()
    {
        var result = Scan(
            "DECLARE @Value NVARCHAR(128); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT AbcABC FROM @@@Marker@@@ WHERE x = 1'; " +
            "SET @sql = REPLACE(@sql, N'@@@Marker@@@', @Value); " +
            "SET @sql = REPLACE(@sql, N'abc', N'X'); " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:replace-collation-sensitive", finding.Reason);
    }

    [Fact]
    public void Scan_ReplaceSourceMixingChoiceAndHole_PreservesChoiceStructureThenSplicesEachLeaf()
    {
        var result = Scan(
            "DECLARE @TableName NVARCHAR(128); " +            "DECLARE @sql NVARCHAR(MAX) = N'CREATE TABLE @@@Table@@@ (a INT'; " +
            "IF @IncludeExtra = 1 BEGIN SET @sql += N', b INT'; END; " +
            "SET @sql += N')'; " +
            "SET @sql = REPLACE(@sql, N'@@@Table@@@', @TableName); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(2, texts.Count);
        Assert.Matches(@"^CREATE TABLE __silentscan_sym_L\d+C\d+__ \(a INT\)$", texts[0]);
        Assert.Matches(@"^CREATE TABLE __silentscan_sym_L\d+C\d+__ \(a INT, b INT\)$", texts[1]);
    }

    [Fact]
    public void Scan_ReplaceSourceWithTwoIndependentChoices_NoLongerDeclinesOutright()
    {
        var result = Scan(
            "DECLARE @DB_Name NVARCHAR(50); " +
            "DECLARE @a NVARCHAR(MAX) = N''; " +
            "DECLARE @b NVARCHAR(MAX) = N''; " +
            "IF @Flag1 = 1 BEGIN SET @a = N'ColA,'; END; " +
            "IF @Flag2 = 1 BEGIN SET @b = N'ColB,'; END; " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + @a + @b + N'* FROM $dbname$.dbo.T'; " +
            "SET @sql = REPLACE(@sql, N'$dbname$', @DB_Name); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(4, texts.Count);
        Assert.All(texts, t => Assert.Matches(@"^SELECT (ColA,)?(ColB,)?\* FROM __silentscan_sym_L\d+C\d+__\.dbo\.T$", t));
    }

    [Fact]
    public void Scan_CastOfFoldedVariableToNVarcharWithTruncation_TierC_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @raw NVARCHAR(MAX) = N'HelloWorld'; " +
            "DECLARE @sql NVARCHAR(MAX) = CAST(@raw AS NVARCHAR(5)); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("Hello", script.InnerText);
    }

    [Fact]
    public void Scan_ConvertOfLiteralToVarcharWithinLength_ProducesAnalyzableScript()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CONVERT(VARCHAR(20), N'SELECT 1'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_CastToNonStringTarget_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CAST(N'select 1' AS INT); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:cast-target-not-pinned", finding.Reason);
    }

    [Fact]
    public void Scan_CastToCharTargetWithExplicitLength_BlankPadsToTheTargetLength()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'[' + CAST(N'ab' AS CHAR(5)) + N']'; EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("[ab   ]", script.InnerText);
    }

    [Fact]
    public void Scan_CastToCharTargetWithNoExplicitLength_DeclinesRatherThanGuessingTheDefaultLength()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CAST(N'ab' AS CHAR); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:cast-target-not-pinned", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfNewIdCastToString_TreatsNewIdAsSymbolicPlaceholder()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'DROP TABLE tbl_' + CAST(NEWID() AS NVARCHAR(36)); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfGetDate_TreatsGetDateAsSymbolicPlaceholder()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CONVERT(VARCHAR(30), GETDATE()); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfChecksumBuiltFromColumns_TreatsChecksumAsSymbolicPlaceholder()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + CAST(CHECKSUM('a', 'b') AS VARCHAR(20)); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_IifWithBothBranchesLiteral_UnionsIntoTwoAssemblies()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = IIF(@flag = 1, N'SELECT A', N'SELECT B'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["SELECT A", "SELECT B"], texts);
    }

    [Fact]
    public void Scan_SearchedCaseWithAllBranchesLiteral_UnionsAcrossEveryWhenAndElse()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = CASE " +
            "WHEN @flags & 1 = 1 THEN N'SELECT A' " +
            "WHEN @flags & 2 = 2 THEN N'SELECT B' " +
            "ELSE N'SELECT C' END; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["SELECT A", "SELECT B", "SELECT C"], texts);
    }

    [Fact]
    public void Scan_SearchedCaseWithNoElse_Declines()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CASE WHEN @flags = 1 THEN N'SELECT A' END; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:conditional", finding.Reason);
    }

    [Fact]
    public void Scan_CaseWithOneUnfoldableBranch_UnfoldableArmDegradesToTypedHole()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = CASE WHEN @flags = 1 THEN N'SELECT A' ELSE CONVERT(VARCHAR(30), SYSDATETIME()) END; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT A");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText.Contains("__silentscan_sym_", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_SearchedCaseWithProvablyFalseGuard_TakesOnlyTheElseBranch()
    {
        var result = Scan(
            "DECLARE @LastEditedByColumnName NVARCHAR(50) = N''; " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + " +
            "CASE WHEN COALESCE(@LastEditedByColumnName, N'') <> N'' THEN QUOTENAME(@LastEditedByColumnName) + N', ' ELSE N'' END + N'Col1'; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT Col1", script.InnerText);
        Assert.Equal(FindingConfidence.High, script.Confidence);
    }

    [Fact]
    public void Scan_SearchedCaseWithProvablyTrueGuard_TakesOnlyTheThenBranch()
    {
        var result = Scan(
            "DECLARE @LastEditedByColumnName NVARCHAR(50) = N'Updated'; " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + " +
            "CASE WHEN COALESCE(@LastEditedByColumnName, N'') <> N'' THEN QUOTENAME(@LastEditedByColumnName) + N', ' ELSE N'' END + N'Col1'; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT [Updated], Col1", script.InnerText);
    }

    [Fact]
    public void Scan_SearchedCaseWithUndeterminedGuard_StillUnionsBothBranches()
    {
        var result = Scan(
            "DECLARE @LastEditedByColumnName NVARCHAR(50); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + " +
            "CASE WHEN COALESCE(@LastEditedByColumnName, N'') <> N'' THEN N'X' ELSE N'Y' END; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var texts = result.AnalyzableScripts.Select(s => s.InnerText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["SELECT X", "SELECT Y"], texts);
    }

    [Fact]
    public void Scan_IifWithProvablyFalseCondition_TakesOnlyTheElseBranch()
    {
        var result = Scan(
            "DECLARE @Mode NVARCHAR(10) = N'prod'; " +
            "DECLARE @sql NVARCHAR(MAX) = IIF(@Mode = N'debug', N'SELECT DebugInfo', N'SELECT 1'); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ThreeWayIfElseIfElse_AllThreeBranchesFold_AllThreeAssembliesAnalyzed()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "DECLARE @mode INT = 0; " +
            "IF @mode = 0 BEGIN SET @sql = N'SELECT 2'; END " +
            "ELSE IF @mode = 1 BEGIN SET @sql = N'SELECT 3'; END " +
            "ELSE BEGIN SET @sql = N'SELECT 4'; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(3, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 2");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 3");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 4");
    }

    [Fact]
    public void Scan_TenIndependentOptionalFilters_CardinalityCapExceeded_CollapsesOverflowToTypedHole()
    {
        var filters = string.Concat(Enumerable.Range(0, 10)
            .Select(i => $"IF @f{i} = 1 BEGIN SET @sql = @sql + N' AND c{i} = 1'; END "));
        var declares = string.Concat(Enumerable.Range(0, 10).Select(i => $"DECLARE @f{i} BIT = 0; "));

        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE 1 = 1'; " +
            declares +
            filters +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(16, result.AnalyzableScripts.Count);
        Assert.All(result.AnalyzableScripts, s => Assert.Contains("__silentscan_sym_", s.InnerText, StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_SixIndependentOptionalFiltersWithMixedDeclaredTypes_DeclinesRatherThanSilentlyDroppingTheCallSite()
    {
        var declares = string.Concat(Enumerable.Range(0, 6).Select(i =>
            $"DECLARE @c{i} {(i % 2 == 0 ? "VARCHAR(50)" : "NVARCHAR(50)")} = N''; DECLARE @f{i} BIT = 0; "));
        var sets = string.Concat(Enumerable.Range(0, 6).Select(i =>
            $"IF @f{i} = 1 BEGIN SET @c{i} = N' AND A{i}=1'; END "));
        var concat = string.Join(" + ", Enumerable.Range(0, 6).Select(i => $"@c{i}"));

        var result = Scan(declares + sets + $"EXEC (N'SELECT 1' + {concat});");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("diverges-across-if-branches:cardinality-cap", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_IfBranchOwnFoldFails_ElseBranchFine_RecoversTheKnownBranchAsAGuardedAlternative()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "IF 1 = 1 BEGIN SET @sql = FORMAT(2, N'N'); END " +
            "ELSE BEGIN SET @sql = N'SELECT 3'; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 3", script.InnerText);
    }

    [Fact]
    public void Scan_LiteralPrefixConcatenatedOntoAGuardedAlternative_PrefixSurvivesInTheRecoveredScript()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'2'; " +
            "IF 1 = 1 BEGIN SET @sql = FORMAT(2, N'N'); END " +
            "ELSE BEGIN SET @sql = N'3'; END " +
            "EXEC(N'SELECT ' + @sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 3", script.InnerText);
    }

    [Fact]
    public void Scan_IfBranchesProduceByteIdenticalAssemblies_CollapseToOneScript()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "IF 1 = 1 BEGIN SET @sql = N'SELECT 2'; END " +
            "ELSE BEGIN SET @sql = N'SELECT 2'; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 2", script.InnerText);
    }

    [Fact]
    public void Scan_LenOfFoldedVariable_FoldsToLiteralLength()
    {
        var result = Scan(
            "DECLARE @sql VARCHAR(MAX) = 'SELECT 1'; " +
            "DECLARE @out VARCHAR(MAX) = LEFT(@sql, LEN(@sql)); " +
            "EXEC(@out);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_LenTrimsTrailingSpacesBeforeCounting()
    {
        var result = Scan(
            "DECLARE @sql VARCHAR(MAX) = 'abc  '; " +
            "DECLARE @out VARCHAR(MAX) = LEFT(@sql, LEN(@sql)); " +
            "EXEC(@out);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("abc", script.InnerText);
    }

    [Fact]
    public void Scan_LeftLengthIsLenMinusLen_StripsTrailingDelimiter()
    {
        var result = Scan(
            "DECLARE @delim VARCHAR(10) = ' AND '; " +
            "DECLARE @sql VARCHAR(MAX) = 'a = 1' + @delim + 'b = 2' + @delim; " +
            "DECLARE @out VARCHAR(MAX) = LEFT(@sql, LEN(@sql) - LEN(@delim)); " +
            "EXEC(@out);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("a = 1 AND b = 2", script.InnerText);
    }

    [Fact]
    public void Scan_LenOfNonLiteralExpression_DeclinesLeft()
    {
        var result = Scan("DECLARE @sql VARCHAR(MAX) = LEFT(N'abcdef', LEN(@undeclared)); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call-argument-diverges", finding.Reason);
    }

    [Fact]
    public void Scan_GuardedSetThenSameGuardExec_ResolvesPastNoInitializer()
    {
        var result = Scan(
            "DECLARE @sql VARCHAR(MAX); " +
            "IF @mode = 1 SET @sql = 'SELECT 1'; " +
            "IF @mode = 1 EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_GuardedSetElseIfChain_MatchingGuardExec_ResolvesEachArm()
    {
        var result = Scan(
            "DECLARE @sql VARCHAR(MAX); " +
            "IF @mode = 1 SET @sql = 'SELECT 1'; " +
            "ELSE IF @mode = 2 SET @sql = 'SELECT 2'; " +
            "IF @mode = 1 EXEC(@sql); " +
            "IF @mode = 2 EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 2");
    }

    [Fact]
    public void Scan_GuardedSetThenDifferentGuardExec_UnionsLiteralWithSymbolicPlaceholder()
    {
        var result = Scan(
            "DECLARE @sql VARCHAR(MAX); " +
            "IF @mode = 1 SET @sql = 'SELECT 1'; " +
            "IF @mode = 1 AND @extra = 1 EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        var literalScript = Assert.Single(result.AnalyzableScripts, s => s.InnerText == "SELECT 1");
        Assert.Equal(FindingConfidence.High, literalScript.Confidence);
        var symbolicScript = Assert.Single(result.AnalyzableScripts, s => s.InnerText != "SELECT 1");
        Assert.Equal(FindingConfidence.Medium, symbolicScript.Confidence);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", symbolicScript.InnerText);
    }

    [Fact]
    public void Scan_GuardedSetThenDifferentGuardExec_UnresolvableAliasType_StaysTainted()
    {
        var result = Scan(
            "DECLARE @sql dbo.SqlTextType; " +
            "IF @mode = 1 SET @sql = 'SELECT 1'; " +
            "IF @mode = 1 AND @extra = 1 EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("no-initializer", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfUpperOfSymbolicVariable_TransfersPlaceholderType()
    {
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = UPPER(@sym); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfLtrimOfSymbolicVariable_TransfersPlaceholderType()
    {
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = LTRIM(@sym); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfLeftOfSymbolicVariable_TransfersPlaceholderType()
    {
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = LEFT(@sym, 5); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfSubstringOfSymbolicVariable_TransfersPlaceholderType()
    {
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = SUBSTRING(@sym, 1, 5); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfReplaceWithSymbolicSourceArgument_TransfersPlaceholderType()
    {
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = REPLACE(@sym, 'a', 'b'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfReplaceWithSymbolicPatternArgument_StillRefuses()
    {
        var result = Scan("DECLARE @pattern NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = REPLACE('source text', @pattern, 'b'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("symbolic-value-in-function-argument", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfQuoteNameOfSymbolicVariable_TransfersPlaceholderType()
    {
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = QUOTENAME(@sym); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfCastOfSymbolicVariableToVarchar_TransfersExplicitTargetType()
    {
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql VARCHAR(MAX) = CAST(@sym AS VARCHAR(50)); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfCastOfSymbolicVariableToInt_StillRefuses()
    {
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = CAST(@sym AS INT); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:cast-target-not-pinned", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfUpperOfMixedLiteralAndSymbolicConcatenation_StillRefuses()
    {
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = UPPER('prefix' + @sym); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("symbolic-value-in-function-argument", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfNCharCrLfConcatenation_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @CrLf NVARCHAR(2) = NCHAR(13) + NCHAR(10); " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1' + @CrLf + N'WHERE 1 = 1'; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1\r\nWHERE 1 = 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfCharOutOfRange_Unanalyzable()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'x' + CHAR(256); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:char-out-of-range", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfNCharOfNonLiteralArgument_FoldsToFixedWidthTypedHole()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = NCHAR(@undeclaredCodePoint); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfIsNullOfFoldedFirstArgument_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @suffix NVARCHAR(20) = N'Active'; " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + ISNULL(@suffix, N'fallback'); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT Active", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfIsNullOfUnfoldableFirstArgument_PropagatesFirstArgumentReason()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = ISNULL(@undeclared, N'fallback'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("variable-not-in-scope", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfCoalesceOfFoldedFirstArgument_ProducesAnalyzableScript()
    {
        var result = Scan(
            "DECLARE @suffix NVARCHAR(20) = N'Active'; " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + COALESCE(@suffix, N'b', N'c'); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT Active", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfNullIf_StillRefuses()
    {
        var result = Scan(
            "DECLARE @a NVARCHAR(20) = N'x'; " +
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + NULLIF(@a, N'y'); " +
            "EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:other", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfServerPropertyCastToVarchar_FoldsToTypedHole()
    {
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.StartsWith("SELECT __silentscan_sym_", script.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_ExecOfGenuinelyUnsupportedFunction_StillReportsGenericFunctionCall()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + SOUNDEX(N'x'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecDropDatabaseBuiltFromLiteralPlusParameter_InsideIfExists_ProducesAnalyzableScript()
    {
        var result = ScanWithEmptyCallGraph("""
            CREATE PROCEDURE dbo.usp_DropDatabase @DbName SYSNAME AS
            BEGIN
                IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @DbName)
                BEGIN
                    EXEC ('DROP DATABASE ' + @DbName)
                END
            END
            """);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_SpExecuteSqlOfVariableAssignedInsideCursorLoopViaIfElseBranching_ProducesAnalyzableScripts()
    {
        var result = ScanWithEmptyCallGraph("""
            CREATE PROCEDURE dbo.usp_Scratch AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX)
                DECLARE @flag INT
                DECLARE cur CURSOR FOR SELECT flag_col FROM dbo.SomeFlags
                OPEN cur
                FETCH NEXT FROM cur INTO @flag
                WHILE (@@FETCH_STATUS = 0)
                BEGIN
                    IF @flag = 1
                        SET @sql = N'SELECT 1'
                    ELSE
                        SET @sql = N'SELECT 2'
                    EXEC sp_executesql @sql
                    FETCH NEXT FROM cur INTO @flag
                END
                CLOSE cur
                DEALLOCATE cur
            END
            """);

        Assert.Empty(result.Findings);
        Assert.NotEmpty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfStringBuiltFromCharOfLiteralCodePoint_FoldsToLiteralValue()
    {
        var result = ScanWithEmptyCallGraph("""
            CREATE PROCEDURE dbo.usp_Scratch AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1' + CHAR(13) + CHAR(10) + N'FROM dbo.T'
                EXEC(@sql)
            END
            """);

        Assert.Empty(result.Findings);
        Assert.Single(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfStrOfChecksumPlaceholder_TransfersPlaceholderTypeAsFixedLengthChar()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + STR(CHECKSUM('a', 'b'), 10); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfCastOfChecksumPlaceholderToChar_TransfersPlaceholderTypeAsFixedLengthChar()
    {
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + CAST(CHECKSUM('a', 'b') AS CHAR(10)); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfReplaceOfLiteralTemplateWithSymbolicProcParam_SplicesPlaceholderIntoTemplate()
    {
        var result = ScanWithEmptyCallGraph("""
            CREATE PROCEDURE dbo.usp_DropFn @FunctionNamePrefix SYSNAME AS
            BEGIN
                DECLARE @Tmp NVARCHAR(MAX) = REPLACE(N'IF OBJECT_ID(''$Fn$'') IS NOT NULL DROP FUNCTION dbo.$Fn$;', N'$Fn$', @FunctionNamePrefix)
                EXEC(@Tmp)
            END
            """);

        Assert.Empty(result.Findings);
        Assert.Single(result.AnalyzableScripts);
    }

    [Fact]
    public void Analyze_ExecOfDropFunctionNamedFromSymbolicProcParam_TreatsIdentifierPositionAsSafe()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_Drop @FunctionName SYSNAME AS
            BEGIN
                DECLARE @tmp NVARCHAR(MAX) = N'DROP FUNCTION ' + @FunctionName
                EXEC(@tmp)
            END
            """);

        Assert.Empty(extraction.Findings);
        Assert.Single(extraction.AnalyzableScripts);
        var finding = Assert.Single(pipeline.Findings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, finding.Outcome);
    }

    private static (DynamicSqlExtractionResult Extraction, DynamicSqlPipelineResult Pipeline) ProbePipeline(string sql)
    {
        var parseResult = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(parseResult.HasErrors, string.Join("; ", parseResult.Errors.Select(e => e.Message)));
        var extraction = DynamicSqlScannerV2.Scan(parseResult, callGraph: new ProcCallGraph([]));
        var catalog = CatalogBuilder.Build([parseResult]);
        var lineage = LineageResolver.Resolve(catalog, [parseResult]);
        var pipeline = DynamicSqlPipeline.Analyze(extraction.AnalyzableScripts, catalog, lineage);
        return (extraction, pipeline);
    }

    private static void PrintProbe(string label, DynamicSqlExtractionResult extraction, DynamicSqlPipelineResult pipeline)
    {
        System.Console.WriteLine($"=== {label} ===");
        System.Console.WriteLine("Scanner findings: " + string.Join(", ", extraction.Findings.Select(f => f.Reason)));
        System.Console.WriteLine("Scanner scripts: " + extraction.AnalyzableScripts.Count);
        System.Console.WriteLine("Pipeline findings: " + string.Join(", ", pipeline.Findings.Select(f => $"{f.Outcome}:{f.Reason}")));
        System.Console.WriteLine("Pipeline typed findings: " + pipeline.TypedFindings.Count);
    }

    [Fact]
    public void Scan_ExecOfConvertVarcharOfProcParamDateSplicedIntoLiteralTemplate_FoldsToTypedPlaceholder()
    {
        var result = ScanWithCatalog(
            "CREATE TABLE dbo.T (TripDate VARCHAR(20) NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_Range @StartDate DATETIME AS
            BEGIN
                DECLARE @SQL NVARCHAR(MAX) = N'SELECT 1 FROM dbo.T WHERE TripDate = ''' + CONVERT(VARCHAR(255), @StartDate, 126) + N''''
                EXEC(@SQL)
            END
            """);

        Assert.Empty(result.Findings);
        Assert.Single(result.AnalyzableScripts);
    }

    [Fact]
    public void Analyze_ExecOfOptionalFilterFragmentAppendedMidStatement_PartiallyAnalyzesTheKnownStructure()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE TABLE dbo.tblEvents (v_marker INT NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_Events @FilterAttention NVARCHAR(100) AS
            BEGIN
                DECLARE @sqlSelect NVARCHAR(MAX) = N'SELECT 1 FROM dbo.tblEvents v ' + @FilterAttention + N' ORDER BY v_marker DESC'
                EXEC(@sqlSelect)
            END
            """);

        Assert.Empty(extraction.Findings);
        Assert.Single(extraction.AnalyzableScripts);
        var finding = Assert.Single(pipeline.Findings);
        Assert.Equal(DynamicSqlOutcome.PartiallyAnalyzed, finding.Outcome);
        Assert.Equal("optional-fragment-elided", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfSymbolicTableNameConcatenatedIntoFromClause_FoldsToSymbolicPlaceholder()
    {
        var result = ScanWithEmptyCallGraph("""
            CREATE PROCEDURE dbo.usp_Lookup @LookupTable SYSNAME AS
            BEGIN
                DECLARE @SQL NVARCHAR(MAX) = N'SELECT 1 FROM dbo.' + @LookupTable + N' WHERE AgencyID = 1'
                EXEC(@SQL)
            END
            """);

        Assert.Empty(result.Findings);
        Assert.Single(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableSetInsideIfBranchNestedInsideAnotherIfBranch_FoldsEachLeafPathIndependently()
    {
        var result = ScanWithEmptyCallGraph("""
            CREATE PROCEDURE dbo.usp_Nested @TypeA INT, @SubType NVARCHAR(10) AS
            BEGIN
                DECLARE @SQL NVARCHAR(MAX)
                IF @TypeA = 1
                BEGIN
                    SET @SQL = N'SELECT a FROM dbo.tblA'
                    IF @SubType = N'X'
                        SET @SQL = @SQL + N' WHERE x = 1'
                    ELSE
                        SET @SQL = @SQL + N' WHERE y = 1'
                END
                ELSE
                    SET @SQL = N'SELECT b FROM dbo.tblB'
                EXEC(@SQL)
            END
            """);

        Assert.Empty(result.Findings);
        Assert.Equal(3, result.AnalyzableScripts.Count);
    }

    [Fact]
    public void Scan_ExecOfStringConcatenatedWithCaseExpressionOnProcParam_FoldsEachCaseBranch()
    {
        var result = ScanWithEmptyCallGraph("""
            CREATE PROCEDURE dbo.usp_CaseConcat @co_id INT AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Col LIKE '''
                    + CASE @co_id WHEN 8 THEN '%' ELSE '' END
                    + N'value'
                    + CASE @co_id WHEN 8 THEN '%' ELSE '' END
                    + N''''
                EXEC(@sql)
            END
            """);

        Assert.Empty(result.Findings);
        Assert.Equal(4, result.AnalyzableScripts.Count);
    }

    [Fact]
    public void Scan_ExecOfVariableSetInsideIfNestedInsideBitwiseGatedIf_FoldsToAnalyzableScripts()
    {
        var result = ScanWithEmptyCallGraph("""
            CREATE PROCEDURE dbo.usp_Bitflags @ColumnControlBits INT AS
            BEGIN
                DECLARE @UnionUser01 NVARCHAR(MAX) = N''
                IF @ColumnControlBits & 1 <> 0
                BEGIN
                    IF @ColumnControlBits & 2 <> 0
                        SET @UnionUser01 = N', User01'
                END
                DECLARE @SQL NVARCHAR(MAX) = N'SELECT 1' + @UnionUser01
                EXEC(@SQL)
            END
            """);

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
    }

    [Fact]
    public void Scan_ExecOfVariableSetAcrossThreeWayIfElseIfElseChain_FoldsAllThreeBranchesWithoutHittingCardinalityCap()
    {
        var result = ScanWithEmptyCallGraph("""
            CREATE PROCEDURE dbo.usp_Merge @mode INT AS
            BEGIN
                DECLARE @SQL NVARCHAR(MAX)
                IF @mode = 1
                    SET @SQL = N'SELECT 1'
                ELSE IF @mode = 2
                    SET @SQL = N'SELECT 2'
                ELSE
                    SET @SQL = N'SELECT 3'
                EXEC(@SQL)
            END
            """);

        Assert.Empty(result.Findings);
        Assert.Equal(3, result.AnalyzableScripts.Count);
    }

    [Fact]
    public void Scan_ExecOfSqlTextLoadedViaSubqueryFromRealTable_NoFetcher_FoldsToSymbolicPlaceholder()
    {
        var result = ScanWithCatalog(
            "CREATE TABLE dbo.tblScheduleAnalysisIssueSolutions (issue_id INT NOT NULL, solution_id INT NOT NULL, solution_sql NVARCHAR(4000) NOT NULL);",
            """
            CREATE PROCEDURE dbo.usp_LoadSql @issue_id INT, @solution_id INT AS
            BEGIN
                DECLARE @sql NVARCHAR(4000) = (SELECT solution_sql FROM dbo.tblScheduleAnalysisIssueSolutions WHERE issue_id = @issue_id AND solution_id = @solution_id)
                EXEC sp_executesql @sql
            END
            """);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Contains("__silentscan_sym_", script.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ExecOfCursorBodyEntirelyFromSymbolicVariable_ElidesToATrivialQuery()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_CursorQuery @Query NVARCHAR(MAX) AS
            BEGIN
                DECLARE @SelectQueryWithCursor NVARCHAR(MAX) = N'DECLARE AbuseCursor CURSOR FOR ' + @Query
                EXEC(@SelectQueryWithCursor)
            END
            """);

        Assert.Empty(extraction.Findings);
        Assert.Single(extraction.AnalyzableScripts);
        Assert.DoesNotContain(pipeline.Findings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
    }

    [Fact]
    public void Analyze_TwoSymbolicPlaceholdersOneLoadBearingOneOptional_TargetedElisionKeepsTheLoadBearingOne()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_BuildReport @TableNameParam SYSNAME, @HintParam NVARCHAR(50) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'CREATE TABLE ' + @TableNameParam + N' (ID INT); INSERT ' + @TableNameParam + N' (ID) SELECT ' + @HintParam + N' 1'
                EXEC(@sql)
            END
            """);

        Assert.Empty(extraction.Findings);
        Assert.Single(extraction.AnalyzableScripts);
        Assert.DoesNotContain(pipeline.Findings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
    }

    [Fact]
    public void Analyze_PlaceholderStandsForEntireMissingSearchCondition_ElidesToTautologyAndAnalyzesTheRest()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_Report @Filter NVARCHAR(200) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE ' + @Filter + N' AND 1 = 1'
                EXEC(@sql)
            END
            """);

        Assert.Empty(extraction.Findings);
        Assert.Single(extraction.AnalyzableScripts);
        Assert.DoesNotContain(pipeline.Findings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
    }

    [Fact]
    public void Probe_R3_MultiVariableConcatDirectlyInExec()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_Report AS
            BEGIN
                DECLARE @a NVARCHAR(MAX) = N'/* comment */'
                DECLARE @b NVARCHAR(MAX) = N'CREATE TABLE #T (x INT)'
                DECLARE @c NVARCHAR(MAX) = N'INSERT INTO #T VALUES (1)'
                DECLARE @d NVARCHAR(MAX) = N'SELECT x FROM #T'
                EXEC (@a + @b + @c + @d)
            END
            """);
        PrintProbe("R3 BUG-1A multi-variable concat in EXEC", extraction, pipeline);
    }

    [Fact]
    public void Probe_R3_SymbolicOrderByAppendedAfterLiteralSelect()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE TABLE dbo.T (Col1 INT NOT NULL, Col2 INT NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_Sorted @Name SYSNAME AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT Col1, Col2 FROM dbo.T'
                SET @sql = @sql + N' ORDER BY ' + @Name
                EXEC (@sql)
            END
            """);
        PrintProbe("R3 BUG-1B symbolic ORDER BY appended after literal SELECT", extraction, pipeline);
    }

    [Fact]
    public void Probe_R3_CreateFunctionNamedFromSymbolicVariable()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_BuildFn @FunctionName SYSNAME AS
            BEGIN
                DECLARE @tmp NVARCHAR(MAX) = N'CREATE FUNCTION ' + @FunctionName + N'(@TripID INT) RETURNS INT AS BEGIN RETURN 1 END'
                EXEC (@tmp)
            END
            """);
        PrintProbe("R3 BUG-1C CREATE FUNCTION named from symbolic variable", extraction, pipeline);
    }

    [Fact]
    public void Probe_R3_SpExecuteSqlArgumentItselfSymbolicConcatWithLiteralTemplate()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_Remote @ServerName SYSNAME, @DbName SYSNAME AS
            BEGIN
                DECLARE @RemoteCall NVARCHAR(MAX) = N'exec ' + @ServerName + N'.' + @DbName + N'.dbo.spDBVersionOutput @CurrentDBVersion OUTPUT'
                DECLARE @CurrentDBVersion INT
                EXEC sp_executesql @RemoteCall, N'@CurrentDBVersion INT OUTPUT', @CurrentDBVersion OUTPUT
            END
            """);
        PrintProbe("R3 BUG-1D sp_executesql arg itself symbolic concat", extraction, pipeline);
    }

    [Fact]
    public void Probe_R3_CardinalityCapFromManyOptionalAndFragments()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_Merge @Action INT, @Col1 SYSNAME = NULL, @Col2 SYSNAME = NULL, @Col3 SYSNAME = NULL, @Col4 SYSNAME = NULL, @Col5 SYSNAME = NULL, @Col6 SYSNAME = NULL AS
            BEGIN
                DECLARE @SQL NVARCHAR(MAX)
                IF @Action = 1
                    SET @SQL = N' DELETE FROM dbo.T'
                ELSE IF @Action = 2
                    SET @SQL = N' UPDATE dbo.T SET x = 1'
                ELSE
                    SET @SQL = N' UPDATE dbo.T SET x = 2'
                SET @SQL = @SQL + N' WHERE 1=1'
                    + COALESCE(N' AND ' + @Col1 + N' = 1', N'')
                    + COALESCE(N' AND ' + @Col2 + N' = 1', N'')
                    + COALESCE(N' AND ' + @Col3 + N' = 1', N'')
                    + COALESCE(N' AND ' + @Col4 + N' = 1', N'')
                    + COALESCE(N' AND ' + @Col5 + N' = 1', N'')
                    + COALESCE(N' AND ' + @Col6 + N' = 1', N'')
                EXEC (@SQL)
            END
            """);
        PrintProbe("R3 BUG-2 cardinality cap from many optional AND fragments", extraction, pipeline);
    }

    [Fact]
    public void Probe_R3_ReplaceSourceArgumentItselfSymbolic()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_Scrub @DbName SYSNAME AS
            BEGIN
                DECLARE @output NVARCHAR(MAX) = N'SELECT ' + @DbName + N'.dbo.Col1 FROM $dbname$.dbo.T'
                SET @output = REPLACE(@output, N'$dbname$', @DbName)
                EXEC (@output)
            END
            """);
        PrintProbe("R3 BUG-3 REPLACE source argument itself symbolic", extraction, pipeline);
    }

    [Fact]
    public void Probe_R3_UserDefinedScalarFunctionCallOnProcParam()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE FUNCTION dbo.ISOReturnDate(@d DATETIME) RETURNS INT AS
            BEGIN
                RETURN 20200101
            END
            GO
            CREATE PROCEDURE dbo.usp_Range @StartDate DATETIME AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE TripDate >= ' + CAST(dbo.ISOReturnDate(@StartDate) AS VARCHAR(20))
                EXEC (@sql)
            END
            """);
        PrintProbe("R3 BUG-4A user-defined scalar function call on proc param", extraction, pipeline);
    }

    [Fact]
    public void Probe_R3_SysFnVarbinToHexSubstringOnSymbolicValue()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_SetContext @value INT AS
            BEGIN
                DECLARE @before NVARCHAR(MAX) = N''
                SET @before = @before + sys.fn_varbintohexsubstring(0, CAST(@value AS VARBINARY(MAX)), 1, 0)
                DECLARE @execVar NVARCHAR(MAX) = N'SET CONTEXT_INFO ' + @before
                EXEC (@execVar)
            END
            """);
        PrintProbe("R3 BUG-4B sys.fn_varbintohexsubstring on symbolic value", extraction, pipeline);
    }

    [Fact]
    public void Probe_R3_CaseWithNonLiteralColumnElseBranchThenSubstringTrim()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE TABLE dbo.t_ConditionalOperators (id INT NOT NULL, DisplayName VARCHAR(20) NOT NULL);
            GO
            CREATE PROCEDURE dbo.usp_Predicate @OperatorID INT AS
            BEGIN
                DECLARE @predicate NVARCHAR(MAX) = N''
                SELECT @predicate = @predicate + N'AND Col ' +
                    CASE co.id
                        WHEN 1 THEN N'='
                        WHEN 2 THEN N'<>'
                        ELSE co.DisplayName
                    END
                FROM dbo.t_ConditionalOperators co WHERE co.id = @OperatorID
                SET @predicate = SUBSTRING(@predicate, 4, LEN(@predicate))
                EXEC (N'SELECT 1 ' + @predicate)
            END
            """);
        PrintProbe("R3 BUG-5 CASE with non-literal ELSE then SUBSTRING/LEN trim", extraction, pipeline);
    }

    [Fact]
    public void Scan_SelectAssignmentSelfAppendsUnresolvableAggregateFromUncatalogedTable_PreservesKnownPrefix()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_Test AS
            BEGIN
                DECLARE @StringToExecute NVARCHAR(MAX)
                SET @StringToExecute = N'INSERT INTO #BlitzResults (CheckID) SELECT 160 HAVING COUNT(DISTINCT plan_handle) > '

                IF 50 > (SELECT COUNT(*) FROM sys.databases)
                    SET @StringToExecute = @StringToExecute + N' 50 '
                ELSE
                    SELECT @StringToExecute = @StringToExecute + CAST(COUNT(*) * 2 AS NVARCHAR(50)) FROM sys.databases

                SET @StringToExecute = @StringToExecute + N' ORDER BY COUNT(DISTINCT plan_handle) DESC OPTION (RECOMPILE);'

                EXECUTE(@StringToExecute)
            END
            """);

        Assert.Empty(extraction.Findings);
        Assert.Equal(2, extraction.AnalyzableScripts.Count);
        Assert.DoesNotContain(pipeline.Findings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        Assert.Contains(extraction.AnalyzableScripts, s =>
            s.InnerText.Contains("__silentscan_sym_", StringComparison.Ordinal)
            && s.InnerText.Contains("INSERT INTO #BlitzResults (CheckID) SELECT 160 HAVING COUNT(DISTINCT plan_handle) > __silentscan_sym_", StringComparison.Ordinal)
            && s.InnerText.Contains("ORDER BY COUNT(DISTINCT plan_handle) DESC OPTION (RECOMPILE);", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_UnfoldableFragmentConcatenatedOntoALiteralWhereClause_PreservesTheSurroundingLiteralAsPartiallyAnalyzed()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_Test AS
            BEGIN
                DECLARE @Frag VARCHAR(MAX)
                DECLARE @sql VARCHAR(MAX)

                SET @Frag = FORMAT(1, N'N')
                SET @sql = 'SELECT * FROM dbo.Address a WHERE (a.ID = 1) AND ' + @Frag

                EXEC(@sql)
            END
            """);

        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Contains("SELECT * FROM dbo.Address a WHERE (a.ID = 1) AND ", script.InnerText, StringComparison.Ordinal);
        Assert.Contains("__silentscan_sym_", script.InnerText, StringComparison.Ordinal);

        var finding = Assert.Single(pipeline.Findings);
        Assert.Equal(DynamicSqlOutcome.PartiallyAnalyzed, finding.Outcome);
    }

    [Fact]
    public void Scan_SelectAssignmentSelfAppendWithUninitializedPriorValue_ConcatenatesTwoHolesSoundly()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_Test AS
            BEGIN
                DECLARE @StringToExecute NVARCHAR(MAX)
                SELECT @StringToExecute = @StringToExecute + CAST(COUNT(*) AS NVARCHAR(50)) FROM sys.databases
                EXECUTE(@StringToExecute)
            END
            """);

        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Matches(@"^(__silentscan_sym_L\d+C\d+__){2}$", script.InnerText);

        var finding = Assert.Single(pipeline.Findings);
        Assert.Equal("symbolic-value-not-positionable:whole-statement", finding.Reason);
    }

    [Fact]
    public void Scan_SelectAssignmentSelfAppendsThroughAThreeTermAdditionChain_PreservesKnownPrefix()
    {
        var (extraction, pipeline) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_Test AS
            BEGIN
                DECLARE @cols VARCHAR(2000)
                SET @cols = 'SELECT '
                SELECT @cols = @cols + ColumnName + ', TAIL' FROM sys.columns
                EXECUTE(@cols)
            END
            """);

        Assert.Empty(extraction.Findings);
        Assert.DoesNotContain(pipeline.Findings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.StartsWith("SELECT __silentscan_sym_", script.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_SelectAssignmentAppendsToADifferentVariableThanTheOneOnTheLeft_StaysOrdinaryHavoc()
    {
        var (extraction, _) = ProbePipeline("""
            CREATE PROCEDURE dbo.usp_Test AS
            BEGIN
                DECLARE @x NVARCHAR(MAX) = N'SELECT 1 WHERE 1 = '
                DECLARE @y NVARCHAR(MAX) = N'unrelated'
                SELECT @x = @y + CAST(COUNT(*) AS NVARCHAR(50)) FROM sys.databases
                EXECUTE(@x)
            END
            """);

        Assert.Empty(extraction.Findings);
        var script = Assert.Single(extraction.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }
}
