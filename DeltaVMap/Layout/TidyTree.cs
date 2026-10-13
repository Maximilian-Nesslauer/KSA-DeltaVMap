using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// Assigns the horizontal (X) position of every node. This is the tidy-tree pass:
// the linear-time Walker algorithm (Buchheim, Junger, Leipert 2002) extended to
// variable node widths (van der Ploeg / d3-flextree). It computes X only; Y is set
// by the band pass and must never be inferred here.
//
// Variable widths are mandatory: the separation between two adjacent contour nodes
// is half of each one's width plus a gap, so wide labels push their neighbours
// apart and sibling subtrees provably never overlap.
//
// The hub bus is handled explicitly. Ancestor hubs are joined to the root by zero-dV
// HubLink edges, so they all share the root band (one Y). Feeding that parent-child
// chain straight to the tidy tree would stack it at a single X and collapse it onto
// one point. Instead we find the bus (everything reachable from the root through
// HubLink edges), lay out each bus node's non-HubLink spoke subtree on its own, and
// pack those subtrees left to right into disjoint X intervals. The bus then reads as
// a horizontal row of stations, each with its spokes hanging below.
//
// A wrapped fan is packed as one block. Each of its rows is its own forest, the rows start at
// one left edge below the gutter that holds the trunk, and the hub sits over the middle of row
// 0. The rows share the depths of the tree, so the Walker contour, which steps by depth, would
// let a neighbour slide under a lower row. The hub's flex node therefore carries two chains of
// contour-only nodes down to the deepest depth of the tree, one reaching the block's left edge
// and one its right edge, and a neighbour at any depth keeps clear of the whole block. The real
// row nodes stay outside the parent's Walker and move with the hub once it is placed.
internal static class TidyTree
{
    public static void AssignX(LayoutTree tree, LayoutConfig cfg, IReadOnlyList<LayoutWrap>? wraps = null, bool skipRevealed = false)
    {
        if (cfg.Mode == LayoutMode.GravityWell)
            AssignXColumns(tree, cfg, wraps);
        else
            AssignXCumulative(tree, cfg, Blocks.For(tree, wraps, skipRevealed));
    }

    private static void AssignXCumulative(LayoutTree tree, LayoutConfig cfg, Blocks? blocks)
    {
        var busNodes = new List<LayoutNode>();
        CollectBus(tree.Root, busNodes);

        double cursorX = 0.0;
        for (int i = 0; i < busNodes.Count; i++)
        {
            List<LayoutNode> positioned = LayoutSubtree(busNodes[i], cfg, blocks);

            // Pack this bus node's whole subtree into the next free horizontal slot.
            // Using width-aware extents keeps the gap between subtrees honest even
            // when the outermost spoke is much wider than the others.
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            foreach (LayoutNode n in positioned)
            {
                min = Math.Min(min, n.X - n.Width / 2.0);
                max = Math.Max(max, n.X + n.Width / 2.0);
            }
            if (blocks != null)
                min = Math.Min(min, blocks.GutterLeft(positioned));

            double offset = cursorX - min;
            foreach (LayoutNode n in positioned)
                n.X += offset;

            // Detach the root cluster (the first bus node and its spokes) from the rest
            // with an extra margin, so the ego root stands out at the top-left.
            double gap = cfg.BusGapPx + (i == 0 ? cfg.RootMarginPx : 0.0);
            cursorX = max + offset + gap;
        }
    }

    // Collect the hub bus in pre-order, following only HubLink edges out of the root.
    // For a planet or moon root this is the spine chain (root, hub, hub, ...); for the
    // interplanetary-cruise root it is the star hub plus its hub-linked planets.
    private static void CollectBus(LayoutNode node, List<LayoutNode> bus)
    {
        bus.Add(node);
        foreach (LayoutEdge edge in node.Out)
        {
            if (edge.IsHubLink)
                CollectBus(edge.To, bus);
        }
    }

    // Run the tidy-tree on one bus node and its spoke subtree (its non-HubLink
    // descendants), centring the bus node at X = 0. Returns every node it positioned
    // so the caller can offset the block during bus packing.
    private static List<LayoutNode> LayoutSubtree(LayoutNode busNode, LayoutConfig cfg, Blocks? blocks)
    {
        FlexNode root = Build(busNode, parent: null, number: 1, cfg, blocks);
        FirstWalk(root, cfg);
        SecondWalk(root, -root.Prelim);

        var positioned = new List<LayoutNode>();
        Collect(root, positioned);
        return positioned;
    }

