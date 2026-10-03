using System;
using System.Collections.Generic;
using DeltaVMap.Core;
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
//
// The visual tree is cut at its Interstellar edges. ToLayoutTree converts the ego system,
// and ToPartTree converts one destination system, either collapsed to its root hub or
// expanded to its whole subtree, for SceneComposer to place beside the ego map. An Approach
// edge inside a destination lays out as a transfer of zero delta-v: it starts a new column,
// steps one band down, and draws no badge.
internal static class VisualTreeAdapter
{
    // Cosmetic rank a minor-body group draws at: moon-level, so its dot reads and its "+N"
    // label is not dropped by the renderer's minor-label cull (which only drops rank 3).
    private const int MinorGroupRank = 2;

    // graph is optional: when supplied (the in-game path), transfer badges show the real
    // Oberth-coupled per-leg burn derived from each end's ladder, and with several star
    // systems the hubs get their star or barycenter glyph; when null (the offline dump, which
    // has no live bodies to read r_lo from) they fall back to the representative v_inf sum,
    // enough to verify topology.
    public static LayoutTree ToLayoutTree(VisualTree visual, SystemGraph? graph = null)
    {
        LayoutNode root = Convert(visual.Root, graph, labelOverride: null, stub: false);
        return LayoutTree.FromRoot($"reroot-{visual.RootBodyId}", root);
    }

    // One destination system as its own layout tree, rooted at the node the Interstellar edge
    // leads to. Collapsed, that root is the whole part, drawn as a star-system stub with a
    // summary of what it holds. The root label carries the distance between the two system
    // roots, and the renderer draws it as the system's title. The summary rides on an opened
    // root too, so the strip sizes the system's place the same way open or not.
    public static LayoutTree ToPartTree(Edge interstellar, SystemGraph? graph, bool expanded)
    {
        StateNode hub = interstellar.To;
        string label = hub.Body.Id + "  " + Format.Distance(interstellar.InterstellarDistance);
        string summary = Summary(hub, graph);
        LayoutNode root = expanded
            ? Convert(hub, graph, label, stub: false, summary)
            : ConvertNode(hub, graph, label, stub: true, summary);
        LayoutTree tree = LayoutTree.FromRoot($"system-{hub.Body.Id}", root);
        // Only the ego root gets the root size, halo and ring.
        root.IsRoot = false;
        root.IsSystemRoot = true;
        return tree;
    }

    // What a collapsed system holds, as "1 star, 4 planets": its star hubs, and the distinct
    // planet-level bodies with a low orbit, from the subtree the visual tree built for it.
    private static string Summary(StateNode hub, SystemGraph? graph)
    {
        if (graph == null)
            return "";
        var stars = new HashSet<string>();
        var planets = new HashSet<string>();
        var stack = new Stack<StateNode>();
        stack.Push(hub);
        while (stack.Count > 0)
        {
            StateNode node = stack.Pop();
            if (node.Kind == StateKind.Hub && HubRoleOf(node.Body, graph) == HubRole.Star)
                stars.Add(node.Body.Id);
            else if (node.Kind == StateKind.LowOrbit && RankOf(node.Body, graph) == 1)
                planets.Add(node.Body.Id);
            foreach (Edge edge in node.Out)
            {
                if (edge.Kind != SegmentKind.Interstellar)
                    stack.Push(edge.To);
            }
        }
        string starText = Count(stars.Count, "star");
        string planetText = Count(planets.Count, "planet");
        if (starText.Length > 0 && planetText.Length > 0)
            return starText + ", " + planetText;
        return starText + planetText;
    }

    private static string Count(int n, string noun)
    {
        if (n == 0)
            return "";
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
    }

    private static LayoutNode Convert(StateNode source, SystemGraph? graph, string? labelOverride, bool stub, string summary = "")
    {
        LayoutNode node = ConvertNode(source, graph, labelOverride, stub, summary);
        Func<string, BodyLadder?>? ladderFor = graph != null ? graph.LadderFor : null;

        foreach (Edge edge in source.Out)
        {
            // The scene draws another star system as its own part.
            if (edge.Kind == SegmentKind.Interstellar)
                continue;
            LayoutNode child = Convert(edge.To, graph, labelOverride: null, stub: false);
            double routeDv = BadgeDv(edge, ladderFor, out double injectionDv, out double captureDv);
            node.AddChild(new LayoutEdge
            {
                From = node,
                To = child,
                Class = MapClass(edge.Kind),
                Dv = RepresentativeDv(edge),
                RouteDv = routeDv,
                InjectionDv = injectionDv,
                CaptureDv = captureDv,
                DescentDv = edge.DescentDv,
                TransferTimeSeconds = edge.TransferTimeSeconds,
                IsApproximate = edge.IsApproximate,
                // A capture into a body with a usable atmosphere can aerobrake (the marker is
                // a capability cue, independent of whether the aerobrake toggle is on). The
                // sibling-leg plane-change figure rides along for the toggled-on number.
                Aerobrake = edge.Kind == SegmentKind.Capture && OrbitalStates.HasUsableAtmosphere(edge.To.Body),
                PlaneChangeDv = edge.PlaneChangeDv,
                IsApproach = edge.Kind == SegmentKind.Approach
            });
        }

        return node;
    }

