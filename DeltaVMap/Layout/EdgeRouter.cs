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
internal static class EdgeRouter
{
    public static void Route(LayoutTree tree, LayoutConfig cfg)
    {
        // The spring layout is free-form, so its edges are plain straight lines rather than
        // the octilinear metro routing the tidy-tree modes use.
        bool straight = cfg.Mode == LayoutMode.Spring;
        foreach (LayoutNode node in tree.Nodes)
        {
            for (int i = 0; i < node.Out.Count; i++)
            {
                LayoutEdge edge = node.Out[i];
                edge.Lane = i;
                edge.Polyline = straight ? StraightLine(edge) : BuildPolyline(edge, cfg.EdgeDiagonalPx);
            }
        }
    }

    private static IReadOnlyList<LayoutPoint> StraightLine(LayoutEdge edge)
    {
        return new[]
        {
            new LayoutPoint(edge.From.SnappedX, edge.From.SnappedY),
            new LayoutPoint(edge.To.SnappedX, edge.To.SnappedY)
        };
    }

    private static IReadOnlyList<LayoutPoint> BuildPolyline(LayoutEdge edge, double diagonalPx)
    {
        double fromX = edge.From.SnappedX;
        double fromY = edge.From.SnappedY;
        double toX = edge.To.SnappedX;
        double toY = edge.To.SnappedY;

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