    private static FlexNode Build(LayoutNode node, FlexNode? parent, int number, LayoutConfig cfg, Blocks? blocks)
    {
        if (blocks != null && blocks.Wraps.TryGetValue(node.Id, out LayoutWrap? wrap))
            return BuildBlock(node, parent, number, wrap, cfg, blocks);

        var flex = new FlexNode(node, parent, number, node.Width);
        int childNumber = 1;
        foreach (LayoutEdge edge in node.Out)
        {
            // Spoke subtree only. HubLink children are other bus nodes, laid out
            // separately as their own blocks, so they are excluded here.
            if (edge.IsHubLink || Skipped(edge.To, blocks))
                continue;
            flex.Children.Add(Build(edge.To, flex, childNumber++, cfg, blocks));
        }
        return flex;
    }

    private static bool Skipped(LayoutNode child, Blocks? blocks)
    {
        return blocks != null && blocks.SkipRevealed && child.IsRevealed;
    }

    // Lay out the rows of a wrapped hub and return the hub's flex node for its parent's Walker.
    // Row 0 is laid out under the hub itself, so the hub lands over the middle of its rungs and
    // its first lanes as an unwrapped hub does. Every lower row is laid out under a virtual parent
    // and moved so that it starts where row 0 starts. The offsets of all row nodes from the hub
    // are kept, and SecondWalk applies them once the hub has its X.
    private static FlexNode BuildBlock(LayoutNode hub, FlexNode? parent, int number, LayoutWrap wrap, LayoutConfig cfg, Blocks blocks)
    {
        int rowCount = wrap.RowSizes.Count;
        var rowChildren = new List<LayoutNode>[rowCount];
        for (int r = 0; r < rowCount; r++)
            rowChildren[r] = new List<LayoutNode>();
        foreach (LayoutEdge edge in hub.Out)
        {
            if (edge.IsHubLink || Skipped(edge.To, blocks))
                continue;
            rowChildren[Math.Clamp(edge.To.WrapRow, 0, rowCount - 1)].Add(edge.To);
        }

        var block = new Block();
        double rowStart = 0.0;
        double right = double.NegativeInfinity;
        for (int r = 0; r < rowCount; r++)
        {
            var rowRoot = new FlexNode(r == 0 ? hub : null, null, 1, r == 0 ? hub.Width : 0.0);
            int childNumber = 1;
            foreach (LayoutNode child in rowChildren[r])
                rowRoot.Children.Add(Build(child, rowRoot, childNumber++, cfg, blocks));
            FirstWalk(rowRoot, cfg);
            SecondWalk(rowRoot, -rowRoot.Prelim);

            var rowNodes = new List<LayoutNode>();
            foreach (FlexNode child in rowRoot.Children)
                Collect(child, rowNodes);
            double left = double.PositiveInfinity;
            foreach (LayoutNode n in rowNodes)
                left = Math.Min(left, n.X - n.Width / 2.0);
            if (r == 0)
                rowStart = left;
            double shift = rowStart - left;
            foreach (LayoutNode n in rowNodes)
            {
                block.Members.Add((n, n.X + shift));
                right = Math.Max(right, n.X + shift + n.Width / 2.0);
            }
        }

        // The left chain reaches over the gutter to the block's left edge, the right chain to its
        // right edge. Their distance is zero, so the hub stays centred between them, and only
        // their outer sides form the contour a neighbour sees.
        double leftReach = Math.Max(hub.Width / 2.0, wrap.GutterPx - rowStart);
        double rightReach = Math.Max(hub.Width / 2.0, right);
        blocks.LeftReach[hub] = leftReach;

        var flex = new FlexNode(hub, parent, number, hub.Width)
        {
            LeftReach = leftReach,
            RightReach = hub.Width / 2.0,
            Block = block
        };
        FlexNode leftChain = flex;
        FlexNode rightChain = flex;
        for (int depth = hub.Depth + 1; depth <= blocks.MaxDepth; depth++)
        {
            bool first = depth == hub.Depth + 1;
            var l = new FlexNode(null, leftChain, 1, 0.0) { LeftReach = leftReach, RightReach = 0.0, Contour = block };
            var r = new FlexNode(null, rightChain, first ? 2 : 1, 0.0) { LeftReach = 0.0, RightReach = rightReach, Contour = block };
            leftChain.Children.Add(l);
            rightChain.Children.Add(r);
            leftChain = l;
            rightChain = r;
        }
        return flex;
    }

    // A block hub stands for every node of its rows, and its contour chains stand for nothing.
    private static void Collect(FlexNode flex, List<LayoutNode> into)
    {
        if (flex.Node != null)
            into.Add(flex.Node);
        if (flex.Block != null)
        {
            foreach ((LayoutNode node, double _) in flex.Block.Members)
                into.Add(node);
            return;
        }
        foreach (FlexNode child in flex.Children)
            Collect(child, into);
    }

