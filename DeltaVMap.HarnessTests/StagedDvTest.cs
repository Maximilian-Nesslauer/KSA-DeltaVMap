using DeltaVMap.Dv;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// Covers the staged-dV readout that feeds the route bar. The number itself comes from the game's
// own SequencePerformanceList, so what is worth testing is the seam around it: that the sequence
// arrays line up, that the sub-part inert repair engages and only ever removes dV, and above all
// that re-walking stock's drain phases reproduces the fuel stock actually burned. That last one is
// the load-bearing assumption of the repair, and a game update could break it silently.
public sealed class StagedDvTest : IHarnessTest
{
    // Stock accumulates thrust and mass flow per phase in floats over many drain steps, so the
    // phase-derived burn will not match its running total to the last kilogram.
    private const double BurnReconcileTolerance = 0.02;
    private const double DvEqualityTolerance = 0.01;

    public string Name => "dvmap-staged-dv";

    public int Run(HeadlessSession session)
    {
        string? saveId = Environment.GetEnvironmentVariable(TestSupport.VehicleEnvVar);
        if (string.IsNullOrEmpty(saveId))
        {
            HarnessLog.Line($"[dvmap-staged-dv] SKIP: {TestSupport.VehicleEnvVar} not set.");
            return 0;
        }

        CelestialSystem system = session.System;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        if (system.HomeBody is not IParentBody home || home is not Astronomical body)
        {
            HarnessLog.Line("[dvmap-staged-dv] FAIL: the loaded system has no home body to orbit.");
            return 1;
        }

        bool ok = true;
        try
        {
            Orbit orbit = VehicleSpawner.CircularCci(home, body.MeanRadius + 500_000.0, Universe.GetElapsedTime());
            Vehicle vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "DvMapStagedDvTest", orbit);
            Program.ControlledVehicle = vehicle;
            StagedDv.Reset();

            if (StagedDv.TryDetailed() is not DvSnapshot snapshot)
            {
                HarnessLog.Line("[dvmap-staged-dv] FAIL: no snapshot for the controlled vehicle.");
                return 1;
            }

            string totals = FormattableString.Invariant(
                $"[dvmap-staged-dv] '{saveId}': corrected {snapshot.CorrectedDv:F1} m/s, stock {snapshot.RawDv:F1} m/s, diff {snapshot.CorrectedDv - snapshot.RawDv:+0.0;-0.0} m/s");
            string detail = FormattableString.Invariant(
                $", subpart inert {snapshot.SubPartInertMassKg:F1} kg, {snapshot.SequenceCount} sequence(s), atmoSeqs={snapshot.AtmosphericSequenceCount}");
            HarnessLog.Line(totals + detail);

            ok &= Check("corrected dV is a positive, finite number",
                double.IsFinite(snapshot.CorrectedDv) && snapshot.CorrectedDv > 0.0);

            // Adding the omitted mass can only worsen the mass ratio, never improve it.
            ok &= Check(FormattableString.Invariant(
                    $"repair never raises dV (corrected {snapshot.CorrectedDv:F1} <= stock {snapshot.RawDv:F1})"),
                snapshot.CorrectedDv <= snapshot.RawDv + DvEqualityTolerance);

            ok &= Check(FormattableString.Invariant(
                    $"the repair engages on a real vehicle (subpart inert {snapshot.SubPartInertMassKg:F1} kg > 0)"),
                snapshot.SubPartInertMassKg > 0.0);

            ok &= CheckAgainstOwnAnalyzer(vehicle, in snapshot);
            ok &= CheckPhaseBurnReconciles(vehicle);
            ok &= CheckCacheKeyedOnTree(vehicle, in snapshot);
            LogRecomputeCost();
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"[dvmap-staged-dv] FAIL: {ex}");
            ok = false;
        }
        finally
        {
            Program.ControlledVehicle = null;
            StagedDv.Reset();
            TestSupport.DespawnNewVehicles(system, preexisting);
        }

        HarnessLog.Line($"[dvmap-staged-dv] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    // The reported raw total must be stock's own accumulator, which only holds if the per-sequence
    // read is index-aligned with SequenceList.Sequences and reads SequencePerformance.DeltaV.
    private static bool CheckAgainstOwnAnalyzer(Vehicle vehicle, ref readonly DvSnapshot snapshot)
    {
        var reference = new SequencePerformanceList(vehicle.Parts);
        reference.RecomputeForFlight(0f);
        double stockTotal = reference.TotalDeltaV;
        return Check(FormattableString.Invariant(
                $"raw total matches stock's own accumulator ({snapshot.RawDv:F1} vs {stockTotal:F1} m/s)"),
            Math.Abs(snapshot.RawDv - stockTotal) <= Math.Max(DvEqualityTolerance, Math.Abs(stockTotal) * 1e-4));
    }

    // The repair re-integrates stock's drain phases from a heavier start mass, which is only valid
    // if mass flow times duration per phase is the fuel stock itself burned in that sequence.
    private static bool CheckPhaseBurnReconciles(Vehicle vehicle)
    {
        var reference = new SequencePerformanceList(vehicle.Parts);
        reference.RecomputeForFlight(0f);

        bool ok = true;
        int checkedSequences = 0;
        ReadOnlySpan<SequencePerformance> perf = reference.PerformanceSequences;
        for (int i = 0; i < perf.Length; i++)
        {
            ref readonly SequencePerformance p = ref perf[i];
            if (p.Phases == null || p.Phases.Count == 0 || !(p.BurnedFuelMass > 0f))
                continue;

            double phaseBurn = 0.0;
            for (int j = 0; j < p.Phases.Count; j++)
                phaseBurn += (double)p.Phases[j].MassFlowRate * p.Phases[j].Duration;

            checkedSequences++;
            double error = Math.Abs(phaseBurn - p.BurnedFuelMass) / p.BurnedFuelMass;
            ok &= Check(FormattableString.Invariant(
                    $"sequence {i} phase burn reconciles ({phaseBurn:F1} vs {p.BurnedFuelMass:F1} kg, err {error:P2})"),
                error <= BurnReconcileTolerance);
        }

        return ok & Check("at least one burning sequence was reconciled", checkedSequences > 0);
    }

    // The cached analyzer is keyed on the part tree, so dropping the cache must produce the same
    // reading for the same vehicle rather than a stale or empty one.
    private static bool CheckCacheKeyedOnTree(Vehicle vehicle, ref readonly DvSnapshot snapshot)
    {
        _ = vehicle;
        StagedDv.Reset();
        double? again = StagedDv.TryTotalDv();
        return Check(FormattableString.Invariant(
                $"reading is reproducible across a cache reset ({snapshot.CorrectedDv:F1} vs {again ?? double.NaN:F1} m/s)"),
            again is double value && Math.Abs(value - snapshot.CorrectedDv) <= DvEqualityTolerance);
    }

    // Not assertions (wall-clock in a headless run is too noisy to gate on), but the readout is
    // gated on the assumption that a full recompute is far too heavy for the draw path while the
    // per-frame change probe is nearly free. Both numbers belong in the log for whoever revisits
    // that trade.
    private static void LogRecomputeCost()
    {
        const int samples = 20;

        var cold = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < samples; i++)
        {
            StagedDv.Reset();
            _ = StagedDv.TryTotalDv();
        }
        cold.Stop();

        // Nothing burns between these calls, so they exercise the path a coasting frame takes.
        var warm = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < samples; i++)
            _ = StagedDv.TryTotalDv();
        warm.Stop();

        HarnessLog.Line(FormattableString.Invariant(
            $"[dvmap-staged-dv] cost: {cold.Elapsed.TotalMilliseconds / samples:F2} ms per full recompute, {warm.Elapsed.TotalMilliseconds / samples * 1000.0:F1} us per unchanged frame (mean of {samples})"));
    }

    private static bool Check(string what, bool pass)
    {
        HarnessLog.Line($"[dvmap-staged-dv] TEST {what} => {TestSupport.Verdict(pass)}");
        return pass;
    }
}
