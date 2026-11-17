using DeltaVMap.Dv;
using DeltaVMap.Model;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// Checks the wait for the return window in the transfer window list against the stock mission
// timing. The list takes the wait from MissionTiming.ComputeReturnWaitSeconds with the transfer
// time of the map's dV cache, so for every sibling window of the home body that time must equal
// the one stock times a mission with: OrbitalTransfers.HohmannSeconds between the stock circular
// radii of the two orbits.
public sealed class ReturnWindowTest : IHarnessTest
{
    private const string Tag = "[dvmap-return-window]";

    private const double TransferTolerance = 1e-9;

    public string Name => "dvmap-return-window";

    public bool OptIn => false;

    public int Run(HeadlessSession session)
    {
        try
        {
            CelestialSystem system = session.System;
            SystemGraph? graph = SystemGraph.Build(system);
            PhysicalNode? home = graph != null && system.HomeBody != null ? graph.Find(system.HomeBody.Id) : null;
            if (graph == null || home == null)
            {
                HarnessLog.Line($"{Tag} FAIL: no graph or home body for system '{system.Id}'.");
                return 1;
            }

            ReRootResult reroot = ReRooter.ReRoot(home);
            if (reroot.Spine.Count == 0 || home.Astro is not IOrbiter)
            {
                HarnessLog.Line($"{Tag} SKIP: the home body '{home.Id}' has no hub above it.");
                return 0;
            }

            var siblings = new HashSet<string>();
            foreach (PhysicalNode sibling in reroot.Spine[0].OtherChildren)
                siblings.Add(sibling.Id);

            List<TransferWindowInfo> windows = TransferWindows.Build(new DvCache(), home, siblings, Universe.GetElapsedTime());
            if (windows.Count == 0)
            {
                HarnessLog.Line($"{Tag} SKIP: no sibling window from '{home.Id}'.");
                return 0;
            }

            var problems = new List<string>();
            foreach (TransferWindowInfo window in windows)
                CheckWindow(window, problems);

            foreach (string problem in problems)
                HarnessLog.Line($"{Tag} FAIL: {problem}");
            if (problems.Count == 0)
                HarnessLog.Line($"{Tag} PASS ({windows.Count} windows from '{home.Id}')");
            return problems.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"{Tag} FAIL: {ex}");
            return 1;
        }
    }

    private static void CheckWindow(TransferWindowInfo window, List<string> problems)
    {
        if (window.Source is not IOrbiter source || window.Target is not IOrbiter target)
        {
            problems.Add($"window '{window.SourceId}' -> '{window.TargetId}' is not between two orbiters");
            return;
        }

        Orbit home = source.Orbit;
        Orbit destination = target.Orbit;
        double transfer = OrbitalTransfers.HohmannSeconds(
            home.Parent.Mu, OrbitalTransfers.GetCircularRadius(home), OrbitalTransfers.GetCircularRadius(destination));

        HarnessLog.Line(FormattableString.Invariant(
            $"{Tag} {window.SourceId} -> {window.TargetId}: transfer {window.TransferTimeSeconds:F0} s (stock {transfer:F0}), wait {window.ReturnWaitSeconds:F0} s"));

        if (!(Math.Abs(window.TransferTimeSeconds - transfer) <= TransferTolerance * Math.Abs(transfer)))
            problems.Add(FormattableString.Invariant(
                $"'{window.TargetId}': transfer time {window.TransferTimeSeconds} s, stock Hohmann {transfer} s"));
    }
}
