using SilentScan.Core.Catalog;
using SilentScan.Core.Predicates;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

/// <summary>
/// Exercises the <see cref="SqlTextValue"/> lattice operations directly - the replacement for
/// the old scanner's <c>FoldState</c>/<c>LiteralSegment</c> model (docs/dynamic-sql-rebuild-plan.md
/// Phase 1). Each test targets one property the design depends on: <see cref="SqlTextValue.Concat"/>
/// never distributes a Choice, <see cref="SqlTextValue.Join"/> only ever recovers a type from each
/// side's own declared type, <see cref="SqlTextValue.Widen"/> is idempotent and only ever loses
/// precision, and <see cref="SqlTextValue.Expand"/> produces the exact cartesian product.
/// </summary>
public sealed class SqlTextValueTests
{
    private static readonly SourceSpan Origin = new("test.sql", 1, 1);
    private static readonly SqlType NVarChar50 = new(SqlTypeCategory.NVarChar, Length: 50);
    private static readonly SqlType Int = new(SqlTypeCategory.Int);

    private static SqlTextValue.Template Lit(string text) => new([new TemplatePiece.Lit(text, Origin, PrefixLength: 1)]);

    private static SqlTextValue.Template Hole(SqlType type, HoleKind kind = HoleKind.UntypedParameter) =>
        new([new TemplatePiece.Hole(type, Origin, kind)]);

    [Fact]
    public void Concat_TwoLiterals_AppendsPiecesInOrder()
    {
        var result = (SqlTextValue.Template)SqlTextValue.Concat(Lit("SELECT "), Lit("* FROM T"));

        Assert.Equal(2, result.Pieces.Count);
        Assert.Equal("SELECT ", ((TemplatePiece.Lit)result.Pieces[0]).Text);
        Assert.Equal("* FROM T", ((TemplatePiece.Lit)result.Pieces[1]).Text);
    }

    [Fact]
    public void Concat_Associative_ProducesIdenticalPieceSequence()
    {
        var a = Lit("A");
        var b = Lit("B");
        var c = Lit("C");

        var leftFirst = (SqlTextValue.Template)SqlTextValue.Concat(SqlTextValue.Concat(a, b), c);
        var rightFirst = (SqlTextValue.Template)SqlTextValue.Concat(a, SqlTextValue.Concat(b, c));

        Assert.True(SqlTextValue.StructurallyEqual(leftFirst, rightFirst));
    }

    [Fact]
    public void Concat_TaintedLeftOperand_AbsorbsAndKeepsItsOwnReason()
    {
        var tainted = new SqlTextValue.Tainted("non-literal-expression", Origin);

        var result = SqlTextValue.Concat(tainted, Lit("X"));

        var resultTainted = Assert.IsType<SqlTextValue.Tainted>(result);
        Assert.Equal("non-literal-expression", resultTainted.Reason);
    }

    [Fact]
    public void Concat_TaintedRightOperand_Absorbs()
    {
        var tainted = new SqlTextValue.Tainted("non-literal-expression", Origin);

        var result = SqlTextValue.Concat(Lit("X"), tainted);

        Assert.IsType<SqlTextValue.Tainted>(result);
    }

    [Fact]
    public void Concat_TwoTemplatesWithChoicePiece_NeverDistributesTheChoice()
    {
        var choice = new SqlTextValue.Template([new TemplatePiece.Choice("guard", [Lit("A"), Lit("B")])]);

        var result = (SqlTextValue.Template)SqlTextValue.Concat(choice, Lit("TAIL"));

        // Two pieces: the Choice, kept whole, followed by the literal - never expanded to two
        // separate "A TAIL" / "B TAIL" templates at this stage.
        Assert.Equal(2, result.Pieces.Count);
        Assert.IsType<TemplatePiece.Choice>(result.Pieces[0]);
    }

