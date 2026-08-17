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
- [x] **Sibling: parameter overwritten before use in a predicate
      (sniffing-defeat) — shipped.** A formal parameter's compile-time
      SNIFFED value (the caller's real argument) is what the cached plan is
      built against — if the procedure's own body reassigns that same
      parameter (`SET @p = ...`/`SELECT @p = ...`) on every path reaching a
      later predicate use of it, the plan was compiled against the ORIGINAL
      value while the predicate actually runs against the NEW one. Distinct
      from the already-shipped "Local-variable predicates" finding: that one
      fires on a `DECLARE`d local, which was never a sniffable, caller-
      supplied value in the first place — this fires on a genuine formal
      parameter whose sniffed value was invalidated by the procedure's own
      code before the predicate that would have benefited from it ever ran.
      New `ParameterReassignmentPredicateFinding`/
      `ParameterReassignmentPredicateScanner`
      (`src/SilentScan.Core/Predicates/ParameterReassignmentPredicateScanner.cs`)
      — a real, sound, path-sensitive reachability walk over the procedure's
      own statement list (`IF`/`ELSE`, `WHILE`, `TRY`/`CATCH`, `BEGIN`/`END`,
      `RETURN`/`THROW`, `GOTO`), the exact same shape
      `OutputParameterScanner`/`TransactionHygieneScanner` already
      established for "does a fact hold on every path" — but tracking the
      DUAL property: those two track "is there some path where a fact does
      NOT yet hold" (state shrinks toward empty, merges via UNION at a
      branch); this tracks "does a fact hold on EVERY path reaching here"
      (state only grows, merges via INTERSECT at a branch — a reassignment on
      only one side of an `IF` is never carried past the merge point, sound
      rather than merely conservative, since a predicate after the merge
      cannot be guaranteed to see the reassigned value unless BOTH branches
      produced it). Deliberately base-table-only and `WHERE`-clause-only,
      matching `CatchAllPredicateScanner`'s own scope: JOIN `ON`/`HAVING`
      predicates and MERGE's own `ON` clause are a known, explicitly
      out-of-v1-scope gap (MERGE's scope resolution differs enough to need
      its own dedicated work, the identical reasoning
      `CatchAllPredicateScanner` already documents). Only
      `BooleanComparisonExpression` operators are matched (`=`/`<`/`<=`/`>`/
      `>=`/`<>`) — `LIKE` uses a distinct `LikePredicate` AST shape, a known
      v1 scope limit. Same `OPTION(RECOMPILE)`/`WITH RECOMPILE` suppression
      as the "Catch-all"/"Local-variable predicates" siblings — fully
      suppressed, not downgraded, since a per-execution recompile sees the
      parameter's real, post-reassignment value. Purely informational,
      `FindingConfidence.Low`, never verdict-bearing, matching
      `LocalVariablePredicateFinding`'s own honesty: no estimate magnitude
      claim, only that the sniffed value is provably stale by the time the
      predicate runs.

      **Real, genuine bug caught only against the real corpus, not by the
      unit-test suite alone (fixed before shipping):** the first working
      version tracked ANY reassigned variable name, not just formal
      parameters — a real module in the local test database `DECLARE`s a
      local, reassigns it via a running-accumulator `SELECT @v = CASE ...`,
      and compares it in a predicate; the first version mis-fired on this as
      if it were a reassigned formal parameter, when it is exactly the
      already-shipped `LocalVariablePredicateFinding`'s own concern instead
      (a `DECLARE`d local was never sniffable to begin with — there is no
      staleness to report). Fixed by seeding a per-procedure
      `_formalParameterNames` set (mirroring
      `CatchAllPredicateScanner`'s/`TypedPredicateExtractor`'s own identical
      tracking) and filtering every reassignment through it before it is
      ever added to the reachability state — a `DECLARE`d local's own
      reassignment is now silently ignored by this scanner, exactly as
      intended. Regression-tested
      (`DeclaredLocalVariable_ReassignedThenUsedInPredicate_NeverFires`).

      **Oracle-confirmed the general mechanism** (a genuine compile-time
      phenomenon, like `LocalVariablePredicateFinding`, not like the
      catch-all stream's own RECOMPILE finding which needed real execution —
      parameter sniffing for a stored-procedure `EXEC` is fully visible to
      the existing compile-only `SET SHOWPLAN_XML ON` probe):
      `ParameterReassignmentPredicateOracleTests` calls a real seeded
      procedure with a common, high-frequency argument value, whose body then
      reassigns the parameter to a value with ZERO real rows before the
      predicate runs — the plan's `EstimateRows` still reflects the ORIGINAL
      sniffed argument's real skew (~1900 of 2000 rows), never the
      reassigned value's own near-zero density, proving the compiled plan is
      structurally blind to the reassignment. Unit-tested
      (`ParameterReassignmentPredicateScannerTests`, 18 cases: `SET`/`SELECT
      @v =` reassignment before a predicate fires, predicate BEFORE the
      reassignment never fires, no reassignment never fires, the
      `DECLARE`d-local regression above, reassignment in only one `IF`
      branch never fires, reassignment in both branches fires, reassignment
      inside the same branch as the predicate fires, a `WHILE` loop body's
      own reassignment never propagates past the loop, `CATCH` never
      inherits what `TRY` did, both RECOMPILE guards, unindexed columns
      still fire, `UPDATE` statements, range operators, `GOTO` declines the
      whole procedure). Wired end-to-end (`ScanReport` schema version 35 →
      36, SARIF rule `silentscan/predicates/reassigned-parameter`,
      readable-report section). **Real coverage against the local RM_ test
      database: 34 findings across 18 modules** after the `DECLARE`d-local
      fix — down from an unfixed-build measurement of 582, confirming the
      fix's real precision impact on this exact corpus, not just a
      theoretical one (the overwhelming majority of the original 582 were
      false positives against reassigned `DECLARE`d locals, not formal
      parameters). Spot-checked a real true positive against actual module
      text (`dbo.spContractList`): a formal `@StartDate DATETIME` parameter
      reassigned via `SET @StartDate = COALESCE(@StartDate, '1900/01/01
      00:00:00')` — a completely ordinary NULL-default idiom — then compared
      directly in the WHERE clause; a caller passing a real date gets that
      date sniffed, but a caller passing `NULL` sniffs a value the predicate
      never actually runs against once the COALESCE default takes over,
      exactly the staleness this rule targets.

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
- [x] **Serial-zone constructs as informational: TOP row goals, recursive
      CTEs, global scalar aggregates — investigated and closed, not built.**
      MSTVF refs are already covered by the shipped MSTVF-as-fence stream. A
      recursive CTE was sanity-checked directly and shows no
      `NonParallelPlanReason` attribute at all (the optimizer never appears
      to consider a parallel plan for the recursive union in the first
      place) — already closed in an earlier pass, not re-investigated here.
      The two genuinely remaining candidates were each investigated directly
      on the oracle and neither survives as a real, precise, non-redundant
      finding:
      * **TOP + ORDER BY "row goal"** — the mechanism itself IS real and
        genuinely attributable: a `SET SHOWPLAN_XML ON` probe against
        `SELECT TOP (10) ... ORDER BY IndexedCol DESC` over a 50,000-row
        indexed table shows the scanned `RelOp` carrying both
        `EstimateRows="10"` (the row-goal-biased estimate that drove the
        operator choice) and a separate `EstimateRowsWithoutRowGoal="50000"`
        attribute — a precise, unambiguous marker distinguishing a row-goal
        plan from an ordinary one, oracle-confirmed directly rather than
        assumed. **But the row goal itself is normal, usually BENEFICIAL
        optimizer behavior** (it is exactly what makes `TOP N ... ORDER BY`
        on an indexed column fast — an index-ordered scan that stops early,
        instead of sorting the whole table) — reporting every occurrence of
        this extremely common, ordinarily-fine pattern would be pure noise,
        not a risk signal. The REAL, well-known risk (a row-goal estimate
        that turns out badly wrong because of a co-occurring highly
        selective filter, causing the optimizer's nested-loop-style plan to
        scan far more rows than the row goal assumed) needs a data-
        distribution magnitude fact this static tool cannot see — the same
        honesty `LocalVariablePredicateFinding`/`OversizedParameterFinding`
        already apply to their own no-magnitude-claim risk. Nothing survives
        that is both true and a distinct, non-noisy finding.
      * **Global scalar aggregate with no GROUP BY** — oracle-falsified
        directly, not merely judged too vague: seeded a table at 550,000
        rows (`SELECT COUNT(*) FROM dbo.T`, no WHERE, no GROUP BY) and found
        `Parallel="0"` with the real `StatementSubTreeCost` (1.79) below the
        server's own `cost threshold for parallelism` (5) — the query is
        forced serial for the same reason ANY cheap-enough query is,
        ordinary cost-based non-parallelism, not a structural restriction
        specific to global aggregates. Confirmed the absence of any real
        block by re-seeding to 2,000,000 rows and re-running the identical
        query: it went fully parallel (`Parallel="1"` on every operator, no
        `NonParallelPlanReason` anywhere), the opposite of what a genuine
        "forces serial" construct (like the three already-shipped kinds)
        would show. There is no structural mechanism here at all — this
        candidate is fully dropped, not merely descoped.
      Nothing shipped for this item — both candidates were oracle-
      investigated and found not to survive as real, precise, non-redundant
      findings, the same "proposed and killed the same session" discipline
      the "Non-foldable nondeterministic intrinsic in a predicate" and "`IF`
      statements containing queries" items elsewhere in this file already
      model. Recorded here because the value is the falsification/scoping
      work, not a shipped verdict.

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
      **Coverage correction (2026-08-17):** this entry originally recorded
      "0 findings" against the local RM_ test database. Re-measured via a
      fresh `scan-db` run while investigating an unrelated stream and found
      **420 findings** (401 `DirectTableReference`, 19 `ThroughView`; 240
      INSERT, 71 UPDATE, 56 DELETE, 53 MERGE) — the original "0" was stale/
      wrong, not a real result; the scanner itself was never broken. Spot-
      checked directly against the real deployed module text for a sampled
      finding (an `INSERT INTO dbo.tblTripsScheduled (...) SELECT ... FROM
      dbo.tblTrips ... WHERE ... AND NOT EXISTS (SELECT * FROM
      tblTripsScheduled WHERE ...)`) and confirmed it is a genuine true
      positive — the exact hole-filling `INSERT ... WHERE NOT EXISTS`
      anti-join idiom this rule targets, with the self-reference appearing
      several lines below the statement's own reported start line (the
      finding's `Line`/`Column` point at the statement, not necessarily the
      exact re-read line, matching this stream's own "one finding per
      statement" design stated above) — not a false positive. The mechanism
      itself remains oracle-proven and the scanner correctly fires on every
      hand-authored fixture in the unit-test suite; only the recorded real-
      corpus number was inaccurate.

