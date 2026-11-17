using DeltaVMap.Dv;
using DeltaVMap.Model;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// The map decides open or bound by the game's own orbit type (Orbit.IsBound), not by the
// eccentricity, because an orbit within 1e-6 of e = 1 is a Parabolic Unbound orbit also with e
// just below 1. For every body of the loaded system this test checks that the graph sorts open
// orbits after every bound sibling, and that the transfer radius is the periapsis of an open
// orbit and a finite, positive radius for a bound one. A system without an open orbit logs a SKIP
// for the open part.
public sealed class OrbitBoundaryTest : IHarnessTest
{
    private const string Tag = "[dvmap-orbit-boundary]";

    public string Name => "dvmap-orbit-boundary";

    public int Run(HeadlessSession session)
    {
        bool ok = true;
        try
        {
            SystemGraph? graph = SystemGraph.Build(session.System);
            if (graph == null)
            {
                HarnessLog.Line($"{Tag} FAIL: no graph for system '{session.System.Id}'.");
                return 1;
            }
            int open = 0;
            int bound = 0;
            foreach (PhysicalNode root in graph.Roots)
                ok &= CheckChildren(root, ref open, ref bound);
            HarnessLog.Line($"{Tag} {bound} bound and {open} open orbits checked in '{session.System.Id}'.");
            ok &= Check("the system has bound orbits", bound > 0);
            if (open == 0)
                HarnessLog.Line($"{Tag} SKIP open orbits: system '{session.System.Id}' has no body on an open orbit.");
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"{Tag} FAIL: {ex}");
            ok = false;
        }

        HarnessLog.Line($"{Tag} {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool CheckChildren(PhysicalNode node, ref int open, ref int bound)
    {
        bool ok = true;
        int lastBound = -1;
        int firstOpen = -1;
        for (int i = 0; i < node.Children.Count; i++)
        {
            PhysicalNode child = node.Children[i];
            if (child.Astro is not IOrbiter orbiter || orbiter.Orbit == null)
                continue;
            Orbit orbit = orbiter.Orbit;
            double radius = OrbitalStates.TransferRadius(orbit);
            if (orbit.IsBound())
            {
                bound++;
                lastBound = i;
                if (!(double.IsFinite(radius) && radius > 0.0))
                    ok &= Check($"{child.Id}: the transfer radius {radius} of a bound orbit is finite and positive", false);
            }
            else
            {
                open++;
                if (firstOpen < 0)
                    firstOpen = i;
                ok &= Check($"{child.Id} (e = {orbit.Eccentricity:R}): the transfer radius of an open orbit is its periapsis",
                    radius == orbit.Periapsis);
            }
            ok &= CheckChildren(child, ref open, ref bound);
        }
        if (firstOpen >= 0)
        {
            ok &= Check($"{node.Id}: the open orbits sort after every bound sibling",
                lastBound < firstOpen);
        }
        return ok;
    }

    private static bool Check(string what, bool pass)
    {
        HarnessLog.Line($"{Tag} TEST {what} => {TestSupport.Verdict(pass)}");
        return pass;
    }
}
