using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Task-comment tracking" and "Non-ANSI and deprecated
/// spellings". Fully syntax-only, no catalog. Oracle-verified separately (Docker instance) for the
/// "= NULL"/"&lt;&gt; NULL" silent always-false trap and the still-parses-on-the-current-engine
/// claims for the deprecated-but-not-removed forms - see <see cref="DeprecatedSyntaxFinding"/>'s own
/// doc comment.
/// </summary>
public sealed class DeprecatedSyntaxScannerTests
{
    private static IReadOnlyList<DeprecatedSyntaxFinding> Scan(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        return DeprecatedSyntaxScanner.Scan(result);
    }

    [Fact]
    public void TodoComment_Fires()
    {
        var findings = Scan("-- TODO: fix this later\nSELECT 1;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.TaskCommentTodo);
    }

    [Fact]
    public void FixmeComment_Fires()
    {
        var findings = Scan("/* FIXME - broken under load */\nSELECT 1;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.TaskCommentFixme);
    }

    [Fact]
    public void TodoAsPartOfLongerWord_NeverFires()
    {
        var findings = Scan("-- see the TODOLIST spreadsheet for the full backlog\nSELECT 1;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.TaskCommentTodo);
    }

    [Fact]
    public void OrdinaryComment_NeverFiresTaskComment()
    {
        var findings = Scan("-- computes the running total\nSELECT 1;");

        Assert.DoesNotContain(findings, f => f.Kind is DeprecatedSyntaxFindingKind.TaskCommentTodo or DeprecatedSyntaxFindingKind.TaskCommentFixme);
    }

    [Fact]
    public void NotLessThanOperator_FiresNonAnsi()
    {
        var findings = Scan("SELECT 1 WHERE 1 !< 2;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.NonAnsiComparisonOperator);
    }

    [Fact]
    public void NotGreaterThanOperator_FiresNonAnsi()
    {
        var findings = Scan("SELECT 1 WHERE 1 !> 2;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.NonAnsiComparisonOperator);
    }

    [Fact]
    public void ExclamationNotEqual_NotComparedToNull_FiresNonAnsi()
    {
        var findings = Scan("SELECT 1 WHERE 1 != 2;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.NonAnsiComparisonOperator);
    }

    [Fact]
    public void AnsiNotEqual_NeverFiresNonAnsi()
    {
        var findings = Scan("SELECT 1 WHERE 1 <> 2;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.NonAnsiComparisonOperator);
    }

    [Fact]
    public void EqualsNull_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.T WHERE Col = NULL;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.EqualsNullComparison);
    }

    /// <summary>
    /// Under ANSI_NULLS OFF (baked in at CREATE/ALTER time from sys.sql_modules.uses_ansi_nulls),
    /// "= NULL" behaves as "IS NULL" and genuinely matches NULL rows - the finding's core claim
    /// (a silent always-false trap) would be actively wrong for this module, so it must be
    /// suppressed rather than fired unconditionally.
    /// </summary>
    [Fact]
    public void EqualsNull_ModuleUsesAnsiNullsOff_Suppressed()
    {
        var result = SqlScriptParser.ParseText("test.sql", "CREATE PROCEDURE dbo.usp_Find AS SELECT * FROM dbo.T WHERE Col = NULL;");
        Assert.False(result.HasErrors);

        var catalog = new DatabaseCatalog();
        catalog.AddModuleUsesAnsiNulls("dbo.usp_Find", usesAnsiNulls: false);
        var findings = DeprecatedSyntaxScanner.Scan(result, catalog);

        Assert.DoesNotContain(findings, f => f.Kind is DeprecatedSyntaxFindingKind.EqualsNullComparison or DeprecatedSyntaxFindingKind.NotEqualsNullComparison);
    }

    [Fact]
    public void EqualsNull_ModuleUsesAnsiNullsTrue_StillFires()
    {
        var result = SqlScriptParser.ParseText("test.sql", "CREATE PROCEDURE dbo.usp_Find AS SELECT * FROM dbo.T WHERE Col = NULL;");
        Assert.False(result.HasErrors);

        var catalog = new DatabaseCatalog();
        catalog.AddModuleUsesAnsiNulls("dbo.usp_Find", usesAnsiNulls: true);
        var findings = DeprecatedSyntaxScanner.Scan(result, catalog);

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.EqualsNullComparison);
    }

    [Fact]
    public void EqualsNull_ModuleFlagUnresolved_StillFires()
    {
        // No catalog entry for this module at all (file-mode scanning, or a live catalog lookup
        // miss) - the documented majority-case default (ANSI_NULLS ON) applies, not a
        // speculative suppression.
        var result = SqlScriptParser.ParseText("test.sql", "CREATE PROCEDURE dbo.usp_Find AS SELECT * FROM dbo.T WHERE Col = NULL;");
        Assert.False(result.HasErrors);

        var findings = DeprecatedSyntaxScanner.Scan(result, catalog: new DatabaseCatalog());

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.EqualsNullComparison);
    }

    [Fact]
    public void NotEqualToBracketsNull_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.T WHERE Col <> NULL;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.NotEqualsNullComparison);
    }

    [Fact]
    public void NotEqualToExclamationNull_FiresNotEqualsNullOnly()
    {
        var findings = Scan("SELECT * FROM dbo.T WHERE Col != NULL;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.NotEqualsNullComparison);
        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.NonAnsiComparisonOperator);
    }

    [Fact]
    public void IsNull_NeverFiresEqualsNull()
    {
        var findings = Scan("SELECT * FROM dbo.T WHERE Col IS NULL;");

        Assert.DoesNotContain(findings, f => f.Kind is DeprecatedSyntaxFindingKind.EqualsNullComparison or DeprecatedSyntaxFindingKind.NotEqualsNullComparison);
    }

    [Fact]
    public void EqualsRealValue_NeverFiresEqualsNull()
    {
        var findings = Scan("SELECT * FROM dbo.T WHERE Col = 1;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.EqualsNullComparison);
    }

    [Fact]
    public void LikePatternWithNoWildcard_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.T WHERE Col LIKE 'ABC';");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.LikeWithNoWildcard);
    }

    [Fact]
    public void LikePatternWithPercentWildcard_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.T WHERE Col LIKE 'ABC%';");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.LikeWithNoWildcard);
    }

