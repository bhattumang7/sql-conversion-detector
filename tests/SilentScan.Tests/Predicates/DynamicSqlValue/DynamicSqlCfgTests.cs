using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;
using SilentScan.Core.TypeInference;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

public sealed class DynamicSqlCfgTests
{
    private const int Cap = 32;
    private static readonly SqlType NVarCharMax = new(SqlTypeCategory.NVarChar, IsMax: true);

    private static IList<TSqlStatement> ParseStatements(string sql)
    {
        var result = SqlScriptParser.ParseText("test.sql", sql);
        Assert.False(result.HasErrors, string.Join(';', result.Errors.Select(e => e.Message)));
        var script = Assert.IsType<TSqlScript>(result.Fragment);
        return script.Batches[0].Statements;
    }

    private static Action<Dictionary<string, SqlTextValue>, bool> CompileLeaf(TSqlStatement statement, List<string> emittedLog) => (state, emit) =>
    {
        if (emit)
        {
            emittedLog.Add(statement.GetType().Name);
        }

        if (statement is SetVariableStatement { Variable.Name: var name, Expression: StringLiteral literal })
        {
            state[name] = new SqlTextValue.Template([new TemplatePiece.Lit(literal.Value, new SourceSpan("test.sql", literal.StartLine, literal.StartColumn), PrefixLength: 1)]) { DeclaredType = NVarCharMax };
        }
    };

    private static string LitText(SqlTextValue value)
    {
        var template = Assert.IsType<SqlTextValue.Template>(value);
        var lit = Assert.IsType<TemplatePiece.Lit>(Assert.Single(template.Pieces));
        return lit.Text;
    }

