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
//  4. A wide fan that continues in rows below its hub keeps the rows honest. The offsets
//     are whole grid cells and only grow downward, the rows keep the fan's order, clear
//     each other and start at one X, and the layout-only prefix of an edge leaves its dV
//     part exactly as long as it would be without the wrap. A map with no wrapped fan passes
//     these rules unchanged.
internal sealed class OverlapReport
{
    private const int MaxMessages = 25;

    public List<string> NodeOverlaps { get; } = new();
    public List<string> SubtreeOverlaps { get; } = new();
    public List<string> LabelOverlaps { get; } = new();

    // Structural invariants beyond overlap. Band violations catch a child placed at or
    // above its parent (Y leaking the wrong way) and a node whose Y left its dV band; bus
    // violations catch the hub spine collapsing onto one point instead of spreading
    // horizontally.
    public List<string> BandViolations { get; } = new();
    public List<string> BusViolations { get; } = new();
    public List<string> WrapViolations { get; } = new();

    public int NodeCount { get; set; }
    public int LabelsPlaced { get; set; }
    public int LabelsDropped { get; set; }
    public int BusNodes { get; set; }
    public int WrapCount { get; set; }

    public bool Ok => NodeOverlaps.Count == 0 && SubtreeOverlaps.Count == 0 && LabelOverlaps.Count == 0
        && BandViolations.Count == 0 && BusViolations.Count == 0 && WrapViolations.Count == 0;

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

    public void AddWrapViolation(string message)
    {
        if (WrapViolations.Count < MaxMessages)
            WrapViolations.Add(message);
    }

    // The wrap figures join the line only when a fan is wrapped or a wrap rule failed, so the
    // summary of an unwrapped map reads as it always has.
    public string Summary()
    {
        string verdict = Ok ? "PASS" : "FAIL";
        string wrap = WrapCount > 0 || WrapViolations.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $", wrap={WrapViolations.Count} ({WrapCount} wrapped fans)")
            : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{verdict}: {NodeCount} nodes, dot-overlaps={NodeOverlaps.Count}, subtree-overlaps={SubtreeOverlaps.Count}, label-overlaps={LabelOverlaps.Count}, band={BandViolations.Count}, bus={BusViolations.Count} ({BusNodes} bus nodes){wrap}, labels {LabelsPlaced}/{NodeCount} placed ({LabelsDropped} dropped)");
    }
}

internal static class OverlapCheck
{
    // Boxes may touch but not interpenetrate; allow a hair of slack for float noise.
    private const double Epsilon = 1e-6;

    // How far a line that carries no dV keeps from a dot or a label plate, in layout pixels. It
    // is the clearance the scene checks ask of a connector in screen pixels. The label placer and
    // the row gutter keep the same distance.
    internal const double WrapLineClearPx = 6.0;

    public static OverlapReport Run(LayoutResult result)
    {
        LayoutTree tree = result.Tree;
        var report = new OverlapReport
        {
            NodeCount = tree.Nodes.Count,
            LabelsPlaced = result.Labels.Placed,
            LabelsDropped = result.Labels.Dropped
        };

        // The dot-overlap and label checks are the universal no-overlap guarantee and run
        // in every mode. The remaining checks are tree-shape invariants: the sibling-subtree
        // X-extent guarantee is specific to the cumulative pass (GravityWell stacks a body's
        // rungs at one X, the spring layout has no tree shape at all), the band check differs
        // per mode, and the hub-bus check only means something for the two tidy-tree modes.
        // The wrap rules run in every mode, so a wrap that reached the spring layout fails.
        LayoutMode mode = result.Config.Mode;
        CheckNodes(tree, result.Config, report);
        CheckLabels(tree, result.Config, report);

        switch (mode)
        {
            case LayoutMode.CumulativeDown:
                CheckSiblingSubtrees(tree, report);
                CheckBands(tree, report);
                CheckBandY(tree, result.Config.BandHeightPx, report);
                CheckHubBus(tree, report, gravityWell: false);
                break;
            case LayoutMode.GravityWell:
                CheckWellBands(tree, report);
                CheckBandY(tree, result.Config.WellBandHeightPx, report);
                CheckHubBus(tree, report, gravityWell: true);
                break;
            case LayoutMode.Spring:
                // Free-form: no band / bus / subtree shape to assert; dots and labels only.
                break;
        }
        CheckWraps(result, report);

        return report;
    }