    private static LayoutNode ConvertNode(StateNode source, SystemGraph? graph, string? labelOverride, bool stub, string summary = "")
    {
        string label = labelOverride ?? source.Label;
        return new LayoutNode
        {
            Id = source.Id,
            Label = label,
            // The overview short label is just the body name; a minor-body group keeps its
            // "+N" headline (a body name would be the hub's, which is wrong and collides).
            // The group also keys its dedup group by its own id, not the borrowed hub body,
            // so the hub's own short label and the "+N" never dedup against each other. A
            // destination root keeps its distance at every zoom.
            ShortLabel = source.Kind == StateKind.MinorGroup || labelOverride != null ? label : source.Body.Id,
            BodyId = source.Kind == StateKind.MinorGroup ? source.Id : source.Body.Id,
            Kind = MapKind(source.Kind),
            // A minor-body group gets a fixed mid rank (a moon-sized dot whose name survives
            // the low-zoom minor-label cull), rather than the rank of the hub body it borrows.
            Rank = source.Kind == StateKind.MinorGroup ? MinorGroupRank : RankOf(source.Body, graph),
            IsYouAreHere = source.IsYouAreHere,
            HubRole = source.Kind == StateKind.Hub ? HubRoleOf(source.Body, graph) : HubRole.None,
            IsSystemStub = stub,
            Summary = summary,
            // Mirrored so the GravityWell pass can sign a "you are here" node's well offset
            // (a medium orbit above or below low orbit) by its radius. The column key is
            // assigned structurally in LayoutTree.FromRoot, so it is not set here.
            Radius = source.RadiusFromBody
        };
    }

    // The star or barycenter glyph of a hub, only in a universe with several star systems, so
    // a single-system map keeps its plain hub bus.
    private static HubRole HubRoleOf(Astronomical body, SystemGraph? graph)
    {
        if (graph == null || !graph.HasSeveralSystems)
            return HubRole.None;
        PhysicalNode? node = graph.Find(body.Id);
        return node?.Role switch
        {
            BodyRole.Star => HubRole.Star,
            BodyRole.Barycenter => HubRole.Barycenter,
            _ => HubRole.None
        };
    }

    // The primary dV the badge displays, plus a transfer's two halves (injection = depart,
    // capture = arrive) which the badge shows individually before the total. A transfer's
    // figures are the real coupled Oberth burns, computed by the same rule the route
    // accumulator uses, so the badge and the route breakdown agree. A ladder edge shows its
    // exact self-contained cost (for an Ascent edge that is the ascent; the renderer pairs it
    // with DescentDv to show both directions) and has no injection/capture split. The dV-free
    // connectors (hub links, group links, Approach) show nothing. When no live ladder is
    // available (the offline dump) the transfer falls back to the representative v_inf sum
    // and leaves the split at zero (it renders no badges).
    private static double BadgeDv(Edge edge, Func<string, BodyLadder?>? ladderFor, out double injectionDv, out double captureDv)
    {
        injectionDv = 0.0;
        captureDv = 0.0;
        switch (edge.Kind)
        {
            case SegmentKind.HubLink:
            case SegmentKind.GroupLink:
            case SegmentKind.Interstellar:
            case SegmentKind.Approach:
                return 0.0;
            case SegmentKind.Transfer when edge.Transfer.HasValue:
                if (ladderFor == null)
                    return edge.Transfer.Value.TotalDv;
                TransferLegs legs = TransferBurns.ComputeLegs(edge, ladderFor);
                injectionDv = legs.DepartBurn;
                captureDv = legs.ArriveBurn;
                return legs.Total;
            default:
                return edge.LadderDv;
        }
    }

    private static double RepresentativeDv(Edge edge)
    {
        switch (edge.Kind)
        {
            case SegmentKind.HubLink:
            case SegmentKind.GroupLink:
            case SegmentKind.Interstellar:
            case SegmentKind.Approach:
                return 0.0;
            case SegmentKind.Transfer when edge.Transfer.HasValue:
                return edge.Transfer.Value.TotalDv;
            default:
                return edge.LadderDv;
        }
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
            StateKind.MinorGroup => LayoutKind.MinorGroup,
            _ => LayoutKind.LowOrbit
        };
    }

    private static EdgeClass MapClass(SegmentKind kind)
    {
        return kind switch
        {
            SegmentKind.HubLink => EdgeClass.HubLink,
            SegmentKind.Transfer => EdgeClass.Transfer,
            // A group link behaves like a transfer for layout: it starts its own column (so a
            // GravityWell group sits in its own well, not stacked into the hub's) and drops one
            // band below the hub in the cumulative mode. It carries no dV, so the band step is
            // the minimum and the renderer draws no badge for it.
            SegmentKind.GroupLink => EdgeClass.Transfer,
            // An Approach edge enters another body of a destination system, so it starts a new
            // column like a transfer, at the minimum band step since it carries no dV.
            SegmentKind.Approach => EdgeClass.Transfer,
            SegmentKind.Interstellar => EdgeClass.Transfer,
            _ => EdgeClass.Ladder
        };
    }

    // Cosmetic rank for the dot radius only: star or barycenter 0, planet 1, moon 2, minor
    // body 3. The ego root and hubs get their own sizes in the engine regardless of rank.
    // Astronomical.Class returns the concrete type name ("TerrestrialBody",
    // "AtmosphericBody", "MinorBody", ...) and never "Planet"/"Moon" (those strings
    // live only on the abstract Celestial base), so rank comes from the graph's roles and
    // its minor-body rule, not a Class string compare. The graph keeps the MinorBody
    // exoplanets of a star at planet rank, and a planet of a barycenter is no moon.
    private static int RankOf(Astronomical body, SystemGraph? graph)
    {
        PhysicalNode? node = graph?.Find(body.Id);
        if (node != null)
            return node.IsHubOnly ? 0 : node.IsMinor ? 3 : node.IsMoon ? 2 : 1;
        if (SystemGraph.IsHubOnlyBody(body))
            return 0;
        if (body is MinorBody)
            return 3;
        return body.IsMoon() ? 2 : 1;
    }
}
