# SQL Server performance anti-pattern reference (complete, un-gated)

The full record of the detection-space research: every query-level performance
problem surveyed, plus the supporting mechanics (plan-XML markers, canonical
engine-behavior lists, version mitigations, and the production-copy inventory)
needed to design rules and oracle probes without redoing the research.
`docs/detection-checklist.md` is the gated working backlog derived from this;
this file exists so no surveyed fact is lost when a future session asks
"was X considered?" or "how exactly does X behave?".

Columns: **Static?** High = precise static rule writable from SQL text +
catalog; Med = detectable but harm/precision depends on optimizer or workload;
Runtime = only observable from live plans/DMVs, out of scope by design.
**Disposition:** T1/T2 = checklist tier; Skip = deliberate skip (reason in
checklist Tier 3); Shipped = already implemented.

## Market context (why the gates are what they are)

- Only two static analyzers in existence ever bind column types to check
  conversions; both flag type mismatches **symmetrically** (no notion of which
  side converts), neither knows collation families, lineage, or dynamic SQL,
  and one is dead (docs recoverable only from web archives) while the other is
  a niche community project. Direction-aware conversion detection otherwise
  exists **only at runtime** (plan-XML convert warnings, the 2022 anti-pattern
  event).
- Microsoft's official static rule set is ~16 rules, ~6 performance-relevant:
  SELECT * in a batch, unindexed column in IN, leading-wildcard LIKE,
  non-sargable expression on a column, varchar(1)/(2), data-loss cast,
  deterministic-function-in-WHERE — plus an ISNULL rule whose recommended fix
  (wrap nullable columns in ISNULL) *creates* the seek-killing anti-pattern.
  Unchanged for a decade.
- The SQL Server 2022 anti-pattern detection is an Extended Event, not Query
  Store: fixed type map `TypeConvertPreventingSeek`, `LargeIn`,
  `LargeNumberOfOrInPredicate`, `NonOptimalOrLogic` (plus an undocumented
  `Max` and a `None` sentinel). Fires at optimization time, only while an XE
  session listens. No non-sargable-function or wildcard pattern in the list.
- Crowding (how many existing linters cover a pattern): leading-wildcard LIKE
  ~5; function-on-column ~5; SELECT * 4+; cursors ~4; SET NOCOUNT ~4;
  NOT IN and `<>` ~3 each. All syntax-only. **Rule of admission: a new
  detection should require the engine-authoritative catalog, the lineage
  pass, or the plan-XML oracle to be possible at all.**
- The mainstream plan-cache advisory tooling detects implicit conversions only
  by reporting the engine's own `PlanAffectingConvert` warning — it cannot
  distinguish column-side (seek-killing) from value-side (harmless)
  conversions. Our precedence-direction analysis is strictly finer-grained
  than everything surveyed.

---

## A. Conversion / type mismatch

| Pattern | Why it hurts | Static? | Disposition |
|---|---|---|---|
| Column-side implicit conversion in WHERE (precedence direction, collation family) | Column converts → seek lost; SQL_* collations scan, Windows collations range-seek via GetRangeThroughConvert | High | Shipped |
| Type mismatch across JOIN `ON` columns | Same conversion mechanics on join keys; misestimates cascade into join order | High | T1-3 |
| Proc/function parameter type vs compared column type | Conversion at call boundary; the param's declared type propagates into every predicate using it | High | T1-3 |
| Variable/literal type incompatible with proc parameter | Conversion when invoking; ambiguous casts | High | T1-3 (folded) |
| Date/time column vs string literal comparisons | Conversion + regional-format ambiguity | High | Shipped (literal typing) |
| Mixed-type CASE/COALESCE/NULLIF result typing | Result takes the highest-precedence operand type → a conversion can land on the column side | High | Shipped (rules) / T1-4 (verdict upgrade) |
| Mixed-type IN lists | Highest-precedence element converts the column | High | Shipped |
| `sql_variant` comparisons | sql_variant has the **highest** precedence of all types → the real column always converts; drivers that send sql_variant params trigger it invisibly | High | T1-3 |
| Column collation ≠ database collation | Cross-collation comparisons force conversions/COLLATE operators, blocking seeks — the schema-side twin of implicit conversion | High | T1-3 |
| Temp table inheriting tempdb collation vs joined column | Hidden collation conflict in joins to #temp tables | High | T1-3 (variant) |
| Data-loss / truncating casts | Correctness + estimate damage | High | Skip (correctness-framed) |

## B. Sargability (predicate shape)