### Temporal table history-side index gap — shipped
- [x] System-versioned temporal table (`sys.tables.temporal_type`) whose
      history table lacks the index set the current table has —
      **oracle-confirmed directly, not assumed from the checklist's own
      premise**: built a real temporal table (5,000 current-table rows, 2,500
      history-table rows, `UPDATE STATISTICS ... WITH FULLSCAN` on both) and
      captured `SET STATISTICS XML ON` for a `FOR SYSTEM_TIME BETWEEN ...`
      query with a sargable predicate on an indexed column. The plan is a
      `Concatenation` (UNION ALL) of the two tables exactly as the checklist's
      own text claimed - confirmed, not assumed - and with no matching index
      on the history side, the current-table branch does a genuine
      `Index Seek` (nonclustered index + clustered key lookup) while the
      history-table branch does a `Clustered Index Scan` of the whole table.
      Adding a structurally matching index to the history side (same key
      columns, same order) restores a seek on that branch too - the SAME
      probe, both directions, both oracle-confirmed with real captured plan
      XML, not inferred from one side alone.
      <br><br>
      **Match criterion, oracle-decided rather than assumed:** a third probe
      (a 2-column composite key, `(Region, Code)` on the current side vs.
      `(Code, Region)` - REVERSED order - on the history side, both columns
      bound by equality) found that a reversed key order CAN still produce a
      seek on both branches when every key column is bound by equality. Key-
      column order is nonetheless treated as SIGNIFICANT (a reversed-order
      history index does not count as a match) - the conservative, safe
      direction for a catalog-only finding making no claim about any one
      query's own predicate shape: a reversed-order index is not guaranteed to
      rescue a predicate that supplies only the current index's own leading
      column(s), which is the common, load-bearing case this rule exists to
      catch. A false negative here (never firing) would hide a real risk; the
      false positive this choice accepts instead (flagging a reversed-order
      index some specific full-equality query would in fact seek through) is
      the safe direction to be wrong in, and is stated explicitly in the
      finding's own doc comment rather than left implicit.
      <br><br>
      **PRIMARY KEY/UNIQUE-constraint indexes are never compared - oracle-
      confirmed structurally impossible on the history side, not a scope
      gap:** `ALTER TABLE ... ADD CONSTRAINT PRIMARY KEY`/`... ADD CONSTRAINT
      UNIQUE` against a real temporal history table both fail outright (Msg
      13558/13583) - a currently-valid history table can never carry either,
      by engine construction, so comparing the current table's own PK/unique-
      constraint index against it would be a guaranteed-always-fire signal
      with no possible fix. Only `CatalogIndexKind.Index` (an ordinary,
      non-constraint-backed index) is a candidate on the current side.
      Filtered/columnstore/disabled indexes are excluded on both sides,
      matching `CatalogTable.IsIndexedColumn`'s own "genuinely seekable"
      definition. Included columns and uniqueness are ignored in the match
      (oracle-confirmed neither affects seek-vs-scan, only covering-ness/cost)
      - only the ordered key-column list matters.
      <br><br>
      `TemporalTablePair`/`DatabaseCatalog.TemporalTablePairs` (populated live
      -only via a new `LiveCatalogReader.ReadTemporalTablePairsAsync` reading
      `sys.tables.temporal_type = 2` joined to its own `history_table_id`;
      always empty for a file-mode scan - no parsed representation of `WITH
      (SYSTEM_VERSIONING = ON (HISTORY_TABLE = ...))` exists anywhere in this
      codebase's DDL-parsing path, the same "everything goes via the
      database" reasoning `ForeignKeys`/`CheckConstraints` already follow).
      Both the current table and its history table are otherwise ordinary
      `CatalogTable` rows already carrying their own real `Indexes` - a
      history table has no distinct `sys.objects.type`, so no new index-
      reading plumbing was needed, only the pairing fact. New
      `TemporalTableHistoryIndexGapFinding`/`TemporalTableHistoryIndexGapScanner`
      (`src/SilentScan.Core/Predicates/TemporalTableHistoryIndexGapScanner.cs`)
      - catalog-only, no AST walking, mirrors `UntrustedConstraintScanner`'s
      own shape. Catalog-only, unconditional - reported once per current-side
      index lacking a history-side match, the same "reported once per object"
      precedent `MaxTypedColumnFinding` already establishes. Not verdict-
      bearing per finding (the oracle confirmation above is of the GENERAL
      mechanism, not a per-finding plan-XML probe against a real query site,
      the same tier `UntrustedConstraintFinding`/`ForcedSerialFinding` already
      use) - `Confidence.High`, SARIF `LevelWarning`.
      <br><br>
      Wired end-to-end (`ScanReport` schema version 27 → 28, SARIF rule
      `silentscan/catalog/temporal-history-index-gap`, readable-report
      section). Live-mode only, same reasoning as `CrossTableTypeDriftFindings`'s
      FK-linked half. Unit-tested (`TemporalTableHistoryIndexGapScannerTests`,
      12 cases: missing-index fires, matching index (name/included-columns-
      agnostic) never fires, reversed key order still fires, PRIMARY
      KEY/UNIQUE CONSTRAINT never compared, filtered/disabled history index
      not treated as a match, disabled/columnstore current index never a
      candidate, an unresolved table in a reported pairing skipped rather
      than throwing, multiple gaps on one table each get their own finding)
      and oracle-tested against a real deployed schema (`LiveCatalogReaderTests`,
      5 new cases: the pairing itself reads correctly off `sys.tables`, both
      sides read as ordinary tables with real indexes and the history side's
      own auto-created period-column clustered index is confirmed never
      mistaken for a match, the scanner fires on a genuine gap, stays silent
      on a genuinely matching pair, and never flags the current table's own
      PRIMARY KEY). **Real coverage against the local RM_ test database: 0
      findings** - a real, honest zero (`SELECT COUNT(*) FROM sys.tables
      WHERE temporal_type = 2` confirms 0 system-versioned temporal tables
      exist there at all), not a detection gap; the mechanism itself is
      oracle-proven end-to-end and the scanner correctly fires on every hand-
      authored fixture in the unit-test suite and the live oracle fixture.

### Small precise adds (each an afternoon, not a stream)
- [x] **Proc authored `WITH RECOMPILE` — shipped**: pure catalog flag
      (`sys.sql_modules.is_recompiled`), read live-only via
      `LiveModuleReader`/`LiveScanRunner` (`DatabaseCatalog
      .AddModuleIsRecompiled`/`TryGetModuleIsRecompiled`, same "baked in
      wholesale at CREATE/ALTER time" shape as `UsesQuotedIdentifier`/
      `UsesAnsiNulls`). New `ModuleCompileFlagFinding`/`ModuleCompileFlagScanner`
      (`src/SilentScan.Core/Predicates/ModuleCompileFlagFinding.cs`,
      `.../ModuleCompileFlagScanner.cs`) — module-level, no AST walk needed
      beyond the module's own CREATE/ALTER position for `Line`/`Column`, same
      shape as `SetOptionScanner`'s catalog-flag half. Purely informational:
      "compiles every call, invisible to `sys.dm_exec_cached_plans`/
      `sys.dm_exec_query_stats`" is a documented, unconditional mechanism, no
      oracle needed for the claim itself. Wired end-to-end (`ScanReport`
      schema version 28 → 29, SARIF rule `silentscan/catalog/with-recompile`,
      readable-report section). Unit-tested
      (`ModuleCompileFlagScannerTests`) + real end-to-end oracle coverage via
      `EngineAuthoritativeScan` against the disposable Docker instance
      (`ModuleCompileFlagPipelineTests`: a real deployed `WITH RECOMPILE` proc
      fires, a plain proc doesn't). **Real coverage against the local RM_ test
      database: 2 findings** (`dbo.spRIL_AdvancedAdhoc`,
      `dbo.spRIL_AdvancedAdhoc2`).
- [x] **`inline_type`/`is_inlineable` parity check against the shipped
      scalar-UDF stream's blocker list — done, found already substantially
      wired, closed the remaining gap.** Investigated before assuming this was
      unbuilt: `LiveCatalogReader.ReadTSqlScalarUdfInfoAsync` already reads
      `sys.sql_modules.is_inlineable` live (`ScalarUdfInfo.EngineIsInlineable`)
      and `ScalarUdfInlineabilityClassifier.Classify` already PREFERS it over
      the static blocker scan unconditionally — the architecture this item
      asked for already existed, so "any disagreement is a bug" cannot
      literally occur: the engine flag always wins, the static scan's own text
      only ever explains. What remained undone was the actual **parity
      measurement** the item asked for. Ran it directly against the local RM_
      test database: of 193 distinct scalar UDFs, **9 were `NotInlineable`
      with no blocker text at all** (`InlineabilityBlocker: null`) — the
      static closed list found nothing to explain the engine's own verdict.
      Investigated each of the 9 directly against the real deployed function
      bodies (not guessed) and found two genuine, previously-unrecognized
      FROID blockers, each oracle-confirmed on the Docker instance before
      being added to the closed list in `ScalarUdfInlineabilityScanner`:
      * **`GOTO`/label usage** (2 of the 9: two sibling functions sharing a
        documented "keep these five UDFs in sync" comment block, both using
        `GOTO END_OF_FUNCTION` as an early-exit). Oracle-confirmed directly: a
        function with a real `GOTO`/label pair shows `is_inlineable = 0`; the
        identical IF/SET control-flow shape with the `GOTO` removed shows
        `is_inlineable = 1` — isolating `GOTO` itself, not the surrounding
        `IF`, as the blocker.
      * **A `SELECT @v = expr(@v) FROM t` running-accumulator assignment**
        (7 of the 9: the real string-concatenation-aggregate idiom production
        code uses in place of `STRING_AGG`/`FOR XML PATH`, e.g. `SELECT
        @s = COALESCE(@s + ',', '') + col FROM t`). Oracle-confirmed
        directly: this shape shows `is_inlineable = 0`; the same FROM-clause
        `SELECT` assignment WITHOUT reading the target variable's own prior
        value (`SELECT @v = col FROM t`) shows `is_inlineable = 1` — isolating
        the self-reference, not the FROM clause itself, as the blocker.
      Both added to `ScalarUdfInlineabilityScanner` (`GoToStatement`/
      `LabelStatement` visitor; a `QuerySpecification` visitor checking every
      `SelectSetVariable` element for a self-referencing `Expression` when a
      `FromClause` is present). **After adding both, re-measured: 0 of the 193
      functions remain unexplained** — full parity achieved, not just a
      partial improvement. Tested: 6 new unit tests (`ScalarUdfScannerTests`,
      `ScalarUdfInfoTests` — fires/near-miss pairs for both new patterns,
      isolating each blocker from its surrounding control flow) + 3 new live
      oracle tests (`LiveCatalogReaderScalarUdfTests` — real deployed
      functions, `EngineIsInlineable` checked directly). `detection-
      reference.md` Appendix 3 updated with both newly-confirmed blockers.
- [x] **`uses_database_collation` — shipped, scope corrected from the
      checklist's own original premise once oracle-investigated.** The
      original framing ("marks a schema-bound module whose correctness
      depends on the database collation") turned out to be a half-truth:
      oracle-probed directly (Docker instance, real schema-bound objects) and
      found `uses_database_collation` is set to **1 for every schema-bound
      object unconditionally** — even a pure-arithmetic `WITH SCHEMABINDING`
      scalar function with an `INT` parameter and `INT` return, zero string
      columns anywhere, still sets it. Schema-binding's own identifier-
      resolution mechanism depends on the database's collation for
      case-insensitive name matching regardless of data type, so the flag
      carries **no differentiating signal** for a schema-bound object — it is
      definitionally always true there, and reporting on it would be a
      redundant, always-true, zero-information finding. Real measurement on
      the local RM_ test database confirmed this exactly: of 29 modules with
      the flag set, only 2 are schema-bound (both scalar functions) — the
      other **27 are non-schema-bound multi-statement table-valued
      functions**. Investigated those 27 directly and found the real,
      narrower, genuinely informative mechanism: a `RETURNS @t TABLE(...)`
      declaring a character column with **no explicit `COLLATE`** clause has
      that column's collation implicitly resolved against the database's
      default at CREATE/ALTER time and baked in — oracle-confirmed precisely
      (three-way probe on the same Docker instance): an `INT`-only return
      table never sets the flag; a `VARCHAR` return column with no `COLLATE`
      sets it; the identical shape with an explicit `COLLATE` on that same
      column does not. **Shipped scope**: fires only for a non-schema-bound
      module with `uses_database_collation = true` — the schema-bound case is
      deliberately excluded (see `ModuleCompileFlagFinding`'s own doc comment
      for the full reasoning), which is what isolates the real, narrow,
      correctly-scoped claim: this TVF's own returned string data will
      silently disagree with the database's default collation the moment a
      future `ALTER DATABASE ... COLLATE` changes what that default IS,
      since the function's own already-compiled return shape is never told.
      Same finding type/scanner as the WITH RECOMPILE item above
      (`ModuleCompileFlagFindingKind.TableValuedFunctionReturnUsesDatabaseCollation`).
      SARIF rule `silentscan/catalog/tvf-return-database-collation`. Tested:
      unit tests covering the schema-bound-exclusion guard directly + 5 live
      oracle tests via `EngineAuthoritativeScan` (`ModuleCompileFlagPipelineTests`
      — un-COLLATE'd TVF fires, explicitly-COLLATE'd TVF doesn't, INT-only TVF
      doesn't, schema-bound function doesn't despite the flag being true).
      **Real coverage against the local RM_ test database: 27 findings**, all
      multi-statement table-valued functions.
- [x] **`RANGE` instead of `ROWS` in window-function frames — shipped, scope
      corrected from the checklist's own "on-disk spool per partition" premise
      once oracle-investigated.** Probed directly (Docker instance, a
      5,000-row seeded table, `SET STATISTICS XML ON` against real
      executions, repeated with and without duplicate `ORDER BY` values to
      isolate peer-group ties from plain row cost): an equivalent `ROWS`
      frame and a `RANGE` frame produce the IDENTICAL `PhysicalOp="Window
      Spool"` operator — there is no on-disk-vs-not distinction between the
      two at the physical-operator level, so the checklist's original framing
      does not survive contact with the oracle. The real, reproduced
      differentiator: the `Window Spool` operator's own `ActualCPUms` was
      measured at roughly 4x higher for `RANGE` than the equivalent `ROWS`
      frame across repeated runs against identical data (peer-group
      value-comparison cost `ROWS`'s pure physical-offset counting doesn't
      pay) — real and reproducible, just not the mechanism originally
      claimed. **Second oracle finding, load-bearing for scope:** a window
      function's `OVER` clause with an `ORDER BY` and NO explicit frame
      clause at all silently defaults to `RANGE BETWEEN UNBOUNDED PRECEDING
      AND CURRENT ROW` (T-SQL's own documented default) — confirmed to carry
      the identical measured cost as writing `RANGE` explicitly, so this
      finding fires on both the explicit and the invisible-in-source-text
      implicit case (`WindowFrameFindingKind.ExplicitRangeFrame`/
      `ImplicitDefaultRangeFrame`), not just the explicit keyword. Fully
      syntax-only (`WindowFrameFinding`/`WindowFrameScanner`,
      `src/SilentScan.Core/Predicates/WindowFrameScanner.cs`) — no catalog
      dependency, both kinds visible directly from the AST
      (`OverClause.WindowFrameClause`). `FindingConfidence.High`, SARIF
      Warning, the same "structural risk, not provably-wrong-result" tier
      `ForcedSerialFinding`/`CatchAllPredicateFinding` use. Wired end-to-end
      (`ScanReport` schema version 29 → 30, SARIF rules
      `silentscan/window-frame/{explicit-range,implicit-default-range}`,
      readable-report section). Unit-tested (`WindowFrameScannerTests`, 6
      cases: explicit ROWS never fires, explicit RANGE fires, ORDER BY with
      no frame clause fires as implicit-default, no ORDER BY at all never
      fires — a frame clause is syntactically illegal without one so this can
      never occur in valid SQL, `ROW_NUMBER()`-style ranking functions with
      no frame support still fire on the shape since it's about the `OVER`
      clause's own AST, not per-function semantics, and multiple window
      functions in one query are each reported independently). **Real
      coverage against the local RM_ test database: 225 findings, all
      `ImplicitDefaultRangeFrame`** — every real window function in this
      corpus that specifies `ORDER BY` leaves the frame clause unwritten
      entirely (the invisible-in-source-text default), rather than spelling
      out `RANGE` explicitly; 0 explicit `RANGE` frames, confirming the
      implicit-default half of this rule is the one carrying the real
      finding volume here, not a rarely-hit edge case.
- [x] **Trigger content scan — investigated and closed: already fully done, no
      code needed.** `LiveModuleReader.ReadReadableModulesAsync`'s own module
      query already includes `o.type IN ('V', 'P', 'FN', 'TF', 'IF', 'TR')` —
      `'TR'` (trigger) has been read alongside every other module kind since
      before this item was ever queued. `LiveScanRunner` parses every
      module's text uniformly via `SqlScriptParser.ParseText` with no
      type-based filtering anywhere downstream — a trigger body flows through
      exactly the same `ScanReportBuilder.BuildFromParseResults` pipeline
      every proc/view/function body does. Confirmed empirically against the
      local RM_ test database rather than just read from the code: 51 real
      triggers exist there, and cross-referencing every finding stream's own
      `SourcePath` against those 51 trigger names shows 46 of them (90%)
      already appear as the source of a real finding today — 60
      `ScalarUdfFindings`, 43 `LocalVariablePredicateFindings`, 24
      `Tier1Findings`, 22 `ExpressionDerivedFindings`, 11 `TypedFindings`, 9
      `ForcedSerialFindings` (cursors/table-variable-modification cost,
      exactly the "hidden per-DML cost" this item's own text named), 11
      `PostExpansionJoinWidthFindings`, 3 `DynamicSqlFindings`, 3
      `WriteLossFindings`, 3 `PartialCompositeForeignKeyJoinFindings`, and
      219 honestly-ledgered `SkippedConstructs`. Nothing was silently
      excluded; this item's premise was already satisfied by the existing
      module-discovery query, and no new wiring, scanner, or test was
      needed.
- [x] **`COMPUTE`/`COMPUTE BY` deprecated aggregate constructs — closed, not
      reachable through this tool's own parser dialect, confirmed empirically
      rather than assumed.** Probed directly against `TSql160Parser` (the
      exact parser class `SqlScriptParser` uses) with `SELECT OrderId, Amount
      FROM Orders ORDER BY OrderId COMPUTE SUM(Amount) BY OrderId;` — a hard
      parse error ("Incorrect syntax near 'OrderId'"), not a distinct AST node
      shape to pattern-match. This syntax was removed from the engine's own
      grammar entirely (deprecated since SQL Server 2012, and ScriptDOM's
      modern-compat parser follows the same grammar), the identical situation
      this checklist already found and closed for the `*=`/`=*` outer-join
      operators under "Schema-scan UDF and computed-column findings" above. A
      real corpus file using this syntax would already fail to parse as a
      whole, surfaced via the existing `ParseHealthReport`/dialect-sniffing
      machinery as a parse error — a stronger, more honest signal than a
      targeted rule that can never fire. Closed exactly as stated, no code
      written.
- [x] **`WAITFOR DELAY`/`WAITFOR TIME` inside a routine or batch — shipped**:
      new `WaitForFinding`/`WaitForScanner`
      (`src/SilentScan.Core/Predicates/WaitForScanner.cs`) — fully
      syntax-only, a direct AST match on `WaitForStatement.WaitForOption`
      (`Delay`/`Time`). `WAITFOR (RECEIVE ...)` (Service Broker's own
      `WaitForOption.Statement`) is deliberately never matched — a distinct,
      legitimate blocking-wait idiom with its own `TIMEOUT` option, not a
      timer sleep with nothing to justify the wait. No oracle needed: a
      blocked worker thread is a documented, unconditional SQL Server
      mechanism, not a plan-shape claim. Both DELAY (relative) and TIME
      (absolute) report identically — the risk story doesn't differ — but the
      finding also tracks whether the `WAITFOR` is reachable inside an open
      `BEGIN TRANSACTION`/no-matching-`COMMIT`-or-`ROLLBACK`-yet span in the
      same batch's own straight-line reading order (a structural signal, not
      real control-flow analysis — a `WAITFOR` reachable only through a
      branch where the transaction was already closed on that path is not
      disambiguated, the same class of documented imprecision
      `SelfReferencingDmlScanner` already accepts for its own alias-reuse
      case), since a WAITFOR inside an open transaction holds that
      transaction's locks for the same duration, a sharper claim than the
      bare worker-idle one. `FindingConfidence.High`, SARIF Warning. Wired
      end-to-end (`ScanReport` schema version 30, SARIF rule
      `silentscan/control-flow/waitfor`, readable-report section).
      Unit-tested (`WaitForScannerTests`, 7 cases: DELAY fires, TIME fires,
      inside an open transaction flags `IsInsideTransaction`, after a
      COMMIT/ROLLBACK does not, plain SELECT never fires, `WAITFOR (RECEIVE
      ...)` never fires). **Real coverage against the local RM_ test
      database: 0 findings** — a real, honest zero (this codebase's own
      T-SQL apparently doesn't use `WAITFOR DELAY`/`WAITFOR TIME` anywhere
      across 4,987 modules), not a detection gap; the scanner correctly
      fires on every hand-authored fixture in the unit-test suite.
- [x] **Transaction hygiene pair — first half shipped, second half
      investigated and descoped.** `BEGIN TRANSACTION` with no reachable
      `COMMIT`/`ROLLBACK` on some path — a real, sound reachability walk over
      the module body's control-flow AST (IF/ELSE, TRY/CATCH, WHILE,
      BEGIN/END, RETURN/THROW), not a heuristic text scan.
      `TransactionHygieneFinding`/`TransactionHygieneScanner`
      (`src/SilentScan.Core/Predicates/TransactionHygieneScanner.cs`) visits
      only procedure and trigger bodies — a function structurally cannot
      contain `BEGIN TRANSACTION` at all, oracle-confirmed directly (Msg
      443, "Invalid use of a side-effecting operator 'BEGIN TRANSACTION'
      within a function") rather than assumed. **Oracle-confirmed the
      underlying mechanism directly, load-bearing for the finding's own
      wording:** a real deployed procedure whose only path opens a
      transaction and `RETURN`s without resolving it leaves the CALLING
      session's `@@TRANCOUNT` elevated by one, and SQL Server itself raises
      its own diagnostic the instant such a procedure returns — Msg 266,
      "Transaction count after EXECUTE indicates a mismatching number of
      BEGIN and COMMIT statements" — confirmed for both the bare-RETURN
      shape and the classic real-world "TRY commits, CATCH never rolls
      back" shape; a correctly try/catch-wrapped procedure never leaves
      `@@TRANCOUNT` elevated, oracle-confirmed the same way
      (`TransactionHygieneOracleTests`, 3 tests).
      <br><br>
      **Known v1 scope limits, stated honestly (the scanner's own doc
      comment states each precisely):** only ONE currently-open `BEGIN
      TRANSACTION` is tracked at a time — a second one found while already
      tracking one declines the whole enclosing scope rather than guessing
      which resolves which; any `GOTO` anywhere in the module body declines
      the WHOLE module's analysis (an arbitrary jump target defeats a
      structural walk without a real labeled-block CFG); a `CATCH` block is
      analyzed as entering with whatever state existed at the START of its
      own `TRY`/`CATCH` construct — **sound, not merely conservative**: an
      error inside `TRY` can occur at literally the first statement, so this
      is itself a real, statically reachable entry state for `CATCH`, never
      an over-claim (the complementary gap — a transaction opened INSIDE its
      own `TRY` block is not cross-checked into that `TRY`'s own `CATCH` —
      is a real under-report, never a false positive); a `WHILE` loop body
      is analyzed as running exactly one representative iteration, OR-merged
      with the "ran zero times" possibility; no cross-procedure tracking,
      matching the SET-options stream's own identical "no proc-call-
      transitive walk" limit for the same reason.
      <br><br>
      **Second half ("lengthy work between an error and its ROLLBACK")
      investigated and explicitly NOT built** — no genuinely precise,
      non-magnitude-guessing static claim survived independent of the first
      half: the only structurally sound signal (a loop or external call
      appearing between a `CATCH` block's own entry and its `ROLLBACK`)
      still can't distinguish "long-running" from "trivial" without
      guessing at row counts or call latency this static pass cannot see,
      and the real, provable defect underneath it — the transaction being
      left open on some path at all — is exactly what the shipped first
      half already reports, at real oracle-confirmed severity (Msg 266), not
      a softer "held longer than ideal" claim. Descoped explicitly rather
      than forcing a weak rule into existence, the same honest-partial-
      shipping precedent the ARITHABORT drop and the CHECK-constraint
      origin-tracking deferral already set elsewhere in this file.
      <br><br>
      Not verdict-bearing — a correctness/robustness finding, not a
      plan-shape one, `FindingConfidence.High`, SARIF Warning (the same
      "structural risk" tier `ForcedSerialFinding`/`WaitForFinding` already
      use, not the `LevelError` correctness tier — this defect is a leaked
      lock/session-state condition, not a wrong row set). Wired end-to-end
      (`ScanReport` schema version 30 → 31, SARIF rule
      `silentscan/control-flow/unresolved-transaction`, readable-report
      section). Version-insensitive: `@@TRANCOUNT` bookkeeping is ANSI/
      T-SQL session-state semantics, unaffected by compat level or CE mode.
      Unit-tested (`TransactionHygieneScannerTests`, 19 cases: unresolved
      fall-off-end, resolved COMMIT/ROLLBACK, RETURN while open, IF/ELSE
      both-resolve and implicit-else-leaks, TRY/CATCH both-resolve and
      CATCH-never-rolls-back and CATCH-throws, WHILE zero-iteration leak and
      unconditional-commit-after-loop clean, nested-BEGIN-TRAN decline,
      GOTO decline, no-transaction-at-all, `SAVE TRANSACTION` not
      resolving the outer one, sequential independent transactions, and a
      trigger body firing the identical shape). **Real coverage against the
      local RM_ test database: 383 findings.** Spot-checked one directly
      against the real deployed module text (`dbo.spAbusePointsDelete`):
      `BEGIN TRANSACTION` at line 28, a validation `IF NOT EXISTS(...) ...
      RAISERROR(...)` at line 55-58 that sets an error flag WITHOUT
      terminating the batch (`RAISERROR` at severity 16 does not stop
      execution), `IF @Error = 0 COMMIT TRANSACTION` at line 80-81 (no
      `ELSE`), and an unconditional `RETURN @Error` at line 83 — confirming
      the scanner's own reported anchor lines (28 → 83) exactly, and
      confirming this is a genuine, real production bug: any call that hits
      the validation failure path leaves the transaction open indefinitely
      on return, precisely the shape `TransactionHygieneOracleTests` proves
      the engine itself flags with Msg 266.
- [x] **`TOP(100) PERCENT` ignored by the optimizer** and **`ORDER BY` in a
      view / inline TVF — shipped together as one finding type, because T-SQL
      structurally cannot separate them.** Oracle-checked first (Docker
      instance, real seeded data): a bare `ORDER BY` with no
      `TOP`/`OFFSET`/`FOR XML` in a view/inline TVF is a hard compile error
      (Msg 1033, "The ORDER BY clause is invalid in views, inline functions,
      derived tables, subqueries, and common table expressions, unless TOP,
      OFFSET or FOR XML is also specified") — so "ORDER BY in a view" only
      ever occurs already paired with a `TOP`/`OFFSET`, meaning the
      checklist's two items describe the same shape from two angles, not two
      independent ones. **Oracle-confirmed the core claim directly**: a real
      view with `TOP (100) PERCENT ... ORDER BY Amt DESC`, queried via
      `SELECT TOP 5 * FROM theView` with no outer `ORDER BY`, returned rows in
      the base table's own storage order, not `Amt DESC` — the view's
      internal ordering was silently discarded entirely, since `TOP (100)
      PERCENT` never excludes a single row and so cannot even be defended as
      "the ORDER BY decided which rows survived." A second probe (a view with
      a genuine row-limiting `TOP (10) ... ORDER BY`) found the weaker,
      related case: the ORDER BY DOES decide which rows survive there, but
      the FINAL output order of the surviving rows sometimes still *appeared*
      ordered to the consumer, purely as a side effect of the chosen plan
      shape (SQL Server often reuses the same sort it needed internally to
      compute the TOP) — a real, undocumented, plan-dependent coincidence,
      never a guarantee, which is why this shipped as the deliberately
      *weaker*, lower-confidence half of the same finding rather than a
      second instance of provable meaninglessness. New
      `ViewOrderingFinding`/`ViewOrderingScanner`
      (`src/SilentScan.Core/Predicates/ViewOrderingScanner.cs`,
      `ViewOrderingFindingKind.{TopPercentOrderByNeverLimits,
      OrderByNotGuaranteedToConsumer}`) — fully syntax-only, only a
      view's/inline TVF's own OUTERMOST query is inspected (a top-level
      `UNION`/`EXCEPT`/`INTERSECT` declines rather than guessing which
      branch's `TOP`/`ORDER BY` matters, the same discipline
      `SelectStarViewScanner.FindOutermostStarLine` already established for
      the identical class of view-body-shape rule). `TopPercentOrderByNeverLimits`:
      `FindingConfidence.High`, SARIF Warning (the meaninglessness is
      provable). `OrderByNotGuaranteedToConsumer`: `FindingConfidence.Low`,
      SARIF Note (purely informational — this pass cannot see whether any
      real consumer relies on the unguaranteed order, the same no-magnitude-
      claim tier `CascadingForeignKeyFinding`/`LocalVariablePredicateFinding`
      use for their own reason). **Known v1 scope limit, deliberate:**
      matches the checklist's own explicit "view / inline TVF" scope — a
      derived table/subquery/CTE using the identical trick (the same Msg 1033
      grammar rule applies to those too) is a real, structurally identical
      relative left unanalyzed rather than silently widened past what was
      asked for; a multi-statement TVF's own `RETURNS @t TABLE(...)` body has
      no single outermost query to inspect the same way, so it is never a
      candidate. Wired end-to-end (`ScanReport` schema version 30, SARIF
      rules `silentscan/view/{top-percent-order-by-no-op,
      order-by-not-guaranteed}`, readable-report section). Unit-tested
      (`ViewOrderingScannerTests`, 12 cases: `TOP (100) PERCENT` fires as
      never-limits, `TOP (10)`/`TOP (50) PERCENT`/`OFFSET...FETCH` all fire as
      not-guaranteed, no `ORDER BY` never fires, CREATE/ALTER/CREATE OR ALTER
      VIEW all fire, an inline TVF fires, a multi-statement TVF never fires, a
      scalar function never fires, a plain top-level `SELECT` (not in a view/
      function) never fires, a top-level `UNION` declines). **Real coverage
      against the local RM_ test database: 3 findings, all
      `TopPercentOrderByNeverLimits`** — the provably-meaningless case; 0
      instances of the weaker `OrderByNotGuaranteedToConsumer` shape, a real,
      honest result given how rare a genuinely row-limiting `TOP` combined
      with `ORDER BY` inside a view/inline TVF is in this corpus.
- [x] **`IF` statements containing queries inside a procedure — investigated
      and closed, not built.** Proposed premise (`detection-reference.md`
      §7.2 `SRD0063`, "estimation and recompile consequences"): a query inside
      an `IF`/`ELSE` branch not taken at runtime still costs something at
      compile time, because the whole procedure is compiled/cached as one
      unit. Probed directly against the Docker oracle (a real 2-branch
      procedure, one branch a `COUNT(*)`, the other a `GROUP BY` aggregate) —
      **the premise is false.** A real executed plan (`SET STATISTICS XML
      ON`) for a call that takes branch A contains ONLY that branch's own
      operators — no trace of the untaken branch B's query at all. Confirmed
      further via `sys.dm_exec_cached_plans`: SQL Server compiles/caches each
      individual statement inside a procedure lazily, the first time that
      statement is actually reached at runtime (documented "deferred
      compilation" behavior) — an untaken `IF` branch's query is never
      compiled at all until (if ever) that branch actually executes, so there
      is no compile-time cost specific to "having multiple `IF` branches with
      queries" distinct from ordinary per-statement compilation. Nothing
      survives that is both true and a performance finding distinct from
      normal query compilation, so this is **not** being built — the same
      "proposed and killed the same session" discipline the "Non-foldable
      nondeterministic intrinsic in a predicate" item above already models.
      Recorded here rather than silently dropped because the value is the
      falsification, not a verdict.
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
- [x] **Explicit-length audit of `CAST`/`CONVERT` to a string type — shipped,
      exactly as scoped: no new finding type, a type-resolution fix that lets
      the existing under-length/oversized-parameter rules see the real
      truncation.** Oracle-confirmed directly (never assumed from
      documentation) that an unsized `CAST`/`CONVERT` to EVERY string- and
      binary-family type — `CHAR`/`VARCHAR`/`NCHAR`/`NVARCHAR`/`BINARY`/
      `VARBINARY` — truncates to exactly 30 characters, not the bare-
      `DECLARE`'s own length-1 default (`UnderLengthParameterFinding`'s own
      subject). Root cause found in `SqlTypeReferenceResolver.Resolve`
      (`src/SilentScan.Core/Parsing/SqlTypeReferenceResolver.cs`): an unsized
      string/binary type resolved `Length: null` regardless of WHICH caller
      asked — correct for a `DECLARE`/column declaration (where `null` means
      "T-SQL will default this to length 1," already interpreted specially
      by `TryAddUnderLengthParameterFinding`'s `IsImplicitDefault` flag), but
      silently wrong for a `CAST`/`CONVERT` target type, whose real default
      is a different number entirely. Fixed by threading a new optional
      `unsizedStringOrBinaryDefaultLength` parameter through `Resolve`
      (defaults to `null`, unchanged for every other existing caller — a
      DDL/DECLARE resolution), passed as `30` from the two call sites that
      actually resolve a `CAST`/`CONVERT` target type:
      `TypedPredicateExtractor.ResolveCastOrConvertOperand` (the one that
      matters for this item — a `CAST`/`CONVERT` appearing directly as a
      predicate comparison operand) and `ExpressionTypeInferencer`'s
      `CastCall`/`ConvertCall` branches (the nested case, e.g. inside a CASE
      or arithmetic expression). Once `Length` resolves to a real `30`
      instead of `null`, `TryAddUnderLengthParameterFinding`/
      `TryAddOversizedParameterFinding` need no changes at all — they
      already compare a resolved length against the column's own, so a
      genuinely narrower-than-30 column now correctly fires
      `UnderLengthParameterFinding` (`IsImplicitDefault: false`, since 30 is
      a real resolved length, not "no length at all"), and a column narrower
      than the CAST/CONVERT's own effective 30 correctly fires
      `OversizedParameterFinding` instead — sharing the existing rules'
      comparison and reporting path exactly as this item's own text
      instructed, with no new finding type.
      <br><br>
      **Correction to a genuine, unverified false-positive risk in the
      PREVIOUS `Length: null` behavior, found while implementing this item:**
      before this fix, `TryAddUnderLengthParameterFinding`'s
      `isImplicitDefault = otherType.Length is null` read TRUE for every
      unsized `CAST`/`CONVERT` operand, and the surrounding logic never
      early-returns when `isImplicitDefault` is true — so EVERY `CAST`/
      `CONVERT`-vs-column comparison in the same string category fired as
      "implicit default" (misleadingly implying a length-1 truncation risk)
      regardless of whether the column was actually narrower than
      `CAST`/`CONVERT`'s real 30-character default. A column ≤ 30 characters
      wide being compared against an unsized `CAST`/`CONVERT` was a real,
      if minor, false positive under the old resolution — now correctly
      never fires (30 ≥ column length is a genuine non-risk, matching the
      already-shipped oversized/under-length rules' own symmetric logic).
      <br><br>
      Oracle-confirmed via real execution, not compile-only (the underlying
      claim is a runtime truncation, the same class of finding
      `UnderLengthParameterOracleTests` already covers):
      `CastConvertUnsizedLengthOracleTests` (3 tests) — the 30-character
      default confirmed directly for `VARCHAR`/`NVARCHAR` and, separately,
      for all six string/binary-family types at once; and a real seeded
      row/query execution showing a column wide enough to hold a 35-
      character value never matches a predicate that routes the comparison
      value through an unsized `CONVERT` first (silently truncated to 30
      before the comparison runs), while the identical query through an
      explicitly `CONVERT(VARCHAR(40), ...)` correctly matches. Structural
      unit tests in `TypedPredicateExtractorTests` (5 new cases: unsized
      `CONVERT`/`CAST` vs. a wider column fires `UnderLengthParameterFinding`
      at length 30 with `IsImplicitDefault: false`, a column already
      narrower than 30 never fires, an EXPLICITLY sized `CONVERT(VARCHAR(10),
      ...)` uses the real 10 rather than 30, and an unsized `CONVERT` vs. a
      column narrower than 30 fires `OversizedParameterFinding` instead).
      Full suite green (3,006 tests) after landing, no regressions from the
      resolver change.
      <br><br>
      **Real coverage against the local RM_ test database: 1 genuine finding**
      (`dbo.spAuditOnboardDeviceActivity4`,
      `RelatedObjectInstanceStr = CAST(@ConversationOwner as varchar)`
      against a 255-character column) — confirmed directly against the real
      deployed module text, not inferred from the finding's own numbers
      alone: a raw-text sweep found 94 modules loosely containing an unsized
      `CONVERT(VARCHAR,`/`CONVERT(NVARCHAR,`/`CONVERT(CHAR,`/`CONVERT(NCHAR,`
      call anywhere (most in SELECT-list projections or display formatting,
      not predicate comparisons this rule targets), and cross-checking every
      one of the 256 real `UnderLengthParameterFinding`/
      `OversizedParameterFinding` results against its own reported source
      line found exactly this one genuine `CAST`-in-a-predicate match — the
      other apparent length-30 coincidences in that same result set turned
      out, on inspection of the real source, to be an unrelated parameter
      explicitly declared `VARCHAR(30)`, not a `CAST`/`CONVERT` at all. A
      real, small, honest number for a narrow defect, not evidence the rule
      is inert - matching this file's own "coverage-empty/low-volume is a
      real result, not a broken rule" precedent set by several other shipped
      streams.

### Hint and index-shape catalog checks — shipped
Folded in from the incumbent-catalog read (`detection-reference.md` Appendix
7) — both need our catalog and neither is done properly anywhere surveyed.

- [x] **Composite index leading-column violation — shipped.** A real composite
      index exists on the table, the query genuinely constrains one of its
      NON-leading key columns via a real AND-reachable comparison, but the
      index's own leading key column is not referenced anywhere in the
      statement at all — a composite index is a single B-tree keyed first by
      its leading column, so without a bound on that column this specific
      index structurally cannot be seek-used for this predicate. **Precision
      guard, load-bearing:** only fires when no OTHER usable index on the
      table leads with the same violating column either — a table with a real
      alternative seek path is never flagged, keeping this a genuine "this
      query cannot seek THIS index" claim rather than index-advisor noise.
      `CompositeIndexLeadingColumnFinding`/`CompositeIndexLeadingColumnScanner`
      (`src/SilentScan.Core/Predicates/CompositeIndexLeadingColumnScanner.cs`)
      — pure catalog+AST, reuses `FromScopeResolver`/`ScalarExpressionResolver`
      (the same machinery `PartialCompositeForeignKeyJoinScanner`/
      `CatchAllPredicateScanner` already use) plus a local `FlattenAnd` for
      AND-only-reachable comparisons (a column bound only inside an OR branch
      never counts as constraining) and a deliberately liberal
      `ColumnReferenceCollector` (referenced ANYWHERE, including inside OR,
      counts toward suppressing — this set only ever suppresses, never
      triggers, so being liberal is the safe direction). No plan-XML oracle
      needed: the b-tree-prefix mechanism is architectural, not
      cardinality-dependent, provable directly from `CatalogIndex.KeyColumns`'
      own ordering. Deliberately scoped `SELECT`/`UPDATE`/`DELETE` only
      (`MERGE`'s own `ON`/`USING` shape is out of v1 scope, the same reasoning
      `CatchAllPredicateScanner`/`PartialCompositeForeignKeyJoinScanner`
      already gave theirs), base-table-only, depth-0 predicates only.
      **Oracle discovery mid-implementation, caught against real corpus text,
      not just synthetic tests:** the collector's first version crashed with
      a `NullReferenceException` on a `COUNT(*)`-shaped wildcard
      `ColumnReferenceExpression` (no `MultiPartIdentifier`) nested inside a
      scalar subquery's own `WHERE` clause — `ScalarExpressionResolver
      .ResolveColumnReference` assumes a real column name is always present
      and has no guard of its own; fixed by skipping `ColumnType.Wildcard`
      nodes before ever calling it, in both this scanner and the index-hint
      scanner below, with a regression test locking in the exact shape that
      crashed. Unit-tested (`CompositeIndexLeadingColumnScannerTests`, 11
      cases: fires on the canonical shape with correct violating-column/
      position, never fires when the leading column is constrained, never
      fires when the leading column is merely referenced anywhere (including
      inside OR), never fires when the violating column itself is only
      OR-reachable, never fires when an alternative index leads with the same
      violating column, never fires on a single-column index, never fires on
      a filtered index, suppressed by a JOIN ON clause supplying the leading
      column, fires on UPDATE/DELETE, the wildcard-crash regression, and a
      3-column index reporting the correct later position). Wired end-to-end
      (`ScanReport` schema version 31 → 32, SARIF rule
      `silentscan/index-shape/composite-leading-column-unconstrained`,
      readable-report section). **Real coverage against the local RM_ test
      database: 1,330 findings** — spot-checked one directly against real
      deployed module text (`dbo.AddressListAllWithoutDependenciesSinceDate`,
      filtering `dbo.tblTrips` by `AgencyID` alone against
      `PK_tblTrips(ID, AgencyID)`) and confirmed a genuine true positive, the
      same multi-tenant "`AgencyID` appended to a composite key but never
      bound by the query" pattern this codebase's own partial-composite-FK-
      join stream already documented finding in this corpus independently.
- [x] **Hint validity against the catalog — shipped.** Two kinds, one finding
      type (`IndexHintFindingKind.{IndexDoesNotExist,HintedIndexNotSeekable}`):
      an `INDEX(...)` table hint naming an index that no longer exists in the
      catalog, or naming a real index whose own leading key column is never
      bound anywhere in the statement — the hint *requires* the engine use
      this specific index (T-SQL's own documented semantics, not a mere
      suggestion), so with no leading-column bound the optimizer cannot route
      around the missing prefix the way it normally would with no hint at
      all. **Oracle-confirmed directly (Docker instance, real seeded table,
      2,000 rows, `UPDATE STATISTICS ... FULLSCAN`), both claims proven, not
      assumed:** a nonexistent index name raises a hard compile error, Msg
      308 ("Index '...' does not exist"), every time — real value shipping
      it anyway, the same "names a provably-always-failing query with more
      precision than the raw engine message" reasoning
      `TempTableExecShapeFindingKind.ColumnCountMismatch` already used for an
      identical guaranteed-error case; hinting a real index whose leading
      column the query never touches degrades an otherwise-clean `Clustered
      Index Seek` to `Index Scan` + `Nested Loops`, while hinting the same
      index with its OWN leading column bound stays a clean `Index Seek` —
      confirming the leading-column binding, not the hint syntax itself,
      decides seek vs. scan. `IndexHintFinding`/`IndexHintScanner`
      (`src/SilentScan.Core/Predicates/IndexHintScanner.cs`) shares its
      "is the leading column bound anywhere" check with the composite-index
      scanner above (deliberately the same conservative, liberal-to-suppress
      test, generalized to single-column indexes too). **Known v1 scope
      limits, stated honestly:** only the identifier form of `WITH
      (INDEX(IndexName))` is inspected — the ordinal form (`INDEX(0)`/
      `INDEX(1)`) has no catalog name to resolve against and is declined
      rather than guessed at; `FORCESEEK`'s own optional index argument
      (`ForceSeekTableHint`, a distinct ScriptDom node) and `FORCESCAN` are
      related, similarly-scoped hint syntaxes deliberately left out of v1.
      Unit-tested (`IndexHintScannerTests`, 7 cases: nonexistent index fires,
      real index with unbound leading column fires, bound leading column
      never fires, no hints never fires, ordinal `INDEX(0)` declines, an
      UPDATE target hint via the extended FROM form fires, a hint on a
      joined table with its leading column bound by the JOIN's own ON clause
      never fires). Oracle-tested (`IndexHintOracleTests`, 4 tests: Msg 308
      for a nonexistent index, a clean seek with no hint as the control, the
      scan degradation with an unbound leading column, and the seek staying
      intact with a bound leading column). Wired end-to-end (same schema
      bump as the composite-index stream, SARIF rules
      `silentscan/hint/{index-does-not-exist,index-not-seekable}`,
      readable-report section). **Real coverage against the local RM_ test
      database: 3 findings, all `HintedIndexNotSeekable`** (0
      `IndexDoesNotExist` — a real, honest zero) — spot-checked one directly
      against real deployed module text
      (`dbo.spCustomerListIDSelectionForm2`) and found a genuinely
      non-synthetic case: the author's own code comment reads "as a general
      rule INDEX HINTS are bad... in this case though I know this index is
      better as it eliminates the SORT required... It thinks it is going to
      reduce the row count but the data just isn't like that" — a deliberate,
      informed tradeoff (forcing a non-seeking scan specifically to avoid a
      sort elsewhere in the plan), not an accidental bug, confirming the
      finding's own structural claim is accurate even in a case where the
      developer already reasoned through the tradeoff themselves.

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

- [x] **`SELECT INTO` a temp table later joined/filtered with no index —
      shipped.** `UnindexedTempTableUsageFinding`/`UnindexedTempTableUsageScanner`
      (`src/SilentScan.Core/Predicates/UnindexedTempTableUsageScanner.cs`) —
      reuses `CatalogBuilder`'s own already-existing temp-table tracking
      directly: a `SELECT ... INTO #temp`'s inferred columns, and any later
      `CREATE INDEX` against the same scoped name, are already recorded on
      the same `CatalogTable` entry by the catalog pass everything else in
      this codebase reads — no new catalog plumbing needed, only a new AST
      pass over usage sites within the same proc/trigger/batch scope. Fires
      on two usage shapes: a JOIN operand (`QualifiedJoin`, either side), or
      the sole FROM-clause source under a WHERE clause (declines rather than
      guesses when more than one table is in scope, the same "no catalog
      column lookup to attribute an unqualified reference" discipline the
      cartesian-join stream below also uses). Oracle-confirmed the underlying
      cost claim directly (Docker instance, a 5,000-row seeded source table):
      an unindexed `#temp` joined to a real table produces
      `PhysicalOp="Hash Match"` reading the entire temp table via
      `PhysicalOp="Table Scan"` — no seek alternative exists at all without
      an index, independent of row count (SQL Server's own automatic
      temp-table statistics change cardinality ESTIMATES, never whether a
      seek is structurally possible). `FindingConfidence.Medium` — this pass
      cannot see the temp table's real row count, so a genuinely tiny
      unindexed temp table may never matter in practice, the same honesty
      `PartialCompositeForeignKeyJoinFinding` already applies for its own
      data-dependent risk. Deliberately scoped to `SELECT INTO` only, per
      the item's own title — `CREATE TABLE #temp` is a known, deliberate v1
      scope limit. Wired end-to-end (`ScanReport` schema version 32 → 33,
      SARIF rules `silentscan/temp-table/unindexed-{join-operand,where-filter}`,
      readable-report section). Unit-tested (`UnindexedTempTableUsageScannerTests`,
      5 cases: JOIN-operand fires, WHERE-filter fires, an index created
      afterward suppresses it, a temp table never used again never fires,
      `CREATE TABLE #temp` — not `SELECT INTO` — never fires). **Real
      coverage against the local RM_ test database: 22 findings** (12 JOIN
      operand, 10 WHERE-filtered) — spot-checked against real module text,
      genuine unindexed temp-table usages in real production procedures.
- [x] **`TRUNCATE TABLE` inside a `TRY` block with no matching `CATCH` —
      shipped, scope corrected from the item's own original framing once the
      real T-SQL grammar was checked.** Oracle-confirmed directly: a `BEGIN
      TRY` with no corresponding `BEGIN CATCH` at all is a hard PARSE ERROR
      (Msg 102) — TRY and CATCH are paired grammar, never independently
      optional, so "no matching CATCH" can never actually occur in valid
      T-SQL. The real, narrower shape: a CATCH block that SWALLOWS the error
      rather than propagating it — no `THROW`/`RAISERROR` anywhere in the
      CATCH block's own statement tree (including one nested inside an
      IF/BEGIN), an empty CATCH being the most extreme case.
      `TruncateSwallowedFinding`/`TruncateSwallowedScanner`
      (`src/SilentScan.Core/Predicates/TruncateSwallowedScanner.cs`), fully
      syntax-only. Oracle-confirmed the underlying mechanism directly (a real
      seeded FK-referenced table): `TRUNCATE` genuinely fails at runtime
      (Msg 4712) when a referencing FK exists, and when that failure lands
      inside a TRY whose CATCH is empty, execution continues as if the
      TRUNCATE had succeeded — no error surfaces to the caller, the
      referenced table's row count unchanged and no exception propagating.
      `FindingConfidence.High`, SARIF Warning — a structural risk (any
      TRUNCATE inside this shape can fail silently the instant a new FK
      reference is added, with zero change to the statement itself), not a
      claim about today's schema specifically. Wired end-to-end (`ScanReport`
      schema version 33, SARIF rule
      `silentscan/control-flow/truncate-swallowed-by-catch`, readable-report
      section). Unit-tested (`TruncateSwallowedScannerTests`, 7 cases: empty
      CATCH fires, a CATCH doing unrelated work but never throwing fires, a
      bare `THROW` never fires, `RAISERROR` never fires, a `THROW` nested
      inside an `IF` inside the CATCH still never fires, no TRUNCATE in the
      TRY never fires, a TRUNCATE outside any TRY never fires). **Real
      coverage against the local RM_ test database: 0 findings** — a real,
      honest zero (this codebase's own TRY/CATCH blocks around TRUNCATE
      apparently all propagate their errors correctly), not a detection gap;
      the mechanism itself is oracle-proven and the scanner correctly fires
      on every hand-authored fixture in the unit-test suite.
- [x] **`SET DATEFORMAT`/`SET DATEFIRST` changed mid-module — shipped.**
      Confirmed no baked-in `sys.sql_modules` column exists for either
      (unlike `QUOTED_IDENTIFIER`/`ANSI_NULLS`), so this is syntax-only, as
      originally expected. ScriptDom's own AST shape verified directly rather
      than assumed: both parse as `SetCommandStatement` containing a
      `GeneralSetCommand` with `CommandType` `DateFormat`/`DateFirst` — NOT a
      `PredicateSetStatement`, the node the existing SET-option stream reads
      (that node only carries the ON/OFF-style boolean options). New
      `SessionDateSettingFinding`/`SessionDateSettingScanner`
      (`src/SilentScan.Core/Predicates/SessionDateSettingScanner.cs`), fully
      syntax-only, one AST match per `SetCommandStatement`. Oracle-confirmed
      the real mechanism directly (Docker instance) rather than trusted from
      the item's own framing: the identical AMBIGUOUS literal `'03/04/2026'`
      resolves to 2026-03-04 under `SET DATEFORMAT mdy` and to 2026-04-03
      under `SET DATEFORMAT dmy` (an unambiguous ISO literal like
      `'2026-03-04'` is unaffected either way — SQL Server special-cases that
      format regardless of session `DATEFORMAT`, so only genuinely ambiguous
      literals are at risk); `DATEPART(weekday, ...)` for a fixed real date
      returns a different ordinal under `SET DATEFIRST 1` vs. `SET DATEFIRST
      7`. Purely informational — this pass cannot see what value the
      CALLER's own session already had, so it cannot claim the module's SET
      actually changes anything for a specific invocation, only that the
      module makes its own date interpretation session-state-dependent;
      `FindingConfidence.Low`, SARIF Note, the same no-magnitude-claim tier
      `LocalVariablePredicateFinding` uses for its own reason. Wired
      end-to-end (`ScanReport` schema version 33, SARIF rules
      `silentscan/session-date/set-{dateformat,datefirst}`, readable-report
      section). Unit-tested (`SessionDateSettingScannerTests`, 5 cases:
      DATEFORMAT fires, DATEFIRST fires, both in the same module both fire,
      unrelated `SET NOCOUNT`/`SET ANSI_NULLS` never fire, no SET statement
      never fires). **Real coverage against the local RM_ test database: 0
      findings** — a real, honest zero (this codebase's own modules
      apparently never override these two session settings mid-body), not a
      detection gap; the mechanism itself is oracle-proven and the scanner
      correctly fires on every hand-authored fixture in the unit-test suite.
- [x] **Unnamed `PRIMARY KEY`/`DEFAULT`/`FOREIGN KEY`/`CHECK` constraint on a
      `#temp` table — investigated and closed, not built. The core premise
      is false in modern SQL Server, oracle-confirmed directly rather than
      trusted from the incumbent tool's own description.** The proposed
      claim was that two sessions creating the same-shaped `#temp` table
      concurrently could collide on the system-generated constraint name.
      Probed directly (Docker instance, `tempdb.sys.tables`/
      `tempdb.sys.key_constraints`): SQL Server itself already disambiguates
      every `#temp` table's own PHYSICAL name in `tempdb` with a unique
      per-object numeric suffix baked in at `CREATE TABLE` time
      (`#t1_______...00000000003F`, confirmed to differ on every single
      creation — three sequential `CREATE TABLE #t1`/`DROP TABLE #t1` calls
      in the SAME session produced three different suffixes, so this isn't
      even a same-session-reuse question, only a new-object-identity one),
      and the generated constraint name is derived from that already-unique
      identity — three sequential creations of the identically-shaped
      unnamed-PK `#t1` produced three genuinely different constraint names
      (`PK__#t1_______3214EC0727670645`,
      `PK__#t1_______3214EC07FAF76D01`,
      `PK__#t1_______3214EC07AD7744EB`). Since every `CREATE TABLE` call —
      concurrent or sequential, same session or different — gets a fresh
      object identity and therefore a fresh generated name, the collision
      this item's whole premise depended on cannot actually occur. Whatever
      version-specific behavior the incumbent tool's rule was written
      against, it does not reproduce on the current engine
      (SQL Server 2022). No code written — forcing a rule to exist for a
      falsified premise would be exactly the "plausible-sounding but
      unverified claim" CLAUDE.md's precision-first discipline exists to
      prevent, the same "proposed and killed the same session" treatment
      already given to "Non-foldable nondeterministic intrinsic in a
      predicate" and "`IF` statements containing queries inside a
      procedure" elsewhere in this file.
- [x] **Database-level configuration flags — shipped, a genuinely new finding
      *category*.** `DatabaseConfigurationFinding`/`DatabaseConfigurationReader`
      (`src/SilentScan.Core/Predicates/DatabaseConfigurationFinding.cs`,
      `src/SilentScan.Live/Catalog/DatabaseConfigurationReader.cs`) — the
      first stream in this codebase reported once per SCAN RUN against the
      target database itself, not once per module/column/predicate. Live-mode
      only by construction (there is no file-mode equivalent of "the
      database's own current configuration"); always empty from
      `ScanReportBuilder`, merged into the report by `LiveScanRunner` after a
      real connection, the identical live-only-merge pattern
      `TempTableExecShapeFindings` already established. Six flags read from
      `sys.databases` (`PAGE_VERIFY`, `AUTO_SHRINK`, `AUTO_CLOSE`,
      `TARGET_RECOVERY_TIME`) and `sys.database_query_store_options` (actual
      state, capture mode) — no query text involved at all.
      <br><br>
      **Severity deliberately NOT uniform across the six flags, decided after
      checking current engine defaults rather than assuming every flag is an
      equally-confident "always should be X" claim:** `PAGE_VERIFY`/
      `AUTO_SHRINK`/`AUTO_CLOSE` are long-established, essentially
      uncontroversial DBA anti-patterns — SARIF Warning. `TARGET_RECOVERY_TIME`
      is also Warning, but only after confirming directly (not assumed) that
      the engine's OWN modern default is `60`, not `0` — checked against a
      freshly created database on the same instance (`model`, the system
      database every new database clones from), which shows
      `target_recovery_time_in_seconds = 60` — a database sitting at `0` has
      genuinely deviated from that default, disabling indirect checkpoint
      entirely (Microsoft's own "Database Checkpoints" guidance since SQL
      Server 2016 recommends enabling it). The two Query Store flags are
      SARIF Note only, not Warning — unlike the others, whether Query Store
      should be on, and which capture mode, are real, deliberate operational
      choices (`ALL` capture mode is a genuine, common choice for active
      troubleshooting; some teams disable Query Store on very high-churn
      ad-hoc workloads), not a universal anti-pattern.
      `QueryStoreCaptureModeNotAuto` is only even evaluated when Query
      Store's own actual state IS `READ_WRITE` — reporting a capture-mode
      complaint about a Query Store that isn't running would be a confusing,
      redundant second finding for the same underlying fact.
      <br><br>
      **Oracle discovery, load-bearing for how this stream's own tests are
      written:** a bare `CREATE DATABASE` on this engine instance genuinely
      starts with Query Store ON and immediately `READ_WRITE` (confirmed
      directly — no warm-up lag) — but every disposable test database this
      test suite's own `DatabaseProvisioner` creates deliberately turns Query
      Store back OFF right after creation (real, measured Docker error-log
      spam from Query Store's own background worker racing this suite's
      CREATE/DROP churn, documented in `DatabaseProvisioner`'s own doc
      comment). This is a genuine property of the test infrastructure, not a
      reader bug — the oracle test suite's own "all defaults" baseline
      explicitly re-enables Query Store first before asserting zero findings,
      rather than asserting a premise the provisioner itself had already
      falsified. The same fact also meant `EngineAuthoritativeScan`-based
      fixture tests elsewhere in this suite (which genuinely run the full
      live pipeline against a real disposable database, not a file-mode
      stub) now legitimately carry one real `QueryStoreNotReadWrite` finding
      each — a real, honest consequence of shipping a live-mode-only stream
      into a test harness that already runs the live pipeline, not a false
      positive.
      <br><br>
      No plan-XML oracle applies — every value is a directly-read, exact
      catalog fact, not a plan-shape claim. Wired end-to-end (`ScanReport`
      schema version 34 → 35, SARIF rule catalog + writer, readable-report
      section). Unit/oracle-tested (`DatabaseConfigurationReaderDefaultsOracleTests`,
      `DatabaseConfigurationReaderUnhealthyFlagsOracleTests`,
      `DatabaseConfigurationReaderQueryStoreCaptureModeOracleTests`,
      `DatabaseConfigurationReaderAutoCloseOracleTests` — 4 real oracle test
      classes against real deployed databases, each flag mutated via a real
      `ALTER DATABASE` and read back, plus the capture-mode-only-when-
      Query-Store-is-on suppression guard). **Real coverage against the local
      RM_ test database: 2 findings** (`TargetRecoveryTimeUnset`,
      `QueryStoreNotReadWrite`) — `PAGE_VERIFY`/`AUTO_SHRINK`/`AUTO_CLOSE`
      are all already at their healthy defaults there, a real, honest partial
      result rather than either extreme.
- [x] **True cartesian join — shipped, with a real precision bug caught and
      fixed against the local test database before this could ship
      honestly.** `CartesianJoinFinding`/`CartesianJoinScanner`
      (`src/SilentScan.Core/Predicates/CartesianJoinScanner.cs`) — a
      comma-join or explicit `CROSS JOIN` where NO predicate anywhere in the
      statement (WHERE clause, or any other JOIN's own ON clause) connects
      the two sides. Deliberately distinct from the shipped
      `PartialCompositeForeignKeyJoinFinding`: that fires when a join
      predicate exists but is incomplete; this fires when there is no
      predicate at all. Pure relational algebra, no oracle needed for the
      finding's own core claim (a row-count-multiplying cartesian product is
      definitional, not implementation-dependent), version-insensitive.
      <br><br>
      **A real false positive, caught only against the real corpus, not by
      the unit-test suite:** the first implementation checked connectivity
      PAIRWISE — "does any single predicate leaf mention both of these two
      specific table aliases" — for every pair of top-level FROM entries.
      Against the local test database this flagged a genuine 5-table
      comma-join (`FROM a, b, c, d, e WHERE a.TypeID = b.ID AND ... AND
      a.AgencyID = c.AgencyID AND c.OriginID = d.AddressID AND
      c.DestinationID = e.AddressID`) as three separate "cartesian" pairs
      (b-c, b-d, b-e) even though every table is transitively connected
      through `a` and `c` — a real query with zero cartesian risk. Connectivity
      is a GRAPH property, not a pairwise one: fixed by building a proper
      union-find over every leaf table in the FROM tree, unioned by every
      leaf predicate that spans two or more of them, and only reporting when
      the resulting graph has more than one connected component — a witness
      pair per disconnected component, not one finding per non-directly-
      connected pair (avoiding the same spam risk the fix also closes).
      Confirmed the fix against the exact real shape that caught the bug
      (`FiveTableCommaJoin_AllTransitivelyConnectedThroughAThirdTable_NeverFires`)
      and a case with one genuinely disconnected table among otherwise-
      connected ones, firing once, not per pair
      (`CommaJoin_OneGenuinelyDisconnectedTableAmongConnectedOthers_FiresOnceNotPerPair`).
      <br><br>
      **A second real bug, also caught only against the real corpus:** a
      `NullReferenceException` on `COUNT(*)` — a wildcard `ColumnReferenceExpression`
      has `MultiPartIdentifier` null ENTIRELY, not merely short, which a
      naive `.Identifiers.Count < 2` check crashes on. Fixed (treated the
      same as unqualified — the whole statement declines, since a wildcard
      inside a nested subquery can't be attributed to a side either) and
      regression-tested
      (`CountStarWildcardInsideNestedSubquery_NeverCrashes`).
      <br><br>
      Precision guards: `CartesianJoinKind.CommaJoin`/`ExplicitCrossJoin`
      report separately (different intent signal — an explicit `CROSS JOIN`
      is the author self-documenting a deliberate cartesian product, still
      worth surfacing since an accidentally-left one is a real, if less
      common, mistake); a witness pair is only reported when BOTH sides are
      themselves a single plain `NamedTableReference` (a nested join/derived
      table/subquery on either side is declined rather than guessed at,
      though it still participates correctly in the wider connectivity
      graph); an UNQUALIFIED column reference anywhere in the combined
      predicate set declines the whole FROM clause (cannot be conservatively
      attributed to a side without a catalog column lookup this pass doesn't
      perform). `FindingConfidence.High`, SARIF Warning. Wired end-to-end
      (`ScanReport` schema version 33, SARIF rules
      `silentscan/join/cartesian-{comma-join,cross-join}`, readable-report
      section). Unit-tested (`CartesianJoinScannerTests`, 12 cases including
      the two regression cases above: comma-join with no connecting
      predicate fires, a connecting WHERE predicate suppresses it, explicit
      `CROSS JOIN` with no connecting predicate fires, an `INNER JOIN ...
      ON` suppresses it, a single-table FROM never fires, an unqualified
      column reference declines, a connecting predicate inside an
      arithmetic expression is still recognized, a third join's ON clause
      connecting two otherwise-unrelated tables suppresses them, a nested
      join on one side declines per the stated scope limit). **Real coverage
      against the local RM_ test database: 63 findings** (62
      `ExplicitCrossJoin`, 1 `CommaJoin`) — spot-checked two: a recurring,
      genuine `dbo.tblAuditKind CROSS JOIN dbo.tblDatabaseObjects` pattern
      (each side filtered to exactly one row by an independent equality
      predicate, a real "constant lookup via cross join" idiom, appearing
      15+ times across real procedures) and a self-join comma-join
      (`FROM tblSettings a, tblSettings b WHERE a.InternalID = 207 AND
      b.InternalID = 1882`, structurally a true cartesian product even
      though both sides happen to resolve to exactly one row by design) —
      both real, both matching the finding's own stated mechanism exactly,
      neither a false positive.
- [x] **Declared type of size 1 or 2 — shipped.**
      `UndersizedDeclarationFinding`/`UndersizedDeclarationScanner`
      (`src/SilentScan.Core/Predicates/UndersizedDeclarationScanner.cs`) —
      genuinely distinct from the shipped under-length-vs-compared-column
      stream: needs no compared column at all, a string/binary declaration
      of length 1 or 2 (`CHAR`/`VARCHAR`/`NCHAR`/`NVARCHAR`/`BINARY`/
      `VARBINARY`) is flagged purely for being that small on its own. Two
      independent scan halves: `ScanCatalog` walks every real table column
      (mirrors `MaxTypedColumnScanner`'s "one structural fact per column, no
      AST" shape); `ScanDeclarations` walks every `DECLARE`'d local variable
      and procedure/function formal parameter across every parsed module.
      **A temp table's/table variable's own column declarations (`CREATE
      TABLE #temp`, `SELECT ... INTO #temp`, `DECLARE @t TABLE(...)`) are
      covered for free by the catalog half** — `CatalogBuilder` already
      registers all three under `DatabaseCatalog.Tables` for other, earlier
      consumers, confirmed directly by real findings against the local test
      database's own temp tables (single-character flag/type columns like
      `BreakType`, `leftoperandtype`/`rightoperandtype`, `SchedObjType`) —
      not a separate pass this stream had to build. Purely advisory/
      structural — no oracle applies ("this declaration looks like a
      mistake" is a code-smell judgment call, not a provable runtime or
      plan-shape fact) — `FindingConfidence.Low` by default, the same
      no-magnitude-claim tier `LocalVariablePredicateFinding`/
      `CascadingForeignKeyFinding` use for their own advisory reasons. Wired
      end-to-end (`ScanReport` schema version 33, SARIF rules
      `silentscan/declaration/undersized-{column,variable-or-parameter}`,
      readable-report section). Unit-tested
      (`UndersizedDeclarationScannerTests`, 9 cases: catalog column length 1
      fires, length 2 fires, length 10 never fires, a non-string/binary
      column never fires, a `DECLARE`d `CHAR(1)` fires, a `VARCHAR(2)`
      procedure parameter fires, a `VARCHAR(50)` local never fires, an `INT`
      local never fires, a `VARCHAR(MAX)` local never fires — MAX is never
      misread as "shorter" the way a raw sentinel length would). **Real
      coverage against the local RM_ test database: 589 findings** (392
      declarations, 197 table columns) — spot-checked several catalog-side
      hits directly against real module text, all genuine single-character
      flag/type columns matching the finding's own claimed pattern, not
      false positives.
- [x] **Output parameter not populated on every code path — shipped, standalone
      (not folded into the Tier 4 "output parameter never assigned" entry, since
      Tier 4 itself stayed out of scope for this whole pass of work).** A real,
      sound path-sensitive reachability walk — `OutputParameterFinding`/
      `OutputParameterScanner` (`src/SilentScan.Core/Predicates/
      OutputParameterScanner.cs`) — directly reuses the reachability-walk shape
      `TransactionHygieneScanner` already established for "does every path
      resolve a state", adapted from tracking one open transaction site to
      tracking a SET of not-yet-guaranteed-assigned OUTPUT parameter names
      through IF/ELSE, TRY/CATCH, WHILE, BEGIN/END, RETURN. A correct
      path-sensitive analysis naturally subsumes the simpler "never assigned at
      all" case as one end of the same spectrum, so nothing from the original
      framing is lost by shipping it here instead.
      <br><br>
      **Oracle-confirmed the real caller-visible risk directly, load-bearing
      for the finding's own wording:** a genuinely never-assigned OUTPUT
      parameter leaves the CALLING session's own variable completely
      UNCHANGED — not reset to NULL, not defaulted, literally untouched
      (`OutputParameterOracleTests`: a caller variable seeded `999` stays
      `999`; one seeded `NULL` stays `NULL`). This is a sharper, more
      dangerous claim than "the caller gets NULL": a caller reusing the same
      local variable across several calls (a common accumulator/status-code
      pattern) can silently read STALE data from a previous, unrelated call
      and never notice.
      <br><br>
      An assignment is recognized in exactly three forms: `SET @p = ...`
      (any compound form, e.g. `+=`), `SELECT @p = ...` in a top-level query
      specification's own select list, and passing `@p` onward as the
      `OUTPUT` argument to a nested `EXEC` call (a real, common "delegate the
      whole output" idiom — a genuinely broken callee is the callee's own
      finding, not a reason to double-flag the caller here).
      <br><br>
      **`THROW` is deliberately never a finding site** — unlike a `RETURN` or
      the natural end of the body, `THROW` raises a real, loud engine error
      the instant it executes, so the caller does not silently receive a
      stale value with no signal at all, matching this codebase's whole scope
      discipline for excluding cases the engine already surfaces loudly
      (`Rules.WriteLossClassifier`'s identical reasoning). `THROW` is still
      terminal for the walk (nothing after it executes on that path), just
      never itself a finding. **`RAISERROR` is NOT treated as terminal at
      all** — by default it does not stop batch execution the way `THROW`
      does, so statements after it are genuinely still reachable and are
      analyzed normally.
      <br><br>
      **Known v1 scope limits, stated honestly:** a `GOTO` anywhere in the
      procedure body declines the whole procedure's analysis, identical
      reasoning to `TransactionHygieneScanner`'s own documented choice; a
      `CATCH` block is analyzed as entering with whatever assignment state
      existed at the START of its own `TRY`/`CATCH` construct (sound, not
      merely conservative, for the identical reason already documented for
      the transaction-hygiene stream); a `WHILE` loop body is analyzed as
      running exactly one representative iteration, OR-merged with the "ran
      zero times" possibility; no cross-procedure tracking beyond the direct
      "passed onward as OUTPUT" recognition above.
      <br><br>
      A correctness finding, not a plan-shape one — no plan-XML oracle
      applies. `FindingConfidence.High`, SARIF Warning — the same "structural
      risk, not a plan-shape claim" tier `TransactionHygieneFinding`/
      `ForcedSerialFinding` already use, not Error: unlike e.g.
      `NotInNullableSubqueryFinding`, this pass cannot see whether a real
      caller ever reads the parameter's post-call value at all, so the
      magnitude of harm is genuinely conditional on caller behavior this
      tool cannot observe. Version-insensitive: OUTPUT parameter marshalling
      is ANSI/T-SQL calling-convention semantics, unaffected by compat level
      or CE mode. Wired end-to-end (`ScanReport` schema version 33 → 34,
      SARIF rule `silentscan/control-flow/unassigned-output-parameter`,
      readable-report section). Unit-tested (`OutputParameterScannerTests`,
      18 cases: falls-off-end fires, unconditional top-of-body assignment
      never fires, `SELECT @p = ...` assignment never fires, IF-branch-only
      assignment with implicit unresolved ELSE fires, both-branches-assign
      never fires, RETURN-before/after-assignment, forwarded-as-OUTPUT-
      argument never fires, plain-input-argument (not forwarded) still
      fires, unconditional THROW never fires despite never assigning,
      RAISERROR does not terminate the walk, GOTO declines the whole scope,
      TRY-only-assignment-CATCH-doesn't fires, both-TRY-and-CATCH-assign
      never fires, WHILE-zero-iterations fires, multiple OUTPUT parameters
      report only the genuinely unassigned one, no OUTPUT parameters at all
      never fires, compound assignment still counts) + 3 real execution-based
      oracle tests (`OutputParameterOracleTests`, the caller-variable-
      unchanged mechanism above). **Real coverage against the local RM_ test
      database: 512 findings.** Spot-checked one true positive directly
      against real deployed module text (`dbo.GetEstimatedDistanceAndTime`):
      both `@distance` and `@timeinminutes` are assigned only inside a `TRY`
      block's own final `SELECT @a = x, @b = y` statement, while the matching
      `CATCH` block only `RAISERROR`s without reassigning either parameter —
      exactly the pattern this rule targets: any real error earlier in the
      TRY block leaves both parameters silently unresolved for the caller.
- [x] **`SR0006`/`SR0015` scoped and closed — both already fully covered, no new
      code needed.** (`ErikEJ` fork rule names only were available to reason
      from — this environment has no network/repo access to read the fork's
      actual rule source, so both were resolved by direct reasoning about the
      named shape plus real verification against this codebase's own shipped
      code and the Docker oracle, not by reading the vendor's implementation.)
  - **`SR0006` "move a column reference to one side of a comparison
    operator" — already fully subsumed by the shipped column-arithmetic
    non-sargability rule, confirmed with a new regression test, not just a
    source read.** `NonSargablePredicateScanner.Visit(BooleanComparisonExpression)`
    calls `InspectSide` on BOTH `FirstExpression` and `SecondExpression`
    symmetrically — a reversed operand order (`3.975 > UnitPrice + 1`
    instead of `UnitPrice + 1 < 3.975`, literal written first) fires
    `SargabilityFindingKind.ColumnArithmetic` identically either way,
    since which side the column sits on was never load-bearing to begin
    with. Locked in by
    `NonSargablePredicateScannerTests.ColumnArithmetic_ReversedOperandOrder_StillFires`.
    No fix needed; nothing was missing.
  - **`SR0015` "extract deterministic function calls from WHERE predicates"
    — investigated and closed, the premise is false.** Oracle-probed
    directly (Docker instance, real seeded 2,000-row indexed table, a
    genuinely expensive schema-bound scalar UDF — a 1,000-iteration `WHILE`
    loop — called two ways): `WHERE indexed_col = dbo.Expensive(indexed_col)`
    (the UDF genuinely depends on each row) measured ~4 seconds of real CPU
    time across 2,000 rows, exactly the per-row-re-evaluation cost the
    shipped scalar-UDF stream already targets; `WHERE indexed_col =
    dbo.Expensive(1)` (the UDF's argument is a literal, independent of any
    column) measured 2ms — indistinguishable from the no-UDF baseline — with
    the captured plan showing a `Compute Scalar` operator feeding a genuine
    `Index Seek`, confirming the optimizer already folds/hoists a
    column-independent deterministic scalar UDF call to evaluate it ONCE,
    the same way it already folds a bare `GETDATE()`/`RAND()` call (see the
    "Non-foldable nondeterministic intrinsic in a predicate" item elsewhere
    in this file, which found and closed a structurally identical false
    premise). There is no repeated-per-row cost for this rule to catch that
    the engine hasn't already eliminated on its own — nothing survives that
    is both true and a performance finding distinct from the already-shipped
    column-dependent scalar-UDF stream, so this is **not** being built, the
    same "proposed and killed the same session" discipline that item already
    models.

---

## Research gates before publication (not detections)
Two items from the wider-landscape/incumbent-catalog reads that are measurement
tasks, not rule candidates — they don't belong in a detection tier, but need
doing before the study can make certain public claims.

- [ ] **Pre-publication gate: measure the second type-binding incumbent's
      conversion rule against our direction fixtures — attempted 2026-08-17,
      still open, not resolved.** Genuinely retried directly (fresh fetch of
      the vendor's own rule page, the legacy static-doc URL scheme, a search
      engine's own indexed snippet) rather than trusting the prior "vendor
      site defeats fetching" finding secondhand — confirmed first-hand that
      the entire site is now a client-rendered SPA with zero server-side
      content, including the legacy static doc pages, which now redirect
      through the same JS shell. A search engine's indexed snippet does
      surface real rule prose, and it reads symmetrically ("two expressions
      of different data types," no directional language) — but an absent
      directional mention in a short snippet is not proof the underlying rule
      logic is symmetric, so this stays an open question, not a confirmed
      negative. Trial-install remains genuinely blocked in this environment
      (Windows/SSMS-only product, headless Linux research environment, no
      Windows host available) — not skipped for convenience. Full write-up,
      including exactly what was tried and why each avenue failed, is
      `detection-reference.md` Appendix 7 §7.10. **The study still cannot
      claim "nothing [commercial] is direction-aware" until this resolves** —
      needs either a Windows/SSMS environment for a real trial-install, or the
      vendor publishing a non-JS-rendered rules reference.
- [x] **Follow-up gate, same shape as the pre-publication gate above: two new
      tools found need a closer look before being ruled out — closed
      2026-08-17, both tools' real rule source read directly.** Docs showed no
      implicit-conversion hit on a grep, but that was a docs-level read; both
      tools' actual source has a real, direction-aware implicit-conversion
      rule — genuinely correcting this checklist's own working "nothing else
      exists" assumption for open-source tools specifically. The Rust/WASM
      tool (~103 T-SQL rules) has TWO independent conversion rules: a real
      file-scoped schema-aware one (parses the same file's own DDL/DECLARE/
      parameter types, direction-aware by construction — its own doc comment
      states the precedence reasoning almost identically to this project's)
      plus a separate, deliberately weaker token-level `N'...'`-vs-column
      heuristic. The NuGet-distributed tool (~130 rules by direct count, not
      the originally-cited 169 — discrepancy not investigated further, out of
      scope for this gate) has a real ScriptDom-visitor + schema-resolution-
      layer rule with a genuinely general type-precedence table
      (`LeftConverted`/`RightConverted`/`BothConverted`), reporting only when
      the converted operand is a column — direction-aware by construction, not
      by accident. **What both still lack, confirmed directly by reading their
      source, not assumed:** neither is collation-aware (one tracks a
      `Collation` schema field that is never actually read by the conversion
      logic; the other only has correct COLLATION FAMILY *prose advice* in its
      remediation text, never a branch on an actual read value), neither has a
      lineage/view-expansion pass, neither has a live-catalog connection (both
      resolve types from parsed DDL text in the files being analyzed, not a
      real database — the exact "reinventing the database-project wheel"
      approach CLAUDE.md's own hard-scope rule rejects for this project), and
      neither has any plan-XML oracle. This project's real differentiator
      narrows precisely to: direction + collation-family verdict split +
      lineage-depth attribution + oracle confirmation, together — not
      direction alone, which is now shown to exist elsewhere. Full write-up:
      `detection-reference.md` Appendix 7 §7.9.

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
Reopened for active work starting 2026-08-17 — sub-bullets get marked
**shipped**/**closed** in place as each one lands, same discipline every
other section of this file already uses; the rest of the bullet list stays
exactly as originally scoped until its own turn comes up.

- **The maintainability/correctness bulk of the paid-tier T-SQL analyzer read
  at source on 2026-08-16** — ~68 of its ~83 rules, grouped by theme:
  * *Size and complexity metrics — shipped (2026-08-17).* Eight
    configurable-threshold structural metrics, one finding type
    (`CodeMetricFinding`/`CodeMetricFindingKind`), no catalog, no oracle (a
    line count or nesting depth is directly observable from the parse, never
    a plan-shape or runtime-behavior claim) — `FindingConfidence.Low` on
    every member (a real, measured structural fact, but no magnitude/cost
    claim). Physical line text is reconstructed losslessly from each
    fragment's own token stream rather than needing a separate raw-text side
    channel.
    <br><br>
    **Real bug caught and fixed before shipping, not left latent:**
    ScriptDOM's `ScriptTokenStream` is a single list shared by reference
    across every fragment/sub-node from the same parse — only
    `FirstTokenIndex`/`LastTokenIndex` mark which slice actually belongs to
    a given fragment. An early version of the line-length/module-length
    reconstruction ignored those bounds and concatenated the fragment's
    entire (potentially much larger) shared stream. It happened not to
    matter for *this* codebase's own one-module-per-parse live-mode
    architecture (confirmed directly: `LiveScanRunner` calls
    `SqlScriptParser.ParseText` once per module, so `FirstTokenIndex` is
    already 0 for every top-level fragment today), but was still a latent
    correctness bug the moment this scanner is ever pointed at a multi-object
    parse result — fixed to slice by the fragment's own token range instead
    of trusting an implicit "one parse, one fragment" assumption that isn't
    actually guaranteed by the type itself. A genuinely enormous real
    finding surfaced during calibration (a 48,621-character single line in
    a real procedure — a commented-out sample `EXEC` call with a giant
    inline literal ID list) was independently verified against the real
    module text and confirmed correct, not a symptom of the bug above.
    <br><br>
    **Real thresholds, calibrated against the corpus's own measured
    distribution** (via a zero-threshold probe pass across the whole local
    test database, then picking a defensible cutoff selecting a small, real,
    selective subset — the same discipline `NestedViewDepthScanner`'s N=2 and
    `PostExpansionJoinWidthScanner`'s gap≥3 thresholds used):
    * Line length: p50=32, p90=98, p95=120, p99=179 chars → threshold **200**
      (selects well under 1% of all lines).
    * Module length: p90=270, p95=614, p99=3,191 lines → threshold **1000**.
    * Routine length: p90=293, p95=712, p99=3,596 lines → threshold **400**.
    * Parameter count: p90=10, p95=20, p99=42 → threshold **15**.
    * Nesting depth: p50=3, p75=7, p90=16, p95=30 (this corpus's own real
      procedural T-SQL nests meaningfully deeper than conventional
      general-purpose-language advice assumes) → threshold **10**, chosen to
      stay a real, selective signal against this codebase's own actual
      nesting habits rather than importing an unrelated-language convention
      that would fire on nearly everything here.
    * Conditional-operator (AND/OR) count in one IF/WHILE condition: p90=2,
      p95=3, p99=4 → threshold **4**.
    * CASE WHEN-branch count: p90=2, p95=3, p99=4 → threshold **5**.
    * CASE WHEN-branch body length: p95=1, p99=6, p999=15 → threshold **5**.
    <br><br>
    Unit-tested (`CodeMetricScannerTests`, 18 cases: fire + near-miss for
    all eight metrics using small custom thresholds for readable fixtures,
    plus a real-CREATE-FUNCTION case and an all-defaults-never-fire sanity
    case). Wired end-to-end (`ScanReport` schema version 36 → 37, SARIF rule
    catalog + writer, readable report section). **Real coverage against the
    local RM_ test database: 9,540 findings** (7,825 line-too-long, 503
    nesting-too-deep, 316 routine-too-long, 309 too-many-parameters, 258
    case-branch-too-long, 175 module-too-long, 113 too-many-case-branches,
    41 too-many-conditional-operators) — every threshold genuinely
    selective against this real corpus, none firing on a majority of the
    codebase.
  * *Formatting and layout — shipped (2026-08-17).* One finding type
    (`FormattingFinding`/`FormattingFindingKind`), no catalog, no oracle —
    every member is a directly observable parse/token-stream fact, never a
    plan-shape or runtime-behavior claim. `FindingConfidence.Low` throughout,
    including the two visual-ambiguity kinds (a dangling statement or an IF
    misreadable as an ELSE IF): the flagged statement's OWN behavior is
    always unaffected either way, only a *future* edit relying on the
    misleading visual shape is at risk.
    <br><br>
    **Cross-checked against the real source, not just this checklist's own
    paraphrase — this bullet's original 8-item list undercounted.** The real
    plugin folder holds nine distinct rule classes for this theme, not eight,
    and two of the checklist's own phrases ("misleading indentation" and "a
    branch keyword sharing a line with the end of the previous block") each
    turned out to name a DIFFERENT real, separate rule than the "missing
    BEGIN...END" item, not a restatement of it — all three ship as distinct
    kinds below. A tenth candidate ("empty statements") does **not** ship —
    see below.
    <br><br>
    Nine kinds shipped:
    * **Tab characters** in the source text — one finding per physical line
      containing one, not per character.
    * **Multiple statements on one physical source line** — a real AST
      statement-list walk cross-referenced against line numbers, not a
      semicolon count (a `;` can sit inside a string literal or nested
      subquery).
    * **Multiple `DECLARE` variables on one physical source line** — fires
      only when two declared variables' own targets literally share a line;
      the common, idiomatic multi-line comma-list `DECLARE @a INT,\n@b INT`
      form never fires, confirmed directly against the real rule logic
      before shipping so this couldn't regress onto ordinary T-SQL style.
    * **Missing `BEGIN...END`** around an IF/WHILE/ELSE body that is a single
      unbraced statement on a *different* line than its own keyword — the
      general "always brace your conditionals" risk. Never fires on an ELSE
      IF continuation (a nested IF as the body is exempt, matching the real
      rule's own exemption).
    * **Single-line conditional body** — the narrower, sharper sibling: the
      unbraced body shares the *exact same line* as its own keyword
      (`IF x = 1 SELECT 1;`), which the checklist's original "misleading
      indentation" phrase was naming. Mutually exclusive with the kind
      above for the same site (never double-reported).
    * **Dangling statement after an unbraced body** — a statement
      immediately follows an unbraced IF/WHILE's single-statement body,
      starting on the very next line at the same or deeper indentation than
      that body, visually appearing to still be "inside" the conditional/loop
      when it structurally is not. **Real precision bug caught and fixed
      against the actual corpus, not left in**: the first version fired on
      ANY following statement, including a following `IF`/`WHILE` — but a
      real scan showed the overwhelming majority of raw matches (18 of the
      original 70) were a completely benign, unambiguous, common T-SQL idiom
      (`IF @a = 1\n  X\nIF @b = 1\n  Y\n...`, a chain of independent
      conditionals, each unmistakably its own new statement the moment its
      own `IF`/`WHILE` keyword is read) — narrowed to exclude a following
      IF/WHILE entirely, since only a *non-conditional* dangling statement is
      genuinely confusable with belonging to the block above it.
    * **IF immediately following a prior block's own `END`, on the same
      line** — the checklist's "a branch keyword sharing a line with the end
      of the previous block" phrase. Fires only when the prior IF has no
      ELSE and its own body is braced; never fires on a genuine `ELSE IF`
      chain (which shares no such line-adjacency shape at all).
    * **Redundant parentheses** — a parenthesized expression whose inner
      expression is itself a bare column reference, variable, literal, or
      another parenthesized expression (including the double-wrapped boolean
      case, `((x = 1))`) - narrowly scoped so a parenthesized *multi-operator*
      subexpression, which genuinely disambiguates precedence, never fires.
    * **Missing file header comment** — whether a module's own definition
      begins with a comment before its first real statement. Shipped at
      `FindingConfidence.Low` and stated as purely advisory in its own SARIF
      description: unlike an application source file's own license-header
      convention, T-SQL modules carry no comparably universal authoring
      norm, so this is reported as an observation, never implied to be a
      real risk the way the other eight kinds are.
    <br><br>
    **One candidate investigated and NOT shipped, confirmed unreachable
    rather than assumed**: "empty statements." Probed directly against
    `TSql160Parser` (the exact parser class this tool uses) — `BEGIN END`
    (an empty block) is a hard parse error ("Incorrect syntax near 'END'.")
    in *every* context tried (bare, inside IF, inside WHILE, inside a
    procedure body), and a bare `;` produces no statement AST node at all to
    attach a finding to. This tool's own parser dialect structurally cannot
    produce the AST shape this rule would need to match — the identical
    disposition already recorded for `COMPUTE`/`COMPUTE BY` and the `*=`/`=*`
    operators elsewhere in this file: closed, not built, no dead code
    shipped for a shape that can never fire.
    <br><br>
    Unit-tested (`FormattingScannerTests`, 25 cases covering every shipped
    kind's fire/near-miss pair, including the chained-unbraced-IF
    false-positive guard above and the ELSE-IF-chain exemptions for both
    conditional-body kinds). Wired end-to-end (`ScanReport` schema version
    37 → 38, SARIF rule catalog + writer, readable report section). **Real
    coverage against the local RM_ test database: 755,268 findings**
    (727,149 tab-character lines — this corpus's own real authoring
    convention is heavily tab-indented, a real fact about the corpus, not a
    detection artifact; 14,146 missing-BEGIN-END; 8,003 same-line
    declarations; 3,429 missing file headers; 1,981 single-line conditional
    bodies; 462 redundant-parentheses; 52 dangling statements; 45
    same-line statements; 1 IF-following-prior-END). Both of the structurally
    riskier kinds spot-checked directly against real module text and
    confirmed genuine: the sole `IfImmediatelyFollowingPriorBlockEnd` hit is
    a real, human-authored `END IF ... = 0` on one line with no `ELSE` at
    all (`dbo.spTripCoordinationAccept`), and a sampled
    `DanglingStatementAfterUnbracedBody` hit
    (`dbo.spADHocReportSelectAllTrips`) shows an unconditional `CREATE TABLE`
    visually indented as if it only ran when a preceding unbraced `IF`'s
    `DROP TABLE` ran - exactly the misleading shape this kind targets.
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
