using SilentScan.Core.Predicates;
using SilentScan.Core.Reporting;
using SilentScan.Core.Rules;
using SilentScan.Tests.Support;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// End-to-end coverage for the roadmap item "trace provably-constant dynamic SQL across
/// proc-call edges": a caller passes a string literal into a callee procedure's parameter, and
/// the callee's OWN body builds dynamic SQL from that parameter. Before this, the dynamic SQL engine
/// never seeded a proc's own formal parameters at all - any reference to one inside dynamic SQL
/// failed as "variable-not-in-scope" regardless of what any caller passed. Runs through
/// ScanReportBuilder (ProcCallGraphBuilder -> DynamicSqlScannerV2 -> DynamicSqlPipeline), the same
/// entry point production uses, not DynamicSqlScannerV2 in isolation.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class DynamicSqlCrossCallEdgePipelineTests
{
    private static async Task<ScanReport> Scan(string sql, FindingConfidence minimumConfidence = FindingConfidence.High)
    {
        var report = await EngineAuthoritativeScan.ScanAsync(sql, "SQL_Latin1_General_CP1_CI_AS", minimumConfidence: minimumConfidence);
        foreach (var file in report.ParseHealth.Files)
        {
            Assert.Empty(file.Errors);
        }

        return report;
    }

    [Fact]
    public async Task SingleCallerLiteral_SeedsCalleeParameter_DynamicSqlAnalyzedAndScanForced()
    {
        // The seeded @Status value is reconstructed into the dynamic SQL text as an EXPLICIT
        // nvarchar literal (N'...' around the placeholder, not just around the STATIC pieces of
        // the concatenation) so the resulting comparison is varchar-column-vs-nvarchar-literal -
        // a genuine ScanForced this test can check for, rather than merely "some finding
        // exists". CLAUDE.md's own rule (only the reconstructed TEXT's own quote characters
        // determine a literal's type, never the outer nvarchar variable that built it) is
        // exactly why the DECLARE below embeds N' around @Status's own surrounding quotes.
        var report = await Scan("""
            CREATE TABLE dbo.Orders (Status varchar(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                EXEC dbo.usp_FindByStatus @Status = N'Active';
            END;
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.NotNull(finding.DynamicSqlCallSite);
        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Reason?.StartsWith("variable-not-in-scope", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task SingleCallerVariable_WithSingleUnconditionalLiteralAssignment_SeedsCalleeParameter_ScanForced()
    {
        // One-level constant propagation (CLAUDE.md roadmap): the caller passes a VARIABLE, not
        // a literal directly - @v is assigned exactly once, unconditionally, so ProcCallGraphBuilder
        // traces it back to 'Active' the same way a direct literal argument already seeds the
        // callee's own parameter. High confidence (an exact traced value), not the Medium a
        // generic "no known value" placeholder would get.
        var report = await Scan("""
            CREATE TABLE dbo.Orders (Status varchar(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                DECLARE @v NVARCHAR(20) = N'Active';
                EXEC dbo.usp_FindByStatus @v;
            END;
            """);

        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
    }

    [Fact]
    public async Task TwoCallersWithDifferentLiterals_BothAssembliesAnalyzed()
    {
        // Value-seeding across proc-call edges (extended beyond a single caller): every known
        // caller supplies a literal for @Status, so both are reparsed and analyzed, rather than
        // the whole site declining just because there's more than one call site. A stored
        // procedure is still a public surface, though - external code this scan's own call graph
        // can't see may call it too - so a third, unresolved assembly is also analyzed for that
        // case rather than the seed collapsing to just the two known-caller literals.
        //
        // DynamicSqlFinding (SourcePath/Line/Column/Outcome/Reason only) carries no per-caller or
        // per-literal detail - every assembly reparsed from this one EXEC(@sql) site reports at
        // the SAME coordinates regardless of which caller's literal seeded it, and
        // TypedPredicateFindingIdentity.ComputeKey deliberately excludes a literal's own text from
        // its dedup key (CLAUDE.md: "the same defect surfacing in more than one assembly is one
        // finding, not one per assembly") - so no assertion against a single scan's own findings
        // can distinguish "both callers' distinct literals were genuinely, independently threaded
        // through" from "a bug fed both callers the same literal". The only way to observe that
        // distinction through this codebase's public API is DIFFERENTIALLY: scan with only
        // CallerA present, then scan again with CallerB added, and confirm the analyzed-literal
        // count increases by exactly one. If CallerB's own call edge were dropped, miscounted, or
        // silently merged into CallerA's, the count would NOT increase - a bug fully invisible to
        // a single-scan assertion is directly caught here.
        const string SharedDdl = """
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = ''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_CallerA AS BEGIN EXEC dbo.usp_FindByStatus @Status = N'Active'; END;
            """;

        var withOnlyCallerA = await Scan(SharedDdl);
        var withBothCallers = await Scan(SharedDdl + """

            GO
            CREATE PROCEDURE dbo.usp_CallerB AS BEGIN EXEC dbo.usp_FindByStatus @Status = N'Closed'; END;
            """);

        var onlyCallerACount = withOnlyCallerA.DynamicSqlFindings.Count(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        var bothCallersCount = withBothCallers.DynamicSqlFindings.Count(f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);

        Assert.Equal(onlyCallerACount + 1, bothCallersCount);
        Assert.DoesNotContain(withBothCallers.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);

        // dbo.Orders is never declared in this test's own DDL, so the reparsed predicate's column
        // never resolves to a real catalog table either way - unrelated to the seeding change.
        Assert.Empty(withBothCallers.TypedFindings);
    }

    [Fact]
    public async Task NoKnownCaller_QuotedPlaceholderPosition_MediumConfidenceScanForced_ExcludedByDefault()
    {
        // Nothing anywhere in this snippet calls usp_FindByStatus - BuildParameterSeed's
        // zero-caller case seeds @Status as a symbolic placeholder of its own declared type
        // (NVARCHAR(20) resolves with no catalog needed) rather than tainting it, since T-SQL's
        // own type contract for this proc guarantees the runtime value really is this type. The
        // placeholder token lands inside a quoted N'...' literal in the generated SQL, so the
        // resulting comparison is a genuine varchar-column-vs-nvarchar-literal one BY
        // CONSTRUCTION - the same real ScanForced the fully-literal cross-call-edge test above
        // gets, just at Medium confidence (the runtime VALUE is unproven) instead of High.
        const string sql = """
            CREATE TABLE dbo.Orders (Status varchar(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            """;

        var defaultReport = await Scan(sql);
        Assert.DoesNotContain(defaultReport.TypedFindings, f => f.Column.ColumnName == "Status");

        var mediumReport = await Scan(sql, FindingConfidence.Medium);
        var finding = Assert.Single(mediumReport.TypedFindings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public async Task NoKnownCaller_ObjectIdentifierPosition_AnalyzedWithZeroFindings()
    {
        // @TableName lands entirely inside DROP TABLE's own object-name position - no value is
        // assumed anywhere (DROP TABLE has no predicate concept at all to miss), so this is
        // AnalyzedLiteral with zero findings even at the default (High) confidence threshold,
        // never Unanalyzable and never a fabricated finding about a table this scanner invented.
        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_DropStagingTable @TableName SYSNAME AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'DROP TABLE ' + @TableName;
                EXEC(@sql);
            END;
            """);

        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        Assert.Empty(report.TypedFindings);
        Assert.Empty(report.Tier1Findings);
    }

    [Fact]
    public async Task NoKnownCaller_ObjectIdentifierPositionInsideFullStatementWithWhereClause_AnalyzedWithZeroFindings()
    {
        // Corpus-measured (First Responder Kit's sp_Blitz/sp_BlitzFirst, QUOTENAME-built FROM
        // targets alongside a real WHERE clause): the dynamically-named table is a full SELECT,
        // not just DROP/TRUNCATE - AllPlaceholdersInSafePosition admits this because EVERY
        // placeholder occurrence sits inside a NamedTableReference's own identifier parts, since a
        // synthesized __silentscan_sym_...__ token can never resolve to a real deployed table
        // regardless of what else the statement's WHERE clause claims.
        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_SkipChecks @SchemaName SYSNAME, @TableName SYSNAME AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM ' + QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName) + N' WHERE Col1 IS NULL';
                EXEC(@sql);
            END;
            """);

        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        Assert.Empty(report.TypedFindings);
        Assert.Empty(report.Tier1Findings);
    }

    [Fact]
    public async Task NoKnownCaller_PlaceholderBareInPredicateValuePosition_AnalyzedButNoFindingFabricated()
    {
        // @Flag's placeholder sits BARE (unquoted) in a WHERE predicate's value position, so the
        // reparsed text becomes `Col1 = __silentscan_sym_...__` - a syntactically valid but
        // semantically nonsensical COLUMN-vs-COLUMN comparison, since a bare identifier there
        // parses as a column reference. The invented token can never resolve against the real
        // catalog, so ordinary column resolution safely skips it (SkippedConstructs) rather than
        // guessing a type for it - the call site is still reported AnalyzedLiteral (its literal
        // structure was fully reparsed) but produces no Typed/Tier1 finding at all, proving the
        // "unresolvable ⇒ skipped, never fabricated" argument for the one position category
        // (a genuine value position, not just an identifier one) most likely to raise doubt.
        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_SkipChecks @SchemaName SYSNAME, @Flag SYSNAME AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM ' + QUOTENAME(@SchemaName) + N'.T WHERE Col1 = ' + @Flag;
                EXEC(@sql);
            END;
            """);

        var finding = Assert.Single(report.DynamicSqlFindings);
        Assert.Equal(DynamicSqlOutcome.AnalyzedLiteral, finding.Outcome);
        Assert.Empty(report.TypedFindings);
        Assert.Empty(report.Tier1Findings);
    }

    [Fact]
    public async Task NoKnownCaller_MixedIdentifierAndQuotedPlaceholdersInOneStatement_QuotedOnePredicateStillFolds()
    {
        // Generalizes the fires-fixture above from "every occurrence is identifier-position" to
        // a genuine MIX in the SAME statement (corpus-measured shape: First Responder Kit's
        // output-to-table blocks mix an identifier-position table target with a quoted-position
        // value in the same IF/INSERT). @LogTableName's placeholder sits entirely inside
        // QUOTENAME's own identifier - it never resolves to a real table, so the CROSS JOIN
        // contributes nothing - while @Status's placeholder sits quoted inside N'''...''', a
        // genuine varchar-column-vs-nvarchar-literal comparison against the REAL dbo.Orders
        // table in the SAME statement. Proves per-occurrence proof, not per-statement shape.
        var report = await Scan("""
            CREATE TABLE dbo.Orders (Status VARCHAR(20) NOT NULL, INDEX IX_Status (Status));
            GO
            CREATE PROCEDURE dbo.usp_JoinAndCheck @LogTableName SYSNAME, @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) =
                    N'SELECT o.Status FROM dbo.Orders AS o CROSS JOIN ' + QUOTENAME(@LogTableName) +
                    N' AS lt WHERE o.Status = N''' + @Status + N'''';
                EXEC(@sql);
            END;
            """, FindingConfidence.Medium);

        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Status");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
        Assert.Equal(FindingConfidence.Medium, finding.Confidence);
    }

    [Fact]
    public async Task NoKnownCaller_TwoStatementsOnlyOneHasAPlaceholder_SiblingStatementGetsOrdinaryExtraction()
    {
        // Generalizes further: a multi-STATEMENT script (two statements separated by `;` inside
        // the same @sql), not just a multi-clause single statement. The first statement's INSERT
        // target is entirely a placeholder identifier (never resolves, contributes nothing); the
        // SECOND statement never touches the placeholder at all and gets full, ordinary
        // extraction - a plain literal comparison against a real table, exactly as it would if
        // the whole script were placeholder-free (the "one statement only" restriction the
        // earlier identifier-only classifier needed no longer applies).
        var report = await Scan("""
            CREATE TABLE dbo.Customers (Name VARCHAR(20) NOT NULL, INDEX IX_Name (Name));
            GO
            CREATE PROCEDURE dbo.usp_TwoStatements @LogTableName SYSNAME AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) =
                    N'INSERT INTO ' + QUOTENAME(@LogTableName) + N' (Msg) VALUES (''x'');' +
                    N'SELECT Name FROM dbo.Customers WHERE Name = N''y'';';
                EXEC(@sql);
            END;
            """, FindingConfidence.Medium);

        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        var finding = Assert.Single(report.TypedFindings, f => f.Column.ColumnName == "Name");
        Assert.Equal(Verdict.ScanForced, finding.Verdict);
        Assert.True(finding.Column.Indexed);
    }

    [Fact]
    public async Task CallerPassesVariableNotLiteral_ResolvableTypeFoldsToSymbolicPlaceholder()
    {
        // @IncomingStatus is a variable, not a literal, so the single known caller can't supply a
        // concrete value for @Status - but @Status's own declared type (NVARCHAR(20)) still
        // resolves with no catalog needed, so it folds to a symbolic placeholder rather than a
        // bare taint. dbo.Orders is never declared in this test's own DDL, so the reparsed
        // predicate's column still never resolves to a real catalog table - unrelated to the
        // seeding change, same as the other-caller-shapes tests above.
        var report = await Scan("""
            CREATE PROCEDURE dbo.usp_FindByStatus @Status NVARCHAR(20) AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM dbo.Orders WHERE Status = ''' + @Status + N'''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller @IncomingStatus NVARCHAR(20) AS
            BEGIN
                EXEC dbo.usp_FindByStatus @Status = @IncomingStatus;
            END;
            """, FindingConfidence.Medium);

        Assert.DoesNotContain(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.Unanalyzable);
        Assert.Contains(report.DynamicSqlFindings, f => f.Outcome == DynamicSqlOutcome.AnalyzedLiteral);
        Assert.Empty(report.TypedFindings);
    }

    [Fact]
    public async Task SingleCallerOmitsArgument_DefaultBehavesExactlyLikeALiteralArgument()
    {
        // The one in-scan caller omits @Table, so @Table's own DEFAULT applies for that edge -
        // which makes the default exactly as trustworthy as a literal ARGUMENT from a single
        // caller: a genuinely-executed value (the finding against dbo.Small is real - usp_Caller
        // really does query it), but never the parameter's COMPLETE value space (a stored
        // procedure is a public surface; external callers this scan can't see may pass
        // anything). The literal-argument branch widens via WidenForPossibleExternalCallers;
        // the default branch didn't (2026-08 audit), so the default path silently skipped the
        // external-caller placeholder assembly the literal path surfaces. Pinned as a parity
        // test: same proc body, value supplied by default vs by literal argument, must produce
        // identical typed findings and identical dynamic-SQL outcome/reason accounting.
        const string body = """
            CREATE TABLE dbo.Small (Code varchar(20) NOT NULL, INDEX IX_Small_Code (Code));
            GO
            CREATE PROCEDURE dbo.usp_Report @Table SYSNAME{0} AS
            BEGIN
                DECLARE @sql NVARCHAR(MAX) = N'SELECT 1 FROM ' + @Table + N' WHERE Code = N''1''';
                EXEC(@sql);
            END;
            GO
            CREATE PROCEDURE dbo.usp_Caller AS
            BEGIN
                EXEC dbo.usp_Report{1};
            END;
            """;

        var viaDefault = await Scan(
            body.Replace("{0}", " = N'dbo.Small'", StringComparison.Ordinal).Replace("{1}", string.Empty, StringComparison.Ordinal),
            minimumConfidence: FindingConfidence.Low);
        var viaLiteralArgument = await Scan(
            body.Replace("{0}", string.Empty, StringComparison.Ordinal).Replace("{1}", " @Table = N'dbo.Small'", StringComparison.Ordinal),
            minimumConfidence: FindingConfidence.Low);

        static IReadOnlyList<string> TypedShape(Core.Reporting.ScanReport report) =>
            [.. report.TypedFindings.Select(f => $"{f.Column.TableQualifiedName}.{f.Column.ColumnName}:{f.Verdict}:{f.Confidence}")];

        static IReadOnlyList<string> DynamicShape(Core.Reporting.ScanReport report) =>
            [.. report.DynamicSqlFindings.Select(f => $"{f.Outcome}:{f.Reason}").OrderBy(s => s, StringComparer.Ordinal)];

        // The genuinely-executed shape must still be reported - widening the default must not
        // suppress the real dbo.Small finding, only add the honest external-caller accounting.
        Assert.Contains(viaDefault.TypedFindings, f => f.Column.TableQualifiedName == "dbo.Small");
        Assert.Equal(TypedShape(viaLiteralArgument), TypedShape(viaDefault));
        Assert.Equal(DynamicShape(viaLiteralArgument), DynamicShape(viaDefault));
    }
}