| Pattern | Why it hurts | Static? | Disposition |
|---|---|---|---|
| Function-wrapped filter column | No seek | High | Shipped |
| CAST/CONVERT on column side | No seek | High | Shipped |
| Column arithmetic (`col + 5 > @p`) | No seek | High | Shipped |
| Leading-wildcard LIKE (`'%x'`, `'_x'`) | No seek | High | Shipped |
| Non-literal LIKE pattern | Optimizer can't evaluate pattern sargability at compile time | High | Shipped |
| `ISNULL(col,x)` / `COALESCE(col,x)` in predicate | No seek; COALESCE typing can additionally flip a conversion onto the column | High | T1-4 |
| Date-form non-sargables: `YEAR(col)=`, `DATEPART(col)`, `DATEADD/DATEDIFF` on column, `CONVERT(varchar,col,n)` comparisons | No seek; the most-repeated family in real-world tuning writeups | High | T1-4 |
| BETWEEN with end-of-period datetime boundary | Boundary correctness (23:59:59.997 tricks); the common "fix" (DATEPART on the column) is itself non-sargable unless persisted-computed + indexed | High | T1-4 |
| `CHARINDEX(x,col)` / `LEFT(col,n)=` instead of sargable LIKE | No seek; mechanical sargable rewrite exists (`LIKE 'x%'`) | High | T1-4 |
| UPPER/LOWER on column (collation-aware) | No seek; pointless under CI collation — rule should fire only when the column's actual collation is case-sensitive (existing linters assume CI blindly) | High | T1-4 |
| Scalar UDF wrapping the filter column | No seek + per-row execution + serial plan | High | T1-1 |
| Deterministic, row-independent function call in WHERE (`ABS(@p)` etc.) | Recomputed per row instead of hoisted to a variable | High | Skip (marginal) |
| `<>` / `!=` predicates | Range from both ends; often fine | Med | Skip (low precision) |
| `= NULL` comparison | Always-unknown predicate under ANSI_NULLS | High | Skip (correctness lint) |
| Contradictory / constant-foldable predicates | Dead predicates, wasted optimization | High | Skip (lint) |
| ISNUMERIC misuse | Wrong semantics (accepts `'.'`, `'$'`, `'1e4'`) + non-sargable | High | Skip (lint) |
| LIKE with no wildcard | Equality in disguise | High | Skip (lint) |
| OR across different columns | Defeats single-index seek; optimizer may index-union, often falls to scan; classic rewrite is UNION ALL | Med | Skip (parked; index-aware variant only: fire when either column lacks its own index) |
| Large IN lists / OR explosions | Optimizer-hostile expansion (also two of the four 2022-XE anti-pattern types) | Med | Skip (threshold guessing) |
| **Precision guard:** `JSON_VALUE(col,'$.path')` / `JSON_QUERY` matched to an identically-defined indexed computed column | The engine has matched this shape to a supporting index since 2016 and used it — a blanket function-wrapped-column rule misfires here | High (catalog: computed-column definition match) | **Applies to the shipped function-wrapped-column rule** — verify the guard exists before the next sargability-rule release |

## C. Opaque modules (UDFs, TVFs, procs, triggers)

