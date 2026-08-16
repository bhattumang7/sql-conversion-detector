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

### Under-length and length-defaulted string declarations — shipped
The third leg of the parameter-sizing stream: the shipped pair covers *too
wide* (`MAX`-typed, and declared longer than the column). This closes *too
narrow*, which is the strictly worse failure — it doesn't just widen an
estimate, it silently truncates the compared value so the predicate matches
the wrong rows or none at all. Found on an incumbent read where it exists
only as a bare "declaration has no length" syntax check with no column
awareness at all; resolved against the catalog it becomes a real finding,
reusing the oversized-parameter rule's own comparison/reporting path almost
exactly, mirrored for direction.

- [x] **`varchar`/`nvarchar`/`char`/`nchar`/`binary`/`varbinary` declared
      with no length at all.** Defaults measured directly (`SqlTypeReferenceResolver`/
      `LiveTypeMapper` both already resolve a length-less declaration to
      `SqlType.Length: null`, never guessed at 1 - this stream is the first
      consumer to interpret that `null` as "T-SQL will default this to length
      1 at runtime" rather than "unresolved"): `IsImplicitDefault` on <see
      cref="UnderLengthParameterFinding"/> is true exactly when the OTHER
      operand's `Length` is `null` (and it isn't MAX-typed) - a bare
      `DECLARE @p VARCHAR = 'ABCDEF';` truncates to `'A'`, oracle-confirmed
      directly (real seeded row, real query execution:
      `UnderLengthParameterOracleTests.VariableWithNoExplicitLength_DefaultsToOneAndTruncatesJustAsSeverely`).
      `CAST`/`CONVERT`'s own different length-30 default is a distinct shape
      (an inline expression, not a declaration) and is NOT covered by this
      stream - out of scope, not silently missed.
- [x] **Declared shorter than the compared column** (`varchar(10)` variable
      or parameter vs a `varchar(100)` column) — the exact mirror of the
      shipped "declared longer than the compared column" rule
      (`TryAddOversizedParameterFinding`), reusing its comparison/reporting
      path directly: `TryAddUnderLengthParameterFinding` in
      `TypedPredicateExtractor.cs`, same literal/MAX/category-mismatch
      exclusions. **Deliberately NOT verdict-bearing**, same reasoning as the
      oversized sibling: this pass never traces the variable's actual
      assigned VALUE (CLAUDE.md "soundness first"), so it cannot claim
      truncation DID happen for a specific query, only that the declared-
      length pairing risks it - the same honesty `WriteLossFinding` already
      applies to assignment-site truncation. Two consequences, both reported
      via `ChangesRangeOrPatternShape` (derived structurally from the
      predicate's own operator, never guessed - true for `LIKE`/`<`/`<=`/
      `>`/`>=`, false for `=`):
      * **Silent truncation of the compared value** (`ChangesRangeOrPatternShape:
        false`, equality/inequality) — no error, no warning. Oracle-confirmed
        with a real seeded row and real query execution
        (`UnderLengthParameterOracleTests.ShorterVariableAssignedALongerLiteral_SilentlyTruncatesAndExcludesTheRealMatch`):
        a `varchar(20)` column holding `'ABCDEF'`, compared via `Code = @p`
        against a `varchar(3)` variable assigned `'ABCDEF'`, matches ZERO
        rows (the variable becomes `'ABC'` at assignment) where the same
        comparison at full length matches one.
      * **A `LIKE` pattern (or range bound) whose meaning changes, not just
        its match count** (`ChangesRangeOrPatternShape: true`) — the sharper
        case: oracle-confirmed
        (`ShorterVariableAssignedALikePattern_LosesTheWildcardAndChangesWhatMatches`)
        that `'ABC%'` (4 chars) assigned to a `varchar(3)` variable becomes
        `'ABC'` with the wildcard silently dropped, converting a prefix match
        into an exact-equality match — a genuinely different question, not
        just a narrower answer to the same one.
