namespace SilentScan.Core.Diagnostics;

/// <summary>
/// Marks the boundary between two whole-database phases of a live scan (catalog-merge, lineage,
/// call-graph, dynamic SQL, Tier-1, typed extraction). A parsed AST runs roughly 200x the size of
/// its source text (measured directly), so a phase that has just finished with one can leave a
/// large graph behind for the next phase to work around.
///
/// A manual <see cref="GC.Collect()"/> call is ordinarily the wrong tool - the runtime's own GC
/// already schedules collections better than hand-placed calls can, and sprinkling them through
/// a hot loop or library code fights the GC instead of helping it. This is neither: it is one
/// call per full-corpus phase boundary (a handful of call sites total) in a CLI process that
/// parses, analyzes, and exits, and it is measured to matter here specifically because .NET's
/// default (non-Server) GC does not proactively return large, now-garbage object graphs to the
/// OS between phases without allocation pressure to trigger a full collection - within a process
/// this short-lived, that pressure often never arrives on its own before the process exits.
/// Verified directly on a 12MB-module-text database (800 procs): with a forced collection at each
/// phase boundary, RSS between phases measured 116-225MB and the process's overall peak fell to
/// 1.3-1.9GB from 2.5GB. <see cref="GCCollectionMode.Aggressive"/> and <c>compacting: true</c>
/// were also measured, not assumed: a non-compacting forced collection left RSS climbing phase
/// over phase (674MB -> 1295MB) for no wall-clock savings over the compacting form.
///
/// The gate below is why this is affordable. Firing unconditionally, it cost every scan roughly
/// 300-420ms in stop-the-world pauses - ~50 blocking Gen2 collections whatever the target's size,
/// so a scan of a three-object database paid the same as one of the 800-proc database above, to
/// reclaim under a megabyte. Worse, a blocking Gen2 collection suspends every thread in the
/// process, so concurrent scans stopped overlapping: measured throughput FELL as concurrency rose
/// (0.24 -> 0.15 -> 0.12 scans/sec at 1, 2 and 6 concurrent scans). Gating on how much has
/// actually been allocated since the last collection keeps the large-database behaviour above
/// intact - one phase there allocates GBs and clears the threshold immediately - while a small
/// scan never reaches it and pays nothing.
/// </summary>
public static class PhaseMemory
{
    /// <summary>
    /// How much must have been allocated process-wide since the last forced collection before
    /// another one earns its cost. A phase that produced a large AST graph clears this many times
    /// over on the first boundary; a scan of a small database never reaches it at all and so pays
    /// nothing. The threshold is deliberately far below what one phase of a large scan allocates
    /// (measured in GB) and far above what a whole small scan allocates (measured in MB), so the
    /// separation does not depend on the exact figure.
    /// </summary>
    private const long MinimumAllocatedBytesBetweenCollections = 64L * 1024 * 1024;

    private static long _allocatedAtLastCollection = GC.GetTotalAllocatedBytes(precise: false);

    /// <summary>Blocking, compacting Gen2 collection, taken only when enough has been allocated since the last one to be worth its pause - see the type-level doc comment.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell", "S1215:\"GC.Collect\" should not be called",
        Justification = "The type-level doc comment records the direct measurements behind this one exception, and the allocation gate below keeps it from firing when there is nothing to reclaim. This class exists precisely to make that exception explicit, documented, and single-sited.")]
    public static void ReleaseBetweenPhases()
    {
        var allocated = GC.GetTotalAllocatedBytes(precise: false);
        var previous = Interlocked.Read(ref _allocatedAtLastCollection);
        if (allocated - previous < MinimumAllocatedBytesBetweenCollections)
        {
            return;
        }

        // Whichever caller wins this exchange does the collection for the whole window; a
        // concurrent phase boundary that loses it would otherwise force a second full pause
        // immediately after, reclaiming what the winner just reclaimed.
        if (Interlocked.CompareExchange(ref _allocatedAtLastCollection, allocated, previous) != previous)
        {
            return;
        }

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
