using DeltaVMap.Dv;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// Covers the staged-dV readout that feeds the route bar. The number itself comes from the game's
// own SequencePerformanceList, so what is worth testing is the seam around it: that the sequence
// arrays line up with stock's own accumulator, that the cache is keyed on the part tree, and that
// stock's start mass still weighs the whole vehicle. The readout adds nothing to stock's masses, so
// a game update that drops a mass term from the model would make every figure too favourable.
public sealed class StagedDvTest : IHarnessTest
{
    private const double DvEqualityTolerance = 0.01;

    // Stock sums the part masses in floats, so its start mass will not match the vehicle total to
    // the last kilogram.
    private const double MassTolerance = 1e-3;

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

            if (StagedDv.TryTotalDv() is not double dv)
            {
                HarnessLog.Line("[dvmap-staged-dv] FAIL: no staged dV for the controlled vehicle.");
                return 1;
            }

            HarnessLog.Line(FormattableString.Invariant($"[dvmap-staged-dv] '{saveId}': staged dV {dv:F1} m/s"));

            ok &= Check("staged dV is a positive, finite number", double.IsFinite(dv) && dv > 0.0);
            ok &= CheckAgainstOwnAnalyzer(vehicle, dv);
            ok &= CheckStartMassWeighsVehicle(vehicle);
            ok &= CheckCacheKeyedOnTree(dv);
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

    // The total must be stock's own accumulator, which only holds if the per-sequence read is
    // index-aligned with SequenceList.Sequences and reads SequencePerformance.DeltaV.
    private static bool CheckAgainstOwnAnalyzer(Vehicle vehicle, double dv)
    {
        var reference = new SequencePerformanceList(vehicle.Parts);
        reference.RecomputeForFlight(0f);
        double stockTotal = reference.TotalDeltaV;
        return Check(FormattableString.Invariant(
                $"total matches stock's own accumulator ({dv:F1} vs {stockTotal:F1} m/s)"),
            Math.Abs(dv - stockTotal) <= Math.Max(DvEqualityTolerance, Math.Abs(stockTotal) * 1e-4));
    }

    // Before anything is jettisoned, the first sequence starts with the whole vehicle. Engines ship
    // as sub-parts, so the save must carry sub-part inert mass for the comparison to catch a model
    // that leaves it out.
    private static bool CheckStartMassWeighsVehicle(Vehicle vehicle)
    {
        var reference = new SequencePerformanceList(vehicle.Parts);
        reference.RecomputeForFlight(0f);
        ReadOnlySpan<SequencePerformance> perf = reference.PerformanceSequences;
        if (perf.Length == 0)
            return Check("stock reports at least one sequence", false);

        ref readonly SequencePerformance first = ref perf[0];
        double subPartInert = SubPartInertMassKg(vehicle.Parts);
        double total = vehicle.TotalMass;
        HarnessLog.Line(FormattableString.Invariant(
            $"[dvmap-staged-dv] first sequence start mass {first.WetMass:F1} kg, vehicle {total:F1} kg, sub-part inert {subPartInert:F1} kg"));

        bool ok = Check(FormattableString.Invariant(
                $"the save carries sub-part inert mass ({subPartInert:F1} kg > 0)"),
            subPartInert > 0.0);
        if (first.AttachedParts == null || first.AttachedParts.Count != vehicle.Parts.Parts.Length)
        {
            HarnessLog.Line("[dvmap-staged-dv] SKIP start mass check: the first sequence already jettisons parts.");
            return ok;
        }

        return ok & Check(FormattableString.Invariant(
                $"the first sequence's start mass is the vehicle mass ({first.WetMass:F1} vs {total:F1} kg)"),
            Math.Abs(first.WetMass - total) <= MassTolerance * Math.Max(total, 1.0));
    }

    private static double SubPartInertMassKg(PartTree tree)
    {
        double mass = 0.0;
        foreach (Part part in tree.Parts)
        {
            foreach (Part subPart in part.SubParts)
            {
                foreach (InertMass inert in subPart.Modules.Get<InertMass>())
                    mass += inert.MassPropertiesAsmb.Props.Mass;
            }
        }
        return mass;
    }

    // The cached analyzer is keyed on the part tree, so dropping the cache must produce the same
    // reading for the same vehicle rather than a stale or empty one.
    private static bool CheckCacheKeyedOnTree(double dv)
    {
        StagedDv.Reset();
        double? again = StagedDv.TryTotalDv();
        return Check(FormattableString.Invariant(
                $"reading is reproducible across a cache reset ({dv:F1} vs {again ?? double.NaN:F1} m/s)"),
            again is double value && Math.Abs(value - dv) <= DvEqualityTolerance);
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
