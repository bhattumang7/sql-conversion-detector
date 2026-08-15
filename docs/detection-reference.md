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
  event). **Named and re-verified from source 2026-08-16 — see "Named
  incumbents" below.** The claim survives intact; the type-binding analyzer is
  `SqlServer.Rules` (`SRP0016`), and it is symmetric exactly as described.
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

### Named incumbents (cloned and read at source level, 2026-08-16)

Six analyzers surveyed. Two are out of scope by dialect or seriousness; the
rest set the competitive floor. **None resolves conversion direction,
collation, or lineage, and none has any plan oracle** — every finding any of
them emits is an unverified static claim.

| Tool | Type-aware? | Status | Conversion rule |
|---|---|---|---|
| `SqlServer.Rules` (DacFx; dormant original + an actively developed superset fork shipping a CLI, IDE extensions and an MCP server) | **Yes** — DacFx semantic model, **base tables only** | Active (fork) | `SRP0016`, symmetric |
| SonarQube T-SQL plugin (ANTLR `grammars-v4`) | No | Dormant since 2024 | none |
| Oracle PL/SQL analyzer | No (block-scope symbol table, no catalog) | Active | none; **no T-SQL support at all** |
| Rust multi-dialect linter, 282 rules | Parses DDL types into a field it **never reads** | Active | declared stub, never fires |
| DacFx rule sample, 9 rules | No | Abandoned 2017 | none |
| WinForms regex scorer, 9 regexes | No | Toy, 0 stars | none |

**`SRP0016` is the entire incumbent state of the art**, and its verdict is one
line: `if (!Comparer.Equals(datatype1, datatype2)) → problem`. No data-type
precedence table exists anywhere in that codebase. Limits, all read from source:

* **Symmetric** — `varchar` col vs `N'x'` (seek lost) and `nvarchar` col vs
  `'x'` (harmless) report identically, at identical severity.
* **No collation field exists** in its type carrier — the `RangeSeek` vs
  `ScanForced` split has no counterpart.
* **Lineage architecturally excluded** — the column-type lookup is hardcoded to
  the model's *table* schema, so views/TVFs resolve to nothing and the check is
  silently skipped. A sibling rule carries the comment
  `// most likely the base is a view.... /sigh`.
* **Not index-aware** — it cannot say whether the converting column is indexed,
  i.e. whether the conversion costs anything at all.
* **WHERE clauses of SELECT statements only** — JOIN `ON` type mismatch
  (checklist T1-3) is undetected, despite its own doc text claiming joins.
* **Literal typing is wrong**: integer literals are typed by *magnitude*
  (`0..255 → tinyint`), but SQL Server types an unsuffixed integer literal as
  `int`/`bigint`. `WHERE IntCol = 1` is a shipped false positive, locked in by
  that repo's own committed test expectations.
* Resolution failures are swallowed by a bare `catch` carrying a literal
  `// TODO: PROPERLY LOG THIS ERROR`, yielding silent zero findings.

**Our oracle, run 2026-08-16** (SQL 2022, `SQL_Latin1_General_CP1_CI_AS`, all
three columns indexed, compile-only SHOWPLAN_XML) across the three cases
`SRP0016` cannot tell apart. It fires on **all three** and is right about
**one**:

| Predicate | Plan marker | Result | `SRP0016` |
|---|---|---|---|
| `IntCol = 1` | `CONVERT_IMPLICIT(int,[@1],0)` — on the autoparameterized **literal** | Index Seek | fires — **FP** |
| `NvarCol = 'x'` | `CONVERT_IMPLICIT(nvarchar(4000),[@1],0)` — on the **literal** | Index Seek | fires — **FP** |
| `VarCol = N'x'` | `CONVERT_IMPLICIT(nvarchar(50),[…].[VarCol],0)=[@1]` — on the **column** | Index Scan | fires — TP |

