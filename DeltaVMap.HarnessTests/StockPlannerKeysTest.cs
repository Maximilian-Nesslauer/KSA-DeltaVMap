using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Model;
using DeltaVMap.Route;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// The OPEN STOCK TRANSFER PLANNER button preselects the interstellar plan through private
// TransferPlanner statics. A game update that renames or retypes one of them only degrades the
// button to opening the window, so this test resolves every key and fails on any that is gone.
// It then writes the plan type and, in a system with several roots, a destination star the way
// the button does, reads the state back and restores what stock held before. Stock offers only
// the stars outside the vessel's own root (TransferPlanner.PopulateWithStars), so with several
// roots and a vessel in the system it also runs the full button path, source included, checks
// which departure root and which destinations get through, and checks the star a route
// preselects.
public sealed class StockPlannerKeysTest : IHarnessTest
{
    private const string Tag = "[dvmap-stock-planner]";

    public string Name => "dvmap-stock-planner";

    public int Run(HeadlessSession session)
    {
        bool ok = true;
        try
        {
            List<string> missing = StockPlannerBridge.MissingKeys();
            foreach (string key in missing)
                HarnessLog.Line($"{Tag} missing key: {key}");
            ok &= Check("every TransferPlanner key resolves", missing.Count == 0);
            if (missing.Count > 0)
            {
                HarnessLog.Line($"{Tag} {TestSupport.Verdict(false)}");
                return 1;
            }

            ok &= CheckSelection(null);

            IIndependentRoot? home = session.System.HomeBody != null ? IIndependentRoot.RootOf(session.System.HomeBody) : null;
            StellarBody? destination = FirstStarOutside(home);
            if (destination != null)
            {
                ok &= CheckSelection(destination);
                ok &= CheckButtonPath(session.System);
            }
            else
            {
                HarnessLog.Line($"{Tag} SKIP destination: system '{session.System.Id}' has no star outside the home root.");
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

    private static bool CheckSelection(Astronomical? destination)
    {
        string label = destination == null ? "type only" : $"destination {destination.Id}";
        StockPlannerBridge.PlannerState before = StockPlannerBridge.Capture();
        bool ok;
        try
        {
            ok = Check($"{label}: the selection runs", StockPlannerBridge.TrySelectInterstellar(destination));
            StockPlannerBridge.PlannerState after = StockPlannerBridge.Capture();
            ok &= Check($"{label}: PLAN TYPE is Interstellar", after.Type.GetKey() == StockPlannerBridge.InterstellarKey);
            ok &= Check($"{label}: the time unit is years", after.TimeUnit.Unit == TimeUnit.Years);
            ok &= Check($"{label}: no stale transfer is shown", !after.Calculated && after.Entry == null);
            // A type change clears the destination, and a plan that already is interstellar keeps it.
            string expected = destination?.Id
                ?? (before.Type.GetKey() == StockPlannerBridge.InterstellarKey ? before.Destination.GetKey() : "N/A");
            ok &= Check($"{label}: the destination is {expected}", after.Destination.GetKey() == expected);
        }
        finally
        {
            StockPlannerBridge.Restore(in before);
        }
        ok &= Check($"{label}: stock state restored", StockPlannerBridge.Capture() == before);
        return ok;
    }

    // The first star outside root, in the Universe.Stars order stock lists them in.
    private static StellarBody? FirstStarOutside(IIndependentRoot? root)
    {
        ReadOnlySpan<StellarBody> stars = Universe.Stars;
        for (int i = 0; i < stars.Length; i++)
        {
            if (!ReferenceEquals(IIndependentRoot.RootOf(stars[i]), root))
                return stars[i];
        }
        return null;
    }

    // The button path with a real vessel: StockDestination accepts only the root the vessel is
    // nearest to as the departure and only a star stock lists as the destination, and Preselect
    // makes the vessel the source as well.
    private static bool CheckButtonPath(CelestialSystem system)
    {
        Vehicle? vehicle = null;
        for (int i = 0; i < system.Count && vehicle == null; i++)
        {
            if (system.GetIndex(i) is Vehicle v)
                vehicle = v;
        }
        if (vehicle == null)
        {
            HarnessLog.Line($"{Tag} SKIP button path: system '{system.Id}' has no vessel.");
            return true;
        }

        if (Universe.NearestRoot(vehicle.GetPositionEcl()) is not Astronomical own)
            return Check($"vessel {vehicle.Id} has a nearest root", false);
        StellarBody? other = FirstStarOutside((IIndependentRoot)own);
        if (other == null)
            return Check($"a star outside {own.Id} exists", false);
        if (IIndependentRoot.RootOf(other) is not Astronomical otherRoot)
            return Check($"star {other.Id} has a root", false);

        bool ok = Check($"{own.Id} to {other.Id} is a stock destination for {vehicle.Id}",
            ReferenceEquals(StockPlannerBridge.StockDestination(vehicle, own, other), other));
        ok &= Check($"departure {otherRoot.Id} is refused for {vehicle.Id}",
            StockPlannerBridge.StockDestination(vehicle, otherRoot, other) == null);
        StellarBody? ownStar = Universe.NearestStar(vehicle.GetPositionEcl());
        ok &= Check($"the vessel's own star {ownStar?.Id} is refused as the destination",
            ownStar != null && StockPlannerBridge.StockDestination(vehicle, own, ownStar) == null);
        Barycenter? barycenter = null;
        ReadOnlySpan<IIndependentRoot> roots = Universe.Roots;
        for (int i = 0; i < roots.Length && barycenter == null; i++)
        {
            if (!ReferenceEquals(roots[i], own) && roots[i] is Barycenter b)
                barycenter = b;
        }
        if (barycenter != null)
        {
            ok &= Check($"the barycenter {barycenter.Id} is refused as the destination",
                StockPlannerBridge.StockDestination(vehicle, own, barycenter) == null);
        }
        else
        {
            HarnessLog.Line($"{Tag} SKIP barycenter destination: system '{system.Id}' has no barycenter root.");
        }
        ok &= CheckPlanetRoute(system, vehicle, own);
        ok &= CheckRouteStars(system, vehicle, own);

        StockPlannerBridge.PlannerState before = StockPlannerBridge.Capture();
        try
        {
            ok &= Check("button path: the preselect runs", StockPlannerBridge.Preselect(vehicle, own, other));
            StockPlannerBridge.PlannerState after = StockPlannerBridge.Capture();
            ok &= Check("button path: PLAN TYPE is Interstellar", after.Type.GetKey() == StockPlannerBridge.InterstellarKey);
            ok &= Check($"button path: the source is {vehicle.Id}", after.Source.GetKey() == vehicle.Id);
            ok &= Check($"button path: the destination is the star {other.Id}",
                after.Destination.Body is StellarBody star && star.Id == other.Id);
            ok &= Check("button path: no stale transfer is shown", !after.Calculated && after.Entry == null);
            // Stock keeps any star as its destination, but its DESTINATION combo shows a star only
            // when its own list holds it, else the first entry, so a pick outside the list would
            // show one star while stock plans for another.
            var list = new TransferObject[Universe.Stars.Length];
            int count = 0;
            TransferPlanner.PopulateWithStars(list, ref count);
            bool listed = false;
            for (int i = 0; i < count; i++)
                listed |= list[i].GetKey() == other.Id;
            ok &= Check($"button path: stock lists {other.Id}, so its combo shows the pick", listed);
        }
        finally
        {
            StockPlannerBridge.Restore(in before);
        }
        ok &= Check("button path: stock state restored", StockPlannerBridge.Capture() == before);
        return ok;
    }

    // A route to a planet of another star system preselects the star it orbits, while the route
    // still names that system's root for the map.
    private static bool CheckPlanetRoute(CelestialSystem system, Vehicle vehicle, Astronomical own)
    {
        SystemGraph? graph = SystemGraph.Build(system);
        PhysicalNode? home = graph != null && system.HomeBody != null ? graph.Find(system.HomeBody.Id) : null;
        if (graph == null || home == null || graph.Find("ProximaCentaurib") == null || !ReferenceEquals(SystemGraph.SystemRootOf(home).Astro, own))
        {
            HarnessLog.Line($"{Tag} SKIP planet route: needs the stock interstellar bodies and a vessel in the home system.");
            return true;
        }
        VisualTree tree = VisualTree.Build(graph, new DvCache(), home, null, BuildOptions.Default);
        InterstellarRoute? leg = RouteTo(graph, tree, home, "ProximaCentaurib");
        Astronomical? destination = leg?.DestinationStar?.Astro;
        return Check("a route to ProximaCentaurib preselects ProximaCentauri in AlphaCentauri",
            destination != null && destination.Id == "ProximaCentauri" && leg!.DestinationRoot?.Id == "AlphaCentauri"
            && ReferenceEquals(StockPlannerBridge.StockDestination(vehicle, leg.SourceRoot.Astro, destination), destination));
    }

    // A route to the parking orbit of every other system root preselects the root itself when it
    // is a star, else the most massive star under it by the masses the graph reads. A route to the
    // parking orbit of a star of a multiple system preselects exactly that star, also when it is
    // not the most massive one. Stock must accept each pick.
    private static bool CheckRouteStars(CelestialSystem system, Vehicle vehicle, Astronomical own)
    {
        SystemGraph? graph = SystemGraph.Build(system);
        PhysicalNode? home = graph != null && system.HomeBody != null ? graph.Find(system.HomeBody.Id) : null;
        if (graph == null || home == null || !ReferenceEquals(SystemGraph.SystemRootOf(home).Astro, own))
        {
            HarnessLog.Line($"{Tag} SKIP route stars: needs a vessel in the home system.");
            return true;
        }
        VisualTree tree = VisualTree.Build(graph, new DvCache(), home, null, BuildOptions.Default);
        bool ok = true;
        foreach (PhysicalNode root in graph.Roots)
        {
            if (ReferenceEquals(root, graph.HomeRoot))
                continue;
            var stars = new List<PhysicalNode>();
            CollectStars(root, stars);
            PhysicalNode? heaviest = null;
            foreach (PhysicalNode star in stars)
            {
                if (heaviest == null || star.Body.Mass > heaviest.Body.Mass)
                    heaviest = star;
            }
            ok &= CheckRouteStar(graph, tree, home, vehicle, root, heaviest);
            foreach (PhysicalNode star in stars)
            {
                if (!ReferenceEquals(star, root))
                    ok &= CheckRouteStar(graph, tree, home, vehicle, star, star);
            }
        }
        return ok;
    }

    private static bool CheckRouteStar(SystemGraph graph, VisualTree tree, PhysicalNode home, Vehicle vehicle, PhysicalNode target, PhysicalNode? expected)
    {
        if (tree.FindBodyNode(target.Id, StateKind.LowOrbit) == null)
        {
            HarnessLog.Line($"{Tag} SKIP route star {target.Id}: its parking orbit is not in the map.");
            return true;
        }
        InterstellarRoute? leg = RouteTo(graph, tree, home, target.Id);
        PhysicalNode? star = leg?.DestinationStar;
        bool ok = Check($"a route to {target.Id} preselects {expected?.Id}", star != null && ReferenceEquals(star, expected));
        if (leg != null && star != null)
        {
            ok &= Check($"stock accepts {star.Id} for {vehicle.Id}",
                ReferenceEquals(StockPlannerBridge.StockDestination(vehicle, leg.SourceRoot.Astro, star.Astro), star.Astro));
        }
        return ok;
    }

    private static void CollectStars(PhysicalNode node, List<PhysicalNode> stars)
    {
        if (node.IsStar)
            stars.Add(node);
        foreach (PhysicalNode child in node.Children)
            CollectStars(child, stars);
    }

    // The interstellar leg of the route from the home body's low orbit to the low or parking orbit
    // of bodyId, the node MapWindow.ResolveTarget picks for a click on that body.
    private static InterstellarRoute? RouteTo(SystemGraph graph, VisualTree tree, PhysicalNode home, string bodyId)
    {
        StateNode? origin = tree.FindBodyNode(home.Id, StateKind.LowOrbit);
        StateNode? target = tree.FindBodyNode(bodyId, StateKind.LowOrbit);
        RoutePath? path = origin != null && target != null ? RouteFinder.FindPath(origin, target) : null;
        return path != null ? RouteAccumulator.Accumulate(path, graph, new RouteOptions(), RouteContext.None).Interstellar : null;
    }

    private static bool Check(string what, bool pass)
    {
        HarnessLog.Line($"{Tag} TEST {what} => {TestSupport.Verdict(pass)}");
        return pass;
    }
}
