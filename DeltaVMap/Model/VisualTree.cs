using System.Collections.Generic;
using DeltaVMap.Dv;
using KSA;

namespace DeltaVMap.Model;

// The re-rooted visual tree: the physical tree turned inside out so the ego body
// sits at the root, its ancestors become hub buses climbing to the star, and every
// body's ladder hangs off the structure. This is the data the layout and routing
// code consume; it carries no positions and does no rendering.
//
// Topology:
//  - The root body's low orbit (or surface, for a surface-only body) is the tree
//    root. Its surface, stationary and SOI-edge rungs and its moons all branch off
//    it; the root is its own moons' local hub.
//  - Each ancestor is a Hub node. Off it branch the hub's own ladder (reached by a
//    transfer from the spine child to the hub's low orbit), the spine child's
//    siblings (each a sibling transfer around the hub), and a structural link up to
//    the next hub. The spine link from a body's low orbit to its parent hub carries
//    no dV: the transfer cost lives on the hub's outgoing spokes, which keeps the
//    pairwise EdgeDv intact and avoids a starburst.
//  - Transfer edges wrap the EdgeDv and are never collapsed; the actual burns are
//    derived per route when one is selected.
internal sealed class VisualTree
{
    public required string SystemId { get; init; }
    public required string RootBodyId { get; init; }
    public required StateNode Root { get; init; }
    public required IReadOnlyList<StateNode> Nodes { get; init; }

    // The node carrying the controlled vehicle's classified state, if any.
    public StateNode? YouAreHere { get; init; }

    public static VisualTree Build(SystemGraph graph, DvCache cache, PhysicalNode root, ClassifiedState? egoState, bool fullLadderEverywhere)
    {
        ReRootResult reroot = ReRooter.ReRoot(root);
        var builder = new Builder(graph, cache, new DetailLevel(reroot, fullLadderEverywhere));
        return builder.Build(reroot, egoState);
    }

    // Carries the mutable build state (the node list and shared services) so the
    // recursive helpers do not each thread it through.
    private sealed class Builder
    {
        private readonly SystemGraph _graph;
        private readonly DvCache _cache;
        private readonly DetailLevel _detail;
        private readonly List<StateNode> _nodes = new();
        private StateNode? _youAreHere;

        public Builder(SystemGraph graph, DvCache cache, DetailLevel detail)
        {
            _graph = graph;
            _cache = cache;
            _detail = detail;
        }

        public VisualTree Build(ReRootResult reroot, ClassifiedState? egoState)
        {
            PhysicalNode root = reroot.Root;

            StateNode rootNode;
            StateNode spineTail;

            if (root.IsStar)
            {
                // Interplanetary-cruise root: the star has no ladder, so its hub bus
                // is the tree root and planets hang off it. There is no well-defined
                // parking origin here; the real origin is the vehicle's actual
                // heliocentric orbit, resolved by the router. Link planets structurally.
                StateNode starHub = NewHub(root);
                rootNode = starHub;
                if (egoState.HasValue)
                    AttachCruiseYouAreHere(starHub, root, egoState.Value);
                foreach (PhysicalNode child in root.Children)
                {
                    LadderNodes childLadder = BuildBodySubtree(child);
                    ConnectHubLink(starHub, childLadder.ArrivalAnchor);
                }
                return Finish(rootNode, root.Id);
            }

            // Normal root: build its ladder (with the you-are-here marker), branch its
            // moons off its local hub, then climb the spine of ancestor hubs.
            LadderNodes rootLadder = BuildLadder(root, egoState, asDestination: false);
            rootNode = rootLadder.LocalHub;

            foreach (PhysicalNode child in root.Children)
            {
                LadderNodes childLadder = BuildBodySubtree(child);
                ConnectTransfer(rootLadder.LocalHub, childLadder.ArrivalAnchor,
                    DirectTransfer(root.Body.Mu, rootLadder.LocalHubRadius, TransferRadius(child), IsOpenOrbit(child)));
            }

            spineTail = rootLadder.LocalHub;
            PhysicalNode spineChild = root;

            foreach (HubLevel level in reroot.Spine)
            {
                StateNode hubNode = NewHub(level.Hub);
                ConnectHubLink(spineTail, hubNode);

                // The hub's own ladder, reached from the hub like any other
                // destination via the transfer that drops the spine child into the
                // hub's low orbit. The star is hub-only and contributes no ladder.
                if (!level.Hub.IsStar)
                {
                    LadderNodes hubLadder = BuildLadder(level.Hub, egoState: null, asDestination: false);
                    EdgeDv toHubLo = DirectTransfer(level.Hub.Body.Mu, TransferRadius(spineChild), hubLadder.LocalHubRadius, IsOpenOrbit(spineChild));
                    ConnectTransfer(hubNode, hubLadder.ArrivalAnchor, toHubLo);
                }

                // Siblings of the spine child: each a sibling transfer around the hub.
                foreach (PhysicalNode sibling in level.OtherChildren)
                {
                    LadderNodes siblingLadder = BuildBodySubtree(sibling);
                    EdgeDv transfer = _cache.GetTransfer((IOrbiter)spineChild.Astro, (IOrbiter)sibling.Astro);
                    ConnectTransfer(hubNode, siblingLadder.ArrivalAnchor, transfer);
                }

                spineTail = hubNode;
                spineChild = level.Hub;
            }

            return Finish(rootNode, root.Id);
        }

