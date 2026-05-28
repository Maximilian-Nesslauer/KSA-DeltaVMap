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
internal static class TidyTree
{
    public static void AssignX(LayoutTree tree, LayoutConfig cfg)
    {
        var busNodes = new List<LayoutNode>();
        CollectBus(tree.Root, busNodes);

        double cursorX = 0.0;
        foreach (LayoutNode busNode in busNodes)
        {
            List<LayoutNode> positioned = LayoutSubtree(busNode, cfg);

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

            double offset = cursorX - min;
            foreach (LayoutNode n in positioned)
                n.X += offset;

            cursorX = max + offset + cfg.BusGapPx;
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
    private static List<LayoutNode> LayoutSubtree(LayoutNode busNode, LayoutConfig cfg)
    {
        FlexNode root = Build(busNode, parent: null, number: 1);
        FirstWalk(root, cfg);
        SecondWalk(root, -root.Prelim);

        var positioned = new List<LayoutNode>();
        Collect(root, positioned);
        return positioned;
    }

    private static FlexNode Build(LayoutNode node, FlexNode? parent, int number)
    {
        var flex = new FlexNode(node, parent, number);
        int childNumber = 1;
        foreach (LayoutEdge edge in node.Out)
        {
            // Spoke subtree only. HubLink children are other bus nodes, laid out
            // separately as their own blocks, so they are excluded here.
            if (edge.IsHubLink)
                continue;
            flex.Children.Add(Build(edge.To, flex, childNumber++));
        }
        return flex;
    }

    private static void Collect(FlexNode flex, List<LayoutNode> into)
    {
        into.Add(flex.Node);
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
        v.Node.X = v.Prelim + modSum;
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
    // place node widths enter the algorithm.
    private static double Distance(FlexNode left, FlexNode right, LayoutConfig cfg)
    {
        return (left.Width + right.Width) / 2.0 + cfg.SiblingGapPx;
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
    // siblings; Ancestor starts as the node itself.
    private sealed class FlexNode
    {
        public readonly LayoutNode Node;
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

        public FlexNode(LayoutNode node, FlexNode? parent, int number)
        {
            Node = node;
            Width = node.Width;
            Parent = parent;
            Number = number;
            Ancestor = this;
        }
    }
}
