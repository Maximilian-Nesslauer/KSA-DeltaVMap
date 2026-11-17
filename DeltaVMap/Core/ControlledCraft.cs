using System;
using KSA;

namespace DeltaVMap.Core;

// The craft the map plans for, read in one place so that every panel describes the same craft.
internal static class ControlledCraft
{
    // The controlled vessel, or null when there is none or its parent body cannot be read.
    // Vehicle.Parent goes through Vehicle.Orbit and Vehicle.Patch to FlightPlan.Patches[0], which
    // throws while the flight plan has no patch (a vessel freshly spawned or mid transition).
    internal static Vehicle? VehicleWithParent()
    {
        try
        {
            Vehicle? vehicle = Program.ControlledVehicle;
            return vehicle?.Parent != null ? vehicle : null;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("controlled-vehicle", $"[DvMap] Could not read the controlled vehicle: {ex.Message}");
            return null;
        }
    }

    // The part tree of the controlled vessel in flight, else of the craft in the vehicle editor.
    // vehicle is null in the editor, because Program.OnFrameEditor clears Program.ControlledVehicle
    // when it opens the editor. The staged delta-v readout and the mission block both resolve
    // their craft here.
    internal static PartTree? ResolveTree(out Vehicle? vehicle)
    {
        vehicle = Program.ControlledVehicle;
        if (vehicle != null)
            return vehicle.Parts;
        return Program.Editor?.EditingSpace?.Parts;
    }
}