        private VisualTree Finish(StateNode root, string rootBodyId)
        {
            return new VisualTree
            {
                SystemId = _graph.SystemId,
                RootBodyId = rootBodyId,
                Root = root,
                Nodes = _nodes,
                YouAreHere = _youAreHere
            };
        }

        // Recursively build a body's ladder and everything below it. The body is the
        // local hub for its own children, so each child is reached by a transfer from
        // this body's low orbit. Returns the ladder so the caller can attach the
        // incoming transfer to its arrival anchor.
        private LadderNodes BuildBodySubtree(PhysicalNode body)
        {
            LadderNodes ladder = BuildLadder(body, egoState: null, asDestination: true);

            foreach (PhysicalNode child in body.Children)
            {
                LadderNodes childLadder = BuildBodySubtree(child);
                ConnectTransfer(ladder.LocalHub, childLadder.ArrivalAnchor,
                    DirectTransfer(body.Body.Mu, ladder.LocalHubRadius, TransferRadius(child), IsOpenOrbit(child)));
            }

            return ladder;
        }

        // Materialize one body's ladder rungs and wire the within-body edges. The set
        // of rungs honours the detail level (full near the root, core for distant
        // bodies). asDestination controls how a surface-only body presents: as a
        // destination it gets an Intercept arrival node above its surface; as the ego
        // root you are already on the surface, so the surface itself is the anchor.
        private LadderNodes BuildLadder(PhysicalNode body, ClassifiedState? egoState, bool asDestination)
        {
            BodyLadder ladder = body.Ladder;
            NodeDetail detail = _detail.For(body.Id);
            var result = new LadderNodes();

            bool hasSurfaceRung = ladder.HasSurface && DetailLevel.IncludeRung(StateKind.Surface, detail);
            if (hasSurfaceRung)
                result.Surface = NewNode(body, StateKind.Surface, ladder.MeanRadius);

            if (ladder.CanHoldOrbit)
            {
                result.LowOrbit = NewNode(body, StateKind.LowOrbit, ladder.LowOrbitRadius);

                if (ladder.StationaryRadius.HasValue && ladder.StationaryRadius.Value > ladder.LowOrbitRadius
                    && DetailLevel.IncludeRung(StateKind.Stationary, detail))
                    result.Stationary = NewNode(body, StateKind.Stationary, ladder.StationaryRadius.Value);

                if (ladder.SoiRadius.HasValue && DetailLevel.IncludeRung(StateKind.SoiEdge, detail))
                    result.SoiEdge = NewNode(body, StateKind.SoiEdge, ladder.SoiRadius.Value);
            }

            // Pick the anchor (where an incoming transfer lands) and the local hub
            // (what children and the spine branch from).
            if (result.LowOrbit != null)
            {
                result.ArrivalAnchor = result.LowOrbit;
                result.LocalHub = result.LowOrbit;
                result.LocalHubRadius = ladder.LowOrbitRadius;
            }
            else if (asDestination)
            {
                // Surface-only destination: you intercept the tiny SOI and land.
                result.Intercept = NewNode(body, StateKind.Intercept, ladder.SoiRadius ?? ladder.MeanRadius);
                result.ArrivalAnchor = result.Intercept;
                result.LocalHub = result.Surface ?? result.Intercept;
                result.LocalHubRadius = ladder.MeanRadius;
            }
            else
            {
                // Surface-only ego root: you are standing on it.
                result.Surface ??= NewNode(body, StateKind.Surface, ladder.MeanRadius);
                result.ArrivalAnchor = result.Surface;
                result.LocalHub = result.Surface;
                result.LocalHubRadius = ladder.MeanRadius;
            }

            WireLadderEdges(body, ladder, result);

            if (egoState.HasValue)
                InsertYouAreHere(body, ladder, result, egoState.Value);

            return result;
        }