    [Fact]
    public void StraightLineStatements_RunInOrder()
    {
        var statements = ParseStatements("SET @x = 'a'; SET @x = 'b'; SET @x = 'c';");
        var emitted = new List<string>();
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, emitted));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("c", LitText(result["@x"]));
        Assert.Equal(3, emitted.Count);
    }

    [Fact]
    public void EmissionIsSuppressedDuringFixpoint_AndRunsOnceInFinalPass()
    {
        var statements = ParseStatements("SET @x = 'a';");
        var emitted = new List<string>();
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, emitted));

        cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["SetVariableStatement"], emitted);
    }

    [Fact]
    public void IfWithoutElse_JoinsToOriginalValueOnFalseBranch()
    {
        var statements = ParseStatements("SET @x = 'a'; IF 1 = 1 BEGIN SET @x = 'b'; END SET @y = 'after';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        Assert.Equal(2, choice.Alternatives.Count);
        Assert.Equal("after", LitText(result["@y"]));
    }

    [Fact]
    public void IfConditionOnCallerSeededIntegerVariable_PrunesToTakenBranchInsteadOfMerging()
    {
        var statements = ParseStatements("IF @Flag = 1 BEGIN SET @x = 'then'; END ELSE BEGIN SET @x = 'else'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "@Flag" });

        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@Flag"] = new SqlTextValue.Template([new TemplatePiece.Lit("1", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
        };

        var result = cfg.Solve(statements, seed);

        Assert.Equal("then", LitText(result["@x"]));
    }

    [Fact]
    public void IfConditionOnCallerSeededIntegerVariable_FalseBranchPrunesToElseValue()
    {
        var statements = ParseStatements("IF @Flag = 1 BEGIN SET @x = 'then'; END ELSE BEGIN SET @x = 'else'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "@Flag" });

        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@Flag"] = new SqlTextValue.Template([new TemplatePiece.Lit("2", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
        };

        var result = cfg.Solve(statements, seed);

        Assert.Equal("else", LitText(result["@x"]));
    }

    [Fact]
    public void IfConditionOnUnseededVariable_DoesNotPrune_StillMergesBothBranches()
    {
        var statements = ParseStatements("SET @Flag = 1; IF @Flag = 1 BEGIN SET @x = 'then'; END ELSE BEGIN SET @x = 'else'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        Assert.Equal(2, choice.Alternatives.Count);
    }

    [Fact]
    public void ParenthesizedConditionOnCallerSeededVariable_PrunesLikeUnparenthesized()
    {
        var statements = ParseStatements("IF (@Flag = 1) BEGIN SET @x = 'then'; END ELSE BEGIN SET @x = 'else'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "@Flag" });

        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@Flag"] = new SqlTextValue.Template([new TemplatePiece.Lit("1", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
        };

        var result = cfg.Solve(statements, seed);

        Assert.Equal("then", LitText(result["@x"]));
    }

    [Fact]
    public void NegatedConditionOnCallerSeededVariable_PrunesToOppositeBranch()
    {
        var statements = ParseStatements("IF NOT (@Flag = 1) BEGIN SET @x = 'then'; END ELSE BEGIN SET @x = 'else'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "@Flag" });

        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@Flag"] = new SqlTextValue.Template([new TemplatePiece.Lit("1", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
        };

        var result = cfg.Solve(statements, seed);

        Assert.Equal("else", LitText(result["@x"]));
    }

    [Fact]
    public void AndConditionWithBothCallerSeededVariablesTrue_PrunesToTakenBranch()
    {
        var statements = ParseStatements("IF @Flag = 1 AND @Other = 2 BEGIN SET @x = 'then'; END ELSE BEGIN SET @x = 'else'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "@Flag", "@Other" });

        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@Flag"] = new SqlTextValue.Template([new TemplatePiece.Lit("1", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
            ["@Other"] = new SqlTextValue.Template([new TemplatePiece.Lit("2", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
        };

        var result = cfg.Solve(statements, seed);

        Assert.Equal("then", LitText(result["@x"]));
    }

    [Fact]
    public void AndConditionWithOneCallerSeededVariableFalse_PrunesToElseBranch()
    {
        var statements = ParseStatements("IF @Flag = 1 AND @Other = 2 BEGIN SET @x = 'then'; END ELSE BEGIN SET @x = 'else'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "@Flag", "@Other" });

        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@Flag"] = new SqlTextValue.Template([new TemplatePiece.Lit("1", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
            ["@Other"] = new SqlTextValue.Template([new TemplatePiece.Lit("99", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
        };

        var result = cfg.Solve(statements, seed);

        Assert.Equal("else", LitText(result["@x"]));
    }

    [Fact]
    public void OrConditionWithOneCallerSeededVariableTrue_PrunesToTakenBranch()
    {
        var statements = ParseStatements("IF @Flag = 1 OR @Other = 2 BEGIN SET @x = 'then'; END ELSE BEGIN SET @x = 'else'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "@Flag", "@Other" });

        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@Flag"] = new SqlTextValue.Template([new TemplatePiece.Lit("1", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
            ["@Other"] = new SqlTextValue.Template([new TemplatePiece.Lit("99", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
        };

        var result = cfg.Solve(statements, seed);

        Assert.Equal("then", LitText(result["@x"]));
    }

    [Fact]
    public void OrConditionWithBothCallerSeededVariablesFalse_PrunesToElseBranch()
    {
        var statements = ParseStatements("IF @Flag = 1 OR @Other = 2 BEGIN SET @x = 'then'; END ELSE BEGIN SET @x = 'else'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "@Flag", "@Other" });

        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@Flag"] = new SqlTextValue.Template([new TemplatePiece.Lit("9", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
            ["@Other"] = new SqlTextValue.Template([new TemplatePiece.Lit("99", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
        };

        var result = cfg.Solve(statements, seed);

        Assert.Equal("else", LitText(result["@x"]));
    }

    [Theory]
    [InlineData("<>")]
    [InlineData("!=")]
    [InlineData(">")]
    [InlineData(">=")]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData("!>")]
    [InlineData("!<")]
    public void ComparisonOperatorOnCallerSeededVariable_FoldsToTrue_AndPrunesToTakenBranch(string comparisonOperator)
    {
        var rhs = comparisonOperator switch
        {
            "<>" or "!=" => "2",
            ">" => "2",
            ">=" => "5",
            "<" => "10",
            "<=" => "5",
            "!>" => "10",
            "!<" => "2",
            _ => throw new InvalidOperationException(),
        };
        var statements = ParseStatements($"IF @Flag {comparisonOperator} {rhs} BEGIN SET @x = 'then'; END ELSE BEGIN SET @x = 'else'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "@Flag" });

        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@Flag"] = new SqlTextValue.Template([new TemplatePiece.Lit("5", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
        };

        var result = cfg.Solve(statements, seed);

        Assert.Equal("then", LitText(result["@x"]));
    }

    [Fact]
    public void IfWithoutElse_ConditionFoldsFalse_HasNoElsePredecessorToPruneTo_StillContinuesNormally()
    {
        var statements = ParseStatements("IF @Flag = 1 BEGIN SET @x = 'then'; END SET @y = 'after';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "@Flag" });

        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@Flag"] = new SqlTextValue.Template([new TemplatePiece.Lit("2", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
        };

        var result = cfg.Solve(statements, seed);

        Assert.Equal("after", LitText(result["@y"]));
    }

    [Fact]
    public void IfConditionIsAnUnrecognizedShapeOnCallerSeededVariable_DoesNotFold_StillMergesBothBranches()
    {
        var statements = ParseStatements("IF @Flag IS NULL BEGIN SET @x = 'then'; END ELSE BEGIN SET @x = 'else'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "@Flag" });

        var seed = new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["@Flag"] = new SqlTextValue.Template([new TemplatePiece.Lit("1", new SourceSpan("test.sql", 1, 1), PrefixLength: 1)]) { DeclaredType = NVarCharMax },
        };

        var result = cfg.Solve(statements, seed);

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        Assert.Equal(2, choice.Alternatives.Count);
    }

    [Fact]
    public void SelfTrimShapeGuardedByNonMatchingLenComparison_DoesNotRecognizeGuard_TaintsInstead()
    {
        var statements = ParseStatements("SET @sql = 'a,b,c,'; IF LEN(@sql) > 5 SET @sql = SUBSTRING(@sql, 1, LEN(@sql) - 1);");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeafWithTaintOnUnrecognizedSet(s));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.IsType<SqlTextValue.Tainted>(result["@sql"]);
    }

    private static Action<Dictionary<string, SqlTextValue>, bool> CompileLeafWithTaintOnUnrecognizedSet(TSqlStatement statement) => (state, emit) =>
    {
        if (statement is SetVariableStatement { Variable.Name: var litName, Expression: StringLiteral literal })
        {
            state[litName] = new SqlTextValue.Template([new TemplatePiece.Lit(literal.Value, new SourceSpan("test.sql", literal.StartLine, literal.StartColumn), PrefixLength: 1)]) { DeclaredType = NVarCharMax };
        }
        else if (statement is SetVariableStatement { Variable.Name: var taintedName })
        {
            state[taintedName] = new SqlTextValue.Tainted("unrecognized", new SourceSpan("test.sql", statement.StartLine, statement.StartColumn));
        }
    };

    [Fact]
    public void RecognizedTrailingCommaTrimGuardedBySelfLen_RestoresPriorValueInsteadOfTainting()
    {
        var statements = ParseStatements("SET @sql = 'a,b,c,'; IF LEN(@sql) > 0 SET @sql = SUBSTRING(@sql, 1, LEN(@sql) - 1);");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeafWithTaintOnUnrecognizedSet(s));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("a,b,c,", LitText(result["@sql"]));
    }

    [Fact]
    public void EqualityGuardedReturnNarrowsVariableToTheGuardLiteralAfterTheIf()
    {
        var statements = ParseStatements("SET @x = 'default'; IF @x <> 'y' BEGIN RETURN; END SET @after = 'reached';");
        string? observedXAtAfter = null;

        Action<Dictionary<string, SqlTextValue>, bool> CompileLeafObservingX(TSqlStatement statement) => (state, emit) =>
        {
            if (statement is SetVariableStatement { Variable.Name: "@after" } afterStatement)
            {
                if (emit && state.TryGetValue("@x", out var xValue))
                {
                    observedXAtAfter = LitText(xValue);
                }

                state["@after"] = new SqlTextValue.Template([new TemplatePiece.Lit("reached", new SourceSpan("test.sql", afterStatement.StartLine, afterStatement.StartColumn), PrefixLength: 1)]) { DeclaredType = NVarCharMax };
                return;
            }

            if (statement is SetVariableStatement { Variable.Name: var name, Expression: StringLiteral literal })
            {
                state[name] = new SqlTextValue.Template([new TemplatePiece.Lit(literal.Value, new SourceSpan("test.sql", literal.StartLine, literal.StartColumn), PrefixLength: 1)]) { DeclaredType = NVarCharMax };
            }
        };

        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeafObservingX(s));

        cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("y", observedXAtAfter);
    }

    [Fact]
    public void LiteralFirstEqualityGuardedReturnNarrowsVariableToTheGuardLiteralAfterTheIf()
    {
        var statements = ParseStatements("SET @x = 'default'; IF 'y' <> @x BEGIN RETURN; END SET @after = 'reached';");
        string? observedXAtAfter = null;

        Action<Dictionary<string, SqlTextValue>, bool> CompileLeafObservingX(TSqlStatement statement) => (state, emit) =>
        {
            if (statement is SetVariableStatement { Variable.Name: "@after" } afterStatement)
            {
                if (emit && state.TryGetValue("@x", out var xValue))
                {
                    observedXAtAfter = LitText(xValue);
                }

                state["@after"] = new SqlTextValue.Template([new TemplatePiece.Lit("reached", new SourceSpan("test.sql", afterStatement.StartLine, afterStatement.StartColumn), PrefixLength: 1)]) { DeclaredType = NVarCharMax };
                return;
            }

            if (statement is SetVariableStatement { Variable.Name: var name, Expression: StringLiteral literal })
            {
                state[name] = new SqlTextValue.Template([new TemplatePiece.Lit(literal.Value, new SourceSpan("test.sql", literal.StartLine, literal.StartColumn), PrefixLength: 1)]) { DeclaredType = NVarCharMax };
            }
        };

        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeafObservingX(s));

        cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("y", observedXAtAfter);
    }

    [Fact]
    public void NeitherSideOfNotEqualGuardIsSelfLiteralShape_DoesNotNarrow_StillMergesAfterTheIf()
    {
        var statements = ParseStatements("SET @x = 'default'; IF @x <> @other BEGIN RETURN; END SET @after = 'reached';");
        string? observedXAtAfter = null;

        Action<Dictionary<string, SqlTextValue>, bool> CompileLeafObservingX(TSqlStatement statement) => (state, emit) =>
        {
            if (statement is SetVariableStatement { Variable.Name: "@after" } afterStatement)
            {
                if (emit && state.TryGetValue("@x", out var xValue))
                {
                    observedXAtAfter = LitText(xValue);
                }

                state["@after"] = new SqlTextValue.Template([new TemplatePiece.Lit("reached", new SourceSpan("test.sql", afterStatement.StartLine, afterStatement.StartColumn), PrefixLength: 1)]) { DeclaredType = NVarCharMax };
                return;
            }

            if (statement is SetVariableStatement { Variable.Name: var name, Expression: StringLiteral literal })
            {
                state[name] = new SqlTextValue.Template([new TemplatePiece.Lit(literal.Value, new SourceSpan("test.sql", literal.StartLine, literal.StartColumn), PrefixLength: 1)]) { DeclaredType = NVarCharMax };
            }
        };

        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeafObservingX(s));

        cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("default", observedXAtAfter);
    }

    [Fact]
    public void RecognizedTrailingCommaTrimGuardedBySelfLenGreaterThanOrEqualToOne_RestoresPriorValueInsteadOfTainting()
    {
        var statements = ParseStatements("SET @sql = 'a,b,c,'; IF LEN(@sql) >= 1 SET @sql = SUBSTRING(@sql, 1, LEN(@sql) - 1);");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeafWithTaintOnUnrecognizedSet(s));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("a,b,c,", LitText(result["@sql"]));
    }

    [Fact]
    public void BreakInsideWhileLoop_ExitsLoopWithoutRepeatingBody()
    {
        var statements = ParseStatements("SET @x = 'start'; WHILE 1 = 1 BEGIN SET @x = 'looped'; BREAK; END SET @y = 'after';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("after", LitText(result["@y"]));
    }

    [Fact]
    public void ContinueInsideWhileLoop_JumpsBackToLoopHeaderInsteadOfFallingThrough()
    {
        var statements = ParseStatements("SET @x = 'start'; WHILE @i < 10 BEGIN IF @i = 1 CONTINUE; SET @x = 'looped'; SET @i = @i + 1; END SET @y = 'after';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("after", LitText(result["@y"]));
    }

    [Fact]
    public void IfStatement_VariableUntouchedByEitherBranch_MergesToItsOwnUnchangedValue()
    {
        var statements = ParseStatements("SET @x = 'unchanged'; IF 1 = 1 BEGIN SET @y = 'then-only'; END ELSE BEGIN SET @z = 'else-only'; END");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("unchanged", LitText(result["@x"]));
    }

    [Fact]
    public void SameGuardTestedTwice_MergesAlternativesInsteadOfNesting()
    {
        var statements = ParseStatements(
            "SET @x = 'base'; " +
            "IF @flag = 1 SET @x = 'first'; " +
            "IF @flag = 1 SET @x = 'second';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        Assert.True(choice.Alternatives.Count <= 3);
        Assert.All(choice.Alternatives, alt => Assert.IsType<TemplatePiece.Lit>(Assert.Single(alt.Pieces)));
    }

    [Fact]
    public void WhileLoop_FixpointConverges_WithoutHittingMaxRounds()
    {
        var statements = ParseStatements("SET @x = 'start'; WHILE @i < 10 BEGIN SET @x = 'looped'; SET @i = @i + 1; END SET @y = 'after';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        Assert.Equal(2, choice.Alternatives.Count);
        Assert.Equal("after", LitText(result["@y"]));
    }

    [Fact]
    public void TryCatch_CatchStartsFromPreTryState_NotFromInsideTry()
    {
        var statements = ParseStatements(
            "SET @x = 'before'; " +
            "BEGIN TRY SET @x = 'in-try'; END TRY " +
            "BEGIN CATCH SET @y = 'in-catch'; END CATCH");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        var texts = choice.Alternatives.Select(LitText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["before", "in-try"], texts);
    }

    [Fact]
    public void TryCatch_VariableDeclaredOnlyInTry_IsVisibleInCatchAsTypedHole()
    {

        var statements = ParseStatements(
            "BEGIN TRY " +
            "DECLARE @errorContext NVARCHAR(200); " +
            "END TRY " +
            "BEGIN CATCH " +
            "SET @y = @errorContext; " +
            "END CATCH");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.True(result.ContainsKey("@errorContext"));
        var template = Assert.IsType<SqlTextValue.Template>(result["@errorContext"]);
        var hole = Assert.IsType<TemplatePiece.Hole>(Assert.Single(template.Pieces));
        Assert.Equal(HoleKind.TryOnlyDeclaration, hole.Kind);
    }

    [Fact]
    public void Goto_SkipsInterveningStatement()
    {
        var statements = ParseStatements(
            "SET @x = 'start'; " +
            "GOTO skip; " +
            "SET @x = 'skipped-over'; " +
            "skip: " +
            "SET @y = 'reached';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("start", LitText(result["@x"]));
        Assert.Equal("reached", LitText(result["@y"]));
    }

    [Fact]
    public void ReturnStatement_ContributesItsOwnStateToTheFinalMergedState()
    {

        var statements = ParseStatements("SET @x = 'a'; IF 1 = 1 BEGIN SET @x = 'returned'; RETURN; END SET @x = 'fallthrough';");
        var cfg = new DynamicSqlCfg("test.sql", Cap, (s, _) => CompileLeaf(s, []));

        var result = cfg.Solve(statements, new Dictionary<string, SqlTextValue>(StringComparer.OrdinalIgnoreCase));

        var xTemplate = Assert.IsType<SqlTextValue.Template>(result["@x"]);
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(xTemplate.Pieces));
        var texts = choice.Alternatives.Select(LitText).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["fallthrough", "returned"], texts);
    }
}