    // The universal no-overlap checks (dots and labels) over a whole composed scene, so a part
    // placed beside the ego map can never cover a dot or a label of another part. The tree-shape
    // checks run per part, on each part's own LayoutResult.
    public static OverlapReport RunScene(LayoutScene scene)
    {
        var merged = new LayoutTree { Name = scene.Ego.Tree.Name, Root = scene.Root, Nodes = scene.Nodes };
        var report = new OverlapReport { NodeCount = scene.Nodes.Count };
        CheckNodes(merged, scene.Config, report);
        CheckLabels(merged, scene.Config, report);
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

    // Y matches the dV band. The band is the quantized dV of a node (cumulative from the root,
    // or the signed well offset in GravityWell), and the pre-snap Y must be exactly that band
    // times the band height plus the layout offset of the node's shelf, so no pass after the
    // band assignment moves a node off the height that states its dV. The band checks above
    // read Band only, so without this rule a Y that drifted from its band would pass them
    // unseen. The grid snap is free to round Y, which is why the check reads Y and not SnappedY.
    private static void CheckBandY(LayoutTree tree, double bandHeightPx, OverlapReport report)
    {
        foreach (LayoutNode node in tree.Nodes)
        {
            double expected = node.Band * bandHeightPx + node.WrapDy;
            if (Math.Abs(node.Y - expected) > Epsilon)
            {
                report.AddBandViolation(string.Create(CultureInfo.InvariantCulture,
                    $"'{node.Id}' Y={node.Y:F1} is off its dV band {node.Band} (expected {expected:F1})"));
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
    // spread across distinct columns rather than collapsing onto one. When the bus itself
    // wraps into shelves, two bus nodes may share a column on different rows, so the key
    // becomes the grid cell. In CumulativeDown
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

        bool busWraps = false;
        foreach (LayoutNode node in bus)
            busWraps |= node.WrapRow > 0;

        int rootBand = tree.Root.Band;
        var seenCells = new HashSet<(int Col, int Row)>();
        foreach (LayoutNode node in bus)
        {
            if (!gravityWell && node.Band != rootBand)
                report.AddBusViolation($"bus node '{node.Id}' left the hub band ({node.Band} != {rootBand})");
            if (!seenCells.Add((node.Col, busWraps ? node.Row : 0)))
            {
                report.AddBusViolation(busWraps
                    ? $"bus node '{node.Id}' shares cell ({node.Col},{node.Row}) with another hub (bus collapsed)"
                    : $"bus node '{node.Id}' shares column {node.Col} with another hub (bus collapsed, not horizontal)");
            }
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

    // Sibling-subtree separation, validated on the tidy-tree X at each tree level. The rows of a
    // wrapped fan sit on different heights and may share X, so each row is checked on its own
    // and a sibling is compared with the next sibling of the same row. Without a wrap every
    // child has row -1 and this is the plain neighbour walk.
    private static void CheckSiblingSubtrees(LayoutTree tree, OverlapReport report)
    {
        var lastInRow = new Dictionary<int, LayoutNode>();
        foreach (LayoutNode parent in tree.Nodes)
        {
            if (parent.Out.Count < 2)
                continue;

            lastInRow.Clear();
            foreach (LayoutEdge edge in parent.Out)
            {
                LayoutNode right = edge.To;
                if (!lastInRow.TryGetValue(right.WrapRow, out LayoutNode? left))
                {
                    lastInRow[right.WrapRow] = right;
                    continue;
                }
                lastInRow[right.WrapRow] = right;

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

    #region Wrapped fans

    // The rules of a wide fan that continues in rows below its hub. Each one names what it
    // guards in its message. With no wrapped fan they reduce to "no node or edge carries a wrap
    // value", which an unwrapped map meets without any change.
    private static void CheckWraps(LayoutResult result, OverlapReport report)
    {
        LayoutTree tree = result.Tree;
        LayoutConfig cfg = result.Config;
        report.WrapCount = result.Wraps.Count;

        var wrapsByParent = new Dictionary<string, LayoutWrap>();
        foreach (LayoutWrap wrap in result.Wraps)
        {
            if (!wrapsByParent.TryAdd(wrap.ParentId, wrap))
                report.AddWrapViolation($"hub '{wrap.ParentId}' has two wrap records");
        }

        CheckWrapOffsets(tree, cfg, wrapsByParent, report);
        CheckWrapPrefixes(tree, cfg, wrapsByParent, report);

        if (result.Wraps.Count > 0)
        {
            var nodesById = new Dictionary<string, LayoutNode>();
            foreach (LayoutNode node in tree.Nodes)
                nodesById.TryAdd(node.Id, node);

            bool rungsInRows = WrapGeometry.RungsInRows(cfg.Mode);
            double bandHeightPx = cfg.Mode == LayoutMode.GravityWell ? cfg.WellBandHeightPx : cfg.BandHeightPx;
            foreach (LayoutWrap wrap in result.Wraps)
            {
                if (!nodesById.TryGetValue(wrap.ParentId, out LayoutNode? hub))
                {
                    report.AddWrapViolation($"a wrap names hub '{wrap.ParentId}', which is not in the tree");
                    continue;
                }
                // The geometry rules below need well-formed rows to measure.
                if (!CheckRowOrder(hub, wrap, cfg, report))
                    continue;
                List<LayoutNode>[] rows = WrapGeometry.RowMembers(hub, wrap, rungsInRows);
                CheckTrunk(hub, wrap, report);
                CheckRowClearance(hub, rows, bandHeightPx, report);
                CheckRowStarts(hub, wrap, rows, cfg.GridPx, report);
                CheckWrapClearance(tree, hub, wrap, rows, report);
                if (cfg.Mode == LayoutMode.GravityWell)
                    CheckWellShelves(hub, rows, report);
            }
        }

        if (cfg.Mode == LayoutMode.GravityWell)
            CheckWellsRightOfParents(tree, report);
    }

    // WrapDy is a whole number of grid cells and never shrinks on the way down from the root,
    // because a row only ever adds height below its hub. In Cumulative-down every node of one
    // shelf shares one offset, so the depth of two nodes on that shelf still compares as dV
    // across fans. A row is set only on the children of a wrapped hub, and a map with no
    // wrapped fan carries no row, shelf or offset at all.
    private static void CheckWrapOffsets(LayoutTree tree, LayoutConfig cfg, Dictionary<string, LayoutWrap> wrapsByParent, OverlapReport report)
    {
        bool anyWrap = wrapsByParent.Count > 0;
        if (anyWrap && cfg.Mode == LayoutMode.Spring)
            report.AddWrapViolation($"the spring layout has no rows, yet {wrapsByParent.Count} fans are wrapped");

        var shelfDy = new Dictionary<int, double>();
        foreach (LayoutNode node in tree.Nodes)
        {
            if (!anyWrap && (node.WrapRow != -1 || node.Shelf != 0 || node.WrapDy != 0.0))
            {
                report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                    $"'{node.Id}' carries row {node.WrapRow}, shelf {node.Shelf}, offset {node.WrapDy:F1} although no fan is wrapped"));
                continue;
            }

            double cells = node.WrapDy / cfg.GridPx;
            if (Math.Abs(cells - Math.Round(cells)) > Epsilon)
            {
                report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                    $"'{node.Id}' row offset {node.WrapDy:F1} is not a whole number of {cfg.GridPx:F0} px grid cells"));
            }

            if (node.WrapRow >= 0 && (node.Parent == null || !wrapsByParent.ContainsKey(node.Parent.Id)))
                report.AddWrapViolation($"'{node.Id}' is in row {node.WrapRow} but its hub is not wrapped");

            LayoutNode? parent = node.Parent;
            if (parent == null)
            {
                if (node.WrapDy != 0.0 || node.Shelf != 0)
                    report.AddWrapViolation($"root '{node.Id}' is not on shelf 0 with no offset");
            }
            else
            {
                if (node.WrapDy < parent.WrapDy - Epsilon)
                {
                    report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                        $"'{node.Id}' row offset {node.WrapDy:F1} is above its parent's {parent.WrapDy:F1}"));
                }
                if (node.Shelf < parent.Shelf)
                    report.AddWrapViolation($"'{node.Id}' is on shelf {node.Shelf}, above its parent's shelf {parent.Shelf}");
            }

            if (cfg.Mode == LayoutMode.CumulativeDown
                && !shelfDy.TryAdd(node.Shelf, node.WrapDy)
                && Math.Abs(shelfDy[node.Shelf] - node.WrapDy) > Epsilon)
            {
                report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                    $"'{node.Id}' on shelf {node.Shelf} has offset {node.WrapDy:F1}, another node of that shelf has {shelfDy[node.Shelf]:F1}"));
            }
        }
    }

