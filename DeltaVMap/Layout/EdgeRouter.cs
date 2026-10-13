using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// Routes every edge as a horizontal run, a short 45-degree diagonal, then a vertical
// drop into the child, in snapped layout space. Every node therefore sits on a clean
// vertical lane (the metro / KSP subway look), the final drop carries the dV metric,
// and the diagonal gives an angled corner instead of a hard right angle. A hub-bus
// link has the child on the parent's band, so the diagonal and drop collapse and the
// line is a straight horizontal trunk; a directly-below child collapses to a plain
// vertical.
//
// This replaces the earlier single 45-degree bend, which with variable band heights
// produced long slopes and, combined with lane nudging, occasional up-and-over
// detours. Keeping the diagonal short and bounded by the available room avoids both.
//
// An edge into a lower row of a wrapped fan first runs from its hub over to the trunk in the
// gutter left of the rows and down the trunk to the row's height, the ghost point, where its
// DvStart points. That prefix carries only the layout. From the ghost point on the edge has
// exactly the run, diagonal and drop it would have from a hub at the ghost point, so the drop
// into the lane keeps the length that states its dV.
internal static class EdgeRouter
{
    public static void Route(LayoutTree tree, LayoutConfig cfg, IReadOnlyList<LayoutWrap>? wraps = null)
    {
        // The spring layout is free-form, so its edges are plain straight lines rather than
        // the octilinear metro routing the tidy-tree modes use.
        bool straight = cfg.Mode == LayoutMode.Spring;
        Dictionary<string, LayoutWrap>? byHub = wraps != null && wraps.Count > 0 ? PlaceTrunks(tree, cfg, wraps) : null;
        foreach (LayoutNode node in tree.Nodes)
        {
            LayoutWrap? wrap = null;
            byHub?.TryGetValue(node.Id, out wrap);
            for (int i = 0; i < node.Out.Count; i++)
            {
                LayoutEdge edge = node.Out[i];
                edge.Lane = i;
                edge.DvStart = 0;
                var from = new LayoutPoint(edge.From.SnappedX, edge.From.SnappedY);
                var to = new LayoutPoint(edge.To.SnappedX, edge.To.SnappedY);
                if (straight)
                {
                    edge.Polyline = StraightLine(edge);
                }
                else if (wrap != null && edge.To.WrapRow >= 1 && !edge.IsHubLink)
                {
                    var ghost = new LayoutPoint(wrap.TrunkX, from.Y + wrap.RowDy[edge.To.WrapRow]);
                    var points = new List<LayoutPoint> { from, new LayoutPoint(wrap.TrunkX, from.Y) };
                    points.AddRange(BuildPolyline(ghost, to, cfg.EdgeDiagonalPx));
                    edge.Polyline = points;
                    edge.DvStart = 2;
                }
                else
                {
                    edge.Polyline = BuildPolyline(from, to, cfg.EdgeDiagonalPx);
                }
            }
        }
    }

    // Puts each trunk half a gutter left of the snapped boxes of the rows, on a grid column. The
    // snap may have nudged a node toward the gutter, which is why this reads the snapped
    // positions and not the pre-snap block edge. The trunk runs from the hub over to that X and
    // down to the last row.
    private static Dictionary<string, LayoutWrap> PlaceTrunks(LayoutTree tree, LayoutConfig cfg, IReadOnlyList<LayoutWrap> wraps)
    {
        var byHub = new Dictionary<string, LayoutWrap>();
        foreach (LayoutWrap wrap in wraps)
            byHub[wrap.ParentId] = wrap;
        foreach (LayoutNode hub in tree.Nodes)
        {
            if (!byHub.TryGetValue(hub.Id, out LayoutWrap? wrap))
                continue;
            double left = double.PositiveInfinity;
            foreach (LayoutEdge edge in hub.Out)
            {
                if (edge.IsHubLink)
                    continue;
                foreach (LayoutNode node in LayoutTree.PreOrder(edge.To))
                    left = Math.Min(left, node.SnappedX - node.Width / 2.0);
            }
            double g = cfg.GridPx;
            wrap.TrunkX = Math.Floor((left - wrap.GutterPx / 2.0) / g + 1e-9) * g;
            wrap.Trunk = new[]
            {
                new LayoutPoint(hub.SnappedX, hub.SnappedY),
                new LayoutPoint(wrap.TrunkX, hub.SnappedY),
                new LayoutPoint(wrap.TrunkX, hub.SnappedY + wrap.RowDy[^1])
            };
        }
        return byHub;
    }

    private static IReadOnlyList<LayoutPoint> StraightLine(LayoutEdge edge)
    {
        return new[]
        {
            new LayoutPoint(edge.From.SnappedX, edge.From.SnappedY),
            new LayoutPoint(edge.To.SnappedX, edge.To.SnappedY)
        };
    }

    // The run, diagonal and drop from one snapped point to another. The acceptance check calls
    // it too, to confirm that the dV part of a wrapped edge has the shape of an unwrapped one.
    internal static IReadOnlyList<LayoutPoint> BuildPolyline(LayoutPoint from, LayoutPoint to, double diagonalPx)
    {
        double fromX = from.X;
        double fromY = from.Y;
        double toX = to.X;
        double toY = to.Y;

        double dx = toX - fromX;
        double dy = toY - fromY;

        // The diagonal covers d on both axes (so it is exactly 45 degrees), bounded by
        // how much horizontal and vertical room the edge has. The horizontal run takes
        // up the rest of dx at the parent's band, the vertical drop the rest of dy down
        // the child's lane. Coincident points collapse, so a hub link becomes a plain
        // horizontal and a directly-below child a plain vertical.
        double d = Math.Min(diagonalPx, Math.Min(Math.Abs(dx), Math.Abs(dy)));
        double sx = Math.Sign(dx);
        double sy = Math.Sign(dy);

        var points = new List<LayoutPoint>(4);
        AddPoint(points, new LayoutPoint(fromX, fromY));
        AddPoint(points, new LayoutPoint(toX - sx * d, fromY));
        AddPoint(points, new LayoutPoint(toX, fromY + sy * d));
        AddPoint(points, new LayoutPoint(toX, toY));
        return points;
    }

    // Append a point unless it coincides with the previous one, so a degenerate bend
    // (for example a perfectly straight hub link) collapses to a plain segment.
    private static void AddPoint(List<LayoutPoint> points, LayoutPoint p)
    {
        if (points.Count > 0)
        {
            LayoutPoint last = points[^1];
            if (Math.Abs(last.X - p.X) < 1e-6 && Math.Abs(last.Y - p.Y) < 1e-6)
                return;
        }
        points.Add(p);
    }
}