The canonical three-case demo for the study: incumbent precision on its own
flagship rule is 1/3, and the discriminator is exactly the column-side marker
our oracle already keys on. Note both false positives still emit a real
`CONVERT_IMPLICIT` — so even a plan-reading tool that greps for the marker
without checking *which side* it wraps reproduces both errors. Side-of-the-
conversion is the whole game.

**Most citable line found**, from the 282-rule Rust linter's own source, at the
stub where its implicit-conversion rule should be — an actively developed,
multi-dialect analyzer declaring the detection infeasible without a catalog:

> *"Implicit type conversion detection requires schema knowledge. Without
> actual column type information, heuristic detection produces false
> positives."*

**Field-confirmed anti-patterns that validate our own gates:** the SonarQube
plugin calls `removeErrorListeners()` on both lexer and parser and never
consults `numberOfSyntaxErrors`, so a file its grammar cannot parse is analyzed
against ANTLR's error-recovery tree and **reported clean** — direct evidence for
why `ParseHealthReport.PassesDialectSniffing` must fail loudly. Its
non-sargability rule ships as `BETA` with no oracle and no near-miss, and its
own "compliant" example is invalid T-SQL.

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
| `ContainsInlineScalarTsqlUdfs="1"` on `StmtSimple` | 2019+ marker: scalar UDF(s) were inlined — the finding's severity downgrade signal. Oracle-verified (T1-1's `ScalarUdfVerifier`, natural probe, no hint). |
| `<UserDefinedFunction FunctionName="[db].[schema].[fn]">` element | Scalar UDF call NOT folded away — the counterpart to `ContainsInlineScalarTsqlUdfs`; absent exactly when that attribute is present. Oracle-verified. **Surprising, load-bearing discovery**: `OPTION (USE HINT('DISABLE_TSQL_SCALAR_UDF_INLINING'))` reliably forces this element for a call made directly at the top level, but does NOT propagate into a scalar UDF called from inside a view's own definition — a trivially-inlineable function referenced through a view still dissolves away under the hint. `ScalarUdfProbeBuilder` always probes the underlying function directly (never through the referencing view) specifically because of this. |
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

---

## Appendix 7 — Complete incumbent rule inventory (survey of 2026-08-16)

The full un-filtered rule catalogs of every surveyed analyzer, extracted
mechanically from source (rule-id constants, display-name strings, and shipped
rule-metadata files), not from prose summaries. This exists so the "what else
do they detect?" question is answered from data rather than re-surveyed, and so
nothing is lost to a summariser's judgement about what looked relevant. **Every
rule is listed, including whole categories out of our scope.** Disposition
notes are opinions; the lists are facts.

Totals: `SqlServer.Rules` (DacFx) 120 · SQL Code Guard (T-SQL, imported by the
SonarQube plugin) 148 · Rust multi-dialect linter 282 · SonarQube plugin's own
22 (16 enabled for T-SQL) · Oracle PL/SQL analyzer 57 · DacFx sample 9.
**~638 rules total.** None of them resolves conversion direction, collation, or
lineage; none has a plan oracle.

### 7.1 What the full sweep changed

Re-reading the *complete* catalogs (rather than the performance-tagged subset)
surfaced candidates the first pass missed, because they sit under Design or
Execution-Issue headings rather than Performance:

* **Join predicate incomplete vs. the backing foreign key** (`SRD0020`) — join
  missing a backing FK, or joining on fewer columns than the FK defines. The
  partial-composite-FK case is a genuine correctness-and-plan defect and is
  **pure catalog work**, which is our machinery exactly. Strongest single find
  of the sweep.
* **Argument-vs-parameter type mismatch on procedure calls** (`EI001`
  variable, `EI002` literal) — the conversion stream's sibling on the call
  boundary. Already queued at T1-3; the sweep confirms an incumbent attempts it
  from *parsed DDL*, which is the wheel CLAUDE.md says not to rebuild. Resolved
  from `sys.parameters` we would be strictly better for near-zero extra cost.