    // The rows of a wrapped hub, read in order, give back the hub's Out order, so the wrap moves
    // no lane in front of another. The rungs stay in row 0, a "+N" group lands in the last row,
    // and every lane sits RowDy of its row below the hub. A balanced fan has no row of a single
    // lane and no two rows that differ by more than one lane, counted without the revealed
    // lanes, which join the row of the lane before them. A hub link that carries the bus on past
    // the fan is no lane and takes no row. Returns whether the rows are well formed.
    private static bool CheckRowOrder(LayoutNode hub, LayoutWrap wrap, LayoutConfig cfg, OverlapReport report)
    {
        bool ok = true;
        void Fail(string message)
        {
            ok = false;
            report.AddWrapViolation(message);
        }

        int rowCount = wrap.RowSizes.Count;
        if (wrap.Mode != cfg.Mode)
            Fail($"the wrap of '{hub.Id}' was planned for {wrap.Mode}, the layout is {cfg.Mode}");
        if (rowCount < 2 || wrap.RowDy.Count != rowCount)
        {
            Fail($"the wrap of '{hub.Id}' has {rowCount} row sizes and {wrap.RowDy.Count} row offsets, it needs two rows or more with one offset each");
            return false;
        }
        if (Math.Abs(wrap.RowDy[0]) > Epsilon)
            Fail(string.Create(CultureInfo.InvariantCulture, $"row 0 of '{hub.Id}' has offset {wrap.RowDy[0]:F1}, not 0"));
        for (int r = 1; r < rowCount; r++)
        {
            if (wrap.RowDy[r] <= wrap.RowDy[r - 1] + Epsilon)
                Fail(string.Create(CultureInfo.InvariantCulture, $"row {r} of '{hub.Id}' at offset {wrap.RowDy[r]:F1} is not below row {r - 1} at {wrap.RowDy[r - 1]:F1}"));
        }

        var lanes = new int[rowCount];
        var unrevealed = new int[rowCount];
        int lastRow = 0;
        foreach (LayoutEdge edge in hub.Out)
        {
            LayoutNode child = edge.To;
            int row = child.WrapRow;
            if (edge.Class == EdgeClass.Ladder)
            {
                if (row != 0)
                    Fail($"rung '{child.Id}' of '{hub.Id}' is in row {row}, not row 0");
                else if (Math.Abs(child.WrapDy - hub.WrapDy) > Epsilon)
                    Fail($"rung '{child.Id}' of '{hub.Id}' has a row offset of its own");
                continue;
            }
            if (row == -1 && edge.IsHubLink)
                continue;
            if (row < 0 || row >= rowCount)
            {
                Fail($"lane '{child.Id}' of '{hub.Id}' is in row {row}, outside rows 0 to {rowCount - 1}");
                continue;
            }
            if (row < lastRow)
                Fail($"lane '{child.Id}' of '{hub.Id}' is in row {row} after a lane in row {lastRow}, so the rows change the fan's order");
            lastRow = Math.Max(lastRow, row);
            lanes[row]++;
            if (!child.IsRevealed)
                unrevealed[row]++;
            if (child.Kind == LayoutKind.MinorGroup && row != rowCount - 1)
                Fail($"group '{child.Id}' of '{hub.Id}' is in row {row}, not in the last row {rowCount - 1}");
            if (Math.Abs(child.WrapDy - hub.WrapDy - wrap.RowDy[row]) > Epsilon)
            {
                Fail(string.Create(CultureInfo.InvariantCulture,
                    $"lane '{child.Id}' of '{hub.Id}' sits {child.WrapDy - hub.WrapDy:F1} below its hub, row {row} is {wrap.RowDy[row]:F1} below"));
            }
        }

        int fewest = int.MaxValue;
        int most = 0;
        for (int r = 0; r < rowCount; r++)
        {
            if (lanes[r] != wrap.RowSizes[r])
                Fail($"row {r} of '{hub.Id}' holds {lanes[r]} lanes, the wrap records {wrap.RowSizes[r]}");
            if (lanes[r] == 0)
                Fail($"row {r} of '{hub.Id}' is empty");
            if (wrap.Balanced && unrevealed[r] < 2)
                Fail($"row {r} of '{hub.Id}' holds a single lane");
            fewest = Math.Min(fewest, unrevealed[r]);
            most = Math.Max(most, unrevealed[r]);
        }
        if (wrap.Balanced && most - fewest > 1)
            Fail($"the rows of '{hub.Id}' are not balanced, they hold {fewest} to {most} lanes");
        return ok;
    }

