using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.TypeInference;

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

    [Fact]
    public void Build_CallerVariableDeclaredWithType_ResolvesCallerArgumentType()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@P nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @Local varchar(20) = 'x';
                EXEC dbo.Callee @Local;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Equal("@Local", argument.CallerVariableName);
        Assert.NotNull(argument.CallerArgumentType);
        Assert.Equal(SqlTypeCategory.VarChar, argument.CallerArgumentType!.Category);
    }

    [Fact]
    public void Build_CallerVariableIsCallersOwnParameter_ResolvesCallerArgumentTypeFromEnclosingScope()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@P int) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller (@Outer int) AS
                EXEC dbo.Callee @Outer;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Equal("@Outer", argument.CallerVariableName);
        Assert.NotNull(argument.CallerArgumentType);
        Assert.Equal(SqlTypeCategory.Int, argument.CallerArgumentType!.Category);
    }

    [Fact]
    public void Build_CallerVariableUndeclared_CallerArgumentTypeStaysNull()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@P int) AS SELECT 1;
            GO
            EXEC dbo.Callee @Undeclared;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Null(argument.CallerArgumentType);
    }

    [Fact]
    public void Build_ScopeChange_DoesNotLeakVariableTypesAcrossProcedures()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@P int) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Other AS
                DECLARE @Local varchar(20) = 'x';
                SELECT @Local;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @Local int = 1;
                EXEC dbo.Callee @Local;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Equal(SqlTypeCategory.Int, argument.CallerArgumentType!.Category);
    }

    [Fact]
    public void RealCallerCalleePair_MatchingDeclaredTypes_ArgumentMismatchScannerNeverFires()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Code varchar(20)) AS SELECT @Code;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @LocalCode varchar(20) = 'abc';
                EXEC dbo.Callee @LocalCode;
            """);

        var findings = ProcCallArgumentMismatchScanner.Scan(graph);

        Assert.Empty(findings);
    }
}
