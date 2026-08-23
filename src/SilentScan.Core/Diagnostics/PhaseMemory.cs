namespace SilentScan.Core.Diagnostics;

public static class PhaseMemory
{
private const long MinimumAllocatedBytesBetweenCollections = 64L * 1024 * 1024;

    private static long _allocatedAtLastCollection = GC.GetTotalAllocatedBytes(precise: false);

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

        if (Interlocked.CompareExchange(ref _allocatedAtLastCollection, allocated, previous) != previous)
        {
            return;
        }

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
