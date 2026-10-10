using DeltaVMap.Dv;
using DeltaVMap.Model;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// Checks the minor-body groups on the loaded system, rooted at the home body. With the default
// threshold a hub folds only when it carries more minor children than the threshold, so the stock
// Sol builds no minor-body group at all, while a dense system (SolDense) folds its belt into one
// group under its star. With the threshold lowered to 3, so the stock moon systems fold too, every
// group holds at least two minor bodies, every member of an outer group sits at or beyond a tenth
// of its planet's SOI, and a planet keeps at most the threshold of inner moons as lanes, plus one
// per band whose group would hold a single body. A searched moon of a minor body that is folded
// itself (Charon under Pluto) is built as its own lane, and its parent with it. The fold of each
// hub is logged, so a run on a custom system shows where its moons land.
public sealed class DenseHubsTest : IHarnessTest
{
    private const string Tag = "[dvmap-dense-hubs]";

    public string Name => "dvmap-dense-hubs";

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

            var problems = new List<string>();

            VisualTree standard = VisualTree.Build(graph, new DvCache(), home, egoState: null, BuildOptions.Default);
            List<StateNode> standardGroups = MinorGroups(standard);
            HarnessLog.Line($"{Tag} default threshold on '{system.Id}': {standard.Nodes.Count} nodes, {standardGroups.Count} minor-body groups");
            foreach (StateNode group in standardGroups)
                HarnessLog.Line($"{Tag}   {group.Id}: {group.Label}");
            if ((system.Id == "Sol" || system.Id == "SolSystemInterstellar") && standardGroups.Count > 0)
                problems.Add($"the stock Sol folds {standardGroups.Count} groups, expected none");
            CheckGroups(graph, standardGroups, BuildOptions.DefaultMinorGroupThreshold, problems);

            const int low = 3;
            var lowOptions = new BuildOptions(fullLadder: false, showMinorBodies: true, showComets: true, minorGroupThreshold: low);
            VisualTree folded = VisualTree.Build(graph, new DvCache(), home, egoState: null, lowOptions);
            List<StateNode> foldedGroups = MinorGroups(folded);
            HarnessLog.Line($"{Tag} threshold {low}: {folded.Nodes.Count} nodes, {foldedGroups.Count} minor-body groups");
            foreach (StateNode group in foldedGroups)
                HarnessLog.Line($"{Tag}   {group.Id}: {group.Label}");
            CheckGroups(graph, foldedGroups, low, problems);
            CheckInnerLanes(graph, folded, low, problems);

            CheckRevealedMoon(graph, home, problems);

