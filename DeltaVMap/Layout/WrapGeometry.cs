using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// The box of one row of a wrapped fan in snapped layout space, over its dots and placed label
// plates.
internal readonly record struct RowBox(double MinX, double MinY, double MaxX, double MaxY);

// The break between two rows of a wrapped fan. Mark is the point on the trunk, halfway down the
// empty height between the rows, where the "//" scale-break mark sits. The faint fold rule runs
// at the same height from the trunk to the right end of the block.
internal readonly record struct RowBreak(LayoutPoint Mark, double RuleLeft, double RuleRight);

// The drawn geometry of a wrapped fan that is not stored in the layout: the rows as boxes and
// the breaks between them. The canvas, the SVG dump and the scene checks all read it from here,
// so they agree on where a row break shows. Pure, no ImGui.
internal static class WrapGeometry
{
    // The hub node of a wrap, or null when the tree does not hold it.
    public static LayoutNode? Hub(LayoutTree tree, LayoutWrap wrap)
    {
        IReadOnlyList<LayoutNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Id == wrap.ParentId)
                return nodes[i];
        }
        return null;
    }

    // In Cumulative-down the hub's rungs hang inside the block of row 0, so the row geometry
    // counts them. In GravityWell they stay in the hub's own column, left of the trunk, and the
    // rows are the lanes alone.
    public static bool RungsInRows(LayoutMode mode)
    {
        return mode == LayoutMode.CumulativeDown;
    }

    // The nodes of each row, which are the subtrees of the row's lanes and, in row 0, also the
    // subtrees of the hub's rungs when they hang inside the block.
    public static List<LayoutNode>[] RowMembers(LayoutNode hub, LayoutWrap wrap, bool rungsInRows)
    {
        var rows = new List<LayoutNode>[wrap.RowSizes.Count];
        for (int r = 0; r < rows.Length; r++)
            rows[r] = new List<LayoutNode>();
        foreach (LayoutEdge edge in hub.Out)
        {
            int row = edge.To.WrapRow;
            if (row < 0 || row >= rows.Length || (edge.Class == EdgeClass.Ladder && !rungsInRows))
                continue;
            rows[row].AddRange(LayoutTree.PreOrder(edge.To));
        }
        return rows;
    }

    // The box of every row, dots and placed label plates. A row with no node gets an empty box
    // at the hub.
    public static RowBox[] RowBoxes(LayoutNode hub, LayoutWrap wrap, LayoutMode mode)
    {
        List<LayoutNode>[] rows = RowMembers(hub, wrap, RungsInRows(mode));
        var boxes = new RowBox[rows.Length];
        for (int r = 0; r < rows.Length; r++)
        {
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            foreach (LayoutNode node in rows[r])
            {
                minX = Math.Min(minX, node.SnappedX - node.DotRadius);
                maxX = Math.Max(maxX, node.SnappedX + node.DotRadius);
                minY = Math.Min(minY, node.SnappedY - node.DotRadius);
                maxY = Math.Max(maxY, node.SnappedY + node.DotRadius);
                if (node.LabelPlaced)
                {
                    minX = Math.Min(minX, node.LabelX);
                    maxX = Math.Max(maxX, node.LabelX + node.Width);
                    minY = Math.Min(minY, node.LabelY);
                    maxY = Math.Max(maxY, node.LabelY + node.Height);
                }
            }
            boxes[r] = rows[r].Count > 0
                ? new RowBox(minX, minY, maxX, maxY)
                : new RowBox(hub.SnappedX, hub.SnappedY, hub.SnappedX, hub.SnappedY);
        }
        return boxes;
    }

    // Add one break per gap between two rows. The gap runs from the bottom of the row above to
    // where the row below starts, which is the top of its box or, when that is lower, the
    // height of its ghost point on the trunk, where its edges leave the trunk. The fold rule
    // spans from the trunk to the right end of the widest row, so it stays inside the block and
    // never crosses an unwrapped subtree beside it.
    public static void AddBreaks(LayoutNode hub, LayoutWrap wrap, LayoutMode mode, List<RowBreak> into)
    {
        RowBox[] boxes = RowBoxes(hub, wrap, mode);
        double right = double.NegativeInfinity;
        for (int r = 0; r < boxes.Length; r++)
            right = Math.Max(right, boxes[r].MaxX);
        for (int r = 1; r < boxes.Length && r < wrap.RowDy.Count; r++)
        {
            double above = boxes[r - 1].MaxY;
            double below = Math.Min(boxes[r].MinY, hub.SnappedY + wrap.RowDy[r]);
            var mark = new LayoutPoint(wrap.TrunkX, (above + below) / 2.0);
            into.Add(new RowBreak(mark, wrap.TrunkX, right));
        }
    }

    // Every wrap of the scene with its hub, the home map first and then each destination part.
    public static void Collect(LayoutScene scene, List<(LayoutNode Hub, LayoutWrap Wrap)> into)
    {
        Collect(scene.Ego, into);
        foreach (LayoutPart part in scene.Parts)
            Collect(part.Result, into);
    }

    public static void Collect(LayoutResult result, List<(LayoutNode Hub, LayoutWrap Wrap)> into)
    {
        foreach (LayoutWrap wrap in result.Wraps)
        {
            LayoutNode? hub = Hub(result.Tree, wrap);
            if (hub != null)
                into.Add((hub, wrap));
        }
    }
}
