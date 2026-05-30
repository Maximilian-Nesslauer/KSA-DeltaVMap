using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// The GravityWell vertical (Y) pass. Unlike BandLayout, Y here is NOT cumulative dV down
// the tree: it is a local signed offset within one body's gravity well. Every body's low
// orbit (and every hub) sits on the horizontal spine at Y=0, so all low orbits share one
// line and the root is vertically centered. Within a body:
//  - Surface hangs below the spine (you descend into the well to land).
//  - Stationary, the outbound SOI edge and the arrival Intercept poke above it (they are
//    higher orbits; you circularize down from the Intercept to low orbit).
//  - A "you are here" node is signed by its radius against low orbit.
// HubLink and Transfer edges contribute nothing to Y (they are the horizontal spine). A
// destination's well is anchored on the spine through its low orbit, even when the incoming
// transfer or hub link lands on the Intercept above it (the Intercept just takes its own
// upward offset like any other high rung).
//
// The offset magnitude is the band step of the within-body ladder dV (the same band
// quantization the cumulative mode uses), clamped shallow by WellMaxBandStep so the well
// stays a low band rather than a tall strip. Because two rungs on the same side of the
// spine can quantize to the same step, each side is de-collided into strictly distinct
// bands (the exact dV lives on the badge, so nudging the band by one costs no information).
internal static class WellLayout
{
    public static void Assign(LayoutTree tree, LayoutConfig cfg)
    {
        // Group nodes into their columns (ladder-connected wells, keyed in FromRoot).
        var columns = new Dictionary<string, List<LayoutNode>>();
        foreach (LayoutNode node in tree.Nodes)
        {
            if (!columns.TryGetValue(node.Column, out List<LayoutNode>? members))
            {
                members = new List<LayoutNode>();
                columns[node.Column] = members;
            }
            members.Add(node);
        }

        foreach (List<LayoutNode> members in columns.Values)
            AssignColumn(members, cfg);

        foreach (LayoutNode node in tree.Nodes)
            node.Y = node.Band * cfg.WellBandHeightPx;
    }

    private static void AssignColumn(List<LayoutNode> members, LayoutConfig cfg)
    {
        LayoutNode anchor = SelectAnchor(members);
        anchor.Band = 0;

        // Split the non-anchor rungs by which side of the spine they sit on, carrying the
        // raw (pre-de-collision) band step so the side keeps its dV ordering.
        var below = new List<(LayoutNode Node, int Step)>();
        var above = new List<(LayoutNode Node, int Step)>();

        foreach (LayoutNode m in members)
        {
            if (ReferenceEquals(m, anchor))
                continue;

            LayoutEdge? link = ConnectingLadderEdge(m, anchor);
            if (link == null)
            {
                // Reached by a HubLink or Transfer (the cruise "you are here" hangs off the
                // star hub by a HubLink): on the spine, contributes nothing to Y.
                m.Band = 0;
                continue;
            }

            int step = Math.Clamp(BandStep(link.Dv, cfg), cfg.MinBandStep, cfg.WellMaxBandStep);
            if (Direction(m, anchor) >= 0)
                below.Add((m, step));
            else
                above.Add((m, step));
        }

        AssignSide(below, sign: +1);
        AssignSide(above, sign: -1);
    }

    // Place one side of a well's rungs at strictly distinct band magnitudes, smallest dV
    // closest to the spine. The raw step drives the spacing; ties (or a smaller step than
    // an already-placed inner rung) are bumped out by one so no two rungs share a cell.
    private static void AssignSide(List<(LayoutNode Node, int Step)> side, int sign)
    {
        side.Sort(static (a, b) =>
        {
            int byStep = a.Step.CompareTo(b.Step);
            return byStep != 0 ? byStep : string.CompareOrdinal(a.Node.Id, b.Node.Id);
        });

        int prev = 0;
        foreach ((LayoutNode node, int step) in side)
        {
            int magnitude = Math.Max(step, prev + 1);
            node.Band = sign * magnitude;
            prev = magnitude;
        }
    }

    // The well anchor sits on the spine (band 0). Low orbit is the natural anchor; a hub is
    // its own anchor; a surface-only body anchors on its arrival Intercept, then its
    // surface, then whatever it has.
    private static LayoutNode SelectAnchor(List<LayoutNode> members)
    {
        LayoutNode? lowOrbit = null;
        LayoutNode? hub = null;
        LayoutNode? intercept = null;
        LayoutNode? surface = null;

        foreach (LayoutNode m in members)
        {
            switch (m.Kind)
            {
                case LayoutKind.LowOrbit when lowOrbit == null: lowOrbit = m; break;
                case LayoutKind.Hub when hub == null: hub = m; break;
                case LayoutKind.Intercept when intercept == null: intercept = m; break;
                case LayoutKind.Surface when surface == null: surface = m; break;
            }
        }

        return lowOrbit ?? hub ?? intercept ?? surface ?? members[0];
    }

    // The ladder edge that links a rung to its well anchor. Rungs hang off low orbit as
    // children (Surface, Stationary, SOI edge, you-are-here); the Intercept is instead the
    // parent of low orbit (the Capture edge runs Intercept -> LowOrbit). Returns null when
    // the link is not a ladder edge (a HubLink-attached same-column node).
    private static LayoutEdge? ConnectingLadderEdge(LayoutNode node, LayoutNode anchor)
    {
        if (ReferenceEquals(node.Parent, anchor))
            return Ladder(node.ParentEdge);
        if (ReferenceEquals(anchor.Parent, node))
            return Ladder(anchor.ParentEdge);
        // Fallback: any intra-column ladder edge into this node.
        if (node.ParentEdge != null && node.Parent != null && node.Parent.Column == node.Column)
            return Ladder(node.ParentEdge);
        return null;
    }

    private static LayoutEdge? Ladder(LayoutEdge? edge)
    {
        return edge != null && edge.Class == EdgeClass.Ladder ? edge : null;
    }

    // Which side of the spine a rung sits on: +1 below (deeper in the well), -1 above.
    // Surface is always below; the higher orbits are above; a you-are-here node is signed
    // by its radius against the anchor (a low medium orbit can sit below low orbit), and
    // defaults above when no radius is known (the common "medium orbit" case).
    private static int Direction(LayoutNode node, LayoutNode anchor)
    {
        switch (node.Kind)
        {
            case LayoutKind.Surface:
                return +1;
            case LayoutKind.Stationary:
            case LayoutKind.SoiEdge:
            case LayoutKind.Intercept:
                return -1;
            case LayoutKind.YouAreHere:
                if (node.Radius > 0.0 && anchor.Radius > 0.0)
                    return node.Radius < anchor.Radius ? +1 : -1;
                return -1;
            default:
                return -1;
        }
    }

    // Map a dV magnitude to a whole number of band steps (the same quantization the
    // cumulative bands use) so a rung's well depth tracks its dV. The shallow well cap
    // (WellMaxBandStep) is applied by the caller, so this only floors at MinBandStep.
    private static int BandStep(double dv, LayoutConfig cfg)
    {
        if (double.IsNaN(dv) || dv <= 0.0)
            return cfg.MinBandStep;
        int step = (int)Math.Round(dv / cfg.BandQuantumDv, MidpointRounding.AwayFromZero);
        return Math.Max(step, cfg.MinBandStep);
    }
}
