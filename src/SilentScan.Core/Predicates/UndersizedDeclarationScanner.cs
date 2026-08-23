using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class UndersizedDeclarationScanner
{
    private const int MaxFlaggedLength = 2;

public static IReadOnlyList<UndersizedDeclarationFinding> ScanCatalog(DatabaseCatalog catalog)
    {
        var findings = new List<UndersizedDeclarationFinding>();

        foreach (var table in catalog.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (!IsFlaggedStringOrBinary(column.Type))
                {
                    continue;
                }

                findings.Add(new UndersizedDeclarationFinding(
                    UndersizedDeclarationSite.TableColumn,
                    $"{table.QualifiedName}.{column.Name}",
                    column.Type!.ToString(),
                    column.Type.Length!.Value,
                    table.SourcePath,
                    table.SourceLine,
                    Column: 1));
            }
        }

        return
        [
            .. findings
                .OrderBy(f => f.QualifiedOrVariableName, StringComparer.Ordinal),
        ];
    }

public static IReadOnlyList<UndersizedDeclarationFinding> ScanDeclarations(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private static bool IsFlaggedStringOrBinary(SqlType? type) =>
        type is { IsMax: false, Length: > 0 and <= MaxFlaggedLength }
        && type.Category is SqlTypeCategory.Char or SqlTypeCategory.VarChar
            or SqlTypeCategory.NChar or SqlTypeCategory.NVarChar
            or SqlTypeCategory.Binary or SqlTypeCategory.VarBinary;

    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog) : TSqlFragmentVisitor
    {
        public List<UndersizedDeclarationFinding> Findings { get; } = [];

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var declaration in node.Declarations)
            {
                TryAdd(declaration.DataType, declaration.VariableName.Value, declaration.StartLine, declaration.StartColumn);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ProcedureParameter node)
        {
            TryAdd(node.DataType, node.VariableName.Value, node.StartLine, node.StartColumn);
            base.ExplicitVisit(node);
        }

        private void TryAdd(DataTypeReference dataType, string variableName, int line, int column)
        {
            var resolved = SqlTypeReferenceResolver.Resolve(dataType, columnCollation: null, catalog.TypeAliases);
            if (!IsFlaggedStringOrBinary(resolved))
            {
                return;
            }

            Findings.Add(new UndersizedDeclarationFinding(
                UndersizedDeclarationSite.Declaration,
                variableName,
                resolved!.ToString(),
                resolved.Length!.Value,
                sourcePath,
                line,
                column));
        }
    }
}
