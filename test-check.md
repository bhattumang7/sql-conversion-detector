 Here are the rules for each test:

  1. WHAT IS THE CLAIM? State the specific engine behavior this test exists to prove, in one
     sentence (read the class/method doc comment and the rule it backs, e.g. RuleCatalog/
     detection-reference.md entry — don't guess from the test name alone).

  2. MUTATION CHECK (the sharpest test): if the source behavior being verified were broken or
     deleted — comment out the relevant scanner logic, or imagine the engine mechanism doesn't
     exist — would this test actually fail? If the assertion is Assert.NotNull, Assert.True(count
     > 0), or a broad Assert.Contains against a large blob, treat this as a strong signal it
     would NOT fail for the right reason. State explicitly what a broken implementation would
     still make this test print.

  3. SELF-MATCH RISK: if the test's own diagnostic query embeds the same literal/pattern text
     it's searching for (a LIKE pattern, a table/column name, a keyword) in its own SQL text, could
     the query match its own compiled plan, its own DDL, or a leftover row from another test's
     run in the same shared instance? SQL Server caches plans at compile time, so a query CAN see
     itself. Check for this explicitly, don't assume it can't happen because "it's just a filter."

  4. RIGHT ROW, NOT A ROW: when a query can return more than one matching row (e.g. an ad-hoc
     cache entry AND a separately-cached parameterized/prepared entry for the same statement,
     multiple plans across compat levels, multiple objects matching a name pattern), does the
     test pin down WHICH row it's reading and confirm that's the one demonstrating the claim —
     or does it grab reader.Read() once and trust whatever comes back?

  5. CONTROL/NEGATIVE CASE: does the test include a sibling case that should NOT show the effect
     (a plain predicate that should parameterize normally, a shape that shouldn't fire), checked
     in the SAME run/statement where possible — the "same-statement isolation" discipline this
     codebase already uses in ForcedParameterizationScanner's own tests? A test with only a
     positive case can't distinguish "the mechanism is real" from "this is what always happens
     here regardless."

  6. RIGHT ARTIFACT: if the claim is about plan SHAPE (an operator, a seek vs scan, a
     CONVERT_IMPLICIT marker), is the test checking SHOWPLAN_XML/actual plan attributes — or
     is it checking cached SQL TEXT, a catalog view, or something else that's merely correlated
     with the real claim rather than being it? CLAUDE.md is explicit: "oracle is plan-XML based,
     never plan-shape based" for conversion findings — verify each test actually reads what it
     needs to, not a proxy.

  7. TEXT-FORMAT BRITTLENESS: for any test asserting on normalized/cached SQL text, does it
     assume exact spacing/casing/punctuation (e.g. "dbo.T" vs the engine's own "dbo . T") that
     could make the assertion silently over-match (pass for the wrong reason) or under-match
     (fail even when the real behavior is correct)? Don't assume the codebase's existing tests
     already got this right — check a sample by actually running one and comparing.

