using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates;

public sealed class DynamicSqlScannerTests
{
    [Fact]
    public void Scan_SelectAssignmentFromSingleKnownTableColumn_FoldsToSymbolicPlaceholder()
    {
        // SELECT @var = expr FROM table is unconditionally tainted for a genuine reason (the
        // assigned VALUE is data- and row-order-dependent), but when the FROM clause names
        // exactly one catalog-known table and the assigned expression is a literal concatenated
        // with one of that table's own columns, the expression's STRUCTURAL SHAPE is fully known
        // even though the concrete row is not - the same "known shape, unknown value" case a
        // proc parameter with no known caller already gets. This must resolve to an analyzable
        // script, not the blanket select-assignment-not-pure taint.
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
        // T-SQL locals are scoped to the whole batch, not to the block they happen to be
        // DECLARE'd inside - a variable declared (and assigned) only inside an IF's THEN branch,
        // with no ELSE, is still perfectly legal to reference afterward: on the path that never
        // ran the THEN branch, it is simply NULL. The variable's own DECLARE is now pre-seeded
        // batch-wide (an UninitializedDeclare hole) BEFORE the IF even runs, so the join at the
        // bottom of the IF sees two genuine alternatives - the pre-seeded hole (the path that
        // skipped the assignment) and the real "SELECT 1" literal (the path that ran it) - the
        // SAME "diverges across branches, analyze each independently" mechanism this engine
        // already uses for two branches assigning two DIFFERENT real values, now correctly
        // reached here too instead of collapsing to one generic placeholder that discarded the
        // fully-known "SELECT 1" possibility.
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
        // A common dispatch pattern: @SQL is set to a DIFFERENT literal fragment in each of an
        // IF/ELSE-IF chain's branches (no final ELSE - a third, uncovered path leaves @SQL at its
        // prior, genuinely unknown state), then a common suffix is appended before one EXEC that
        // always runs. Each covered branch's own resulting SQL text is fully known even though
        // the variable as a whole never folds to one single value - this must analyze the two
        // known branches rather than declining the whole call site as an undifferentiated
        // cross-branch divergence.
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

        // The unrecognized proc call (`EXEC dbo.SomeUnknownProc @SQL OUTPUT`) no longer blanket-
        // taints @SQL: its declared type (NVARCHAR(MAX)) is known, so it degrades to a typed hole
        // instead - the same havoc-default improvement documented on DynamicSqlTransfer's
        // HavocOrTaint helper. That means the THIRD, uncovered dispatch path (neither @action='X'
        // nor 'Y') is no longer silently invisible the way it was under the old engine (where the
        // unknown proc call fully tainted @SQL, so that path could never contribute a script at
        // all) - it now correctly surfaces as its own typed-hole assembly, a genuine completeness
        // improvement, not a false positive.
        Assert.Empty(result.Findings);
        Assert.Equal(3, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText.Contains("DELETE dbo.Foo WHERE id = 1", StringComparison.Ordinal));
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText.Contains("UPDATE dbo.Foo SET col = val WHERE id = 1", StringComparison.Ordinal));
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText.Contains("__silentscan_sym_", StringComparison.Ordinal) && s.InnerText.Contains("WHERE id = 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_SubstringOfVariableWithLenOfSameVariableAsLength_TrimsLeadingLiteralPrefix()
    {
        // SUBSTRING(x, n, LEN(x)) is a common real-world idiom for stripping a fixed leading
        // literal prefix (e.g. " AND ") off a predicate string built by repeated concatenation -
        // found verbatim in production code as `SUBSTRING(@predicate, 4, LEN(@predicate))`. It
        // never actually needs @predicate's own LENGTH: SQL Server clamps a too-long `length`
        // argument to whatever remains from position `n` onward, so this is always "everything
        // from n to the end" - computable by trimming (n-1) characters off @predicate's own
        // known leading literal piece, even though @predicate still carries an unresolved hole
        // later on (an uninitialized @Name here) that makes its overall LENGTH genuinely unknown.
        // Before this existed, the LEN(@predicate) argument itself declined
        // (non-literal-expression:function-call-argument-diverges) and took the whole SUBSTRING -
        // and therefore the whole EXEC - down with it.
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
        // The leading literal piece is only 2 characters, but the idiom asks to skip 3 - trimming
        // would require slicing INTO the following hole, which is never attempted (no guessing).
        // Falls back to the ordinary per-argument decomposition: SUBSTRING's own source-type
        // passthrough no longer requires a clean Hole - @predicate itself is a MIXED literal+hole
        // template (from @Name's own unknown value), but it still carries its own DECLARE type
        // (VARCHAR(MAX)) regardless of that content being unresolved, and SUBSTRING's result type
        // depends only on the source's TYPE, never its content or the length argument's value. So
        // this now resolves to a VARCHAR(MAX) hole rather than declining outright.
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
        // SUBSTRING(x, 1, LEN(x) - K) is the mirror-image real-world idiom for stripping a fixed
        // trailing separator (e.g. a trailing ',' left by repeated `@select = @select + '...,'`
        // concatenation) - found verbatim in production code. Unlike the leading-trim idiom, this
        // one is NOT clamp-safe by itself: a too-short x would make LEN(x) - K negative, a genuine
        // SQL Server runtime error, not "everything". It only folds because the K trailing
        // characters being removed are themselves proven to exist as a literal suffix of
        // @select's own already-folded value - that proof is what makes LEN(x) - K non-negative.
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
    public void Scan_SubstringOfVariableFromOneToLenMinusConstant_TrailingLiteralShorterThanTrim_FoldsToDeclaredTypeHole()
    {
        // The trailing literal piece is only 1 character ('C'), but the idiom asks to trim 2 -
        // trimming would require reaching back INTO the preceding hole, which
        // TryTrimTrailingCharacters never attempts (no guessing: we cannot prove @select's true
        // length is >= 2 without knowing the hole's content). The exact TRIM value therefore
        // declines, but SUBSTRING's own source-type passthrough still applies (its result type
        // depends only on the source's declared type, never the length argument's value or the
        // content), so this resolves to a VARCHAR(MAX) hole rather than an unanalyzable finding.
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
        // A real corpus shape (dbo.spRIL_NTD, found via scan-db --fetch-sql-from-tables against a
        // restored production database): @select is built from SEVERAL independent optional
        // column-list appends (each its own IF, no ELSE), producing a NESTED Choice - then one
        // shared `IF LEN(@select) > 0 SET @select = SUBSTRING(@select, 1, LEN(@select) - 1)` trims
        // the trailing comma before EXEC. Before this existed, the trim's own trailing-piece walk
        // only ever looked at TOP-level Lit pieces, declining the instant it hit the Choice
        // (rather than the literal comma it was hiding) - collapsing the ENTIRE otherwise-known
        // column list into one opaque placeholder for every combination, or (after the earlier
        // GuardedAlternatives/StructurallyEqual fixes in this same area) leaving the untrimmed
        // comma to break the reparse. Trimming now recurses through the Choice's own alternatives,
        // dropping only the ones that can't prove enough length (the all-flags-false case, where
        // @select stays empty - exactly the case the source's own IF LEN(@select) > 0 guard
        // exists to route around, so it never actually reaches this SUBSTRING call for real) -
        // every genuinely reachable combination now resolves to real, parseable SQL text.
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
    }

    [Fact]
    public void Scan_SubstringTrimOfVariableAlreadyTaintedWithAGuardedAlternative_StillAnalyzesRatherThanBreakingParse()
    {
        // A real corpus shape (dbo.spRIL_FRTrips, found via scan-db --fetch-sql-from-tables
        // against a restored production database): @select is built by an EARLIER IF/ELSE (one
        // side folds, the other doesn't) before this trim ever runs, then the trim itself is
        // ALSO guarded by its own IF (`IF LEN(@select) > 0 ...`, no ELSE - the idiom's real
        // shape everywhere it's been seen). Both the trimmed (THEN) and un-trimmed (ELSE/
        // unchanged) paths reaching the trim's own join are Tainted with the SAME underlying
        // reason but DIFFERENT known alternatives - StructurallyEqual's own Tainted case used to
        // compare only Reason/Location/DeclaredType, so it treated these as identical and Join's
        // equal-shortcut (and DynamicSqlCfg's own early-continue) silently kept whichever side it
        // happened to see first, discarding the OTHER side's alternative outright. Once
        // StructurallyEqual also compares GuardedAlternatives, the two paths correctly resolve as
        // DIFFERENT, and @select's declared type lets the join recover a generic typed hole
        // instead - never the specific literal (the two alternatives, trimmed vs not, no longer
        // structurally agree on ONE text), but a real, sound, parseable script instead of the
        // OLD outcome: the untrimmed alternative's own trailing comma surviving into "SELECT
        // ColA, FROM ..." and breaking the reparse outright (symbolic-value-broke-parse /
        // InnerParseFailed on production procs shaped exactly like this).
        var result = Scan(
            "DECLARE @select VARCHAR(MAX) = ''; " +
            "IF 1 = 1 BEGIN SET @select = @select + 'ColA,'; END " +
            "ELSE BEGIN SET @select = FORMAT(2, N'N'); END " +
            "IF LEN(@select) > 0 SET @select = SUBSTRING(@select, 1, LEN(@select) - 1); " +
            "EXEC('SELECT ' + @select);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^SELECT __silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    /// <summary>A fake ILiveRowValueFetcher for unit tests - no database involved, just a fixed lookup table keyed exactly like the real fetcher's own contract, plus a call log so a test can assert it was (or wasn't) invoked.</summary>
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
        // The --fetch-sql-from-tables splice: the WHERE clause pins the row down to a single
        // literal-equality key (SettingName = 'ReportSql') and exactly one distinct value comes
        // back, so a supplied fetcher's real value replaces the RowDependentColumn hole entirely -
        // the resulting script is a genuine, fully-known literal, not a symbolic placeholder.
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

        // The CFG's fixpoint solver re-evaluates state-mutating statements across several
        // (suppressed, non-emitting) rounds before it converges - the fetcher is genuinely
        // called more than once for this one call site, not a bug (a real live implementation
        // memoizes per (table, column, keys, maxRows) to avoid the redundant round-trips this
        // implies - Core's own contract makes no call-count promise either way). Every call must
        // still carry identical, correct arguments.
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
        // No fetcher supplied (the ordinary static-only path, and every existing scan-corpus/
        // file-mode call site) - behavior is byte-for-byte the pre-existing RowDependentColumn
        // hole, never silently different depending on whether the feature COULD apply.
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
        // No WHERE clause at all no longer declines the fetch outright - every distinct value in
        // the column is a real candidate this scanner has no static way to exclude, so the
        // fetcher is called with an EMPTY key list (no filter pushed down) and every candidate it
        // returns feeds the analysis, exactly like the pinned-key case above.
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
        // More than one distinct value comes back - no longer declined as "ambiguous". Each
        // candidate becomes its own analyzable script (the SAME Choice-widening mechanism an
        // IF/ELSE branch's own divergence already uses), so neither candidate is silently lost.
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
        // A WHERE condition this pass can't push down (here: compared against a variable, not a
        // literal) is simply skipped rather than aborting the whole fetch - the filter narrows on
        // a best-effort basis only, never a reason to decline outright.
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
        // The fetcher itself returning null (its own contract: the read failed or genuinely zero
        // rows matched) must fall back to the ordinary symbolic placeholder, not propagate a
        // missing value as if it were an empty string.
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
        // The real-world dominant shape for "load dynamic SQL from a table" turned out NOT to be
        // `SELECT @var = col FROM t`, but a scalar-subquery DECLARE initializer:
        // `DECLARE @sql VARCHAR(MAX) = (SELECT col FROM t WHERE key = @param)`. Without this case,
        // --fetch-sql-from-tables would be a no-op against that shape - it would still decline as
        // non-literal-expression:sql-loaded-from-table via ExpressionEvaluator.Fold's ScalarSubquery
        // case, never even reaching the fetcher. The WHERE key here is compared against a
        // variable (not a literal), so it is not pushed down either - matching the real pattern
        // this was found against, where the filter column is a proc parameter.
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
        // Same shape as the DECLARE-initializer case above, reached via SET instead - both funnel
        // through DynamicSqlTransfer.FoldByDeclaredType, so both must resolve identically.
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
        // A cursor loop feeding EXEC from FETCH-populated variables is a common DBA/admin
        // pattern (per-table/per-column maintenance scripts). FETCH INTO overwrites its target
        // variables with a value this scanner can never know, but each target's OWN declared
        // type is a hard T-SQL guarantee regardless of which row comes back - the same
        // "known shape, unknown value" case an uninitialized DECLARE already gets. This must
        // resolve to an analyzable script (parameterized on the two unknown-but-typed targets),
        // not a blanket taint just because FETCH isn't one of the specially-modeled statements.
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
        // The overwhelmingly common real-world variant of the cursor-loop pattern above: the
        // FETCH targets are declared with an EXPLICIT `= NULL` initializer (a defensive habit,
        // not a bare `DECLARE @x TYPE` with none at all). NULL carries no more information than
        // no initializer does - the variable's declared type is still a hard T-SQL guarantee -
        // but HandleDeclare's placeholder seeding only special-cases the "Value is null" (no
        // initializer syntax at all) case; an explicit NullLiteral value instead falls through
        // to TryFoldExpression, which has no NullLiteral case, so it taints as a bare, type-less
        // "non-literal-expression:other" - and HandleFetch's own placeholder recovery then has
        // no PlaceholderType to work from, degrading the whole cursor loop to
        // "unsupported-statement-in-scope" indistinguishably from a genuinely unmodeled shape.
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
        // A common real-world pattern for a unique global temp table name:
        // REPLACE(CAST(NEWID() AS VARCHAR(36)), '-', ''). NEWID()'s VALUE is genuinely
        // unknowable at compile time (a fresh GUID every call), but its RETURN TYPE
        // (uniqueidentifier) is a hard guarantee regardless of which call produced it - the same
        // "known shape, unknown value" case an uninitialized DECLARE already gets. The whole
        // chain (NEWID -> CAST to varchar(36) -> REPLACE) already has generic placeholder-type-
        // transfer machinery for every step BUT the innermost NEWID() call itself, which today
        // refuses outright via the NonDeterministicFunctions taint - this must fold to an
        // analyzable script instead of tainting the whole call site.
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
        // T-SQL locals are batch/proc scoped, not block-scoped - a variable DECLARE'd only
        // inside a TRY block is still perfectly legal to reference inside its own CATCH block
        // (the classic "log the dynamic SQL that just failed" pattern), because ScriptDOM/SQL
        // Server allocate every local's storage for the whole batch at PARSE time regardless of
        // whether the DECLARE statement itself ever executes. HandleTryCatch's own CATCH walk
        // starts from a clone of the PRE-TRY state (correctly - CATCH only runs if TRY throws
        // mid-way, so how far TRY got is unknowable) - but a variable that never existed in that
        // pre-TRY state at all is looked up DURING the catch walk itself, before the outer
        // TryMergeFreshlyDeclaredInOneBranchOnly placeholder logic (which only fires AFTER both
        // branches finish, at the join point) ever gets a chance to run, so it still floors to
        // the generic "variable-not-in-scope" taint today.
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
        // @SQL is built before a TRY block; the TRY's own EXEC must analyze it exactly as the
        // TRY-side state has it, regardless of what the CATCH block goes on to do with the SAME
        // variable afterward - the EXEC statement itself always lives on the TRY path, so a
        // divergent CATCH-side reassignment happening AFTER it in source order is never relevant
        // to what already executed. HandleTryCatch already walks tryDict to completion (running
        // every EXEC it contains) before catchDict is even built, so this is a proof this already
        // holds, not a fix.
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
        // Tier C: a straight-line DECLARE-with-literal-initializer immediately reaching the
        // EXEC is provably constant - CLAUDE.md's dynamic SQL policy explicitly wants this
        // traced, not lumped into the unanalyzable bucket just because it's a variable.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfUndeclaredVariable_Unanalyzable()
    {
        // No DECLARE/proc-parameter for @sql anywhere in scope - genuinely unknowable.
        var result = Scan("EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("variable-not-in-scope", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromFunctionCall_Unanalyzable()
    {
        // The assignment itself isn't a bare literal/concatenation - CLAUDE.md's known hard
        // case (an ordinary function call not on the whitelisted string-builder list is not
        // reimplemented, just declined honestly). FORMAT is deliberately NOT one of the
        // whitelisted builders (unlike UPPER/LOWER/LTRIM/RTRIM/LEFT/RIGHT/SUBSTRING/QUOTENAME/
        // REPLICATE/REVERSE which now fold) - its locale/format-string-driven rendering algorithm
        // is never modeled, the same "never guess a rendering" reasoning STR's own concrete-input
        // decline uses (BuiltinRegistryTests.Str_ConcreteFloatExpr_StillDeclines) - see
        // Scan_ExecOfVariableAssignedFromUpperOnAsciiLiteral_TierC_ProducesAnalyzableScript below
        // for the whitelisted-builder behavior.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = FORMAT(1, N'N'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromColumnReference_ReasonNamesColumnReference()
    {
        // ScriptDom parses an unqualified identifier here as a ColumnReferenceExpression
        // regardless of whether a real FROM scope exists for it (that's a semantic question this
        // syntax-level scanner never answers) - exactly the shape TryFoldExpression's own
        // column-reference case exists to name.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1' + SomeColumn; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:column-reference", finding.Reason);
    }

    [Fact]
    public void Scan_ExecOfVariableAssignedFromScalarSubquery_ReasonNamesSqlLoadedFromTable()
    {
        // A subquery with its own FROM clause is the recognizable "SQL text stored in a table"
        // shape - see Scan_ExecOfSqlTextLoadedViaSubqueryFromRealTable_ReportsDistinctReason for
        // the full rationale. The generic "non-literal-expression:subquery" reason still applies
        // to a FROM-less scalar subquery (e.g. a correlated EXISTS-style expression), untested
        // here but unaffected by this split.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = (SELECT TOP 1 SomeColumn FROM dbo.T); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:sql-loaded-from-table", finding.Reason);
    }

    // CASE/IIF and CAST/CONVERT are no longer unconditional declines - see the "CASE/IIF folding"
    // and "CAST/CONVERT folding" sections below for what each now folds and what still declines
    // (and why: CASE with both branches literal now unions; CAST to a pinned VARCHAR(n)/
    // NVARCHAR(n) target now truncates - only a non-string or CHAR/NCHAR target still declines,
    // under "non-literal-expression:cast-target-not-pinned", not the old generic
    // "non-literal-expression:cast-or-convert", which no longer exists as a reason at all).

    [Fact]
    public void Scan_ExecOfVariableAssignedFromSubtraction_ReasonNamesUnsupportedOperator()
    {
        // Add is the only BinaryExpressionType folded (string concatenation) - every other
        // operator on a dynamic SQL text expression is its own, distinct, rarer shape.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT 1' + (5 - 1); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:unsupported-operator", finding.Reason);
    }

    [Fact]
    public void Scan_SetCursorVariable_TaintsRatherThanCrashes()
    {
        // SetVariableStatement.Expression is null when the RHS is modeled in a sibling
        // property instead - SET @c = CURSOR FOR ... puts the RHS in CursorDefinition. Must
        // taint @c as unsupported-assignment rather than NRE inside TryFoldExpression.
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
        // Branch-fold coverage (roadmap "trace dynamic SQL across IF/ELSE branches"): an IF's
        // THEN/ELSE are mutually exclusive, fully-determined outcomes, so when BOTH fold to a
        // constant value (here: reassigned, or left unchanged - the implicit ELSE), the real
        // value after the statement is provably one of the two - both are analyzed, not tainted.
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
        // The branch exists but never touches @sql - folding must survive it (precise, not a
        // blanket "any branch anywhere taints everything" over-approximation).
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
        // A GOTO/label used to disable folding for the WHOLE enclosing scope outright, on the
        // theory that a jump can move execution past/around assignments in ways a straight-line
        // walk can't safely reason about - true of an arbitrary jump in general, but this one
        // provably never crosses any assignment of @sql at all: the control-flow graph models
        // it as a real edge straight to the label, same value either way.
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
        // A label sitting AFTER the EXEC, referenced by a jump that itself can never reach the
        // EXEC (here, jumped to only from further still down) - the CFG's own reachability, not
        // a blanket "this scope has a label somewhere" rule, is what must decide this.
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
        // A backward jump that DOES re-enter above the EXEC, but re-assigns the exact same
        // literal on every path into it - the fixpoint must converge on that one value rather
        // than declining just because a back-edge exists at all.
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
        // A WHILE body that assigns the SAME literal every iteration used to be pre-tainted
        // unconditionally before even examining what the body actually does - HandleWhile now
        // solves its own genuine fixpoint (Header_0 = loop entry, Header_n+1 = merge(loop entry,
        // body applied to Header_n)) regardless of whether this scope needs control-flow-graph
        // mode for an unrelated GOTO/label - a loop-invariant assignment must fold to that one
        // value, not widen to taint by default just because the loop touches it.
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
        // The common accumulation pattern: SET @sql = @sql + '...' (and += equivalent).
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
        // A while body may run zero, one, or many times - this scanner never evaluates the
        // condition, so both "never entered" (@sql keeps its pre-loop value) and "ran at least
        // once" (@sql holds whatever the body's own fixpoint converges to) are genuinely
        // possible outcomes, not a reason to decline. The body assigns the SAME literal on
        // every iteration it does run, so the fixpoint converges to exactly two values, not an
        // ever-growing set.
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
        // An EXEC *inside* the loop body can still fold using state as of loop entry plus
        // whatever the body itself assigned before reaching it - valid on the first (and every
        // identical) iteration.
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
        // After the TRY/CATCH statement, exactly one of two fully-determined outcomes happened:
        // TRY ran to completion with no exception (tryDict's own value), or an exception
        // occurred and CATCH ran to completion (catchDict's value, itself built from the
        // pre-TRY baseline - see HandleTryCatch's own comment on why CATCH never starts from
        // tryDict). Both fold constant here, so both are analyzed rather than tainted.
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
        // An ordinary SELECT with no variable assignment can't mutate a local variable - must
        // not needlessly taint anything.
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
        // A statement kind this scanner doesn't explicitly model (here, INSERT) can only have
        // mutated a variable it names literally - T-SQL locals cannot alias. This INSERT never
        // mentions @sql, so @sql must survive untainted.
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
        // An unrecognized statement kind that only READS the tracked variable (here, an INSERT
        // ... VALUES supplying it as a value) cannot have written it - T-SQL locals cannot
        // alias, and INSERT has no mechanism to assign a scalar local. Mere mention is not
        // grounds to taint; only a genuine write mechanism (quirky UPDATE, FETCH INTO, RECEIVE)
        // is - see the near-miss tests below.
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
        // PRINT can only read, never write, a scalar local.
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
        // Near-miss: the legacy "quirky update" (UPDATE ... SET @v = col) is a genuine T-SQL
        // scalar-write mechanism this scanner doesn't model the VALUE of (AssignmentSetClause
        // .Variable, not a plain read) - but @sql's OWN declared type (NVARCHAR(MAX)) is still a
        // hard guarantee regardless, so this degrades to a typed hole (DynamicSqlTransfer's
        // HavocOrTaint helper) rather than a bare taint, the same as every other unmodeled-write
        // site with a resolvable declared type.
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
        // Near-miss: cursor FETCH INTO is a genuine write mechanism (FetchCursorStatement
        // .IntoVariables) this scanner doesn't model the VALUE of, but @sql's own declared type
        // is still known, so - like the quirky-update case above - it degrades to a typed hole
        // instead of a bare taint.
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
        // The quirky update names a DIFFERENT variable (@other) - @sql, never named by it, must
        // survive untainted.
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
        // The unrecognized statement mentions a DIFFERENT variable (@other) - only @other may
        // be tainted, @sql (never named by the INSERT) must survive.
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
        // SELECT @x = expr [, @y = expr2] with no FROM clause is a pure variable assignment,
        // just like SET - the other common way T-SQL builds up dynamic SQL text.
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
        // SELECT @x = Col FROM T is data/row-order dependent - genuinely unknowable, not the
        // pure-assignment shape. @sql's own declared type is still known, though, so this
        // degrades to a typed hole instead of a bare taint, same as every other
        // "select-assignment-not-pure" site.
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
        // A SELECT that assigns @sql alongside a real projected column is still not the pure-
        // assignment shape - but @sql's own declared type is known, so this degrades to a typed
        // hole instead of a bare taint, same as every other "select-assignment-not-pure" site.
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
        // No initializer at all, but the declared type (NVARCHAR(MAX)) resolves with no catalog
        // needed - folds to a symbolic placeholder rather than a bare taint, at the SCANNER
        // level. The pipeline's own position classifier (not exercised by this scanner-only
        // helper) is what actually refuses this particular shape - the whole EXEC argument is
        // nothing but the one placeholder - see DynamicSqlPipeline's own tests for that.
        var result = Scan("DECLARE @sql NVARCHAR(MAX); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfVariableWithNoInitializer_UnresolvableAliasType_StaysTainted()
    {
        // Same shape, but the declared type is a CREATE TYPE ... FROM alias, unresolvable
        // without a catalog at this point in the pipeline - falls back to the honest taint
        // reason rather than a placeholder claiming a type this scanner couldn't determine.
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
        // T-SQL's own DECLARE is a compile-time, BATCH-scoped construct - a variable declared
        // inside only ONE branch of an IF/ELSE IF chain is still perfectly legal to reference
        // from a SIBLING branch that never runs that DECLARE (simply NULL there), even though
        // this branch's own path through the CFG never visits the node that declares it. Real
        // corpus shape (found auditing a real production database: several large IF/ELSE-IF-
        // chain-shaped report procs, each branch declaring its own working variables) - the
        // sibling branch's reference must resolve to a typed hole, not the misleading
        // "variable-not-in-scope" a genuinely undeclared-anywhere variable gets (that label
        // implies a real T-SQL compile error, which this is not).
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
        // The left side of the SET's own `+` fails to fold - a different code path than the
        // right side failing.
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
        // CreateFunctionStatement bodies (multi-statement TVFs, scalar functions) get their
        // own fresh variable scope, same as CREATE PROCEDURE.
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
        // Regression: real-world corpus code (e.g. Brent Ozar's First Responder Kit) commonly
        // uses "stub CREATE PROCEDURE ... AS RETURN 0" followed by ALTER PROCEDURE for the
        // real body - matching only CreateProcedureStatement would silently never walk into
        // the ALTER'd body at all (not even reporting it Unanalyzable - just never visited).
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
        // A body-less proc declaration (e.g. a CLR proc's EXTERNAL NAME body) has no
        // StatementList - real-world corpus code (First Responder Kit) hits this.
        var result = Scan("CREATE PROCEDURE dbo.usp_Test AS EXTERNAL NAME Assembly.Class.Method;");

        Assert.Empty(result.Findings);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_InlineTableValuedFunction_NoStatementListBody_DoesNotThrow()
    {
        // An inline TVF has no StatementList (its body is a single RETURN expression) - must
        // be handled without throwing, even though it can't contain dynamic SQL.
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
        // Only Equals/AddEquals are meaningful for string values; other compound operators are
        // not modeled - but @sql's own declared type is known, so this degrades to a typed hole
        // rather than guessing at (or fully discarding) the assigned value.
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
        // Tier C: a bare literal concatenated with a locally-folded variable is provably
        // constant end to end.
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
        // EXEC dbo.usp_DoThing is a normal proc call, not dynamic SQL - must not fire.
        var result = Scan("EXEC dbo.usp_DoThing;");

        Assert.Empty(result.Findings);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfVariableMutatedByPriorProcCallWithOutput_FoldsToTypedHole()
    {
        // The P0 fix: `EXEC dbo.BuildQuery @sql OUTPUT` can mutate @sql through a mechanism
        // this scanner has no visibility into. Before that fix, an unrecognized ExecuteEntity
        // (any ordinary procedure call) fell through HandleExecute doing nothing at all, so the
        // later EXEC(@sql) folded the STALE pre-call literal and reported AnalyzedLiteral for
        // SQL that never actually ran. @sql's own declared type is known, though, so the P0 fix's
        // "unsupported-execute-form" outcome now degrades to a typed hole instead of a bare taint,
        // same as every other unmodeled-write site with a resolvable declared type.
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
        // The unrecognized proc call only mutates @other (named as its OUTPUT argument) -
        // @sql, never mentioned by that call, must survive untainted.
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
        // `EXEC @rc = dbo.BuildQuery @sql` taints @rc (the return-status target) - but @sql is
        // passed WITHOUT the OUTPUT keyword, a genuine T-SQL call-by-VALUE the callee cannot
        // write back through no matter what it does internally, so @sql itself is never touched
        // and keeps its own already-known literal value.
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
        // The genuine OUTPUT case, unlike the by-value one above: @sql IS marked OUTPUT, a real
        // T-SQL call-by-reference the callee CAN write through - @sql's own declared type is
        // known, so it degrades to a typed hole rather than keeping its stale prior value.
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
        // The loop body itself reassigns @sql AFTER the EXEC reads it, in program order,
        // appending more text each time it runs - a genuine unbounded accumulator with no fixed
        // iteration count this scanner evaluates, so the possible-value set grows every round
        // and is guaranteed to exceed the cardinality cap. DynamicSqlCfg.FindUnboundedAccumulators
        // detects this structurally (a variable both self-referentially reassigned and EXEC'd
        // within the SAME loop body) and forces it Tainted for the whole loop, rather than
        // letting the general Join/Widen fixpoint either race to MaxFixpointRounds or
        // accidentally stabilize on an arbitrary under-cap intermediate snapshot (the old V2 bug:
        // duplicated AnalyzableScripts up to ~18-19 repeats of the appended text, none of them a
        // real bound on the loop's actual behavior).
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
        // `SELECT @x = ... WHERE <cond>` assigns zero or one time depending on the WHERE -
        // unlike a FROM-less unconditional assignment, this is not certain to run at all, so it
        // is not the pure-assignment shape - but @sql's own declared type is known, so it
        // degrades to a typed hole rather than fold as if the assignment always executes.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX); " +
            "DECLARE @flag BIT = 1; " +
            "SELECT @sql = N'SELECT 1' WHERE @flag = 1; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    // ------------------------------------------------------------------
    // Cross-call-edge seeding (roadmap "trace provably-constant dynamic SQL across proc-call
    // edges") - a proc's OWN parameter, folded into dynamic SQL built inside its OWN body, using
    // a literal this scan saw a CALLER pass at a call site the ProcCallGraph recorded. The graph
    // is hand-built here rather than via ProcCallGraphBuilder (which needs a real
    // DatabaseCatalog) - these tests exercise DynamicSqlScannerV2's own seeding logic in
    // isolation; ScanReportBuilder wiring the two together end-to-end belongs in a pipeline test.
    // ------------------------------------------------------------------

    private const string CalleeProcName = "dbo.usp_RunLookup";

    private static ProcCallGraph SingleCallerGraph(ProcCallArgument argument) =>
        new([new ProcCallEdge(null, CalleeProcName, new SourceSpan("caller.sql", 10, 5), [argument])]);

    private static DynamicSqlExtractionResult ScanWithCallGraph(string sql, ProcCallGraph callGraph)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DynamicSqlScannerV2.Scan(result, callGraph: callGraph);
    }

    [Fact]
    public void Scan_ProcParamSeededFromSingleCallerLiteral_ProducesAnalyzableScript()
    {
        var literal = new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2);
        var graph = SingleCallerGraph(new ProcCallArgument("@Status", FormalParameterType: null, FormalParameterIsOutput: false, CallerVariableName: null, IsLiteral: true, literal));

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1 WHERE Status = 'Active'", script.InnerText);
    }

    [Fact]
    public void Scan_ProcParamWithMultipleCallersPassingSameLiteral_ProducesAnalyzableScript()
    {
        // Value-seeding across proc-call edges (roadmap "trace provably-constant dynamic SQL
        // across proc-call edges", extended beyond a single caller): every known caller supplies
        // a literal for this parameter, so its runtime value is provably one of them - here both
        // callers happen to agree, so the assembly set collapses to one script.
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
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 1 WHERE Status = 'Active'", script.InnerText);
    }

    [Fact]
    public void Scan_ProcParamWithMultipleCallersPassingDifferentLiterals_BothAssembliesAnalyzed()
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
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1 WHERE Status = 'Active'");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT 1 WHERE Status = 'Archived'");
    }

    [Fact]
    public void Scan_ProcParamWithMultipleCallers_OneCallerNonLiteral_FoldsToSymbolicPlaceholder()
    {
        // Value-seeding requires EVERY known caller to supply a literal - a single non-literal
        // caller means the parameter's true value set genuinely includes something this scan
        // can't pin down, so the OTHER callers' literals are discarded rather than partially
        // seeding just from them. With a resolvable declared type (NVARCHAR(20)), the whole
        // parameter folds to a symbolic placeholder instead - the SAME treatment a single unseeded
        // caller gets - rather than a bare taint, since T-SQL's own type contract for this proc
        // still guarantees the runtime value really is this type.
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
        // 40 distinct callers, each passing its own distinct literal - over the 32-assembly cap.
        // @Status has a resolvable declared type (NVARCHAR(20)), so SqlTextValue.Widen collapses
        // the OVERFLOWING portion of the choice into one typed hole instead of tainting the whole
        // parameter - the same cap-then-degrade policy as every other over-cap divergence, rather
        // than discarding literals the fold already proved just because MORE of them exist than
        // the cap allows. The exact split below (33 callers collapsed into the hole, the
        // remaining 7 surviving as their own literal alternatives) is deterministic from the cap
        // and the callers' declaration order, not an arbitrary/incidental count.
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

    // ------------------------------------------------------------------
    // OUTPUT-parameter tracking through the call graph: an ordinary `EXEC dbo.Helper @out =
    // @var OUTPUT` seeds the CALLER's own @var from a prior scan's summary of what dbo.Helper's
    // body always assigns its OUTPUT parameter, instead of blanket-tainting @var.
    // ------------------------------------------------------------------

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
    public void Scan_ExecWithOutputArgumentButNoKnownSummary_FoldsToTypedHole()
    {
        // The call graph resolved the target, but no summary exists for its OUTPUT parameter
        // (its own body couldn't prove @out constant, or the callee was never scanned at all).
        // @select's own declared type is known, though, so this degrades to a typed hole spliced
        // into the surrounding concatenation, rather than tainting the whole EXEC argument.
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
        // A second variable this same EXEC references (the return-value var, @rc) is NOT an
        // OUTPUT argument with a known summary - only the OUTPUT binding this scan actually
        // proved (@select) gets seeded from the summary. @rc's own declared type (INT) is still
        // known, though, so - like every other unmodeled-write site with a resolvable declared
        // type - it degrades to a typed hole through CAST(@rc AS varchar(10)) rather than
        // tainting the whole EXEC argument.
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
        // One call site, but the actual argument was a variable, not a literal - nothing this
        // scan can trace back to a concrete value. With a resolvable declared type (NVARCHAR(20)),
        // this folds to a symbolic placeholder rather than a bare taint.
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
        // Same shape, but the declared type is a CREATE TYPE ... FROM alias, unresolvable without
        // a catalog at this point in the pipeline - falls back to the honest taint reason rather
        // than a placeholder claiming a type this scanner couldn't actually determine.
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
        // Zero edges for this callee (application code, an unparsed caller, a synonym this scan
        // didn't resolve) - the parameter IS declared, with a resolvable declared type
        // (NVARCHAR(20)), so its runtime value is seeded as a symbolic placeholder rather than
        // tainted: T-SQL's own type contract for this proc guarantees the value really is this
        // type, even though this scan has no known caller to learn the VALUE from - enough to
        // fold the surrounding concatenation into one analyzable (Medium-confidence) script.
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
        // A CREATE TYPE ... FROM alias can't resolve without a catalog, and DynamicSqlScannerV2
        // runs before CatalogBuilder - so this falls back to the same honest, specific taint the
        // scanner reported before symbolic placeholders existed, never a guessed type.
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
    public void Scan_OutputParamNeverSeededEvenWithSingleLiteralLookingCaller()
    {
        // FormalParameterIsOutput true means the argument flows callee-to-caller, never the
        // other direction - seeding it from anything would be backwards, so it must stay
        // unseeded (falls back to "variable-not-in-scope" exactly like a genuinely unknown one).
        var literal = new ProcCallLiteralArgument("Active", "caller.sql", 10, 30, PrefixLength: 2);
        var argument = new ProcCallArgument("@Status", null, FormalParameterIsOutput: true, null, true, literal);
        var graph = SingleCallerGraph(argument);

        var result = ScanWithCallGraph(
            $"CREATE PROCEDURE {CalleeProcName} @Status NVARCHAR(20) OUTPUT AS " +
            "BEGIN DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 WHERE Status = ''' + @Status + N''''; EXEC(@sql); END",
            graph);

        Assert.Empty(result.AnalyzableScripts);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("variable-not-in-scope", finding.Reason);
    }

    // ------------------------------------------------------------------
    // QUOTENAME folding (roadmap "fold high-volume string-builder functions in dynamic SQL,
    // oracle-checked") - every expected string below was verified directly against a live Docker
    // SQL Server instance, not assumed from documentation.
    // ------------------------------------------------------------------

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
        // The realistic pattern this fold exists for: a variable that already folded constant
        // via straight-line DECLARE tracing, THEN wrapped in QUOTENAME.
        var result = Scan(
            "DECLARE @table VARCHAR(50) = 'Orders'; " +
            "DECLARE @sql VARCHAR(MAX) = 'SELECT * FROM ' + QUOTENAME(@table); " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT * FROM [Orders]", script.InnerText);
    }

    // Real T-SQL's EXEC('...') string-list form only accepts a char literal, local variable, or
    // +-concatenation of those (ScriptDom rejects a bare function call there directly, verified:
    // "EXEC(QUOTENAME(...))" is itself a syntax error) - so every QUOTENAME scenario below routes
    // through a DECLARE assignment first, exactly like real dynamic SQL code has to.
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
        // Only the CLOSING delimiter character is ever escaped - oracle-verified: QUOTENAME('ab[c')
        // returns "[ab[c]", not "[ab[[c]".
        var result = ScanQuoteName("QUOTENAME(N'ab[c')");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("[ab[c]", script.InnerText);
    }

    /// <summary>T-SQL source-text escaping (doubling an embedded single quote) for embedding an arbitrary string as a literal in a TEST's own generated SQL - unrelated to QUOTENAME's own escaping, which happens inside the engine once parsed.</summary>
    private static string AsSqlStringLiteral(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [Theory]
    // inputWithEmbeddedCloseChar always contains the family's own close character so every case
    // actually exercises the doubling QUOTENAME itself performs, not just the wrap.
    [InlineData("'", "ab'c", "'ab''c'")]
    [InlineData("\"", "ab\"c", "\"ab\"\"c\"")]
    [InlineData("(", "ab)c", "(ab))c)")] // only ')' is the escaped close char for the paren family, doubled
    [InlineData("<", "ab>c", "<ab>>c>")]
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
        // Oracle-verified: QUOTENAME('abc', 'x') returns SQL NULL for real (not brackets, not an
        // error) - concatenating NULL propagates NULL through the whole @sql build, which this
        // scanner has no representation for, so it must fail the fold rather than guess.
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
        // Oracle-verified boundary: QUOTENAME on a 128-character input still returns a real
        // value; 129 characters returns SQL NULL.
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
        // The argument itself can't fold to a concrete value, but QUOTENAME's return type is
        // nvarchar(258) unconditionally - a hard T-SQL guarantee regardless of the argument's
        // own value - so this is "known shape, unknown value" like any other typed hole, not a
        // taint. This is a genuine improvement over the old engine, which discarded the known
        // return type the moment any argument was unresolved.
        var result = ScanQuoteName("QUOTENAME(SomeColumn)");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Contains("__silentscan_sym_", script.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_QuoteNameWithThreeArguments_UnanalyzableAsFunctionCall()
    {
        // Not a real QUOTENAME overload - ScriptDom still parses it as a FunctionCall, and this
        // scanner declines rather than guessing which two of the three arguments matter.
        var result = ScanQuoteName("QUOTENAME(N'a', N'[', N'extra')");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
    }

    // ------------------------------------------------------------------
    // Environment-name builtins (DB_NAME/USER_NAME/SUSER_SNAME/SUSER_NAME/APP_NAME/HOST_NAME/
    // SCHEMA_NAME/ORIGINAL_LOGIN) - a real corpus pattern (audit-trail messages, schema-qualified
    // USE statements) previously declined outright as an unrecognized function name; each now
    // resolves to a typed hole since its return type (oracle-verified nvarchar(128), nvarchar(4000)
    // for ORIGINAL_LOGIN) is a hard T-SQL guarantee independent of the caller's own arguments.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_DbNameConcatenatedIntoDynamicSql_FoldsToTypedHole()
    {
        // Real corpus shape: a table name qualified by DB_NAME() so a script works unmodified
        // against whichever database it's deployed to. DB_NAME()'s VALUE can never be known from
        // source text alone, but its return type (nvarchar(128), oracle-verified) is a hard
        // guarantee - so this resolves to a typed hole instead of declining outright.
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

    // ------------------------------------------------------------------
    // User-defined scalar function fallback - a call to a function this scanner has no builtin
    // spec for is not automatically unanalyzable when the catalog already knows its RETURNS type
    // from the real CREATE/ALTER FUNCTION DDL (CLAUDE.md: catalog truth comes from the engine's
    // own DDL, never a guess) - the function's own body is never inspected or evaluated, only its
    // declared signature, matching how NEWID()/SERVERPROPERTY() already degrade to a typed hole
    // instead of declining outright.
    // ------------------------------------------------------------------

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
        // A scalar UDF call with no explicit schema prefix - ScriptDom's own FunctionCall.CallTarget
        // is absent (unlike "dbo.udf_X(...)"), so resolution must fall back to the same dbo default
        // SchemaObjectNameHelper.QualifyFunctionCall applies, exactly matching how the catalog itself
        // qualified the CREATE FUNCTION statement that defined it.
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
        // No CREATE FUNCTION for dbo.udf_NeverDefined anywhere - the catalog has no return type to
        // offer, so this must decline exactly as it did before the UDF fallback existed, never a
        // guess at some assumed type.
        var result = ScanWithCatalog(
            "CREATE TABLE dbo.Unrelated (Id INT NOT NULL);",
            "DECLARE @sql NVARCHAR(MAX) = dbo.udf_NeverDefined(@SomeParam); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
    }

    [Fact]
    public void Scan_UnmodeledScalarUdfWithNoCatalogSupplied_DeclinesRatherThanGuessing()
    {
        // Scan() (no catalog argument) mirrors real callers that haven't wired the catalog through
        // yet - the UDF fallback must never assume a type when there is no catalog to consult.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = dbo.udf_FormatCode(@SomeParam); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
    }

    // ------------------------------------------------------------------
    // Whitelisted string-builder folding (roadmap "fold high-volume string-builder functions in
    // dynamic SQL, oracle-checked") - every expected string and every decline below was verified
    // directly against a live Docker SQL Server instance, not assumed from documentation.
    // ------------------------------------------------------------------

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
    [InlineData("UPPER(N'select id')")] // contains 'i'
    [InlineData("UPPER(N'SELECT Id')")] // contains 'I'
    [InlineData("LOWER(N'select ID')")]
    public void Scan_CaseConversionOnInputContainingI_Declines_TurkishCollationAmbiguity(string expression)
    {
        // Oracle-verified: UPPER('i' COLLATE Turkish_CI_AS) is 'İ', not 'I' as under every other
        // collation family - the one ASCII letter pair whose case mapping genuinely depends on
        // collation. This scanner has no collation context at all, so it declines rather than
        // guessing which mapping the real target database uses.
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
        // Oracle-verified: LTRIM/RTRIM trim ONLY the space character (0x20) - a leading tab is
        // left untouched, unlike .NET's parameterless TrimStart(), which strips all whitespace.
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
        // Oracle-verified: LEFT('abc', 10) returns 'abc' - no padding, no error.
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
        // Oracle-verified: LEFT with a negative length raises Msg 536 rather than returning
        // anything - the real EXEC would never reach this text on that path.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = LEFT(N'abc', -1); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:negative-length", finding.Reason);
    }

    [Fact]
    public void Scan_LeftWithLengthCarriedInIntVariable_TierC_ProducesAnalyzableScript()
    {
        // An INTEGER-family DECLARE now folds its own literal initializer (FoldByDeclaredType),
        // so a length carried in a plain int variable resolves through FoldInteger's own
        // (newly added) variable-reference case, not declined the way it used to be.
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
        // Oracle-verified: SUBSTRING('abcdef', 2, 100) returns 'bcdef' - clamped, not an error.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', 2, 100); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("bcdef", script.InnerText);
    }

    [Fact]
    public void Scan_SubstringStartBeyondInput_FoldsToEmptyString()
    {
        // Oracle-verified: SUBSTRING('abcdef', 10, 5) returns an empty string, not an error.
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
        // Real, defined T-SQL behavior (oracle-verified: the window still clips against the
        // string's bounds), but rare enough outside adversarial input that this scanner declines
        // rather than adding the extra below-1 clipping arithmetic.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', -2, 5); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:substring-start-below-one", finding.Reason);
    }

    [Fact]
    public void Scan_SubstringWithStartCarriedInIntVariable_TierC_ProducesAnalyzableScript()
    {
        // Same widening as LEFT above - a SUBSTRING start position carried in a plain int
        // variable now resolves instead of declining.
        var result = Scan("DECLARE @n INT = 2; DECLARE @sql NVARCHAR(MAX) = SUBSTRING(N'abcdef', @n, 3); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("bcd", script.InnerText);
    }

    [Fact]
    public void Scan_LeftOnFoldedVariable_TierC_ProducesAnalyzableScript()
    {
        // The realistic pattern this fold exists for: a variable that already folded constant
        // via straight-line DECLARE tracing, THEN wrapped in a whitelisted builder.
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
        // The specific correctness hazard the FoldByDeclaredType/FoldInteger split guards
        // against: `@j = @i + 1` must arithmetically add (5 + 1 = 6), never fall through to
        // Fold's general (string-concatenation-flavored) BinaryExpression-Add handling, which
        // would otherwise silently produce "51".
        var result = Scan(
            "DECLARE @i INT = 5; DECLARE @j INT = @i + 1; " +
            "DECLARE @sql NVARCHAR(MAX) = LEFT(N'abcdefg', @j); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("abcdef", script.InnerText);
    }

    [Fact]
    public void Scan_IntVariableAddEquals_ResolvesArithmeticSum_NotStringConcat()
    {
        // `@i += 1` on an INTEGER-family variable is arithmetic, not the ordinary +=
        // text-concatenation path every other declared type still uses.
        var result = Scan(
            "DECLARE @i INT = 5; SET @i += 2; " +
            "DECLARE @sql NVARCHAR(MAX) = LEFT(N'abcdefg', @i); EXEC(@sql);");

        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("abcdefg", script.InnerText);
    }

    [Fact]
    public void Scan_IntVariableFromUnfoldableExpression_StillDeclines_NotAGuess()
    {
        // A length carried through an expression FoldInteger genuinely can't evaluate (a column
        // reference) still declines - never a guess. LEFT/RIGHT's HoleTransfer only needs to
        // PROVE the length negative to decline on the length itself; an unprovable length falls
        // through toward the source argument, but still prefers the LENGTH's own unresolved
        // reason over the generic fallback - the source here is concrete literal text (nothing to
        // pass a type through from either), so the length's own reason surfaces, exactly as
        // before this widening.
        var result = Scan(
            "CREATE TABLE dbo.T (N INT NOT NULL); " +
            "DECLARE @n INT; SELECT @n = N FROM dbo.T; " +
            "DECLARE @sql NVARCHAR(MAX) = LEFT(N'abcdef', @n); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call-argument-diverges", finding.Reason);
    }

    // ------------------------------------------------------------------
    // REPLACE folding: agree-under-both-comparisons or decline. Every expected string and every
    // decline below was verified directly against a live Docker SQL Server instance.
    // ------------------------------------------------------------------

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
        // Oracle-verified: REPLACE('AbcABC','abc','X') is 'AbcABC' unchanged under an ordinal/
        // case-sensitive comparison (no exact "abc" substring present) but 'XX' under a
        // case-insensitive one - exactly the collation-dependent divergence this scanner has no
        // way to resolve without knowing the real target collation.
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
        // The WHERE-clause accumulator shape: @sql already carries two possible values from an
        // earlier optional-filter IF, THEN gets REPLACE'd before the EXEC -
        // ExpressionEvaluator.TryFoldCrossProduct folds REPLACE once per alternative in @sql's
        // own Choice and re-unions the results, instead of ToBuiltinArgument collapsing the
        // whole argument straight to a generic "symbolic-value-in-function-argument" decline the
        // moment it sees more than one possible input.
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
        // One of the two possible @sql values hits the ordinal-vs-case-insensitive divergence
        // REPLACE always declines for - since a Choice means "one of these really happens",
        // TryFoldCrossProduct taints the WHOLE fold the moment any one alternative's own REPLACE
        // taints, rather than silently dropping just that branch from the union.
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
        // Real corpus pattern (SQL-Server-First-Responder-Kit's sp_DatabaseRestore.sql):
        // REPLACE(text, N'''', REPLICATE(N'''', 4)) quadruples an embedded single quote when
        // splicing a literal path into dynamic SQL. REPLICATE was entirely unmodeled, so this
        // whole REPLACE call used to decline with "symbolic-value-in-function-argument" the
        // moment it saw an unrecognized function name for its own replacement argument.
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
        // Real corpus pattern (SQL-Server-First-Responder-Kit's sp_DatabaseRestore.sql):
        // @FileListParamSQL is built as `SET @x = N'INSERT INTO t (a, b';` then diverges across
        // an IF with no ELSE (`IF @v >= 13 SET @x += N', SnapshotUrl';` - a genuine 2-alternative
        // Choice, both purely literal), THEN gets MORE straight-line concatenation appended
        // (`SET @x += N')' + ...;`) - Concat deliberately never distributes over a Choice, so the
        // Choice ends up as ONE piece among several literal ones by the time @x reaches
        // REPLACE(@x, ...). Both ToBuiltinArgument (unchanged - correctly still declines a bare
        // "mix of literal and Choice pieces" it can't itself disentangle) and the OLD
        // TryFoldCrossProduct (which only recognized a Choice as an argument's SOLE piece) missed
        // this shape entirely, declining the whole REPLACE with the generic
        // "symbolic-value-in-function-argument". Each alternative is now spliced back into its
        // own original surrounding literal text before folding REPLACE once per alternative.
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
        // Real corpus pattern (SQL-Server-First-Responder-Kit's sp_BlitzIndex.sql and others):
        // several sequential SET @sql = REPLACE(@sql, '@@@Marker@@@', @CallerValue) calls, each
        // substituting one placeholder marker for a proc parameter this scanner can't prove
        // constant. Once the FIRST REPLACE splices a hole in, @sql is no longer pure Text nor a
        // single Hole - it MIXES literal text with an already-opaque hole - a shape
        // ToBuiltinArgument correctly declines (it has no per-piece splicing notion), so every
        // REPLACE call downstream of the first used to decline too, cascading into
        // "symbolic-value-in-function-argument" for the whole chain.
        var result = Scan(
            "DECLARE @DatabaseName NVARCHAR(128); " + // unresolved proc parameter -> typed Hole
            "DECLARE @SchemaName NVARCHAR(128); " +
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
        // The per-literal-segment splicing still runs REPLACE's own collation-sensitivity check
        // (unchanged, reused verbatim from BuiltinRegistry) on each literal segment independently -
        // a segment that hits the ordinal-vs-case-insensitive divergence still declines with the
        // specific reason, never silently dropped from the chain.
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
        // A source Template can accumulate BOTH an earlier REPLACE's own hole-splice AND a real
        // IF-branch divergence (a genuine corpus shape, sp_BlitzIndex.sql) - the embedded Choice's
        // own branching structure survives the splice UNCHANGED (each of its alternatives gets
        // the same per-Lit-segment REPLACE applied independently, never cross-producted here),
        // and the ordinary EXEC-time Expand still enumerates it into one script per alternative,
        // exactly as if REPLACE had never touched it.
        var result = Scan(
            "DECLARE @TableName NVARCHAR(128); " + // unresolved -> typed Hole
            "DECLARE @sql NVARCHAR(MAX) = N'CREATE TABLE @@@Table@@@ (a INT'; " +
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
        // A real corpus shape (multiple spRIL_* procs, found via scan-db --fetch-sql-from-tables
        // against a restored production database): a shared @sql variable is assembled from
        // SEVERAL independently-guarded concatenated pieces (each its own optional IF-append,
        // contributing its own Choice), THEN one REPLACE(@sql, '$dbname$', @DB_Name) call
        // substitutes a routing token before EXEC. Before this existed, TWO OR MORE Choice
        // pieces in the source made the whole REPLACE decline outright (the old
        // TryFoldCrossProduct-style restriction), collapsing the entire otherwise-known @sql
        // text into one opaque placeholder. FoldReplaceOverPiecesPreservingChoices never needs to
        // cross-product independent choices at all - it just splices every literal leaf while
        // keeping each Choice's own branching shape intact - so this analyzes fully now.
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

    // ------------------------------------------------------------------
    // CAST/CONVERT folding onto a VARCHAR(n)/NVARCHAR(n) target only - every non-string target
    // and CHAR/NCHAR's blank-padding declines rather than guessing a rendering.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_CastOfFoldedVariableToNVarcharWithTruncation_TierC_ProducesAnalyzableScript()
    {
        // Oracle-verified: CAST(N'HelloWorld' AS NVARCHAR(5)) silently truncates to 'Hello',
        // no error - the shape #5 in the audit ("caller passes a query string cast to nvarchar
        // before sp_executesql").
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
        // Oracle-verified: CAST('ab' AS CHAR(5)) is 'ab   ' (blank-padded to exactly 5) - a
        // different rendering from VARCHAR(n)'s plain truncation, but a fully deterministic,
        // length-driven one this scanner now folds rather than declines.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'[' + CAST(N'ab' AS CHAR(5)) + N']'; EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("[ab   ]", script.InnerText);
    }

    [Fact]
    public void Scan_CastToCharTargetWithNoExplicitLength_DeclinesRatherThanGuessingTheDefaultLength()
    {
        // A bare CAST(x AS CHAR) with no explicit length uses T-SQL's own default length (30),
        // which this scanner's type resolver does not independently pin - declines rather than
        // guessing that default, distinct from the explicit-length case above which now folds.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CAST(N'ab' AS CHAR); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:cast-target-not-pinned", finding.Reason);
    }

    // ------------------------------------------------------------------
    // Non-deterministic builtins - genuinely unknowable at compile time, reported with their own
    // reason distinct from an ordinary unimplemented function call.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_ExecOfNewIdCastToString_TreatsNewIdAsSymbolicPlaceholder()
    {
        // Superseded by Scan_ExecOfTableNameBuiltFromNewId_TreatsNewIdAsSymbolicPlaceholder's own
        // fix: NEWID()'s return TYPE (uniqueidentifier) is a hard guarantee even though its VALUE
        // isn't, so this now folds to a placeholder rather than declining outright.
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
        // Superseded by Scan_ExecOfTableNameBuiltFromNewId_TreatsNewIdAsSymbolicPlaceholder's own
        // reasoning, extended to GETDATE(): its return type (datetime) is a hard guarantee even
        // though its VALUE isn't, so this now folds to a placeholder rather than declining.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CONVERT(VARCHAR(30), GETDATE()); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfChecksumBuiltFromColumns_TreatsChecksumAsSymbolicPlaceholder()
    {
        // CHECKSUM/BINARY_CHECKSUM are variadic (one or more arguments), unlike NEWID's own
        // zero-argument shape - proves the placeholder case isn't accidentally scoped to
        // zero-parameter calls only.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + CAST(CHECKSUM('a', 'b') AS VARCHAR(20)); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    // ------------------------------------------------------------------
    // CASE/IIF folding by unioning every branch - the discriminator/condition is never evaluated
    // at all, so this works even when it references a variable this scanner has no value for.
    // ------------------------------------------------------------------

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
        // No ELSE means "no WHEN matched" returns SQL NULL, which this scanner's string-assembly
        // model has no representation for - omitting that outcome from the union would be
        // unsound, so it declines outright instead.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = CASE WHEN @flags = 1 THEN N'SELECT A' END; EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:conditional", finding.Reason);
    }

    [Fact]
    public void Scan_CaseWithOneUnfoldableBranch_UnfoldableArmDegradesToTypedHole()
    {
        // SYSDATETIME() (unlike GETDATE()) stays a genuinely unfoldable branch - its DATETIME2
        // return type carries its own scale/precision this scanner doesn't resolve. @sql's own
        // declared type is known, though, so the CASE's own unresolvable arm degrades to a typed
        // hole (the same havoc-default policy every other unmodeled-write site gets) rather than
        // discarding the OTHER, perfectly known arm just because its sibling didn't fold.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = CASE WHEN @flags = 1 THEN N'SELECT A' ELSE CONVERT(VARCHAR(30), SYSDATETIME()) END; " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        Assert.Equal(2, result.AnalyzableScripts.Count);
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText == "SELECT A");
        Assert.Contains(result.AnalyzableScripts, s => s.InnerText.Contains("__silentscan_sym_", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // CASE/IIF predicate evaluation (real corpus bug: wide-world-importers'
    // DeactivateTemporalTablesBeforeDataLoad.sql built a CREATE TRIGGER body guarding
    // QUOTENAME(@col) behind CASE WHEN COALESCE(@col, N'') <> N'' THEN ... ELSE N'' END - since
    // FoldConditional used to union BOTH branches unconditionally, its "fully known" assembly
    // included the QUOTENAME branch even though @col provably folded to the SAME empty literal
    // the guard checks against, producing an invalid empty-bracket [] column name that failed to
    // re-parse as T-SQL. TryEvaluatePredicate now proves a WHEN/IIF condition true or false
    // outright whenever both comparison sides fold to known literal text, picking exactly the
    // branch T-SQL's own short-circuit semantics would take.
    // ------------------------------------------------------------------

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
        // The declared type resolves but there is no initializer, so @LastEditedByColumnName
        // folds to a typed Hole - the guard's own truth value is genuinely unknowable at compile
        // time, so this must still union both branches (the existing, safe fallback), never
        // guess which one a real run would take.
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

    // ------------------------------------------------------------------
    // Branch-fold coverage (roadmap "trace provably-constant dynamic SQL across IF/ELSE/TRY-
    // CATCH branches") - the optional-filter accumulation pattern this scanner previously declined
    // outright is now analyzed as the union of every branch's own provably-constant assembly.
    // ------------------------------------------------------------------

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
        // 10 independent optional filters, each appending to @sql under its own IF with no ELSE,
        // produce up to 2^10 = 1024 possible assemblies - comfortably over the 32-assembly cap.
        // @sql has a resolvable declared type, so SqlTextValue.Widen collapses the choice's
        // overflowing prefix (the first six filters, whose 2^6 = 64 combinations alone already
        // exceed the cap) into one typed hole the moment the running total would cross the cap,
        // then keeps folding the remaining four filters normally on top of it - 2^4 = 16 surviving
        // assemblies, all still under the cap. This is the SAME degrade-instead-of-discard policy
        // the proc-call-graph cardinality cap uses; declining the whole EXEC outright would throw
        // away four filters' worth of real, provably-constant structure for no reason.
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
    public void Scan_IfBranchOwnFoldFails_ElseBranchFine_RecoversTheKnownBranchAsAGuardedAlternative()
    {
        // The THEN branch's own assignment can't fold (FORMAT is not a whitelisted builtin - its
        // locale/format-string-driven rendering is never modeled), so the merged value stays
        // Tainted with THAT reason - but SqlTextValue.Join now attaches the ELSE branch's own
        // known text as a GuardedAlternative rather than discarding it, and EmitScriptsOrFinding
        // recovers any such alternative into a real script. A genuine improvement (generalizing
        // DynamicSqlCfg's own IF-only guarded-alternative recovery to every join site): the ELSE
        // branch really did fold, so reporting nothing at all here would throw away real,
        // provably-constant structure for no reason.
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
        // SqlTextValue.Concat's own doc comment claims "EITHER side's own GuardedAlternatives are
        // propagated the SAME way" - but the actual code only handled the case where the TAINTED
        // operand is on the LEFT (`a`); when a known literal prefix concatenates onto a TAINTED
        // right-hand value that itself carries a recoverable GuardedAlternative (b), the prefix
        // was silently dropped rather than prepended onto that alternative - a real corpus shape
        // (`EXEC('SELECT ' + @select + ' FROM dbo.' + @t)` where @select's own fold ends up
        // Tainted-with-alternatives from an earlier IF-guarded append) lost its "SELECT " prefix
        // entirely, producing a bare, un-prefixed column list that could never parse as SQL.
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
        // Both branches happen to assign the exact same literal text - the union must not report
        // the same defect twice just because two independent branches agree on it.
        var result = Scan(
            "DECLARE @sql NVARCHAR(MAX) = N'SELECT 1'; " +
            "IF 1 = 1 BEGIN SET @sql = N'SELECT 2'; END " +
            "ELSE BEGIN SET @sql = N'SELECT 2'; END " +
            "EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal("SELECT 2", script.InnerText);
    }

    // ------------------------------------------------------------------
    // LEN(...) and +/- integer arithmetic folding for LEFT/RIGHT/SUBSTRING's numeric arguments -
    // the "strip a trailing delimiter" idiom: LEFT(@sql, LEN(@sql) - LEN(@delim)).
    // ------------------------------------------------------------------

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
        // Oracle-verified: LEN trims trailing spaces before counting, unlike DATALENGTH.
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
        // The real-world idiom this fix targets: strip a trailing delimiter of known length off
        // an accumulator string built up via straight-line concatenation.
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
        // LEN's own argument doesn't fold (an undeclared variable) - the length stays unknown,
        // same reason as any other non-literal LEFT/RIGHT length argument (LEFT's HoleTransfer
        // still prefers the length's OWN unresolved reason over the generic fallback, even though
        // it no longer requires a concrete length to pass through the source's own type).
        var result = Scan("DECLARE @sql VARCHAR(MAX) = LEFT(N'abcdef', LEN(@undeclared)); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("non-literal-expression:function-call-argument-diverges", finding.Reason);
    }

    // ------------------------------------------------------------------
    // Guarded-EXEC path resolution: "IF cond SET @sql = ... ; ... ; IF cond EXEC(@sql)" - the
    // second IF's own guard proves the first IF's THEN branch is the path that ran, so the
    // no-initializer taint left by the implicit "neither branch ran" path doesn't apply here.
    // ------------------------------------------------------------------

    // DynamicSqlCfg.ApplyGuardedAlternativeFixup attaches each resolved branch's own value as a
    // GuardedAlternative onto whatever the general Join produced (a live Choice, a widened Hole,
    // ...), not just when the merge happens to stay Tainted, and propagates a NESTED join's own
    // tags upward (an "ELSE IF" chain's inner guard survives to outer joins). A later EXEC's own
    // static "active guard" stack (see DynamicSqlTransfer.CompileLeaf) is matched against those
    // tags in EmitScriptsOrFinding: an EXACT text match narrows straight to that one alternative,
    // regardless of whether the un-narrowed value was already usable on its own.
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
        // A DIFFERENT (even if a human could prove it implies the first) guard is left unresolved
        // - this scanner matches guard text exactly, never implication, per its soundness-first
        // policy: no heuristic guessing about what one condition proves about another. @sql's own
        // declared type (VARCHAR(MAX)) resolves, so the IF's implicit ELSE (no explicit
        // assignment, @sql keeps its pre-IF symbolic placeholder) unions cleanly with the THEN
        // branch's literal 'SELECT 1' - two real possible values, one proven, one not - rather
        // than the whole branch merge collapsing to a taint the way an unresolvable type would.
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
        // Same shape, but the declared type is a CREATE TYPE ... FROM alias, unresolvable
        // without a catalog - the IF's implicit ELSE branch stays tainted (no-initializer), and
        // ONE tainted side means the whole branch merge stays tainted too, exactly as before
        // symbolic placeholders existed. DynamicSqlCfg's ApplyGuardedAlternativeFixup DOES attach
        // 'SELECT 1' as a GuardedAlternative under the THEN branch's own guard text
        // ("@mode = 1"), but the consuming EXEC's own active guard ("@mode = 1 AND @extra = 1")
        // is a DIFFERENT string - EmitScriptsOrFinding's exact-text-only narrowing (never
        // implication) finds no match, and since the consuming EXEC IS itself guarded (a non-
        // empty active-guard stack), it declines with the original tainted reason rather than
        // falling back to the old "recover every alternative unconditionally" policy (that
        // fallback only applies to an UNGUARDED consuming EXEC - see EmitScriptsOrFinding's own
        // doc comment).
        var result = Scan(
            "DECLARE @sql dbo.SqlTextType; " +
            "IF @mode = 1 SET @sql = 'SELECT 1'; " +
            "IF @mode = 1 AND @extra = 1 EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("no-initializer", finding.Reason);
    }

    // WP2 (dynamic-SQL v2.1): a symbolic placeholder folded through a KNOWN builtin transfers its
    // TYPE through the call, via BuiltinFunctionSemantics's TryTransferPlaceholderThroughFunction,
    // instead of refusing with "symbolic-value-in-function-argument" - closing the single largest
    // measured real-corpus gap (28 occurrences of that exact reason across the pinned 5-repo
    // corpus at the time this landed). Each fires-fixture below asserts the SAME shape the
    // no-initializer placeholder tests above already assert for a bare `EXEC(@sql)`: the whole
    // EXEC argument becomes nothing but the one (function-wrapped) placeholder, so it still folds
    // to a single AnalyzableScript at Medium confidence.

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
        // Only the SOURCE argument (@sql) is a placeholder - pattern/replacement stay literal, so
        // this is the "pure placeholder source" case the type transfer is scoped to.
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = REPLACE(@sym, 'a', 'b'); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfReplaceWithSymbolicPatternArgument_StillRefuses()
    {
        // Near-miss for the fires-fixture above: the placeholder is the PATTERN argument, not the
        // source - TryTransferPlaceholderThroughFunction only ever inspects REPLACE's source
        // argument (functionCall.Parameters[0]), so this still falls through to the ordinary
        // TryFoldOverArgumentCombinations path and refuses exactly as it did before WP2.
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
        // CAST/CONVERT's target type is already pinned by the call site's own syntax, so this
        // goes through TryTransferPlaceholderThroughFunction's explicitTargetType parameter
        // rather than the PlaceholderTypeTransfer registry lookup the other functions use.
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql VARCHAR(MAX) = CAST(@sym AS VARCHAR(50)); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfCastOfSymbolicVariableToInt_StillRefuses()
    {
        // Near-miss: CAST's own pre-existing "target must be VARCHAR/NVARCHAR" guard runs BEFORE
        // the placeholder-transfer check, so a non-string target still refuses exactly as before -
        // WP2 only ever widens what a STRING-family fold can do, never a numeric one.
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = CAST(@sym AS INT); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:cast-target-not-pinned", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfUpperOfMixedLiteralAndSymbolicConcatenation_StillRefuses()
    {
        // Near-miss: the argument to UPPER is 'prefix' + @sql - a MIXED assembly (one literal
        // segment, one placeholder segment), not a pure single-placeholder assembly.
        // TryTransferPlaceholderThroughFunction only intercepts a pure placeholder (exactly one
        // assembly holding exactly one placeholder segment), so this falls through to the
        // ordinary path and refuses exactly as it did before WP2 - UPPER genuinely could destroy
        // or reshape the literal portion depending on the placeholder's real runtime value.
        var result = Scan("DECLARE @sym NVARCHAR(MAX); DECLARE @sql NVARCHAR(MAX) = UPPER('prefix' + @sym); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("symbolic-value-in-function-argument", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfNCharCrLfConcatenation_ProducesAnalyzableScript()
    {
        // Corpus-measured (WWI's DeactivateTemporalTablesBeforeDataLoad.sql): @CrLf is built as
        // NCHAR(13) + NCHAR(10), then spliced pervasively into every downstream dynamic SQL
        // block - previously the single largest real blocker in the "non-literal-expression:
        // function-call" bucket, since NCHAR/CHAR weren't foldable at all.
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
        // Near-miss: CHAR's valid range is [0, 255] (oracle-verified) - 256 returns SQL NULL,
        // which this scanner has no LiteralSegment representation for, so the fold must decline
        // rather than guess.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'x' + CHAR(256); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:char-out-of-range", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecOfNCharOfNonLiteralArgument_FoldsToFixedWidthTypedHole()
    {
        // NCHAR's own argument isn't foldable to an integer literal (a variable that was never
        // declared) - but NCHAR/CHAR always return exactly one character (or NULL for an
        // out-of-range code point, never an error), a hard T-SQL guarantee regardless of the code
        // point argument's own value, the same "known shape, unknown value" reasoning CAST/CONVERT's
        // own target type already gets. This now resolves to a typed nchar(1) hole rather than
        // declining the whole call site outright.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = NCHAR(@undeclaredCodePoint); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }

    [Fact]
    public void Scan_ExecOfIsNullOfFoldedFirstArgument_ProducesAnalyzableScript()
    {
        // A successfully-folded expression is provably non-NULL (a bare `SET @x = NULL` fails to
        // fold outright), so ISNULL(a, b) always evaluates to `a` whenever `a` folds - `b` is
        // never even inspected.
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
        // Near-miss: the first argument doesn't fold at all (undeclared variable) - ISNULL cannot
        // fall back to the second argument, since a genuinely unfoldable first argument's runtime
        // nullness is unknown.
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
        // NULLIF is deliberately NOT folded, even when both arguments fold: unlike ISNULL/
        // COALESCE it can produce a genuine SQL NULL (when the two arguments compare equal),
        // which this scanner has no LiteralSegment representation for.
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
        // Corpus-measured (First Responder Kit): CAST(SERVERPROPERTY('ServerName') AS
        // NVARCHAR(128)) feeding a REPLACE token-substitution build. SERVERPROPERTY is
        // deterministic PER SERVER (unlike NEWID/GETDATE) but this scanner has no visibility into
        // the target deployment - however its return type (sql_variant, then CAST to NVARCHAR(128)
        // by this call site's own syntax) IS a hard guarantee regardless of deployment, so
        // BuiltinRegistry now reports that well-known return type as a typed hole instead of
        // failing unconditionally the way an "unimplemented"/generic function call would.
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
        // Near-miss: a function that is neither whitelisted, non-deterministic, nor
        // environment-dependent keeps the ordinary generic reason - the new classification
        // doesn't widen to swallow real gaps.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + SOUNDEX(N'x'); EXEC(@sql);");

        var finding = Assert.Single(result.Findings);
        Assert.Equal(DynamicSqlOutcome.Unanalyzable, finding.Outcome);
        Assert.Equal("non-literal-expression:function-call", finding.Reason);
        Assert.Empty(result.AnalyzableScripts);
    }

    [Fact]
    public void Scan_ExecDropDatabaseBuiltFromLiteralPlusParameter_InsideIfExists_ProducesAnalyzableScript()
    {
        // A cross-database admin proc's own dispatch pattern: EXEC('DROP DATABASE ' + @DbName),
        // wrapped in an IF EXISTS guard against sys.databases. Structurally this is nothing but
        // the ordinary "literal + variable" EXEC() shape HandleStringList already handles - the
        // guard and the DROP DATABASE keyword itself introduce no new construct this scanner
        // doesn't already model.
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
        // sp_executesql @sql where @sql's own value comes from an IF/ELSE branch INSIDE a
        // cursor's WHILE loop body - nothing here is a new construct beyond what the cursor
        // placeholder fix and the ordinary IF/ELSE branch-union machinery already handle
        // independently; this proves the two compose correctly together.
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
        // CHAR(N)/NCHAR(N) with a literal integer code point already folds to the actual
        // character via TryFoldCharOrNChar - this locks that existing behavior in as a
        // permanent regression test rather than leaving it proven only by code reading.
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
        // STR(float_expr [, length [, decimal]]) always returns a CHAR(length) value (length 10
        // by default) regardless of what the input actually is - the same "target type pinned by
        // the call site's own syntax, not the input's" reasoning CAST/CONVERT already use, so a
        // numeric placeholder (CHECKSUM here) should transfer through it the same way.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + STR(CHECKSUM('a', 'b'), 10); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfCastOfChecksumPlaceholderToChar_TransfersPlaceholderTypeAsFixedLengthChar()
    {
        // CAST/CONVERT's blank-padding rendering algorithm for a CHAR/NCHAR target isn't modeled
        // (declines rather than guessing the padded VALUE, same as before) - but that's a value-
        // rendering concern, not a type one: CAST(@x AS CHAR(10)) unambiguously produces a
        // CHAR(10)-typed result regardless of what @x actually is, so the placeholder-transfer
        // path (which only needs the TYPE, never the rendered content) should still work for a
        // CHAR/NCHAR target exactly as it already does for VARCHAR/NVARCHAR.
        var result = Scan("DECLARE @sql NVARCHAR(MAX) = N'SELECT ' + CAST(CHECKSUM('a', 'b') AS CHAR(10)); EXEC(@sql);");

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Equal(FindingConfidence.Medium, script.Confidence);
    }

    [Fact]
    public void Scan_ExecOfReplaceOfLiteralTemplateWithSymbolicProcParam_SplicesPlaceholderIntoTemplate()
    {
        // REPLACE(literalTemplate, literalToken, @symbolicValue) has a literal SOURCE and a
        // literal PATTERN - only the REPLACEMENT is symbolic. The template's shape (everything
        // around each occurrence of the token) is still fully known, so this should splice the
        // placeholder into the template at each occurrence rather than declining the whole fold
        // just because one of REPLACE's three arguments isn't itself a literal.
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
        // DROP FUNCTION dbo.@symbolicName already folds fine (the symbolic identifier substitutes
        // cleanly, same as a FROM-clause table name), and the reparse of the substituted text
        // succeeds too - the placeholder just wasn't recognized as sitting in a safe IDENTIFIER
        // position by AllPlaceholdersInSafePosition, which only checked NamedTableReference/
        // DropTableStatement/TruncateTableStatement, not the DropObjectsStatement-derived DDL
        // family (DROP FUNCTION/PROCEDURE/VIEW/TRIGGER/SYNONYM all share that same shape).
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


    // ------------------------------------------------------------------
    // Round 2 probes - PROBE-ONLY, temporary, deleted once triaged.
    // ------------------------------------------------------------------

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
        // CONVERT(VARCHAR(n), @param, style) already has dedicated handling
        // (TryFoldCastOrConvert) that transfers the target VARCHAR type onto a placeholder -
        // this is not a new construct, just that handling exercised inside a concatenation chain.
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
        // @FilterAttention stands for a whole optional clause fragment (empty, or " AND x = y",
        // spliced in between the FROM clause and ORDER BY) - a single symbolic identifier token
        // can never legally sit there, so the ordinary token-substituted reparse breaks outright.
        // Replacing the placeholder with a single space instead (never a token that can fuse two
        // adjacent literal fragments together) still yields valid SQL missing only the part this
        // scanner could never see anyway - the surrounding, unaffected structure should still be
        // analyzed rather than declining the whole call site.
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
        // A caller-supplied identifier concatenated straight into the FROM clause (no wrapping
        // template text around it) is already handled by the existing symbolic-placeholder
        // machinery - the placeholder lands in an identifier position ScriptDom can reparse.
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
        // A SET nested two IF levels deep (outer selects the base query, inner appends a WHERE
        // fragment) is just two independent applications of the same guarded-alternative
        // divergence machinery that already handles a single level - no new construct.
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
        // A CASE expression is a StringBuilders whitelisted node whose result is itself folded
        // per-arm by the existing literal-value folding, so a CASE spliced into a concatenation
        // chain folds the same way a literal segment would.
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
        // Two IF levels gated on bitwise-AND conditions rather than equality comparisons is not
        // a distinct construct from ordinary nested IF divergence - the guard expression's shape
        // doesn't matter to the fold, only which branch a SET lives in.
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
        // A plain 3-way IF/ELSE-IF/ELSE chain assigning the same variable is nowhere near the
        // 32-assembly cardinality cap - it produces exactly one assembly per branch.
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
        // The SQL text's own content lives in a database ROW, not anywhere in the source file, so
        // without a fetcher (the ordinary static-only path, and every scan-corpus/file-mode call
        // site - CLAUDE.md forbids executing corpus DML/procs) the concrete value can never be
        // known. But the STRUCTURAL SHAPE is: single catalog-known table, no JOIN, one selected
        // column - fully known even though the row is not, the same "known shape, unknown value"
        // case the equivalent `SELECT @var = col FROM t` form already gets. This must resolve to
        // an analyzable script parameterized on a RowDependentColumn hole, not decline outright -
        // matching this project's real-world dominant shape for "load dynamic SQL from a table"
        // (a scalar-subquery DECLARE initializer, not a SELECT-assignment).
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
        // @Query stands for the ENTIRE cursor SELECT, not an optional trailing fragment -
        // DECLARE ... CURSOR FOR needs a real query no matter what, so eliding to a single space
        // (or "1=1"/"NULL", neither of which is a query either) fails to parse. The "(SELECT 1)"
        // filler candidate - tried only after all three others already failed - is the one
        // grammar-neutral filler that DOES satisfy a whole-missing-query position; a real corpus
        // shape (a temp/local cursor built from an entirely symbolic body) now recovers as
        // PartiallyAnalyzed rather than declining outright. "(SELECT 1)" itself has no column
        // operand and no FROM clause, so it can never contribute a fabricated predicate finding.
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
        // Real corpus shape (SQL-Server-First-Responder-Kit's sp_BlitzFirst.sql): a temp table
        // name built from a symbolic value is reused as an IDENTIFIER in both CREATE TABLE and
        // INSERT - that placeholder is genuinely load-bearing and can NEVER be blanked without
        // breaking the parse (CREATE TABLE with no name, INSERT with no target). A SEPARATE
        // symbolic value sits where only an optional query hint could go - THAT one can be
        // blanked. The old blank-everything elision policy would blank BOTH, breaking the parse
        // a second time (no table name survives) even though the actual culprit was the other
        // placeholder alone. Targeted elision must isolate just the placeholder ScriptDOM's own
        // error blames, leaving the load-bearing identifier as a real token, and succeed.
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
        // Real corpus shape: an unseeded proc parameter stands for a WHOLE optional filter
        // fragment appended after WHERE, immediately followed by a real, load-bearing predicate
        // (`WHERE <filter> AND real.condition`) - a bare identifier-shaped placeholder token can
        // never be a legal search_condition on its own (T-SQL has no standalone-boolean-value
        // grammar outside a real comparison/EXISTS/IN), and the OLD blank-to-a-single-space
        // elision policy left a dangling "AND" with no left operand, so this used to decline
        // outright as symbolic-value-broke-parse even though the REST of the statement (the real
        // predicate, the real SELECT list) was fully known. The "1=1" filler candidate produces a
        // genuine (if uninformative) search_condition here, letting the rest of the statement
        // analyze - PartiallyAnalyzed, never a fabricated verdict about the elided fragment itself
        // (int-literal-vs-int-literal has no column operand at all).
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

    // ------------------------------------------------------------------
    // Round 3 probes - PROBE-ONLY, temporary, deleted once triaged.
    // ------------------------------------------------------------------

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

    // ------------------------------------------------------------------
    // Self-referential SELECT-assignment append (SELECT @x = @x + expr FROM t) - the running-
    // total/"quirky update" idiom real corpus code uses to accumulate dynamic SQL text across
    // rows (SQL-Server-First-Responder-Kit's sp_Blitz.sql: an aggregate count spliced into an
    // ORDER BY/OPTION(RECOMPILE) tail). T-SQL evaluates this per matching row as
    // @x := @x + expr(row), so @x's final value always keeps its OWN prior value intact with at
    // most some unknown text appended - never something unrelated to what @x already held.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_SelectAssignmentSelfAppendsUnresolvableAggregateFromUncatalogedTable_PreservesKnownPrefix()
    {
        // Both IF and ELSE branches append to the SAME @StringToExecute that already holds a big
        // literal prefix - the IF branch appends a literal, the ELSE branch appends an aggregate
        // over sys.databases (uncataloged, unresolvable). Before the self-referential-append fix,
        // the ELSE branch's own SELECT-assignment discarded that ENTIRE prefix via the general
        // HavocOrTaint path, rendering ONLY a bare placeholder followed by the trailing literal -
        // which then broke the parse outright (the placeholder alone can't open a whole
        // INSERT/SELECT statement). Now the prefix survives and only the truly-unknown appended
        // piece is a hole, so both branches analyze cleanly.
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
    public void Scan_SelectAssignmentSelfAppendWithUninitializedPriorValue_ConcatenatesTwoHolesSoundly()
    {
        // @x's own PRIOR value is itself an uninitialized-DECLARE hole, not literal text - self-
        // append must still soundly concatenate two unknowns rather than fabricating any content,
        // producing two adjacent placeholder tokens (one for the prior value, one for the
        // appended aggregate) instead of collapsing to a single generic hole.
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

        // No real SQL text survives once both holes are stripped - honestly unanalyzable at the
        // pipeline stage, not this fix's concern (it only guarantees the CONCATENATION stays
        // sound; whether what's left is enough to parse is a separate, correctly-reported outcome).
        var finding = Assert.Single(pipeline.Findings);
        Assert.Equal("symbolic-value-not-positionable:whole-statement", finding.Reason);
    }

    [Fact]
    public void Scan_SelectAssignmentAppendsToADifferentVariableThanTheOneOnTheLeft_StaysOrdinaryHavoc()
    {
        // SELECT @x = @y + expr FROM t - @x is NOT self-referential (the appended-to variable on
        // the right is @y, not @x), so this scanner must not assume @x's own prior value survives;
        // it falls back to ordinary havoc exactly as before this fix.
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
