using System;
using DeltaVMap.Dv;
using DeltaVMap.Model;
using KSA;

namespace DeltaVMap.Layout;

// Bridges the game-typed visual tree (StateNode / Edge) to the pure
// layout tree the engine consumes. This is the only file in the Layout folder that
// touches game types; keeping the engine free of them is what lets the layout run and
// be tested offline against synthetic trees.
//
// The representative dV an edge contributes to band placement is the simple,
// frame-consistent figure available on the edge: a ladder edge's self-contained cost,
// or a transfer's two v_inf legs summed. The exact route burns (Oberth at each end)
// are derived later during routing and are not needed to decide which band a node
// sits on; the badge will carry the precise number.
internal static class VisualTreeAdapter
{
    // graph is optional: when supplied (the in-game path), transfer badges show the real
    // Oberth-coupled per-leg burn derived from each end's ladder; when null (the offline
    // dump, which has no live bodies to read r_lo from) they fall back to the
    // representative v_inf sum, enough to verify topology.
    public static LayoutTree ToLayoutTree(VisualTree visual, SystemGraph? graph = null)
    {
        Func<string, BodyLadder?>? ladderFor = null;
        if (graph != null)
            ladderFor = graph.LadderFor;
        LayoutNode root = Convert(visual.Root, ladderFor);
        return LayoutTree.FromRoot($"reroot-{visual.RootBodyId}", root);
    }

    private static LayoutNode Convert(StateNode source, Func<string, BodyLadder?>? ladderFor)
    {
        var node = new LayoutNode
        {
            Id = source.Id,
            Label = source.Label,
            Kind = MapKind(source.Kind),
            Rank = RankOf(source.Body),
            IsYouAreHere = source.IsYouAreHere,
            // Mirrored so the GravityWell pass can sign a "you are here" node's well offset
            // (a medium orbit above or below low orbit) by its radius. The column key is
            // assigned structurally in LayoutTree.FromRoot, so it is not set here.
            Radius = source.RadiusFromBody
        };

        foreach (Edge edge in source.Out)
        {
            LayoutNode child = Convert(edge.To, ladderFor);
            node.AddChild(new LayoutEdge
            {
                From = node,
                To = child,
                Class = MapClass(edge.Kind),
                Dv = RepresentativeDv(edge),
                RouteDv = BadgeDv(edge, ladderFor),
                DescentDv = edge.DescentDv,
                IsApproximate = edge.IsApproximate,
                // A capture into a body with a usable atmosphere can aerobrake (the marker is
                // a capability cue, independent of whether the aerobrake toggle is on). The
                // sibling-leg plane-change figure rides along for the toggled-on number.
                Aerobrake = edge.Kind == SegmentKind.Capture && OrbitalStates.HasUsableAtmosphere(edge.To.Body),
                PlaneChangeDv = edge.PlaneChangeDv
            });
        }

        return node;
    }

    // The primary dV the badge displays. A transfer shows its real coupled burn (depart +
    // capture), computed by the same rule the route accumulator uses, so the badge and the
    // route breakdown agree. A ladder edge shows its exact self-contained cost (for an
    // Ascent edge that is the ascent; the renderer pairs it with DescentDv to show both
    // directions). A hub link shows nothing.
    private static double BadgeDv(Edge edge, Func<string, BodyLadder?>? ladderFor)
    {
        if (edge.Kind == SegmentKind.HubLink)
            return 0.0;
        if (edge.Kind == SegmentKind.Transfer && edge.Transfer.HasValue)
        {
            if (ladderFor == null)
                return edge.Transfer.Value.TotalDv;
            return TransferBurns.ComputeLegs(edge, ladderFor).Total;
        }
        return edge.LadderDv;
    }

    private static double RepresentativeDv(Edge edge)
    {
        if (edge.Kind == SegmentKind.HubLink)
            return 0.0;
        if (edge.Kind == SegmentKind.Transfer && edge.Transfer.HasValue)
            return edge.Transfer.Value.TotalDv;
        return edge.LadderDv;
    }

    private static LayoutKind MapKind(StateKind kind)
    {
        return kind switch
        {
            StateKind.Surface => LayoutKind.Surface,
            StateKind.LowOrbit => LayoutKind.LowOrbit,
            StateKind.Stationary => LayoutKind.Stationary,
            StateKind.SoiEdge => LayoutKind.SoiEdge,
            StateKind.YouAreHere => LayoutKind.YouAreHere,
            StateKind.Hub => LayoutKind.Hub,
            StateKind.Intercept => LayoutKind.Intercept,
            _ => LayoutKind.LowOrbit
        };
    }

    private static EdgeClass MapClass(SegmentKind kind)
    {
        return kind switch
        {
            SegmentKind.HubLink => EdgeClass.HubLink,
            SegmentKind.Transfer => EdgeClass.Transfer,
            _ => EdgeClass.Ladder
        };
    }

    // Cosmetic rank for the dot radius only: star 0, planet 1, moon 2, minor body 3.
    // The ego root and hubs get their own sizes in the engine regardless of rank.
    // Astronomical.Class returns the concrete type name ("TerrestrialBody",
    // "AtmosphericBody", "MinorBody", ...) and never "Planet"/"Moon" (those strings
    // live only on the abstract Celestial base), so rank comes from the semantic
    // IsStar / IsMoon predicates and the MinorBody type, not a Class string compare.
    private static int RankOf(Astronomical body)
    {
        if (body.IsStar())
            return 0;
        if (body is MinorBody)
            return 3;
        return body.IsMoon() ? 2 : 1;
    }
}
