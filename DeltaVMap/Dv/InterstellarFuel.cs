using System;
using KSA;

namespace DeltaVMap.Dv;

// Everything the antimatter-drive estimate depends on. A record struct, so an unchanged input set
// compares equal and the estimate is not run again.
internal readonly record struct DriveFuelInputs(
    InterstellarDrive Drive,
    double Mass,
    double HydrogenAboard,
    double CruiseSpeed,
    double DepartureAntimatter,
    double BrakeAntimatter,
    double FinalSpeed,
    double GasDensity,
    double Distance,
    double HydrogenSpace);

// The result of the drive estimate: the speed-up burn, the brake burn, the coast between them and
// the hydrogen the scoop collects on the way.
internal readonly record struct DriveFuelPlan(
    InterstellarBurnEstimate Departure,
    InterstellarBurnEstimate Brake,
    double CoastSeconds,
    double Scooped)
{
    public double TimeOfFlightSeconds => Departure.Seconds + CoastSeconds + Brake.Seconds;
    public double AntimatterNeeded => Departure.Antimatter + Brake.Antimatter;
    public double HydrogenNeeded => Departure.CarriedHydrogen + Brake.CarriedHydrogen - Scooped;

    // The speed-up burn reached some speed at all; without that there is no trip.
    public bool Departs => Departure.EndSpeed > 0.0;

    // The brake ends above the final speed (same tolerance as the game's planner readout).
    public bool BrakeFallsShort(double finalSpeed) => Brake.EndSpeed > finalSpeed * 1.001 + 1.0;
}

// The controlled vessel's antimatter drive and the propellant around it, read once per refresh.
internal readonly record struct VesselDrive(
    InterstellarDrive Drive,
    double Mass,
    double AntimatterAboard,
    double HydrogenAboard,
    double HydrogenSpace);

// Fuel estimate for a vessel with an antimatter ram drive. All the physics is the game's own
// public InterstellarBurnPlan (Burn integrates the drive, CoastCollection the scoop); this file
// only feeds it, in the same order as the game's interstellar planner: a speed-up burn over at
// most half the distance, a brake burn on the hydrogen left, the coast collection, and the brake
// again with the scooped hydrogen when the scoop collected any. Matching that order keeps this
// readout equal to the stock planner's for the same inputs.
internal static class InterstellarFuel
{
    public static DriveFuelPlan Plan(in DriveFuelInputs inputs)
    {
        InterstellarDrive drive = inputs.Drive;
        InterstellarBurnEstimate departure = InterstellarBurnPlan.Burn(in drive, inputs.Mass, 0.0, inputs.CruiseSpeed,
            inputs.DepartureAntimatter, inputs.HydrogenAboard, 0.5 * inputs.Distance, inputs.GasDensity);
        double hydrogenLeft = Math.Max(0.0, inputs.HydrogenAboard - departure.CarriedHydrogen);
        double brakeDistance = inputs.Distance - departure.Distance;
        InterstellarBurnEstimate brake = InterstellarBurnPlan.Burn(in drive, departure.EndMass, departure.EndSpeed, inputs.FinalSpeed,
            inputs.BrakeAntimatter, hydrogenLeft, brakeDistance, inputs.GasDensity);

        double coast = departure.EndSpeed > 0.0
            ? Math.Max(0.0, inputs.Distance - departure.Distance - brake.Distance) / departure.EndSpeed
            : 0.0;
        double scooped = InterstellarBurnPlan.CoastCollection(in drive, inputs.GasDensity, departure.EndSpeed, coast,
            inputs.HydrogenSpace + departure.CarriedHydrogen);
        if (scooped > 0.0)
        {
            brake = InterstellarBurnPlan.Burn(in drive, departure.EndMass + scooped, departure.EndSpeed, inputs.FinalSpeed,
                inputs.BrakeAntimatter, hydrogenLeft + scooped, brakeDistance, inputs.GasDensity);
        }
        return new DriveFuelPlan(departure, brake, coast, scooped);
    }

    // Read the vessel's drive and propellant. False when no ACTIVE engine carries an antimatter
    // ram core (InterstellarDrive.TryGetFrom skips unstaged engines).
    public static bool TryReadVessel(Vehicle vehicle, out VesselDrive vessel)
    {
        vessel = default;
        if (!InterstellarDrive.TryGetFrom(vehicle, out InterstellarDrive drive))
            return false;
        if (drive.Antimatter == null || drive.ReactionMass == null)
            return false;

        ReadPhase(vehicle, drive.Antimatter.Name, out double antimatterAboard, out _);
        ReadPhase(vehicle, drive.ReactionMass.Name, out double hydrogenAboard, out double hydrogenSpace);
        vessel = new VesselDrive(drive, vehicle.Props.TotalMass, antimatterAboard, hydrogenAboard, hydrogenSpace);
        return true;
    }

    // The mass of one substance phase aboard and the free tank room for it, the same sums as the
    // game planner's private TransferPlanner.MassAboard and TransferPlanner.TankSpace. The free
    // room is the mass a container would hold full minus the mass in it now, evaluated in float
    // as stock does, so the drive inputs equal the stock planner's to the last bit.
    public static void ReadPhase(Vehicle vehicle, string phaseName, out double mass, out double space)
    {
        mass = 0.0;
        space = 0.0;
        foreach (var entry in vehicle.Parts.Moles.ModulesAndStates)
        {
            if (entry.Module.SubstancePhase.Name != phaseName)
                continue;
            mass += entry.State.Mass;
            float storedVolumePerKg = entry.Module.GetStoredVolume(1f);
            if (storedVolumePerKg > 0f)
                space += Math.Max(0.0, entry.Module.ContainerVolume / storedVolumePerKg - entry.State.Mass);
        }
    }
}
