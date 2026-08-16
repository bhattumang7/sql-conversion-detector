# SilentScan detection checklist

The complete, gated candidate list of SQL Server query-level performance
problems this tool could detect. This is the working backlog — work items one
by one, check them off, and prune sections when they stop being useful.

Competitor tools are referenced generically throughout ("a commercial
schema-bound analyzer," "one small T-SQL type-checker") rather than by name —
deliberate, so nothing about a specific vendor lands in a public commit. The
real identities are recorded locally in `vendor/tool-references.md`
(gitignored, never committed) if a name is ever needed again.

Context that shapes the whole list: no living static tool does type-aware,
direction-aware, lineage-aware analysis; the tools that ever bound types flag
mismatches symmetrically and are dead or niche, while the purely syntactic
patterns are each covered by several existing linters. **Rule of admission: a
new detection should require the engine-authoritative catalog, the lineage
pass, or the plan-XML oracle to be possible at all. Precision beats recall;
every rule ships with a near-miss guard and, where verdict-bearing, an oracle
test.**

Base rates below are module counts from an aggregate pattern inventory of the
local production copy (crude LIKE-heuristic counts over ~5,000 modules —
signal, not findings; no schema details recorded here).

---

## Already shipped (context, not work)

- [x] Column-side implicit conversion in predicates — precedence direction,
      collation family (`ScanForced` vs `RangeSeek`), literal typing,
      view/iTVF lineage depth + origin, dynamic-SQL constant tracing,
      oracle confirmation via `CONVERT_IMPLICIT`-on-column.
- [x] Tier-1 syntactic non-sargability stream (`SargabilityFindingKind`):
      function-wrapped column, CAST/CONVERT on column, column arithmetic,
      leading-wildcard LIKE, non-literal LIKE pattern.
- [x] MSTVF-as-fence stream (`TvfFenceFindingKind`): direct FROM/JOIN,
      correlated `CROSS/OUTER APPLY` (ranked first — no engine version
      mitigation rescues it), fence inherited through a view/iTVF layer
      (`Lineage.TvfFenceMap`, including the inline-TVF-function-call-syntax
      case), standalone reference, `INSERT ... EXEC`. Dynamic-SQL folding and
      verify-corpus oracle confirmation both wired in
      (`TvfFenceProbeBuilder`/`TvfFenceVerifier`; marker:
      `PhysicalOp="Table-valued function"` / `StatementType="INSERT EXEC"`,
      oracle-verified directly). iTVFs never fire — oracle-verified against
      `sys.objects.type='IF'` on the local test DB.
- [x] Scalar UDF stream (`ScalarUdfFindingKind`): predicate-context invocation
      (WHERE/JOIN ON/HAVING/MERGE ON, ranked first), reached through
      view/iTVF expansion (`Lineage.ScalarUdfMap`, same depth/origin shape as
      the TVF-fence map), schema-level dependency (computed column/DEFAULT/
      CHECK constraint, catalog-only via `SchemaDependencyScanner` — no
      query-site AST needed), projection-context invocation (SELECT list/
      ORDER BY/GROUP BY/SET/variable assignment, ranked last). Every call
      carries the finding's own inlineability read (SQL 2019+ FROID: engine
      `is_inlineable` preferred, `ScalarUdfInlineabilityScanner`'s static
      blocker scan only ever explains, never asserts Inlineable on its own),
      non-schemabound constant-argument-folding defeat, and CLR data-access
      status. Dynamic-SQL folding and verify-corpus oracle confirmation both
      wired in (`ScalarUdfProbeBuilder`/`ScalarUdfVerifier`; two-probe design
      — pinned probe with `OPTION (USE HINT('DISABLE_TSQL_SCALAR_UDF_INLINING'))`
      confirms the function reference via a `<UserDefinedFunction
      FunctionName="...">` plan element, natural probe cross-checks the
      finding's own Inlineability read via `ContainsInlineScalarTsqlUdfs="1"`
      on the `StmtSimple`). Oracle-discovered and load-bearing: the hint does
      NOT propagate into a scalar UDF called from inside a view's own
      definition (a call that inlines away under the hint at the top level
      still inlines away identically with it, even though the same view
      referenced elsewhere shows the `UserDefinedFunction` element for a
      genuinely non-inlineable function) — so every probe targets the finding's
      underlying function directly, never through the referencing view,
      mirroring `TvfFenceProbeBuilder`'s identical choice. SchemaDependency
      findings are never probed — the constraint/computed-column definition
      text they cite is already engine truth. All findings share the same
      "never guess" catalog gate: a 2-part call that doesn't resolve in the
      scalar-UDF registry (built-in or truly unknown) never fires.
- [x] Write-loss stream (`silentscan/write-loss/*`, `WriteLossKind`): silent
      DML data loss with no engine error raised — unicode-to-non-unicode
      replacement (NVARCHAR/NCHAR source into VARCHAR/CHAR: out-of-codepage
      characters become `?`), approximate-to-exact truncation (REAL/FLOAT into
      an exact integer type: fractional part dropped), numeric scale narrowing
      (DECIMAL/NUMERIC into a smaller-scale target: digits rounded away),
      temporal precision loss (DATETIME-family into DATE: time-of-day
      dropped). Every member oracle-verified by inserting a real probe row and
      reading it back. Deliberately excludes cases the engine already hard-
      errors on (too-long VARCHAR, integer overflow) — flagging something
      T-SQL already stops for you would be a false "silent" claim.
- [x] Collation conflict (`silentscan/verdict/collation-conflict`): two real
      string columns compared directly whose resolved collations are
      genuinely incompatible — Msg 468, "Cannot resolve the collation
      conflict", a compile error, not a seek/scan question.

---

## Corrections to shipped work (clear before starting a new stream)

One confirmed false positive, one decision to re-examine, two omissions. No
new detections here. The false positive outranks every Tier 1 item: a shipped
rule that fires on a predicate the engine seeks is exactly the failure the
precision bar exists to prevent, and it is in the stream the Tier 1
"Type-aware upgrade of the sargability stream" section builds on.

- [x] **`JSON_VALUE(col, '$.path')` false-positives the shipped
      function-wrapped-column rule** — fixed: `JsonComputedColumnMatcher`
      (`src/SilentScan.Core/Predicates/JsonComputedColumnMatcher.cs`, wired into
      `Predicates/NonSargablePredicateScanner.cs`) reparses
      each computed column's definition text (the same throwaway-wrapper-
      statement trick `SchemaDependencyScanner` already uses) and suppresses
      `FunctionWrappedColumn` only on an exact AST match — same function, same
      source column, same literal path string, ordinal comparison — against an
      indexed computed column. Oracle-verified against the Docker instance
      (SQL Server 2022, compat 160): a matching indexed computed column
      produces a genuine `Index Seek`; a different path on an indexed computed
      column still scans (`FUNCTION_WRAPPED_COLUMN_json_value_different_path_fires.sql`
      guards exactly this). Fixtures:
      `FUNCTION_WRAPPED_COLUMN_json_value_{fires,clean}.sql` +the different-path
      guard; oracle coverage in `JsonComputedColumnSuppressionTests` (file-mode
      catalog + the live engine-authoritative pipeline + a real
      `PhysicalOp="Index Seek"` plan-XML probe with a literal comparison value,
      not a MAX-typed variable — a MAX-typed comparison value was oracle-found
      to defeat the seek even with the matching index present, tracked
      separately under Tier 1 "Oversized and MAX-typed parameters"). Coverage-empty
      against the local RM_ test database today (0 computed columns reference
      JSON_VALUE/JSON_QUERY there) — a real, fixable false positive with no
      local corpus signal, not evidence it's rare in the wild.
- [x] **Re-examine whole-plan `GetRangeThroughConvert` attribution in the
      plan-cache path.** `src/SilentScan.Live/Catalog/LivePlanCacheReader.cs:212`
      sets `hasRangeSeek` from `planXml.Contains("GetRangeThroughConvert")`
      and applies it to every conversion the detector finds in that plan.
      **This is deliberate, not an oversight** — the code comment states the
      marker is read plan-wide to match how `TypeMatrixGenerator`'s oracle
      probe reads the same signal, and `WorkloadVerdict`'s own doc comment
      discloses it. The reason to revisit: in the matrix generator each probe
      is a single authored predicate, so plan-wide and per-node are the same
      thing there, whereas a cached plan from real application SQL can carry
      several conversions at once — and the marker actually sits inside one
      specific seek predicate's `ScalarString`. Where those differ, a plan
      with one range-seek conversion marks its genuinely `ScanForced`
      siblings `RangeSeek` too. Decide with evidence rather than by argument:
      find (or author, in the standing Docker instance) a plan carrying a
      range-seek conversion and a scan-forcing conversion together, and see
      whether the misattribution is real. If it is, resolve the marker
      against the conversion node `ConvertImplicitDetector` already located.
      If multi-conversion plans can't actually arise here, record that and
      close the item — the current behavior is then correct as written.
      **Resolved: the misattribution was real.** Oracle-authored a single
      cached plan (a UNION ALL of a Windows-collation range-seek predicate and
      a SQL_\*-collation scan-forced predicate on the same table) and confirmed
      `GetRangeThroughConvert` appears once, scoped to the range-seeking
      branch's own `RelOp`, while the scan-forced branch's `RelOp` has no
      `SeekPredicates` entry for its column at all — the old plan-wide
      `.Contains()` read would have marked both `RangeSeek`. Fixed:
      `ConvertImplicitFinding` now carries a per-node `RangeSeekBound` field
      (`src/SilentScan.Verify/Oracle/ConvertImplicitFinding.cs`),
      `ConvertImplicitDetector.FindColumnConversions` computes it by walking
      each `Convert` node up to its nearest ancestor `RelOp` and checking
      *that* operator's own `SeekPredicates`/`SeekPredicateNew`
      `RangeColumns` for the same column
      (`src/SilentScan.Verify/Oracle/ConvertImplicitDetector.cs`), and
      `LivePlanCacheReader.AccumulateConversions` now ORs in
      `conversion.RangeSeekBound` per column instead of a plan-wide boolean
      (`src/SilentScan.Live/Catalog/LivePlanCacheReader.cs:212`).
      `TypeMatrixGenerator`/`CorpusFindingVerifier`'s own plan-wide
      `.Contains()` reads are unchanged — both are genuinely single-predicate
      probes where plan-wide and per-node are equivalent, exactly as this item
      anticipated. Regression coverage: a unit test feeding a synthetic
      two-branch plan XML directly to `ConvertImplicitDetector`
      (`ConvertImplicitDetectorTests.FindColumnConversions_TwoConversionsInSamePlanOnlyOneRangeBound_AttributesPerNodeNotPlanWide`)
      and a live Docker-oracle test running the actual UNION ALL query through
      `LiveScanRunner`
      (`LivePlanCacheReaderPerConversionAttributionOracleTests.RunAsync_OnePlanWithBothRangeSeekAndScanForcedConversions_AttributesEachColumnIndependently`
      in `tests/SilentScan.Tests/Integration/LivePlanCacheWorkloadFindingTests.cs`),
      both confirmed to fail against the pre-fix code. Not reachable against
      the local RM_ test database today (its plan cache never accumulates real
      application workload, since CLAUDE.md forbids ever executing scanned-
      target DML/procs) — the bug matters for real `scan-db` targets under
      live application traffic, where multi-predicate cached plans are the
      norm, not for this project's own local corpus target.