* **Temp-table collation vs. tempdb/database collation** (`SRD0062`,
  `PE`-adjacent) — a conversion *seed* on every join between a temp table and a
  user table. We are the collation-aware tool; nobody treats this as a
  conversion source. Pairs with the already-queued "column collation ≠ database
  collation".
* **SET options that silently disable plan features** (`SRD0088`
  NUMERIC_ROUNDABORT OFF, `SRD0089` QUOTED_IDENTIFIER ON) — framed everywhere
  as hygiene, but the actual consequence is *indexed views and filtered indexes
  cannot be used*. That is a plan-shape consequence, catalog-verifiable
  (`sys.sql_modules.uses_quoted_identifier`), and materially different from the
  style rule it's usually filed as.
* **`TOP(100) PERCENT` is ignored by the optimizer** (`SRD0081`) — commonly
  written to "force" ordering in a view; the optimizer discards it. Syntactic,
  near-zero FP.
* **`ORDER BY` in a view or inline TVF** (`EI030`) — same family: the ordering
  is not guaranteed and the sort may still be paid for.
* **`ISNULL`/`COALESCE` arguments of differing datatypes** (`SRD0043`) — feeds
  directly into the queued T1-4 COALESCE result-type inference work.
* **`IF` statements containing queries inside procedures** (`SRD0063`) — an
  estimation/recompile concern nobody frames as such.

### 7.2 `SqlServer.Rules` (DacFx) — all 120

**Performance (28).** SRP0001 nested views · SRP0002 leading-wildcard LIKE ·
SRP0003 DISTINCT inside aggregate · SRP0004 results returned from trigger ·
SRP0005 SET NOCOUNT ON missing · SRP0006 `<>`/`!=` in WHERE · SRP0007 cursor
not closed · SRP0008 cursor not deallocated · SRP0009 function wrapping column
in WHERE · SRP0010 UDF in UPDATE/INSERT/DELETE (Halloween) · SRP0011 NOT IN ·
SRP0012 un-indexed column in IN predicate · SRP0013 OUTER JOIN used for
existence test · SRP0014 table variable in JOIN · SRP0015 column arithmetic in
WHERE · **SRP0016 mismatched types either side of an equality (the incumbent
conversion rule)** · SRP0017 UPDATE of primary-key column · SRP0018 high join
count · SRP0020 table missing clustered index · SRP0021 parameter modified
before use · SRP0022 WITH RECOMPILE vs OPTION(RECOMPILE) · SRP0023 COUNT used
for existence · SRP0024 correlated subquery · SRP0025 SELECT * in EXISTS ·
SRP0026 cross-server join · **SRP0027 explicit CAST/CONVERT on column** ·
SRP0028 explicit RANGE window frame · SRP0029 implicit RANGE window frame ·
SRP0030 cursor without FAST_FORWARD.