    private static void FirstWalk(FlexNode v, LayoutConfig cfg)
    {
        if (v.Children.Count == 0)
        {
            FlexNode? leftSibling = LeftSibling(v);
            v.Prelim = leftSibling != null ? leftSibling.Prelim + Distance(leftSibling, v, cfg) : 0.0;
            return;
        }

        FlexNode defaultAncestor = v.Children[0];
        foreach (FlexNode child in v.Children)
        {
            FirstWalk(child, cfg);
            defaultAncestor = Apportion(child, defaultAncestor, cfg);
        }

        ExecuteShifts(v);

        double midpoint = (v.Children[0].Prelim + v.Children[^1].Prelim) / 2.0;
        FlexNode? w = LeftSibling(v);
        if (w != null)
        {
            v.Prelim = w.Prelim + Distance(w, v, cfg);
            v.Mod = v.Prelim - midpoint;
        }
        else
        {
            v.Prelim = midpoint;
        }
    }

    private static void SecondWalk(FlexNode v, double modSum)
    {
        double x = v.Prelim + modSum;
        if (v.Node != null)
            v.Node.X = x;
        if (v.Block != null)
        {
            foreach ((LayoutNode node, double offset) in v.Block.Members)
                node.X = x + offset;
            return;
        }
        foreach (FlexNode child in v.Children)
            SecondWalk(child, modSum + v.Mod);
    }

    // The shift-distribution pass: smear the accumulated shifts evenly across the
    // gaps between children so the inner subtrees spread out instead of bunching.
    private static void ExecuteShifts(FlexNode v)
    {
        double shift = 0.0;
        double change = 0.0;
        for (int i = v.Children.Count - 1; i >= 0; i--)
        {
            FlexNode w = v.Children[i];
            w.Prelim += shift;
            w.Mod += shift;
            change += w.Change;
            shift += w.Shift + change;
        }
    }

    // Walk the right contour of the already-placed left subtrees against the left
    // contour of v's subtree, pushing v right whenever the two would overlap. Threads
    // stitch the contours of differently shaped subtrees together in linear time.
    private static FlexNode Apportion(FlexNode v, FlexNode defaultAncestor, LayoutConfig cfg)
    {
        FlexNode? w = LeftSibling(v);
        if (w == null)
            return defaultAncestor;

        FlexNode vInsideRight = v;
        FlexNode vOutsideRight = v;
        FlexNode vInsideLeft = w;
        FlexNode vOutsideLeft = v.Parent!.Children[0];

        double sInsideRight = vInsideRight.Mod;
        double sOutsideRight = vOutsideRight.Mod;
        double sInsideLeft = vInsideLeft.Mod;
        double sOutsideLeft = vOutsideLeft.Mod;

        while (NextRight(vInsideLeft) != null && NextLeft(vInsideRight) != null)
        {
            vInsideLeft = NextRight(vInsideLeft)!;
            vInsideRight = NextLeft(vInsideRight)!;
            vOutsideLeft = NextLeft(vOutsideLeft)!;
            vOutsideRight = NextRight(vOutsideRight)!;
            vOutsideRight.Ancestor = v;

            double shift = (vInsideLeft.Prelim + sInsideLeft)
                - (vInsideRight.Prelim + sInsideRight)
                + Distance(vInsideLeft, vInsideRight, cfg);
            if (shift > 0.0)
            {
                MoveSubtree(Ancestor(vInsideLeft, v, defaultAncestor), v, shift);
                sInsideRight += shift;
                sOutsideRight += shift;
            }

            sInsideLeft += vInsideLeft.Mod;
            sInsideRight += vInsideRight.Mod;
            sOutsideLeft += vOutsideLeft.Mod;
            sOutsideRight += vOutsideRight.Mod;
        }

        if (NextRight(vInsideLeft) != null && NextRight(vOutsideRight) == null)
        {
            vOutsideRight.Thread = NextRight(vInsideLeft);
            vOutsideRight.Mod += sInsideLeft - sOutsideRight;
        }

        if (NextLeft(vInsideRight) != null && NextLeft(vOutsideLeft) == null)
        {
            vOutsideLeft.Thread = NextLeft(vInsideRight);
            vOutsideLeft.Mod += sInsideRight - sOutsideLeft;
            defaultAncestor = v;
        }

        return defaultAncestor;
    }

