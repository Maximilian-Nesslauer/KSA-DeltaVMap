using DeltaVMap.Dv;
using DeltaVMap.Mission;
using DeltaVMap.Model;
using DeltaVMap.Route;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// Locks the mission default route against the map of the loaded system. For every body stock's
// mission picker lists and every arrival, the goal of a mission there resolves to a node of that
// body, of the kind the goal asks for or of the documented fallback, the route to it from the home
// low orbit accumulates and ends on that node, and the route counts as the mission's. This runs at
// the core and at the full ladder detail, because a flyby has its intercept node only at full
// detail. Bodies without a node on the map are logged, not failed. The way home and the stock LOW
// ORBIT mission resolve to the home low orbit.
public sealed class MissionRouteTest : IHarnessTest
{
    private const string Tag = "[dvmap-mission-route]";

    public string Name => "dvmap-mission-route";

    public int Run(HeadlessSession session)
    {
        bool ok = true;
        try
        {
            CelestialSystem system = session.System;
            SystemGraph? graph = SystemGraph.Build(system);
            PhysicalNode? home = system.HomeBody != null ? graph?.Find(system.HomeBody.Id) : null;
            if (graph == null || home == null || system.HomeBody is not IParentBody homeBody)
            {
                HarnessLog.Line($"{Tag} FAIL: no graph or no home body for system '{system.Id}'.");
                return 1;
            }

            List<Astronomical> bodies = MissionCoreTest.PickerBodies(homeBody);
            foreach (bool fullLadder in new[] { false, true })
            {
                VisualTree tree = VisualTree.Build(graph, new DvCache(), home, egoState: null,
                    new BuildOptions(fullLadder, showMinorBodies: true, showComets: true));
                ok &= CheckHome(tree, home.Id, fullLadder);
                ok &= CheckBodies(tree, graph, bodies, home.Id, fullLadder);
            }
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"{Tag} FAIL: {ex}");
            ok = false;
        }

        HarnessLog.Line($"{Tag} {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool CheckHome(VisualTree tree, string homeId, bool fullLadder)
    {
        string detail = fullLadder ? "full" : "core";
        var arrived = new MissionSnapshot(MissionGoal.Body, "Elsewhere", RouteArrival.Land, Return: true,
            MissionRoutes.DefaultStaySeconds, MissionPlan.DEFAULT_CRUISE_SPEED_C, Custom: false, Arrived: true);
        var lowOrbit = new MissionSnapshot(MissionGoal.LowOrbit, "", RouteArrival.Orbit, Return: true,
            MissionRoutes.DefaultStaySeconds, MissionPlan.DEFAULT_CRUISE_SPEED_C, Custom: false, Arrived: false);
        bool ok = true;
        foreach ((string what, MissionSnapshot mission) in new[] { ("the way home", arrived), ("the LOW ORBIT mission", lowOrbit) })
        {
            MissionRouteGoal? goal = MissionRoutes.DefaultRoute(mission, homeId);
            var resolved = goal is { } g ? MissionRouteResolver.Resolve(tree, in g) : null;
            ok &= Check($"{detail}: {what} resolves to {homeId}.LowOrbit",
                resolved is { } r && r.Node.Body.Id == homeId && r.Node.Kind == StateKind.LowOrbit && r.EndOnMap == RouteEnd.Orbit);
        }
        return ok;
    }

    private static bool CheckBodies(VisualTree tree, SystemGraph graph, List<Astronomical> bodies, string homeId, bool fullLadder)
    {
        string detail = fullLadder ? "full" : "core";
        StateNode? origin = tree.FindBodyNode(homeId, StateKind.LowOrbit);
        if (origin == null)
            return Check($"{detail}: the home low orbit exists", false);

        bool ok = true;
        int checkedRoutes = 0;
        int offMap = 0;
        var arrivals = new[] { RouteArrival.Flyby, RouteArrival.Orbit, RouteArrival.Land };
        foreach (Astronomical body in bodies)
        {
            foreach (RouteArrival arrival in arrivals)
            {
                var mission = new MissionSnapshot(MissionGoal.Body, body.Id, arrival, Return: true,
                    MissionRoutes.DefaultStaySeconds, MissionPlan.DEFAULT_CRUISE_SPEED_C, Custom: false, Arrived: false);
                if (MissionRoutes.DefaultRoute(mission, homeId) is not { } goal)
                {
                    ok &= Check($"{detail}: a mission to {body.Id} ({arrival}) asks for a route", false);
                    continue;
                }

                var resolved = MissionRouteResolver.Resolve(tree, in goal);
                StateNode? expected = ExpectedNode(tree, body.Id, goal.End);
                if (resolved == null)
                {
                    if (expected != null)
                        ok &= Check($"{detail}: {body.Id} ({arrival}) resolves to {expected.Id}", false);
                    else
                        offMap++;
                    continue;
                }

                (StateNode node, RouteEnd endOnMap) = resolved.Value;
                bool sameNode = ReferenceEquals(node, expected) && node.Body.Id == body.Id
                    && endOnMap == MissionRouteResolver.EndOf(node.Kind);
                if (!sameNode)
                {
                    ok &= Check($"{detail}: {body.Id} ({arrival}) resolves to {expected?.Id ?? "nothing"}, got {node.Id}", false);
                    continue;
                }

                RoutePath? path = RouteFinder.FindPath(origin, node);
                if (path == null)
                {
                    ok &= Check($"{detail}: a path reaches {node.Id}", false);
                    continue;
                }
                var options = new RouteOptions
                {
                    LandAtDestination = goal.End == RouteEnd.Surface,
                    ShowReturnTrip = goal.Return,
                };
                RouteSummary summary = RouteAccumulator.Accumulate(path, graph, options, RouteContext.None);
                bool mine = ReferenceEquals(summary.Target, node)
                    && MissionRoutes.IsMissionRoute(goal, endOnMap, summary.Target.Body.Id,
                        MissionRouteResolver.EndOf(summary.Target.Kind), options.ShowReturnTrip);
                if (!mine)
                    ok &= Check($"{detail}: the route to {node.Id} counts as the mission to {body.Id} ({arrival})", false);
                checkedRoutes++;
            }
        }

        HarnessLog.Line($"{Tag} {detail}: {checkedRoutes} mission routes checked, {offMap} goals without a node on the map.");
        return ok & Check($"{detail}: at least one mission route was checked", checkedRoutes > 0);
    }

    // The documented order: a landing takes the surface, else the low orbit, else the intercept;
    // a flyby the intercept, else the low orbit; an orbit the low orbit, else the intercept.
    private static StateNode? ExpectedNode(VisualTree tree, string bodyId, RouteEnd end)
    {
        StateKind[] order = end switch
        {
            RouteEnd.Surface => new[] { StateKind.Surface, StateKind.LowOrbit, StateKind.Intercept },
            RouteEnd.Flyby => new[] { StateKind.Intercept, StateKind.LowOrbit },
            _ => new[] { StateKind.LowOrbit, StateKind.Intercept },
        };
        foreach (StateKind kind in order)
        {
            if (tree.FindBodyNode(bodyId, kind) is { } node)
                return node;
        }
        return null;
    }

    private static bool Check(string what, bool pass)
    {
        HarnessLog.Line($"{Tag} TEST {what} => {TestSupport.Verdict(pass)}");
        return pass;
    }
}
