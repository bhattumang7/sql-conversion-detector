# Future work — deliberately out of scope

Work identified during the core-detection audit (2026-08-03) and consciously
excluded from the active task set (tasks #1–#16). Each item here is an
exclusion by decision, not an oversight: the related tasks make these cases
*visible* (Unknown + ledger, annotations) but do not model them. If one of
these is promoted to in-scope, turn it into a task and delete its entry here.

## Detection-engine modeling

- **Remote schema resolution for linked-server / cross-database references.**
  Task #7 stops four-part names from silently binding to local objects; the
  references resolve to `Unknown` with a `linked-server-reference` ledger
  entry. Actually fetching remote catalogs (linked servers, other databases on
  the instance) is excluded — it breaks the single-database scope and the
  read-only guard story would need rethinking per remote target.
- **Multi-database enumeration in one `scan-db` run.** One invocation scans
  one database. Scanning every database on an instance (and stitching
  cross-database lineage between them) is a product feature on its own.
- **Full OR index-union modeling.** Task #10 makes OR-tree handling consistent
  with the `<>` exclusion policy and marks per-leaf verdicts inside OR trees;
  reasoning about whether the engine can satisfy an OR via index union/merge
  is excluded.
- **Covering-index reasoning.** `CatalogIndex.IncludedColumns` is captured but
  unused — only the leading key column drives the `Indexed` flag. Whether an
  index *covers* a query (and therefore how painful the lost seek is) is not
  modeled.
- **Filtered-index predicate implication.** Task #8 reads
  `filter_definition` and annotates why a filtered index was excluded from
  seekability; proving "this predicate falls inside the filter, so the
  filtered index still applies" is excluded.
- **Partitioning.** Partition schemes/functions, `ON ps(col)`, and
  partition-aligned index reasoning are not modeled.
- **Memory-optimized, temporal/system-versioned, and graph (NODE/EDGE)
  tables.** Ledgered as unanalyzable table kinds, not modeled.
- **Encrypted-module recovery.** Modules with `sys.sql_modules.definition IS
  NULL` are counted as unanalyzable with reason; no DAC/decryption path.
- **Server-scoped triggers.** They live outside the scanned database and are
  invisible to `scan-db`; database-scoped DDL triggers are covered.

## Ranking and evidence

- **Cost-based impact ranking.** Ranking stays static
  (verdict → indexed → depth). Task #14 adds Query Store / plan-cache
  *evidence*, but row counts (`sys.dm_db_partition_stats`), index usage
  (`sys.dm_db_index_usage_stats`), and estimated-cost weighting are excluded.

## Hard project scope (per CLAUDE.md, unchanged)

- EF Core / ORM source analysis — SQL text only in v1.
- Non-T-SQL dialects — ScriptDom/T-SQL only, no ANTLR.
- Corpus expansion beyond the pinned 5-repo pilot set.
- CI gating features and remediation/fix suggestions — explicitly rejected.