**Design (88).** SRD0001 no natural key · SRD0002 no primary key · SRD0003 wide
/GUID PK · **SRD0004 both sides of an FK should be indexed** · SRD0005 (n)char
misuse · SRD0006 SELECT * · SRD0009 multi-statement action without transaction ·
SRD0011 `= NULL` comparison · SRD0012 variable declared never used · SRD0013
missing TRY/CATCH · SRD0014 TOP without ORDER BY · SRD0015 INSERT without
column list · SRD0016 unused parameter · SRD0017 DELETE without row limit ·
SRD0018 UPDATE without row limit · SRD0019 joining tables with views ·
**SRD0020 join missing backing FK or missing FK columns** · SRD0021 EXISTS
instead of IN · SRD0024 EXEC with string literal · SRD0025 ORDER BY ordinal ·
SRD0026 var-length type without length · SRD0027 DECIMAL without precision ·
SRD0028 unprefixed column names · SRD0030 avoid hints · SRD0031 CHARINDEX in
WHERE · SRD0032 OR in WHERE (sargable) · SRD0033 avoid cursors · SRD0034 NOLOCK
· SRD0035 WAITFOR in module · SRD0036 SET ROWCOUNT · SRD0038 alias all sources ·
SRD0039 fully-qualified names · SRD0041 SELECT INTO · **SRD0043 ISNULL/COALESCE
args of differing type** · SRD0044 RAISERROR ≥18 needs WITH LOG · SRD0045 too
many indexes · SRD0046 real/float columns · **SRD0047 same-name columns of
differing type/size** · SRD0050 always-TRUE/FALSE comparison · SRD0051
TEXT/NTEXT/IMAGE · **SRD0052 duplicate/overlapping index** · **SRD0053 object
collation differs from database** · SRD0055 object created with invalid options
· SRD0056 @@IDENTITY vs SCOPE_IDENTITY · SRD0057 DDL mixed with DML · SRD0058
named parameters on proc calls · SRD0060 procedure grants itself permissions ·
SRD0061 invalid database options · **SRD0062 temp-table collation vs tempdb** ·
**SRD0063 IF containing queries in a proc** · SRD0064 cache repeated
GETDATE/SYSDATETIME · SRD0065 NotForReplication · SRD0066 BEGIN/END in
conditionals · SRD0067 keyword casing · SRD0068 statement semicolons · SRD0069
XACT_ABORT with explicit transactions · SRD0071 CASE without ELSE · SRD0072
self-assignment · SRD0073 repeated NOT · SRD0074 weak hashing (MD5/SHA1) ·
SRD0075 hard-coded credentials · SRD0076 identical expressions either side of a
comparison · SRD0077 FETCH variable count mismatch · SRD0078 single-char alias ·
SRD0079 single-char variable · SRD0080 TOP expression parenthesisation ·
**SRD0081 TOP(100) PERCENT ignored by optimizer** · **SRD0091 derived-table
ORDER BY does not guarantee ordering** · SRD0082 DATEFORMAT changed · SRD0083
DATEFIRST changed · SRD0084 CONCAT_NULL_YIELDS_NULL ON · SRD0085 ANSI_NULLS ON ·
SRD0086 ANSI_PADDING ON · SRD0087 ANSI_WARNINGS ON · **SRD0088
NUMERIC_ROUNDABORT OFF — required for indexed views** · **SRD0089
QUOTED_IDENTIFIER ON — required for indexed views and filtered indexes** ·
SRD0090 SET FORCEPLAN OFF · SRD0092-0095 named constraints on temp tables (PK/
DEFAULT/FK/CHECK) · **SRD0096 potential SQL injection** · SRD0700-0706 project-
file/database settings (PAGE_VERIFY, Query Store state and capture mode, target
recovery time, AUTO_CLOSE, AUTO_SHRINK).

**Naming (4).** SRN0001-0004 — object-name prefix conventions.

### 7.3 SQL Code Guard (T-SQL) — all 148

The richest T-SQL catalog found. Imported as metadata by the SonarQube plugin;
the engine itself is closed-source Redgate software, so detection quality is
unverified.

**Performance, `PE` (23).** PE001 proc schema not specified · PE002 table/view
schema not specified · PE003 SELECT INTO · PE004 INDEX HINT · PE005 JOIN HINT ·
PE006 TABLE HINT · PE007 QUERY HINT · PE008 SET NOCOUNT OFF · PE009 no SET
NOCOUNT ON before DML · PE010 interleaved DDL and DML · PE011 PRINT in trigger ·
**PE012 settings causing procedure recompilation** · PE013 COUNT instead of
EXISTS · PE014 SET FORCEPLAN · PE015 cursor not forward-only despite no
FETCH FIRST/LAST/PRIOR · PE016 cursor opened not deallocated · **PE017
incorrect usage of const UDF** · PE018 cursor not readonly · PE019 EXISTS
instead of IN · PE020 INSERT INTO with ORDER BY · PE021 WITH RECOMPILE ·
**PE022 foreign key is not trusted** · PE023 DDL without schema name.

