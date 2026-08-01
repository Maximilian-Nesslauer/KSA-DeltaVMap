using System;
using DeltaVMap.Core;
using KSA;

namespace DeltaVMap.Dv;

// Reads stock's own staged delta-v (SequencePerformanceList.TotalDeltaV) for the editor
// vehicle, so the editor vehicle-dV bar delegates to it instead of the mod's hand-rolled walk.
//
// Stock keeps this value fresh in the editor on its own worker job (Program queues a
// SequencePerformanceJob whenever the tree is dirty) and computes it in vacuum by default, the
// same basis the route budget uses. It is materially more accurate than the mod's walk on a
// full multi-stage stack (stock does an event-driven per-reactant drain with robust jettison
// and cross-stage attribution, where the mod's simpler walk can mis-attribute or mis-jettison).
//
// One basis caveat: a sequence the player toggles to Atmospheric is folded into the total at sea
// level, so the editor "available" can dip below the vacuum route budget for that opt-in case.
// The default Environment is Vacuum, so the common case stays consistent, and the debug
// cross-check logs the atmospheric-sequence count when it is not.
//
// Flight is deliberately NOT delegated: there stock only recomputes while the staging window is
// open and pressure-corrects the active sequence, so the mod's own vacuum walk stays the flight
// source (and reads accurately there).
internal static class StockVehicleDv
{
    // Stock's total staged dV for the vehicle under construction in the editor, or null when
    // there is no editor vehicle. Read-only: never drives a recompute, so it cannot race stock's
    // worker job into corruption. Stock publishes the total once with a Volatile.Write after its
    // accumulation loop, so the read can at worst be one frame stale, never a partial sum.
    internal static double? TryEditorTotalDeltaV()
    {
        try
        {
            PartTree? parts = Program.Editor?.EditingSpace.Parts;
            if (parts == null || parts.Count == 0)
                return null;
            return parts.PerformanceSequences.TotalDeltaV;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("stock-editor-dv", $"[DvMap] Stock editor dV read failed: {ex}");
            return null;
        }
    }
}
