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
    public void Build_SpExecuteSqlCall_RecordsNoEdgeButLedgersTheGap()
    {
        var (graph, ledger) = BuildFrom("EXEC sp_executesql N'SELECT 1';");

        Assert.Empty(graph.Edges);
        Assert.Contains(ledger.Entries, e => e.ConstructKind == "procedure call graph edge" && e.Reason.Contains("sp_executesql", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DynamicStringExecCall_RecordsNoEdgeButLedgersTheGap()
    {
        var (graph, ledger) = BuildFrom("EXEC ('SELECT 1');");

        Assert.Empty(graph.Edges);
        Assert.Contains(ledger.Entries, e => e.ConstructKind == "procedure call graph edge" && e.Reason.Contains("dynamic SQL string", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ExecCallThroughProcedureNameVariable_RecordsNoEdgeButLedgersTheGap()
    {
        var (graph, ledger) = BuildFrom("""
            DECLARE @ProcName sysname = N'dbo.SomeProc';
            EXEC @ProcName;
            """);

        Assert.Empty(graph.Edges);
        Assert.Contains(ledger.Entries, e => e.ConstructKind == "procedure call graph edge" && e.Reason.Contains("variable holding the procedure name", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_NamedArgumentNotMatchingAnyFormalParameter_LedgersTheUnmatchedArgument()
    {
        var (graph, ledger) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Real int) AS SELECT 1;
            GO
            EXEC dbo.Callee @Typo = 5;
            """);

        var edge = Assert.Single(graph.Edges);
        Assert.Empty(edge.Arguments);
        Assert.Contains(
            ledger.Entries,
            e => e.ConstructKind == "procedure call graph edge" && e.Reason.Contains("@Typo", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ExcessPositionalArgument_LedgersTheUnmatchedArgument()
    {
        var (graph, ledger) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Only int) AS SELECT 1;
            GO
            EXEC dbo.Callee 1, 2;
            """);

        var edge = Assert.Single(graph.Edges);
        Assert.Single(edge.Arguments);
        Assert.Contains(
            ledger.Entries,
            e => e.ConstructKind == "procedure call graph edge" && e.Reason.Contains("positional argument", StringComparison.Ordinal));
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
    public void Build_CallerVariableAssignedInEarlierSiblingBeginEndBlock_PropagatesLiteral()
    {

        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
            BEGIN
                DECLARE @v NVARCHAR(20) = N'Active';
            END
            BEGIN
                EXEC dbo.Callee @v;
            END
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Equal("@v", argument.CallerVariableName);
        Assert.NotNull(argument.LiteralArgument);
        Assert.Equal("Active", argument.LiteralArgument!.Value);
    }

    [Fact]
    public void Build_CallerVariableAssignedInsideNestedBeginEndInsideIf_DoesNotPropagateLiteral()
    {

        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller @flag INT AS
            BEGIN
                DECLARE @v NVARCHAR(20) = N'Active';
                IF @flag = 1
                BEGIN
                    SET @v = N'Archived';
                END
                EXEC dbo.Callee @v;
            END
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Equal("@v", argument.CallerVariableName);
        Assert.Null(argument.LiteralArgument);
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
    public void Build_DirectLiteralArgument_ResolvesCallerArgumentTypeFromTheLiteralItself()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@P int) AS SELECT 1;
            GO
            EXEC dbo.Callee @P = 1;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.NotNull(argument.CallerArgumentType);
        Assert.Equal(SqlTypeCategory.Int, argument.CallerArgumentType!.Category);
    }

    [Fact]
    public void Build_NegativeDecimalLiteralArgument_ResolvesCallerArgumentType()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@P decimal(4,1)) AS SELECT 1;
            GO
            EXEC dbo.Callee @P = -123.456;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Null(argument.CallerVariableName);
        Assert.NotNull(argument.CallerArgumentType);
        Assert.Equal(SqlTypeCategory.Decimal, argument.CallerArgumentType!.Category);
    }

    [Fact]
    public void RealCallerCalleePair_LiteralArgumentNarrowing_ArgumentMismatchScannerFires()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Code varchar(3)) AS SELECT @Code;
            GO
            CREATE PROCEDURE dbo.Caller AS
                EXEC dbo.Callee @Code = 'abcdef';
            """);

        var findings = ProcCallArgumentMismatchScanner.Scan(graph);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.LengthTruncation, finding.Kind);
        Assert.Equal("'abcdef'", finding.CallerExpressionDisplay);
    }

    [Fact]
    public void RealCallerCalleePair_DecimalLiteralArgumentScaleNarrowing_ArgumentMismatchScannerFires()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Amount decimal(4,1)) AS SELECT @Amount;
            GO
            CREATE PROCEDURE dbo.Caller AS
                EXEC dbo.Callee @Amount = 123.456;
            """);

        var findings = ProcCallArgumentMismatchScanner.Scan(graph);

        var finding = Assert.Single(findings);
        Assert.Equal(WriteLossKind.NumericScaleNarrowing, finding.Kind);
        Assert.Equal("123.456", finding.CallerExpressionDisplay);
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
    public void Build_TopLevelDeclareInEarlierBatch_DoesNotLeakVariableTypeIntoLaterBatch()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@P int) AS SELECT 1;
            GO
            DECLARE @Shared varchar(20) = 'x';
            SELECT @Shared;
            GO
            EXEC dbo.Callee @Shared;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = Assert.Single(edge.Arguments);
        Assert.Null(argument.CallerArgumentType);
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

    [Fact]
    public void Build_AlterProcedure_ProducesEdge()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@A int) AS SELECT 1;
            GO
            ALTER PROCEDURE dbo.Caller AS EXEC dbo.Callee @A = 1;
            """);

        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Build_CreateOrAlterProcedure_ProducesEdge()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@A int) AS SELECT 1;
            GO
            CREATE OR ALTER PROCEDURE dbo.Caller AS EXEC dbo.Callee @A = 1;
            """);

        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Build_CreateFunction_ProducesEdge()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@A int) AS SELECT 1;
            GO
            CREATE FUNCTION dbo.Caller() RETURNS INT AS
            BEGIN
                EXEC dbo.Callee @A = 1;
                RETURN 1;
            END
            """);

        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Build_AlterFunction_ProducesEdge()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@A int) AS SELECT 1;
            GO
            ALTER FUNCTION dbo.Caller() RETURNS INT AS
            BEGIN
                EXEC dbo.Callee @A = 1;
                RETURN 1;
            END
            """);

        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Build_CreateOrAlterFunction_ProducesEdge()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@A int) AS SELECT 1;
            GO
            CREATE OR ALTER FUNCTION dbo.Caller() RETURNS INT AS
            BEGIN
                EXEC dbo.Callee @A = 1;
                RETURN 1;
            END
            """);

        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Build_CreateTrigger_ProducesEdge()
    {
        var (graph, _) = BuildFrom("""
            CREATE TABLE dbo.T (Id INT);
            GO
            CREATE PROCEDURE dbo.Callee (@A int) AS SELECT 1;
            GO
            CREATE TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS EXEC dbo.Callee @A = 1;
            """);

        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Build_AlterTrigger_ProducesEdge()
    {
        var (graph, _) = BuildFrom("""
            CREATE TABLE dbo.T (Id INT);
            GO
            CREATE PROCEDURE dbo.Callee (@A int) AS SELECT 1;
            GO
            ALTER TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS EXEC dbo.Callee @A = 1;
            """);

        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Build_CreateOrAlterTrigger_ProducesEdge()
    {
        var (graph, _) = BuildFrom("""
            CREATE TABLE dbo.T (Id INT);
            GO
            CREATE PROCEDURE dbo.Callee (@A int) AS SELECT 1;
            GO
            CREATE OR ALTER TRIGGER dbo.trg_T ON dbo.T AFTER INSERT AS EXEC dbo.Callee @A = 1;
            """);

        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Build_TopLevelDeclareBeforeProcedure_RestoresPriorVariableTypesAfterScope()
    {
        var (graph, _) = BuildFrom("""
            DECLARE @g INT = 1;
            GO
            CREATE PROCEDURE dbo.Callee (@X int) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS EXEC dbo.Callee @X = 1;
            """);

        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Build_NamedArgumentNotMatchingAnyFormalParameter_IsSkipped()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@A int) AS SELECT 1;
            GO
            EXEC dbo.Callee @NoSuchParam = 1;
            """);

        var edge = Assert.Single(graph.Edges);
        Assert.Empty(edge.Arguments);
    }

    [Fact]
    public void Build_UnrelatedPrintStatementBeforeCall_IsSkippedDuringLiteralPropagation()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @Status nvarchar(20) = 'PENDING';
                PRINT 'noop';
                EXEC dbo.Callee @Status = @Status;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.Equal("PENDING", argument.LiteralArgument!.Value);
    }

    [Fact]
    public void Build_UnrelatedDeclareOfDifferentVariableBeforeCall_IsSkippedDuringLiteralPropagation()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @Status nvarchar(20) = 'PENDING';
                DECLARE @Other INT = 1;
                EXEC dbo.Callee @Status = @Status;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.Equal("PENDING", argument.LiteralArgument!.Value);
    }

    [Fact]
    public void Build_CallerVariableAssignedInsideWhileLoop_DoesNotPropagateLiteral()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @Status nvarchar(20);
                WHILE 1 = 0
                BEGIN
                    SET @Status = 'PENDING';
                END
                EXEC dbo.Callee @Status = @Status;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.Null(argument.LiteralArgument);
    }

    [Fact]
    public void Build_CallerVariableAssignedInsideTryCatch_DoesNotPropagateLiteral()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @Status nvarchar(20);
                BEGIN TRY
                    SET @Status = 'PENDING';
                END TRY
                BEGIN CATCH
                END CATCH
                EXEC dbo.Callee @Status = @Status;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.Null(argument.LiteralArgument);
    }

    [Fact]
    public void Build_CallerVariableDeclaredWithoutInitializerAndNeverWritten_WasNotAssignedBeforeCall()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20) OUTPUT) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @v NVARCHAR(20);
                EXEC dbo.Callee @v OUTPUT;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.False(argument.CallerVariableWasAssignedBeforeCall);
    }

    [Fact]
    public void Build_CallerVariableDeclaredWithInitializer_WasAssignedBeforeCall()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20) OUTPUT) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @v NVARCHAR(20) = N'x';
                EXEC dbo.Callee @v OUTPUT;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.True(argument.CallerVariableWasAssignedBeforeCall);
    }

    [Fact]
    public void Build_CallerVariableConditionallyWrittenBeforeCall_WasAssignedBeforeCall()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20) OUTPUT) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller @flag INT AS
                DECLARE @v NVARCHAR(20);
                IF @flag = 1
                    SET @v = N'Archived';
                EXEC dbo.Callee @v OUTPUT;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.True(argument.CallerVariableWasAssignedBeforeCall);
    }

    [Fact]
    public void Build_CallerVariableIsEnclosingFormalParameter_WasAssignedBeforeCall()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20) OUTPUT) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller (@Outer nvarchar(20)) AS
                EXEC dbo.Callee @Outer OUTPUT;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.True(argument.CallerVariableWasAssignedBeforeCall);
    }

    [Fact]
    public void Build_CallerVariableWrittenAfterCallOnly_WasNotAssignedBeforeCall()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20) OUTPUT) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @v NVARCHAR(20);
                EXEC dbo.Callee @v OUTPUT;
                SET @v = N'Archived';
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.False(argument.CallerVariableWasAssignedBeforeCall);
    }

    [Fact]
    public void Build_CallerVariableDeclaredInsideConditional_DoesNotPropagateLiteral()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20)) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                IF 1 = 0
                BEGIN
                    DECLARE @Status nvarchar(20) = 'PENDING';
                END
                EXEC dbo.Callee @Status = @Status;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.Null(argument.LiteralArgument);
    }

    [Fact]
    public void Build_CallerVariableAssignedViaSelectSetVariable_WasAssignedBeforeCall()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20) OUTPUT) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @v NVARCHAR(20);
                SELECT @v = N'Archived';
                EXEC dbo.Callee @v OUTPUT;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.True(argument.CallerVariableWasAssignedBeforeCall);
    }

    [Fact]
    public void Build_CallerVariableAssignedViaFetchInto_WasAssignedBeforeCall()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20) OUTPUT) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @v NVARCHAR(20);
                DECLARE cur CURSOR FOR SELECT N'x';
                OPEN cur;
                FETCH NEXT FROM cur INTO @v;
                EXEC dbo.Callee @v OUTPUT;
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.True(argument.CallerVariableWasAssignedBeforeCall);
    }

    [Fact]
    public void Build_CallerVariableAssignedViaExecOutputEarlier_WasAssignedBeforeCall()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20) OUTPUT) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Other (@Status nvarchar(20) OUTPUT) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller AS
                DECLARE @v NVARCHAR(20);
                EXEC dbo.Other @v OUTPUT;
                EXEC dbo.Callee @v OUTPUT;
            """);

        var edge = graph.Edges.Single(e => e.CalleeQualifiedName == "dbo.Callee");
        var argument = edge.Arguments.Single();
        Assert.True(argument.CallerVariableWasAssignedBeforeCall);
    }

    [Fact]
    public void Build_CallerVariableWrittenEarlierInSameIfBranchAsCall_WasAssignedBeforeCall()
    {
        var (graph, _) = BuildFrom("""
            CREATE PROCEDURE dbo.Callee (@Status nvarchar(20) OUTPUT) AS SELECT 1;
            GO
            CREATE PROCEDURE dbo.Caller @flag INT AS
                DECLARE @v NVARCHAR(20);
                IF @flag = 1
                BEGIN
                    SET @v = N'Archived';
                    EXEC dbo.Callee @v OUTPUT;
                END
            """);

        var edge = Assert.Single(graph.Edges);
        var argument = edge.Arguments.Single();
        Assert.True(argument.CallerVariableWasAssignedBeforeCall);
    }
}