    private static void MoveSubtree(FlexNode wMinus, FlexNode wPlus, double shift)
    {
        int subtrees = wPlus.Number - wMinus.Number;
        wPlus.Change -= shift / subtrees;
        wPlus.Shift += shift;
        wMinus.Change += shift / subtrees;
        wPlus.Prelim += shift;
        wPlus.Mod += shift;
    }

    private static FlexNode Ancestor(FlexNode vInsideLeft, FlexNode v, FlexNode defaultAncestor)
    {
        FlexNode candidate = vInsideLeft.Ancestor;
        return candidate.Parent == v.Parent ? candidate : defaultAncestor;
    }

    // The required centre-to-centre separation between two nodes that sit side by side
    // at the same level: half of each width plus the sibling gap. This is the single
    // place node widths enter the algorithm. A block hub and its contour chains reach
    // further to one side than to the other, and the two halves of one chain level sit on
    // the same X.
    private static double Distance(FlexNode left, FlexNode right, LayoutConfig cfg)
    {
        if (left.Contour != null && left.Contour == right.Contour)
            return 0.0;
        if (left.LeftReach < 0.0 && right.LeftReach < 0.0)
            return (left.Width + right.Width) / 2.0 + cfg.SiblingGapPx;
        return Reach(left, right: true) + Reach(right, right: false) + cfg.SiblingGapPx;
    }

    private static double Reach(FlexNode v, bool right)
    {
        if (v.LeftReach < 0.0)
            return v.Width / 2.0;
        return right ? v.RightReach : v.LeftReach;
    }

    private static FlexNode? NextLeft(FlexNode v)
    {
        return v.Children.Count > 0 ? v.Children[0] : v.Thread;
    }

    private static FlexNode? NextRight(FlexNode v)
    {
        return v.Children.Count > 0 ? v.Children[^1] : v.Thread;
    }

    private static FlexNode? LeftSibling(FlexNode v)
    {
        if (v.Parent == null || v.Number <= 1)
            return null;
        return v.Parent.Children[v.Number - 2];
    }

    // Per-node scratch for one tidy-tree run. Number is the 1-based index among
    // siblings, and Ancestor starts as the node itself. Node is null for a contour node of a
    // block and for the virtual parent of a lower row.
    private sealed class FlexNode
    {
        public readonly LayoutNode? Node;
        public readonly double Width;
        public readonly FlexNode? Parent;
        public readonly int Number;
        public readonly List<FlexNode> Children = new();

        public double Prelim;
        public double Mod;
        public double Shift;
        public double Change;
        public FlexNode? Thread;
        public FlexNode Ancestor;

        // How far the node reaches left and right of its X when that is not half its width,
        // which holds only for a block hub and its contour nodes. Negative means half the width.
        public double LeftReach { get; init; } = -1.0;
        public double RightReach { get; init; } = -1.0;

        // Block is set on the hub of a wrapped fan, Contour on the contour nodes below it.
        public Block? Block { get; init; }
        public Block? Contour { get; init; }

        // Width is passed explicitly: the cumulative pass uses the node's own box width,
        // the column pass uses the widest box in the column (so a column is wide enough
        // for its longest rung label).
        public FlexNode(LayoutNode? node, FlexNode? parent, int number, double width)
        {
            Node = node;
            Width = width;
            Parent = parent;
            Number = number;
            Ancestor = this;
        }
    }

    // The nodes of one wrapped fan's rows with their X offsets from the hub.
    private sealed class Block
    {
        public readonly List<(LayoutNode Node, double Offset)> Members = new();
    }

    // The wrapped fans of one X pass, keyed by hub, the deepest depth of the tree, which the
    // contour chains reach down to, and how far each block reaches left of its hub, gutter
    // included. SkipRevealed leaves the revealed lanes out, for scoring a row plan without them.
    private sealed class Blocks
    {
        public required Dictionary<string, LayoutWrap> Wraps { get; init; }
        public required int MaxDepth { get; init; }
        public required bool SkipRevealed { get; init; }
        public readonly Dictionary<LayoutNode, double> LeftReach = new();

        public static Blocks? For(LayoutTree tree, IReadOnlyList<LayoutWrap>? wraps, bool skipRevealed)
        {
            if ((wraps == null || wraps.Count == 0) && !skipRevealed)
                return null;
            var byHub = new Dictionary<string, LayoutWrap>();
            if (wraps != null)
            {
                foreach (LayoutWrap wrap in wraps)
                    byHub[wrap.ParentId] = wrap;
            }
            int maxDepth = 0;
            foreach (LayoutNode node in tree.Nodes)
                maxDepth = Math.Max(maxDepth, node.Depth);
            return new Blocks { Wraps = byHub, MaxDepth = maxDepth, SkipRevealed = skipRevealed };
        }

