using System.Diagnostics;
using System.Reflection;
using DeltaVMap.Dv;
using DeltaVMap.Model;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// Locks the antimatter fuel estimate to the game's own interstellar planner. DeltaVMap feeds the
// public InterstellarBurnPlan in the order TransferPlanner.PlanInterstellarFuel does; that method
// is private, so this test calls it through reflection with the same inputs and requires an
// identical result. A game update that changes the planner's sequence (another burn, a different
// distance split, a new scoop rule) then fails here instead of drifting silently. The drive is
// synthetic, because InterstellarBurnPlan.Burn reads only its numeric fields. The propellant
// reads that feed the estimate are checked against the stock sums on a real vessel.
public sealed class InterstellarFuelTest : IHarnessTest
{
    private const string Tag = "[dvmap-interstellar-fuel]";

    private const double C = 299792458.0;

    // A stock RAIR-1 class drive: 30 TW jet power, half the annihilation energy to the jet, carried
    // exhaust at 0.4 c, and a scoop of 1000 km capture radius.
    private const double JetPower = 3.0e13;
    private const double AnnihilationEfficiency = 0.5;
    private const double CarriedExhaustVelocity = 0.4 * C;
    private const double CaptureRadius = 1.0e6;

    // A 100 t vessel with 2 t of antimatter, 40 t of hydrogen and room for 40 t more, to Alpha
    // Centauri (4.344 ly), braking to the circular speed 1 AU from the barycenter.
    private const double Mass = 100_000.0;
    private const double AntimatterAboard = 2_000.0;
    private const double HydrogenAboard = 40_000.0;
    private const double HydrogenSpace = 40_000.0;
    private const double Distance = 4.109915256421932e16;
    private const double FinalSpeed = 41995.36064;
    private const double FallbackGasDensity = 1.6735575e-22;

    public string Name => "dvmap-interstellar-fuel";

    public int Run(HeadlessSession session)
    {
        bool ok = true;
        try
        {
            Type planner = typeof(TransferPlanner);
            Type? inputsType = planner.GetNestedType("InterstellarFuelInputs", BindingFlags.NonPublic);
            MethodInfo? plan = planner.GetMethod("PlanInterstellarFuel", BindingFlags.NonPublic | BindingFlags.Static);
            if (inputsType == null || plan == null)
            {
                HarnessLog.Line($"{Tag} FAIL: TransferPlanner.InterstellarFuelInputs or PlanInterstellarFuel not found; re-verify the stock planner.");
                return 1;
            }

            double rated = 2.0 * JetPower / CarriedExhaustVelocity;
            var drive = new InterstellarDrive(rated, JetPower, AnnihilationEfficiency, CarriedExhaustVelocity, 0.01,
                Math.PI * CaptureRadius * CaptureRadius, null!, null!);
            double gas = session.System.InterstellarGasDensity;
            if (!(gas > 0.0))
                gas = FallbackGasDensity;

            foreach (double speedC in new[] { 0.001, 0.01, 0.1 })
            {
                var inputs = new DriveFuelInputs(drive, Mass, HydrogenAboard, speedC * C,
                    0.3 * AntimatterAboard, 0.3 * AntimatterAboard, FinalSpeed, gas, Distance, HydrogenSpace);

                var watch = Stopwatch.StartNew();
                DriveFuelPlan ours = InterstellarFuel.Plan(in inputs);
                watch.Stop();

                object stockInputs = Activator.CreateInstance(inputsType, drive, inputs.Mass, inputs.HydrogenAboard, inputs.CruiseSpeed,
                    inputs.DepartureAntimatter, inputs.BrakeAntimatter, inputs.FinalSpeed, inputs.GasDensity, inputs.Distance,
                    inputs.HydrogenSpace)!;
                object stock = plan.Invoke(null, new[] { stockInputs })!;

                var departure = (InterstellarBurnEstimate)Read(stock, "Departure");
                var brake = (InterstellarBurnEstimate)Read(stock, "Brake");
                double coast = (double)Read(stock, "CoastSeconds");
                double scooped = (double)Read(stock, "Scooped");

                string at = FormattableString.Invariant($"{speedC:0.###} c");
                ok &= Check($"speed-up burn matches stock at {at}", ours.Departure == departure);
                ok &= Check($"brake burn matches stock at {at}", ours.Brake == brake);
                ok &= Check($"coast matches stock at {at}", ours.CoastSeconds == coast);
                ok &= Check($"scooped hydrogen matches stock at {at}", ours.Scooped == scooped);

                HarnessLog.Line(
                    FormattableString.Invariant($"{Tag} {at}: flight {ours.TimeOfFlightSeconds / 3.15576e7:F2} yr, coast {ours.CoastSeconds / 3.15576e7:F2} yr, ")
                    + FormattableString.Invariant($"cruise {ours.Departure.EndSpeed / C:F4} c ({ours.Departure.End}), brake to {ours.Brake.EndSpeed / 1000.0:F1} km/s ({ours.Brake.End}), ")
                    + FormattableString.Invariant($"antimatter {ours.AntimatterNeeded:F1} kg, hydrogen {ours.HydrogenNeeded:F1} kg, scooped {ours.Scooped:F1} kg, {watch.Elapsed.TotalMilliseconds:F2} ms"));
            }

            // A planet or moon target brakes to rest, and the chain capture follows as an
            // impulsive burn. The drive model must give a finite brake for that final speed.
            {
                var toRest = new DriveFuelInputs(drive, Mass, HydrogenAboard, 0.01 * C,
                    0.3 * AntimatterAboard, 0.3 * AntimatterAboard, 0.0, gas, Distance, HydrogenSpace);
                DriveFuelPlan rest = InterstellarFuel.Plan(in toRest);
                ok &= Check("a brake to rest gives a finite plan",
                    rest.Departs && double.IsFinite(rest.Brake.Seconds) && double.IsFinite(rest.Brake.EndSpeed) && rest.Brake.EndSpeed >= 0.0
                    && double.IsFinite(rest.AntimatterNeeded));
                HarnessLog.Line(FormattableString.Invariant(
                    $"{Tag} brake to rest at 0.01 c: brake {rest.Brake.Seconds / 86400.0:F1} d, ends at {rest.Brake.EndSpeed:F1} m/s ({rest.Brake.End}), antimatter {rest.AntimatterNeeded:F1} kg"));
            }
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"{Tag} FAIL: {ex}");
            ok = false;
        }