    // The shared trunk runs from the hub over to the gutter and down to the last row, with only
    // horizontal and vertical segments.
    private static void CheckTrunk(LayoutNode hub, LayoutWrap wrap, OverlapReport report)
    {
        IReadOnlyList<LayoutPoint> pts = wrap.Trunk;
        if (pts.Count < 2)
        {
            report.AddWrapViolation($"the wrap of '{hub.Id}' has no trunk");
            return;
        }
        if (!SamePoint(pts[0], new LayoutPoint(hub.SnappedX, hub.SnappedY)))
            report.AddWrapViolation($"the trunk of '{hub.Id}' does not start at its hub");
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            if (!AxisAligned(pts[i], pts[i + 1]))
                report.AddWrapViolation($"segment {i} of the trunk of '{hub.Id}' is neither horizontal nor vertical");
        }
        var end = new LayoutPoint(wrap.TrunkX, hub.SnappedY + wrap.RowDy[^1]);
        if (!SamePoint(pts[^1], end))
        {
            report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                $"the trunk of '{hub.Id}' ends at ({pts[^1].X:F1},{pts[^1].Y:F1}), not at the last row ({end.X:F1},{end.Y:F1})"));
        }
    }

    // The rows clear each other. Label plates included, a row reaches no higher than the row
    // above it reaches down, and between their dots lies at least one band of empty height.
    private static void CheckRowClearance(LayoutNode hub, List<LayoutNode>[] rows, double bandHeightPx, OverlapReport report)
    {
        for (int r = 1; r < rows.Length; r++)
        {
            double bottomAbove = PlateSpan(rows[r - 1]).Bottom;
            double topBelow = PlateSpan(rows[r]).Top;
            if (topBelow < bottomAbove - Epsilon)
            {
                report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                    $"row {r} of '{hub.Id}' reaches up to y={topBelow:F1}, into row {r - 1}, which reaches down to y={bottomAbove:F1}"));
            }

            double gap = MinY(rows[r]) - MaxY(rows[r - 1]);
            if (gap < bandHeightPx - Epsilon)
            {
                report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                    $"row {r} of '{hub.Id}' sits {gap:F1} px below row {r - 1}, less than one band of {bandHeightPx:F0} px"));
            }
        }
    }

    // Every row of a block starts at the X where row 0 starts, within one grid cell of snapping,
    // so the rows read like lines of text. The trunk runs down past every row, so its X must lie
    // outside each row's node boxes.
    private static void CheckRowStarts(LayoutNode hub, LayoutWrap wrap, List<LayoutNode>[] rows, double gridPx, OverlapReport report)
    {
        double start = BoxLeft(rows[0]);
        for (int r = 0; r < rows.Length; r++)
        {
            double left = BoxLeft(rows[r]);
            double right = BoxRight(rows[r]);
            if (r > 0 && Math.Abs(left - start) > gridPx + Epsilon)
            {
                report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                    $"row {r} of '{hub.Id}' starts at x={left:F1}, row 0 at x={start:F1}"));
            }
            if (wrap.TrunkX > left - Epsilon && wrap.TrunkX < right + Epsilon)
            {
                report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                    $"the trunk of '{hub.Id}' at x={wrap.TrunkX:F1} runs through row {r} ({left:F1} to {right:F1})"));
            }
        }
    }

    // The layout-only prefix of an edge into a lower row starts at its hub, runs only
    // horizontally and vertically, and ends on the ghost point on the trunk at the row's height.
    // From there the dV part is the exact run, diagonal and drop the router draws for an
    // unwrapped edge, so the drop into the lane keeps the length that states its dV. Every other
    // edge has no prefix.
    private static void CheckWrapPrefixes(LayoutTree tree, LayoutConfig cfg, Dictionary<string, LayoutWrap> wrapsByParent, OverlapReport report)
    {
        foreach (LayoutNode node in tree.Nodes)
        {
            wrapsByParent.TryGetValue(node.Id, out LayoutWrap? wrap);
            foreach (LayoutEdge edge in node.Out)
            {
                LayoutNode child = edge.To;
                IReadOnlyList<LayoutPoint> pts = edge.Polyline;
                bool lowerRow = wrap != null && child.WrapRow >= 1 && child.WrapRow < wrap.RowDy.Count;
                if (!lowerRow)
                {
                    if (edge.DvStart != 0)
                        report.AddWrapViolation($"edge '{node.Id}' -> '{child.Id}' has a layout prefix but enters no lower row");
                    continue;
                }
                if (edge.DvStart < 1 || edge.DvStart > pts.Count - 2)
                {
                    report.AddWrapViolation($"edge '{node.Id}' -> '{child.Id}' into row {child.WrapRow} has prefix end {edge.DvStart} on a polyline of {pts.Count} points");
                    continue;
                }

                if (!SamePoint(pts[0], new LayoutPoint(node.SnappedX, node.SnappedY)))
                    report.AddWrapViolation($"the prefix of '{node.Id}' -> '{child.Id}' does not start at its hub");
                for (int i = 0; i < edge.DvStart; i++)
                {
                    if (!AxisAligned(pts[i], pts[i + 1]))
                        report.AddWrapViolation($"prefix segment {i} of '{node.Id}' -> '{child.Id}' is neither horizontal nor vertical");
                }

                var ghost = new LayoutPoint(wrap!.TrunkX, node.SnappedY + wrap.RowDy[child.WrapRow]);
                if (!SamePoint(pts[edge.DvStart], ghost))
                {
                    report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                        $"the dV part of '{node.Id}' -> '{child.Id}' starts at ({pts[edge.DvStart].X:F1},{pts[edge.DvStart].Y:F1}), not on the trunk at ({ghost.X:F1},{ghost.Y:F1})"));
                    continue;
                }

                IReadOnlyList<LayoutPoint> tail = EdgeRouter.BuildPolyline(ghost, new LayoutPoint(child.SnappedX, child.SnappedY), cfg.EdgeDiagonalPx);
                bool same = tail.Count == pts.Count - edge.DvStart;
                for (int i = 0; same && i < tail.Count; i++)
                    same = SamePoint(tail[i], pts[edge.DvStart + i]);
                if (!same)
                    report.AddWrapViolation($"the dV part of '{node.Id}' -> '{child.Id}' is not the unwrapped run, diagonal and drop from the trunk");
            }
        }
    }

    // Layout clearance of the lines that carry no dV. The trunk and every prefix keep
    // WrapLineClearPx from each dot but their hub's and from each label plate, so no label
    // hides where a row break runs. The dV part of a lane in a lower row starts below the
    // row above and must not cross a dot of it.
    private static void CheckWrapClearance(LayoutTree tree, LayoutNode hub, LayoutWrap wrap, List<LayoutNode>[] rows, OverlapReport report)
    {
        const double clear = WrapLineClearPx;
        var lines = new List<(string What, LayoutPoint A, LayoutPoint B)>();
        for (int i = 0; i + 1 < wrap.Trunk.Count; i++)
            lines.Add(($"the trunk of '{hub.Id}'", wrap.Trunk[i], wrap.Trunk[i + 1]));
        foreach (LayoutEdge edge in hub.Out)
        {
            for (int i = 0; i < edge.DvStart && i + 1 < edge.Polyline.Count; i++)
                lines.Add(($"the prefix of '{hub.Id}' -> '{edge.To.Id}'", edge.Polyline[i], edge.Polyline[i + 1]));
        }

        foreach ((string what, LayoutPoint a, LayoutPoint b) in lines)
        {
            foreach (LayoutNode node in tree.Nodes)
            {
                if (node != hub)
                {
                    double d = SceneComposer.SegmentPointDistance(a, b, new LayoutPoint(node.SnappedX, node.SnappedY));
                    if (d < node.DotRadius + clear - Epsilon)
                    {
                        report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                            $"{what} passes {d - node.DotRadius:F1} px from the dot of '{node.Id}'"));
                    }
                }
                if (node.LabelPlaced && SceneComposer.SegmentHitsRect(a, b,
                    node.LabelX - clear, node.LabelY - clear, node.LabelX + node.Width + clear, node.LabelY + node.Height + clear))
                {
                    report.AddWrapViolation($"{what} runs within {clear:F0} px of the label of '{node.Id}'");
                }
            }
        }

        foreach (LayoutEdge edge in hub.Out)
        {
            int row = edge.To.WrapRow;
            if (row < 1 || row >= rows.Length || edge.DvStart < 1)
                continue;
            for (int i = edge.DvStart; i + 1 < edge.Polyline.Count; i++)
            {
                foreach (LayoutNode node in rows[row - 1])
                {
                    if (SceneComposer.SegmentPointDistance(edge.Polyline[i], edge.Polyline[i + 1], new LayoutPoint(node.SnappedX, node.SnappedY)) < node.DotRadius - Epsilon)
                        report.AddWrapViolation($"the dV part of '{hub.Id}' -> '{edge.To.Id}' in row {row} crosses '{node.Id}' in row {row - 1}");
                }
            }
        }
    }

    // GravityWell keeps each planet system in one shelf. The whole subtree of a lane stays out
    // of the height of every other row of its hub, so no system is split across shelves.
    private static void CheckWellShelves(LayoutNode hub, List<LayoutNode>[] rows, OverlapReport report)
    {
        var spans = new (double Top, double Bottom)[rows.Length];
        for (int r = 0; r < rows.Length; r++)
            spans[r] = PlateSpan(rows[r]);

        foreach (LayoutEdge edge in hub.Out)
        {
            int row = edge.To.WrapRow;
            if (edge.Class == EdgeClass.Ladder || row < 0 || row >= rows.Length)
                continue;
            (double top, double bottom) = PlateSpan(LayoutTree.PreOrder(edge.To));
            for (int r = 0; r < rows.Length; r++)
            {
                if (r != row && top < spans[r].Bottom - Epsilon && bottom > spans[r].Top + Epsilon)
                    report.AddWrapViolation($"the system of '{edge.To.Id}' in row {row} of '{hub.Id}' reaches into row {r}");
            }
        }
    }

    // GravityWell places every well that a transfer or hub link leads to right of the well it
    // leaves, in every row, so a moon never reads left of its planet.
    private static void CheckWellsRightOfParents(LayoutTree tree, OverlapReport report)
    {
        foreach (LayoutNode node in tree.Nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.Class != EdgeClass.Ladder && edge.To.X <= node.X + Epsilon)
                {
                    report.AddWrapViolation(string.Create(CultureInfo.InvariantCulture,
                        $"'{edge.To.Id}' at x={edge.To.X:F1} is not right of '{node.Id}' at x={node.X:F1}"));
                }
            }
        }
    }

    // The top and bottom of a set of nodes in snapped space, dots and placed label plates.
    private static (double Top, double Bottom) PlateSpan(IReadOnlyList<LayoutNode> nodes)
    {
        double top = double.PositiveInfinity;
        double bottom = double.NegativeInfinity;
        foreach (LayoutNode node in nodes)
        {
            top = Math.Min(top, node.SnappedY - node.DotRadius);
            bottom = Math.Max(bottom, node.SnappedY + node.DotRadius);
            if (node.LabelPlaced)
            {
                top = Math.Min(top, node.LabelY);
                bottom = Math.Max(bottom, node.LabelY + node.Height);
            }
        }
        return (top, bottom);
    }

    // The highest and lowest pre-snap dot Y, the heights the bands and row offsets set.
    private static double MinY(List<LayoutNode> nodes)
    {
        double y = double.PositiveInfinity;
        foreach (LayoutNode node in nodes)
            y = Math.Min(y, node.Y);
        return y;
    }

    private static double MaxY(List<LayoutNode> nodes)
    {
        double y = double.NegativeInfinity;
        foreach (LayoutNode node in nodes)
            y = Math.Max(y, node.Y);
        return y;
    }

    // The left and right ends of the node boxes in snapped space.
    private static double BoxLeft(List<LayoutNode> nodes)
    {
        double x = double.PositiveInfinity;
        foreach (LayoutNode node in nodes)
            x = Math.Min(x, node.SnappedX - node.Width / 2.0);
        return x;
    }

    private static double BoxRight(List<LayoutNode> nodes)
    {
        double x = double.NegativeInfinity;
        foreach (LayoutNode node in nodes)
            x = Math.Max(x, node.SnappedX + node.Width / 2.0);
        return x;
    }

    private static bool SamePoint(LayoutPoint a, LayoutPoint b)
    {
        return Math.Abs(a.X - b.X) <= Epsilon && Math.Abs(a.Y - b.Y) <= Epsilon;
    }

    private static bool AxisAligned(LayoutPoint a, LayoutPoint b)
    {
        return Math.Abs(a.X - b.X) <= Epsilon || Math.Abs(a.Y - b.Y) <= Epsilon;
    }

    #endregion
}
