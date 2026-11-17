using System;
using DeltaVMap.Core;
using KSA;

namespace DeltaVMap.Mission;

// Writes a route into a craft's stock mission plan, the way stock's own mission changes write it:
// in the vehicle editor as the mission picker (MissionPicker) does, in flight as the transfer
// planner's UPDATE MISSION button (MissionTransfer.Retarget) does.
internal static class MissionWriter
{
    // Why a stock mission cannot name a route to target that ends at end, or null when it can.
    // The accepted set is the one MissionPicker offers: the home body only as its LOW ORBIT
    // mission, because MissionPicker.DrawBodyOptions skips it among the bodies, every star except
    // the root of the home system, and every other body on a bound orbit; never a barycenter, a
    // root that is no star, or a vessel. home is MissionReader.HomeBody.
    internal static string? RefusalFor(Astronomical target, RouteEnd end, IParentBody? home)
    {
        if (home != null && ReferenceEquals(target, home))
            return end == RouteEnd.Orbit ? null : "A STOCK MISSION TO THE HOME BODY IS LOW ORBIT ONLY";
        switch (target)
        {
            case Vehicle:
                return "A VESSEL CANNOT BE A MISSION DESTINATION";
            case StellarBody:
                return home != null && ReferenceEquals(target, RootOf(home)) ? "STOCK MISSIONS CANNOT TARGET THE HOME STAR" : null;
            case Celestial celestial:
                if (celestial.Orbit == null)
                    return "STOCK MISSIONS CANNOT TARGET THIS BODY";
                return celestial.Orbit.IsBound() ? null : "STOCK MISSIONS CANNOT TARGET A BODY ON AN OPEN ORBIT";
            default:
                return "STOCK MISSIONS CANNOT TARGET A BARYCENTER, PICK ONE OF ITS STARS";
        }
    }

    // The top of body's parent chain, as MissionPicker.FindRoot walks it.
    private static IParentBody RootOf(IParentBody body)
    {
        while (body is IOrbiter orbiter && orbiter.Parent is { } parent)
            body = parent;
        return body;
    }

    // False when the write failed. The caller checks RefusalFor first, and plan comes from
    // MissionRoutes.PlanFor.
    internal static bool Write(in MissionHost host, in MissionWrite plan)
    {
        try
        {
            PartTree tree = host.Tree;
            if (host.InEditor)
            {
                // MissionPicker.BeginEdit, its destination, plan and stay writes, then EndEdit, as
                // one step of the editor's undo history. Stock's editor never writes ArrivedAt or
                // LeftAt, so they stay as they are.
                VehicleEditor.SyncHistoryBeforeEditFor(tree);
                MissionPlan mission = tree.Mission ?? new MissionPlan();
                mission.Destination = plan.Destination == MissionGoal.LowOrbit ? MissionDestination.LowOrbit : MissionDestination.Body;
                mission.BodyId = plan.BodyId;
                WritePlan(mission, in plan);
                mission.Custom = false;
                tree.Mission = mission;
                VehicleEditor.MarkHistoryStepFor(tree, EditorHistoryAction.Mission);
                tree.HasUnsavedChanges = true;
            }
            else
            {
                // Stock's flight UI only retargets an existing plan; a vessel without one gets a
                // new plan, as the editor's picker creates one. Clearing ArrivedAt is what
                // MissionTransfer.Retarget does; stock records the arrival again on the next
                // vessel update when the vessel is already inside the new destination. No undo
                // history and no unsaved mark exist in flight.
                MissionPlan mission = tree.Mission ?? new MissionPlan();
                if (plan.Destination == MissionGoal.LowOrbit)
                {
                    mission.Destination = MissionDestination.LowOrbit;
                    mission.BodyId = "";
                    mission.Custom = false;
                    mission.ArrivedAt = null;
                    mission.LeftAt = null;
                }
                else
                {
                    MissionTransfer.Retarget(mission, plan.BodyId);
                }
                WritePlan(mission, in plan);
                tree.Mission = mission;
            }
            return true;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("mission-write",
                $"[DvMap] Mission write of '{plan.BodyId}' ({plan.Destination}, {(host.InEditor ? "editor" : "flight")}) failed: {ex}");
            return false;
        }
    }

    private static void WritePlan(MissionPlan mission, in MissionWrite plan)
    {
        mission.Arrival = MissionReader.ToGame(plan.Arrival);
        mission.Trip = plan.Return ? MissionTrip.Return : MissionTrip.OneWay;
        mission.Stay = new UniverseTime(plan.StaySeconds);
        if (plan.CruiseSpeedC is double c)
            mission.CruiseSpeedC = c;
    }
}