        ok &= CheckContent(session);
        ok &= CheckPropellantReads(session);

        HarnessLog.Line($"{Tag} {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    // The trip above is the stock Sol to Alpha Centauri trip. When the loaded system has both,
    // its distance and its capture speed must still be the ones the constants hold.
    private static bool CheckContent(HeadlessSession session)
    {
        PhysicalNode? sol = null;
        PhysicalNode? alpha = null;
        SystemGraph? graph = SystemGraph.Build(session.System);
        if (graph != null)
        {
            sol = graph.Find("Sol");
            alpha = graph.Find("AlphaCentauri");
        }
        if (sol == null || alpha == null)
        {
            HarnessLog.Line($"{Tag} SKIP content: the loaded system has no Sol and AlphaCentauri.");
            return true;
        }

        double distance = (alpha.Astro.GetPositionEcl() - sol.Astro.GetPositionEcl()).Length();
        double finalSpeed = Math.Sqrt(alpha.Body.Mu / InterstellarMath.AstronomicalUnit);
        bool ok = Check("the trip distance is the live Sol to AlphaCentauri distance", Math.Abs(distance - Distance) <= 1e-9 * Distance);
        ok &= Check("the final speed is the live circular speed 1 AU from AlphaCentauri", Math.Abs(finalSpeed - FinalSpeed) <= 1e-9 * FinalSpeed);
        return ok;
    }

    // The drive inputs the panel uses come from InterstellarFuel.ReadPhase, which repeats the
    // private TransferPlanner.MassAboard and TransferPlanner.TankSpace. Compare them bit for bit on
    // a real vessel, for every substance phase it carries, so the fuel estimate above is fed the
    // same numbers as the stock planner. The two methods are looked up first, so a run without a
    // vehicle save still fails when a game update renames one of them.
    private static bool CheckPropellantReads(HeadlessSession session)
    {
        Type planner = typeof(TransferPlanner);
        MethodInfo? massAboard = planner.GetMethod("MassAboard", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo? tankSpace = planner.GetMethod("TankSpace", BindingFlags.NonPublic | BindingFlags.Static);
        if (massAboard == null || tankSpace == null)
            return Check("TransferPlanner.MassAboard and TankSpace exist; re-verify the stock planner", false);

        string? saveId = Environment.GetEnvironmentVariable(TestSupport.VehicleEnvVar);
        if (string.IsNullOrEmpty(saveId))
        {
            HarnessLog.Line($"{Tag} SKIP propellant reads: {TestSupport.VehicleEnvVar} not set.");
            return true;
        }

        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home || home is not Astronomical body)
            return Check("the loaded system has a home body to orbit", false);

        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        bool ok = true;
        try
        {
            Orbit orbit = VehicleSpawner.CircularCci(home, body.MeanRadius + 500_000.0, Universe.GetElapsedTime());
            Vehicle vehicle = VehicleSpawner.SpawnFromSave(saveId, system, home, "DvMapInterstellarFuelTest", orbit);

            var phases = new HashSet<string>();
            foreach (var entry in vehicle.Parts.Moles.ModulesAndStates)
                phases.Add(entry.Module.SubstancePhase.Name);
            ok &= Check($"'{saveId}' carries at least one substance", phases.Count > 0);

            foreach (string phase in phases)
            {
                InterstellarFuel.ReadPhase(vehicle, phase, out double mass, out double space);
                double stockMass = (double)massAboard.Invoke(null, new object[] { vehicle, phase })!;
                double stockSpace = (double)tankSpace.Invoke(null, new object[] { vehicle, phase })!;
                ok &= Check($"mass of {phase} matches stock", mass == stockMass);
                ok &= Check($"tank space for {phase} matches stock", space == stockSpace);
            }
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"{Tag} FAIL propellant reads: {ex}");
            ok = false;
        }
        finally
        {
            TestSupport.DespawnNewVehicles(system, preexisting);
        }
        return ok;
    }

    private static object Read(object record, string property)
    {
        PropertyInfo? info = record.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        if (info == null)
            throw new MissingMemberException(record.GetType().Name, property);
        return info.GetValue(record)!;
    }

    private static bool Check(string what, bool pass)
    {
        HarnessLog.Line($"{Tag} TEST {what} => {TestSupport.Verdict(pass)}");
        return pass;
    }
}
