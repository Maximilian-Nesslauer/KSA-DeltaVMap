using System;
using DeltaVMap.Core;
using KSA;

namespace DeltaVMap.Mission;

// The craft whose mission the map reads: the controlled vessel in flight, else the craft in the
// vehicle editor. Vehicle is null in the editor.
internal readonly record struct MissionHost(PartTree Tree, Vehicle? Vehicle, bool InEditor);

// Reads the mission plan of a craft (PartTree.Mission) into a snapshot free of game types.
internal static class MissionReader
{
    // The same order as the staged delta-v readout resolves its tree, so the mission and the
    // delta-v bar describe one craft. Opening the editor clears Program.ControlledVehicle.
    internal static MissionHost? ResolveHost()
    {
        try
        {
            Vehicle? vehicle = Program.ControlledVehicle;
            if (vehicle != null)
                return vehicle.Parts is { } parts ? new MissionHost(parts, vehicle, false) : null;

            PartTree? tree = Program.Editor?.EditingSpace?.Parts;
            return tree != null ? new MissionHost(tree, null, true) : null;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("mission-read", $"[DvMap] Mission read failed: {ex}");
            return null;
        }
    }

    // Null when the craft has no mission plan.
    internal static MissionSnapshot? Read(PartTree tree)
    {
        try
        {
            MissionPlan? mission = tree.Mission;
            if (mission == null)
                return null;

            // Stock records the arrival as soon as the destination is on the vessel's parent chain
            // (MissionProgress.Record), so a mission to a body that encloses the home body, such
            // as the home star, counts as arrived at launch. Routing ignores such an arrival.
            bool arrived = mission.ArrivedAt.HasValue;
            if (arrived && Universe.CurrentSystem?.HomeBody is { } home)
                arrived = MissionProgress.FindRelation(home, mission.BodyId ?? "") == MissionBodyRelation.Outside;

            MissionGoal destination = mission.Destination switch
            {
                MissionDestination.LowOrbit => MissionGoal.LowOrbit,
                MissionDestination.Body => MissionGoal.Body,
                _ => MissionGoal.None,
            };
            // Stock times a LOW ORBIT mission as an orbit (MissionTiming.Compute) and keeps the
            // arrival the plan had before, which can be a flyby or a landing elsewhere.
            return new MissionSnapshot(
                destination,
                mission.BodyId ?? "",
                destination == MissionGoal.LowOrbit ? RouteArrival.Orbit : ToRoute(mission.Arrival),
                mission.Trip == MissionTrip.Return,
                mission.Stay.Seconds(),
                mission.CruiseSpeedC,
                mission.Custom,
                arrived);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("mission-read", $"[DvMap] Mission read failed: {ex}");
            return null;
        }
    }

    internal static string? HomeBodyId()
    {
        try
        {
            return Universe.CurrentSystem?.HomeBody?.Id;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("mission-read", $"[DvMap] Mission read failed: {ex}");
            return null;
        }
    }

    internal static RouteArrival ToRoute(MissionArrival arrival)
    {
        return arrival switch
        {
            MissionArrival.Flyby => RouteArrival.Flyby,
            MissionArrival.Land => RouteArrival.Land,
            _ => RouteArrival.Orbit,
        };
    }

    internal static MissionArrival ToGame(RouteArrival arrival)
    {
        return arrival switch
        {
            RouteArrival.Flyby => MissionArrival.Flyby,
            RouteArrival.Land => MissionArrival.Land,
            _ => MissionArrival.Orbit,
        };
    }
}
