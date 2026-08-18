namespace SilentScan.Core.Catalog;

/// <summary>
/// One Row-Level Security predicate binding, read live from <c>sys.security_policies</c>/
/// <c>sys.security_predicates</c> - engine-authoritative by construction, the same reasoning
/// <see cref="ForeignKeyRelationship"/>/<see cref="CatalogCheckConstraint"/> already document.
/// Always empty for a file-mode scan: RLS is a purely server-side binding (<c>CREATE SECURITY
/// POLICY ... ADD FILTER/BLOCK PREDICATE fn(cols) ON table</c>) with no in-module DDL text this
/// codebase's file-mode catalog builder ever sees on its own.
///
/// <paramref name="PredicateDefinitionText"/> is <c>sys.security_predicates.predicate_definition</c>
/// verbatim (e.g. <c>([Security].[fn_TenantPredicate]([TenantId]))</c>) - confirmed directly against
/// the standing Docker oracle (disposable scratch database, dropped immediately after) to be the
/// exact call-site expression authored in the policy's own <c>ADD FILTER PREDICATE</c> clause: the
/// predicate function's schema-qualified name followed by its argument list, where each argument is
/// (in every real-world pattern this codebase targets) a bare reference to one of the SECURED
/// table's own columns, positionally bound to the function's own parameters. <see
/// cref="Predicates.SecurityPredicateIndexScanner"/> reparses this text - there is deliberately no
/// separate "predicate function object_id" column on <c>sys.security_predicates</c> to join through
/// instead (confirmed empirically: the view carries <c>object_id</c> for the owning policy,
/// <c>target_object_id</c> for the secured table, and this text - nothing else identifies the
/// function or its bound columns), so reparsing this call-site expression is the only catalog-level
/// way to recover both facts, the same "reparse the engine's own stored text" discipline <see
/// cref="CatalogCheckConstraint.DefinitionText"/> and <c>sys.indexes.filter_definition</c> reads
/// already use elsewhere in this codebase.
///
/// <paramref name="IsFilterPredicate"/> is <c>sys.security_predicates.predicate_type_desc = 'FILTER'</c>
/// (vs. <c>'BLOCK'</c>) - deliberately kept, since only a FILTER predicate is silently applied to
/// EVERY <c>SELECT</c>/<c>UPDATE</c>/<c>DELETE</c> against the secured table (a BLOCK predicate only
/// gates specific write operations - <c>AFTER INSERT</c>/<c>AFTER UPDATE</c>/<c>BEFORE UPDATE</c>/
/// <c>BEFORE DELETE</c> - and does not force a residual per-row filter over the table's own read
/// path the way a FILTER predicate does), so <see cref="Predicates.SecurityPredicateIndexFinding"/>
/// only ever fires for the FILTER case - see that type's own doc comment.
/// </summary>
public sealed record CatalogSecurityPredicate(
    string PolicyQualifiedName,
    string TargetTableQualifiedName,
    string PredicateDefinitionText,
    bool IsFilterPredicate,
    bool IsPolicyEnabled);
