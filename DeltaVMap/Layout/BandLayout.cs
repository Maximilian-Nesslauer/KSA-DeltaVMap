using System;

namespace DeltaVMap.Layout;

// Assigns the vertical (Y) position of every node from cumulative delta-v, quantized
// into bands. Raw cumulative dV would make near-equal nodes collide and
// space unevenly; quantizing into discrete band steps makes the map read like a
// subway map, with the exact number left to the edge badge.
//
// Two rules carry the weight here:
//  - A normal edge (ladder or transfer) drops its child at least one band below the
//    parent, so the band height enforces the minimum vertical gap and no parent and
//    child ever share a row.
//  - A HubLink edge adds zero bands, so the whole hub spine and the root anchor share
//    one band (the same Y). That is what lets the tidy-tree pass lay the hub bus out
//    horizontally instead of collapsing the spine into a vertical stack.
internal static class BandLayout
{
    public static void AssignBands(LayoutTree tree, LayoutConfig cfg)
    {
        tree.Root.Band = 0;
        tree.Root.Y = 0.0;
        Assign(tree.Root, cfg);
    }

    private static void Assign(LayoutNode node, LayoutConfig cfg)
    {
        foreach (LayoutEdge edge in node.Out)
        {
            LayoutNode child = edge.To;
            int step = edge.IsHubLink ? 0 : BandStep(edge.Dv, cfg);
            child.Band = node.Band + step;
            child.Y = child.Band * cfg.BandHeightPx;
            Assign(child, cfg);
        }
    }

    // Map a dV magnitude to a whole number of band steps. A tiny rung still advances
    // one band (so the parent and child never overlap); a large transfer advances
    // proportionally more, clamped so the map does not grow unreadably tall.
    private static int BandStep(double dv, LayoutConfig cfg)
    {
        if (double.IsNaN(dv) || dv <= 0.0)
            return cfg.MinBandStep;

        int step = (int)Math.Round(dv / cfg.BandQuantumDv, MidpointRounding.AwayFromZero);
        if (step < cfg.MinBandStep)
            step = cfg.MinBandStep;
        if (step > cfg.MaxBandStep)
            step = cfg.MaxBandStep;
        return step;
    }
}