| Pattern | Why it hurts | Static? | Disposition |
|---|---|---|---|
| Scalar UDF in predicate | Per-row execution, non-sargable, whole plan serial (pre-inlining); cost invisible to the optimizer (plan cost stays tiny while CPU burns — the classic "low cost, high CPU" signature) | High | T1-1 |
| Scalar UDF in SELECT list / projections | Per-row + serial; hides a join when the UDF does data lookups ("poor man's join" = correlated subquery in disguise) | High | T1-1 |
| Scalar UDF reached through iTVF/view expansion | Inlining spreads the UDF into every consumer invisibly; lineage-only detection | High | T1-1 |
| Scalar UDF in computed column / DEFAULT / CHECK | Serializes **every** query touching the table, even ones not naming the column; also blocks parallel index rebuilds | High | T1-1 |
| Non-inlineable UDF (2019+ blocker list — see Appendix 3) | The official fix silently doesn't apply; devs believe they're safe | High | T1-1 |
| Non-schemabound scalar UDF used as a constant | Engine won't trust it as deterministic/precise → executes per row even for constant args | High | T1-1 |
| CLR scalar UDF with data access | Forces whole plan serial | High | T1-1 |
| MSTVF in FROM/JOIN | Optimization fence: body opaque, result spooled to a stats-less table variable, fixed estimate (1 row legacy CE / 100 rows new CE) poisons join order, join types, grants | High | T1-2 |
| Correlated `CROSS/OUTER APPLY dbo.fn(t.col)` over MSTVF | Entire body executes once per outer row; interleaved execution explicitly does NOT rescue correlated cases on any version | High | T1-2 |
| MSTVF nested under views/other TVFs | Inherited fence, invisible at the call site; lineage depth + origin reporting | High | T1-2 |
| `INSERT ... EXEC` | Forced full materialization to a worktable; cannot nest | High | T1-2 |
| Proc/MSTVF where a view or iTVF suffices | Blocks predicate pushdown and composition | Med | Skip (judgment call) |
| Trigger hidden costs (triggers containing cursors/UDFs/MSTVFs; nested/recursive trigger fires) | Invisible per-DML cost; rollback amplification; wide locks | High (content scan) | T2-13 |
| Overloaded routines (parameter picks which branch/query runs) | One cached plan shape for many logical queries | Med | Skip (heuristic) |
| Giant single queries (optimizer-timeout territory) | Optimizer aborts early (`TimeOut` / `MemoryLimitExceeded` abort reasons) → best-guess plan | Med | Skip (complexity thresholds) |
| CHECK constraint referencing a scalar/CLR function | Forces serialized execution of every query and maintenance op against the table; catalog-scan trigger, independent of whether the object ever appears in a cached plan | High | T2-14 |
| Non-persisted computed column, independent of whether it references a UDF | Recomputed on every read; broader trigger than the UDF-specific computed-column rule | High | T2-14 |

## D. Query structure and estimation

| Pattern | Why it hurts | Static? | Disposition |
|---|---|---|---|
| Catch-all `(col=@p OR @p IS NULL)` optional filters | One plan must serve all parameter combinations → scan-safe plan chosen; per-branch seeks impossible | High (with RECOMPILE guard) | T2-6 |
| Local DECLAREd variable in predicate | Optimizer can't sniff a DECLAREd variable → density-vector "average" estimate; distinct from parameter sniffing | High (with RECOMPILE guard) | T2-7 |
| Parameter overwritten before use in predicate | Sniffed value ≠ value actually used at runtime | High (straight-line dataflow) | T2-6 |
| `NOT IN (subquery)` over nullable column | One NULL makes the predicate unknown for every row (correctness) AND forces a null-aware anti-semi-join with per-row pass-through predicate (expensive) | High (nullability gate) | T2-8 |
| `UPDATE ... FROM` without source uniqueness | Each target row updated once with an **arbitrary** matching source row — silent wrong results (MERGE raises an error for the same condition) | High (uniqueness gate) | T2-9 |
| Nested views (view-on-view depth) | Estimation degrades per layer; optimizer sometimes fails to simplify; redundant joins not eliminated | High (lineage) | T2-11 |
| Joins between large views | Un-indexable join surface | Med | Skip (folded into depth metric) |
| Multi-referenced CTE | CTEs are inline macros, never materialized — each reference re-executes the full subtree | High | T2-11 |
| Correlated subquery that doesn't unnest | Per-row apply when decorrelation fails (inequality correlation is the documented failure case) | Low-Med | Skip (optimizer-dependent) |
| Scalar subquery per row in SELECT list | N executions (apply/spool); hides a join; forces serial if it contains a UDF | Med-High | Skip (decorrelation uncertainty; revisit) |
| DISTINCT masking a fan-out join | Join multiplies rows, DISTINCT re-collapses → huge intermediate + sort; symptom of a missing join predicate | Low-Med | Skip |
| UNION instead of UNION ALL when duplicates impossible | Hidden DISTINCT sort/hash over the whole result | Med (needs uniqueness proof across branches) | Skip (parked) |
| `COUNT(*) > 0` instead of EXISTS | Full count where one row suffices (no row goal applied) | High | Skip (lint-tier; cheap add if wanted) |
| OUTER JOIN where EXISTS suffices / EXISTS-vs-IN | Extra join work | Med | Skip |
| TOP without ORDER BY | Nondeterminism + row goal side effects | High | Skip (correctness lint) |
| TOP(100) PERCENT + ORDER BY inside views/derived tables | Useless sort work the engine may discard; order not guaranteed anyway | High | Skip (lint) |
| Row goals gone wrong (TOP / FAST n / EXISTS with rare predicates) | Optimizer scales estimates down assuming an early match; rare predicate → near-full scan at ~1-row estimated cost | Low | Skip (estimation-vs-reality) |
| RANGE instead of ROWS in window frames (RANGE is the default when ORDER BY has no explicit frame) | RANGE spools to an on-disk worktable — drastically slower than ROWS | High | T2-13 |
| Recursive CTE | Serial zone in the plan | High | T2-10 (informational) |
| Too many joined tables | Optimization-space explosion; contributes to optimizer timeouts | Med | Skip (threshold) |
| Missing join predicate (accidental Cartesian) | Product join; engine flags it at compile time (`NoJoinPredicate`) | Runtime warning; static approximation risky | Skip |
| Deprecated `*=`/`=*` outer-join operators | Legacy syntax can silently change join semantics/plan shape across engine versions | High | T2-14 |
| `GROUP BY ALL` / `GROUP BY <ordinal>` / `COMPUTE`/`COMPUTE BY` | Deprecated constructs; COMPUTE bypasses normal set-based aggregate optimization entirely | High | T2-17 (COMPUTE only; GROUP BY ordinal is lint-tier) |
| Halloween Protection eager spool from self-referencing DML (`INSERT`/`UPDATE`/`DELETE`/`MERGE` whose source reads the target table) | Forces a blocking eager spool; distinct mechanism from UDF-in-DML | High | T2-15 |
| Window function without a Partition-Order-Covering (POC) index (key ordered PARTITION BY → ORDER BY → covering SELECT columns) | Forces a Sort operator per partition | High (catalog: index key order + OVER clause columns) | Skip (index-advisor scope; revisit if scope grows) |
| Partition elimination defeated by non-literal/wrapped predicate on the partitioning column | Static elimination needs a literal; a parameter/variable/expression forces dynamic elimination or touches every partition, independent of any index | Medium (needs partition function/scheme catalog modeling) | Skip (revisit if a target uses partitioning) |
| Always Encrypted: comparison against a randomized-encryption column | Range predicates forbidden entirely; even `=` requires matching encryption metadata on the parameter or silently fails to bind / forces client-side evaluation | High (catalog: `sys.columns.encryption_type`) | Skip (revisit if a target uses the feature) |
| Temporal table history-table missing the current table's index set | `FOR SYSTEM_TIME AS OF/BETWEEN` rewrites to UNION ALL; a sargable predicate on the current side does nothing for the history side | High (catalog: compare index sets across `parent_id`/`history_table_id`) | T2-16 |
| Batch Mode on Rowstore eligibility loss (compat ≥150) | Specific constructs/types silently disqualify an otherwise-fine query from batch execution | Medium (no canonical disqualifier list published) | Skip (parked pending a trustworthy exhaustive list) |