- [x] **Two shipped streams are missing from "Already shipped" above** — fixed,
      both now recorded there: the
      write-loss stream (`silentscan/write-loss/*`: unicode-to-non-unicode,
      approximate-to-exact truncation, numeric scale narrowing, temporal
      precision loss) and collation conflict
      (`silentscan/verdict/collation-conflict`, Msg 468, two real string
      columns with differing resolved collations and no explicit `COLLATE`).
      Both exist in code with rule IDs in `SarifRuleCatalog`. A backlog that
      under-reports what shipped is the same rot the narrative plan file died
      of; record them with the same detail as the other four entries.
- [x] **Fix the checklist's own numbering before it misleads anyone.**
      Resolved both open questions: (a) dropped the `### N.` numeric prefixes
      entirely in favor of descriptive headers used as slug-style anchors —
      numbers were insertion-order accidents that a future insert/delete would
      immediately stale again, while a descriptive header ("Join-key and
      cross-object type/collation mismatch") survives reordering the way a
      number can't, and every shipped rule already has its own real stable ID
      (`SargabilityFindingKind` etc.) for when precision actually matters; (b)
      folded "Candidates from the incumbent rule catalogs" and "Candidates
      from the wider product landscape" into Tier 1/2/3 by the same criteria
      (precision, catalog requirement, base rate) used everywhere else in this
      file — see each item's new home below. Two items didn't fold cleanly
      into a detection tier and got their own small sections instead: the two
      "measure a competitor before publication" items (not detections — put
      under a new "Research gates before publication" section), and the
      security/compliance rule-axis question, which is a scope decision this
      reorg pass deliberately did NOT make on its own (see "Open scope
      questions" below) since it would change what kind of tool this is, not
      just where a candidate rule lives.
- [x] **Record the runtime incumbent in `detection-reference.md`** — done, as
      Appendix 7 §7.7: a multi-platform commercial response-time analyzer
      whose entire SQL Server plan-advice surface is two XPath queries
      (missing index, unmatched index), and whose implicit-conversion
      detection is a plain `CONVERT_IMPLICIT` substring test with no notion
      of which side converted — a real shipping instance of the "greps for
      the marker without checking which side" failure mode this reference
      otherwise only posed hypothetically. Opens no detection gap on our
      side; its remaining analyses are runtime aggregates or DBMS-specific,
      already covered by the Tier 3 skips.

---

## Tier 1 — build next (high precision, needs our machinery, high base rate)

### Join-key and cross-object type/collation mismatch
Direct reuse of the precedence matrix on new predicate sites.

- [x] Type mismatch across JOIN `ON` columns — **already shipped**, discovered
      re-planning this item: `TypedPredicateExtractor.ExplicitVisit(QualifiedJoin)`
      already sets `Seekable` position over `node.SearchCondition` exactly like
      WHERE/HAVING, so every JOIN `ON` predicate already flows through the
      identical classification path. Oracle-tested:
      `TypedPredicateExtractorTests.JoinOnClausePredicate_IsResolved_OracleConfirmed`.
- [x] Proc/function **parameter type vs compared column type, inside the
      callee's own body** — **already shipped**: `RecordParameters` seeds a
      proc's declared parameters into the same variable-type dictionary a
      `DECLARE`d variable uses, so `WHERE Col = @Param` inside a proc body is
      indistinguishable from any other typed comparison to the existing
      pipeline. Oracle-tested:
      `TypedPredicateExtractorTests.VarcharColumnVsNVarcharParam_SqlCollation_ScanForced_OracleConfirmed`.
- [x] **Column collation ≠ database collation** — **shipped**:
      `Predicates/ColumnCollationDriftScanner.cs`, catalog-only, no AST
      walking. Wired into `ScanReport.ColumnCollationDriftFindings`
      (schema v7), SARIF rule `silentscan/catalog/column-collation-drift`,
      readable-report section. Also covers the **temp-table/table-variable
      vs. tempdb collation** half from the incumbent-catalog read in the same
      scanner (`DatabaseCatalog.EffectiveTempdbCollation`, `IsTempObject` on
      the finding) — the two were always the same catalog diff with a
      different baseline. Tested: 8 unit tests
      (`ColumnCollationDriftScannerTests`) covering fires/clean/unresolved-
      baseline/temp-object-baseline-fallback. Measured live against the
      local RM_ test database: 0 columns diverge there (its collation is
      uniform) — a real, honest zero; real-world drift (multi-tenant/
      migrated databases) is exactly what this catches, and its fixtures are
      directly authored (not internet-sourced) since a catalog-diff rule has
      no "bug repro" to cite the way a predicate rule does.
- [x] Cross-table same-name type drift report, **FK-linked half shipped**:
      `Catalog/ForeignKeyRelationship.cs` + `DatabaseCatalog.ForeignKeys`,
      populated live-only via `LiveCatalogReader` reading
      `sys.foreign_key_columns` (always empty in file mode, per CLAUDE.md's
      "everything goes via the database" rule — replicating FK DDL semantics
      would be reinventing the database-project wheel).
      `Predicates/CrossTableTypeDriftScanner.cs` flags a genuine category or
      collation difference (length/precision-only drift within the same
      category never fires — no conversion-seed story). Wired into
      `ScanReport.CrossTableTypeDriftFindings` (schema v8), SARIF rule
      `silentscan/catalog/cross-table-fk-type-drift`, readable-report
      section. **Oracle discovery, load-bearing for how this rule is
      described everywhere it's cited:** SQL Server structurally forbids a
      drifted FK from existing at all — `ALTER TABLE ADD CONSTRAINT` rejects
      a category *or collation* mismatch outright (Msg 1757, verified even
      `WITH NOCHECK`), and `ALTER COLUMN` on either side is blocked while the
      constraint exists (Msg 5074/4922). This means a currently-valid FK's
      two sides can **never** actually drift in a real, intact database —
      explaining why the local RM_ test database's real 1,247 FK pairs show
      0 drift (not empirical luck, a structural guarantee) — so the FK-linked
      half's real-world yield is essentially always zero on a healthy schema;
      its value is as a defensive/completeness check (a drifted FK could
      still exist behind an orphaned/disabled constraint this pass doesn't
      currently distinguish) rather than a real finding source. The scanner's
      own logic is proven correct directly against a hand-built catalog
      (`CrossTableTypeDriftScannerTests`, 4 tests — the only way to construct
      the state at all) plus a live oracle test locking in the negative
      (`LiveCatalogReaderTests.ReadAsync_MatchingForeignKey_CrossTableTypeDriftScannerNeverFires`).
      **Remaining, explicitly not shipped:** the join-candidate (no-FK,
      same-name-column) half — scoped to pairs with an actually-observed
      column-vs-column comparison somewhere in the scanned corpus, not a
      blanket same-name sweep across every table pair (an unqualified sweep
      has real false-positive risk: two unrelated tables that happen to both
      have a `Notes` column, never joined on, is noise). This is where the
      real-world value of this whole item now concentrates, given the FK-half
      finding above — needs genuinely new AST machinery (collecting every
      column-vs-column comparison in the corpus regardless of whether
      `VerdictClassifier` would itself flag it, unlike the existing typed-
      predicate pipeline which only records a finding when types actually
      differ). Of 1,053 same-named columns across ≥2 tables in the local RM_
      database, 83 have real sargability-relevant type/collation drift (29
      collation-specific) — the number to cite once the observed-comparison
      narrowing ships, not the old crude-heuristic "148" (the same live
      measurement reproduces "148" exactly when length/precision-only
      differences are also counted, confirming the old number was accurate
      but over-inclusive for what actually matters).
- [x] `sql_variant` in comparisons — **shipped**: one new branch pair in
      `VerdictClassifier.ClassifyWithReason`, before the existing
      `IsOutOfModelCategory` early-out — sql_variant is T-SQL's highest-
      precedence type, so unlike every other out-of-model category it
      participates cleanly in the standard "lower-precedence side converts"
      rule. Oracle-verified **both directions**: an indexed `int` column vs.
      a `sql_variant` value produces `Convert DataType="sql_variant"
      Implicit="1"` directly on the column's `ColumnReference`, an Index
      Scan with no `RangeColumns`/`GetRangeThroughConvert` anywhere
      (`ScanForced`); an indexed `sql_variant` column vs. an `int` value
      converts the *value* instead, with a genuine Index Seek and a real
      `SeekPredicates`/`RangeColumns` entry on the column (`SeekPreserved`).
      Two `sql_variant` operands, or `sql_variant` paired with another
      out-of-model category, correctly stay `Unknown` (execution-time boxed-
      type semantics, not statically resolvable). Tested: 4 unit tests
      (`VerdictClassifierTests`) + 2 live oracle tests
      (`TypedPredicateExtractorOracleTests`). 4 columns locally (confirmed
      accurate via live measurement, matching the existing figure).
- [x] **Call-boundary argument mismatch** — **shipped**: the value flowing
      INTO a parameter at a real `EXEC` call site has a different declared
      type than the parameter's own declaration (e.g. a `varchar` local
      variable passed into an `nvarchar` param). Doesn't itself lose a seek —
      EXEC parameter marshalling isn't a predicate — so it's classified with
      `Rules.WriteLossClassifier` (same family as `WriteLossFinding`) rather
      than `VerdictClassifier`'s seek/scan vocabulary. `ProcCallGraphBuilder`
      now tracks a per-scope variable-type dictionary (a variable's DECLARED
      type never changes after its own DECLARE, unlike its value, so this
      needs none of `ResolvePropagatedLiteral`'s reaching-definitions
      machinery — seeded once per scope from the scope's own formal
      parameters plus every DECLARE at any nesting depth) and stamps
      `ProcCallArgument.CallerArgumentType`. New
      `Predicates/ProcCallArgumentMismatchScanner.cs` walks the built graph
      and classifies each variable-reference argument. Ships as a standalone
      catalog+call-graph finding (`ProcCallArgumentMismatchFinding`) rather
      than chaining into a specific predicate finding inside the callee — the
      cross-referencing needed to chain provenance is real additional
      machinery, deliberately out of scope for this pass; revisit as a
      follow-up. No plan-XML oracle marker applies to a parameter binding the
      way `CONVERT_IMPLICIT`-on-column does to a predicate, so this rule has
      no plan-XML oracle fixture — the underlying silent-data-loss runtime
      behavior each `WriteLossKind` claims is already oracle-proven
      separately (`WriteLossOracleTests`, self-authored probe rows), so this
      stream's own tests only needed to prove the call-boundary WIRING: 12
      unit tests (`ProcCallGraphBuilderTests` for caller-type resolution
      including scope-isolation and enclosing-parameter cases,
      `ProcCallArgumentMismatchScannerTests` for the classifier) plus a live
      end-to-end oracle test through the real engine-authoritative pipeline
      (`ProcCallArgumentMismatchPipelineTests`) confirming a real deployed
      caller/callee pair surfaces the finding. Wired into
      `ScanReport.ProcCallArgumentMismatchFindings` (schema v9), SARIF rule
      `silentscan/call-graph/argument-type-mismatch`, readable-report
      section. Real-world fixtures for this exact phenomenon (as opposed to
      the in-body case, which is well-documented) are rare, so its own
      fixtures are directly authored rather than internet-sourced, per
      CLAUDE.md's rare-exception allowance.
- Oracle: identical `CONVERT_IMPLICIT`-on-column probe as the existing
  stream, except the call-boundary and catalog-only items above, which have
  no plan-XML oracle by construction (see each item).

**This entire section is now closed** — all five sub-rules shipped (two
turned out to already exist; three genuinely new), leaving only the explicit
follow-up under cross-table type drift (the join-candidate/observed-
comparison half).

### Type-aware upgrade of the sargability stream
Real AST/catalog-derived counts (live `scan-db` against the local RM_ test
database, replacing the old crude LIKE-heuristic numbers below where they
disagree): 6,942 Tier-1 findings total. **Date-form functions (2,044
findings, `DATEDIFF` alone 1,633) are the largest bucket, not ISNULL/COALESCE
(1,713)** — the "highest base rate" framing this section opened with was
wrong once measured with the real parser; corrected below. `CHARINDEX`: 12.
`LEFT(col,n)` genuinely wrapping a column: 0. `UPPER`: 11 (`LOWER`: 0).

- [x] `ISNULL(col, x) = y` — **shipped, the false-positive half**:
      oracle-verified `ISNULL(col, x)` on a catalog-provable NOT NULL column
      is a false positive the blanket rule didn't catch — the optimizer
      proves `ISNULL(NOT-NULL-col, x) = col` and simplifies the wrap away
      entirely, **regardless of the default argument's own type** (even a
      widening int-vs-bigint default still seeks — nullability alone decides
      it, never a type question). Suppressed in
      `Predicates/NonSargablePredicateScanner.cs` (`IsKnownNotNullColumn`).
      Fixtures `FUNCTION_WRAPPED_COLUMN_isnull_not_null_clean.sql` +
      oracle coverage (`IsNullNotNullSuppressionTests`, 5 tests including the
      widening-default oracle probe and a nullable-column near-miss guard the
      other direction).