        // Connect the rungs within a single body. Surface ascends to low orbit;
        // stationary and SOI-edge rungs raise off low orbit; a surface-only body's
        // surface hangs off its intercept as a landing.
        private void WireLadderEdges(PhysicalNode body, BodyLadder ladder, LadderNodes nodes)
        {
            double mu = body.Body.Mu;

            if (nodes.LowOrbit != null && nodes.Surface != null)
            {
                double ascent = OrbitalStates.ComputeAscent(ladder).Effective;
                ConnectLadder(nodes.LowOrbit, nodes.Surface, SegmentKind.Ascent, ascent);
            }

            if (nodes.LowOrbit != null && nodes.Stationary != null)
            {
                double dv = DeltaVCalculator.CircularToCircular(mu, ladder.LowOrbitRadius, ladder.StationaryRadius!.Value);
                ConnectLadder(nodes.LowOrbit, nodes.Stationary, SegmentKind.Raise, dv);
            }

            if (nodes.LowOrbit != null && nodes.SoiEdge != null)
            {
                double dv = DeltaVCalculator.EscapeToSoi(mu, ladder.LowOrbitRadius, ladder.SoiRadius!.Value);
                ConnectLadder(nodes.LowOrbit, nodes.SoiEdge, SegmentKind.Raise, dv);
            }

            if (nodes.Intercept != null && nodes.Surface != null)
            {
                // Rough landing estimate on a body too small to park around: kill the
                // near-surface circular speed. Good enough for a reference badge.
                double dv = DeltaVCalculator.CircularSpeed(mu, ladder.MeanRadius);
                ConnectLadder(nodes.Intercept, nodes.Surface, SegmentKind.Land, dv);
            }
        }

        // Place the controlled vehicle's classified state. If it snapped onto an
        // existing rung, just flag that node; otherwise (a medium orbit between
        // rungs) insert a dedicated node raised off the low orbit.
        private void InsertYouAreHere(PhysicalNode body, BodyLadder ladder, LadderNodes nodes, ClassifiedState state)
        {
            StateNode? target = state.Kind switch
            {
                StateKind.Surface => nodes.Surface,
                StateKind.LowOrbit => nodes.LowOrbit,
                StateKind.Stationary => nodes.Stationary,
                StateKind.SoiEdge => nodes.SoiEdge,
                _ => null
            };

            if (target != null)
            {
                target.IsYouAreHere = true;
                _youAreHere = target;
                return;
            }

            StateNode anchor = nodes.LowOrbit ?? nodes.LocalHub;
            double anchorRadius = nodes.LowOrbit != null ? ladder.LowOrbitRadius : nodes.LocalHubRadius;
            var youAreHere = NewNode(body, StateKind.YouAreHere, state.Radius);
            double dv = DeltaVCalculator.CircularToCircular(body.Body.Mu, anchorRadius, state.Radius);
            ConnectLadder(anchor, youAreHere, SegmentKind.Raise, dv);
            youAreHere.IsYouAreHere = true;
            _youAreHere = youAreHere;
        }