- [x] **Precision guards (mandatory):** never fires on a literal (a literal's
      length is its actual content, not a declared one - matches the
      oversized sibling's identical guard); never fires on a MAX-typed other
      operand (that's item #1's own separate `MaxTypedColumnFinding`, not
      this rule - a declared length of "MAX" must never misread as "shorter"
      the way a raw `-1` sentinel would); never fires across a category
      mismatch (the implicit-conversion stream's own, already-covered
      concern); `sysname` and other aliases resolve to their underlying type
      first via the same `SqlTypeReferenceResolver`/`catalog.TypeAliases`
      path every other typed rule in this codebase already uses, not a
      separate resolution step.
- [x] Oracle: **not verdict-bearing (no `SET SHOWPLAN_XML` plan claim), but
      real execution-based oracle confirmation of the general mechanism** -
      `UnderLengthParameterOracleTests` (3 tests: equality truncation, LIKE
      wildcard loss, implicit-default truncation), the same self-authored-
      probe-row-plus-real-execution discipline `WriteLossOracleTests`/
      `TemporalBoundaryPrecisionOracleTests` already use for this exact class
      of "runtime DML/query-result behavior, not a query-plan one" claim -
      this is a general confirmation of the rule's own premise, not a
      per-finding proof (mirrors how `CaseFoldColumnPipelineTests`/
      `DateFunctionColumnPipelineTests` confirm a Tier-1 rule's general
      mechanism once, not per finding). Structural unit tests
      (`TypedPredicateExtractorTests`, 9 cases: shorter declared variable/
      procedure parameter, implicit-default, LIKE/range operator detection,
      and every precision-guard negative). Real coverage against the local
      RM_ test database: **76 findings** (11 implicit-default, 12 changing
      LIKE/range shape).

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

### SET options that silently disable plan features — shipped (5 of the original 6 plan-feature kinds - ARITHABORT oracle-falsified and dropped - plus the independent ANSI_PADDING comparison-seed finding)
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
      `QuotedIdentifierOffBlocksIndexedFeature`.
- [x] **Complete the required-option set — shipped.** The three remaining
      plan-feature candidates (`ANSI_NULLS`/`ANSI_WARNINGS`/
      `CONCAT_NULL_YIELDS_NULL`) were oracle-probed directly (real seeded
      filtered index, real `SHOWPLAN_XML`) rather than assumed to behave like
      QUOTED_IDENTIFIER/NUMERIC_ROUNDABORT or like the falsified ARITHABORT —
      **all three demonstrably degrade the same filtered-index seek to a
      table scan** (`PhysicalOp="Table Scan"`, matching QUOTED_IDENTIFIER/
      NUMERIC_ROUNDABORT exactly, not ARITHABORT's no-op). `ANSI_NULLS` is
      the catalog half (`sys.sql_modules.uses_ansi_nulls`, confirmed the only
      one of the three with a baked-in module column — read live via
      `LiveModuleReader`/`LiveModule.UsesAnsiNulls`, registered on
      `DatabaseCatalog` via `AddModuleUsesAnsiNulls`/
      `TryGetModuleUsesAnsiNulls`, same shape as `QuotedIdentifier`);
      `ANSI_WARNINGS`/`CONCAT_NULL_YIELDS_NULL` are syntax-scan-only
      (`SetOptions.AnsiWarnings`/`SetOptions.ConcatNullYieldsNull` flag bits,
      same `PredicateSetStatement` visitor `NUMERIC_ROUNDABORT` already
      uses — generalized into one data-driven trigger table
      (`SetOptionScanner.SyntaxOnlyTriggers`) instead of one hand-written
      branch per option). `ModuleReachableObjectWalker` reused verbatim for
      the guard, unchanged. Oracle-tested (`SetOptionOracleTests`, 3 new
      cases, one per option, same real-seeded-filtered-index mechanism as
      QUOTED_IDENTIFIER/NUMERIC_ROUNDABORT). Unit-tested (`SetOptionScannerTests`,
      7 new cases: fires/never-fires per option, catalog-flag-unknown never
      guesses for ANSI_NULLS, and a same-`IsOn`-comma-list statement firing
      two distinct kinds from one `PredicateSetStatement` node). Real
      coverage against the local RM_ test database: still 99 findings, all
      `QuotedIdentifierOffBlocksIndexedFeature` — the three new kinds are
      coverage-empty on this particular database (it doesn't compile under
      `ANSI_NULLS OFF` or explicitly `SET` the other two), but the rules are
      real, oracle-confirmed, and shipped, exercised cleanly end-to-end
      against the whole 4,987-module corpus with no errors.
- [x] **`ANSI_PADDING OFF` as a second, independent finding — shipped, scope
      narrowed from the original framing once oracle-checked directly.** With
      the option off, trailing blanks are stripped on INSERT into
      `varchar`/`varbinary` columns, catalog-visible per column
      (`sys.columns.is_ansi_padded`, now on `CatalogColumn.IsAnsiPadded`,
      read live in `LiveCatalogReader.ReadColumnsAsync`; defaults to `true`
      for every caller that doesn't know/care, including file mode, which
      has no session-history to read this from at all). **Oracle-probed
      directly before implementing (real seeded rows, real query execution) —
      the original "equality/LIKE comparisons ... change meaning" framing was
      too broad:** plain `=` is NOT affected regardless of padding or
      trailing whitespace on either side — T-SQL trims trailing spaces for
      equality comparisons either way, confirmed both cross-column
      (non-padded vs. padded) and column-vs-trailing-whitespace-literal, both
      still matching identically. Only `LIKE`, where a pattern's own trailing
      whitespace is semantically significant and never trimmed, shows a real
      difference: `LIKE 'abc '` matched a padded column storing `'abc   '`
      but not a non-padded column storing the identical value as stripped
      `'abc'`. **Shipped scope: `LIKE` against a literal pattern with
      significant trailing whitespace only** — narrower than "column vs
      column, or column vs literal," since the column-vs-column and equality
      shapes were investigated and found not to reproduce (CLAUDE.md
      "precision beats recall everywhere"). `AnsiPaddingMismatchFinding`/
      `TryAddAnsiPaddingMismatchFinding` in `TypedPredicateExtractor.cs`.
      **Known, deliberate scope limit:** only the literal's own FINAL
      character is checked - a pattern with significant whitespace
      immediately before a trailing wildcard (`'abc %'`, also unmatchable by
      a non-padded column) is not caught, since catching it would need
      wildcard-aware pattern parsing this stream doesn't attempt; left
      honestly uncaught rather than guessed at.
      Reported at `LevelError` (stronger than every other structural report
      in this session's other parameter-sizing findings): unlike those, this
      is not a conditional risk dependent on an unknown runtime value - a
      non-padded column can never store a value ending in whitespace at all,
      so the predicate is PROVABLY always false, the same certainty tier the
      already-shipped `TemporalBoundaryPrecisionFinding` gets. **This is a
      data-semantics finding, not a plan-shape one** — it changes which rows
      match, not how they are found. No verdict; needs no compile-only
      `SET SHOWPLAN_XML` oracle, but a real execution-based oracle DID
      confirm the general mechanism directly (`AnsiPaddingMismatchOracleTests`,
      3 tests: trailing-blank stripping at INSERT, the LIKE non-match,
      and the equality-is-unaffected falsification that scoped this rule),
      the same self-authored-probe-row-plus-real-execution discipline
      `WriteLossOracleTests`/`TemporalBoundaryPrecisionOracleTests`/
      `UnderLengthParameterOracleTests` already use for this class of
      runtime, not query-plan, claim. Unit-tested (`TypedPredicateExtractorTests`,
      6 cases: fires on trailing whitespace, never fires on a padded column,
      never fires with no trailing whitespace, never fires on `=`, never
      fires against a non-literal pattern, and the known wildcard-adjacent
      gap documented as a passing negative rather than a silent omission).
      Wired end-to-end (`ScanReport` schema version 14 → 15, SARIF, readable
      report). Real coverage against the local RM_ test database: **0
      findings** — the database has zero non-padded `varchar`/`varbinary`
      columns at all (`sys.columns.is_ansi_padded = 0` count is 0), a
      legacy pattern this particular schema never used; the rule is real and
      shipped, just not exercised by this database (matching the same
      "coverage-empty, not broken" honesty the `CHARINDEX`/`LEFT` rule
      documented for itself earlier in this file).

---

## Tier 2 — strong candidates (precise rules exist, new machinery needed)

### Catch-all / kitchen-sink predicates
425 modules in the production copy (`... OR @param IS NULL`).

- [x] `(col = @p OR @p IS NULL)` and its swapped-order/chained variants —
      requires the compared column to be a bare `ColumnReferenceExpression`
      (no stacking onto the already-shipped function-wrapped-column Tier-1
      finding) and both OR-leaves to reference the exact same *formal
      parameter* (never a `DECLARE`'d local — that's the separate
      Local-variable-predicates item below). Own standalone AST-walking
      scanner (`CatchAllPredicateScanner`), the same "spans multiple sibling
      AST nodes" reasoning `PartialCompositeForeignKeyJoinScanner` already
      documents for not folding into `TypedPredicateExtractor`'s
      one-comparison-at-a-time walk. A new `FlattenOr`/`FlattenOrLeaves` pair
      isolates each independent OR-group under a chain of ANDs so
      `(A OR B) AND (C OR D)` never cross-pairs `A`/`D`. Reported even when
      the base column is unindexed (ranked last, weaker wording — matches
      the `Tier1Findings` convention of never suppressing the unindexed
      case), `Confidence.High` (the shape is unambiguous once matched).
      COALESCE/ISNULL-disabled variants of the same idiom are a known,
      explicitly out-of-v1-scope gap (only the direct `OR ... IS NULL` shape
      is matched) — not silently missed.
- [x] **Precision guard (mandatory):** `OPTION(RECOMPILE)` on the statement
      (`StatementWithCtesAndXmlNamespaces.OptimizerHints`) or `WITH
      RECOMPILE` on the enclosing procedure (`ProcedureStatementBody.Options`
      — confirmed via reflection against the referenced ScriptDom assembly
      to live only there, never on the shared `ProcedureStatementBodyBase`,
      since functions/triggers can never carry it) fully **suppresses** the
      finding, not merely downgrades it — real execution-based oracle proof
      (below) confirms RECOMPILE genuinely restores the seek, so a
      downgraded-but-still-reported finding would be a false positive in
      spirit. MERGE's own `ON` clause is a known, explicitly out-of-v1-scope
      gap (RECOMPILE-guard tracking still applies to it; the catch-all match
      itself does not) — its scope resolution and raw shape differ enough
      from every other statement kind to need its own dedicated work.
- [ ] Sibling: parameter overwritten before use in a predicate
      (sniffing-defeat — straight-line dataflow we already have from
      dynamic-SQL tracing). Deliberately deferred, not attempted this pass —
      a distinct dataflow question from "does this shape exist," which is
      what this item's own scanner answers.

      **Oracle correction worth recording (load-bearing, not a footnote):**
      the compile-only `SET SHOWPLAN_XML ON` oracle every other Tier-1
      sargability rule in this codebase uses (`PlanXmlCapture`) **cannot
      observe `OPTION (RECOMPILE)`'s real effect at all** — probed directly,
      a compile-only plan for `(Region = @p OR @p IS NULL) OPTION
      (RECOMPILE)` still showed `Table Scan`, identical to the un-guarded
      shape, because `SHOWPLAN_XML` never reaches the execution-time moment
      RECOMPILE's real value-embedding happens; it always produces an
      *estimated* plan regardless of the hint. Confirmed the general claim
      is still true by switching to **real execution** (`SET STATISTICS XML
      ON` against a self-authored, seeded probe table in the disposable
      Docker instance — CLAUDE.md permits this exact case, never
      scanned-target code): the bare-equality probe seeks, the catch-all
      shape without RECOMPILE forces a Table Scan, and the identical catch-all
      shape *with* `OPTION (RECOMPILE)` restores the Index Seek.
      `CatchAllPredicateOracleTests` (3 tests, all passing) locks this in
      permanently — no reusable capture helper was built for this (unlike
      `PlanXmlCapture`'s compile-only pattern), since inline `SqlCommand`
      use, reading the `ShowPlanXML`-containing result set back off a real
      execution, was sufficient for the one call site that needs it so far.

      Unit-tested (`CatchAllPredicateScannerTests`, 14 cases covering
      canonical/swapped/chained order, unindexed columns, `DECLARE`'d
      locals never firing, mismatched variable names never firing, wrapped
      columns never firing, unrelated ORs never firing, plain equality
      never firing, De Morgan's-negated shape never firing, both RECOMPILE
      guards, UPDATE statements, and ad-hoc batches with no formal-parameter
      concept). Wired end-to-end (`ScanReport` schema version 15 → 16,
      SARIF, readable report). **Real coverage against the local RM_ test
      database: 12 findings across 6 modules** (`dbo.procFRAVL4`,
      `dbo.spFRDepotSelect`, `dbo.sp_helpdiagrams`, and three
      scalar-UDF/optional-filter helpers), 10 of 12 against indexed
      columns — cross-checked against a raw-text sweep of every module
      containing `IS NULL` for the tight `= @p ... OR @p IS NULL`/swapped
      shape, which independently found the same modules (plus system-shipped
      `sp_helpdiagrams`, correctly matched too — it genuinely uses the
      idiom).

### Local-variable predicates
- [x] `WHERE col <op> @v` where `@v` is `DECLARE`'d in the batch/procedure,
      not a formal parameter — the optimizer sees the value at compile time
      but treats it as opaque (no sniffed-histogram lookup), falling back to
      a generic density-vector estimate. Covers every comparison operator,
      not just `=` (`=`, `<`, `<=`, `>`, `>=`, `LIKE` all confirmed present
      in real RM_ findings — the premise is about the optimizer's own
      value-blindness, not the comparison shape). Distinguished from a
      formal parameter purely in the AST: `TypedPredicateExtractor` grew a
      parallel `_formalParameterNames` tracking set (mirroring the existing
      `_variables` set), populated by `RecordParameters` and by
      `externalVariables` (an `sp_executesql`-seeded parameter counts as a
      genuine formal parameter too, since it really is caller-supplied per
      execution) — `PredicateOperand.Value` grew trailing
      `VariableName`/`IsFormalParameter` fields to carry this through.
      Reuses the existing typed-predicate full-corpus pass rather than
      adding a new one (no new scanner needed — this is a property of an
      already-visited comparison, not a new AST shape to search for).
      Purely informational, `Confidence.Low`, never verdict-bearing — the
      finding's own doc comment states this explicitly, since it makes no
      magnitude claim, only "the optimizer is blind here." Same
      `OPTION(RECOMPILE)`/`WITH RECOMPILE` guard as the "Catch-all /
      kitchen-sink predicates" section above, and for the identical reason:
      **fully suppressed**, not downgraded, when active.

      **Oracle note (a genuine compile-time phenomenon, unlike the
      catch-all stream's RECOMPILE claim):** the divergence between a
      sniffed/literal value's cardinality estimate and a `DECLARE`'d local's
      estimate is baked in at *compile* time, not something that only
      reveals itself at execution — so the existing compile-only
      `PlanXmlCapture` (`SET SHOWPLAN_XML ON`) is the right tool here, and
      was used directly (no new execution-based mechanism needed for this
      one). `LocalVariablePredicateOracleTests` seeds a skewed column
      (1900/2000 rows sharing one value) and confirms a literal-value probe's
      `EstimateRows` reflects the real skew (~1900) while the identical
      value held in a `DECLARE`'d local produces a materially smaller
      estimate (less than half) — the estimator premise this finding relies
      on, confirmed directly rather than assumed.

      Unit-tested (`TypedPredicateExtractorTests`, 7 new cases: fires on a
      declared local, never fires on a formal parameter, fires across a
      range operator (not just `=`), never fires on a bare literal, never
      fires under either RECOMPILE guard, and an `sp_executesql`-seeded
      parameter is correctly treated as a formal parameter rather than a
      local). Wired end-to-end alongside the catch-all stream (same schema
      bump, SARIF, readable report). **Real coverage against the local RM_
      test database: 4,373 findings** across every comparison operator
      (2,886 `=`, 760 `>=`, 625 `<=`, 52 `<`, 49 `>`, 1 `LIKE`) — cross-checked
      against a raw-text sweep (1,833 modules contain both `DECLARE @` and
      `WHERE`, a loose but consistent upper bound the scanner's own
      depth-0-only, RECOMPILE-guard-respecting count sits well inside of).

### NOT IN over a nullable subquery column
346 modules locally use `NOT IN (SELECT ...)`.

- [x] Fires **only** when the catalog says the subquery column is nullable —
      `WHERE x NOT IN (SELECT y FROM t)` desugars to a chain of `<> ALL`
      comparisons; the instant the subquery produces one `NULL` row, ANDing
      UNKNOWN into that chain makes the whole predicate UNKNOWN, which
      `WHERE` treats as excluding every row, not just the one that would
      have matched the `NULL`. **A correctness bug, not a plan-shape one** —
      the query returns the wrong result set (often zero rows) whenever the
      underlying data hits the trap, independent of any index or plan
      choice. Own standalone AST-walking scanner
      (`NotInNullableSubqueryScanner`), the same "different traversal shape
      than a plain comparison" reasoning `CatchAllPredicateScanner`/
      `PartialCompositeForeignKeyJoinScanner` already document — `InPredicate`
      with a populated `Subquery` needs a SECOND, independent
      `FromScopeResolver.Resolve` call over the subquery's own `FromClause`,
      a kind of nested-scope resolution no other scanner in this codebase
      does. `TypedPredicateExtractor` already explicitly bails on `NOT IN`
      without looking at the subquery at all, so there is no overlap.
      Deliberately narrow, base-table-only, Depth-0-only on the subquery
      side (matching `CatchAllPredicateScanner`'s own scope): only a bare
      `ColumnReferenceExpression` projected as the subquery's sole SELECT
      element, resolving to a base table, is matched — a projected
      expression (`SELECT ISNULL(y, 0)`, which can never itself be `NULL`
      and would be a genuine false positive if guessed at), a multi-column/
      `SELECT *` subquery, a view/CTE-derived column, or a set-operator
      (`UNION`/`EXCEPT`/`INTERSECT`) subquery is left unanalyzed rather than
      guessed at — known v1 scope limits, not silently-missed cases. An
      `InPredicate` reachable only through an `OR` branch of the outer WHERE
      is also left unmatched (the OR could be masking an already-safe
      alternate path). **A subquery that already defends itself with a
      top-level `WHERE y IS NOT NULL` on the identical projected column
      never fires** — this is the single most common real-world fix for
      this exact bug (see fixture note below), and firing on already-fixed
      code would be a visible, avoidable false positive; a filter only
      reachable through an OR branch does not count, since it doesn't
      unconditionally exclude NULLs from every row the subquery could
      project. `Confidence.High`, SARIF `LevelError` — the same certainty
      tier as `AnsiPaddingMismatchFinding`/`TemporalBoundaryPrecisionFinding`,
      never downgraded by indexed-ness (no seek/scan angle to this finding
      at all). **Version-insensitive**: fundamental ANSI three-valued-logic
      semantics, not an optimizer behavior — unaffected by compat level or
      CE mode.

      Real, internet-sourced fire-shape reference: a SQLServerCentral forum
      thread, "Subquery Returns No Rows when there are NULLs and 'NOT IN' is
      used," reporting the exact `WHERE SID NOT IN (SELECT SID FROM
      dbo.BusRoute)` shape returning zero rows the instant `BusRoute.SID`
      has any `NULL` — its own suggested fix, adding `WHERE SID IS NOT
      NULL` to the subquery, is precisely the defensive-filter shape this
      rule detects and suppresses on, confirming that check is load-bearing
      rather than optional precision. Real execution-based oracle proof
      (`NotInNullableSubqueryOracleTests`, 3 tests, all passing — a genuine
      correctness/result-set-shape claim, so plan-XML is irrelevant here,
      matching how `AnsiPaddingMismatchOracleTests` already verifies this
      class of finding): a seeded nullable column's `NOT IN` returns zero
      rows despite intuitively-matching rows existing; the identical query
      against a `NOT NULL` column returns the expected anti-join rows; and
      the identical nullable column WITH a defensive `IS NOT NULL` filter
      also returns the expected anti-join rows, confirming the suppression
      is sound rather than a guess.

      Unit-tested (`NotInNullableSubqueryScannerTests`, 13 cases covering
      the nullable-fires/not-null-never-fires core pair, plain `IN` never
      firing, a literal value list never firing, the defensive-filter
      suppression (including the OR-reachable and different-column
      near-misses that must still fire), a projected expression never
      firing, a multi-column subquery never firing, `NOT EXISTS` never
      firing, an `InPredicate` inside an OR branch never firing, `UPDATE`
      statements, and an expression on the outer side still firing with a
      null `OuterColumnName`). Wired end-to-end (`ScanReport` schema
      version 16 → 17, SARIF, readable report). **Real coverage against the
      local RM_ test database: 5 findings** across 5 distinct modules — a
      raw-text sweep found 515 modules containing the loose `NOT IN
      (SELECT` shape, the overwhelming majority of which the catalog
      correctly rules safe (`NOT NULL` subquery column) or leaves
      unanalyzed under the deliberately narrow base-table/bare-column v1
      scope above, rather than the rule spraying across all of them the way
      a syntax-only linter would.

### UPDATE ... FROM without source uniqueness
- [x] Target joined to a source whose join columns carry no PK/unique
      constraint — nondeterministic multi-match update (each target row takes
      an arbitrary source row). SQL Server's documented behavior: it silently
      picks a value from ONE of the matching source rows, and which one is
      unspecified, plan-dependent, and not guaranteed stable even across
      repeated executions of the identical statement. **A structurally
      unsafe finding, not a "wrong for current data" one** — a meaningfully
      *stronger* claim than that distinction usually implies: unlike the
      NOT-IN-nullable stream, which needs today's data to already contain a
      NULL, this defect requires no data inspection at all — the statement
      has no schema guarantee against a future duplicate join-key value, so
      it can start returning a silently wrong answer the moment a single
      `INSERT` happens on the source, with zero code or schema change to
      the statement itself. The absence of the uniqueness guarantee is the
      full, provable defect. `Confidence.High`, SARIF `LevelError`, never
      downgraded by indexed-ness (no seek/scan angle to this finding at
      all). **Version-insensitive**: no compat level or CE mode makes this
      defined behavior — which specific row wins on a given execution is
      plan-dependent, and this finding makes no claim about that, only that
      the statement has no guarantee against it.

      Own standalone scanner (`NonUniqueUpdateSourceScanner`), reusing
      `FromScopeResolver.ResolveForDataModification` (the same UPDATE-scope
      resolution `TypedPredicateExtractor`/`NotInNullableSubqueryScanner`
      already use) and the same JOIN-tree-flattening/AND-only-flattening
      shape `PartialCompositeForeignKeyJoinScanner` already established.
      Only examines a JOIN where one side is unambiguously the UPDATE's own
      target, matched **by alias** rather than resolved base-table name — a
      self-join aliases the identical table twice, so a qualified-name-only
      comparison could not tell the target side's own column from the
      source side's; matching by the JOIN's own alias handles this for
      free. A join two hops from the target (target→A→B, with the `SET`
      clause reading from B) is a materially different claim this scanner
      does not make — a known v1 scope limit, not a silently-missed case.
      Composite-uniqueness-aware: the source's join columns are provably
      unique iff a `CatalogIndex` with `IsUnique == true` (not filtered, not
      disabled) has `KeyColumns` that are a subset of (or exactly equal to)
      the join's own equality columns on that side — a unique constraint
      over a strict **superset** of the join columns does NOT suppress the
      finding (`UNIQUE(TargetId, Cat)` does not make a join on `TargetId`
      alone safe), confirmed directly against the oracle and covered by its
      own precision-critical test case. No new catalog surface was needed —
      `CatalogIndex`/`LiveCatalogReader` already model every PK/unique
      constraint/unique index with ordered, composite-aware `KeyColumns`.
      Only fires when the `SET` clause actually reads a value from the
      unsafe source — a join to a non-unique source used only for filtering
      carries no observable risk. Deliberately base-table-only: a
      derived-table/aggregated source (`FROM (SELECT SourceId,
      MAX(Val)...GROUP BY SourceId) s`, which can be provably unique
      per-group without any catalog constraint) and a view/CTE-derived
      source are left unanalyzed rather than guessed at — known v1 scope
      limits. `MERGE`'s own `USING` source is out of scope by construction:
      the engine itself raises an error there, so there is nothing silent
      to detect.

      Real, internet-sourced fire-shape references: a Microsoft Q&A thread,
      "update statement: one target row, multiple source rows. What are the
      rules?", confirming directly that "which row is accessed first is up
      to the developer" (i.e. plan-dependent, not guaranteed); an Experts
      Exchange thread, "UPDATE Based on Join, nondeterministic example,"
      discussing the same shape and the MERGE-as-fix pattern; and
      Microsoft's own `UPDATE (Transact-SQL)` Remarks section, which
      documents the exact "an unspecified row from the multiple qualifying
      rows is used" warning — all corroborating, not merely trusted, since
      the mechanism was directly reproduced against the Docker oracle.
      **The MERGE contrast is directly confirmed, not assumed from
      documentation**: the identical non-unique-source shape run as a
      `MERGE ... WHEN MATCHED THEN UPDATE` genuinely raises SQL Server error
      8672 ("The MERGE statement attempted to UPDATE or DELETE the same row
      more than once...") rather than picking silently —
      `NonUniqueUpdateSourceOracleTests` asserts the exact error number and
      message substring, not just that *an* exception is thrown.

      Real execution-based oracle proof (`NonUniqueUpdateSourceOracleTests`,
      4 tests, all passing — a genuine nondeterminism/correctness claim, so
      plan-XML is irrelevant here): a non-unique source with 3 conflicting
      rows sharing the join value updates the target to one of the 3 source
      values (asserted as "one of," never "which one" — the whole point is
      it's unspecified); the identical shape against a source with a
      genuine unique index is deterministic every time; the composite-
      unique-superset case (`UNIQUE(TargetId, Cat)`, joined on `TargetId`
      alone) still multi-matches at the engine level, confirming the
      scanner's own precision-critical non-suppression is grounded in real
      behavior, not just a catalog-shape guess; and the MERGE contrast
      above.

      Unit-tested (`NonUniqueUpdateSourceScannerTests`, 11 cases covering
      the non-unique-fires/unique-index-never-fires core pair, the exact-
      composite-match near-miss, the composite-superset-still-fires
      precision case, the subset-of-composite-join-safe near-miss, the
      SET-clause-never-reads-from-source near-miss, no-FROM-clause simple
      UPDATE never firing, a self-join firing correctly (join on the
      non-PK column), a SET value that's an expression referencing the
      source still firing, a filtered unique index treated as not provably
      unique, and an indirect two-hop join never firing). Wired end-to-end
      (`ScanReport` schema version 17 → 18, SARIF, readable report). **Real
      coverage against the local RM_ test database: 2 findings** across 2
      modules — a raw-text sweep found 704 modules containing the loose
      `UPDATE...FROM...JOIN` shape, the overwhelming majority of which the
      catalog correctly rules safe (a genuine unique index/constraint on
      the join columns) or leaves unanalyzed under the deliberately narrow
      base-table/direct-join v1 scope above, rather than the rule spraying
      across all of them.

### Forced-serial construct inventory
Fully syntax-only (no catalog needed) — one scanner, one `Kind` enum, the
same "inventory" shape `SetOptionFinding`/`SetOptionFindingKind` already
established. A performance-cost finding, not a correctness one: forced-serial
execution never changes the result, only its cost — `Confidence.High`,
SARIF `LevelWarning` for all three kinds (the same "structural risk" tier
`CatchAllPredicateFinding`/`SetOptionFinding` use, not the `LevelError`
correctness tier `NotInNullableSubqueryFinding`/`NonUniqueUpdateSourceFinding`
get), never downgraded by indexed-ness (no seek/scan angle to any of these
three at all). **Version-insensitive**: all three are long-standing,
documented optimizer restrictions, unaffected by compat level or CE mode —
table-variable deferred compilation (SQL Server 2019/compat 150+) improves
only cardinality estimates for table variables and does NOT restore
parallelism, confirmed directly on this engine at its own default (and
latest) compat level.

- [x] Table-variable **modification** (INSERT/UPDATE/DELETE/MERGE target, or
      the INTO target of an OUTPUT clause) — that ONE containing statement's
      plan is forced serial, confirmed as
      `NonParallelPlanReason="TableVariableTransactionsDoNotSupportParallelNestedTransaction"`
      in a real executed plan. **Not the whole batch/procedure** — a
      correction to the checklist's own original "whole plan serial"
      phrasing: an unrelated statement later in the same batch that never
      touches the table variable stayed fully parallel in direct testing.
      A read-only reference to the same table variable does not fire
      (direction-style distinction, as originally scoped). OUTPUT INTO a
      table variable is the same mechanism as a direct DML target (verified,
      not assumed) — OUTPUT INTO a real table never fires.
      821 modules locally use table variables (matches the checklist's own
      original count exactly).
- [x] `FAST_FORWARD` cursor (or the equivalent bare `FORWARD_ONLY READ_ONLY`
      lacking an explicit `STATIC`/`KEYSET`/`DYNAMIC`) forces the cursor's
      own defining query plan serial, confirmed as
      `NonParallelPlanReason="NoParallelFastForwardCursor"`.
      **The checklist's own original premise was backwards** — it framed
      "cursor without `LOCAL FAST_FORWARD`" as the risk shape, but oracle
      probing showed the opposite: `FAST_FORWARD` itself is what defeats
      parallelism for the cursor's defining SELECT. `LOCAL FAST_FORWARD`
      remains the right advice for row-by-row fetch overhead — that claim
      is unaffected — it's specifically a different, separate cost this
      finding reports, one the same well-known advice doesn't warn about.
      `STATIC`/`KEYSET`/`DYNAMIC` cursors were oracle-checked directly and
      do NOT trigger this mechanism — deliberately never included, rather
      than assumed unsafe the way the checklist's original "dynamic/keyset
      cursors" framing did. 202 modules locally declare a cursor, 110 of
      which already say `FAST_FORWARD` explicitly — i.e. roughly half of
      all cursor-using modules use the exact option combination that, per
      this correction, is the one that forces serial execution.
- [x] The finite, oracle-confirmed serial-forcing intrinsics list:
      `OBJECT_ID`, `IDENT_CURRENT`, `ERROR_NUMBER`, `ERROR_MESSAGE`,
      `ERROR_LINE`, `ERROR_SEVERITY`, `ERROR_STATE`, `ERROR_PROCEDURE`,
      `@@TRANCOUNT` — referenced inside a query with a real FROM clause,
      confirmed as `NonParallelPlanReason="NonParallelizableIntrinsicFunction"`.
      Several commonly-cited "always serial" intrinsics
      (`@@ROWCOUNT`, `@@IDENTITY`, `@@ERROR`, `SCOPE_IDENTITY()`, `NEWID()`)
      were directly oracle-checked and do NOT trigger this — deliberately
      excluded rather than guessed into the list, the same precision
      discipline as everywhere else in this codebase. Additional
      catalog-metadata candidates (`OBJECTPROPERTY()`, `COL_LENGTH()`,
      `COL_NAME()`, `DATABASEPROPERTYEX()`) are plausible members of the
      same family but were not probed — deliberately left out rather than
      guessed in, a known v1 gap, not a claim they're safe.
- [ ] Serial-zone constructs as informational: TOP row goals, recursive
      CTEs, global scalar aggregates — deliberately deferred. MSTVF refs are
      already covered by the shipped MSTVF-as-fence stream. A recursive CTE
      was sanity-checked directly and shows no `NonParallelPlanReason`
      attribute at all (the optimizer never appears to consider a parallel
      plan for the recursive union in the first place) — a structurally
      weaker, harder-to-attribute signal than the three shipped kinds above,
      not pursued further this pass.

      Real internet-sourced references (verified against the oracle, never
      trusted blind): Adam Machanic's original documentation of the
      table-variable-forces-serial gotcha; Aaron Bertrand
      (sqlperformance.com, "Performance Surprises and Assumptions: DOP and
      Cursors") on the `FAST_FORWARD`-forces-serial mechanism, matching the
      `NoParallelFastForwardCursor` result exactly; Microsoft's own Query
      Processing Architecture guide's list of parallelism-restricting
      functions, treated as a starting point rather than ground truth since
      several commonly-repeated members of "the list" elsewhere online
      (`@@ROWCOUNT`, `@@IDENTITY`, `@@ERROR`) were oracle-disproven above.

      Real execution-based oracle proof (three test classes, all passing —
      a genuine plan-shape claim needing real execution, never compile-only
      `SHOWPLAN_XML`, the same correction the earlier catch-all-predicate
      stream's `OPTION (RECOMPILE)` finding needed):
      `TableVariableModificationOracleTests` (4 tests: INSERT-target and
      OUTPUT-INTO-table-variable both fire with the exact same reason
      string; a read-only reference and an OUTPUT INTO a real table both
      never carry it); `FastForwardCursorOracleTests` (5 tests: bare
      `FAST_FORWARD` and `FORWARD_ONLY READ_ONLY` both fire;
      `LOCAL STATIC FORWARD_ONLY READ_ONLY`, `DYNAMIC`, and no-options all
      never fire); `NonParallelizableIntrinsicOracleTests` (11 tests: all 9
      confirmed intrinsics/`@@TRANCOUNT` fire individually, `@@ROWCOUNT`
      and `SCOPE_IDENTITY()` both confirmed never to fire).

      Unit-tested (`ForcedSerialScannerTests`, 19 cases spanning all three
      kinds: table-variable INSERT/UPDATE/DELETE/OUTPUT-INTO targets firing,
      OUTPUT INTO a real table never firing, a read-only reference never
      firing, per-batch scoping (a re-declared `@t` in a later batch never
      inheriting the first batch's modification), `FAST_FORWARD`/bare
      `FORWARD_ONLY READ_ONLY` firing, `LOCAL STATIC FORWARD_ONLY READ_ONLY`/
      no-options/`DYNAMIC` never firing, a cursor-variable
      (`SET @c = CURSOR FAST_FORWARD FOR ...`) form firing, three confirmed
      intrinsics/`@@TRANCOUNT` firing only inside a query with a FROM
      clause, and the two confirmed-safe intrinsics never firing). Wired
      end-to-end (`ScanReport` schema version 18 → 19, SARIF, readable
      report). **Real coverage against the local RM_ test database: 4,218
      findings** (3,828 table-variable modifications, 233 non-parallelizable
      intrinsics, 157 `FAST_FORWARD` cursors) — cross-checked against raw-
      text sweeps: 821 modules use table variables (exact match to the
      checklist's own original count), 202 modules declare a cursor (110 of
      which already say `FAST_FORWARD`), 208 modules reference
      `@@TRANCOUNT`, 128 reference `OBJECT_ID(`.

### Lineage-metric findings (cheap adds on existing passes)
- [x] Nested-view depth report — we already compute topo order; emit depth ≥ N
      as a finding with the chain (57 views reference other views locally).
      Structural depth, not a claim a query through the view is currently
      slow — the risk is maintenance/robustness: a change to a base table
      now has to be traced through 2+ independent view layers before its
      blast radius is understood, and each layer is a place a `SELECT *`/
      column-list mismatch or silent type widening can hide. Threshold
      N = 2, chosen against real local depth distribution (depth 0/1/2/3 =
      80/37/17/3 of the 57 views that touch another view at all — depth 1
      is common and not itself notable; depth ≥ 2 is a small, real,
      selective signal). "View" means both `CREATE VIEW` and inline TVF
      uniformly, matching this codebase's own established "inline TVFs =
      views" treatment elsewhere. `Chain` reports top-down (the view
      itself first, then each further-nested view, ending just before the
      base tables) — the order a reader debugging "why is this wrong/slow"
      actually wants.

      New shared foundation (`Lineage.ViewExpansionMap`), reused by "Post-
      expansion join width" below rather than built twice — one memoized
      DFS over every `ViewDefinition` (the exact `TvfFenceMap`-shaped
      "walk once, memoize, reuse" template already established for the
      same kind of transitive view-dependency walk), computing depth,
      top-down chain, and the full set of transitively-reached base tables
      in a single pass. Catalog/lineage-only, unconditional — reported once
      per qualifying view regardless of whether any scanned query calls it
      (the same "reported once per object" precedent `MaxTypedColumnFinding`
      already establishes). `Confidence.High`, SARIF `LevelWarning`. No
      oracle needed: depth is a pure catalog/AST fact, not a plan-shape
      claim. Version-insensitive: pure DDL-dependency structure.

      Unit-tested (`NestedViewDepthScannerTests`, 5 cases: a view over a
      base table never fires, a view over one other view (depth 1) never
      fires, a view over a view over a view (depth 2) fires with the
      correct top-down chain and base-table set, a 4-deep chain fires for
      both qualifying views at their own correct depths, and a view fanning
      out to two base tables lists both). Wired end-to-end (`ScanReport`
      schema version 21 → 22, SARIF, readable report). **Real coverage
      against the local RM_ test database: 62 findings** (42 at depth 2, 14
      at depth 3, 6 at depth 4) — cross-checked directly against
      `sys.sql_expression_dependencies`: exactly 57 distinct views
      reference another view at all, matching the checklist's own original
      count exactly, of which the tool's own depth-2+ threshold correctly
      selects a smaller, real subset rather than reporting all 57.
- [x] Multi-referenced CTE — inline macro re-executed per reference; count
      references in the AST. Rarely covered anywhere; high precision.
      SQL Server does NOT materialize a plain (non-recursive) CTE once and
      reuse it — each reference re-runs the CTE's own defining query
      independently. **Load-bearing, not folklore-trusted**: confirmed
      directly against the Docker oracle (real execution, `SET STATISTICS IO
      ON`) rather than assumed from documentation — a base table's own scan
      count went from 1 (one CTE reference) to 2 (two references), matching
      exactly two independent scans, not one materialized-and-reused result.
      This same discipline mattered earlier this session: the FAST_FORWARD
      cursor finding found a piece of "everyone knows this" SQL Server
      folklore to be backwards once actually checked, so this claim got the
      same direct verification rather than being trusted on reputation.
      Standalone syntax-only scanner (`MultiReferencedCteScanner`), no
      catalog/lineage dependency at all — counts `NamedTableReference`
      occurrences matching a declared CTE name across the main query body
      and every OTHER CTE's own body, excluding a self-reference inside a
      recursive CTE's own defining query (T-SQL has no separate `RECURSIVE`
      keyword — a CTE that references its own name simply is recursive, and
      that reference is the structurally mandated recursion mechanism, not
      the optional re-invocation this rule targets). Fires at reference
      count ≥ 2. Deliberately scoped to `SELECT` statements in v1 — an
      `UPDATE`/`DELETE`/`MERGE` statement's own WITH-clause CTEs are a real
      but comparatively rare shape, left unanalyzed rather than guessed at.
      `Confidence.High`, SARIF `LevelWarning` (structural risk, matching
      `ForcedSerialFinding`/`CatchAllPredicateFinding`'s own tier — a real
      cost, not a correctness claim).

      Unit-tested (`MultiReferencedCteScannerTests`, 8 cases: two references
      in the main body fires, a single reference never fires, a later CTE
      referencing an earlier one twice fires, a recursive CTE's own self-
      reference never counts toward its own total, a recursive CTE
      genuinely referenced twice downstream still fires, two independent
      single-reference CTEs never fire, no WITH clause never fires, and a
      3-reference CTE reports all 3 reference lines). Real execution-based
      oracle proof (`MultiReferencedCteOracleTests`, 2 tests, both passing):
      one reference scans the base table once, two references scan it
      twice — the general mechanism confirmed once, not per finding. Wired
      end-to-end (`ScanReport` schema version 20 → 21, SARIF, readable
      report). **Real coverage against the local RM_ test database: 150
      findings** across many modules (reference counts ranging 2–14, most
      commonly 2–3) — a raw-text sweep found 1,906 modules containing the
      loose `WITH ... AS (SELECT` shape, the overwhelming majority of which
      either declare a CTE referenced only once (safe) or aren't a real CTE
      match at all, rather than the rule spraying across all of them.
- [x] Untrusted (WITH NOCHECK) FK/CHECK constraints — optimizer forfeits join
      elimination; pure catalog flag (`is_not_trusted`). `ForeignKeyRelationship`
      gained `IsNotTrusted`/`IsDisabled` fields, `LiveCatalogReader.ReadForeignKeysAsync`
      now selects `fk.is_not_trusted`/`fk.is_disabled`; a new `CatalogCheckConstraint`
      record + `DatabaseCatalog.CheckConstraints` + `LiveCatalogReader.ReadCheckConstraintsAsync`
      read `sys.check_constraints` (previously not modeled in the catalog at
      all — no `LiveCatalogReader`/DDL-parsed path had ever touched CHECK
      constraints). Catalog-only, unconditional (every untrusted, non-disabled
      constraint reported once, the same `MaxTypedColumnFinding` "once per
      object" precedent) — a disabled constraint is not reported, since it's
      openly off, not silently weaker than it looks. `Confidence.High`, SARIF
      `LevelWarning` (structural risk, not itself a proof of a wrong result —
      the same tier `ForcedSerialFinding`/`SetOptionFinding` use, not the
      `LevelError` correctness tier). No oracle needed: `is_not_trusted` is a
      documented, exact catalog fact, not a plan-shape claim.
      **Origin half, added from the incumbent read:** deliberately deferred,
      not attempted this pass. Checked directly: this tool's corpus-deploy
      pipeline (`ScriptDeployer`) discards the parsed DDL AST after
      deployment, with no existing constraint-name-to-(file, line)
      side-channel anywhere to attribute a specific re-enabling statement
      back to — real new cross-project plumbing (deploy-time AST capture
      threaded into report-construction time), not an incremental add to
      this stream. Also structurally corpus-only even if built: a `scan-db`
      target has no deployment script text at all to attribute back to.
      Documented as a known gap rather than half-built.
      Unit-tested (`UntrustedConstraintScannerTests`, 6 cases: untrusted FK
      fires, trusted FK never fires, untrusted-but-disabled FK never fires,
      a composite FK reported once not once per column pair, untrusted
      CHECK fires, trusted CHECK never fires) and oracle-tested against a
      real deployed schema (`LiveCatalogReaderTests`, 4 new cases confirming
      `is_not_trusted`/`is_disabled` read correctly off `sys.foreign_keys`/
      `sys.check_constraints`, plus the scanner firing against real catalog
      state). Wired end-to-end (`ScanReport` schema version 19 → 20, SARIF,
      readable report). **Real coverage against the local RM_ test
      database: 81 findings** (65 untrusted FKs, 16 untrusted CHECK
      constraints).
- [x] Cascading FK actions (ON DELETE/UPDATE CASCADE) — hidden multi-table
      work per DML; catalog-only, informational. `ForeignKeyRelationship`
      gained `DeleteAction`/`UpdateAction` fields (a `ReferentialAction`
      enum matching `sys.foreign_keys`' own documented integer codes),
      read in the same `ReadForeignKeysAsync` query change as the untrusted-
      constraint item above (same query, same row, no extra join). Reported
      unconditionally, not gated on any DML-correlation heuristic — matching
      `MaxTypedColumnFinding`'s own precedent, since gating on "only report
      where the scan can see application DML touching this table" would
      itself be an unsound guess (a table modified only through an
      unresolvable proc, or an ORM entirely outside this tool's hard scope,
      would silently vanish from a gated report). `Confidence.High` (the
      action is an exact catalog fact), but SARIF `LevelNote`, not Warning —
      purely informational, no magnitude claim (how many rows, how often
      depends on data this pass cannot see), the same no-magnitude-claim
      tier `LocalVariablePredicateFinding` uses for its own reason, though
      at higher confidence since (unlike that finding's cardinality-estimate
      risk) the structural fact itself is never in doubt.
      Unit-tested (`CascadingForeignKeyScannerTests`, 4 cases: DELETE
      CASCADE fires, UPDATE SET NULL fires, NO ACTION never fires, a
      composite FK reported once not once per column pair) and oracle-
      tested against a real deployed schema (`LiveCatalogReaderTests`, 2 new
      cases confirming `delete_referential_action`/`update_referential_action`
      read correctly off `sys.foreign_keys`, including a genuinely
      cascading-and-SET-NULL FK, plus the scanner firing against real
      catalog state). Wired end-to-end alongside the untrusted-constraint
      stream (same schema bump, SARIF, readable report). **Real coverage
      against the local RM_ test database: 115 findings** (101 `DELETE
      CASCADE` only, 8 `SET NULL`, 6 carrying both a cascading delete and a
      cascading update action).
- [x] **Post-expansion join width.** Every surveyed tool counts tables in the
      written `FROM`/`JOIN` list and warns past a threshold; that count is
      meaningless when half the sources are views. The number that matters is
      the *expanded* one — base tables after resolving views and inline TVFs
      through the lineage pass — because that is what the optimizer actually
      reorders, and past roughly a dozen joined relations it stops searching
      exhaustively and takes a greedy plan. We are the only tool that can
      compute it. Report the expanded count, the written count, and the chain
      that inflated it; rank by the gap between the two, since a query that
      *looks* like a three-table join and expands to twenty is the finding
      nobody else can produce.

      **The "past a dozen, optimizer gives up exhaustive search" absolute
      threshold is deliberately NOT quoted anywhere in this finding's
      output** — attempted to confirm it directly (a synthetic chain of 6
      to 18 joined base tables, `SET STATISTICS XML ON`, reading
      `StatementOptmEarlyAbortReason`): the attribute reads
      `"GoodEnoughPlanFound"` at every join count tried, including the
      smallest (6) — this is evidently a normal, common early-exit reason
      unrelated to "gave up specifically because there were too many
      tables," not the clean signal the checklist's own folklore number
      needed. Rather than force a number that wasn't actually confirmed,
      the shipped finding ranks purely by the **gap** (expanded − written)
      instead, which needs no such threshold — a real, honest structural
      fact either way, and the "nobody else can compute this" differentiator
      holds regardless of whether the absolute-optimizer-threshold claim
      is ever nailed down. That investigation remains open as a genuine
      follow-up, not silently abandoned.

      Reuses `Lineage.ViewExpansionMap` (built once, shared with "Nested-
      view depth report" above — see that item for the map's own design).
      Standalone per-query scanner (`PostExpansionJoinWidthScanner`),
      reusing `FromScopeResolver.Resolve`'s existing FROM-clause flattening
      purely for its written-count/qualified-name output. Fires at gap ≥ 3.
      `PartiallyUnexpanded` is surfaced explicitly whenever a derived
      table, MSTVF/CLR TVF fence, or other unresolved reference means the
      expanded count is a lower bound, never silently undercounted as if
      it were exhaustive. Deliberately scoped to `SELECT` statements in
      v1, matching "Multi-referenced CTE"'s own scope decision.
      `Confidence.High`, SARIF `LevelWarning`. No oracle needed for the
      shipped claim — the gap is exact structural arithmetic over the
      already-verified lineage pass, not a plan-shape claim requiring
      confirmation.

      Unit-tested (`PostExpansionJoinWidthScannerTests`, 5 cases: a view
      fanning out to 5 base tables with a written count of 1 fires with the
      correct expanded set, a plain base-table query never fires, a gap of
      1 (below the threshold of 3) never fires, written == expanded never
      fires, and the inflating view is correctly named in the finding).
      Wired end-to-end alongside the nested-view-depth stream (same schema
      bump, SARIF, readable report). **Real coverage against the local RM_
      test database: 2,993 findings** across 1,260 distinct modules
      (roughly a quarter of the whole corpus) — including several written-
      count-1 queries expanding to 36 base tables through a single view
      reference, exactly the "looks like nothing, is actually huge" shape
      no written-count-only tool can see. No silent cap: the real count is
      reported honestly rather than truncated for volume's sake.
- [x] **`SELECT *` inside a view or inline TVF.** The bare `SELECT *` rule is
      an explicit Tier 3 skip below and stays skipped — but the in-a-view case
      is a different defect with a lineage consequence: the column list is
      frozen at create time, so it silently disagrees with the base table
      after any change, and it forces every consumer to carry the full width
      whether or not it selects from it, which is how a covering index stops
      covering. Fire only at depth ≥ 1 (the view's own `ViewExpansionOrigin.Depth`
      from the already-shipped "Nested-view depth report" — it itself
      references another view/TVF, not just a base table directly) and only
      when a real consuming query elsewhere in the corpus selects a strict,
      named subset of the expanded columns — that guard is what keeps it out
      of the style-linting territory the plain rule lives in; a consumer that
      itself does `SELECT *` never narrows anything and is never matched.

      **Stronger than the "live parity gate already detects this" framing
      originally assumed — confirmed directly, not trusted from that
      description.** A view's `SELECT *` column list stays frozen not just
      in `sys.columns` (ordinary catalog staleness) but even through
      `sys.dm_exec_describe_first_result_set` (the same live, describe-only
      ground truth this codebase's own live-parity gate otherwise trusts as
      authoritative) and through a REAL EXECUTION of the view — all three
      confirmed directly on the Docker oracle: after `ALTER TABLE ... ADD`
      a new column, `sys.columns`, the describe-only probe, and an actual
      `SELECT * FROM theView` execution all kept returning only the
      original, pre-ALTER columns, until `sp_refreshview` ran. This is a
      genuinely different, current-answer-is-wrong condition, not merely a
      stale-cache-the-live-answer-still-fixes case.

      New shared foundation reused, not duplicated: `ViewExpansionMap`
      (already built for "Nested-view depth report"/"Post-expansion join
      width") supplies the depth gate and the view's already-`*`-expanded
      full column set via `LineageCatalog.AllRelations`. Own standalone
      two-step scanner (`SelectStarViewScanner`): `BuildCandidates` finds
      every view/TVF whose own OUTERMOST query specification's SELECT list
      contains a bare or qualified `*` (a `*` nested only inside an inner
      derived-table subquery does not itself qualify the view; a top-level
      `UNION`ed view declines rather than guessing which branch's star
      matters) and whose depth is ≥ 1; `Scan` then walks every query site
      corpus-wide for a consumer whose SELECT list explicitly names a
      strict, named subset — only a bare `ColumnReferenceExpression`
      qualified with the view's own alias, or unqualified when it's the
      query's only FROM source, counts as "selected"; any other shape
      declines rather than guesses. One finding per (candidate view,
      consuming query site) pair, matching `PostExpansionJoinWidthFinding`'s
      own per-query-site granularity. `Confidence.High`, SARIF `LevelWarning`
      (structural/maintenance risk, not a proven-wrong-result or proven-
      performance-cost claim — the "covering index defeated" framing is
      risk-color, not something this stream attempts to prove via an
      optimizer-inlining oracle). No oracle needed for the shipped claim
      itself (a pure catalog/lineage/AST fact); the underlying frozen-
      column-list mechanism got its own one-time oracle confirmation
      above. Version-insensitive: stable, ancient CREATE/ALTER-VIEW-time
      binding, unaffected by compat level or CE mode.

      **Bug caught and fixed during real-corpus verification, not left
      silent:** the scanner's first pass against real data returned 0
      findings despite a genuine, real consumer existing — traced to
      `FromScopeResolver.Resolve` being called with an empty resolved-
      views map (matching `PostExpansionJoinWidthScanner`'s own choice),
      which is fine for a plain `NamedTableReference` (its qualified name
      survives even when unresolved) but silently drops the qualified name
      entirely for a view/TVF invoked with inline-TVF call syntax
      (`FROM SomeView(@arg1, @arg2)`) — a real, common shape in this
      corpus. Fixed by passing the real `LineageCatalog.AllRelations` into
      the resolver instead. A second bug (the finding's own
      `ViewSourcePath` was accidentally set to the CONSUMER's file, not
      the view's own) was caught the same way, comparing the debug probe's
      output against the real CLI's JSON output line by line rather than
      trusting a single "count > 0" check.

      Unit-tested (`SelectStarViewScannerTests`, 10 cases: a consumer
      selecting a strict subset fires, a consumer selecting `*` never
      fires, a consumer explicitly listing every column never fires, a
      depth-0 view is never a candidate, qualified `alias.*` candidate
      detection fires the same as bare, a `*` nested only inside a derived
      subquery never qualifies the view, a view with no `*` at all is
      never a candidate, an unqualified column reference across multiple
      joined sources declines rather than guesses, a consumer referencing
      the view via inline-TVF-call syntax still fires (the real-corpus
      shape that caught the first bug above), and no consumer at all never
      fires). Wired end-to-end (`ScanReport` schema version 22 → 23,
      SARIF, readable report). **Real coverage against the local RM_ test
      database: 1 finding** — a real inline TVF nested 2 layers deep,
      consumed by a sibling module that explicitly selects 5 of its 6
      columns — against a raw-text sweep of 34 views and 589 inline TVFs
      containing the literal text `SELECT *` anywhere, and 697 views/TVFs
      sitting at depth ≥ 1 at all; the combination of both structural
      gates plus a genuine strict-subset consumer is a real, honest,
      low-volume signal in this codebase, not the rule spraying across
      every `SELECT *` occurrence the way a plain style linter would.

### Dynamic SQL quality (extends the existing dynamic-SQL pass)
123 modules use `EXEC(...)`, 51 use sp_executesql locally.

- [x] **Concatenated value (vs identifier) in proven-constant dynamic SQL,
      and EXEC(string) where sp_executesql with params was possible — shipped
      together, one finding type.** `UnparameterizedDynamicSqlFinding`
      (`src/SilentScan.Core/Predicates/UnparameterizedDynamicSqlFinding.cs`),
      one record with a `Kind` discriminator (`ConcatenatedValueInConstantSql`,
      `ExecStringConcatenatesParameterizableValue`) — the established "one
      record, one Kind enum, shared plumbing" shape
      (`SetOptionFinding`/`ForcedSerialFinding`/`UntrustedConstraintFinding`).
      Both kinds detect the same underlying fact: a value this scanner already
      proved constant (CLAUDE.md's Tier A dynamic-SQL folding) was spliced
      into the assembled SQL text via string concatenation, rather than
      authored as one whole literal or passed through sp_executesql's own
      `@params`. New `DynamicSqlSegmentMap.ConcatenationBoundaryOffsets`
      exposes every point where two independently-sourced literal segments
      meet in a folded script's `InnerText`; new
      `DynamicSqlOperandPositionClassifier`
      (`src/SilentScan.Core/Predicates/DynamicSqlOperandPositionClassifier.cs`)
      classifies each such offset, against the script's own reparse, as a
      VALUE position (a `Literal` node — comparison/IN/LIKE/BETWEEN operand,
      VALUES-row scalar, assignment RHS, function argument, or any other
      position only a literal can occupy) or an IDENTIFIER position (an
      `Identifier` node — schema object/column/alias name part) or Ambiguous
      (declined, never guessed). `ConcatenatedValueInConstantSql` fires on ANY
      call shape (EXEC or sp_executesql) the moment a boundary lands on a
      VALUE; `ExecStringConcatenatesParameterizableValue` fires only when the
      call site is a genuine `EXEC(string)`/`EXEC(@sql)` (never sp_executesql,
      which already has its own `@params` mechanism to lose) — the sharper,
      actionable "you had sp_executesql available and didn't use it" claim.
      New `DynamicSqlScript.IsExecString` (threaded through
      `DynamicSqlValue/DynamicSqlTransfer.cs`'s `CompileStringList`/
      `CompileSpExecuteSql`) is the precise discriminator — `sp_executesql`
      whose own `@params` argument merely failed to fold to a constant also
      has a null `ParameterDeclarationText`, which is why that field alone
      couldn't tell the two cases apart. A single `EXEC(string)` call site
      concatenating a value fires BOTH kinds (different claims about the same
      fact, not a duplicate).
      <br><br>
      **Oracle discovery, load-bearing for the finding's own wording:**
      confirmed directly against the Docker instance (`sys.dm_exec_cached_plans`)
      that three `EXEC(@sql)` calls differing only in a concatenated literal
      value compile **three** distinct cached plans, while three
      `sp_executesql` calls passing the same three values as a real `@Code`
      parameter compile **one** — real, measured plan-cache pollution, not
      blog folklore. Not verdict-bearing (no seek/scan plan-shape claim); SARIF
      level warning, floored by confidence (structural/plan-cache report, not
      a provably-wrong-result claim — same tier as
      `CatchAllPredicateFinding`/`SetOptionFinding`).
      <br><br>
      **Scope note on `ScriptDOM`'s own visitor dispatch, found and fixed
      mid-implementation:** an early version of the classifier overrode only
      `TSqlFragmentVisitor.ExplicitVisit(Literal node)` (the abstract base
      type) and silently never fired on any real literal — every literal-type
      test failed as Ambiguous. Root cause: each of ScriptDOM's 11 concrete
      `Literal` subclasses (`StringLiteral`, `IntegerLiteral`, ...) has its own
      `Accept` override that calls `ExplicitVisit` for **its own concrete
      type** at compile time, never the abstract base — a visitor overriding
      only the base type is never actually dispatched to for any real node.
      Fixed by overriding all 11 concrete subclasses explicitly (`Identifier`
      itself is concrete, so its own override needed no such fix). Caught by
      a failing unit test before shipping, not discovered against RM_.
      <br><br>
      Wired end-to-end: `ScanReport` (schema version 23 → 24), SARIF rule
      catalog (two new rule IDs) + writer, readable report section, full
      `DynamicSqlPipeline` nested-recursion plumbing (mirrors
      `CollationConflictFinding`'s own accumulator/dedupe/remap/nested-remap
      shape). Tests: 8 unit tests for the classifier itself
      (`DynamicSqlOperandPositionClassifierTests` — every VALUE/IDENTIFIER/
      Ambiguous case, including the schema-qualifier-is-an-identifier case)
      + 4 pipeline tests (`DynamicSqlUnparameterizedTests` — both kinds firing
      together on a literal EXEC(string) splice, the general kind alone firing
      on an sp_executesql call that concatenates into its own SQL text instead
      of using `@params`, no finding on a whole-single-literal EXEC with no
      splicing, no finding when the spliced boundary is an identifier — a
      dynamic table name — not a value). Real coverage against the local RM_
      test database: **4 findings, 2 real call sites** (both kinds fire on
      each) — genuinely rare, because the rule only fires on the narrow
      subset of dynamic SQL this scanner can ALREADY prove fully constant
      (964 of 1,291 real call sites), not blanket EXEC/sp_executesql usage;
      both real hits are real, non-synthetic antipatterns: one author's own
      code comment reads "silly to use DSQL but there isn't another choice...
      bummer" directly at the flagged call site, confirming this is a
      genuine, self-acknowledged antipattern in real production code, not a
      false positive. No internet-sourced fixture used — the underlying
      antipattern (concatenated literal instead of a real sp_executesql
      parameter) is universally covered advisory material (Erland
      Sommarskog's dynamic SQL articles are the canonical citation in this
      space) rather than a single distinct bug report, so test cases are
      directly authored, matching this codebase's own rare-exception
      allowance for catalog/mechanism-diff rules with no single "bug repro"
      to cite.
- [x] **Temp-table shape mismatch across a proc-call boundary — shipped.**
      `TempTableExecShapeFinding` (`src/SilentScan.Core/Predicates/
      TempTableExecShapeFinding.cs`), one record with a `Kind` discriminator
      (`ColumnCountMismatch`, `ColumnTypeMismatch`) — the established "one
      record, one Kind enum, shared plumbing" shape. `INSERT INTO #temp EXEC
      OtherProc` binds `OtherProc`'s result set to `#temp`'s own declared
      columns purely by POSITION; this compares them against the executed
      proc's REAL, engine-described shape
      (`sys.dm_exec_describe_first_result_set`, compile-only), not a re-
      derivation from the proc's own SELECT list text. `ColumnCountMismatch`
      is a distinct, cheaper claim than `ColumnTypeMismatch`: T-SQL raises a
      hard, immediate runtime error (Msg 213/8164) the instant the counts
      differ, so it isn't itself a SILENT defect — but it's still worth
      reporting, since it names a query that provably fails every time it
      runs, which static analysis alone can otherwise never promise.
      `ColumnTypeMismatch` reuses `Rules.WriteLossClassifier` per matched
      position, matching `ProcCallArgumentMismatchFinding`'s own precedent
      for a call-boundary conversion (not a predicate, so no seek/scan
      verdict and no plan-XML oracle marker — the underlying `WriteLossKind`
      mechanism is already oracle-proven in `WriteLossOracleTests`).
      <br><br>
      **Live-mode only by construction** (the DMV round trip is the whole
      verdict), computed as its own stage in `LiveScanRunner` — new
      `TempTableExecShapeCandidateScanner` (pure AST+catalog: finds every
      `INSERT INTO #temp EXEC proc` site via the identical `ExecuteInsertSource`/
      `ExecutableProcedureReference` AST shape `TvfFenceScanner`'s own
      `InsertExec` kind already walks, resolves the temp table's own declared
      columns from the catalog) feeds new `SilentScan.Live.Catalog.
      TempTableExecShapeChecker` (the live round trip: reads every executed
      proc's own parameters once via new `ReadProcedureParametersAsync`,
      builds a probe via new `LiveDescribeProbeBuilder.BuildProcedureProbe`,
      describes it via new `LiveDescribedColumnReader.DescribeProcedureOrderedAsync`
      — ordinal-preserving, unlike the existing name-keyed `DescribeObject`
      the view/TVF parity gate uses, since `INSERT ... EXEC` binds by
      position and a described column may not even carry a name). Findings
      fold into `ScanReport.TempTableExecShapeFindings` (schema version
      24 → 26 — a concurrent sibling stream landed 24 → 25 in between;
      folded on top of it, not reverted) and are also exposed whole on the
      new `LiveScanResult.TempTableExecShape` field alongside every
      declined-and-honestly-reported site (`Unanalyzed`, mirroring
      `LiveLineageParityReport`'s own "findings in the report, honesty list
      beside it" split) — a site whose temp table shape or executed proc
      couldn't be resolved is never silently dropped or guessed at.
      <br><br>
      **User-approved `LiveReadOnlyGuard` carve-out, the item's one open
      question before this could ship at all:** the DMV probe for a
      procedure needs an `EXEC dbo.SomeProc` batch, not a bare `SELECT` —
      new `LiveReadOnlyGuard.AssertDescribeFirstResultSetProbeOnly` accepts a
      bare `SelectStatement` OR a bare `ExecuteStatement` whose entity is a
      named `ExecutableProcedureReference` (never `ExecutableStringList` — a
      string-form EXEC could contain arbitrary text, not a fixed catalog-
      known name), used ONLY for text about to be bound as this DMV's own
      parameter; every other live query, including the outer command text
      this probe itself travels in, is unaffected and stays SELECT-only.
      CLAUDE.md's "read-only" bullet updated to document this precisely.
      Empirically confirmed compile-only for the EXEC form too before
      shipping (real Docker probe: 0 rows before, 0 rows after — the
      executed proc's own INSERT never ran).
      <br><br>
      **Oracle discovery mid-implementation, load-bearing for the probe's
      own design:** `EXECUTE`'s grammar accepts only a constant or a
      variable as an argument value, never an arbitrary expression —
      `CAST(NULL AS type)` (the function-probe sibling's own dummy-argument
      trick) is a real parse error here (Msg 156, "Incorrect syntax near the
      keyword 'NULL'", oracle-confirmed), so `BuildProcedureProbe` uses a
      bare, untyped `NULL` instead — oracle-confirmed to compile and
      implicitly convert to whatever the parameter's own declared type is,
      simpler than the function-probe path and never needing the
      parameter's own resolved type at all. Caught by a failing oracle
      test before shipping, not a guess. **Second oracle-found bug, caught
      only by running against the real RM_ database, not by the test suite
      alone:** an initial version of `ReadProcedureParametersAsync` also
      joined `sys.types` a second time to resolve each parameter's own type
      (mirroring `ReadFunctionParametersAsync`'s identical join) — a real
      parameter in the RM_ database had a `NULL` `ty.name` from that second
      join (no guaranteed match from `system_type_id` to every base type),
      crashing `SqlDataReader.GetString` with `SqlNullValueException` and
      aborting the whole scan. Root-caused and fixed by dropping the join
      and the `Type` field entirely rather than patching around it with a
      null guard — the probe never actually reads a parameter's resolved
      type (see above), so the join was dead weight the crash exposed, not
      a case worth guarding defensively.
      <br><br>
      Wired end-to-end: `ScanReport` (schema version 25 → 26), SARIF rule
      catalog (two new rule IDs — `ColumnCountMismatch` at `error` level, a
      provably-wrong-outcome claim; `ColumnTypeMismatch` at `warning`,
      matching `WriteLossFinding`'s own tier) + writer, readable report
      section. Tests: 6 candidate-scanner unit tests
      (`TempTableExecShapeCandidateScannerTests` — named-procedure EXEC
      fires, string-form/dynamic-variable EXEC never fires, a real table
      target never fires, an ordinary SELECT source never fires, unresolved/
      batch-level temp table shape reported honestly as null not guessed),
      7 pure classification unit tests
      (`TempTableExecShapeCheckerClassifyTests` — matching shape, both
      count-mismatch directions, a real `WriteLossKind` per type pair,
      only the mismatched position reported), 9 probe-builder/guard unit
      tests, and 5 real end-to-end oracle tests through the live engine-
      authoritative pipeline (`TempTableExecShapePipelineTests` — column-
      count mismatch fires, a unicode-into-non-unicode type mismatch fires,
      a matching shape never fires, a nonexistent executed proc reports
      unanalyzed not a false finding, an OUTPUT parameter reports unanalyzed
      not a false finding). Real coverage against the local RM_ test
      database: **0 findings, 19 real `INSERT INTO #temp EXEC proc` call
      sites, every one honestly declined rather than guessed** — 9 because
      the temp table's own shape wasn't resolved by this tool's own catalog
      pass (nested/complex control flow), 8 because the executed proc
      itself couldn't be described (half of those are `EXEC sp_executesql
      ...` sites — `sp_executesql` itself IS a named `ExecutableProcedureReference`
      syntactically, so this pass correctly attempts it, then correctly
      declines once the DMV reports the actual dynamic SQL text is what
      can't be described, Msg 11514/11526/11529 — not a false candidate,
      a genuinely undescribable one), 2 because the executed proc's own
      parameter list includes a table-valued parameter this probe has no
      positional literal form for. A real, honest zero-finding result, not
      evidence the rule is inert — every declined site is a genuine boundary
      case this pass is honest about rather than silently either dropping
      or guessing a verdict for.

**This closes the entire "Dynamic SQL quality" section** — all three items
now shipped.

### Schema-scan UDF and computed-column findings (found on completeness audit) — closed
Distinct trigger from the already-shipped scalar UDF stream's plan-based
findings: these fire from catalog
metadata alone, independent of whether the object ever shows up in a cached
plan, so they need no plan/oracle involvement to report (though should still
get an oracle fixture for the serial-plan consequence).

- [x] **CHECK constraint whose definition references a scalar/CLR function —
      already fully shipped**, discovered re-planning this item: the
      already-shipped scalar-UDF stream's schema-dependency half
      (`SchemaDependencyScanner`, `ScalarUdfFindingKind.SchemaDependency`)
      already walks every `SchemaExpressionReference` with
      `SchemaDependencyKind.CheckConstraint` — a CHECK constraint's
      definition text is reparsed through the same throwaway-wrapper trick
      as a computed column/DEFAULT and resolved against the scalar-UDF
      registry identically. Already tested
      (`SchemaDependencyScannerTests`: `Fixture_CheckConstraint_RealCitedFunction_Fires`,
      `ColumnLevelCheckConstraintCallingScalarUdf_Fires`,
      `TableLevelCheckConstraintCallingScalarUdf_Fires`,
      `CheckConstraintWithNoUdfCall_DoesNotFire`, plus a near-miss). **New
      this pass: the "forces serialized execution" runtime claim in this
      item's own text was not previously oracle-tested for the CHECK-
      constraint case specifically** — now is
      (`SchemaDependencyCheckConstraintSerialOracleTests`): a real seeded
      table (5,000 rows) with a CHECK constraint calling a scalar UDF shows
      `NonParallelPlanReason="TSQLUserDefinedFunctionsNotParallelizable"` on
      an `UPDATE` that evaluates the constraint. **Correction to the item's
      own premise, oracle-confirmed:** the claim as originally written
      ("forces serialized execution of every query and maintenance
      operation against the table") is overstated — a CHECK constraint only
      evaluates on a write that could violate it (`INSERT`/`UPDATE`), never
      on a plain `SELECT`; a read-only query against the same table shows no
      such marker, confirmed directly in the same oracle test file. This
      matches `detection-reference.md`'s own Appendix 2 framing ("Scalar UDF
      in computed column / DEFAULT / CHECK: serializes every query touching
      the table" is itself about *writes* touching the table, not reads) —
      the checklist item's shorthand just needed the same precision the
      research record already had. Real coverage against the local RM_ test
      database: this is the `SchemaDependencyKind.CheckConstraint` slice of
      the already-measured scalar-UDF `SchemaDependency` count — 0 CHECK
      constraints in that database currently call a scalar/CLR function (a
      real, honest zero, not a detection gap — the mechanism fires
      identically to the already-verified computed-column/DEFAULT cases
      that do have local hits).
- [x] **Non-persisted computed column (`is_persisted = 0`), independent of
      whether it references a UDF — shipped**: new
      `NonPersistedComputedColumnFinding`/`NonPersistedComputedColumnScanner`
      (`src/SilentScan.Core/Predicates/NonPersistedComputedColumnScanner.cs`)
      — pure catalog walk over `DatabaseCatalog.Tables`, mirroring
      `MaxTypedColumnScanner`'s own "one structural fact per column, no AST"
      shape. Both `CatalogColumn.IsComputed`/`IsPersisted` were already read
      by both file mode (the DDL's own `PERSISTED` keyword) and live mode
      (`sys.computed_columns.is_persisted`, joined into
      `LiveCatalogReader`'s existing per-column read) for an earlier,
      unrelated consumer — no new catalog plumbing needed, just a new
      scanner reading fields that already existed. Cross-references
      `DatabaseCatalog.SchemaExpressions`' `ComputedColumn` entries purely to
      recover the definition text/precise line for the finding message.
      Purely structural/informational — "recomputed on every read" is
      definitionally true for a non-persisted computed column, no plan-XML
      oracle needed (same reasoning `MaxTypedColumnScanner`/
      `ColumnCollationDriftScanner` already used for their own catalog-only
      facts). Never fires on a `PERSISTED` computed column regardless of
      whether it's also indexed. Wired end-to-end (`ScanReport` schema
      version 24 → 25, SARIF rule `silentscan/catalog/non-persisted-
      computed-column`, readable-report section). Tested: 5 unit tests
      (`NonPersistedComputedColumnScannerTests` — fires, PERSISTED never
      fires, ordinary column never fires, PERSISTED+indexed never fires,
      multi-table ordering). Real coverage against the local RM_ test
      database: **41 findings**.
- [x] **Deprecated `*=`/`=*` outer-join operators — closed, not reachable
      through this tool's own parser dialect, confirmed empirically rather
      than assumed**: probed directly against `TSql160Parser` (the exact
      parser class `SqlScriptParser` uses, SQL Server 2022/compat 160) with
      `SELECT * FROM A, B WHERE A.Id *= B.AId;` — a hard parse error
      ("Incorrect syntax near '\*='"), not a distinct AST node shape to
      pattern-match. This syntax was removed from the engine's own grammar
      entirely at compatibility level 90+ (SQL Server 2005), and ScriptDOM's
      parser follows the same modern grammar — there is no shape for a rule
      to ever match. A real corpus file using this syntax would already
      fail to parse as a whole (surfaced today via the existing
      `ParseHealthReport`/dialect-sniffing machinery as a parse error, not
      silently swallowed), which is a strictly stronger and more honest
      signal than a narrow AST-pattern rule could give — a targeted rule
      here would be dead code that can never fire. Closed as not shippable
      exactly as stated, rather than forcing a shape that doesn't exist.

**This entire section is now closed.**

### Halloween Protection and self-referencing DML — shipped
- [x] `INSERT`/`UPDATE`/`DELETE`/`MERGE` whose source query reads the same
      target table (hole-filling `INSERT ... WHERE NOT EXISTS`,
      `UPDATE ... FROM` self-join) — **corrected from "always an eager
      spool" to "an eager spool OR a sort, depending on statement shape,"
      oracle-confirmed the hard way, not assumed.** A compile-only `SET
      SHOWPLAN_XML` probe against all four statement kinds, each cross-
      checked against an otherwise-identical control reading a DIFFERENT
      table: INSERT and DELETE really do gain a
      `PhysicalOp="Table Spool" LogicalOp="Eager Spool"` operator exactly as
      the checklist's own text claimed — but `UPDATE ... FROM` self-join and
      MERGE gain NO spool at all; instead the plan gains an extra `Sort`
      (`LogicalOp="Distinct Sort"` for UPDATE, plain `LogicalOp="Sort"` for
      MERGE) that is completely absent from the cross-table control. Both
      mechanisms materialize/reorder the affected rows before any write
      starts — the same "read fully before you write" correctness guarantee,
      just a different physical operator depending on shape — so the
      finding's own message says "extra defensive plan work (a spool or
      sort)" rather than overclaiming a spool where the real mechanism is a
      sort. Also oracle-confirmed: reading through a **view** over the same
      base table triggers the identical Eager Spool a direct reference gets
      on an INSERT — `SelfReferencingDmlFindingKind.ThroughView` exists
      because of this, resolved via the already-built
      `Lineage.ViewExpansionMap`'s own `BaseTables` set, not a guess.
      `SelfReferencingDmlFinding`/`SelfReferencingDmlScanner`
      (`src/SilentScan.Core/Predicates/SelfReferencingDmlScanner.cs`) —
      pure syntax, reuses `FromScopeResolver.ResolveForDataModification`/
      `ResolveForMerge` (the same UPDATE/MERGE-scope resolution
      `TypedPredicateExtractor`/`NonUniqueUpdateSourceScanner` already use)
      purely to learn the write target's own resolved qualified name and
      FROM-clause alias, never for column resolution. One finding per
      statement (the fact reported is "does the read side re-read the
      target," not an occurrence count) — the target's own single canonical
      FROM-clause entry is skipped exactly once (T-SQL forbids duplicating
      an alias within one FROM clause, so this is always safe) so it is
      never mistaken for a re-read of itself; every other match, including
      one found inside a WHERE/SET-clause subquery (which never contains
      the target's own entry at all), is a genuine extra read. **Known v1
      scope limits, stated honestly:** only a `NamedTableReference` match is
      covered (an inline-TVF-call-syntax read of the target's own MSTVF
      wrapper is not chased); a WHERE/SET-clause subquery reusing the
      outer target's own alias for an unrelated table in its own nested
      scope is not disambiguated from a genuine self-reference sharing that
      alias (full nested-scope tracking is out of scope for a syntax-only
      rule); a self-join whose two sides are provably disjoint by a static
      predicate still fires — proving disjointness statically is out of
      scope, the same over-reporting trade-off `NonUniqueUpdateSourceFinding`
      already accepts for its own fan-out risk. A performance-cost finding,
      not a correctness one (the result is identical either way) —
      `FindingConfidence.High` by default but SARIF Warning, the same
      "structural risk, not provably-wrong-result" tier
      `ForcedSerialFinding`/`CatchAllPredicateFinding` already use. Wired
      end-to-end (`ScanReport` schema version 26 → 27, SARIF rule
      `silentscan/dml/self-referencing`, readable-report section). Tested:
      14 structural unit tests (`SelfReferencingDmlScannerTests` — all four
      statement kinds, both Direct/ThroughView kinds, every near-miss/control
      pairing, the one-finding-per-statement dedup guard) + 10 real oracle
      tests (`SelfReferencingDmlOracleTests`, compile-only `SET SHOWPLAN_XML`
      against the live Docker instance — a self-referencing DML's defensive
      plan work is a compile-time structural artifact, not a cardinality-
      dependent choice, confirmed directly against completely empty tables).
      Real coverage against the local RM_ test database: **0 findings** — a
      real, honest zero (this codebase's own DML apparently doesn't use the
      hole-filling/self-join idioms this rule targets), not a detection gap;
      the mechanism itself is oracle-proven and the scanner correctly fires
      on every hand-authored fixture in the unit-test suite.

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

### Second OSS/commercial sweep (7 repos, 2026-08-16)
`s01nik/SQL-Performance-Analyzer` (9-rule regex toy), `felipebz/zpa` (Oracle
PL/SQL, no T-SQL — matches the existing Appendix 7 entry), `ashleyglee/
TSqlRules` (dead since 2017, 8 rules, naming-only) and the current
`gretard/sonar-sql-plugin` (confirmed same 16 T-SQL rules already on record,
no expansion) added nothing new. Two did:

- **`slowql`** — this is `detection-reference.md`'s existing "Rust
  multi-dialect linter" (§7.4), already surveyed and already recorded at 282
  rules; re-cloning it confirmed the count and behavior, nothing new. Real,
  active, 14-dialect Rust analyzer, pattern/regex-based, no live catalog.
  Only ~10 rules are T-SQL-specific; the rest corroborate rules already
  shipped or queued (NOCOUNT, ANSI_NULLS/QUOTED_IDENTIFIER, cursor
  FAST_FORWARD, WAITFOR, `@@IDENTITY`, MERGE/HOLDLOCK).
- **`gretard/sonar-sql-plugin`** — likewise already `detection-reference.md`'s
  existing "SonarQube T-SQL plugin" (§7.5), confirmed unchanged at 16 rules.
- **`ErikEJ/SqlServer.Rules`** (active fork of the dormant `tcartwright/
  SqlServer.Rules`, itself `detection-reference.md`'s existing "SqlServer.Rules
  (DacFx)" entry, §7.2) — genuinely bigger than recorded: 135 rules now, not
  120 (`tcartwright` baseline is 80; the fork adds 56, including exposing
  DacFx's own built-in SR0001–SR0016 for the first time). Corrects our count.

**The real finding of this round isn't new incumbent rules — it's that several
were already sitting in `detection-reference.md`'s existing Appendix 7 (from
the original survey session) and never got promoted into this checklist as
work items.** Re-cloning these repos mostly re-confirmed what we already had
on record; queuing them below is what was actually missing.

- [ ] **`SELECT INTO` a temp table later joined/filtered with no index** and
      **`TRUNCATE TABLE` inside a `TRY` block with no matching `CATCH`** —
      both already in Appendix 7.4 (`PERF-TSQL-002`, `REL-TSQL-003`), never
      queued until now. The first needs the same temp-table-lifecycle
      tracking the queued "temp-table shape mismatch across a proc-call
      boundary" item already requires — natural to build together. The
      second is pure syntax: `TRUNCATE` can fail (an untrusted/enforced FK
      reference is the common case), and unlike a hard error outside any
      `TRY`, the failure is silently swallowed if nothing catches it.
- [ ] **`SET DATEFORMAT`/`SET DATEFIRST` changed mid-module** — already in
      Appendix 7.2 (`SRD0082`/`SRD0083`), never queued. Same family as the
      shipped/queued SET-option stream, different mechanism: these don't
      block a plan feature, they change how date *literals* and
      `DATEPART`-relative comparisons are parsed, so the same literal means a
      different date depending on which session compiled it first — a
      direction-of-harm this project is already built to state precisely.
      Syntax-only (no baked-in `sys.sql_modules` column expected; confirm
      against the Appendix 8 column list before assuming).
- [ ] **Unnamed `PRIMARY KEY`/`DEFAULT`/`FOREIGN KEY`/`CHECK` constraint on a
      `#temp` table** — already in Appendix 7.2 (`SRD0092`–`0095`), never
      queued. An unnamed constraint gets a system-generated name; two
      sessions creating the same-shaped temp table concurrently in the same
      `tempdb` can collide on that generated name, an intermittent,
      hard-to-reproduce failure. In scope by the same CLAUDE.md carve-out
      that already covers temp tables created inside proc bodies.
- [ ] **Database-level configuration flags** — already in Appendix 7.2, never
      queued; genuinely new finding *category*, not module/predicate-level
      like everything else in this file: `PAGE_VERIFY <> CHECKSUM` (silent
      corruption goes undetected), `AUTO_SHRINK = ON` (a well-known, severe
      anti-pattern — constant fragmentation churn), `AUTO_CLOSE <> OFF`,
      `TARGET_RECOVERY_TIME` unset, `QUERY_STORE <> READ_WRITE`,
      `QUERY_STORE_CAPTURE_MODE <> AUTO`. All read directly from
      `sys.databases` in live mode — no query text involved at all, the
      simplest possible catalog-only finding, but needs a new finding shape
      since nothing here today reports at database granularity rather than
      module/column/predicate granularity.
- [ ] **True cartesian join — comma-join or explicit `CROSS JOIN` with no
      predicate anywhere connecting the two sides** — already in Appendix 7.5
      (`C023`), never queued. Deliberately distinct from the shipped
      partial-composite-FK-join rule: that fires when a join predicate exists
      but is incomplete; this fires when there is no predicate joining the
      pair at all. Pure AST — cheaper and higher-precision than the partial
      case, worth building even before it if sequencing matters.
- [ ] **Declared type of size 1 or 2** (`varchar(1)`, `varchar(2)`, etc.) —
      already in `detection-reference.md`'s hard-cases table (line ~317,
      previously decided "Skip (lint)" under the now-superseded admission
      rule; that disposition no longer applies under the current scope rule).
      A narrow declaration smell distinct from the shipped
      under-length-vs-column-comparison rule — this one doesn't need a
      compared column at all, a size that small on its own is almost always
      a truncated-from-a-larger-source mistake or a leftover placeholder.
      Lightweight companion to the existing under-length stream.
- [ ] **Output parameter not populated on every code path** — (`ErikEJ` fork,
      `SR0013`) — real control-flow strengthening of the already-queued
      "output parameter never assigned" item (some paths vs. no paths at
      all); fold into that item rather than building twice.
- **Two read but not yet understood well enough to scope** — (`ErikEJ` fork):
  `SR0006` "move a column reference to one side of a comparison operator" —
  possibly already subsumed by the shipped column-arithmetic non-sargability
  rule, needs a source-level read of the actual rule (not just its name)
  before deciding; `SR0015` "extract deterministic function calls from WHERE
  predicates" — exact trigger condition unclear from the survey alone. Both
  need a closer read before either gets queued or dropped, same discipline as
  the two unresolved Tier 4 items from the earlier vendor-plugin sweep.

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

## Tier 3 — out of scope (production-only signals)
Not visible from code or schema at all, so no amount of static analysis
reaches them — the one real exclusion under CLAUDE.md's scope rule. Two
items:

- **Parameter sniffing** — which plan a parameter *value* gets depends on the
  runtime data distribution and which value first compiled the plan; neither
  is present in the code or the schema. A static tool can flag risk factors
  (a catch-all predicate, `OPTION(RECOMPILE)` absent) but not the sniffing
  event itself — those risk factors are separately queued under "Catch-all /
  kitchen-sink predicates" in Tier 2, which is the honest static form of this.
- **Runtime-only signals** — spills, memory grants, execution frequency,
  compile time, stale statistics, plan-cache duplication, row-estimate
  mismatch, query/order hint usage counters (`sys.dm_exec_query_optimizer_info`
  is a since-restart aggregate, not a per-query static fact). None of these
  exist until a query actually runs against real data; the oracle in this
  project stays compile-only by design, which structurally cannot reach them.

Everything else formerly listed here was excluded for reasons that no longer
apply under the current scope rule (crowded linter territory, "different
tool," "we'd be guessing," "needs a corpus repo that uses the feature") —
none of those are about production-only visibility, so they've moved to
Tier 4 as in-scope, unbuilt candidates.

Context note, not a scope decision: the mainstream CI-analysis platform's
T-SQL coverage was measured on both its tiers — free (16 rules, dormant since
2024) and paid (~83 rules, read at source 2026-08-16) — and neither has a
conversion, collation, or lineage-aware rule, or a plan oracle. Details in
`detection-reference.md` Appendix 7.

---

## Tier 4 — syntax-only, unbuilt (reopened 2026-08-16)
Was excluded for needing no catalog/lineage/oracle; that's no longer a reason
to exclude anything (see CLAUDE.md's scope rule). Each item still needs its
own fixture pair (fire + near-miss) before it ships, same as every rule here.

- **The maintainability/correctness bulk of the paid-tier T-SQL analyzer read
  at source on 2026-08-16** — ~68 of its ~83 rules, grouped by theme:
  * *Size and complexity metrics* — line length, file length, routine length,
    parameter count, nesting depth, expression-operator count, branch count,
    branch-body length. Configurable thresholds over the AST, no catalog
    needed.
  * *Formatting and layout* — tab characters, one statement per line, one
    declaration per line, misleading indentation, a branch keyword sharing a
    line with the end of the previous block, missing `BEGIN…END` around a
    conditional body, useless parentheses, empty statements, file header
    comments.
  * *Naming and identifiers* — routine name patterns, variable name patterns,
    a reserved keyword used as an identifier, database/schema qualification on
    a `CREATE`.
  * *Dead and duplicated code* — unreachable code, unused labels, unused local
    variables, unused parameters, redundant jumps, commented-out code,
    duplicated string literals, a loop that can only run once, self-assignment,
    identical operands either side of an operator, duplicated conditions,
    identical branch bodies, a conditional whose branches are all the same,
    redundant conditions, mutually exclusive conditions, collapsible nested
    conditionals, nested conditional-expression functions, a repeated unary
    operator, a negated comparison written as the negation of its opposite.
    The always-true/always-false predicate family here overlaps the
    enum-style `CHECK`-constraint candidate below — same rule, build once.
  * *Task-comment tracking* — `TODO`, `FIXME`.
  * *Non-ANSI and deprecated spellings* — `!=`/`!<`/`!>`, `= NULL` in place of
    `IS NULL`, a `LIKE` pattern containing no wildcard, legacy system
    compatibility views, table hints written without `WITH`, index hints with
    a two-part name, numbered procedures, string literals used as column
    aliases, unparenthesised error-raising, an assortment of removed system
    procedures. (The one member with real plan teeth, the old non-ANSI
    outer-join operators, is already queued separately in Tier 2.)
  * *Statement-shape advice* — `SELECT *`, `INSERT` without a column list,
    ordinal `ORDER BY`, `TOP` without `ORDER BY`, a table with no primary key,
    `UPDATE`/`DELETE` with no `WHERE`, an existence check over an unfiltered
    `SELECT`, more than N tables written in a join (the resolved,
    view-expanded version of this is separately queued in Tier 2 and is the
    one worth building first), requiring a named session setting at the top
    of every routine, requiring an explicit constraint-check mode.
  * *Cursor and control-flow correctness* — a fetch selecting a different
    column count than its cursor declares, an output parameter never
    assigned, an empty catch block, output emitted from a trigger, dirty-read
    isolation hints, duplicated arguments in a call, a legacy identity
    intrinsic where the scope-limited one was meant.
  * *Security* — dynamic code execution, hard-coded credentials, hard-coded IP
    addresses, weak hash algorithms in general and in sensitive contexts.
    Still separately tracked under Open scope questions below, since it's a
    bigger axis question than this reopening covers.
  * *Missing/ambiguous `ELSE`* — `IF`/`CASE` with no `ELSE` where a sibling
    has one, and the closely related dangling-`IF`-on-a-shared-line ambiguity.
  * `GOTO` usage.
  * A redundant database/schema qualifier on a reference already in scope
    (the opposite complaint from the qualification-*requiring* rule above).
  * A non-deterministic function (`RAND`/`NEWID`/`CRYPT_GEN_RANDOM`) used as a
    `CASE` **input expression** — re-evaluated per `WHEN` comparison, so the
    branch taken can silently disagree with itself. Distinct from the
    per-row-predicate premise probed and killed elsewhere in this file (that
    one was about seek behavior; this is about `CASE` evaluating its own
    input more than once) — never itself probed, but syntax-only either way.
  * Two with unresolved exact semantics — read from decompiled source, not
    confirmed: a rule pairing one `SET` option being `OFF` against a sibling
    that should be `ON` (unclear whether this overlaps the already-falsified
    `ARITHABORT` finding elsewhere in this file), and a rule about a statement
    "forcing serialization" without `SNAPSHOT ISOLATION` whose firing
    condition wasn't reconstructable from the bytecode alone. Pin down the
    actual trigger condition (vendor docs or a fresh oracle probe) before
    building either.
  The read's other conclusion still stands regardless of this reopening: the
  richest paid T-SQL rule set found still contains no implicit-conversion
  rule, no collation-aware rule, no lineage-aware rule, and no plan oracle —
  recorded in `detection-reference.md`.
- **Everything else the old Tier 3 excluded for a reason other than
  production-only visibility** — same status as above, unbuilt candidates now:
  `SELECT *`/`SET NOCOUNT`/`sp_` prefix/schema-prefix/ordinal `ORDER BY`
  style linting; missing/duplicate/unused indexes, heaps, fill factor,
  clustering-key width (index-advisor space); `NOLOCK`/`READ UNCOMMITTED`;
  `MERGE` pitfalls (`WHEN MATCHED THEN DELETE`, missing `HOLDLOCK`);
  `CHECK (col IN (...))` treated as an enum, flagging a predicate proven
  false against it; DISTINCT masking a bad join, a correlated subquery that
  won't unnest, row goals, `UNION` vs `UNION ALL`; indexed-view `NOEXPAND`
  matching; `OR` across different columns; partition-elimination defeat;
  Always Encrypted column-comparison restrictions; Batch Mode on Rowstore
  eligibility loss; the window-function Partition-Order-Covering index shape.
  Each item's old entry (elsewhere in this file's history) already states
  its own precision caveat — those still apply as implementation guidance,
  just not as an admission veto anymore.

---

## Open scope questions

Resolved by the scope rule above (2026-08-16): security/compliance/
correctness rules are in scope on the same basis as everything else —
detectable from code and schema, so admissible. Not built yet; goes in the
same reopened queue as Tier 4. The old open question here was whether
CLAUDE.md's identity statement covered this axis at all — it does now, since
the identity statement itself changed.

Incumbent security rule lists for reference: `detection-reference.md`
Appendix 7 §7.4.

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