## E. Cursors, loops, table variables, temp objects

| Pattern | Why it hurts | Static? | Disposition |
|---|---|---|---|
| Cursors generally / WHILE-fetch RBAR | Row-at-a-time vs set engine; per-fetch overhead | High | T2-10 |
| Cursor with default options (global, updatable, **dynamic** — the slowest type) | Dynamic re-evaluates membership per fetch; dynamic and keyset cursors force whole-plan serial | High | T2-10 |
| Cursor not FORWARD_ONLY / with OPTIMISTIC concurrency | Scrollability worktable; optimistic adds per-row version/value checking | High | T2-10 |
| `LOCAL FAST_FORWARD` absence as the crisp subrule | The one cursor form with minimal overhead; anything else is a flag (note: even FAST_FORWARD inhibits parallelism) | High | T2-10 |
| Table-variable **modification** (INSERT/UPDATE/DELETE @t) | Forces the **entire plan** serial; read-only use of @t does not | High | T2-10 |
| Table variable joined at scale | No column statistics ever; fixed low estimate cascades (1 row pre-2019; deferred compilation 2019+ helps count, still no histogram) | Med ("scale" is runtime) | T2-10 (flag join only) |
| Temp table vs table variable mis-choice (both directions) | Temp tables trigger recompiles; table variables give no stats — tradeoff is workload-dependent | Med | Skip |
| DDL inside procs (create/drop table or index, SELECT INTO) | Recompiles, plan-cache churn, compile locks | High | Skip (often legitimate) |

## F. Hints, options, session settings