**Execution issues, `EI` (33).** **EI001 incompatible variable type for
procedure call** · **EI002 incompatible literal type for procedure call** ·
EI003 non-scalar subquery used as scalar · EI004 extra parameter passed · EI005
unnamed call after named call · EI006 required parameter not passed · EI007/
EI008 OUTPUT parameter mismatch · EI009 too many parameters · EI010-EI013
OPEN/FETCH/CLOSE/DEALLOCATE of undefined cursor · EI014 FETCH from cursor with
`*` (uncheckable) · EI015 incorrect fetch-variable count · EI016 proc reference
in other database · EI017 hard-coded current database name · EI018 missing
parameter names · EI019 BEGIN TRAN without ROLLBACK · EI020 ROLLBACK without
BEGIN · EI021 close of unopened cursor · EI022 fetch from unopened cursor ·
EI023 cursor update/delete but not declared updatable · EI024 `sp_` prefix ·
EI025 proc executed without collecting result · EI026 function reference in
other database · EI027 table/view reference in other database · EI028 NOT NULL
column added without default · EI029 ISNUMERIC() · **EI030 ORDER BY in view or
inline TVF** · **EI031 relying on INSERT…EXEC** · EI032 xp_cmdshell · EI033
dynamic SQL without EXECUTE AS.

**Best practice, `BP` (24).** BP001 index type unspecified · BP002 ORDER BY
constants · BP003 SELECT in trigger · BP004 INSERT without column list · BP005
SELECT * · BP006 TOP without ORDER BY · **BP007 var-length type without
explicit length** · **BP008 CAST/CONVERT to var type without length** · BP009
var types of length 1-2 · BP010 @@IDENTITY · BP011 NULL comparison/arithmetic ·
BP012 CASE without ELSE · BP013 EXECUTE('script') · BP014 NULL option
unspecified · BP015 cursor scope unspecified · BP016 RETURN without result code
· BP017 DELETE without WHERE/INNER JOIN · BP018 UPDATE without WHERE/INNER JOIN
· **BP019 foreign key is disabled** · **BP020 column created with ANSI_PADDING
OFF** · BP021 table without clustered index · BP022 money/smallmoney · BP023
float/real · **BP024 sql_variant**.

**Deprecated, `DEP` (26).** DEP001 table hint without WITH · DEP002 WRITETEXT/
UPDATETEXT/READTEXT · DEP003 GROUP BY ALL · **DEP004 COMPUTE / COMPUTE BY** ·
DEP005 FASTFIRSTROW hint · DEP006 SETUSER · DEP007 TAPE backup device · DEP008
BACKUP/RESTORE passwords · DEP009-DEP012 DBCC DBREINDEX / CONCURRENCYVIOLATION
/ INDEXDEFRAG / SHOWCONTIG · DEP013 deprecated SET options · DEP014 SET
ROWCOUNT · DEP015 READONLY/READWRITE · DEP016 TORN_PAGE_DETECTION · **DEP017
non-ANSI `*=` / `=*` join** · DEP018 ALL in GRANT/DENY/REVOKE · DEP019
deprecated system table/view · DEP020 numbered procedures · DEP021 string
literal column aliases · DEP025 deprecated system stored procedure · DEP026
three/four-part column references in SELECT list · DEP027 deprecated system
function · **DEP028 module created with ANSI_NULLS/QUOTED_IDENTIFIER OFF**.

**Misc, `MI` (8).** MI001 unused table variable · MI002 unused temp table ·
MI003 unqualified column name · MI004 sp_executesql usage · MI005 unused
variable · MI006 unused parameter · MI007 WAITFOR DELAY/TIME · MI008
QUOTED_IDENTIFIER inside a module.

