using System;
using Brutal.Logging;
using DeltaVMap.Core;
using KSA;

namespace DeltaVMap.Dv;

// Debug-only, read-only diagnostic that measures what stock's omitted sub-part inert mass is worth
// on real vehicles: the repaired staged total against stock's own. It only logs; it changes no
// displayed number.
//
// It reads the snapshot StagedDv already produced for the bar, so it drives no extra recompute.
// The atmospheric-sequence count is logged because stock integrates a sequence the player toggled
// to Atmospheric at sea level, which lowers its delta-v for a reason that is not this correction.
//
// Gated behind DebugConfig.CrossCheck (false in release) and throttled to changes, so a settled
// vehicle logs once instead of every frame.
internal static class DvCrossCheck
{
    private const string Tag = "[DvMap]";

    // Re-log only once one of the two totals moves by more than this many m/s.
    private const double LogEpsilon = 1.0;

    private static double _lastCorrected = double.NaN;
    private static double _lastRaw = double.NaN;

    internal static void Run()
    {
        try
        {
            if (StagedDv.TryDetailed() is not DvSnapshot snapshot)
                return;

            if (!double.IsNaN(_lastCorrected)
                && Math.Abs(snapshot.CorrectedDv - _lastCorrected) < LogEpsilon
                && Math.Abs(snapshot.RawDv - _lastRaw) < LogEpsilon)
                return;

            _lastCorrected = snapshot.CorrectedDv;
            _lastRaw = snapshot.RawDv;

            double diff = snapshot.CorrectedDv - snapshot.RawDv;
            double pct = snapshot.RawDv > 1.0 ? 100.0 * diff / snapshot.RawDv : 0.0;
            string context = Program.ControlledVehicle != null ? "flight" : "editor";

            string totals = FormattableString.Invariant(
                $"{Tag} staged dV ({context}): corrected {snapshot.CorrectedDv:F1} vs stock {snapshot.RawDv:F1} m/s, diff {diff:+0.0;-0.0} ({pct:+0.0;-0.0}%)");
            string detail = FormattableString.Invariant(
                $", subpart inert {snapshot.SubPartInertMassKg:F1}kg, {snapshot.SequenceCount} sequence(s), atmoSeqs={snapshot.AtmosphericSequenceCount}");
            DefaultCategory.Log.Info(totals + detail);
        }
        catch (Exception ex)
        {
            // Never let the cross-check unwind into the draw path.
            LogHelper.ErrorOnce("dv-crosscheck", $"{Tag} dV cross-check failed: {ex}");
        }
    }

    internal static void Reset()
    {
        _lastCorrected = double.NaN;
        _lastRaw = double.NaN;
    }
}