        // The left end of the gutter of the leftmost block among the placed nodes.
        public double GutterLeft(List<LayoutNode> positioned)
        {
            double min = double.PositiveInfinity;
            foreach (LayoutNode node in positioned)
            {
                if (LeftReach.TryGetValue(node, out double reach))
                    min = Math.Min(min, node.X - reach);
            }
            return min;
        }
    }

    #region GravityWell column packing

    // GravityWell lays the map out as one horizontal spine of bodies: every body's low
    // orbit (and every hub) sits on the spine at Y=0, with the body's surface dangling
    // below and its high orbits / capture poking above (the Y pass, WellLayout). So the X
    // pass cannot reuse the depth-stacking tidy tree: with every anchor flattened onto one
    // line, a parent body and its transfer children all share Y=0 and would collide if a
    // parent were centred over them. Instead it packs whole COLUMNS (one body's rungs share
    // one X) side by side in a pre-order walk, advancing a cursor by each column's width so
    // no two columns overlap and a body's moons stay grouped immediately to its right. A
    // wider gap before a hub-bus branch keeps the heliocentric junctions visually apart.
    //
    // A wrapped hub continues its lanes in rows below its own spine, like lines of text. Every
    // row starts one gutter right of the hub's column, so the moons stay right of their planet
    // and the trunk that joins the rows runs down the gutter. A lane takes its whole block into
    // its row, its own moons and their rows included, so a planet system is never split between
    // two rows. The measure pass sizes the blocks bottom up and gives the row offsets, the place
    // pass sets X top down, and neither one writes Y.
    private sealed class Column
    {
        public required string Key { get; init; }
        public readonly List<LayoutNode> Members = new();
        public double Width;
        public int MinBand = int.MaxValue;
        public int MaxBand = int.MinValue;
        public readonly List<Link> Spokes = new();      // child columns via Transfer
        public readonly List<Link> BusChildren = new(); // child columns via HubLink
    }

    // A child column and the edge that leads to it.
    private readonly record struct Link(Column Child, LayoutEdge Edge);

    private readonly record struct Box(double Width, double Top, double Bottom);

    private static void AssignXColumns(LayoutTree tree, LayoutConfig cfg, IReadOnlyList<LayoutWrap>? wraps)
    {
        Dictionary<string, LayoutWrap>? byHub = null;
        if (wraps != null && wraps.Count > 0)
        {
            byHub = new Dictionary<string, LayoutWrap>();
            foreach (LayoutWrap wrap in wraps)
                byHub[wrap.ParentId] = wrap;
        }
        WellColumns.Build(tree).Place(cfg, byHub);
    }

    // The block sizes and row offsets of one row plan. Top and Bottom are the highest and the
    // lowest dot of the whole map relative to the root's spine, RowDy holds the offset of every
    // row of each wrapped hub below the hub's spine, and BlockWidth the width of the block each
    // edge leads to, when the measure keeps them.
    internal sealed class WellMeasure
    {
        public WellMeasure(int edges, bool blockWidths)
        {
            HasBlockWidths = blockWidths;
            BlockWidth = new Dictionary<LayoutEdge, double>(blockWidths ? edges : 0);
        }

        public double Width { get; set; }
        public double Top { get; set; }
        public double Bottom { get; set; }
        public bool HasBlockWidths { get; }
        public Dictionary<string, double[]> RowDy { get; } = new();
        public Dictionary<LayoutEdge, double> BlockWidth { get; }
    }

    // The columns of one GravityWell tree and how they nest. The row plan builds them once and
    // measures every candidate on them, and the X pass places the chosen plan with them.
    internal sealed class WellColumns
    {
        private readonly Dictionary<string, Column> _columns;
        private readonly Dictionary<LayoutEdge, Link> _links;
        private readonly Column _root;

        // The lanes of every node that has one, in the node's order, and for every node Id the
        // columns that hold such a node with its place among their members. A plan names its
        // wrapped hubs by Id, and these find their columns without testing every member.
        private readonly Dictionary<LayoutNode, List<Link>> _lanes;
        private readonly Dictionary<string, List<(Column Col, int Index)>> _byId;
        private static readonly List<Link> NoLanes = new();

        private WellColumns(Dictionary<string, Column> columns, Dictionary<LayoutEdge, Link> links, Column root,
            Dictionary<LayoutNode, List<Link>> lanes, Dictionary<string, List<(Column Col, int Index)>> byId)
        {
            _columns = columns;
            _links = links;
            _root = root;
            _lanes = lanes;
            _byId = byId;
        }

