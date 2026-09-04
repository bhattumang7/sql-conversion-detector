using SilentScan.Core.Reporting.Sarif;

namespace SilentScan.Core.Reporting.RuleDocs.Predicates;

internal static class XmlSchemaCollectionMismatch
{
    public static string RuleId => SarifRuleCatalog.XmlSchemaCollectionMismatchRuleId;

    public static RuleDocContent Content { get; } = new(
        WhyItMatters: """
            A typed XML variable or parameter (`XML(schema_collection)`) carries its schema
            collection as part of its static type. Confirmed directly against a real SQL Server
            instance: assigning one typed-XML variable directly to another whose declared schema
            collection is different - via a `DECLARE ... = @other` initializer or a plain `SET @x =
            @other` - fails to compile with Msg 527 ("Implicit conversion between XML types
            constrained by different XML schema collections is not allowed. Use the CONVERT
            function to run this query."), regardless of what the source variable's actual XML
            content is at runtime.

            This is scoped to a direct variable-to-variable assignment where both variables'
            declared schema collections are statically known from their own `DECLARE`/parameter
            list. An assignment through `CONVERT(XML(target_schema), source)` is never flagged -
            that is the engine's own documented way past this restriction, and it changes the
            expression's static type away from a bare variable reference.
            """,
        HowToFixIt: """
            Wrap the source expression in `CONVERT(XML(target_schema_collection), source)` if the
            value is genuinely expected to validate against the target's schema collection, or
            declare both variables against the same schema collection if they always carry the
            same shape of document.
            """,
        Examples:
        [
            new RuleDocExample(
                Title: "Assigning between two differently-typed XML variables never compiles",
                NoncompliantSql: """
                    DECLARE @order XML(dbo.OrderSchema) = '<Order/>';
                    DECLARE @invoice XML(dbo.InvoiceSchema);

                    SET @invoice = @order;
                    """,
                NoncompliantExplanation: "@order and @invoice are typed against different schema collections - this SET fails to compile with Msg 527 every time it runs.",
                CompliantSql: """
                    DECLARE @order XML(dbo.OrderSchema) = '<Order/>';
                    DECLARE @invoice XML(dbo.InvoiceSchema);

                    SET @invoice = CONVERT(XML(dbo.InvoiceSchema), @order);
                    """,
                CompliantExplanation: "The assignment goes through CONVERT, so it compiles - SQL Server validates the converted content against dbo.InvoiceSchema at execution time."),
        ]);
}