        private void AttachCruiseYouAreHere(StateNode starHub, PhysicalNode star, ClassifiedState state)
        {
            var youAreHere = NewNode(star, StateKind.YouAreHere, state.Radius);
            ConnectHubLink(starHub, youAreHere);
            youAreHere.IsYouAreHere = true;
            _youAreHere = youAreHere;
        }

        private StateNode NewHub(PhysicalNode body)
        {
            return NewNode(body, StateKind.Hub, 0.0);
        }

        private StateNode NewNode(PhysicalNode body, StateKind kind, double radius)
        {
            var node = new StateNode
            {
                Id = $"{body.Id}.{kind}",
                Body = body.Astro,
                Kind = kind,
                RadiusFromBody = radius,
                Label = $"{body.Id} {KindLabel(kind)}"
            };
            _nodes.Add(node);
            return node;
        }

        private static Edge ConnectTransfer(StateNode from, StateNode to, EdgeDv dv)
        {
            var edge = new Edge
            {
                From = from,
                To = to,
                Kind = SegmentKind.Transfer,
                Transfer = dv,
                TransferTimeSeconds = dv.TransferTimeSeconds,
                IsApproximate = dv.IsApproximate
            };
            from.AddChild(edge);
            return edge;
        }

        private static Edge ConnectLadder(StateNode from, StateNode to, SegmentKind kind, double ladderDv)
        {
            var edge = new Edge { From = from, To = to, Kind = kind, LadderDv = ladderDv };
            from.AddChild(edge);
            return edge;
        }

        private static Edge ConnectHubLink(StateNode from, StateNode to)
        {
            var edge = new Edge { From = from, To = to, Kind = SegmentKind.HubLink };
            from.AddChild(edge);
            return edge;
        }

        // Transfer radius of a non-star body around its parent. Uses the shared
        // OrbitalStates helper so the open-orbit (comet) fallback matches DvCache and
        // cannot drift. Never called on the star (which has no orbit).
        private static double TransferRadius(PhysicalNode body)
        {
            Orbit orbit = ((IOrbiter)body.Astro).Orbit;
            return OrbitalStates.TransferRadius(orbit);
        }

        // A transfer to or from an open orbit (comet, e >= 1) is flagged approximate,
        // matching the sibling-transfer path in DvCache. The hub side of a direct
        // transfer is always a bound body, so only the non-hub endpoint is checked.
        private static bool IsOpenOrbit(PhysicalNode body)
        {
            return ((IOrbiter)body.Astro).Orbit.Eccentricity >= 1.0;
        }

        private static EdgeDv DirectTransfer(double muHub, double r1, double r2, bool isApproximate)
        {
            DeltaVCalculator.Hohmann(muHub, r1, r2, out double depart, out double arrive);
            double time = DeltaVCalculator.TransferTimeSeconds(muHub, r1, r2);
            return new EdgeDv(depart, arrive, time, isApproximate);
        }

        private static string KindLabel(StateKind kind)
        {
            return kind switch
            {
                StateKind.Surface => "Surface",
                StateKind.LowOrbit => "Low Orbit",
                StateKind.Stationary => "Stationary",
                StateKind.SoiEdge => "SOI Edge",
                StateKind.Intercept => "Intercept",
                StateKind.Hub => "Hub",
                StateKind.YouAreHere => "You Are Here",
                _ => kind.ToString()
            };
        }
    }

    // The nodes produced for a single body's ladder, plus the two attachment points
    // the caller needs: ArrivalAnchor (where an incoming transfer lands) and
    // LocalHub (what this body's children and the spine branch from).
    private sealed class LadderNodes
    {
        public StateNode ArrivalAnchor = null!;
        public StateNode LocalHub = null!;
        public double LocalHubRadius;
        public StateNode? Surface;
        public StateNode? LowOrbit;
        public StateNode? Stationary;
        public StateNode? SoiEdge;
        public StateNode? Intercept;
    }
}