        public static WellColumns Build(LayoutTree tree)
        {
            var links = new Dictionary<LayoutEdge, Link>();
            Dictionary<string, Column> columns = BuildColumns(tree, links);

            var lanes = new Dictionary<LayoutNode, List<Link>>();
            foreach (LayoutNode node in tree.Nodes)
            {
                foreach (LayoutEdge edge in node.Out)
                {
                    if (edge.Class == EdgeClass.Ladder)
                        continue;
                    if (!lanes.TryGetValue(node, out List<Link>? list))
                    {
                        list = new List<Link>();
                        lanes[node] = list;
                    }
                    list.Add(links[edge]);
                }
            }

            var byId = new Dictionary<string, List<(Column Col, int Index)>>();
            foreach (Column col in columns.Values)
            {
                for (int i = 0; i < col.Members.Count; i++)
                {
                    string id = col.Members[i].Id;
                    if (!byId.TryGetValue(id, out List<(Column Col, int Index)>? places))
                    {
                        places = new List<(Column Col, int Index)>();
                        byId[id] = places;
                    }
                    places.Add((col, i));
                }
            }
            return new WellColumns(columns, links, columns[tree.Root.Column], lanes, byId);
        }

        // Sets X on every node. skipRevealed leaves the revealed lanes and their blocks out and
        // their X as it was, for scoring a plan without them.
        public void Place(LayoutConfig cfg, IReadOnlyDictionary<string, LayoutWrap>? wrapsByHub, bool skipRevealed = false)
        {
            double cursorX = 0.0;
            PlaceColumn(_root, ref cursorX, cfg, wrapsByHub, HubsOf(wrapsByHub), skipRevealed);
        }

        // Sizes every block bottom up without placing it. wrapped holds the row count and the
        // gutter of each wrapped hub, whose lanes carry their rows in WrapRow. A row sits gapPx
        // below the lowest dot of the row above it, rounded down onto the grid. skipRevealed
        // leaves out the revealed lanes and their blocks, for scoring a plan without them, and
        // blockWidths keeps the width of every block, which only a split into shelves reads.
        public WellMeasure Measure(LayoutConfig cfg, IReadOnlyDictionary<string, (int Rows, double Gutter)> wrapped, double gapPx, bool skipRevealed, bool blockWidths = true)
        {
            var result = new WellMeasure(_links.Count, blockWidths);
            Box box = MeasureColumn(_root, cfg, wrapped, HubsOf(wrapped), gapPx, skipRevealed, result);
            result.Width = box.Width;
            result.Top = box.Top;
            result.Bottom = box.Bottom;
            return result;
        }

        // How far the right label plate of a rung below the hub's spine reaches past the right
        // edge of the hub's column. The trunk of a wrapped hub runs down past those rungs, so the
        // gutter must hold the plate as well, or the label has to leave its first slot.
        public double LabelReach(LayoutNode hub, LayoutConfig cfg)
        {
            Column col = _columns[hub.Column];
            double reach = 0.0;
            foreach (LayoutNode m in col.Members)
            {
                if (m.Band <= 0)
                    continue;
                double right = m.DotRadius + LabelPlacer.LabelPadPx + Math.Max(m.Width, cfg.MinNodeWidthPx);
                reach = Math.Max(reach, right - col.Width / 2.0);
            }
            return reach;
        }

        // The wrapped hub of every column that holds one, which is the first of its members that
        // the plan names.
        private Dictionary<Column, LayoutNode>? HubsOf<T>(IReadOnlyDictionary<string, T>? wrapped)
        {
            if (wrapped == null || wrapped.Count == 0)
                return null;
            var first = new Dictionary<Column, int>();
            foreach (string id in wrapped.Keys)
            {
                if (!_byId.TryGetValue(id, out List<(Column Col, int Index)>? places))
                    continue;
                foreach ((Column col, int index) in places)
                {
                    if (!first.TryGetValue(col, out int current) || index < current)
                        first[col] = index;
                }
            }
            var hubs = new Dictionary<Column, LayoutNode>(first.Count);
            foreach ((Column col, int index) in first)
                hubs[col] = col.Members[index];
            return hubs;
        }

