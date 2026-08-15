using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;

namespace SilentScan.Tests.Predicates;

public sealed class ProcCallGraphBuilderTests
{
    private static (ProcCallGraph Graph, SkipLedger Ledger) BuildFrom(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors.Select(e => e.Message)));
        var catalog = CatalogBuilder.Build([result]);
        var ledger = new SkipLedger();
        return (ProcCallGraphBuilder.Build([result], catalog, ledger), ledger);
    }

    [Fact]
    public void Build_NamedArguments_MatchByFormalNameRegardlessOfCallOrder()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@A int, @B varchar(10)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @Local varchar(10) = 'x';
                EXEC dbo.Callee @B = @Local, @A = 1;
            """);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal("dbo.Caller", edge.CallerScopeQualifiedName);
        Assert.Equal("dbo.Callee", edge.CalleeQualifiedName);
        Assert.Equal(2, edge.Arguments.Count);

        var bArg = edge.Arguments.Single(a => a.FormalParameterName == "@B");
        Assert.Equal("@Local", bArg.CallerVariableName);
        Assert.False(bArg.IsLiteral);
        Assert.Equal(SqlTypeCategory.VarChar, bArg.FormalParameterType!.Category);

        var aArg = edge.Arguments.Single(a => a.FormalParameterName == "@A");
        Assert.Null(aArg.CallerVariableName);
        Assert.True(aArg.IsLiteral);
        Assert.Equal(SqlTypeCategory.Int, aArg.FormalParameterType!.Category);
    }

    [Fact]
    public void Build_PositionalArguments_MatchByDeclarationOrder()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@First int, @Second varchar(10)) AS SELECT 1;
            GO
            EXEC dbo.Callee @X, @Y;
            """);

        var edge = Assert.Single(graph.Edges);
        Assert.Null(edge.CallerScopeQualifiedName);

        Assert.Equal("@X", edge.Arguments.Single(a => a.FormalParameterName == "@First").CallerVariableName);
        Assert.Equal("@Y", edge.Arguments.Single(a => a.FormalParameterName == "@Second").CallerVariableName);
    }

    [Fact]
    public void Build_OutputParameter_IsFlagged()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Result int OUTPUT) AS SELECT 1;
            GO
            DECLARE @R int;
            EXEC dbo.Callee @R OUTPUT;
            """);

        var edge = Assert.Single(graph.Edges);
        var arg = Assert.Single(edge.Arguments);
        Assert.True(arg.FormalParameterIsOutput);
        Assert.Equal("@R", arg.CallerVariableName);
    }

    [Fact]
    public void Build_TableValuedParameterPositionallyPrecedingScalar_KeepsLaterScalarAligned()
    {
        // A TVP occupies a real positional slot even though it carries no SqlType - if it were
        // simply omitted from the callee's registered parameter list, @AfterTvp below would
        // wrongly match the TVP's own formal name instead of the real trailing scalar parameter.
        var (graph, _) = BuildFrom("""
            CREATE TYPE dbo.CodeList AS TABLE (Code varchar(20) NOT NULL);
            GO
            CREATE PROCEDURE dbo.Callee (@Codes dbo.CodeList READONLY, @After int) AS SELECT 1;
            GO
            DECLARE @Codes dbo.CodeList;
            DECLARE @Value int = 5;
            EXEC dbo.Callee @Codes, @Value;
            """);

        var edge = Assert.Single(graph.Edges);
        var afterArg = edge.Arguments.Single(a => a.FormalParameterName == "@After");
        Assert.Equal("@Value", afterArg.CallerVariableName);
        Assert.Equal(SqlTypeCategory.Int, afterArg.FormalParameterType!.Category);
    }

    [Fact]
    public void Build_UnresolvableCalleeName_RecordsNoEdgeButLedgersTheGap()
    {
        var (graph, ledger) = BuildFrom("EXEC dbo.NeverDeclared @X;");

        Assert.Empty(graph.Edges);
        Assert.Contains(ledger.Entries, e => e.ConstructKind == "procedure call graph edge" && e.Reason.Contains("dbo.NeverDeclared", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_SpExecuteSqlCall_ProducesNoEdge()
    {
        // sp_executesql is the dynamic SQL engine's own concern (a system proc, never itself
        // catalogued as a CREATE PROCEDURE) - it must never appear as a call graph edge, and
        // must not be reported as an unresolvable callee either, since it was never meant to
        // resolve against this graph in the first place.
        var (graph, ledger) = BuildFrom("EXEC sp_executesql N'SELECT 1';");

        Assert.Empty(graph.Edges);
        Assert.DoesNotContain(ledger.Entries, e => e.ConstructKind == "procedure call graph edge");
    }

    [Fact]
    public void Build_SynonymTargetingKnownProcedure_ResolvesThroughToRealCallee()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.RealProc (@A int) AS SELECT 1;
            GO
            CREATE SYNONYM dbo.ProcAlias FOR dbo.RealProc;
            GO
            EXEC dbo.ProcAlias @X;
            """);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal("dbo.RealProc", edge.CalleeQualifiedName);
    }

    [Fact]
    public void EdgesCalling_FiltersByCalleeQualifiedNameCaseInsensitively()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@A int) AS SELECT 1;
            GO
            EXEC dbo.Callee @X;
            EXEC DBO.CALLEE @Y;
            """);

        Assert.Equal(2, graph.EdgesCalling("dbo.Callee").Count());
        Assert.Empty(graph.EdgesCalling("dbo.SomeoneElse"));
    }

    [Fact]
    public void Build_CallerVariableWithSingleUnconditionalLiteralAssignment_PropagatesLiteral()
    {
        // One-level constant propagation (CLAUDE.md roadmap): DECLARE @v = 'literal' then EXEC
        // callee @v resolves the SAME as passing the literal directly would - @v is never
        // reassigned, so its value at the call site is unambiguous.
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
            BEGIN
                DECLARE @v NVARCHAR(20) = N'Active';
                EXEC dbo.Callee @v;
            END
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Equal("@v", argument.CallerVariableName);
        Assert.False(argument.IsLiteral);
        Assert.NotNull(argument.LiteralArgument);
        Assert.Equal("Active", argument.LiteralArgument!.Value);
    }

    [Fact]
    public void Build_CallerVariableReassignedInsideIf_DoesNotPropagateLiteral()
    {
        // @v's value at the call site depends on which IF branch ran - this scan has no way to
        // determine that relative to the call, so it must not guess either branch's literal.
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller @flag INT AS
            BEGIN
                DECLARE @v NVARCHAR(20) = N'Active';
                IF @flag = 1
                    SET @v = N'Archived';
                EXEC dbo.Callee @v;
            END
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Equal("@v", argument.CallerVariableName);
        Assert.Null(argument.LiteralArgument);
    }

    [Fact]
    public void Build_CallerVariableAssignedTwiceAtTopLevel_PropagatesTheLastAssignmentBeforeTheCall()
    {
        // Two top-level assignments are no longer ambiguous once WHERE the call sits relative to
        // them is known - T-SQL executes top-to-bottom, so the LAST assignment before the call
        // ('Archived') is genuinely what's in effect when the call actually runs, not a guess.
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
            BEGIN
                DECLARE @v NVARCHAR(20) = N'Active';
                SET @v = N'Archived';
                EXEC dbo.Callee @v;
            END
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Equal("Archived", argument.LiteralArgument?.Value);
    }

    [Fact]
    public void Build_CallerVariableAssignedAgainAfterTheCall_DoesNotPropagateTheLaterAssignment()
    {
        // A later assignment (after the call site) is irrelevant to what the call itself sees -
        // only the LAST assignment BEFORE the call matters, matching real T-SQL execution order.
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
            BEGIN
                DECLARE @v NVARCHAR(20) = N'Active';
                EXEC dbo.Callee @v;
                SET @v = N'Archived';
            END
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Equal("Active", argument.LiteralArgument?.Value);
    }

    [Fact]
    public void Build_CallerVariableAssignedNonLiterallyThenLiterallyBeforeTheCall_PropagatesTheLastLiteral()
    {
        // A non-literal assignment earlier in the prefix poisons the value only up to the point a
        // LATER literal assignment (still before the call) overwrites it again - the non-literal
        // write is not "sticky" once a real, later, position-proven literal supersedes it.
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller (@Seed NVARCHAR(20)) AS
            BEGIN
                DECLARE @v NVARCHAR(20) = @Seed;
                SET @v = N'Archived';
                EXEC dbo.Callee @v;
            END
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Equal("Archived", argument.LiteralArgument?.Value);
    }

    [Fact]
    public void Build_DirectIntegerLiteralArgument_PopulatesLiteralArgument()
    {
        // An integer literal's own source text (digits only) IS its canonical string form -
        // unlike a date/money/real literal, there's no formatting ambiguity to guess about, so
        // (unlike those other literal kinds) it seeds LiteralArgument directly, the same as a
        // StringLiteral already does. Real corpus shape: a bitmask/mode argument
        // (dbo.spRIL_PrintingFunction's own @ColumnControlBits/@RowControlBits) passed as a bare
        // integer literal at its one in-database call site.
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Mask int) AS SELECT 1;
            GO
            EXEC dbo.Callee 127;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.True(argument.IsLiteral);
        Assert.Equal("127", argument.LiteralArgument?.Value);
    }

    [Fact]
    public void Build_CallerVariableAssignedIntegerLiteralAtTopLevel_PropagatesTheIntegerLiteral()
    {
        // One-level constant propagation through a caller variable (already proven for a string
        // literal above) extends the same way to an integer literal assigned via DECLARE's own
        // initializer.
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Mask int) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
            BEGIN
                DECLARE @m INT = 267;
                EXEC dbo.Callee @m;
            END
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Equal("@m", argument.CallerVariableName);
        Assert.Equal("267", argument.LiteralArgument?.Value);
    }

    [Fact]
    public void Build_DirectDecimalLiteralArgument_DoesNotPopulateLiteralArgument()
    {
        // A decimal/real/money literal's own source text is NOT provably its canonical string
        // form (trailing zeros, precision, and formatting can differ from what an implicit
        // conversion to varchar actually produces) - unlike an integer literal (digits only, no
        // such ambiguity), seeding from it would be a guess this project's soundness-first rule
        // forbids, so LiteralArgument stays null even though IsLiteral is still true. (A T-SQL
        // date literal is NOT a distinct case here - ScriptDOM parses it as an ordinary
        // StringLiteral, already covered by the passing string-literal tests above.)
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@d decimal(10,2)) AS SELECT 1;
            GO
            EXEC dbo.Callee 3.140;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.True(argument.IsLiteral);
        Assert.Null(argument.LiteralArgument);
    }
}
