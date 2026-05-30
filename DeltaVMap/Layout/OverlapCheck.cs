using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeltaVMap.Layout;

// The layout acceptance check. It verifies three independent properties of a laid
// out tree and collects human-readable violations (capped so a broken layout cannot
// flood the log):
//
//  1. No two node dots overlap. After the grid snap every node owns a distinct cell
//     and the cell is wider than the largest dot, so this should always hold; the
//     check is the safety net that makes a regression in any earlier stage visible.
//  2. No two sibling subtrees overlap. This is the tidy-tree guarantee and is checked
//     on the pre-snap X at each tree level (not per band): for adjacent children, the
//     right edge of the left subtree stays left of the left edge of the right one at
//     every shared depth.
//  3. No two placed labels overlap each other or a dot. This is the greedy placer's
//     contract; unplaced labels are reported separately, not treated as failures.
internal sealed class OverlapReport
{
    private const int MaxMessages = 25;

    public List<string> NodeOverlaps { get; } = new();
    public List<string> SubtreeOverlaps { get; } = new();
    public List<string> LabelOverlaps { get; } = new();

    // Structural invariants beyond overlap. Band violations catch a child placed at or
    // above its parent (Y leaking the wrong way); bus violations catch the hub spine
    // collapsing onto one point instead of spreading horizontally.
    public List<string> BandViolations { get; } = new();
    public List<string> BusViolations { get; } = new();

    public int NodeCount { get; set; }
    public int LabelsPlaced { get; set; }
    public int LabelsDropped { get; set; }
    public int BusNodes { get; set; }

    public bool Ok => NodeOverlaps.Count == 0 && SubtreeOverlaps.Count == 0 && LabelOverlaps.Count == 0
        && BandViolations.Count == 0 && BusViolations.Count == 0;

    public void AddNodeOverlap(string message)
    {
        if (NodeOverlaps.Count < MaxMessages)
            NodeOverlaps.Add(message);
    }

    public void AddSubtreeOverlap(string message)
    {
        if (SubtreeOverlaps.Count < MaxMessages)
            SubtreeOverlaps.Add(message);
    }

    public void AddLabelOverlap(string message)
    {
        if (LabelOverlaps.Count < MaxMessages)
            LabelOverlaps.Add(message);
    }

    public void AddBandViolation(string message)
    {
        if (BandViolations.Count < MaxMessages)
            BandViolations.Add(message);
    }

    public void AddBusViolation(string message)
    {
        if (BusViolations.Count < MaxMessages)
            BusViolations.Add(message);
    }

    public string Summary()
    {
        string verdict = Ok ? "PASS" : "FAIL";
        return string.Create(CultureInfo.InvariantCulture,
            $"{verdict}: {NodeCount} nodes, dot-overlaps={NodeOverlaps.Count}, subtree-overlaps={SubtreeOverlaps.Count}, label-overlaps={LabelOverlaps.Count}, band={BandViolations.Count}, bus={BusViolations.Count} ({BusNodes} bus nodes), labels {LabelsPlaced}/{NodeCount} placed ({LabelsDropped} dropped)");
    }
}

internal static class OverlapCheck
{
    // Boxes may touch but not interpenetrate; allow a hair of slack for float noise.
    private const double Epsilon = 1e-6;

    public static OverlapReport Run(LayoutResult result)
    {
        LayoutTree tree = result.Tree;
        var report = new OverlapReport
        {
            NodeCount = tree.Nodes.Count,
            LabelsPlaced = result.Labels.Placed,
            LabelsDropped = result.Labels.Dropped
        };

        CheckNodes(tree, result.Config, report);
        // The sibling-subtree X-extent guarantee is specific to the cumulative pass,
        // where a body's rungs are X-spread siblings. GravityWell stacks a body's rungs
        // at one X (separated in Y), so adjacent "siblings" legitimately share an X;
        // there the dot-overlap check is the no-overlap guarantee instead.
        if (result.Config.Mode != LayoutMode.GravityWell)
            CheckSiblingSubtrees(tree, report);
        CheckLabels(tree, result.Config, report);
        if (result.Config.Mode == LayoutMode.GravityWell)
            CheckWellBands(tree, report);
        else
            CheckBands(tree, report);
        CheckHubBus(tree, report, result.Config.Mode == LayoutMode.GravityWell);
        return report;
    }