        // Place a column at the cursor, then walk its children left to right, a body's own
        // moons (Transfer spokes) first so they cluster beside it, then the hub-bus branch. A
        // wrapped hub puts its lanes into rows that start one gutter right of its column, and
        // its other children follow the widest row on its own spine.
        private void PlaceColumn(Column col, ref double cursorX, LayoutConfig cfg, IReadOnlyDictionary<string, LayoutWrap>? wrapsByHub, Dictionary<Column, LayoutNode>? hubs, bool skipRevealed)
        {
            double half = col.Width / 2.0;
            double x = cursorX + half;
            foreach (LayoutNode m in col.Members)
                m.X = x;
            cursorX = x + half;

            if (hubs != null && hubs.TryGetValue(col, out LayoutNode? hub))
            {
                // Each row places its lanes in the hub's order. The rungs stay in the hub's column,
                // and a hub link that carries the bus on past the hub has no row.
                LayoutWrap wrap = wrapsByHub![hub.Id];
                double rowStart = cursorX + wrap.GutterPx;
                double end = cursorX;
                List<Link> lanes = LanesOf(hub);
                for (int r = 0; r < wrap.RowSizes.Count; r++)
                {
                    double c = rowStart;
                    bool first = true;
                    foreach (Link link in lanes)
                    {
                        if (link.Edge.To.WrapRow != r || (skipRevealed && link.Edge.To.IsRevealed))
                            continue;
                        if (!first)
                            c += GapBefore(link, cfg);
                        first = false;
                        PlaceColumn(link.Child, ref c, cfg, wrapsByHub, hubs, skipRevealed);
                    }
                    end = Math.Max(end, c);
                }
                cursorX = end;
                PlaceRest(col.Spokes, hub, ref cursorX, cfg, wrapsByHub, hubs, skipRevealed);
                PlaceRest(col.BusChildren, hub, ref cursorX, cfg, wrapsByHub, hubs, skipRevealed);
                return;
            }

            foreach (Link spoke in col.Spokes)
            {
                if (skipRevealed && spoke.Edge.To.IsRevealed)
                    continue;
                cursorX += cfg.SiblingGapPx;
                PlaceColumn(spoke.Child, ref cursorX, cfg, wrapsByHub, hubs, skipRevealed);
            }
            foreach (Link bus in col.BusChildren)
            {
                if (skipRevealed && bus.Edge.To.IsRevealed)
                    continue;
                cursorX += cfg.BusGapPx;
                PlaceColumn(bus.Child, ref cursorX, cfg, wrapsByHub, hubs, skipRevealed);
            }
        }

        // Places the children of a wrapped hub's column that are no lane of it, in the unwrapped
        // order, which is the spokes first and then the bus children.
        private void PlaceRest(List<Link> links, LayoutNode hub, ref double cursorX, LayoutConfig cfg, IReadOnlyDictionary<string, LayoutWrap>? wrapsByHub, Dictionary<Column, LayoutNode>? hubs, bool skipRevealed)
        {
            foreach (Link link in links)
            {
                if (!IsRest(link, hub) || (skipRevealed && link.Edge.To.IsRevealed))
                    continue;
                cursorX += GapBefore(link, cfg);
                PlaceColumn(link.Child, ref cursorX, cfg, wrapsByHub, hubs, skipRevealed);
            }
        }

        private Box MeasureColumn(Column col, LayoutConfig cfg, IReadOnlyDictionary<string, (int Rows, double Gutter)> wrapped, Dictionary<Column, LayoutNode>? hubs, double gapPx, bool skipRevealed, WellMeasure result)
        {
            // A band times the height grows or falls with the band, so the extreme bands give
            // the highest and the lowest dot of the column.
            double h = cfg.WellBandHeightPx;
            double top = Math.Min(col.MinBand * h, col.MaxBand * h);
            double bottom = Math.Max(col.MinBand * h, col.MaxBand * h);
            double width = col.Width;

            if (hubs != null && hubs.TryGetValue(col, out LayoutNode? hub))
            {
                // Each row adds up its lanes in the hub's order, which is the order the rows
                // place them in.
                (int rowCount, double gutter) = wrapped[hub.Id];
                var rowDy = new double[rowCount];
                var rowWidth = new double[rowCount];
                var rowTop = new double[rowCount];
                var rowBottom = new double[rowCount];
                var rowLanes = new int[rowCount];
                foreach (Link link in LanesOf(hub))
                {
                    int row = link.Edge.To.WrapRow;
                    if (row < 0 || row >= rowCount || (skipRevealed && link.Edge.To.IsRevealed))
                        continue;
                    Box box = MeasureColumn(link.Child, cfg, wrapped, hubs, gapPx, skipRevealed, result);
                    if (result.HasBlockWidths)
                        result.BlockWidth[link.Edge] = box.Width;
                    if (rowLanes[row] > 0)
                        rowWidth[row] += GapBefore(link, cfg);
                    rowWidth[row] += box.Width;
                    rowTop[row] = rowLanes[row] == 0 ? box.Top : Math.Min(rowTop[row], box.Top);
                    rowBottom[row] = rowLanes[row] == 0 ? box.Bottom : Math.Max(rowBottom[row], box.Bottom);
                    rowLanes[row]++;
                }

                double widest = 0.0;
                double bottomAbove = 0.0;
                for (int r = 0; r < rowCount; r++)
                {
                    if (r > 0)
                        rowDy[r] = SnapUp(rowDy[r - 1] + bottomAbove - rowTop[r] + gapPx, cfg.GridPx);
                    bottomAbove = rowBottom[r];
                    widest = Math.Max(widest, rowWidth[r]);
                    top = Math.Min(top, rowDy[r] + rowTop[r]);
                    bottom = Math.Max(bottom, rowDy[r] + rowBottom[r]);
                }
                result.RowDy[hub.Id] = rowDy;
                width = col.Width + gutter + widest;
                MeasureRest(col.Spokes, hub, ref width, ref top, ref bottom, cfg, wrapped, hubs, gapPx, skipRevealed, result);
                MeasureRest(col.BusChildren, hub, ref width, ref top, ref bottom, cfg, wrapped, hubs, gapPx, skipRevealed, result);
                return new Box(width, top, bottom);
            }

            MeasureRest(col.Spokes, null, ref width, ref top, ref bottom, cfg, wrapped, hubs, gapPx, skipRevealed, result);
            MeasureRest(col.BusChildren, null, ref width, ref top, ref bottom, cfg, wrapped, hubs, gapPx, skipRevealed, result);
            return new Box(width, top, bottom);
        }

