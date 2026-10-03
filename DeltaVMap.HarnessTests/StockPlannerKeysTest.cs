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
// It then writes the plan type and, in a system with several roots, a destination root the way
// the button does, reads the state back and restores what stock held before. With several roots
// and a vessel in the system, it also runs the full button path, source included, and checks
// which departure root lets the destination through.
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

            Astronomical? destination = null;
            ReadOnlySpan<IIndependentRoot> roots = Universe.Roots;
            IIndependentRoot? home = session.System.HomeBody != null ? IIndependentRoot.RootOf(session.System.HomeBody) : null;
            for (int i = 0; i < roots.Length && destination == null; i++)
            {
                if (!ReferenceEquals(roots[i], home) && roots[i] is Astronomical astro)
                    destination = astro;
            }
            if (destination != null)
            {
                ok &= CheckSelection(destination);
                ok &= CheckButtonPath(session.System);
            }
            else
            {
                HarnessLog.Line($"{Tag} SKIP destination: system '{session.System.Id}' has no second root.");
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

    // The button path with a real vessel: StockDestination accepts only the root the vessel is
    // nearest to as the departure, and Preselect makes the vessel the source as well.
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
        Astronomical? other = null;
        ReadOnlySpan<IIndependentRoot> roots = Universe.Roots;
        for (int i = 0; i < roots.Length && other == null; i++)
        {
            if (!ReferenceEquals(roots[i], own) && roots[i] is Astronomical astro)
                other = astro;
        }
        if (other == null)
            return Check($"a root other than {own.Id} exists", false);

        bool ok = Check($"{own.Id} to {other.Id} is a stock destination for {vehicle.Id}",
            ReferenceEquals(StockPlannerBridge.StockDestination(vehicle, own, other), other));
        ok &= Check($"departure {other.Id} is refused for {vehicle.Id}",
            StockPlannerBridge.StockDestination(vehicle, other, own) == null);
        ok &= Check($"the vessel's own root {own.Id} is refused as the destination",
            StockPlannerBridge.StockDestination(vehicle, own, own) == null);
        ok &= CheckPlanetRoute(system, vehicle, own);

        StockPlannerBridge.PlannerState before = StockPlannerBridge.Capture();
        try
        {
            ok &= Check("button path: the preselect runs", StockPlannerBridge.Preselect(vehicle, own, other));
            StockPlannerBridge.PlannerState after = StockPlannerBridge.Capture();
            ok &= Check("button path: PLAN TYPE is Interstellar", after.Type.GetKey() == StockPlannerBridge.InterstellarKey);
            ok &= Check($"button path: the source is {vehicle.Id}", after.Source.GetKey() == vehicle.Id);
            ok &= Check($"button path: the destination is {other.Id}", after.Destination.GetKey() == other.Id);
            ok &= Check("button path: no stale transfer is shown", !after.Calculated && after.Entry == null);
        }
        finally
        {
            StockPlannerBridge.Restore(in before);
        }
        ok &= Check("button path: stock state restored", StockPlannerBridge.Capture() == before);
        return ok;
    }

    // A route to a planet of another star system preselects that system's root: stock can only
    // target a root, so the panel passes InterstellarRoute.DestinationRoot.
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
        StateNode? origin = tree.FindBodyNode(home.Id, StateKind.LowOrbit);
        StateNode? target = tree.FindBodyNode("ProximaCentaurib", StateKind.LowOrbit);
        RoutePath? path = origin != null && target != null ? RouteFinder.FindPath(origin, target) : null;
        InterstellarRoute? leg = path != null ? RouteAccumulator.Accumulate(path, graph, new RouteOptions(), RouteContext.None).Interstellar : null;
        Astronomical? destination = leg?.DestinationRoot?.Astro;
        return Check("a route to ProximaCentaurib preselects AlphaCentauri",
            destination != null && destination.Id == "AlphaCentauri"
            && ReferenceEquals(StockPlannerBridge.StockDestination(vehicle, leg!.SourceRoot.Astro, destination), destination));
    }

    private static bool Check(string what, bool pass)
    {
        HarnessLog.Line($"{Tag} TEST {what} => {TestSupport.Verdict(pass)}");
        return pass;
    }
}