**Style/naming/script, `ST`/`SC`/`NC`/`CGUNP` (34).** ST001 old-style comma
join · ST002 old-style `=` alias · ST003 proc body without BEGIN/END · ST004
SQL-92 cursor declaration · ST005 IF/ELSE without BEGIN/END · ST006 old-style
TOP · ST007 cursor name reused · ST008 non-named parameter style · ST009 GOTO ·
ST010 alias all table sources · **ST011 consider table variable instead of temp
table** · **ST012 consider temp table instead of table variable** · ST013 `!=`
· ST014-ST015 proc-name patterns · ST016 `fn_` prefix · ST017 digits in table
names · SC001-SC006 script hygiene (trailing GO, trailing newline, USE in
batch, TODO comments, self-granting procedure, CRLF) · NC001A/NC001D
transaction-name allow/deny lists · **CGUNP unparsed SQL** (the incumbent's
parse-health signal, sourced from the external tool's report).

### 7.4 Rust multi-dialect linter — all 282

Every rule is a regex over raw query text (`query.raw` appears 343 times in
`src/rules`; no rule touches the AST). Multi-dialect, so a large fraction is
irrelevant to T-SQL. At least 11 rules are unconditional stubs that never fire,
including its implicit-conversion rule.

**Performance (73).** PERF-SCAN-001 SELECT * · -002 unbounded DML · -003
unbounded SELECT · -004 NOT IN subquery · -005 expensive DISTINCT ·
PERF-IDX-001 function on indexed column · -002 leading wildcard · **-003
implicit type conversion on indexed column (STUB — never fires)** · -004 OR in
WHERE · -005 deep offset pagination · **-006 composite index column-order
violation** · -007 non-SARGable OR · **-008 COALESCE/ISNULL/NVL on indexed
column** · -009 negation on indexed column · PERF-JOIN-001 cross join · -002
excessive joins · -003 LEFT JOIN with IS NOT NULL · PERF-AGG-001 unfiltered
aggregation · -002 ORDER BY in subquery · -003 HAVING without GROUP BY ·
PERF-LOCK-001 table lock hint · -002 NOLOCK · -003 long transaction · -004
missing isolation level · PERF-CURSOR-001 cursor · -002 WHILE loop · -003
nested-loop hint · PERF-MEM-001 large IN list · -002 unbounded temp table ·
-003 ORDER BY without LIMIT in subquery · -004 GROUP BY on high-cardinality
expression · PERF-HINT-001 optimizer hint · -002 index hint · -003 parallel
hint · **PERF-SCALAR-001 scalar UDF in SELECT/WHERE** · -002 correlated
subquery · PERF-SORT-001 ORDER BY on non-indexed column · PERF-BATCH-001/002
unbatched operations · PERF-NET-001 excessive column count · -002 LOB column in
unfiltered query · PERF-TSQL-001 missing SET NOCOUNT ON · **-002 SELECT INTO
temp table without index** · **-003 implicit conversion in JOIN predicate
(regex for CAST/CONVERT near JOIN)** · -004 WAITFOR DELAY · SCHEMA-IDX-001
missing index on WHERE column (stub). Remainder are PostgreSQL/MySQL/Oracle/
Snowflake/BigQuery/Redshift/ClickHouse/DuckDB/Presto/Spark/SQLite specific.

**Security (61)** — the largest non-performance block, spanning injection (18),
data exposure (13), authentication (10), access control (6), cryptography (4),
denial of service (3), authorization (3), session (2), logging (2). T-SQL
relevant: SEC-INJ-001 concatenation-based injection · SEC-INJ-002 dynamic SQL
execution · SEC-INJ-003 tautological OR · SEC-INJ-004 time-based blind
injection (WAITFOR DELAY) · SEC-INJ-005 second-order injection · SEC-INJ-006
LIKE wildcard injection (framed as a DoS/scan vector) · SEC-CMD-001
xp_cmdshell · SEC-CFG-001 sp_configure enabling xp_cmdshell / OLE Automation /
CLR / Ad Hoc Distributed Queries · SEC-DATA-001 BULK INSERT and file-operation
exfiltration · SEC-DATA-002 OPENROWSET/OPENDATASOURCE/OPENQUERY · SEC-TSQL-001
OPENROWSET/OPENDATASOURCE · SEC-TSQL-002 sp_OACreate · SEC-PRIV-001 EXECUTE AS
elevation · SEC-AUTH-001..005 hardcoded passwords, GRANT ALL, GRANT to PUBLIC,
user without password, CHECK_POLICY/CHECK_EXPIRATION OFF · SEC-CRYPTO-001..004
weak hashing/plaintext passwords/hardcoded keys/weak ciphers · SEC-AUTHZ-001..003
role-grant escalation, ownership transfer, missing tenant filter · SEC-LOG-001
sensitive data in RAISERROR/THROW/PRINT · SEC-LOG-002 audit-trail tampering ·
SEC-SESSION-001/002 session token storage and expiry · **SEC-DOS-001 recursive
CTE without MAXRECURSION** · SEC-DOS-002 ReDoS · SEC-INFO-001..004 version and
schema disclosure, timing attacks, verbose errors · SEC-PATH-001/002 path
traversal and local file inclusion · SEC-SSRF-001 database-issued HTTP requests
· SEC-CONFIG-001..004 hardcoded credentials, weak TLS, default credentials,
permissive access. Remaining SEC-PG/ORA/MYSQL/SQLITE/RS/CH/SF rules are other
dialects. **SEC-INJ-007..011 (LDAP, NoSQL, XPath, template, JSON injection) are
regex rules for languages that are not SQL** — a good illustration of the
catalog's breadth-over-precision posture.

**Reliability (44).** REL-DATA-001 catastrophic data-loss risk · -002 TRUNCATE
without transaction · -003 ALTER TABLE without backup signal · -004 destructive
DROP · REL-TXN-001 missing rollback handler · -002 autocommit disabled · -003
empty transaction block · REL-ERR-001 swallowed exception · REL-REC-001 missing
savepoint · REL-IDEM-001/002 non-idempotent INSERT/UPDATE · **REL-RACE-001
read-modify-write without lock** · **-002 TOCTOU** · REL-FK-001 orphan record
risk · **-002 cascade delete risk** · **REL-DEAD-001 deadlock pattern** · -002
lock escalation risk · REL-TIMEOUT-001 long-running query · REL-STALE-001 stale
read · REL-RETRY-001 missing retry · **REL-TSQL-001 @@IDENTITY vs
SCOPE_IDENTITY** · **REL-TSQL-002 MERGE without HOLDLOCK** · **REL-TSQL-003
TRUNCATE in TRY without CATCH** · SCHEMA-TBL-001/COL-001 non-existent table/
column (the only schema-aware checks that actually run). Remainder
dialect-specific.

**Cost (33).** COST-COMPUTE-001 full scan on large table · -002 unpartitioned
window functions · COST-STORAGE-001 SELECT * in ETL/CTAS · COST-IO-001
redundant ORDER BY in subquery · COST-PAGE-001..003 OFFSET pagination without
index, deep pagination, COUNT(*) for totals · **COST-IDX-001 duplicate index** ·
-002 over-indexed table · **-003 missing covering index / key lookup** · **-004
redundant index column order** · COST-CROSS-001..003 cross-database join,
multi-region latency, distributed transaction overhead · COST-TSQL-001 cursor
without FAST_FORWARD · COST-PARTITION-001 large table without partitioning
(stub) · COST-ARCHIVE-001, COST-COMPRESS-001, COST-NETWORK-001,
COST-SERVERLESS-001/002 · remainder cloud-warehouse specific.

**Quality (51).** QUAL-NULL-001 incorrect NULL comparison · QUAL-MODERN-001
implicit join syntax · -002 hardcoded date literal in filter · -003 UNION
without ALL · -004 CASE without ELSE · QUAL-STYLE-001..005 · QUAL-DRY-001
duplicate WHERE condition · QUAL-COMPLEX-001..005 nesting, god query,
cyclomatic complexity, length · QUAL-NAME-001..004 · QUAL-DOC-001..003 ·
**QUAL-SCHEMA-001 missing primary key** · **-002 implicit foreign key** ·
**-003 missing index on foreign key** · **-004 float for currency** ·
QUAL-TEST-001 non-deterministic query · **-002 pagination without ORDER BY** ·
-003 hardcoded test data · QUAL-DEBT-001/002 · **QUAL-DEAD-001 unused database
object (stub)** · -002 unreachable code · -003 duplicate query (stub) ·
QUAL-TSQL-001 SET ANSI_NULLS OFF · QUAL-TSQL-002 SET QUOTED_IDENTIFIER OFF ·
QUAL-DBT-001/002 (stubs) · remainder dialect-specific.

**Compliance (18).** COMP-GDPR-001..006, COMP-HIPAA-001..003, COMP-PCI-001..003,
COMP-SOX-001/002, COMP-CCPA-001, COMP-SEC-001, COMP-RET-001, COMP-AUD-001 —
PII/PHI/PAN detection, consent, retention, segregation of duties, audit trails.
Entirely regex-over-column-names; listed for completeness only.

**Schema/migration (2).** SCH-BRK-001 cross-file breaking change · MIG-BRK-001
breaking schema change.

### 7.5 SonarQube plugin's own rules — 22 (16 enabled for T-SQL)

C001 WAITFOR/SLEEP · C002 SELECT * · C003 INSERT without column list · C004
ORDER BY ordinal · C005 EXEC dynamic query · C006 non-schema-qualified name /
cursor lifecycle · C007 NOLOCK / multiple cursor declarations · C008 cursor
closed in different control statement · **C009 non-sargable statement (BETA —
function-wrapped column, plus leading-wildcard LIKE)** · C010/C011/C013 PK/FK/
index naming conventions · C011 variable declared not set (disabled for T-SQL) ·
C012 `= NULL` comparison · C014 OR in WHERE · C015 UNION used · C016 IN/NOT IN
with subquery · C017 ORDER BY without ASC/DESC · C018 file too large · C020
hint used · C021 missing COMMIT · C022 non-materialized view · C023 cartesian
join · C024 implicit column reference (disabled for T-SQL) · C030 missing file
header comment.

### 7.6 Out-of-scope catalogs (recorded so they are not re-surveyed)

* **Oracle PL/SQL analyzer — 57 checks.** Confirmed no T-SQL support of any
  kind. Has a real symbol table, but for PL/SQL block/variable scope only —
  never a database catalog. Checks cover correctness (comparison with NULL/
  boolean, concatenation with NULL, identical expressions, duplicate
  conditions, dead code, unhandled exceptions, TOO_MANY_ROWS, TO_DATE without
  format), unused declarations, style, and two test-related checks. Nothing
  conversion-, sargability-, or index-related beyond `ToCharInOrderBy`,
  `UnnecessaryLike`, and `SelectAllColumns`.
* **DacFx sample — 9 rules.** SRD0001 table without PK · SRP0001 WAITFOR DELAY ·
  SRN0001-0007 naming conventions. Abandoned 2017. Its README additionally
  documents *Microsoft's* built-in SR0001-SR0016 set, of which the
  conversion-adjacent one is **SR0014 "Maintain compatibility between data
  types"** — closed-source, behaviour unverified, and the most likely source of
  a "SSDT already does this" objection. Worth an empirical check against a
  dacpac build before publication.
* **WinForms regex scorer — 9 regexes.** SELECT *, `= NULL`, `!=`/`<>`, leading
  wildcard, NOT IN, COUNT(col), TOP without ORDER BY, IN predicate, OR
  operator. Toy repo; recorded only to close it out.