| Pattern | Why it hurts | Static? | Disposition |
|---|---|---|---|
| Proc-level `WITH RECOMPILE` | Compiles on every call: CPU burn, no plan reuse, invisible to cache-based monitoring; catalog flag `sys.sql_modules.is_recompiled` | High | T2-13 |
| OPTION(RECOMPILE) overuse | Per-execution compile CPU; plan-cache blindness | Med ("overuse" is a runtime judgment) | Skip (inventory only; also the *neutralizing guard* for T2-6/7) |
| Index / FORCESEEK / FORCESCAN / join hints hard-coded | Freezes access-method and join choices as data changes; index hints also suppress missing-index requests | High | Skip (inventory-grade) |
| MAXDOP 1 / OPTION hints scattered in app queries | Permanent serial by decree | High | Skip (inventory-grade) |
| QUERYTRACEON / session trace flags in code | Per-statement optimizer hijack; also drops the query to downlevel CE when used for that | High | Skip |
| SET ROWCOUNT instead of TOP | Optimizer can't apply the row goal as well as TOP | High | Skip (lint) |
| NOLOCK / READ UNCOMMITTED | Dirty reads, **missed and double-read rows** under allocation scans; masks the real blocking problem | High | Skip (correctness) |
| ANSI_WARNINGS / ARITHABORT OFF in modules | Blocks use of indexed views and indexes on computed columns | High | Skip (revisit if corpus shows indexed views) |
| Forced plans / plan guides / forced parameterization | Frozen strategy; forced parameterization also breaks filtered-index matching | Runtime-ish (cache attributes) | Skip |
| Downlevel CE per query (hint or compat level) | Different estimation model per query | High (hint text) | Skip (bench already sweeps CE modes) |
| Implicit transactions left open | Blocking, log growth (app/connection setting) | Runtime | Skip |
| Lengthy work (loops, RBAR, external calls) between an error and its ROLLBACK | Extends lock hold duration, blocks other sessions | High (control-flow scan) | T2-17 |
| `BEGIN TRANSACTION` with no reachable ROLLBACK/COMMIT on some code path | Orphaned transaction holds locks indefinitely | High (dataflow) | T2-17 |
| `WAITFOR DELAY`/`WAITFOR TIME` inside a routine or batch | Holds a worker thread idle; contributes to worker-thread exhaustion under load | High | T2-17 |
| Query/order hint frequency counters (`sys.dm_exec_query_optimizer_info`) | Aggregate signal that hint usage is widespread | Runtime (counts since restart) | Skip (static form already covered by the hard-coded-hints row above) |

## G. Schema/catalog-side seeds (no query text involved)

| Pattern | Why it hurts | Static? | Disposition |
|---|---|---|---|
| Same column name, different types across tables | Every future join on the pair converts one side | High | T1-3 |
| MAX-typed parameters/variables vs (n) columns | Predicate can't be pushed to the storage engine even when base types match; no seek; giant memory grants | High | T1-5 |
| Parameter declared longer than compared column | Memory-grant inflation | High | T1-5 |
| MAX-typed columns as predicate/join targets | LOB, off-page, can't be an index key, blocks online reindex | High | T1-5 |
| Untrusted (NOCHECK) FK/CHECK constraints | Constraint re-enabled without WITH CHECK stays untrusted → optimizer won't use it for join elimination; catalog flag `is_not_trusted` — invisible in any query text | High | T2-11 |
| Missing FK/RI constraints entirely | No constraint-based join elimination possible | High | Skip (design advice) |
| Cascading FK actions (ON DELETE/UPDATE CASCADE) | Hidden multi-table work and wide locks per DML; serial zones | High | T2-11 |
| Unindexed FK columns | Every RI check / join against the FK side scans | High | Skip (index-advisor space) |
| Heaps / missing clustered index / clustering-key width / GUID clustering keys | Forwarded records, no ordered access; every NC index inherits the clustering key → bloat | High | Skip (index-advisor space) |
| Duplicate/overlapping/excessive indexes; ≥5 indexes touched by one DML | Write amplification per modification | High/Runtime | Skip (index-advisor space) |
| FLOAT/REAL as keys or indexed columns | Approximate seeks, conversion issues | High | Skip (design advice) |
| varchar(1)/(2); DATETIME where DATE/TIME suffices | Row width, marginal IO | High | Skip (lint) |
| sql_variant columns existing at all | Per-row internal type resolution; comparison chaos | High | T1-3 (comparison rule only) |
| Polymorphic associations / god-object wide tables | No FK constraint possible; conditional joins unoptimizable | Med (heuristic) | Skip |
| Updating PK / clustered-key columns | Row movement + every NC index updated + RI maintenance | High | Skip (rare, judgment) |
| Indexed view present but queries lack NOEXPAND | On non-Enterprise editions a matched indexed view is ignored without explicit `WITH (NOEXPAND)`; even Enterprise matching is fragile | Med-High | Skip (parked; edition-dependent, matching-logic FP risk) |
| Missing schema prefix on object references | Per-user plan-cache entries + compile locks (SCH-M contention under load) | High | Skip (lint with mild teeth) |
| `sp_` prefix on user procs | Master-db lookup first + SCH-S | High | Skip (marginal) |
| Identity with odd seed/increment or near range end | Insert-failure risk more than perf | High | Skip |

