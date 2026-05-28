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
    public static LayoutTree ToLayoutTree(VisualTree visual)
    {
        LayoutNode root = Convert(visual.Root);
        return LayoutTree.FromRoot($"reroot-{visual.RootBodyId}", root);
    }

    private static LayoutNode Convert(StateNode source)
    {
        var node = new LayoutNode
        {
            Id = source.Id,
            Label = source.Label,
            Kind = MapKind(source.Kind),
            Rank = RankOf(source.Body),
            IsYouAreHere = source.IsYouAreHere
        };

        foreach (Edge edge in source.Out)
        {
            LayoutNode child = Convert(edge.To);
            node.AddChild(new LayoutEdge
            {
                From = node,
                To = child,
                Class = MapClass(edge.Kind),
                Dv = RepresentativeDv(edge),
                IsApproximate = edge.IsApproximate
            });
        }

        return node;
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