    [Fact]
    public void Join_StructurallyEqualValues_ReturnsFirstOperandUnchanged()
    {
        var a = Lit("SELECT 1");
        var b = Lit("SELECT 1");

        var result = SqlTextValue.Join(a, b, "guard", cap: 32, at: Origin);

        Assert.True(SqlTextValue.StructurallyEqual(a, result));
    }

    [Fact]
    public void Join_TwoDifferentTemplates_ProducesSinglePieceChoice()
    {
        var a = Lit("A");
        var b = Lit("B");

        var result = (SqlTextValue.Template)SqlTextValue.Join(a, b, "IF @x IS NOT NULL", cap: 32, at: Origin);

        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(result.Pieces));
        Assert.Equal("IF @x IS NOT NULL", choice.GuardText);
        Assert.Equal(2, choice.Alternatives.Count);
    }

    [Fact]
    public void Join_SameGuardTwice_MergesIntoOneChoiceInsteadOfNesting()
    {
        var a = Lit("A");
        var b = Lit("B");
        var c = Lit("C");

        var firstJoin = SqlTextValue.Join(a, b, "guard", cap: 32, at: Origin);
        var secondJoin = SqlTextValue.Join(firstJoin, c, "guard", cap: 32, at: Origin);

        var template = (SqlTextValue.Template)secondJoin;
        var choice = Assert.IsType<TemplatePiece.Choice>(Assert.Single(template.Pieces));
        Assert.Equal(3, choice.Alternatives.Count); // A, B, C - not [[A, B], C] nested
    }

    /// <summary>
    /// The generic <see cref="SqlTextValue.DivergesInControlFlowGraphReason"/> sentinel is a LAST
    /// resort, never the default: whenever exactly one side is already <see cref="SqlTextValue.Tainted"/>
    /// with its own specific reason, that reason survives unchanged (a real cause, more useful than
    /// "diverges"), and the OTHER side's known value survives too, as a <see cref="GuardedAlternative"/>
    /// under <c>guardText</c> - a later consumer that independently proves that branch is the one in
    /// play can still recover real text instead of an unresolvable placeholder.
    /// </summary>
    [Fact]
    public void Join_TemplateAndTainted_WithNoDeclaredType_PreservesReasonAndKeepsKnownSideAsAlternative()
    {
        var template = Lit("A");
        var tainted = new SqlTextValue.Tainted("non-literal-expression", Origin);

        var result = SqlTextValue.Join(template, tainted, "guard", cap: 32, at: Origin);

        var resultTainted = Assert.IsType<SqlTextValue.Tainted>(result);
        Assert.Equal("non-literal-expression", resultTainted.Reason);
        var alternative = Assert.Single(resultTainted.GuardedAlternatives!);
        Assert.Equal("guard", alternative.GuardText);
        Assert.True(SqlTextValue.StructurallyEqual(template, alternative.Value));
    }

    [Fact]
    public void Join_TwoTaintedValues_WithDifferentReasons_KeepsFirstOperandsReason()
    {
        var a = new SqlTextValue.Tainted("reason-a", Origin);
        var b = new SqlTextValue.Tainted("reason-b", Origin);

        var result = SqlTextValue.Join(a, b, "guard", cap: 32, at: Origin);

        var resultTainted = Assert.IsType<SqlTextValue.Tainted>(result);
        Assert.Equal("reason-a", resultTainted.Reason);
    }

    [Fact]
    public void Join_TemplateAndTainted_WithAgreeingDeclaredType_RecoversTypedHole()
    {
        var template = Lit("A") with { DeclaredType = NVarChar50 };
        var tainted = new SqlTextValue.Tainted("non-literal-expression", Origin) { DeclaredType = NVarChar50 };

        var result = (SqlTextValue.Template)SqlTextValue.Join(template, tainted, "guard", cap: 32, at: Origin);

        var hole = Assert.IsType<TemplatePiece.Hole>(Assert.Single(result.Pieces));
        Assert.Equal(NVarChar50, hole.Type);
        Assert.Equal(HoleKind.WidenedChoice, hole.Kind);
    }

    [Fact]
    public void Join_TemplateAndTainted_WithDisagreeingDeclaredType_ProducesTaintedWithGuardedAlternative()
    {
        var template = Lit("A") with { DeclaredType = NVarChar50 };
        var tainted = new SqlTextValue.Tainted("non-literal-expression", Origin) { DeclaredType = Int };

        var result = SqlTextValue.Join(template, tainted, "guard", cap: 32, at: Origin);

        var resultTainted = Assert.IsType<SqlTextValue.Tainted>(result);
        Assert.Equal("non-literal-expression", resultTainted.Reason);
        Assert.Single(resultTainted.GuardedAlternatives!);
    }

    [Fact]
    public void Widen_BelowCap_ReturnsValueUnchanged()
    {
        var value = (SqlTextValue.Template)SqlTextValue.Join(Lit("A"), Lit("B"), "guard", cap: 32, at: Origin);

        var widened = SqlTextValue.Widen(value, cap: 32, at: Origin);

        Assert.True(SqlTextValue.StructurallyEqual(value, widened));
    }

    /// <summary>Builds a single-piece Choice template with <paramref name="count"/> distinct-text alternatives directly (bypassing <see cref="SqlTextValue.Join"/>, which self-widens on every call) so a test can put an over-cap Choice in front of <see cref="SqlTextValue.Widen"/> without an earlier Join call already having collapsed it.</summary>
    private static SqlTextValue.Template OverCapChoice(int count, SqlType? declaredType = null)
    {
        var alternatives = Enumerable.Range(0, count).Select(i => Lit($"v{i}") with { DeclaredType = declaredType }).ToList();
        return new SqlTextValue.Template([new TemplatePiece.Choice("guard", alternatives)]) { DeclaredType = declaredType };
    }

    [Fact]
    public void Widen_ExceedsCap_WithNoUniformType_ProducesTaintedWithCardinalityCapReason()
    {
        var value = OverCapChoice(count: 5);

        var result = SqlTextValue.Widen(value, cap: 4, at: Origin);

        var tainted = Assert.IsType<SqlTextValue.Tainted>(result);
        Assert.Equal(SqlTextValue.CardinalityCapReason, tainted.Reason);
    }

    [Fact]
    public void Widen_ExceedsCap_WithAgreeingDeclaredType_ProducesTypedHoleInsteadOfTaint()
    {
        var value = OverCapChoice(count: 5, declaredType: NVarChar50);

        var result = (SqlTextValue.Template)SqlTextValue.Widen(value, cap: 4, at: Origin);

        var hole = Assert.IsType<TemplatePiece.Hole>(Assert.Single(result.Pieces));
        Assert.Equal(NVarChar50, hole.Type);
    }

    [Fact]
    public void Widen_IsIdempotent()
    {
        var value = OverCapChoice(count: 5);

        var oncewidened = SqlTextValue.Widen(value, cap: 4, at: Origin);
        var twiceWidened = SqlTextValue.Widen(oncewidened, cap: 4, at: Origin);

        Assert.True(SqlTextValue.StructurallyEqual(oncewidened, twiceWidened));
    }

    [Fact]
    public void Widen_NeverIncreasesExpansionCount()
    {
        var value = (SqlTextValue.Template)SqlTextValue.Join(Lit("A"), Lit("B"), "guard", cap: 32, at: Origin);
        var before = SqlTextValue.ExpansionCount(value);

        var widened = (SqlTextValue.Template)SqlTextValue.Widen(value, cap: 32, at: Origin);
        var after = SqlTextValue.ExpansionCount(widened);

        Assert.True(after <= before);
    }

    [Fact]
    public void ExpansionCount_NoChoice_IsOne()
    {
        Assert.Equal(1, SqlTextValue.ExpansionCount(Lit("SELECT 1")));
    }

    [Fact]
    public void ExpansionCount_SingleChoice_IsAlternativeCount()
    {
        var value = (SqlTextValue.Template)SqlTextValue.Join(Lit("A"), Lit("B"), "guard", cap: 32, at: Origin);

        Assert.Equal(2, SqlTextValue.ExpansionCount(value));
    }

    [Fact]
    public void ExpansionCount_TwoSequentialChoices_Multiplies()
    {
        var choiceOne = new SqlTextValue.Template([new TemplatePiece.Choice("g1", [Lit("A"), Lit("B")])]);
        var choiceTwo = new SqlTextValue.Template([new TemplatePiece.Choice("g2", [Lit("X"), Lit("Y"), Lit("Z")])]);
        var combined = (SqlTextValue.Template)SqlTextValue.Concat(choiceOne, choiceTwo);

        Assert.Equal(6, SqlTextValue.ExpansionCount(combined));
    }

    [Fact]
    public void Expand_NoChoice_ProducesOneAssembly()
    {
        var assemblies = SqlTextValue.Expand(Lit("SELECT 1"), maxAssemblies: 32);

        var assembly = Assert.Single(assemblies);
        var lit = Assert.IsType<FlatPiece.Lit>(Assert.Single(assembly));
        Assert.Equal("SELECT 1", lit.Text);
    }

    [Fact]
    public void Expand_SingleChoice_ProducesOneAssemblyPerAlternative()
    {
        var value = (SqlTextValue.Template)SqlTextValue.Join(Lit("A"), Lit("B"), "guard", cap: 32, at: Origin);

        var assemblies = SqlTextValue.Expand(value, maxAssemblies: 32);

        Assert.Equal(2, assemblies.Count);
        var texts = assemblies.Select(a => ((FlatPiece.Lit)Assert.Single(a)).Text).OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(["A", "B"], texts);
    }

    [Fact]
    public void Expand_TwoSequentialChoices_ProducesFullCartesianProduct()
    {
        var choiceOne = new SqlTextValue.Template([new TemplatePiece.Choice("g1", [Lit("A"), Lit("B")])]);
        var choiceTwo = new SqlTextValue.Template([new TemplatePiece.Choice("g2", [Lit("X"), Lit("Y"), Lit("Z")])]);
        var combined = (SqlTextValue.Template)SqlTextValue.Concat(choiceOne, choiceTwo);

        var assemblies = SqlTextValue.Expand(combined, maxAssemblies: 32);

        Assert.Equal(6, assemblies.Count);
        var pairs = assemblies
            .Select(a => (((FlatPiece.Lit)a[0]).Text, ((FlatPiece.Lit)a[1]).Text))
            .OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            [("A", "X"), ("A", "Y"), ("A", "Z"), ("B", "X"), ("B", "Y"), ("B", "Z")],
            pairs);
    }

    [Fact]
    public void Expand_ExceedingCap_Throws()
    {
        var choice = new SqlTextValue.Template([new TemplatePiece.Choice("guard", [Lit("A"), Lit("B"), Lit("C")])]);

        Assert.Throws<InvalidOperationException>(() => SqlTextValue.Expand(choice, maxAssemblies: 2));
    }

    [Fact]
    public void ContainsHole_AssemblyWithNoHole_IsFalse()
    {
        var assemblies = SqlTextValue.Expand(Lit("SELECT 1"), maxAssemblies: 32);

        Assert.False(SqlTextValue.ContainsHole(assemblies[0]));
    }

    [Fact]
    public void ContainsHole_AssemblyWithHole_IsTrue()
    {
        var value = Hole(NVarChar50);

        var assemblies = SqlTextValue.Expand(value, maxAssemblies: 32);

        Assert.True(SqlTextValue.ContainsHole(assemblies[0]));
    }

    // ------------------------------------------------------------------
    // The guarded-alternatives depth invariant: a value stored AS an alternative never carries
    // alternatives of its own (see WithoutOwnAlternatives' doc comment in SqlTextValue).
    // Without it, concatenating two alternative-bearing Templates nests each side's
    // alternatives inside the other's stored values, so depth grows by one per Concat and the
    // recursive propagation costs MaxGuardedAlternatives^depth - a real accumulator-style proc
    // (`SET @sql = @sql + @piece` over IF/ELSE-IF-built variables) measured 78M+ Concat calls
    // in 60s with no convergence. These pin the invariant at every store site, so a future
    // refactor that reintroduces nesting fails HERE in milliseconds instead of hanging a scan.
    // ------------------------------------------------------------------

    private static SqlTextValue.Template WithAlternative(SqlTextValue.Template value, string guardText, SqlTextValue.Template alternative) =>
        value with { GuardedAlternatives = [new GuardedAlternative(guardText, alternative)] };

    [Fact]
    public void WithGuardedAlternative_BranchValueCarryingAlternatives_StoresItFlattened()
    {
        // Built via `with` directly, NOT via WithGuardedAlternative itself - post-invariant,
        // the funnel is exactly what strips this, so the nested shape must be hand-made.
        var nestedBranch = WithAlternative(Lit("outer"), "inner-guard", Lit("inner"));

        var result = SqlTextValue.WithGuardedAlternative(Lit("base"), "outer-guard", nestedBranch);

        var stored = Assert.Single(result.GuardedAlternatives!);
        Assert.Equal("outer-guard", stored.GuardText);
        Assert.Null(stored.Value.GuardedAlternatives);
        // Flattening drops only the side channel, never the value's own text.
        Assert.Equal("outer", ((TemplatePiece.Lit)Assert.Single(stored.Value.Pieces)).Text);
    }

    [Fact]
    public void Concat_TaintedLeftWithAlternatives_ExtendedStoredValuesStayFlat()
    {
        // The one store that bypasses WithGuardedAlternative's funnel: Concat's tainted-left
        // path extends each alternative's value with b directly. b itself carrying alternatives
        // is exactly what used to nest.
        var tainted = new SqlTextValue.Tainted("non-literal-expression", Origin)
        {
            GuardedAlternatives = [new GuardedAlternative("g1", Lit("SELECT "))],
        };
        var addend = WithAlternative(Lit("* FROM T"), "g2", Lit("* FROM U"));

        var result = Assert.IsType<SqlTextValue.Tainted>(SqlTextValue.Concat(tainted, addend));

        var extended = Assert.Single(result.GuardedAlternatives!);
        Assert.Null(extended.Value.GuardedAlternatives);
        Assert.Equal("SELECT * FROM T", string.Concat(extended.Value.Pieces.Cast<TemplatePiece.Lit>().Select(l => l.Text)));
    }

    [Fact]
    public void Concat_TwoTemplatesEachCarryingAlternatives_PropagatedStoredValuesStayFlat()
    {
        // Template+Template is the accumulator path: each side's alternatives propagate onto
        // the result with the OTHER side spliced in - both stored values must come out flat.
        var a = WithAlternative(Lit("SELECT "), "ga", Lit("SELECT TOP 1 "));
        var b = WithAlternative(Lit("* FROM T"), "gb", Lit("* FROM U"));

        var result = Assert.IsType<SqlTextValue.Template>(SqlTextValue.Concat(a, b));

        Assert.Equal(2, result.GuardedAlternatives!.Count);
        Assert.All(result.GuardedAlternatives, alt => Assert.Null(alt.Value.GuardedAlternatives));
        var byGuard = result.GuardedAlternatives.ToDictionary(alt => alt.GuardText);
        Assert.Equal("SELECT TOP 1 * FROM T", string.Concat(byGuard["ga"].Value.Pieces.Cast<TemplatePiece.Lit>().Select(l => l.Text)));
        Assert.Equal("SELECT * FROM U", string.Concat(byGuard["gb"].Value.Pieces.Cast<TemplatePiece.Lit>().Select(l => l.Text)));
    }
}
