using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class SchemaboundAliasTypeScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<SchemaboundAliasTypeFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath, catalog);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath, DatabaseCatalog catalog) => new(sourcePath, catalog);

    internal static IReadOnlyList<SchemaboundAliasTypeFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.MemberName, StringComparer.Ordinal),
        ];

    internal sealed class Rule(string sourcePath, DatabaseCatalog catalog) : IModuleRule
    {
        public List<SchemaboundAliasTypeFinding> Findings { get; } = [];

        public void OnEnterCreateFunctionStatement(CreateFunctionStatement node, ModuleWalker walker) => Inspect(node);

        public void OnEnterAlterFunctionStatement(AlterFunctionStatement node, ModuleWalker walker) => Inspect(node);

        public void OnEnterCreateOrAlterFunctionStatement(CreateOrAlterFunctionStatement node, ModuleWalker walker) => Inspect(node);

        private void Inspect(FunctionStatementBody node)
        {
            if (!node.Options.Any(option => option.OptionKind == FunctionOptionKind.SchemaBinding))
            {
                return;
            }

            var qualifiedName = SchemaObjectNameHelper.Qualify(node.Name);

            foreach (var parameter in node.Parameters)
            {
                if (TryGetAliasTypeName(parameter.DataType) is { } aliasName)
                {
                    Add(qualifiedName, SchemaboundAliasTypeKind.Parameter, parameter.VariableName.Value, aliasName, node);
                }
            }

            switch (node.ReturnType)
            {
                case ScalarFunctionReturnType scalarReturn when TryGetAliasTypeName(scalarReturn.DataType) is { } aliasName:
                    Add(qualifiedName, SchemaboundAliasTypeKind.ReturnType, "RETURNS", aliasName, node);
                    break;

                case TableValuedFunctionReturnType { DeclareTableVariableBody.Definition.ColumnDefinitions: { } columnDefinitions }:
                    foreach (var column in columnDefinitions)
                    {
                        if (TryGetAliasTypeName(column.DataType) is { } columnAliasName)
                        {
                            Add(qualifiedName, SchemaboundAliasTypeKind.TableColumn, column.ColumnIdentifier.Value, columnAliasName, node);
                        }
                    }

                    break;
            }
        }

        private string? TryGetAliasTypeName(DataTypeReference? dataType)
        {
            if (dataType is not UserDataTypeReference userType
                || string.Equals(userType.Name.BaseIdentifier.Value, "sysname", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var qualifiedName = SchemaObjectNameHelper.Qualify(userType.Name);
            return catalog.TypeAliases.ContainsKey(qualifiedName) ? qualifiedName : null;
        }

        private void Add(string functionQualifiedName, SchemaboundAliasTypeKind kind, string memberName, string aliasTypeQualifiedName, TSqlFragment node) =>
            Findings.Add(new SchemaboundAliasTypeFinding(
                functionQualifiedName, kind, memberName, aliasTypeQualifiedName, sourcePath, node.StartLine, node.StartColumn));
    }
}