            foreach (string problem in problems)
                HarnessLog.Line($"{Tag} FAIL: {problem}");
            if (problems.Count == 0)
                HarnessLog.Line($"{Tag} PASS");
            return problems.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"{Tag} FAIL: {ex}");
            return 1;
        }
    }

    // The minor-body groups of a tree, without the group of folded star systems.
    private static List<StateNode> MinorGroups(VisualTree tree)
    {
        var groups = new List<StateNode>();
        foreach (StateNode node in tree.Nodes)
        {
            if (node.Kind == StateKind.MinorGroup && !node.Id.EndsWith(".SystemGroup", StringComparison.Ordinal))
                groups.Add(node);
        }
        return groups;
    }

    private static void CheckGroups(SystemGraph graph, List<StateNode> groups, int threshold, List<string> problems)
    {
        foreach (StateNode group in groups)
        {
            IReadOnlyList<PhysicalNode> members = group.GroupMembers ?? Array.Empty<PhysicalNode>();
            if (members.Count < 2)
                problems.Add($"{group.Id} holds {members.Count} bodies, a group needs two");
            PhysicalNode? hub = graph.Find(group.Body.Id);
            if (hub == null)
            {
                problems.Add($"{group.Id} has no hub body in the graph");
                continue;
            }
            int minorChildren = 0;
            foreach (PhysicalNode child in hub.Children)
            {
                if (child.IsMinor)
                    minorChildren++;
            }
            if (minorChildren <= threshold)
                problems.Add($"{group.Id} folds under {hub.Id}, which has only {minorChildren} minor children");

            foreach (PhysicalNode member in members)
            {
                if (!member.IsMinor)
                    problems.Add($"{group.Id} folds the major body {member.Id}");
            }

            if (group.Id.EndsWith(MinorFold.IdSuffix(FoldSlot.OuterGroup), StringComparison.Ordinal))
            {
                double edge = MinorFold.InnerBandSoiFraction * (hub.Ladder.SoiRadius ?? double.PositiveInfinity);
                foreach (PhysicalNode member in members)
                {
                    double radius = OrbitalStates.TransferRadius(((IOrbiter)member.Astro).Orbit);
                    if (radius < edge)
                        problems.Add($"{group.Id} folds {member.Id} at {radius:E3} m, inside the inner band edge {edge:E3} m");
                }
            }
        }
    }

    // A folded planet keeps at most the threshold of its minor children as lanes, plus one per
    // band, because a group that would hold a single body leaves it as a lane.
    private static void CheckInnerLanes(SystemGraph graph, VisualTree tree, int threshold, List<string> problems)
    {
        var checkedHubs = new HashSet<string>();
        foreach (StateNode group in MinorGroups(tree))
        {
            PhysicalNode? hub = graph.Find(group.Body.Id);
            if (hub == null || hub.IsHubOnly || !checkedHubs.Add(hub.Id))
                continue;
            int lanes = 0;
            foreach (PhysicalNode child in hub.Children)
            {
                if (child.IsMinor && tree.FindBodyNode(child.Id, StateKind.LowOrbit) != null)
                    lanes++;
            }
            HarnessLog.Line($"{Tag}   {hub.Id}: {lanes} minor moons as lanes");
            if (lanes > threshold + 2)
                problems.Add($"{hub.Id} keeps {lanes} minor moons as lanes, the threshold is {threshold}");
        }
    }

    // With every hub folded (threshold 0), a searched moon of a minor body is still built, and so
    // is the minor body it orbits.
    private static void CheckRevealedMoon(SystemGraph graph, PhysicalNode home, List<string> problems)
    {
        PhysicalNode? moon = null;
        foreach (PhysicalNode node in graph.AllNodes)
        {
            if (node.Parent is { IsMinor: true, Parent: not null } && !ReferenceEquals(node, home) && !ReferenceEquals(node.Parent, home))
            {
                moon = node;
                break;
            }
        }
        if (moon == null)
        {
            HarnessLog.Line($"{Tag} SKIP revealed moon: no moon of a minor body in this system");
            return;
        }

        var options = new BuildOptions(fullLadder: false, showMinorBodies: true, showComets: true,
            revealedBodies: new HashSet<string> { moon.Id }, minorGroupThreshold: 0);
        VisualTree tree = VisualTree.Build(graph, new DvCache(), home, egoState: null, options);
        bool moonBuilt = IsBuilt(tree, moon.Id);
        bool parentBuilt = IsBuilt(tree, moon.Parent!.Id);
        HarnessLog.Line($"{Tag} revealed {moon.Id} under the minor body {moon.Parent.Id}: moon built {moonBuilt}, parent built {parentBuilt}");
        if (!moonBuilt || !parentBuilt)
            problems.Add($"the searched {moon.Id} is not on the map with its parent {moon.Parent.Id}");
    }

    // A small body whose SOI does not clear its surface has no low orbit, so any rung counts.
    private static bool IsBuilt(VisualTree tree, string bodyId)
    {
        foreach (StateKind kind in new[] { StateKind.LowOrbit, StateKind.Surface, StateKind.Intercept, StateKind.SoiEdge, StateKind.Hub })
        {
            if (tree.FindBodyNode(bodyId, kind) != null)
                return true;
        }
        return false;
    }
}
