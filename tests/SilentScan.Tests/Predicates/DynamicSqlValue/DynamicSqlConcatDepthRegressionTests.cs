using System.Globalization;
using System.Text;
using SilentScan.Core.Parsing;
using SilentScan.Core.Predicates.DynamicSqlValue;

namespace SilentScan.Tests.Predicates.DynamicSqlValue;

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

        Assert.Empty(result.Findings);
        var script = Assert.Single(result.AnalyzableScripts);
        Assert.Matches(@"^__silentscan_sym_L\d+C\d+__$", script.InnerText);
    }
}
