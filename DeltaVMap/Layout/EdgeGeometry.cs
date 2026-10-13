using System.Collections.Generic;

namespace DeltaVMap.Layout;

// Where an edge's delta-v reads on its routed polyline. The canvas badge, the aerobrake
// marker and the SVG dump badge all anchor through here, so the dump shows each badge on
// the same segment the canvas draws it on. Every method expects a polyline of at least two
// points, which the router gives every drawn edge.
internal static class EdgeGeometry
{
    // The index i of the segment Polyline[i] to Polyline[i + 1] that carries the edge's
    // metric, which is where the dV badge sits. A ladder edge reads on its last segment, the
    // vertical drop into the rung, in every mode. A GravityWell transfer is a horizontal hop
    // between wells, so its metric is the spine run on the first segment. Cumulative-down and
    // Spring transfers end on their drop like a ladder edge.
    public static int DvSegment(LayoutEdge edge, LayoutMode mode)
    {
        if (mode == LayoutMode.GravityWell && edge.Class == EdgeClass.Transfer)
            return 0;
        return edge.Polyline.Count - 2;
    }

    public static LayoutPoint DvSegmentMidpoint(LayoutEdge edge, LayoutMode mode)
    {
        IReadOnlyList<LayoutPoint> pts = edge.Polyline;
        int i = DvSegment(edge, mode);
        return new LayoutPoint((pts[i].X + pts[i + 1].X) / 2.0, (pts[i].Y + pts[i + 1].Y) / 2.0);
    }

    // The straight chord the aerobrake marker sits on and points along, from where the edge
    // leaves its origin to the node it arrives at.
    public static (LayoutPoint From, LayoutPoint To) DvChord(LayoutEdge edge)
    {
        IReadOnlyList<LayoutPoint> pts = edge.Polyline;
        return (pts[0], pts[pts.Count - 1]);
    }
}
