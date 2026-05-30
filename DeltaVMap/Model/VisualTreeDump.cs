using System;
using System.Collections.Generic;
using Brutal.Logging;
using DeltaVMap.Dv;
using KSA;

namespace DeltaVMap.Model;

// A debug-only dump that builds the physical graph, re-roots it at a few
// representative bodies (a planet, a moon, a gas giant, and the live ego root if a
// vehicle is controlled), and logs the resulting visual tree. It verifies the
// graph model end to end without any layout or rendering: ancestors should show
// as hub buses, siblings should branch off them, ladders should be inserted, edge
// dV should be present, distant bodies should collapse to core rungs, and
// re-rooting at a moon should climb moon -> planet hub -> star hub.
internal static class VisualTreeDump
{
    private const string Tag = "[DvMap]";

    // Safety cap so a pathological or huge modded system cannot flood the log.
    private const int MaxLinesPerTree = 600;

    internal static void Run()
    {
        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
        {
            DefaultCategory.Log.Warning($"{Tag} Visual tree dump: no current system loaded.");
            return;
        }

        SystemGraph? graph = SystemGraph.Build(system);
        if (graph == null)
        {
            DefaultCategory.Log.Warning($"{Tag} Visual tree dump: could not build system graph (no star/home body).");
            return;
        }

        var cache = new DvCache();

        DefaultCategory.Log.Info($"{Tag} === Visual tree dump (system '{graph.SystemId}', {CountBodies(graph)} bodies) ===");

        DumpRootByName(graph, cache, "Earth", "planet");
        DumpRootByName(graph, cache, "Luna", "moon");
        DumpGasGiant(graph, cache);
        DumpEgoRoot(graph, cache);

        DefaultCategory.Log.Info($"{Tag} === End visual tree dump ===");
    }

    private static void DumpRootByName(SystemGraph graph, DvCache cache, string id, string role)
    {
        PhysicalNode? node = graph.Find(id);
        if (node == null)
        {
            DefaultCategory.Log.Info($"{Tag} Root '{id}' ({role}): not present in this system, skipping.");
            return;
        }
        DumpTree(graph, cache, node, egoState: null, role);
    }

    private static void DumpGasGiant(SystemGraph graph, DvCache cache)
    {
        // Prefer a well-known gas giant for a stable choice, otherwise pick the
        // largest atmosphere-bearing body with no solid surface (Id-sorted for
        // determinism if radii tie).
        PhysicalNode? chosen = graph.Find("Jupiter") ?? graph.Find("Saturn");
        if (chosen == null || chosen.Ladder.HasSurface)
        {
            chosen = null;
            foreach (PhysicalNode node in SortedById(graph))
            {
                if (node.IsStar || node.Ladder.HasSurface)
                    continue;
                if (chosen == null || node.Ladder.MeanRadius > chosen.Ladder.MeanRadius)
                    chosen = node;
            }
        }

        if (chosen == null)
        {
            DefaultCategory.Log.Info($"{Tag} Root (gas giant): none found in this system, skipping.");
            return;
        }
        DumpTree(graph, cache, chosen, egoState: null, "gas giant");
    }

    private static void DumpEgoRoot(SystemGraph graph, DvCache cache)
    {
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle?.Parent == null)
        {
            DefaultCategory.Log.Info($"{Tag} Ego root: no controlled vehicle; skipping live you-are-here dump.");
            return;
        }

        PhysicalNode? node = graph.Find(vehicle.Parent.Id);
        if (node == null)
        {
            DefaultCategory.Log.Info($"{Tag} Ego root: vehicle parent '{vehicle.Parent.Id}' not in graph, skipping.");
            return;
        }

        ClassifiedState state = StateClassifier.Classify(vehicle, node.Ladder);
        DefaultCategory.Log.Info(FormattableString.Invariant(
            $"{Tag} Ego: '{vehicle.Id}' around '{node.Id}' classified as {state.Kind} at r={state.Radius / 1000.0:F1}km."));
        DumpTree(graph, cache, node, state, "ego root");
    }

    private static void DumpTree(SystemGraph graph, DvCache cache, PhysicalNode root, ClassifiedState? egoState, string role)
    {
        VisualTree tree;
        try
        {
            tree = VisualTree.Build(graph, cache, root, egoState, BuildOptions.Default);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"{Tag} Failed to build visual tree rooted at '{root.Id}': {ex}");
            return;
        }

        DefaultCategory.Log.Info($"{Tag} --- Re-rooted at '{tree.RootBodyId}' ({role}) ---");

        int lines = 0;
        DumpNode(tree.Root, incoming: null, depth: 0, ref lines);

        DefaultCategory.Log.Info(FormattableString.Invariant(
            $"{Tag}   [{tree.Nodes.Count} nodes; {SummarizeKinds(tree)}; you-are-here: {tree.YouAreHere?.Id ?? "none"}]"));
    }

    private static void DumpNode(StateNode node, Edge? incoming, int depth, ref int lines)
    {
        if (lines >= MaxLinesPerTree)
        {
            if (lines == MaxLinesPerTree)
            {
                DefaultCategory.Log.Info($"{Tag}   ... (tree truncated at {MaxLinesPerTree} lines)");
                lines++;
            }
            return;
        }
        lines++;

        string indent = new string(' ', 2 + depth * 2);
        string marker = node.IsYouAreHere ? " <== YOU ARE HERE" : "";
        string edge = incoming == null ? "(root)" : DescribeEdge(incoming);
        DefaultCategory.Log.Info($"{Tag} {indent}{edge} {node.Label} [{node.Kind}]{marker}");

        foreach (Edge outEdge in node.Out)
            DumpNode(outEdge.To, outEdge, depth + 1, ref lines);
    }

    private static string DescribeEdge(Edge edge)
    {
        switch (edge.Kind)
        {
            case SegmentKind.Transfer:
                EdgeDv dv = edge.Transfer!.Value;
                string approx = edge.IsApproximate ? "~" : "";
                return FormattableString.Invariant(
                    $"-[Transfer {approx}dep {dv.DepartDv:F0}/arr {dv.ArriveDv:F0} m/s, t {FormatTime(edge.TransferTimeSeconds)}]->");
            case SegmentKind.HubLink:
                return "-[HubLink]->";
            default:
                return FormattableString.Invariant($"-[{edge.Kind} {edge.LadderDv:F0} m/s]->");
        }
    }

    private static string SummarizeKinds(VisualTree tree)
    {
        var counts = new Dictionary<StateKind, int>();
        foreach (StateNode node in tree.Nodes)
            counts[node.Kind] = counts.GetValueOrDefault(node.Kind) + 1;

        var parts = new List<string>();
        foreach (StateKind kind in Enum.GetValues<StateKind>())
        {
            if (counts.TryGetValue(kind, out int c) && c > 0)
                parts.Add($"{kind}={c}");
        }
        return string.Join(", ", parts);
    }

    private static IEnumerable<PhysicalNode> SortedById(SystemGraph graph)
    {
        var nodes = new List<PhysicalNode>(graph.AllNodes);
        nodes.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return nodes;
    }

    private static int CountBodies(SystemGraph graph)
    {
        return graph.AllNodes.Count;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds <= 0.0)
            return "-";
        double hours = seconds / 3600.0;
        if (hours < 1.0)
            return FormattableString.Invariant($"{seconds / 60.0:F0} min");
        if (hours < 48.0)
            return FormattableString.Invariant($"{hours:F1} h");
        double days = hours / 24.0;
        if (days < 365.0)
            return FormattableString.Invariant($"{days:F1} d");
        return FormattableString.Invariant($"{days / 365.0:F1} yr");
    }
}
