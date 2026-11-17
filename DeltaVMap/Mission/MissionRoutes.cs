using System;

namespace DeltaVMap.Mission;

// The member order of KSA.MissionDestination.
internal enum MissionGoal : byte { None, LowOrbit, Body }

// The member order of KSA.MissionArrival.
internal enum RouteArrival : byte { Flyby, Orbit, Land }

// What the target node of a route is: an orbit, the surface, or a flyby (intercept) state.
internal enum RouteEnd : byte { Orbit, Surface, Flyby }

// A vessel's mission plan, free of game types. Arrived is true only when the arrival counts for
// routing (MissionReader leaves out an arrival at a body that encloses the home body).
internal readonly record struct MissionSnapshot(
    MissionGoal Destination,
    string BodyId,
    RouteArrival Arrival,
    bool Return,
    double StaySeconds,
    double CruiseSpeedC,
    bool Custom,
    bool Arrived);

// The route a mission asks for. WayHome marks the trip back to the home body after the arrival.
internal readonly record struct MissionRouteGoal(string BodyId, RouteEnd End, bool Return, bool WayHome);

// What decides whether the map applies the mission route again. Tree compares by reference,
// because PartTree does not override Equals, so another craft is always another key.
internal readonly record struct MissionKey(
    object? Tree,
    MissionGoal Destination,
    string BodyId,
    RouteArrival Arrival,
    bool Return,
    bool Custom,
    bool Arrived);

// The fields a route writes into a stock mission plan. CruiseSpeedC is null for an in-system
// route, which leaves the plan's cruise speed alone.
internal readonly record struct MissionWrite(
    MissionGoal Destination,
    string BodyId,
    RouteArrival Arrival,
    bool Return,
    double StaySeconds,
    double? CruiseSpeedC);

// The policy between a vessel's mission and the route the map shows: which route a mission asks
// for, when that route replaces the selection, and which plan a route writes back.
internal static class MissionRoutes
{
    // MissionPlan.MIN_CRUISE_SPEED_C, MAX_CRUISE_SPEED_C, DEFAULT_CRUISE_SPEED_C and
    // DEFAULT_STAY_SECONDS.
    public const double MinCruiseSpeedC = 0.001;
    public const double MaxCruiseSpeedC = 0.8999999999999999;
    public const double DefaultCruiseSpeedC = 0.1;
    public const double DefaultStaySeconds = 86400.0;

    // The route the mission asks for, or null when it asks for none. After the arrival a return
    // mission asks for the way home, which is the Home leg of MissionTransfer.ClassifyLeg; a
    // one-way mission keeps its destination route, because a landing still has its descent to show.
    public static MissionRouteGoal? DefaultRoute(MissionSnapshot? mission, string? homeBodyId)
    {
        if (mission is not { } m || m.Custom || m.Destination == MissionGoal.None)
            return null;

        if (m.Arrived && m.Return)
        {
            return string.IsNullOrEmpty(homeBodyId)
                ? null
                : new MissionRouteGoal(homeBodyId, RouteEnd.Orbit, Return: false, WayHome: true);
        }

        if (m.Destination == MissionGoal.LowOrbit)
        {
            return string.IsNullOrEmpty(homeBodyId)
                ? null
                : new MissionRouteGoal(homeBodyId, RouteEnd.Orbit, m.Return, WayHome: false);
        }

        if (string.IsNullOrEmpty(m.BodyId))
            return null;
        RouteEnd end = m.Arrival switch
        {
            RouteArrival.Land => RouteEnd.Surface,
            RouteArrival.Flyby => RouteEnd.Flyby,
            _ => RouteEnd.Orbit,
        };
        return new MissionRouteGoal(m.BodyId, end, m.Return, WayHome: false);
    }

    public static RouteArrival ArrivalOf(RouteEnd end)
    {
        return end switch
        {
            RouteEnd.Surface => RouteArrival.Land,
            RouteEnd.Flyby => RouteArrival.Flyby,
            _ => RouteArrival.Orbit,
        };
    }

    // True when the route shown is the one the goal asks for. goalEndOnMap is the end the map
    // could resolve for the goal, which differs from goal.End for a flyby of a body without an
    // intercept node or a landing on a body without a surface, so the route the map fell back to
    // still counts as the mission's.
    public static bool IsMissionRoute(MissionRouteGoal? goal, RouteEnd? goalEndOnMap, string targetBodyId, RouteEnd end, bool returnTrip)
    {
        if (goal is not { } g)
            return false;
        return string.Equals(g.BodyId, targetBodyId, StringComparison.Ordinal)
            && g.Return == returnTrip
            && end == (goalEndOnMap ?? g.End);
    }

    public static MissionKey KeyOf(object? tree, MissionSnapshot? mission)
    {
        if (mission is not { } m)
            return new MissionKey(tree, MissionGoal.None, "", RouteArrival.Orbit, false, false, false);
        return new MissionKey(tree, m.Destination, m.BodyId ?? "", m.Arrival, m.Return, m.Custom, m.Arrived);
    }

    // Whether the mission route replaces the selection now. A change of the mission itself
    // replaces even a pick of the player, because the player changed the mission. Another craft or
    // an arrival replaces only no selection or a selection the mission made.
    public static bool ShouldApply(MissionKey? previous, MissionKey current, bool opening, bool hasSelection, bool selectionFromMission)
    {
        if (opening && !hasSelection)
            return true;
        if (previous is not { } p)
            return false;

        bool sameTree = ReferenceEquals(p.Tree, current.Tree);
        // Checked before the arrival, because a retarget also clears the recorded arrival.
        bool planChanged = p.Destination != current.Destination
            || !string.Equals(p.BodyId, current.BodyId, StringComparison.Ordinal)
            || p.Arrival != current.Arrival
            || p.Return != current.Return
            || p.Custom != current.Custom;
        if (sameTree && planChanged)
            return true;

        if (!sameTree || p.Arrived != current.Arrived)
            return !hasSelection || selectionFromMission;
        return false;
    }

    // The plan a route writes. The home body as the target is the stock LOW ORBIT mission. A stay
    // that is not finite falls back to the stock default, and the cruise speed of an interstellar
    // route is clamped to the range a stock mission accepts.
    public static MissionWrite PlanFor(string targetBodyId, string? homeBodyId, RouteEnd end, bool returnTrip, bool interstellar, double staySeconds, double cruiseSpeedC)
    {
        double stay = double.IsFinite(staySeconds) ? Math.Max(0.0, staySeconds) : DefaultStaySeconds;
        if (!string.IsNullOrEmpty(homeBodyId) && string.Equals(targetBodyId, homeBodyId, StringComparison.Ordinal))
            return new MissionWrite(MissionGoal.LowOrbit, "", RouteArrival.Orbit, returnTrip, stay, null);

        double? cruise = null;
        if (interstellar)
        {
            double c = double.IsNaN(cruiseSpeedC) ? DefaultCruiseSpeedC : cruiseSpeedC;
            cruise = Math.Clamp(c, MinCruiseSpeedC, MaxCruiseSpeedC);
        }
        return new MissionWrite(MissionGoal.Body, targetBodyId, ArrivalOf(end), returnTrip, stay, cruise);
    }
}
