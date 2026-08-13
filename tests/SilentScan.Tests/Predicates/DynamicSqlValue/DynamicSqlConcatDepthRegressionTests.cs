using System.Globalization;
using System.Text;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

/// <summary>
/// End-to-end regression pin for the guarded-alternatives depth invariant (see
/// <c>SqlTextValue.WithoutOwnAlternatives</c> and the unit-level pins in
/// <see cref="SqlTextValueTests"/>). The fixture reproduces the exact shape that hung a real
/// scan: variables assigned literals across IF/ELSE-IF/ELSE chains (every arm resolves, so each
/// variable ends as a live Template carrying GuardedAlternatives via
/// <c>DynamicSqlCfg.ApplyGuardedAlternativeFixup</c>), then a running accumulator
/// (<c>SET @out = @out + @vN + ', '</c>) concatenating those alternative-bearing values - each
/// such Concat used to nest one side's alternatives inside the other's stored values, growing
/// depth by one per statement, and the recursive propagation then cost
/// <c>MaxGuardedAlternatives</c>^depth. At THESE dimensions the pre-invariant scanner ran past a
/// 15-second kill (measured; the real-database original ran 78M+ Concat calls in 60s, still
/// diverging, at 10GB+ RSS); post-invariant the same scan completes in roughly a quarter
/// second. A regression re-manifests as this test hanging - loud in CI, never flaky.
/// </summary>
public sealed class DynamicSqlConcatDepthRegressionTests
{
    [Fact]
    public void AccumulatorConcatOverAlternativeBearingVariables_CompletesAndEmitsOneSymbolicScript()
    {
        const int Arms = 8;
        const int Vars = 12;
        const int AccumulationSteps = 24;

        var sb = new StringBuilder();
        sb.AppendLine("CREATE PROCEDURE dbo.usp_AccumulatorRepro @mode INT AS");
        sb.AppendLine("BEGIN");
        for (var v = 0; v < Vars; v++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"DECLARE @v{v} VARCHAR(MAX);");
        }

        sb.AppendLine("DECLARE @out VARCHAR(MAX);");
        for (var v = 0; v < Vars; v++)
        {
            // Distinct guard per arm so MaxGuardedAlternatives' same-guard dedupe can't
            // collapse them; every arm a literal so both sides of every join resolve.
            sb.Append(CultureInfo.InvariantCulture, $"IF @mode = {v * 100} SET @v{v} = 'p{v}a0 '; ");
            for (var a = 1; a < Arms; a++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"ELSE IF @mode = {v * 100 + a} SET @v{v} = 'p{v}a{a} '; ");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"ELSE SET @v{v} = 'p{v}else ';");
        }

        sb.AppendLine("SET @out = 'start ';");
        for (var s = 0; s < AccumulationSteps; s++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"SET @out = @out + @v{s % Vars} + ', ';");
        }

        var pieces = string.Join(", ", Enumerable.Range(0, Vars).Select(v => $"@v{v}"));
        sb.AppendLine(CultureInfo.InvariantCulture, $"EXEC (@out, {pieces});");
        sb.AppendLine("END");

        var parsed = SqlScriptParser.ParseText("accumulator_repro.sql", sb.ToString());
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Errors.Select(e => e.Message)));

        var result = DynamicSqlScannerV2.Scan(parsed);

        // The accumulated value's Choice expansion far exceeds the assembly cap, so Widen
        // collapses the whole EXEC argument to one typed hole - emitted as a single
        // symbolic-placeholder script, with no Unanalyzable finding. Deterministic: same
        // fixture, same collapse, every run.
        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }
}
