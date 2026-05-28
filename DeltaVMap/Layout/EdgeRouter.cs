using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// Routes every edge as a one-bend octilinear polyline in snapped layout space: a
// straight run along the dominant axis from the parent, then a single 45-degree
// segment into the child. For a normal edge the dominant axis is
// vertical, so the straight run is the part that carries the dV metric and the
// diagonal is cosmetic; for a hub-bus link (parent and child on the same band) the
// run is horizontal and the line is effectively straight.
//
// Edges leaving the same node get a small per-lane perpendicular offset so parallel
// tracks (several siblings dropping off one hub) stay visually distinct rather than
// overprinting each other.
internal static class EdgeRouter
{
    public static void Route(LayoutTree tree, LayoutConfig cfg)
    {
        foreach (LayoutNode node in tree.Nodes)
        {
            int count = node.Out.Count;
            for (int i = 0; i < count; i++)
            {
                LayoutEdge edge = node.Out[i];
                edge.Lane = i;
                // The hub bus is a single trunk line and must run straight, so a
                // structural HubLink gets no lane offset; only parallel spoke tracks
                // are nudged apart.
                double laneShift = edge.IsHubLink ? 0.0 : (i - (count - 1) / 2.0) * cfg.LaneOffsetPx;
                edge.Polyline = BuildPolyline(edge, laneShift);
            }
        }
    }

    private static IReadOnlyList<LayoutPoint> BuildPolyline(LayoutEdge edge, double laneShift)
    {
        double fromX = edge.From.SnappedX;
        double fromY = edge.From.SnappedY;
        double toX = edge.To.SnappedX;
        double toY = edge.To.SnappedY;

        double dx = toX - fromX;
        double dy = toY - fromY;

        LayoutPoint knee;
        if (Math.Abs(dy) >= Math.Abs(dx))
        {
            // Vertical dominant: near-vertical run (nudged sideways by the lane) then
            // a 45-degree diagonal covering the remaining horizontal distance.
            double runX = fromX + laneShift;
            double horizontal = Math.Abs(toX - runX);
            double kneeY = toY - Math.Sign(dy) * horizontal;
            knee = new LayoutPoint(runX, kneeY);
        }
        else
        {
            // Horizontal dominant (hub-bus links and wide-short edges): horizontal run
            // then the 45-degree diagonal.
            double runY = fromY + laneShift;
            double vertical = Math.Abs(toY - runY);
            double kneeX = toX - Math.Sign(dx) * vertical;
            knee = new LayoutPoint(kneeX, runY);
        }

        var points = new List<LayoutPoint>(3);
        AddPoint(points, new LayoutPoint(fromX, fromY));
        AddPoint(points, knee);
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
