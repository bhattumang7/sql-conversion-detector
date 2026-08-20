using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Predicates;

/// <summary>
/// The catalog-only half of the scalar-UDF stream (docs/detection-checklist.md Tier 1 #1): a
/// computed column, DEFAULT, or CHECK constraint whose definition calls a scalar UDF poisons
/// every query touching the table with per-row/serial cost, even one that never names the
/// column - no query-site AST to walk, so this runs once over
/// <see cref="DatabaseCatalog.SchemaExpressions"/> instead of per-file, after the whole catalog
/// (including every scalar UDF) is known. A definition's own text is reparsed through a throwaway
/// wrapper statement (same technique <c>LiveCatalogReader.TryParseSchemaObjectName</c> uses for a
/// synonym's raw target text) rather than requiring a retained AST - the only shape both file mode
/// and live mode (whose definitions arrive as plain <c>sys.*.definition</c> strings) can share.
/// </summary>
public static class SchemaDependencyScanner
{
    public static IReadOnlyList<ScalarUdfFinding> Scan(DatabaseCatalog catalog)
    {
        var findings = new List<ScalarUdfFinding>();

        foreach (var reference in catalog.SchemaExpressions)
        {
            var fragment = TryParse(reference);
            if (fragment is null)
            {
                continue;
            }

            var visitor = new FunctionCallCollector();
            fragment.Accept(visitor);

            // A UDF called more than once in the same definition (rare, but possible) is one
            // finding, not one per call - there is only one schema object for the SARIF/readable
            // output to point a reader at.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var call in visitor.Calls)
            {
                var qualifiedName = catalog.ResolveSynonymName(SchemaObjectNameHelper.QualifyFunctionCall(call));
                if (!seen.Add(qualifiedName) || !catalog.TryGetScalarUdfInfo(qualifiedName, out var info) || info is null)
                {
                    continue;
                }

                var (inlineability, blocker) = ScalarUdfInlineabilityClassifier.Classify(info);

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

    /// <summary>A CHECK constraint's definition is a boolean predicate (not valid as a bare SELECT list expression), so it wraps under WHERE; a computed-column/DEFAULT definition is an ordinary scalar expression and wraps directly in the SELECT list. Either shape parses to the same tree kind of interest - a FunctionCall walk doesn't care which statement shape it ended up inside.</summary>
    private static TSqlFragment? TryParse(SchemaExpressionReference reference)
    {
        var wrapped = reference.Kind == SchemaDependencyKind.CheckConstraint
            ? $"SELECT 1 WHERE {reference.DefinitionText};"
            : $"SELECT {reference.DefinitionText};";

        var result = SqlScriptParser.ParseText("schema-expression.sql", wrapped);
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