        // Adds the blocks of the given child links beside a column, leaving out the lanes of its
        // wrapped hub when it has one.
        private void MeasureRest(List<Link> links, LayoutNode? hub, ref double width, ref double top, ref double bottom, LayoutConfig cfg,
            IReadOnlyDictionary<string, (int Rows, double Gutter)> wrapped, Dictionary<Column, LayoutNode>? hubs, double gapPx, bool skipRevealed, WellMeasure result)
        {
            foreach (Link link in links)
            {
                if ((hub != null && !IsRest(link, hub)) || (skipRevealed && link.Edge.To.IsRevealed))
                    continue;
                Box box = MeasureColumn(link.Child, cfg, wrapped, hubs, gapPx, skipRevealed, result);
                if (result.HasBlockWidths)
                    result.BlockWidth[link.Edge] = box.Width;
                width += GapBefore(link, cfg) + box.Width;
                top = Math.Min(top, box.Top);
                bottom = Math.Max(bottom, box.Bottom);
            }
        }

        // The lanes of a node in its order. The rungs stay in the node's column, so they are no
        // lanes.
        private List<Link> LanesOf(LayoutNode node)
        {
            return _lanes.TryGetValue(node, out List<Link>? lanes) ? lanes : NoLanes;
        }

        // Whether a child of a wrapped hub's column is no lane of that hub.
        private static bool IsRest(Link link, LayoutNode hub)
        {
            return link.Edge.From != hub || link.Edge.To.WrapRow < 0;
        }

        private static double GapBefore(Link link, LayoutConfig cfg)
        {
            return link.Edge.IsHubLink ? cfg.BusGapPx : cfg.SiblingGapPx;
        }

        private static double SnapUp(double v, double g) => Math.Ceiling(v / g - 1e-9) * g;
    }

    // Group the tree's nodes into columns and wire the column adjacency. Walking the
    // tree in its existing pre-order keeps the spoke/bus child order identical to the
    // sibling order, so the column packing is as deterministic as the cumulative pass.
    private static Dictionary<string, Column> BuildColumns(LayoutTree tree, Dictionary<LayoutEdge, Link> links)
    {
        var columns = new Dictionary<string, Column>();

        Column Get(string key)
        {
            if (!columns.TryGetValue(key, out Column? col))
            {
                col = new Column { Key = key };
                columns[key] = col;
            }
            return col;
        }

        foreach (LayoutNode node in tree.Nodes)
        {
            Column col = Get(node.Column);
            col.Members.Add(node);
            col.Width = Math.Max(col.Width, node.Width);
            col.MinBand = Math.Min(col.MinBand, node.Band);
            col.MaxBand = Math.Max(col.MaxBand, node.Band);
        }

        foreach (LayoutNode node in tree.Nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.Class == EdgeClass.Ladder)
                    continue; // intra-column, no column edge
                Column parent = columns[node.Column];
                var link = new Link(columns[edge.To.Column], edge);
                links[edge] = link;
                if (edge.Class == EdgeClass.HubLink)
                    parent.BusChildren.Add(link);
                else
                    parent.Spokes.Add(link);
            }
        }

        return columns;
    }

    #endregion
}