    // Band monotonicity: a normal edge must drop its child strictly below the parent
    // (so Y always reads as more dV downward and parent and child never share a row),
    // while a HubLink must keep both endpoints on the same band.
    private static void CheckBands(LayoutTree tree, OverlapReport report)
    {
        foreach (LayoutNode node in tree.Nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                LayoutNode child = edge.To;
                if (edge.IsHubLink)
                {
                    if (child.Band != node.Band)
                        report.AddBandViolation($"hub link '{node.Id}' -> '{child.Id}' crosses bands ({node.Band} -> {child.Band})");
                }
                else if (child.Band <= node.Band)
                {
                    report.AddBandViolation($"edge '{node.Id}' -> '{child.Id}' does not descend ({node.Band} -> {child.Band})");
                }
            }
        }
    }

    // GravityWell well integrity. Y is a signed within-body offset around the spine, so
    // the cumulative "every edge descends" rule does not apply. Instead: every low orbit
    // and hub anchors the spine (band 0); surfaces sit below it and high orbits / capture
    // above; and a body's rungs occupy distinct bands so they stack without colliding.
    private static void CheckWellBands(LayoutTree tree, OverlapReport report)
    {
        var perColumn = new Dictionary<string, HashSet<int>>();
        foreach (LayoutNode node in tree.Nodes)
        {
            if ((node.Kind == LayoutKind.LowOrbit || node.Kind == LayoutKind.Hub) && node.Band != 0)
                report.AddBandViolation($"'{node.Id}' ({node.Kind}) is off the spine (band {node.Band})");

            if (node.Kind == LayoutKind.Surface && node.Band < 0)
                report.AddBandViolation($"surface '{node.Id}' sits above the spine (band {node.Band})");

            if ((node.Kind == LayoutKind.Stationary || node.Kind == LayoutKind.SoiEdge || node.Kind == LayoutKind.Intercept)
                && node.Band > 0)
                report.AddBandViolation($"'{node.Id}' ({node.Kind}) sits below the spine (band {node.Band})");

            if (node.Band != 0)
            {
                if (!perColumn.TryGetValue(node.Column, out HashSet<int>? bands))
                {
                    bands = new HashSet<int>();
                    perColumn[node.Column] = bands;
                }
                if (!bands.Add(node.Band))
                    report.AddBandViolation($"column '{node.Column}' has two rungs on band {node.Band} (well rungs collide)");
            }
        }
    }

    // Hub-bus integrity: every node reachable from the root through HubLink edges must
    // spread across distinct columns rather than collapsing onto one. In CumulativeDown
    // they must also share the root band (the bus is one horizontal dV row). GravityWell
    // does not have that band rule: a hub link can land on a destination's Intercept (the
    // interplanetary-cruise root attaches planets that way), which legitimately sits above
    // the spine in its own well, so only the distinct-column rule applies there (the spine
    // anchoring is checked per-column by CheckWellBands). A single-node bus passes trivially.
    private static void CheckHubBus(LayoutTree tree, OverlapReport report, bool gravityWell)
    {
        var bus = new List<LayoutNode>();
        CollectBus(tree.Root, bus);
        report.BusNodes = bus.Count;

        int rootBand = tree.Root.Band;
        var seenColumns = new HashSet<int>();
        foreach (LayoutNode node in bus)
        {
            if (!gravityWell && node.Band != rootBand)
                report.AddBusViolation($"bus node '{node.Id}' left the hub band ({node.Band} != {rootBand})");
            if (!seenColumns.Add(node.Col))
                report.AddBusViolation($"bus node '{node.Id}' shares column {node.Col} with another hub (bus collapsed, not horizontal)");
        }
    }

    private static void CollectBus(LayoutNode node, List<LayoutNode> bus)
    {
        bus.Add(node);
        foreach (LayoutEdge edge in node.Out)
        {
            if (edge.IsHubLink)
                CollectBus(edge.To, bus);
        }
    }

    // Dot-overlap: bucket node centres by grid cell, then compare each node only with
    // nodes in its own and the eight surrounding cells. Two dots overlap when the
    // centre distance is less than the sum of their radii.
    private static void CheckNodes(LayoutTree tree, LayoutConfig cfg, OverlapReport report)
    {
        var byCell = new Dictionary<(int, int), List<LayoutNode>>();
        foreach (LayoutNode node in tree.Nodes)
        {
            var key = (node.Col, node.Row);
            if (!byCell.TryGetValue(key, out List<LayoutNode>? list))
            {
                list = new List<LayoutNode>();
                byCell[key] = list;
            }
            list.Add(node);
        }

        foreach (LayoutNode a in tree.Nodes)
        {
            for (int dCol = -1; dCol <= 1; dCol++)
            {
                for (int dRow = -1; dRow <= 1; dRow++)
                {
                    if (!byCell.TryGetValue((a.Col + dCol, a.Row + dRow), out List<LayoutNode>? list))
                        continue;
                    foreach (LayoutNode b in list)
                    {
                        // Compare each unordered pair once.
                        if (string.CompareOrdinal(a.Id, b.Id) >= 0)
                            continue;
                        double dx = a.SnappedX - b.SnappedX;
                        double dy = a.SnappedY - b.SnappedY;
                        double minDist = a.DotRadius + b.DotRadius;
                        if (dx * dx + dy * dy < minDist * minDist - Epsilon)
                        {
                            double dist = Math.Sqrt(dx * dx + dy * dy);
                            report.AddNodeOverlap(string.Create(CultureInfo.InvariantCulture,
                                $"'{a.Id}' and '{b.Id}' dots overlap: dist={dist:F1} < {minDist:F1} (cells {a.Col},{a.Row} and {b.Col},{b.Row})"));
                        }
                    }
                }
            }
        }
    }

    // Sibling-subtree separation, validated on the tidy-tree X at each tree level.
    private static void CheckSiblingSubtrees(LayoutTree tree, OverlapReport report)
    {
        foreach (LayoutNode parent in tree.Nodes)
        {
            if (parent.Out.Count < 2)
                continue;

            for (int i = 0; i + 1 < parent.Out.Count; i++)
            {
                LayoutNode left = parent.Out[i].To;
                LayoutNode right = parent.Out[i + 1].To;

                Dictionary<int, double> leftMaxRight = ExtentsByDepth(left, takeMax: true);
                Dictionary<int, double> rightMinLeft = ExtentsByDepth(right, takeMax: false);

                foreach ((int depth, double maxRight) in leftMaxRight)
                {
                    if (!rightMinLeft.TryGetValue(depth, out double minLeft))
                        continue;
                    if (maxRight - minLeft > Epsilon)
                    {
                        report.AddSubtreeOverlap(string.Create(CultureInfo.InvariantCulture,
                            $"under '{parent.Id}': subtrees '{left.Id}' and '{right.Id}' overlap at depth {depth}: leftRight={maxRight:F1} > rightLeft={minLeft:F1}"));
                    }
                }
            }
        }
    }

    // Per-depth horizontal extent of a subtree using pre-snap X and node width:
    // takeMax returns the rightmost edge per depth, otherwise the leftmost.
    private static Dictionary<int, double> ExtentsByDepth(LayoutNode subtreeRoot, bool takeMax)
    {
        var extents = new Dictionary<int, double>();
        var stack = new Stack<LayoutNode>();
        stack.Push(subtreeRoot);
        while (stack.Count > 0)
        {
            LayoutNode node = stack.Pop();
            double edge = takeMax ? node.X + node.Width / 2.0 : node.X - node.Width / 2.0;
            if (extents.TryGetValue(node.Depth, out double current))
                extents[node.Depth] = takeMax ? Math.Max(current, edge) : Math.Min(current, edge);
            else
                extents[node.Depth] = edge;

            foreach (LayoutEdge child in node.Out)
                stack.Push(child.To);
        }
        return extents;
    }

    // Placed labels must not cover each other or any dot. Built independently from the
    // placer so it genuinely re-checks the result rather than trusting it.
    private static void CheckLabels(LayoutTree tree, LayoutConfig cfg, OverlapReport report)
    {
        var labels = new List<(string Id, LabelPlacer.Rect Rect)>();
        foreach (LayoutNode node in tree.Nodes)
        {
            if (node.LabelPlaced)
                labels.Add((node.Id, new LabelPlacer.Rect(node.LabelX, node.LabelY, node.Width, node.Height)));
        }

        for (int i = 0; i < labels.Count; i++)
        {
            for (int j = i + 1; j < labels.Count; j++)
            {
                if (labels[i].Rect.Intersects(labels[j].Rect))
                {
                    report.AddLabelOverlap($"labels of '{labels[i].Id}' and '{labels[j].Id}' overlap");
                }
            }
        }

        foreach ((string id, LabelPlacer.Rect rect) in labels)
        {
            foreach (LayoutNode node in tree.Nodes)
            {
                double r = node.DotRadius;
                var dot = new LabelPlacer.Rect(node.SnappedX - r, node.SnappedY - r, 2 * r, 2 * r);
                if (rect.Intersects(dot))
                {
                    report.AddLabelOverlap($"label of '{id}' covers dot of '{node.Id}'");
                    break;
                }
            }
        }
    }
}
