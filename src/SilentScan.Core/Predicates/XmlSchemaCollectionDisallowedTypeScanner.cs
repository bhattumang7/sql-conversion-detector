using System.Xml;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Common;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class XmlSchemaCollectionDisallowedTypeScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";

    public static IReadOnlyList<XmlSchemaCollectionDisallowedTypeFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, catalog, EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<XmlSchemaCollectionDisallowedTypeFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<XmlSchemaCollectionDisallowedTypeFinding> Findings { get; } = [];

        private static (string? Namespace, string Local) ResolveQualifiedName(XElement context, string value)
        {
            var separatorIndex = value.IndexOf(':');
            if (separatorIndex < 0)
            {
                return (context.GetDefaultNamespace().NamespaceName is { Length: > 0 } ns ? ns : null, value);
            }

            var prefix = value[..separatorIndex];
            var local = value[(separatorIndex + 1)..];
            return (context.GetNamespaceOfPrefix(prefix)?.NamespaceName, local);
        }

        public void OnEnterCreateXmlSchemaCollectionStatement(CreateXmlSchemaCollectionStatement node, ModuleWalker walker) =>
            Inspect(node.Name, node.Expression);

        public void OnEnterAlterXmlSchemaCollectionStatement(AlterXmlSchemaCollectionStatement node, ModuleWalker walker) =>
            Inspect(node.Name, node.Expression);

        private void Inspect(SchemaObjectName name, ScalarExpression expression)
        {
            if (expression is not StringLiteral stringLiteral)
            {
                return;
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(stringLiteral.Value);
            }
            catch (XmlException)
            {
                return;
            }

            if (document.Root is null)
            {
                return;
            }

            var schemaCollectionName = SchemaObjectNameHelper.Qualify(name);

            foreach (var element in document.Root.DescendantsAndSelf())
            {
                if (element.Name.Namespace.NamespaceName != XsdNamespace)
                {
                    continue;
                }

                InspectQNameAttribute(element, "type", schemaCollectionName, stringLiteral, isElementTypeAttribute: element.Name.LocalName == "element");
                InspectQNameAttribute(
                    element, "base", schemaCollectionName, stringLiteral,
                    isElementTypeAttribute: element.Name.LocalName is "extension" or "restriction");
            }
        }

        private void InspectQNameAttribute(XElement element, string attributeName, string schemaCollectionName, StringLiteral stringLiteral, bool isElementTypeAttribute)
        {
            if (element.Attribute(attributeName) is not { } attribute)
            {
                return;
            }

            var (ns, local) = ResolveQualifiedName(element, attribute.Value);
            if (ns != XsdNamespace)
            {
                return;
            }

            if (local == "NOTATION")
            {
                Findings.Add(new XmlSchemaCollectionDisallowedTypeFinding(
                    schemaCollectionName, local, XmlSchemaCollectionDisallowedTypeKind.NotationType,
                    sourcePath, stringLiteral.StartLine, stringLiteral.StartColumn));
                return;
            }

            if (isElementTypeAttribute && local is "ID" or "IDREF" or "IDREFS")
            {
                Findings.Add(new XmlSchemaCollectionDisallowedTypeFinding(
                    schemaCollectionName, local, XmlSchemaCollectionDisallowedTypeKind.IdOrIdRefType,
                    sourcePath, stringLiteral.StartLine, stringLiteral.StartColumn));
            }
        }
    }
}