## H. Dynamic SQL and plan-cache hygiene

| Pattern | Why it hurts | Static? | Disposition |
|---|---|---|---|
| Value (not identifier) concatenated into proven-constant dynamic SQL | Unparameterized: plan-cache pollution, per-literal compiles, compile storms | High (taint analysis exists) | T2-12 |
| `EXEC(string)` where sp_executesql with params was possible | Same + injection surface | High | T2-12 |
| Unparameterized ad-hoc literals generally | One plan per literal value; cache bloat | Med statically | Skip (app-side) |
| Partial parameterization (literal+param mix) | Defeats auto-parameterization | Med | Skip |
| Plan-cache duplication / single-use plan floods | Cache-state symptom of the above (measured as % duplicate / % single-use plans) | Runtime | Skip |

## I. Remote and exotic

| Pattern | Why it hurts | Static? | Disposition |
|---|---|---|---|
| Linked-server / 4-part-name predicates | Remote query is a serial zone; remote statistics unavailable without elevated remote permissions → guess estimates; rows dragged across the wire | Med-High | Skip (rare in corpus; revisit) |
| Cross-database queries | Estimation and security-context costs | Med | Skip |
| Full-text predicates, spatial operations | Opaque, costly operators; spatial estimates are guesses | Runtime-ish | Skip |
| Row-level security predicates injected into plans | Hidden predicate inflation | Runtime | Skip |

## J. Runtime-only (listed so nobody re-asks; structurally out of scope)

Parameter-sniffing variance (min/max worker time or rows varying ≫ average
across executions); execution frequency; long-running/low-CPU patterns
(waits: blocking, linked servers, slow client drain); spills to tempdb;
unused or oversized memory grants; expensive key/RID lookups, sorts, index
spools (optimizer building an index on the fly per execution), table spools;
busy loops (operator rebinds+rewinds ≫ estimated rows); row-estimate vs
actual mismatch; compilation timeouts, compile memory, compile CPU; stale or
missing statistics; many-to-many merge joins (hidden worktable); backward
scans (serial for that operator); columnstore row-mode fallback; adaptive-join
flips; trivial-plan-as-top-consumer (trivial plans skip full optimization and
missing-index requests); SELECTs generating writes (worktables/version
store); cache duplication and single-use plan rates. All require live
execution or a populated plan cache. The oracle deliberately stays
compile-only; the study's credibility rests on that line.

---

## Appendix 1 — Plan-XML markers (for oracle probes and rule design)

Compile-only SHOWPLAN_XML exposes all of these; none require execution.

| Marker | Meaning / use |
|---|---|
| `CONVERT_IMPLICIT(...)` around a column reference | The core conversion oracle: confirmed iff applied to the COLUMN side of the probed predicate |
| `PlanAffectingConvert` warning, `ConvertIssue="Seek Plan"` | Engine's own "conversion killed a seek" flag — equivalent of ScanForced, but symmetric (doesn't say which side) |
| `PlanAffectingConvert` warning, `ConvertIssue="Cardinality Estimate"` | Conversion poisoned row estimates (separate damage channel from access method) |
| `NonParallelPlanReason` attribute | Whole plan forced serial; value names the cause (e.g. scalar UDF) |
| Table Valued Function operator | MSTVF present as fence (an iTVF dissolves into base operators instead); estimated rows shows the fixed 1/100 guess |
| `ContainsInterleavedExecutionCandidates` | 2017+ marker: an MSTVF in this plan is eligible for interleaved execution (correlated ones are not) |
| `ContainsInlineScalarTsqlUdfs` | 2019+ marker: scalar UDF(s) were inlined — the finding's severity downgrade signal |
| `StatementOptmEarlyAbortReason` = `TimeOut` / `MemoryLimitExceeded` | Optimizer gave up early; plan is a best guess (query too complex) |
| `StatementOptmLevel="TRIVIAL"` | Skipped full optimization (also suppresses missing-index requests) |
| `NoJoinPredicate` warning | Accidental Cartesian product detected at compile |
| `UnmatchedIndexes` / `Parameterization` element | A filtered index existed but couldn't be used because the query was parameterized |
| `MissingIndexGroup` | Optimizer's own index request (schema-dependent; not our verdict source) |
| `ColumnsWithNoStatistics` / `ColumnsWithStaleStatistics` (2022) | Optimizer estimating blind |
| `ScanDirection="BACKWARD"` | Backward scan — serial for that operator |
| Merge join with `ManyToMany="1"` | Hidden worktable per execution |
| `CardinalityEstimationModelVersion` | Which CE compiled the plan (downlevel detection) |
| Cursor elements: `CursorConcurrency="Optimistic"`, non-FORWARD_ONLY, dynamic/keyset types | Cursor option costs; dynamic/keyset also force serial |
| Index spool / table spool operators with cost share | On-the-fly index build / repeated-work materialization |
| `Lookup="1"` on an index operation | Key/RID lookup (non-covering index) |

