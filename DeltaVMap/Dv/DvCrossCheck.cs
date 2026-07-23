using System;
using Brutal.Logging;
using DeltaVMap.Core;
using KSA;

namespace DeltaVMap.Dv;

// Debug-only, read-only diagnostic that measures how far the mod's hand-rolled staged dV
// (VehicleDvAnalyzer) diverges from stock's own SequencePerformanceList.TotalDeltaV on real
// vehicles. It only logs; it changes no displayed number.
//
// The editor vehicle-dV bar now delegates to stock (the mod's walk diverged badly on multi-
// stage stacks in the editor); this keeps logging that divergence so the gap stays visible and
// flight accuracy, where the mod's walk is still the bar source, is watched.
//
// It reads stock's already-computed TotalDeltaV and never drives a recompute, so it adds no
// concurrency risk: stock keeps that value fresh on its own worker job in the editor, and in
// flight only while the staging window is open. The freshness state is logged (stockFresh,
// stockDirty, atmoSeqs) so a stale or pressure-corrected stock value is not read as real
// divergence. In the editor the value is a pure-vacuum staged total, the same basis as the
// analyzer; in flight it is fresh only while ResourceGroups.IsOpen, and the active sequence is
// pressure-corrected to real altitude regardless of its Environment toggle, so a flight
// comparison holds only with the staging window open, out of atmosphere (near-zero ambient),
// and every sequence on its default Vacuum.
//
// Gated behind DebugConfig.CrossCheck (false in release) and throttled to changes, so a settled
// vehicle logs once instead of every frame.
internal static class DvCrossCheck
{
    private const string Tag = "[DvMap]";

    // Re-log only once the mod or stock total moves by more than this many m/s.
    private const double LogEpsilon = 1.0;

    private static double _lastMod = double.NaN;
    private static double _lastStock = double.NaN;

    // In flight the caller has already computed the mod's walk for the bar, so it is passed in
    // to avoid a second walk. In the editor the bar reads stock instead, so the mod's own walk
    // is computed here purely for the comparison.
    internal static void Run(double? modControlledDv)
    {
        try
        {
            Vehicle? vehicle = Program.ControlledVehicle;
            if (vehicle != null)
            {
                Compare("flight", modControlledDv, vehicle.Parts, ResourceGroups.IsOpen);
                return;
            }

            VehicleEditor? editor = Program.Editor;
            if (editor != null)
                Compare("editor", VehicleDvAnalyzer.TryEditorVehicleDv(), editor.EditingSpace.Parts, stockFresh: true);
        }
        catch (Exception ex)
        {
            // Never let the cross-check unwind into the draw path.
            LogHelper.ErrorOnce("dv-crosscheck", $"{Tag} dV cross-check failed: {ex}");
        }
    }

    internal static void Reset()
    {
        _lastMod = double.NaN;
        _lastStock = double.NaN;
    }

    private static void Compare(string label, double? modDv, PartTree? parts, bool stockFresh)
    {
        if (modDv is not double mod || parts is null)
            return;

        SequencePerformanceList perf = parts.PerformanceSequences;
        double stock = perf.TotalDeltaV;

        if (!double.IsNaN(_lastMod)
            && Math.Abs(mod - _lastMod) < LogEpsilon
            && Math.Abs(stock - _lastStock) < LogEpsilon)
            return;

        _lastMod = mod;
        _lastStock = stock;

        double diff = mod - stock;
        double pct = stock > 1.0 ? 100.0 * diff / stock : 0.0;
        int atmoSeqs = CountAtmosphericSequences(parts);
        DefaultCategory.Log.Info(FormattableString.Invariant($"{Tag} dV cross-check ({label}): mod {mod:F1} vs stock {stock:F1} m/s, diff {diff:+0.0;-0.0} ({pct:+0.0;-0.0}%), stockFresh={stockFresh}, stockDirty={perf.IsDirty}, atmoSeqs={atmoSeqs}"));
    }

    // How many sequences are toggled to the Atmospheric environment: stock computes those at
    // sea level rather than vacuum, so any non-zero count means stock and the mod are on a
    // different basis and the diff is expected, not a bug.
    private static int CountAtmosphericSequences(PartTree parts)
    {
        int count = 0;
        ReadOnlySpan<Sequence> sequences = parts.SequenceList.Sequences;
        for (int i = 0; i < sequences.Length; i++)
            if (sequences[i].Environment == PerformanceEnvironment.Atmospheric)
                count++;
        return count;
    }
}
