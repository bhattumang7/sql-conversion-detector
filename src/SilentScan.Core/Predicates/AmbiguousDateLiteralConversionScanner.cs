using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static partial class AmbiguousDateLiteralConversionScanner
{
    private static readonly HashSet<SqlDataTypeOption> DateFamilyTypes =
    [
        SqlDataTypeOption.Date,
        SqlDataTypeOption.DateTime,
        SqlDataTypeOption.DateTime2,
        SqlDataTypeOption.SmallDateTime,
        SqlDataTypeOption.DateTimeOffset,
    ];

    [GeneratedRegex(@"^(\d{1,2})([/.\-])(\d{1,2})\2(\d{2}|\d{4})$")]
    private static partial Regex AmbiguousDatePattern { get; }

    internal static bool IsAmbiguousDateLiteral(string text)
    {
        var match = AmbiguousDatePattern.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var first = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var second = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);

        return first is >= 1 and <= 12 && second is >= 1 and <= 12 && first != second;
    }

    public static IReadOnlyList<AmbiguousDateLiteralConversionFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<AmbiguousDateLiteralConversionFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<AmbiguousDateLiteralConversionFinding> Findings { get; } = [];

        public void OnEnterCastCall(CastCall node, ModuleWalker walker) =>
            Inspect(node.DataType, node.Parameter);

        public void OnEnterConvertCall(ConvertCall node, ModuleWalker walker)
        {
            if (node.Style is not null)
            {
                return;
            }

            Inspect(node.DataType, node.Parameter);
        }

        private void Inspect(DataTypeReference dataType, ScalarExpression parameter)
        {
            if (dataType is not SqlDataTypeReference { SqlDataTypeOption: var option } || !DateFamilyTypes.Contains(option))
            {
                return;
            }

            if (parameter is not StringLiteral stringLiteral || !IsAmbiguousDateLiteral(stringLiteral.Value))
            {
                return;
            }

            Findings.Add(new AmbiguousDateLiteralConversionFinding(
                stringLiteral.Value, sourcePath, stringLiteral.StartLine, stringLiteral.StartColumn));
        }
    }
}