## Appendix 2 — Canonical forced-serial lists (encodable, finite)

**Whole plan goes serial** when any of these is present:
- T-SQL scalar UDF (pre-inlining), including one referenced by a computed
  column, DEFAULT, or CHECK constraint on any table the query touches.
- CLR scalar UDF with data access.
- Modification of a table variable (INSERT/UPDATE/DELETE @t). Reading a table
  variable does NOT force serial.
- Dynamic or keyset cursors (and FAST_FORWARD cursors inhibit parallelism).
- System-table access.
- Certain intrinsics: `IDENT_CURRENT`, `ERROR_NUMBER`, `@@TRANCOUNT`,
  `OBJECT_ID`, and family. Note: `@@ERROR` and `@@NESTLEVEL` do **not**
  force serial — the list is specific, don't generalize it.

**Serial zone only** (part of the plan, not all of it):
- TOP (row goals), multi-statement TVF reference, recursive CTEs,
  backward-ordered scans, global scalar aggregates, multi-consumer spools,
  sequence functions.

## Appendix 3 — Scalar UDF inlining (2019+) blocker list

A UDF is NOT inlined (and the pre-2019 costs all still apply) when its body
contains any of: time-dependent intrinsics (GETDATE class), WHILE loops,
TRY/CATCH, RETURN in multiple places / certain control flow, table variable
or table-access patterns outside the supported shapes, invocation from a
computed column or check constraint, recursion, use as a DEFAULT, reference
to a non-inlineable UDF, or `WITH SCHEMABINDING`-related edge cases. The
inlineability bit is per-UDF observable in the catalog
(`sys.sql_modules.is_inlineable`) — prefer reading that flag over
re-implementing the blocker scan, and use the body scan only to *explain*
why inlining fails.

## Appendix 4 — Engine-version mitigation matrix

| Version | Change | Effect on rules |
|---|---|---|
| 2014 (new CE) | MSTVF fixed estimate 1 → 100 rows | Fence still present; misestimate direction changes |
| 2016 SP1 | (baseline for most behavior above) | — |
| 2017 | Interleaved execution for MSTVFs | Rescues **uncorrelated** MSTVF references only; correlated APPLY and UPDATE-plan references stay broken |
| 2019 | Scalar UDF inlining (FROID); table-variable deferred compilation | Inlining subject to Appendix 3 blockers; deferred compilation fixes @t rowcount, still no histogram |
| 2022 | Anti-pattern XE; `ColumnsWithStaleStatistics` in showplan; CE feedback | XE list is 4 pattern types (`TypeConvertPreventingSeek`, `LargeIn`, `LargeNumberOfOrInPredicate`, `NonOptimalOrLogic`, plus undocumented `Max`), listener-only; confirmed unchanged as of this audit |
| 2025 (compat 170) | Parameter Sensitive Plan (PSP) optimization extended to DML (DELETE/INSERT/MERGE/UPDATE), tempdb tables, multiple eligible predicates per table; Query Store Hints can target individual PSP variants | Nuances (does not invalidate) a blanket `ScanForced` verdict: under compat 170 the same predicate can get a different per-parameter-value plan. State this as a caveat when the study targets a compat-170 database |
| All | `OPTION(RECOMPILE)` embeds literal parameter values (2008 SP1 CU5+) | Neutralizes catch-all and local-variable findings — mandatory guard |

## Appendix 5 — Production-copy inventory (aggregate counts, 2026-08-15)

Local restored production copy, ~5,000 modules. Crude LIKE-heuristic counts —
base-rate signal for prioritization, not findings. No schema details recorded.

