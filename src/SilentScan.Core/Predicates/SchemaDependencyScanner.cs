using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

public static class SchemaDependencyScanner
{
    public static IReadOnlyList<ScalarUdfFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<ScalarUdfFinding>();

        foreach (var reference in catalog.SchemaExpressions)
        {
            var fragment = TryParse(reference, catalog.CompatibilityLevel);
            if (fragment is null)
            {
                continue;
            }

            var visitor = new FunctionCallCollector();
            fragment.Accept(visitor);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var call in visitor.Calls)
            {
                var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.QualifyFunctionCall(call));
                if (!seen.Add(qualifiedName) || !catalog.TryGetScalarUdfInfo(qualifiedName, out var info) || info is null)
                {
                    continue;
                }

                var (inlineability, blocker) = ScalarUdfInlineabilityClassifier.Classify(info, catalog.CompatibilityLevel);

                findings.Add(new ScalarUdfFinding(
                    ScalarUdfFindingKind.SchemaDependency,
                    FunctionQualifiedName: qualifiedName,
                    ReferencedObjectQualifiedName: reference.TableQualifiedName,
                    UdfKind: info.Kind,
                    Inlineability: inlineability,
                    InlineabilityBlocker: blocker,
                    IsSchemaBound: info.IsSchemaBound,
                    ConstantArgumentsNotFolded: false,
                    ClrDataAccess: info.ClrDataAccess,
                    Context: ScalarUdfContext.Other,
                    SchemaDependencyKind: reference.Kind,
                    SourcePath: reference.SourcePath,
                    Line: reference.Line,
                    Column: 0,
                    ReferenceFragmentText: reference.DefinitionText));
            }
        }

        return findings;
    }

    private static TSqlFragment? TryParse(SchemaExpressionReference reference, int? compatibilityLevel)
    {
        var wrapped = reference.Kind == SchemaDependencyKind.CheckConstraint
            ? $"SELECT 1 WHERE {reference.DefinitionText};"
            : $"SELECT {reference.DefinitionText};";

        var result = SqlScriptParser.ParseText("schema-expression.sql", wrapped, initialQuotedIdentifiers: true, compatibilityLevel);
        return result.HasErrors ? null : result.Fragment;
    }

    private sealed class FunctionCallCollector : TSqlFragmentVisitor
    {
        public List<FunctionCall> Calls { get; } = [];

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget)
            {
                Calls.Add(node);
            }

            base.ExplicitVisit(node);
        }
    }
}