- [x] `COALESCE(col, x) = y` — **decided, no change needed**: oracle-verified
      `COALESCE` gets **no** equivalent NOT-NULL simplification —
      `COALESCE(NOT-NULL-col, x) = y` still scans even with no type
      conversion present at all (`COALESCE` is `CASE` syntax sugar; the
      optimizer never folds it the way it folds `ISNULL`). This closes the
      original "does COALESCE's result-type inference flip a conversion"
      question — the real blocker was never type inference, so no new
      classifier is needed; `COALESCE` already fires correctly today as
      `FunctionWrappedColumn` and now benefits from index-existence weighting
      (below). The "ISNULL/COALESCE arguments of differing datatypes"
      incumbent-catalog fold-in is superseded by this finding — computing the
      result type would not have changed COALESCE's verdict here regardless.
- [x] Date-form non-sargables as a named rule — **shipped**:
      `SargabilityFindingKind.DateFunctionOnColumn` covers
      `YEAR`/`MONTH`/`DAY`/`DATEPART`/`DATEDIFF`/`DATEADD`/`DATENAME`.
      Oracle-verified structurally identical to case-folding (below): the
      wrap forces a scan unconditionally, so this is syntactic-with-guard,
      not a new verdict system — the "does the type change" framing this
      item started with turned out not to apply. **Oracle discovery, load-
      bearing for the precision guard:** SQL Server rewrites `YEAR(x)`/
      `MONTH(x)`/`DAY(x)` to `DATEPART(year/month/day, x)` the moment a
      computed column definition is stored (`sys.computed_columns.definition`)
      — a real false-negative trap the guard's structural comparer now
      canonicalizes around (`ComputedColumnMatcher.TryAsCanonicalDatePart`),
      found and fixed via a failing live-pipeline oracle test before it could
      ship broken. Fixtures `DATE_YEAR_ON_COLUMN_{fires,clean}.sql` (real
      source: Kendra Little's computed-column-index article) and
      `DATE_DATEDIFF_ON_COLUMN_fires.sql` (labeled synthetic, no distinct
      real bug report found for this exact shape, per CLAUDE.md's rare-
      exception allowance) + `DateFunctionColumnPipelineTests` (4 oracle
      tests, both directions). `CONVERT(varchar, col, n)`-style date
      comparisons stay under the existing `CastOrConvertOnColumn` kind
      (not a new named kind) but now share the same computed-column guard —
      see below.
- [x] **BETWEEN with an end-of-period boundary — shipped, re-scoped as a
      CORRECTNESS finding, not a sargability one**: BETWEEN itself is
      perfectly sargable here — the real bug (Aaron Bertrand's widely-cited
      "Bad Habits: Using BETWEEN") is that an upper-bound literal with fewer
      fractional-second digits than the column's own declared
      TIME/DATETIME2/DATETIMEOFFSET precision silently EXCLUDES rows in the
      precision gap, oracle-confirmed directly: a `DATETIME2(7)` row at
      `23:59:59.9999999` is dropped by the classic `'...23:59:59.997'`
      end-of-day literal (the very hack people write believing they've fixed
      the cruder bare-date/`.999` version of the same bug) while a
      `>= start AND < next-period-start` rewrite includes it. New
      `TemporalBoundaryPrecisionFinding`/`TemporalBoundaryPrecisionScanner`
      wired into `NonSargablePredicateScanner`'s existing scope-resolution
      walk via a new `ScanFull` API (`Scan` is now a thin wrapper over it, no
      behavior change) rather than a second, redundant AST pass — reused the
      already-correct FROM-scope/column-resolution machinery instead of
      rebuilding it. **Oracle discovery, load-bearing:** `SqlTypeReferenceResolver`
      (file mode) and `LiveTypeMapper` (live mode) both silently dropped
      TIME/DATETIME2/DATETIMEOFFSET's own declared scale entirely before this
      — every column of these types resolved with `Scale: null` — a real,
      independently-worth-fixing gap in both parsing paths, now captured
      (defaulting to 7 when no explicit `(n)` is given, T-SQL's own default).
      Wired into `ScanReport.TemporalBoundaryFindings` (schema v10), SARIF
      rule `silentscan/correctness/between-end-of-period-boundary` (error
      level — a live correctness bug, not a "worth investigating" signal),
      readable-report section. Fixtures:
      `TEMPORAL_BOUNDARY_PRECISION_fires.sql` (real source: Aaron Bertrand's
      article) + two near-misses (`_clean.sql`: a range comparison instead of
      BETWEEN; `_matching_scale_clean.sql`: a boundary literal precise to the
      column's full declared scale) + 6 unit/oracle tests
      (`TemporalBoundaryPrecisionTests`, `TemporalBoundaryPrecisionOracleTests`
      — the oracle test inserts a real probe row at the exact edge of the
      precision gap and proves the buggy query misses it while the safe
      rewrite includes it, the same self-authored-probe-row discipline
      `WriteLossOracleTests` uses, since this is a runtime DML/query-result
      behavior, not a query-plan one).
- [x] UPPER/LOWER on a column — **shipped, scope corrected from the
      checklist's original framing**: oracle-verified the wrap forces a scan
      **regardless of collation family** (built one CS-collation and one
      CI-collation indexed table, 5,000 rows, `UPDATE STATISTICS ...
      FULLSCAN` — both produced `Index Scan`, never `Index Seek`) — the
      checklist's premise that SQL Server special-cases away the wrap for a
      case-insensitive collation is **false** as a seek-preservation claim
      (it's only true as a *result-set-correctness* claim: the row set
      doesn't change). So this is syntactic-with-index-weighting, never
      suppressed by collation — only the finding's own remediation message
      changes: a CI-collation column's wrap is a provably safe, zero-risk
      deletion; a CS/BIN-collation column's wrap is load-bearing and needs a
      real rewrite. New `SargabilityFindingKind.CaseFoldOnColumn`,
      `Predicates/NonSargablePredicateScanner.cs` (`AddCaseFold`,
      `DescribeCaseFoldRemediation`). Fixtures
      `CASE_FOLD_ON_COLUMN_{fires,clean}.sql` (fires is labeled synthetic —
      no confirmed distinct real bug report, only general advisory blog
      coverage — per CLAUDE.md's rare-exception allowance) +
      `CaseFoldColumnPipelineTests` (7 tests: both collation families
      oracle-confirmed scanning, both remediation messages, computed-column
      guard both directions).
- [x] **Mandatory precision guard for every rule in this section** — shipped
      as a genuine generalization, not per-rule copy-paste: new
      `Predicates/ComputedColumnMatcher.cs`, a structural-equality comparer
      over arbitrary `FunctionCall`/`CastCall`/`ConvertCall` subtrees
      (column references, literals, CAST/CONVERT target type + CONVERT
      style, and the DATEPART-unit `IdentifierLiteral` shape), reused by both
      the date-function and case-fold rules above, and also retrofitted onto
      the existing generic `CastCall`/`ConvertCall` case so a
      `CONVERT(varchar, col, n)`-style predicate gets the same suppression.
      Deliberately does NOT refactor the already-shipped, already-tested
      `JsonComputedColumnMatcher` (JSON_VALUE keeps its own narrower,
      hand-parsed matcher) — avoids regressing working code for a refactor
      with no new behavior to show for it.
- [x] Index-existence weighting for all sargability findings — **shipped**:
      `ScanReportBuilder.cs` orders `Tier1Findings` by `Indexed` (true first,
      unresolved second, false last — CLAUDE.md's "unresolved ≠ false"
      discipline) before source position, mirroring `TypedFindings`' own
      existing `Indexed` ordering, which `Tier1Findings` never had until now.
      Tested: `ScanReportBuilderTier1OrderingTests`.
- [x] `CHARINDEX(x, col)` / `LEFT(col, n) =` — rewritable-to-sargable forms —
      **shipped**: new `SargabilityFindingKind.CharindexOrLeftOnColumn`, split
      from the generic catch-all specifically to carry a rewrite verdict in
      `Detail`: `CHARINDEX(x, col) = 1` / `LEFT(col, n) = 'x'` (with
      `LEN('x') = n`) are both exactly equivalent to `col LIKE 'x%'`, a real
      sargable rewrite — any other comparison shape is a genuine substring
      search with no such rewrite, and the finding says which case applies.
      Both directions oracle-confirmed (`CharindexLeftRewritePipelineTests`):
      the original form scans, the rewritten `LIKE 'x%'` form seeks. **Oracle
      discovery mid-implementation:** `LEFT(col, n)` parses to ScriptDOM's own
      dedicated `LeftFunctionCall` node, not a generic `FunctionCall` the way
      `CHARINDEX` does — the first implementation attempt silently produced
      zero findings for every `LEFT` fixture because of this, caught by a
      failing unit test before shipping; `ComputedColumnMatcher` gained a
      matching `LeftFunctionCall` structural-equality case (oracle-confirmed
      `sys.computed_columns.definition` stores it unnormalized as
      `left([col],(n))`, unlike `YEAR`/`MONTH`/`DAY`'s `DATEPART` rewrite, so
      no canonicalization was needed there). Real base rate is near-zero
      locally (12 `CHARINDEX` findings across 5 files, 0 genuine
      `LEFT(col,n)`-wrapping-a-column occurrences in the local RM_ corpus —
      the rule is real and shipped, just not exercised by this particular
      database) — fixtures: `CHARINDEX_PREFIX_MATCH_fires.sql` +
      `CHARINDEX_SUBSTRING_fires.sql` (real source: MS Learn forum, "Charindex
      very bad performance"), `LEFT_PREFIX_MATCH_fires.sql` (labeled
      synthetic — no distinct real bug report found for this exact shape, per
      CLAUDE.md's rare-exception allowance).
- Oracle: seek-vs-scan probes for every rule above; the case-fold rule's own
  CS-vs-CI fixture pair confirms the SAME outcome for both (not a different
  one, correcting the checklist's original "verdict differs by collation"
  premise).

### Oversized and MAX-typed parameters — shipped
Under-represented in existing rule sets, adjacent to the existing conversion
code, high precision.

- [x] `varchar(max)`/`nvarchar(max)` parameter or variable compared to a
      `(n)`-typed column — blocks predicate pushdown even when the base type
      matches; no seek. **Correction to the item's own premise, oracle-
      confirmed:** a bounded-length column compared against a MAX-typed value
      of the same category does NOT force a scan — it still seeks, via
      `GetRangeWithMismatchedTypes` (`RangeSeek`, the same bound-search
      mechanism `GetRangeThroughConvert` uses for a Windows-collation implicit
      conversion), cheaper than a scan but dearer than a clean seek. A first
      pass, based on an unverified sub-agent oracle claim, coded this as
      collation-dependent (SQL_\* collations getting a clean `SeekPreserved`,
      only Windows collations getting `RangeSeek`); independently re-verified
      against the Docker oracle with real populated tables (2,000 rows,
      `UPDATE STATISTICS ... WITH FULLSCAN`) and found the claim wrong — BOTH
      collation families produce `PhysicalOp="Index Seek"` via
      `GetRangeWithMismatchedTypes` identically. `VerdictClassifier
      .ClassifySameCategory` (`src/SilentScan.Core/Rules/VerdictClassifier.cs`)
      now returns `RangeSeek` unconditionally on an `IsMax` mismatch, no
      collation branch. Oracle-tested in
      `TypedPredicateExtractorTests.BoundedColumnVsMaxTypedParameter_RangeSeek_OracleConfirmed`
      (both collation families as theory cases, real deployed tables, real
      captured plan XML).
- [x] Parameter declared longer than the compared column (`varchar(200)` param
      vs `varchar(50)` column) — memory-grant inflation; lower severity.
      **Scoped down from a verdict-bearing finding to a purely informational
      one, oracle-falsified:** probed directly whether a bare equality
      predicate against an oversized parameter shows any memory-grant
      difference in `SET SHOWPLAN_XML` output on its own — it does not; only
      a downstream Sort/Hash-consuming operator would size a memory grant off
      the parameter's declared length, and a compile-only equality predicate
      never reaches one. `OversizedParameterFinding`
      (`src/SilentScan.Core/Predicates/OversizedParameterFinding.cs`) is
      reported with no verdict field and SARIF level `warning` (structural
      report, not a plan-shape claim for this specific predicate), matching
      the pattern Paul White (sqlperformance.com, "Performance Myths:
      Oversizing String Columns") and Brent Ozar's memory-grant series warn
      about. Extraction lives in `TypedPredicateExtractor
      .TryAddOversizedParameterFinding` — same-category string/binary pair,
      neither side MAX-typed (that's this section's own separate item), the
      other operand a real variable/parameter/expression (never a literal —
      a literal's length is its actual content, not a declared size).
      Future follow-up noted directly in the finding's own doc comment: a
      live-mode enhancement could rank findings by whether the plan cache
      shows a real memory-grant-consuming operator downstream, mirroring how
      `--plan-cache-evidence` already ranks conversion findings by observed
      cached-plan behavior — not built now, no live signal available yet to
      justify it. Unit-tested (fires on longer variable/parameter; does not
      fire on literal, MAX-typed, shorter/equal, or cross-category operands)
      in `TypedPredicateExtractorTests`. Coverage against the local RM_ test
      database: **180 findings** (`scan-db --format json`,
      `OversizedParameterFindings.Count`).
- [x] MAX-typed columns used as predicate/join targets (can't be an index
      key) — catalog-only report. `MaxTypedColumnFinding`/
      `MaxTypedColumnScanner` (`src/SilentScan.Core/Predicates/
      MaxTypedColumnScanner.cs`) walk `DatabaseCatalog.Tables` directly, no
      AST or predicate site needed — SQL Server itself enforces this at
      `CREATE INDEX` time (Msg 1919), so it's a plain catalog-metadata fact,
      not a plan-behavior claim needing an oracle probe. Unit-tested
      (`MaxTypedColumnScannerTests`: fires per MAX-typed string/binary
      subtype, never fires on bounded types, stable table/column ordering).
      Coverage against the local RM_ test database: **245 findings**
      (191 `varchar(max)`, 35 `varbinary(max)`, 19 `nvarchar(max)`).
- [x] **A MAX-typed (or otherwise oversized) *comparison value* defeats a seek
      even against a matching indexed computed column** — oracle-found while
      landing the JSON_VALUE computed-column suppression above: comparing
      `JSON_VALUE(Payload, '$.status')` (matched to an indexed computed
      column) against an `NVARCHAR(MAX)` variable. **Corrected once this
      section's first item was oracle-corrected:** it does not force a scan —
      it still seeks, via `GetRangeWithMismatchedTypes`, exactly like any
      other bounded-column-vs-MAX-value comparison; the matched computed
      column only removes the syntactic Tier-1 `FunctionWrappedColumn`
      finding, it does not exempt the comparison from the same MAX-mismatch
      classifier rule as this section's first item. Oracle-confirmed directly
      in `JsonComputedColumnSuppressionTests
      .MatchingIndexedComputedColumn_MaxTypedComparisonValue_StillSeeksButThroughGetRangeWithMismatchedTypes`
      — real captured plan XML shows both `PhysicalOp="Index Seek"` and
      `GetRangeWithMismatchedTypes`, reusing the class's already-deployed DDL
      (no new fixture file needed; the DDL shape was already exactly right).

All four sub-items wired end-to-end: `ScanReportBuilder` → `ScanReport`
(schema version bumped 10 → 11) → SARIF (`SarifRuleCatalog` +
`SarifReportWriter.ToResult` overloads) → readable report
(`ReadableScanReportWriter`, summary rows + dedicated sections). Full test
suite green (2,635 tests) after landing.

### Under-length and length-defaulted string declarations
The third leg of the parameter-sizing stream, and the only one still missing:
the shipped pair covers *too wide* (`MAX`-typed, and declared longer than the
column). Nothing covers *too narrow*, which is the strictly worse failure —
it doesn't just widen an estimate, it silently truncates the compared value so
the predicate matches the wrong rows or none at all. Found on an incumbent read
where it exists only as a bare "declaration has no length" syntax check with no
column awareness at all; resolved against the catalog it becomes a real
finding, and the machinery is already built.

- [ ] **`varchar`/`nvarchar`/`char`/`binary` declared with no length at all.**
      Defaults **measured against the engine, not quoted from docs** (probe
      2026-08-16, recorded in `detection-reference.md` Appendix 8): length **1**
      in a `DECLARE` or parameter declaration, **30 characters** in
      `CAST`/`CONVERT` (`nvarchar` 30 characters = 60 bytes). Two different
      defaults for the same spelling is why this gets written by accident.
      Detection is syntax-only, but the finding only earns its place once the
      catalog says what it is compared against, so it must be reported jointly
      with the compared column, not standalone.
      **Scope wider than the incumbent's**, which covers only `varchar`/
      `nvarchar` and only in variable/parameter declarations: include
      `char`/`nchar`/`binary`/`varbinary`, and resolve `sysname` and other
      alias types to their underlying type before judging.
- [ ] **Declared shorter than the compared column** (`varchar(10)` variable or
      parameter vs a `varchar(100)` column) — the exact mirror of the shipped
      "declared longer than the compared column" rule, and it should reuse that
      rule's comparison and reporting path rather than starting a new one.
      **The seek is preserved** (measured: a `varchar(3)` variable against an
      indexed `varchar(50)` column still plans an Index Seek with the variable
      as the seek predicate), so this is emphatically **not** a verdict-bearing
      rule and must not be reported as one — the defect is in *which rows
      match*, not in how they are found. Two consequences, and the finding
      should say which applies:
      * **Silent truncation of the compared value.** No error and no warning:
        assigning a 10-character literal to a `varchar(3)` yields `'ABC'`, and
        to an unsized `varchar` yields `'A'` (both measured).
      * **A `LIKE` pattern whose wildcard is truncated away** — the sharpest
        case and the one the fixture should be built from: `'ABCDEF%'` assigned
        to a `varchar(4)` becomes `'ABCD'`, silently converting a prefix match
        into an equality match. The predicate still seeks; it just answers a
        different question. Same shape for a truncated range bound.
- [ ] **Precision guards (mandatory):** don't fire when the declaration is
      never actually compared to a catalog-typed column (the sizing is then
      nobody's business but the author's); don't fire on assignment from a
      source the pipeline can't type, report `Unknown` instead; treat
      `sysname` and other aliases by resolving to the underlying type first.
- [ ] Carry the standard schema — indexed?, depth, and origin (both the
      declaration site and the predicate site, which are usually different
      lines and sometimes different modules once dynamic SQL is involved).
      Engine-version note: no version sensitivity; the defaults are the same
      across every supported release, which makes this one of the cheaper
      rules to state honestly.
- [ ] Oracle: **none — the whole rule is syntactic-plus-catalog**, per the
      measured seek-preservation above. Ships fire/near-miss fixtures from
      real, internet-sourced bugs, and nothing else.

### Join predicate incomplete vs. the backing foreign key — shipped
Folded in from the incumbent-catalog read — "strongest single find" there,
nobody resolves it properly.

- [x] A join missing a backing FK entirely, or joining on fewer columns than
      a composite FK defines. The partial-composite case is a real
      correctness *and* plan defect (silent row multiplication); pure catalog
      work (walk `sys.foreign_key_columns` against the query's own join
      columns). **Scoped to the partial-composite case only, deliberately:**
      "FK exists but the join matches none of its columns" is a different,
      much lower-precision claim (bridge tables, hierarchy self-joins, and
      business-key joins are all legitimate reasons a join wouldn't use a
      declared FK at all) and is out of scope — this stream only fires when
      the join already equates at least one of a composite FK's column pairs
      and still omits at least one other, uncovered anywhere else in the same
      statement (another JOIN's ON clause, or the WHERE clause — a composite
      key legitimately split across `ON a.Id = b.Id WHERE a.TenantId =
      b.TenantId` is not a bug). A further precision guard suppresses the
      finding when the column subset the join DOES use is itself a superset
      of a real unique index's key columns on the referenced side — that
      join can never multiply rows regardless of what the FK's remaining
      columns would have added. Reported at `FindingConfidence.Medium` by
      default (not `High`, unlike every other catalog-only stream this
      session shipped): a narrower join CAN be a genuine, deliberate fan-out
      (e.g. joining every historical revision), which static analysis alone
      cannot always tell apart from a forgotten column — this is stated
      honestly in the finding's own severity rather than overclaimed.
      `PartialCompositeForeignKeyJoinFinding`/`PartialCompositeForeignKeyJoinScanner`
      (`src/SilentScan.Core/Predicates/PartialCompositeForeignKeyJoinScanner.cs`)
      — a hybrid pass: catalog-only for FK discovery (live-mode only, like
      every other `DatabaseCatalog.ForeignKeys` consumer), but a real
      per-file AST walk (reusing `FromScopeResolver`/`ScalarExpressionResolver`,
      the exact machinery the typed-predicate pipeline already uses) to see
      which columns a JOIN's own ON clause — or a legacy comma join's
      WHERE-clause condition — actually equates. Covers ANSI `JOIN`, the
      legacy comma-join shape, and `UPDATE ... FROM`/`DELETE ... FROM`.
      **Known v1 scope limit:** only a direct, single-table-to-single-table
      join is inspected — a 3+-way join chain (`A JOIN B ON ... JOIN C ON
      ...`, where a later join's own two "sides" are themselves composite
      sub-trees) is skipped rather than guessed about, since the ON clause
      of the outer join could reference any table from the accumulated left
      scope, not just the two immediate operands.
      Not verdict-bearing — a correctness finding (the defect is the ROW
      COUNT the join produces, not its access path), oracle-confirmed with
      real seeded data rather than a plan-XML marker: two revisions of the
      same order, one order line tied to only one revision — a partial join
      on `OrderId` alone returns 2 rows (COUNT(*) fans the single order line
      out across both revisions) where the full composite join correctly
      returns 1 (`PartialCompositeForeignKeyJoinOracleTests`). Version-
      insensitive: row multiplication from a partial equality join is pure
      relational algebra, unaffected by CE version, interleaved execution,
      or UDF inlining. Wired end-to-end (`ScanReport` schema version 11 →
      12, SARIF rule catalog + writer, readable report). Unit-tested for the
      matching/suppression logic (`PartialCompositeForeignKeyJoinScannerTests`
      — 13 cases: full vs. partial coverage, WHERE-split coverage, coverage
      against an unrelated third table not counting, the unique-index
      suppression guard, zero-overlap non-firing, comma joins, `UPDATE
      ... FROM`). Real coverage against the local RM_ test database: **865
      findings** (214 composite FKs exist in that catalog) — dominated by one
      recurring, real pattern: a multi-tenant `AgencyID` column consistently
      present in the FK but omitted from the join, matching the exact
      SCD2/multi-tenant bug class the fixtures cite as the mechanism's real-
      world provenance (Kimball's effective-dated dimension-join literature;
      the widely documented multi-tenant "always join on tenant_id too"
      SaaS bug class).

### SET options that silently disable plan features — shipped (2 of the originally-proposed 3 kinds)
Folded in from the incumbent-catalog read. Universally filed elsewhere as
style hygiene; the actual consequence is plan-shape, and it's
catalog-verifiable per module, not a guess.

**Correction to this section's own premise, oracle-confirmed the hard way:**
the checklist's original text (echoing a common but imprecise summary of
Microsoft's own docs) claimed all of `ARITHABORT`/`NUMERIC_ROUNDABORT`/
`QUOTED_IDENTIFIER` (and, further down, `ANSI_NULLS`/`ANSI_PADDING`/
`ANSI_WARNINGS`/`CONCAT_NULL_YIELDS_NULL`) gate whether the optimizer can use
an indexed view or filtered index at query-compile time. Probed directly
against the Docker oracle (SQL Server 2022 Developer edition, real seeded
data, both a real filtered index and a real indexed view, `SET
SHOWPLAN_XML`-compile-only, no execution): **`QUOTED_IDENTIFIER OFF` and
`NUMERIC_ROUNDABORT ON` both demonstrably degrade a filtered-index seek to a
table scan and an indexed-view match to a base-table scan** — confirmed.
**`ARITHABORT OFF` alone changed neither plan at all** — the filtered index
still sought, the indexed view still matched, oracle-refuted directly, twice,
with `PhysicalOp`/`IndexKind` checked precisely (not a raw substring match on
the index's own name, which is a trap: an unused index's name still appears
in `OptimizerStatsUsage/StatisticsInfo` even when it was never chosen as an
access path — an earlier draft of this stream's own oracle test fell into
exactly that trap and had to be corrected). **`ARITHABORT` is dropped from
this stream entirely** rather than shipped as an unverified or
false-positive-prone rule — CLAUDE.md's "precision beats recall everywhere."
The remaining four options (`ANSI_NULLS`/`ANSI_PADDING`/`ANSI_WARNINGS`/
`CONCAT_NULL_YIELDS_NULL`) were NOT probed this session — do not assume they
behave like QUOTED_IDENTIFIER/NUMERIC_ROUNDABORT OR like the falsified
ARITHABORT; each needs its own direct oracle confirmation before being added,
exactly the same way ARITHABORT's own assumption just failed one.

- [x] `QUOTED_IDENTIFIER OFF` means **indexed views and filtered indexes
      cannot be used** by that module — catalog flag
      (`sys.sql_modules.uses_quoted_identifier`, already read by
      `LiveModuleReader` for parsing purposes, now also registered per-module
      on `DatabaseCatalog` via `AddModuleUsesQuotedIdentifier`/
      `TryGetModuleUsesQuotedIdentifier`). Baked in wholesale at CREATE/ALTER
      compile time — a mid-body `SET QUOTED_IDENTIFIER` statement has no
      bearing on this; only the catalog-level flag does.
- [x] `NUMERIC_ROUNDABORT ON` — same plan consequence, no baked-in
      `sys.sql_modules` column (verified), so this half is a syntax-only
      `SET NUMERIC_ROUNDABORT ON` scan of the module's own body
      (`PredicateSetStatement`/`SetOptions.NumericRoundAbort` flag bit —
      `SET NUMERIC_ROUNDABORT, ANSI_NULLS ON` is legal T-SQL sharing one
      `IsOn`, handled via the flags bit test, not statement-per-option).
- [x] **Precision guard (mandatory), shared by both kinds:** only fire when
      the module's own body actually touches a table with a filtered index
      or an indexed view — `ModuleReachableObjectWalker`
      (`src/SilentScan.Core/Predicates/ModuleReachableObjectWalker.cs`) walks
      every `NamedTableReference` in the module's own AST directly against
      the catalog, plus transitively through a referenced VIEW'S own
      containment for free from the already-resolved `LineageCatalog`
      (`ColumnProvenanceAnalysis.FindUnderlyingBaseColumns`, no re-parsing of
      the view's own body). **Known, deliberate scope limit:** does NOT
      recurse through a called PROCEDURE's own body — `ScanReportBuilder`'s
      own documented design never holds every module's parsed AST alive
      simultaneously (live-mode reparse runs ~200x source text size), and a
      proc-call-transitive walk would need exactly that. A false negative
      here is the honest trade against a real, measured memory property of
      this codebase's scan pipeline, not a gap silently claimed as covered.
      New catalog registry needed for the indexed-view half: `DatabaseCatalog
      .IndexedViews`/`.IsIndexedView`/`.AddIndexedView`, read live via
      `LiveCatalogReader.ReadIndexedViewsAsync` (`sys.indexes` joined against
      `sys.views` instead of `sys.tables` — indexed views were not read
      anywhere in this codebase before this stream; a view is never a
      `CatalogTable`, so this is a narrow side-registry like `ForeignKeys`,
      not a `Tables` entry).
      Oracle: `PlanXmlCapture` gained a `sessionSetStatements` overload (a
      separate overload, not an added optional parameter, so every existing
      positional call site keeps compiling unchanged) to pin a session-level
      `SET` before compilation, still entirely compile-only.
      Unit-tested (`SetOptionScannerTests`: direct/transitive-through-view
      touch, catalog-flag-unknown never guesses, comma-separated option list,
      short-circuit when nothing could trigger a finding). Oracle-tested
      (`SetOptionOracleTests`: real seeded filtered index, `PhysicalOp`/
      `IndexKind` checked directly for both settings, plus the ARITHABORT
      exclusion kept as a permanent regression guard). Real coverage against
      the local RM_ test database: **99 findings**, all
      `QuotedIdentifierOffBlocksIndexedFeature` (0 `NUMERIC_ROUNDABORT ON`
      occurrences found in that codebase's own text) — every one a module
      compiled under legacy `QUOTED_IDENTIFIER OFF` that touches a real
      filtered unique constraint.
- [ ] **Complete the required-option set — not resumed this session.** The
      four remaining candidates (`ANSI_NULLS`/`ANSI_PADDING`/
      `ANSI_WARNINGS`/`CONCAT_NULL_YIELDS_NULL`) still need their own direct
      oracle confirmation before landing as additional kinds on this stream —
      do not assume any of them behaves like QUOTED_IDENTIFIER/
      NUMERIC_ROUNDABORT or like the falsified ARITHABORT without checking.
      `ANSI_NULLS` is a baked-in module setting (`sys.sql_modules
      .uses_ansi_nulls`) if it does turn out to matter; the other three would
      need the same in-body `SET` scan pattern `NUMERIC_ROUNDABORT` already
      uses. Reuse `ModuleReachableObjectWalker` verbatim for the guard.
      **The catalog-vs-syntax half of that is now settled** (independent of the
      plan question, which is still open and still needs its own probe):
      `sys.sql_modules`' full column list was read off the engine on
      2026-08-16 and carries exactly two session settings, `uses_ansi_nulls`
      and `uses_quoted_identifier`. So `ANSI_NULLS` is the only one of the four
      with a catalog half at all, and `ANSI_PADDING`/`ANSI_WARNINGS`/
      `CONCAT_NULL_YIELDS_NULL` are confirmed syntax-scan-only. No need to
      re-check that list; see `detection-reference.md` Appendix 8.
- [ ] **`ANSI_PADDING OFF` as a second, independent finding — a comparison
      seed, not just a plan-feature blocker.** With the option off, trailing
      blanks are stripped on insert into `varchar`/`varbinary` columns, so a
      column's stored values stop matching the padding semantics the rest of
      the codebase assumes, and equality/`LIKE` comparisons against that column
      change meaning. That is the same family as the shipped collation and
      conversion work — a per-column property that silently changes what a
      predicate does — and it is catalog-visible per column
      (`sys.columns.is_ansi_padded`), not just per module. Scope it as: a
      predicate or join comparing a non-ANSI-padded `varchar` column against a
      padded one, or against a literal with trailing whitespace. Precision
      guard: fixed-length and `nvarchar` columns are unaffected; only fire
      where the catalog actually reports the column as not padded.
      **Measured 2026-08-16, so the premise here does not need re-deriving
      (unlike the plan-feature premise this section had to correct above — and
      note this is a deliberately *different* claim from that one, so the
      ARITHABORT falsification says nothing about it either way):**
      `is_ansi_padded` is per column and is captured at CREATE time from the
      then-current session setting, so one table can hold both kinds; the same
      insert of `'abc   '` stores 3 bytes in a non-padded `varchar(20)` column
      and 6 in a padded one. **This is a data-semantics finding, not a
      plan-shape one** — it changes which rows match, not how they are found,
      the same posture as the under-length rule in Tier 1. It therefore needs
      **no oracle** and must not be reported with a verdict; drop the earlier
      suggestion of a `CONVERT_IMPLICIT`/residual-predicate probe, which was
      assuming a plan consequence that has no reason to exist.

---

## Tier 2 — strong candidates (precise rules exist, new machinery needed)

### Catch-all / kitchen-sink predicates
425 modules in the production copy (`... OR @param IS NULL`).

- [ ] `(col = @p OR @p IS NULL)` and COALESCE/ISNULL-disabled optional
      filters — one plan must serve all parameter combinations.
- [ ] **Precision guard (mandatory):** `OPTION(RECOMPILE)` on the statement
      largely neutralizes it — detect and either suppress or downgrade.
      70 modules locally already use OPTION(RECOMPILE).
- [ ] Sibling: parameter overwritten before use in a predicate
      (sniffing-defeat — straight-line dataflow we already have from
      dynamic-SQL tracing).

### Local-variable predicates
- [ ] `WHERE col = @v` where `@v` is DECLAREd in the batch (not a parameter) —
      density-vector estimate instead of sniffed value. Distinguishable from
      parameters purely in the AST; same OPTION(RECOMPILE) guard as the
      "Catch-all / kitchen-sink predicates" section above.

### NOT IN over a nullable subquery column
346 modules locally use `NOT IN (SELECT ...)`.

- [ ] Fires **only** when the catalog says the subquery column is nullable —
      correctness trap plus expensive null-aware anti-semi-join. The
      nullability gate is what makes this precise where linters spray.

### UPDATE ... FROM without source uniqueness
- [ ] Target joined to a source whose join columns carry no PK/unique
      constraint — nondeterministic multi-match update (each target row takes
      an arbitrary source row). Catalog-gated; correctness-first finding with
      a real perf angle (MERGE raises where UPDATE silently picks).

### Forced-serial construct inventory
- [ ] Table-variable **modification** (INSERT/UPDATE/DELETE @t) — whole plan
      serial; read-only use does not fire (direction-style distinction).
      821 modules locally use table variables.
- [ ] Dynamic/keyset cursors; cursor without `LOCAL FAST_FORWARD` as the
      crisp subrule (197 modules with cursors locally).
- [ ] The finite serial-forcing intrinsics list (IDENT_CURRENT, ERROR_NUMBER,
      @@TRANCOUNT, OBJECT_ID, ...) in queries — encodable, documented.
- [ ] Serial-zone constructs as informational: TOP row goals, recursive CTEs,
      MSTVF refs (already shipped — MSTVF-as-fence stream), global scalar aggregates.
- Note: several of these fold naturally into the already-shipped scalar UDF
  stream or the already-shipped MSTVF-as-fence stream rather than standing
  alone — decide at design time.

### Lineage-metric findings (cheap adds on existing passes)
- [ ] Nested-view depth report — we already compute topo order; emit depth ≥ N
      as a finding with the chain (57 views reference other views locally).
- [ ] Multi-referenced CTE — inline macro re-executed per reference; count
      references in the AST. Rarely covered anywhere; high precision.
- [ ] Untrusted (WITH NOCHECK) FK/CHECK constraints — optimizer forfeits join
      elimination; pure catalog flag (`is_not_trusted`).
      **Origin half, added from the incumbent read:** the catalog flag says a
      constraint is untrusted but not *why*, and the answer is almost always a
      re-enabling statement that omitted `WITH CHECK` (the default there is
      `WITH NOCHECK`, the opposite of the default on the original `ADD
      CONSTRAINT`). Since we already parse deployment/migration text, pair the
      catalog finding with the statement that caused it wherever the scan can
      see it — that turns an unactionable flag into a one-line fix, and origin
      attribution is the schema every stream here already carries.
- [ ] Cascading FK actions (ON DELETE/UPDATE CASCADE) — hidden serial
      multi-table work per DML; catalog-only, informational.
- [ ] **Post-expansion join width.** Every surveyed tool counts tables in the
      written `FROM`/`JOIN` list and warns past a threshold; that count is
      meaningless when half the sources are views. The number that matters is
      the *expanded* one — base tables after resolving views and inline TVFs
      through the lineage pass — because that is what the optimizer actually
      reorders, and past roughly a dozen joined relations it stops searching
      exhaustively and takes a greedy plan. We are the only tool that can
      compute it. Report the expanded count, the written count, and the chain
      that inflated it; rank by the gap between the two, since a query that
      *looks* like a three-table join and expands to twenty is the finding
      nobody else can produce. Confirm the engine's actual reordering
      thresholds against the oracle before quoting a number in output —
      the "past a dozen" figure above is folklore until measured.
- [ ] **`SELECT *` inside a view or inline TVF.** The bare `SELECT *` rule is
      an explicit Tier 3 skip below and stays skipped — but the in-a-view case
      is a different defect with a lineage consequence: the column list is
      frozen at create time, so it silently disagrees with the base table
      after any change (which is exactly the drift the live parity gate already
      detects), and it forces every consumer to carry the full width whether or
      not it selects from it, which is how a covering index stops covering.
      Fire only at depth ≥ 1 and only when the consuming query selects a strict
      subset of the expanded columns — that guard is what keeps it out of the
      style-linting territory the plain rule lives in.

### Dynamic SQL quality (extends the existing dynamic-SQL pass)
123 modules use `EXEC(...)`, 51 use sp_executesql locally.

- [ ] Concatenated **value** (vs identifier) in proven-constant dynamic SQL —
      unparameterized: plan-cache pollution + per-literal compiles. We already
      prove constancy; classifying concatenation operands as value/identifier
      is incremental.
- [ ] `EXEC(string)` where sp_executesql with params was possible (only when
      the taint analysis shows a parameterizable value) — report, don't guess.
- [ ] **Temp-table shape mismatch across a proc-call boundary** — one small
      T-SQL type-checker in the wider tool landscape checks a version of this;
      ours would be catalog+plan-backed, not a type-inference heuristic. T-SQL
      scoping means a `#temp` table created by a calling proc stays visible to
      a proc it calls during that call, and `INSERT INTO #temp EXEC OtherProc`
      assumes the executed proc's result-set shape matches `#temp`'s declared
      columns — silently wrong column count/order/type is either a runtime
      error or, worse, a silent per-column implicit conversion that our
      existing conversion stream would never see because it never runs on the
      `EXEC`'d proc's own SELECT list against the caller's temp-table DDL. This
      is squarely in-scope by CLAUDE.md's own boundary: temp tables created
      inside proc bodies are explicitly one of the module-body objects we
      already parser-model (the engine doesn't expose them). Ground truth for
      the executed proc's actual shape: `sys.dm_exec_describe_first_result_set`
      (compile-only, same guarantee the `scan-db` describe-only path already
      relies on) rather than re-deriving it from the proc's own SELECT list.
      Verdict: column-position type mismatch between the temp table's DDL and
      the executed proc's described result set. Oracle: compile-only probe of
      the `INSERT ... EXEC` batch under SHOWPLAN_XML, same discipline as the
      shipped streams.

### Schema-scan UDF and computed-column findings (found on completeness audit)
Distinct trigger from the already-shipped scalar UDF stream's plan-based
findings: these fire from catalog
metadata alone, independent of whether the object ever shows up in a cached
plan, so they need no plan/oracle involvement to report (though should still
get an oracle fixture for the serial-plan consequence).

- [ ] CHECK constraint whose definition references a scalar/CLR function —
      forces serialized execution of every query and maintenance operation
      against the table. Pure catalog scan (`sys.check_constraints`
      definition text against `sys.objects` function list).
- [ ] Non-persisted computed column (`is_persisted = 0`), independent of
      whether it references a UDF — recomputed on every read; broader trigger
      than the UDF-specific rule in the already-shipped scalar UDF stream,
      catalog-only.
- [ ] Deprecated `*=`/`=*` outer-join operators — legacy syntax that can
      silently change join semantics and plan shape across engine versions;
      pure AST syntax check, near-zero FP risk, cheap to add.

### Halloween Protection and self-referencing DML
- [ ] `INSERT`/`UPDATE`/`DELETE`/`MERGE` whose source query reads the same
      target table (hole-filling `INSERT ... WHERE NOT EXISTS`,
      `UPDATE ... FROM` self-join) — forces a blocking eager spool, distinct
      mechanism from the UDF-in-DML case already covered. Pure syntax: target
      table object also appears in the statement's read-side FROM/subquery.

### Temporal table history-side index gap
- [ ] System-versioned temporal table (`sys.tables.temporal_type`) whose
      history table lacks the index set the current table has — `FOR
      SYSTEM_TIME AS OF/BETWEEN` queries rewrite to a UNION ALL between the
      two tables, so a sargable predicate on the current side does nothing
      for the history side, silently forcing a scan on half the union.
      Catalog-only: compare index definitions between `parent_id` and
      `history_table_id`.

### Small precise adds (each an afternoon, not a stream)
- [ ] Proc authored `WITH RECOMPILE` — compiles every call, invisible to
      cache-based monitoring; pure catalog flag (`sys.sql_modules
      .is_recompiled`, column existence confirmed 2026-08-16, so this really is
      the one-column job it was assumed to be).
- [ ] **Two more `sys.sql_modules` columns nobody has claimed yet**, surfaced
      while confirming the above and recorded so they are not rediscovered:
      * `inline_type` / `is_inlineable` give **the engine's own verdict** on
        whether a scalar UDF is inlineable under 2019+ — i.e. ground truth for
        the shipped scalar-UDF stream, which currently reasons from our own
        reimplementation of the blocker list in `detection-reference.md`
        Appendix 3. Worth a parity check of the two against the local database:
        any disagreement is a bug in our blocker list, and agreeing lets the
        stream cite the engine instead of a hand-maintained list.
      * `uses_database_collation` marks a schema-bound module whose correctness
        depends on the database collation — a collation dependency the shipped
        collation work does not currently consider at all.
- [ ] `RANGE` instead of `ROWS` in window-function frames — on-disk spool per
      partition; purely syntactic, near-zero FP risk.
- [ ] Trigger content scan — run trigger bodies through the existing pipeline
      so cursors/UDFs/MSTVFs inside triggers surface as hidden per-DML cost
      (the modules are already in `sys.sql_modules`; this is mostly wiring).
- [ ] `COMPUTE`/`COMPUTE BY` deprecated aggregate constructs — bypasses
      normal set-based aggregate optimization; syntax-only, rare but trivial.
- [ ] `WAITFOR DELAY`/`WAITFOR TIME` inside a routine or batch — holds a
      worker thread idle, contributing to worker exhaustion under load;
      syntax-only.
- [ ] Transaction hygiene pair: lengthy work (loops, RBAR, external calls)
      between an error and its `ROLLBACK` extends lock hold duration;
      `BEGIN TRANSACTION` with no reachable `ROLLBACK`/`COMMIT` on some path
      leaves locks held indefinitely. Both are control-flow/dataflow checks
      over the AST, no catalog needed.
- [ ] **`TOP(100) PERCENT` ignored by the optimizer** and **`ORDER BY` in a
      view / inline TVF** (folded in from the incumbent-catalog read) — same
      family, both commonly written to "force" ordering that is not
      guaranteed. Syntactic, near-zero FP.
- [ ] **`IF` statements containing queries inside a procedure** (folded in
      from the incumbent-catalog read) — estimation and recompile
      consequences; nobody frames it as a performance finding.
- [x] **Non-foldable nondeterministic intrinsic in a predicate** — *proposed
      and killed the same session, before any code was written.* Worth keeping
      as a worked example of the admission rule doing its job. Proposed premise:
      `NEWID()`/`RAND()`/`CRYPT_GEN_RANDOM()` cannot fold to a runtime constant,
      so a predicate containing one is re-evaluated per row and cannot seek.
      Both halves of that premise were probed against the Docker oracle
      (2026-08-16) and **both are false**:
      * **Bare `RAND()` is a runtime constant**, folded once per query exactly
        like `GETDATE()`/`SYSDATETIME()` — measured as one distinct value across
        200 rows, against 200 distinct values for `NEWID()`,
        `CRYPT_GEN_RANDOM()` and `RAND(<non-constant>)`. So the incumbent's
        three-name list is simply wrong, and adopting it would have shipped a
        false positive on the most commonly written member of it.
      * **Per-row evaluation does not cost the seek anyway.** `WHERE
        indexed_col = NEWID()` compiles to an Index Seek with `newid()` as the
        seek predicate — a per-row seek, not a scan. The plan is fine; the query
        is merely nonsense. That is a correctness smell, and one already covered
        by the always-false-predicate family skipped in Tier 3.
      Nothing survives that is both true and a performance finding, so this is
      **not** being built. The genuine relatives (`ORDER BY NEWID()` forcing a
      sort; a nondeterministic call in a correlated position) are ordinary
      syntax patterns with no need for our machinery. Recorded here rather than
      in Tier 3 because the value is the falsification, not the verdict.
- [ ] **Explicit-length audit of `CAST`/`CONVERT` to a string type**, as the
      expression-side companion to the Tier 1 under-length item: an unsized
      `CONVERT(varchar, …)` silently means 30 characters, which truncates
      quietly at exactly the sizes real identifiers and dates land on. Only
      worth doing after the Tier 1 declaration rule lands, since it shares that
      rule's comparison and reporting path.

### Hint and index-shape catalog checks
Folded in from the incumbent-catalog read (`detection-reference.md` Appendix
7) — both need our catalog and neither is done properly anywhere surveyed.

- [ ] **Hint validity against the catalog** — every surveyed tool flags
      `INDEX`/`JOIN`/`TABLE`/`QUERY` hints as a *style* smell ("avoid hints").
      The catalog-requiring version nobody does: an `INDEX(...)` hint naming an
      index that **no longer exists**, or pinning an index the predicate cannot
      seek anyway, so the hint forces a scan of the wrong index.
- [ ] **Composite index leading-column violation** — predicate filters a
      non-leading key column while the leading column is unconstrained. One
      surveyed tool has this as a regex; against real index key ordering it
      becomes precise. Scope it as a *predicate* finding ("this query cannot
      seek this index"), never as an index recommendation, or it drifts into
      the index-advisor skip.

---

## Research gates before publication (not detections)
Two items from the wider-landscape/incumbent-catalog reads that are measurement
tasks, not rule candidates — they don't belong in a detection tier, but need
doing before the study can make certain public claims.

- [ ] **Pre-publication gate: measure the second type-binding incumbent's
      conversion rule against our direction fixtures.** The commercial
      schema-bound analyzer previously recorded as dead is in fact alive, and
      its cross-type-operator rule is genuinely connection-bound. Its docs
      read as symmetric, but that's an unverified negative (vendor site
      defeats fetching). Trial-install it and run the same three-case demo
      used against `SRP0016`; the study cannot claim "nothing is
      direction-aware" in public until this is measured.
- [ ] **Follow-up gate, same shape as the pre-publication gate above: two new
      tools found need a closer look before being ruled out.** One
      (Rust, WASM-delivered, ~103 T-SQL rules, actually issues `SET PARSEONLY`
      and rollback-plan probes — the closest oracle discipline to ours found
      anywhere; actively maintained) and one distributed via
      NuGet (169 rules across security/correctness/performance/convention,
      actively published). Docs for both show no implicit-conversion hit on a
      grep, but that's from docs, not a source read — same caveat class as the
      pre-publication gate item. Lower priority than that one since neither
      claims schema-binding, but cheap to close: read their rule source before
      the study cites "nothing else exists" as settled.

---

## Tier 3 — deliberate skips (decided; don't re-litigate without new evidence)

- **Parameter sniffing** — runtime data-distribution problem; static tools can
  only flag risk factors → hedged findings violate the precision bar.
- **SELECT \*, SET NOCOUNT, sp_ prefix, schema-prefix, ORDER BY ordinal,
  style/correctness linting** — crowded syntax-only territory several linters
  already cover; diluting into a generic linter destroys the tool's identity.
  (Missing schema prefix and unparameterized ad-hoc SQL are the only ones
  with real perf teeth, and both are plan-cache problems, not plan-shape
  problems.)
- **Missing/duplicate/unused indexes, heaps, fill factor, clustering-key
  width** — index-advisor space; catalog-only, no query analysis, different
  tool.
- **Runtime-only signals** — spills, memory grants, execution frequency,
  compile time, stale stats, plan-cache duplication, row-estimate mismatch:
  by definition not static. The oracle stays compile-only.
- **NOLOCK / READ UNCOMMITTED** — correctness smell wearing a performance
  costume; only 17 modules locally; linters cover it.
- **MERGE pitfalls** — real but correctness-focused and version-dependent;
  19 modules locally. Revisit only if a precise perf-framed subrule emerges
  (`WHEN MATCHED THEN DELETE`, missing HOLDLOCK).
- **CHECK-constraint-as-enum dead predicate** (from the wider-product-landscape
  read: one small PoC-labeled T-SQL type-checker treats `CHECK (col IN (...))`
  as an implicit enum and flags a predicate comparing the column against a
  literal proven outside it) — *decided, folding the checklist-numbering
  item's open question.* This is fundamentally a correctness finding (an
  always-false predicate), the same family as the incumbents' "comparison
  always evaluates to TRUE/FALSE" rule, which they themselves file under
  Design, not Performance. The one sibling correctness-adjacent finding kept
  on this backlog — NOT IN over a nullable subquery column, above — was kept
  specifically because it has a real perf angle beyond the correctness bug
  (an expensive null-aware anti-semi-join); this candidate has no equivalent
  extra angle, so it gets the same treatment as MERGE pitfalls just above:
  skipped unless a genuinely perf-framed variant turns up.
- **DISTINCT-masking-bad-join, correlated-subquery-won't-unnest, row goals,
  UNION vs UNION ALL** — harm depends on optimizer decisions we'd be
  guessing; low-precision by nature. Inventory-grade at best.
- **Indexed view NOEXPAND matching** — edition-dependent and matching-logic
  FP risk; revisit if the corpus shows indexed views at all.
- **OR across different columns** — detection trivial, harm imprecise
  (index union often fine). Only viable as an index-aware variant; parked.
- **Partition elimination defeat** (non-literal/wrapped predicate on the
  partitioning column) — real and distinct from b-tree seek/scan, but needs
  partition function/scheme catalog modeling we don't have yet. Revisit if a
  corpus repo turns out to use partitioning.
- **Always Encrypted column-comparison restrictions** — high precision in
  principle (catalog exposes `encryption_type`), but needs a target using the
  feature to matter at all; the local production copy and pilot corpus don't.
  Revisit if that changes.
- **Batch Mode on Rowstore eligibility loss** — deterministic in principle,
  but Microsoft doesn't publish a fully canonical disqualifier list, so
  completeness (and therefore precision) is a real risk; parked until a
  trustworthy exhaustive list exists.
- **Window-function POC (Partition-Order-Covering) index shape** — real
  (missing index keyed PARTITION BY → ORDER BY → covering columns forces a
  Sort per partition), catalog + syntax detectable, but scoped as an
  index-advisor recommendation rather than a query-defect finding; revisit if
  the tool's scope ever grows to include index suggestions.
- **The maintainability and correctness bulk of the paid-tier T-SQL analyzer
  read at source on 2026-08-16** — roughly sixty of its ~83 rules, decided as a
  block rather than one at a time because they share one disposition and one
  reason. Grouped by theme, with the reason each group is out:
  * *Size and complexity metrics* (line length, file length, routine length,
    parameter count, nesting depth, expression-operator count, branch count,
    branch-body length) — configurable thresholds over the AST, no catalog, no
    plan consequence. Generic-linter territory by definition.
  * *Formatting and layout* (tab characters, one statement per line, one
    declaration per line, misleading indentation, a branch keyword sharing a
    line with the end of the previous block, missing `BEGIN…END` around a
    conditional body, useless parentheses, empty statements, file header
    comments) — style only.
  * *Naming and identifiers* (routine name patterns, variable name patterns,
    a reserved keyword used as an identifier, database/schema qualification on
    a `CREATE`) — style only.
  * *Dead and duplicated code* (unreachable code, unused labels, unused local
    variables, unused parameters, redundant jumps, commented-out code,
    duplicated string literals, a loop that can only run once, self-assignment,
    identical operands either side of an operator, duplicated conditions,
    identical branch bodies, a conditional whose branches are all the same,
    redundant conditions, mutually exclusive conditions, collapsible nested
    conditionals, nested conditional-expression functions, a repeated unary
    operator, a negated comparison written as the negation of its opposite) —
    correctness-and-tidiness, none of it plan-shape. Note that the
    always-true/always-false predicate family here is the same one already
    decided against under the enum-style `CHECK`-constraint entry above; that
    reasoning covers these too and does not need redoing.
  * *Task-comment tracking* (`TODO`, `FIXME`) — process tooling, not analysis.
  * *Non-ANSI and deprecated spellings* (`!=`/`!<`/`!>`, `= NULL` in place of
    `IS NULL`, a `LIKE` pattern containing no wildcard, legacy system
    compatibility views, table hints written without `WITH`, index hints with
    a two-part name, numbered procedures, string literals used as column
    aliases, unparenthesised error-raising, and an assortment of removed system
    procedures) — a deprecation list, mechanically derivable from vendor
    documentation, with no query-level consequence. The one member of this
    family with real plan teeth, the old non-ANSI outer-join operators, is
    already queued in Tier 2 on its own merits.
  * *Statement-shape advice* (`SELECT *`, `INSERT` without a column list,
    ordinal `ORDER BY`, `TOP` without `ORDER BY`, a table with no primary key,
    `UPDATE`/`DELETE` with no `WHERE`, an existence check over an unfiltered
    `SELECT`, more than N tables written in a join, requiring a named session
    setting at the top of every routine, requiring an explicit constraint-check
    mode) — already covered by the `SELECT *`/`SET NOCOUNT`/schema-prefix skip
    at the top of this list, and the two members that do have a real angle when
    resolved rather than counted (join width after view expansion; `SELECT *`
    confined to a view) are queued in Tier 2 as their own, differently-scoped
    items.
  * *Cursor and control-flow correctness* (a fetch selecting a different column
    count than its cursor declares, an output parameter never assigned, an
    empty catch block, output emitted from a trigger, dirty-read isolation
    hints, duplicated arguments in a call, a legacy identity intrinsic where
    the scope-limited one was meant) — correctness findings wearing no
    performance costume at all. Dirty-read hints are separately skipped above;
    trigger output is already reachable through the queued trigger content
    scan.
  * *Security* (dynamic code execution, hard-coded credentials, hard-coded IP
    addresses, weak hash algorithms in general and in sensitive contexts) —
    not skipped, deferred: this is the same security-axis question already
    filed under Open scope questions below, and this read is one more data
    point for it (a performance-oriented commercial T-SQL analyzer devotes
    about one rule in sixteen to security) rather than a new decision.
  The load-bearing result of the read is not any single rule: it is that the
  richest paid T-SQL rule set found still contains **no implicit-conversion
  rule of any kind, no collation-aware rule, no lineage-aware rule, and no
  plan oracle** — the same gap every other surveyed catalog has. That is a
  citable negative for the study, recorded in `detection-reference.md`.
- **Query/order hint usage counters** (`sys.dm_exec_query_optimizer_info`
  join/order hint frequency) — inherently a runtime aggregate (counts since
  last restart), not a per-query static fact; the static form is already
  covered by the hard-coded-hints skip above.
- **The mainstream CI-analysis platform's T-SQL coverage** — *resolved, no
  longer open, and now measured on both tiers.* The free tier is a
  community-maintained analyzer, read at source: 16 enabled T-SQL rules, all
  declarative parse-tree shape matches, dormant since 2024, no
  implicit-conversion rule of any kind, and its one non-sargability rule is
  marked beta with no ground truth. The paid tier is a separate, far larger
  analyzer with a real hand-written T-SQL grammar — read at source on
  2026-08-16, ~83 rules — and it too has no conversion, collation, or
  lineage-aware rule and no plan oracle; its disposition is the block entry
  above. So the CI-gate niche is unoccupied at both price points, which is a
  stronger claim than the one this entry originally recorded. Details in
  `detection-reference.md` → Appendix 7.

---

## Open scope questions
Genuinely undecided, unlike Tier 3 above — these change what *kind* of tool
this is rather than just adding a rule, so they're kept separate from both the
detection tiers and the deliberate-skip list rather than defaulted either way.

- **Security, compliance, and correctness-only rules as a whole new axis.**
  The incumbent catalogs carry a substantial security axis (one tool devotes
  61 rules to it — its second-largest category — and the actively-developed
  DacFx pack ships a SQL-injection rule). Adding that axis would mean this
  tool detects a bug class CLAUDE.md's own identity statement doesn't mention
  at all (type-aware, direction-aware, lineage-aware *performance* analysis) —
  a scope call for whoever owns that identity, not something a checklist
  reorg should decide by default. The full incumbent security rule lists are
  in `detection-reference.md` Appendix 7 §7.4 so the decision can be made from
  the actual rules rather than a category label. Note the one already-admitted
  overlap: `EXEC(string)` where `sp_executesql` was possible (Tier 2's
  "Dynamic SQL quality" section) is admitted purely on plan-cache grounds,
  with the injection surface as a side effect, not as security scope creep.

## Reporting ideas worth stealing (not detections)
Folded in from the wider-product-landscape read — cross-cutting reporting/UX
ideas, not rule candidates, so they don't belong in a detection tier.

- [ ] **Confidence tiers as a first-class output axis** — one surveyed tool
      gates findings Proven/Contextual/Advisory with a CI-safe default. Maps
      onto our `Verdict` + `Unknown` split and the "static-only findings go in
      an appendix" rule; would make the SARIF export a safer CI gate.
- [ ] **Source-context classification** (migration/deployment script vs
      hot-path module) used to filter before reporting — a one-off deployment
      script legitimately does things a proc must not.
- [ ] **Machine-readable rule catalog generated from the rule types**, carrying
      id/severity/rationale/examples/fix-guidance, feeding both docs and the
      SARIF `rules` block — keeps documentation and code from drifting apart.

## Engineering debt (not detections)
Codebase-structure work, not rule candidates. Assessed 2026-08-16; do the
pieces opportunistically, ideally next time the touched code is worked on
anyway — the existing fire/near-miss/oracle fixtures are the safety net that
makes each move verifiable.

- [ ] **Separate rule decisions from ScriptDom traversal mechanics.** The
      verdict layer is already clean (`VerdictClassifier`, `TypePairMatrix`,
      `WriteLossClassifier`, `ExpressionTypeInferencer` are pure); the tangle
      is duplicated traversal mechanics plus secondary rules decided inline in
      visitors. Template for the target shape:
      `ScalarUdfInlineabilityClassifier` (pure facts → decision function,
      shared by two scanners). Pieces, in dependency order:
  - [ ] **`ScopeTracker`/`ScopedSqlVisitorBase`** — extract the scope/CTE
        stack, proc/trigger body scoping, and the nine `CreateOrAlter*`
        overrides that `NonSargablePredicateScanner` copies verbatim from
        `TypedPredicateExtractor` (its own comments say "Mirrors
        TypedPredicateExtractor's identical …" five times). Highest value:
        kills ~250 duplicated lines and the two-copies-drift hazard.
  - [ ] **`ResolvedColumnFacts` + one resolver** — one lineage/catalog query
        returning type/indexed/nullable/collation/depth/origin;
        `NonSargablePredicateScanner` currently re-resolves the same column
        up to three times per finding for type, then nullability, then
        collation.
  - [ ] **Pure per-rule classifiers** for decisions currently inline in
        visitors: `NonSargablePredicateScanner`'s case-fold/date-function
        sets, ISNULL-on-NOT-NULL suppression, CHARINDEX/LEFT rewrite (incl.
        remediation prose), temporal-boundary digit counting;
        `TypedPredicateExtractor`'s `TryAddOversizedParameterFinding` and
        `TryRecordCollationConflict`; `TvfFenceScanner`/`ScalarUdfScanner`
        kind decisions. Also: `CrossTableTypeDriftScanner` hand-rolls the
        collation-mismatch test its own doc comment cites
        `VerdictClassifier` for — route it through the real one. Best done
        one scanner at a time when that rule is next touched.
  - [ ] **`SourceSpan` + shared finding emitter** — replaces hand-threaded
        `(sourcePath, StartLine, StartColumn)` triples, the identical
        `OrderBy` tails in four catalog scanners, and the copy-pasted
        nested depth/origin emission in `TvfFenceScanner`/`ScalarUdfScanner`;
        the natural place for `Confidence` to become a real rule output
        (today it's a record default no scanner sets from evidence).
  - [ ] **`IRelocatableFinding`** — normalize the position field name across
        finding records (`Column` vs `ColumnPosition` is the blocker), then
        collapse `DynamicSqlPipeline`'s fourteen near-identical remap methods
        into one generic.
  - [ ] Shared `TableReferenceFlattener` and generic `CollectorVisitor<T>`
        (both copy-pasted per scanner today).
  - Deliberately NOT in scope: a generic self-registering rule engine
    (scanners genuinely differ in traversal shape — region stacks, polarity
    tracking, claim sets — and one framework would re-tangle them on a
    different axis); dismantling `TypedPredicateExtractor` (its verdict path
    is clean — it only needs the scope harness pulled out and its two inline
    secondary rules moved); touching `MaxTypedColumnScanner`/
    `ColumnCollationDriftScanner` beyond the shared emitter (one-line rules;
    extraction is ceremony).

---

## Cross-cutting requirements for every new stream

- Verdict-bearing rules ship an oracle fixture (compile-only SHOWPLAN_XML);
  syntactic-only rules ship fire + near-miss fixtures from real,
  internet-sourced bugs (no invented repros).
- Findings carry the same schema as conversions: verdict, indexed?, depth,
  origin (predicate site + introducing layer), machine-readable reasons.
- Every rule states its engine-version sensitivity (2017 interleaved
  execution, 2019 UDF inlining, 2022 CE behavior) rather than assuming one.
- Deterministic ordering; `Unknown` over guesses; unanalyzable counts
  reported honestly.
- The study angle for each shipped stream: prevalence at lineage depth ≥ 1
  ("inherited through views/TVFs") is the number nobody else can produce.