    [Fact]
    public void LikePatternWithUnderscoreWildcard_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.T WHERE Col LIKE 'A_C';");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.LikeWithNoWildcard);
    }

    [Fact]
    public void LegacyCompatibilityView_Fires()
    {
        var findings = Scan("SELECT * FROM sysobjects;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.LegacySystemCompatibilityView);
    }

    [Fact]
    public void RealCatalogView_NeverFiresLegacyCompatibilityView()
    {
        var findings = Scan("SELECT * FROM sys.objects;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.LegacySystemCompatibilityView);
    }

    [Fact]
    public void OrdinaryTableNamedLikeCompatibilityView_NeverFires()
    {
        var findings = Scan("SELECT * FROM app.sysobjects;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.LegacySystemCompatibilityView);
    }

    [Fact]
    public void TableHintWithoutWith_Fires()
    {
        var findings = Scan("SELECT * FROM dbo.T (NOLOCK);");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.TableHintWithoutWith);
    }

    [Fact]
    public void TableHintWithWith_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.T WITH (NOLOCK);");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.TableHintWithoutWith);
    }

    [Fact]
    public void NoTableHint_NeverFires()
    {
        var findings = Scan("SELECT * FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.TableHintWithoutWith);
    }

    [Fact]
    public void NumberedProcedureDefinition_Fires()
    {
        var findings = Scan("CREATE PROCEDURE dbo.Foo;1 AS SELECT 1;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.NumberedProcedureDefinition);
    }

    [Fact]
    public void OrdinaryProcedureDefinition_NeverFiresNumbered()
    {
        var findings = Scan("CREATE PROCEDURE dbo.Foo AS SELECT 1;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.NumberedProcedureDefinition);
    }

    [Fact]
    public void NumberedProcedureExecution_Fires()
    {
        var findings = Scan("EXEC dbo.Foo;1;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.NumberedProcedureExecution);
    }

    [Fact]
    public void OrdinaryProcedureExecution_NeverFiresNumbered()
    {
        var findings = Scan("EXEC dbo.Foo;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.NumberedProcedureExecution);
    }

    [Fact]
    public void StringLiteralColumnAlias_Fires()
    {
        var findings = Scan("SELECT Col 'My Alias' FROM dbo.T;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.StringLiteralColumnAlias);
    }

    [Fact]
    public void IdentifierColumnAlias_NeverFires()
    {
        var findings = Scan("SELECT Col AS MyAlias FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.StringLiteralColumnAlias);
    }

    [Fact]
    public void BracketedColumnAlias_NeverFires()
    {
        var findings = Scan("SELECT Col AS [My Alias] FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.StringLiteralColumnAlias);
    }

    [Fact]
    public void RemovedSecurityStoredProcedure_Fires()
    {
        var findings = Scan("EXEC sp_addlogin 'someuser';");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.RemovedSecurityStoredProcedure);
    }

    [Fact]
    public void OrdinaryUserProcedure_NeverFiresRemovedSecurityProcedure()
    {
        var findings = Scan("EXEC dbo.spDoSomething;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.RemovedSecurityStoredProcedure);
    }

    [Fact]
    public void SetRowcount_Fires()
    {
        var findings = Scan("SET ROWCOUNT 10;");

        Assert.Contains(findings, f => f.Kind == DeprecatedSyntaxFindingKind.DeprecatedSetRowcount);
    }

    [Fact]
    public void NoSetRowcount_NeverFires()
    {
        var findings = Scan("SELECT TOP (10) * FROM dbo.T;");

        Assert.DoesNotContain(findings, f => f.Kind == DeprecatedSyntaxFindingKind.DeprecatedSetRowcount);
    }
}