Module population: 3,674 procs · 889 inline TVFs · 195 scalar UDFs ·
41 MSTVFs · 137 views · 51 triggers · 835 tables.

| Signal | Count |
|---|---|
| Procs referencing a scalar UDF | 2,693 (73%) |
| Inline TVFs referencing a scalar UDF (inlining spreads it to all callers) | 603 |
| Scalar UDFs in column DEFAULTs | 37 |
| Scalar UDFs in CHECK constraints | 4 |
| MSTVF references total | 126 (98 from procs, 20 from other TVFs, 5 from scalar UDFs, 3 from iTVFs) |
| Views referencing other views | 57 |
| Modules with ISNULL in WHERE | 516 |
| Modules with COALESCE in WHERE | 661 |
| Modules with catch-all `OR @p IS NULL` | 425 |
| Modules with `NOT IN (SELECT` | 346 |
| Modules with DECLARE CURSOR | 197 |
| Modules with table variables | 821 |
| Modules with temp tables | 331 |
| Modules with SELECT * | 2,063 |
| Modules with SELECT DISTINCT | 389 |
| Modules with leading-wildcard LIKE | 54 |
| Modules with RTRIM-family functions in WHERE | 96 |
| Modules with UPPER/LOWER in WHERE | 15 |
| Modules with EXEC( dynamic SQL | 123 |
| Modules with sp_executesql | 51 |
| Modules with OPTION(RECOMPILE) | 70 |
| Modules with proc-level WITH RECOMPILE | 2 |
| Modules with MERGE | 19 |
| Modules with NOLOCK | 17 |
| Modules with CROSS APPLY | 124 |
| Modules with TOP 100 PERCENT | 13 |
| sql_variant columns | 4 |
| Computed columns referencing a UDF | 0 |
| Column names appearing across tables with differing type/length/collation | 148 |

## Appendix 6 — Completeness audit (2026-08-15)

A second pass independently re-derived each of the four research sweeps and
diffed the result against this document, specifically hunting for coverage
gaps rather than re-surveying from scratch. Findings from that audit are
folded into the tables above; this appendix records what changed and what
remains open, so a third pass doesn't have to redo the diff.

**Folded into the tables:** deprecated `*=`/`=*` join operators, `COMPUTE`/
`COMPUTE BY`, Halloween Protection self-referencing DML, temporal table
history-index gaps, CHECK-constraint-referencing-a-function and
non-persisted-computed-column (schema-scan variants distinct from the
plan-based UDF findings), the transaction-hygiene pair (lengthy work before
ROLLBACK; unreachable ROLLBACK/COMMIT), `WAITFOR DELAY/TIME`, and — most
important — the **JSON_VALUE precision guard**, which is a correction to an
already-shipped rule, not just a new candidate: verify it before the next
sargability-rule release.

**Explicitly checked and confirmed NOT gaps** (so they aren't re-litigated):
STRING_SPLIT/OPENJSON's fixed 50-row cardinality estimate (excluded on
principle — static verdicts never depend on the cardinality estimator);
ODBC/JDBC driver default-Unicode string binding (root cause of a pattern
already covered, not a new pattern); APPLY-vs-JOIN rewriting (requires
semantic query-rewrite reasoning, not static analysis); native/In-Memory OLTP
anti-patterns and RBAR string concatenation in loops (subsumed by existing
cursor/RBAR items); the SQL Server 2022 anti-pattern XE's enum membership
(confirmed unchanged: still exactly 5 members).

**Parked with an explicit reason** (weren't missing from the survey, just
correctly gated out — listed in the tables above with their skip reason):
partition elimination defeat, Always Encrypted comparison restrictions,
Batch Mode on Rowstore eligibility loss (no canonical disqualifier list
exists to build against), window-function POC index shape (index-advisor
scope, not a query defect).

**Left genuinely unverified, not confirmed either way:** one rule-catalog
source's performance-tagged rule list could not be reached this pass (site
unreachable) — treat its earlier-claimed coverage as unconfirmed rather than
complete until a future session can reach it. One community rule-catalog
project's total rule count needed a correction (was overcounted by 2) — pure
bookkeeping, no pattern implications.

**New engine context, not a detection candidate:** SQL Server 2025 (compat
170) extended Parameter Sensitive Plan optimization to DML statements and
tempdb tables — recorded in Appendix 4 as a caveat for the study's
`ScanForced` narrative on compat-170 targets, not as something to detect.
