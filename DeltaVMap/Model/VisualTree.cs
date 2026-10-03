using System;
using System.Collections.Generic;
using System.Globalization;
using DeltaVMap.Dv;
using KSA;

namespace DeltaVMap.Model;

// The re-rooted visual tree: the physical tree turned inside out so the ego body
// sits at the root, its ancestors become hub buses climbing to the system root (a
// star, or the barycenter of a multiple star system), and every
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
//  - When the loaded universe holds several star systems, the ego system's root hub
//    also carries one Interstellar edge per other system (nearest first, at most
//    SystemGraph.MaxShownSystems, the rest in one "+N star systems" group). Under it
//    the destination system hangs from dV-free Approach edges: a star or barycenter is
//    a hub with a parking-orbit leaf and its children below, a planet or moon is its
//    ladder with its moons below. One tree keeps routing, selection and the route
//    highlight the same for every target; the layout cuts the tree at the Interstellar
//    edges and draws each destination as its own part.
internal sealed class VisualTree
{
    public required string SystemId { get; init; }
    public required string RootBodyId { get; init; }
    public required StateNode Root { get; init; }
    public required IReadOnlyList<StateNode> Nodes { get; init; }

    // The node carrying the controlled vehicle's classified state, if any.
    public StateNode? YouAreHere { get; init; }

    // The ego system's root hub, the node every Interstellar edge leaves from.
    public required StateNode SystemHub { get; init; }

    // The Interstellar edges, nearest destination first. Empty with a single star system.
    public required IReadOnlyList<Edge> InterstellarEdges { get; init; }

    private readonly Dictionary<(string BodyId, StateKind Kind), StateNode> _byBody = new();

    // The node of one body in one state, or null when the tree does not hold it.
    public StateNode? FindBodyNode(string bodyId, StateKind kind)
    {
        return _byBody.TryGetValue((bodyId, kind), out StateNode? node) ? node : null;
    }

    private void IndexBodies()
    {
        for (int i = 0; i < Nodes.Count; i++)
            _byBody.TryAdd((Nodes[i].Body.Id, Nodes[i].Kind), Nodes[i]);
    }

    public static VisualTree Build(SystemGraph graph, DvCache cache, PhysicalNode root, ClassifiedState? egoState, BuildOptions options)
    {
        ReRootResult reroot = ReRooter.ReRoot(root);
        var builder = new Builder(graph, cache, new DetailLevel(reroot, options.FullLadder), options);
        return builder.Build(reroot, egoState);
    }

    // Carries the mutable build state (the node list and shared services) so the
    // recursive helpers do not each thread it through.
    private sealed class Builder
    {
        private readonly SystemGraph _graph;
        private readonly DvCache _cache;
        private readonly DetailLevel _detail;
        private readonly BuildOptions _options;
        private readonly List<StateNode> _nodes = new();
        private readonly List<Edge> _interstellar = new();
        private StateNode? _youAreHere;

        public Builder(SystemGraph graph, DvCache cache, DetailLevel detail, BuildOptions options)
        {
            _graph = graph;
            _cache = cache;
            _detail = detail;
            _options = options;
        }

        // Whether a destination body is shown given the visibility toggles. A minor body
        // (PhysicalNode.IsMinor) drops when "show minor bodies" is off; a comet drops when
        // "show comets" is off. Major bodies (planets, moons, stars) are always shown. The
        // root and spine ancestors are never passed here, so they are never hidden.
        private bool Include(PhysicalNode body)
        {
            Astronomical a = body.Astro;

            // A revealed (searched) body is always shown, ahead of every hide rule, so a
            // chosen asteroid stays visible under isolate and is pulled out of its group.
            if (_options.IsRevealed(body.Id))
                return true;

            bool minor = body.IsMinor;
            // Isolate keeps only the spine, the major bodies and revealed bodies, so it hides
            // every un-revealed minor body (asteroids and comets alike).
            if (_options.Isolate && minor)
                return false;
            if (!_options.ShowMinorBodies && minor)
                return false;
            if (!_options.ShowComets && a is Comet)
                return false;
            return true;
        }

        public VisualTree Build(ReRootResult reroot, ClassifiedState? egoState)
        {
            PhysicalNode root = reroot.Root;

            StateNode rootNode;
            StateNode spineTail;

            if (root.IsHubOnly)
            {
                // Cruise root: a star or barycenter is hub-only, so its hub bus is the tree
                // root and its planets (or, under a barycenter, its stars) hang off it. There
                // is no well-defined parking origin here; the real origin is the vehicle's
                // actual orbit around the root, resolved by the router. Link them
                // structurally. A star of a multiple system then climbs the spine to its
                // barycenter like any other root, so its sibling stars stay reachable.
                StateNode starHub = NewHub(root);
                rootNode = starHub;
                if (egoState.HasValue)
                    AttachCruiseYouAreHere(starHub, root, egoState.Value);

                // A vessel coasting on an open orbit around a system root (arriving from or
                // leaving for another star system) has no orbit to transfer from, so with
                // several systems its targets are priced from its measured excess: they hang
                // from Approach edges like a destination system, the root's parking orbit
                // included, so a capture chosen at that system's stub survives the crossing.
                // The route panel prices them only while the vessel still falls inward.
                if (egoState is { IsOpenCruise: true } && root.IsSystemRoot && _graph.HasSeveralSystems)
                {
                    ConnectApproach(starHub, NewNode(root, StateKind.LowOrbit, root.Ladder.LowOrbitRadius));
                    AddChildren(starHub, root.Children, child => ConnectApproach(starHub, BuildDestination(child)));
                }
                else
                {
                    AddChildren(starHub, root.Children, child =>
                    {
                        LadderNodes childLadder = BuildBodySubtree(child);
                        ConnectHubLink(starHub, childLadder.ArrivalAnchor);
                    });
                }
                spineTail = starHub;
            }
            else
            {
                // Normal root: build its ladder (with the you-are-here marker), branch its
                // moons off its local hub, then climb the spine of ancestor hubs.
                LadderNodes rootLadder = BuildLadder(root, egoState, asDestination: false);
                rootNode = rootLadder.LocalHub;

                AddChildren(rootLadder.LocalHub, root.Children, child =>
                {
                    LadderNodes childLadder = BuildBodySubtree(child);
                    ConnectTransfer(rootLadder.LocalHub, childLadder.ArrivalAnchor,
                        DirectTransfer(root.Body.Mu, rootLadder.LocalHubRadius, false, 0.0, TransferRadius(child), IsOpenOrbit(child), Ecc(child)));
                });

                spineTail = rootLadder.LocalHub;
            }

            PhysicalNode spineChild = root;

            foreach (HubLevel level in reroot.Spine)
            {
                StateNode hubNode = NewHub(level.Hub);
                ConnectHubLink(spineTail, hubNode);

                // The hub's own ladder, reached from the hub like any other
                // destination via the transfer that drops the spine child into the
                // hub's low orbit. A star or barycenter is hub-only and contributes no ladder.
                if (!level.Hub.IsHubOnly)
                {
                    LadderNodes hubLadder = BuildLadder(level.Hub, egoState: null, asDestination: false);
                    // The spine child descends to the hub's low orbit; the open end here is
                    // the spine child (r1), which is a comet only when the map is rooted at one.
                    EdgeDv toHubLo = DirectTransfer(level.Hub.Body.Mu, TransferRadius(spineChild), IsOpenOrbit(spineChild), Ecc(spineChild), hubLadder.LocalHubRadius, false, 0.0);
                    ConnectTransfer(hubNode, hubLadder.ArrivalAnchor, toHubLo);
                }

                // Siblings of the spine child: each a sibling transfer around the hub. A
                // local pins the spine child for the callback, since the loop reassigns it
                // below (the callback runs synchronously here, but the local keeps it clear).
                PhysicalNode hubSpineChild = spineChild;
                AddChildren(hubNode, level.OtherChildren, sibling =>
                {
                    LadderNodes siblingLadder = BuildBodySubtree(sibling);
                    EdgeDv transfer = _cache.GetTransfer((IOrbiter)hubSpineChild.Astro, (IOrbiter)sibling.Astro);
                    double planeChange = SiblingPlaneChange(hubSpineChild, sibling, transfer);
                    ConnectTransfer(hubNode, siblingLadder.ArrivalAnchor, transfer, planeChange);
                });

                spineTail = hubNode;
                spineChild = level.Hub;
            }

            if (_graph.HasSeveralSystems)
                AddDestinationSystems(spineTail, spineChild);

            return Finish(rootNode, root.Id, spineTail);
        }

        private VisualTree Finish(StateNode root, string rootBodyId, StateNode systemHub)
        {
            var tree = new VisualTree
            {
                SystemId = _graph.SystemId,
                RootBodyId = rootBodyId,
                Root = root,
                Nodes = _nodes,
                YouAreHere = _youAreHere,
                SystemHub = systemHub,
                InterstellarEdges = _interstellar
            };
            tree.IndexBodies();
            return tree;
        }

        // One Interstellar edge from the ego system's root hub to each other star system,
        // nearest first. Past MaxShownSystems the rest fold into one group node, except a
        // system that holds a revealed (searched) body, which is always shown.
        private void AddDestinationSystems(StateNode systemHub, PhysicalNode systemRoot)
        {
            IReadOnlyList<PhysicalNode> destinations = _graph.DestinationsFrom(systemRoot);
            List<PhysicalNode>? folded = null;
            int shown = 0;
            for (int i = 0; i < destinations.Count; i++)
            {
                PhysicalNode destination = destinations[i];
                if (shown >= SystemGraph.MaxShownSystems && !HoldsRevealedBody(destination))
                {
                    (folded ??= new List<PhysicalNode>()).Add(destination);
                    continue;
                }
                shown++;
                StateNode entry = BuildDestination(destination);
                ConnectInterstellar(systemHub, entry, SystemGraph.Distance(systemRoot, destination));
            }

            if (folded != null)
            {
                var group = new StateNode
                {
                    Id = $"{systemRoot.Id}.SystemGroup",
                    Body = systemRoot.Astro,
                    Kind = StateKind.MinorGroup,
                    RadiusFromBody = 0.0,
                    Label = "+" + folded.Count.ToString(CultureInfo.InvariantCulture) + " star systems",
                    GroupMembers = folded
                };
                _nodes.Add(group);
                ConnectGroupLink(systemHub, group);
            }
        }

        private bool HoldsRevealedBody(PhysicalNode systemRoot)
        {
            if (_options.RevealedBodies == null)
                return false;
            foreach (string id in _options.RevealedBodies)
            {
                PhysicalNode? body = _graph.Find(id);
                if (body != null && ReferenceEquals(SystemGraph.SystemRootOf(body), systemRoot))
                    return true;
            }
            return false;
        }

        // A body of another star system, or reached from an open cruise, and everything below
        // it. A star or barycenter is a hub with its parking orbit as a leaf, so a route can end
        // there without a planet ever hanging under that parking orbit. A planet or moon is its
        // ladder entered at its arrival anchor, with its moons below its low orbit. Every edge in
        // between is a dV-free Approach. Returns the node the incoming edge attaches to.
        private StateNode BuildDestination(PhysicalNode body)
        {
            if (body.IsHubOnly)
            {
                StateNode hub = NewHub(body);
                StateNode parking = NewNode(body, StateKind.LowOrbit, body.Ladder.LowOrbitRadius);
                ConnectApproach(hub, parking);
                AddChildren(hub, body.Children, child => ConnectApproach(hub, BuildDestination(child)));
                return hub;
            }

            LadderNodes ladder = BuildLadder(body, egoState: null, asDestination: true);
            AddChildren(ladder.LocalHub, body.Children, child => ConnectApproach(ladder.LocalHub, BuildDestination(child)));
            return ladder.ArrivalAnchor;
        }

        // Recursively build a body's ladder and everything below it. The body is the
        // local hub for its own children, so each child is reached by a transfer from
        // this body's low orbit. Returns the ladder so the caller can attach the
        // incoming transfer to its arrival anchor.
        private LadderNodes BuildBodySubtree(PhysicalNode body)
        {
            LadderNodes ladder = BuildLadder(body, egoState: null, asDestination: true);

            AddChildren(ladder.LocalHub, body.Children, child =>
            {
                LadderNodes childLadder = BuildBodySubtree(child);
                ConnectTransfer(ladder.LocalHub, childLadder.ArrivalAnchor,
                    DirectTransfer(body.Body.Mu, ladder.LocalHubRadius, false, 0.0, TransferRadius(child), IsOpenOrbit(child), Ecc(child)));
            });

            return ladder;
        }

        // Branch a hub's children off it, collapsing minor bodies into one synthetic "+N"
        // group when the hub carries more of them than the threshold. buildChild builds one
        // real child's subtree and wires its incoming edge; it runs for every shown body, in
        // the graph's deterministic (innermost-first) order, so a hub under the threshold
        // keeps its exact lane layout (a stock-sized system is unchanged). Only a dense hub
        // collapses, and then the minor bodies are recorded on the group rather than built as
        // thousands of lanes / subtrees; search / isolate surfaces a chosen member later, so
        // the lanes are never built all at once (the member list itself is cheap). Visibility
        // (Include) is applied first, so a hidden minor body counts toward neither the lanes
        // nor the group.
        private void AddChildren(StateNode hub, IReadOnlyList<PhysicalNode> children, Action<PhysicalNode> buildChild)
        {
            int minorCount = 0;
            foreach (PhysicalNode child in children)
            {
                if (Include(child) && child.IsMinor)
                    minorCount++;
            }

            bool collapse = minorCount > _options.MinorGroupThreshold;
            List<PhysicalNode>? collapsed = collapse ? new List<PhysicalNode>(minorCount) : null;

            foreach (PhysicalNode child in children)
            {
                if (!Include(child))
                    continue;
                // A revealed (searched) minor body is built as its own lane, never folded into
                // the group, so the search pulls exactly that body out of the "+N".
                if (collapse && child.IsMinor && !_options.IsRevealed(child.Id))
                    collapsed!.Add(child);
                else
                    buildChild(child);
            }

            if (collapsed != null && collapsed.Count > 0)
                AddMinorGroup(hub, collapsed);
        }

        // Create the synthetic "+N" group node for a hub's collapsed minor bodies and hang it
        // off the hub by a dV-free GroupLink. The group borrows the hub's body for its color
        // and a stable Id ("<hub>.MinorGroup", unique because a hub appears once per tree); it
        // is not a real destination, so it carries no ladder and no transfer cost.
        private void AddMinorGroup(StateNode hub, List<PhysicalNode> members)
        {
            var group = new StateNode
            {
                Id = $"{hub.Body.Id}.MinorGroup",
                Body = hub.Body,
                Kind = StateKind.MinorGroup,
                RadiusFromBody = 0.0,
                Label = MinorGroupLabel(members),
                GroupMembers = members
            };
            _nodes.Add(group);
            ConnectGroupLink(hub, group);
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

                // The outbound SOI-edge rung (a place you climb out to) belongs to the
                // body you are sitting at, not to one you arrive at: a destination gets
                // an inbound Intercept capture anchor instead (picked below).
                if (ladder.SoiRadius.HasValue && DetailLevel.IncludeRung(StateKind.SoiEdge, detail) && !asDestination)
                    result.SoiEdge = NewNode(body, StateKind.SoiEdge, ladder.SoiRadius.Value);
            }

            // Pick the arrival anchor (where an incoming transfer lands) and the local
            // hub (what moons and the spine branch from). A body you arrive at from
            // outside its SOI is captured into a loose ellipse near the SOI edge first
            // (the Intercept anchor) and circularizes down to low orbit; low orbit stays
            // the hub. The ego root is different: you are already there, so low orbit is
            // the anchor and the SOI-edge ellipse is an outbound place you raise to.
            if (result.LowOrbit != null)
            {
                result.LocalHub = result.LowOrbit;
                result.LocalHubRadius = ladder.LowOrbitRadius;

                if (asDestination && detail == NodeDetail.Full && ladder.SoiRadius.HasValue)
                {
                    result.Intercept = NewNode(body, StateKind.Intercept, ladder.SoiRadius.Value);
                    result.ArrivalAnchor = result.Intercept;
                }
                else
                {
                    // Root, the hub's own ladder, or a distant body at core detail: the
                    // transfer lands straight in low orbit.
                    result.ArrivalAnchor = result.LowOrbit;
                }
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
                AscentDv ascent = OrbitalStates.ComputeAscent(ladder);
                double descent = OrbitalStates.ComputeDescent(ladder);
                // Atmospheric ascent is empirical, so flag the edge approximate: the badge
                // and the route breakdown both read this one flag and show the "~" mark.
                // Descent is the cheaper landing cost used when the route lands here.
                ConnectLadder(nodes.LowOrbit, nodes.Surface, SegmentKind.Ascent, ascent.Effective, ascent.IsApproximate, descent);
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

            if (nodes.Intercept != null && nodes.LowOrbit != null)
            {
                // Arrived in a loose ellipse near the SOI edge; circularize down to low
                // orbit. Same magnitude as the outbound escape-to-SOI, run inward. This
                // edge is ONLY the circularize: the hyperbolic capture into the ellipse
                // is the transfer's arrive leg (its v_inf). The two together equal one
                // Oberth capture straight to low orbit, so route accumulation must not
                // also charge a separate full capture on top.
                double dv = DeltaVCalculator.EscapeToSoi(mu, ladder.LowOrbitRadius, ladder.SoiRadius!.Value);
                ConnectLadder(nodes.Intercept, nodes.LowOrbit, SegmentKind.Capture, dv);
            }
            else if (nodes.Intercept != null && nodes.Surface != null)
            {
                // Surface-only body, too small to park: intercept the tiny SOI and land.
                // Rough estimate, kill the near-surface circular speed. Good enough for a
                // reference badge.
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
            var youAreHere = NewNode(star, StateKind.YouAreHere, state.Radius, state.IsOpenCruise);
            ConnectHubLink(starHub, youAreHere);
            youAreHere.IsYouAreHere = true;
            _youAreHere = youAreHere;
        }

        private StateNode NewHub(PhysicalNode body)
        {
            return NewNode(body, StateKind.Hub, 0.0);
        }

        private StateNode NewNode(PhysicalNode body, StateKind kind, double radius, bool isOpenCruise = false)
        {
            var node = new StateNode
            {
                Id = $"{body.Id}.{kind}",
                Body = body.Astro,
                Kind = kind,
                RadiusFromBody = radius,
                IsOpenCruise = isOpenCruise,
                Label = $"{body.Id} {KindLabel(kind, body.IsHubOnly)}"
            };
            _nodes.Add(node);
            return node;
        }

        private static Edge ConnectTransfer(StateNode from, StateNode to, EdgeDv dv, double planeChangeDv = 0.0)
        {
            var edge = new Edge
            {
                From = from,
                To = to,
                Kind = SegmentKind.Transfer,
                Transfer = dv,
                TransferTimeSeconds = dv.TransferTimeSeconds,
                IsApproximate = dv.IsApproximate,
                PlaneChangeDv = planeChangeDv
            };
            from.AddChild(edge);
            return edge;
        }

        // Maximum plane-change dV for a sibling transfer, where both bodies orbit the same
        // hub (two moons of a planet, or two planets of the star). It is turned against the
        // cheaper hyperbolic-excess leg, the same rule the route accumulator applies, so the
        // on-map number and the route breakdown agree. Returns zero for any pairing that is
        // not a true sibling leg, or below the calculator's half-degree threshold.
        private static double SiblingPlaneChange(PhysicalNode a, PhysicalNode b, EdgeDv transfer)
        {
            if (a.Astro is not IOrbiter oa || b.Astro is not IOrbiter ob)
                return 0.0;
            if (oa.Orbit?.Parent == null || ob.Orbit?.Parent == null || !ReferenceEquals(oa.Orbit.Parent, ob.Orbit.Parent))
                return 0.0;
            double di = oa.Orbit.GetRelativeInclination(ob.Orbit).Value();
            double vInf = Math.Min(transfer.DepartDv, transfer.ArriveDv);
            return DeltaVCalculator.PlaneChange(vInf, di);
        }

        private static Edge ConnectLadder(StateNode from, StateNode to, SegmentKind kind, double ladderDv, bool isApproximate = false, double descentDv = 0.0)
        {
            var edge = new Edge { From = from, To = to, Kind = kind, LadderDv = ladderDv, IsApproximate = isApproximate, DescentDv = descentDv };
            from.AddChild(edge);
            return edge;
        }

        private static Edge ConnectHubLink(StateNode from, StateNode to)
        {
            var edge = new Edge { From = from, To = to, Kind = SegmentKind.HubLink };
            from.AddChild(edge);
            return edge;
        }

        private void ConnectInterstellar(StateNode from, StateNode to, double distance)
        {
            var edge = new Edge { From = from, To = to, Kind = SegmentKind.Interstellar, InterstellarDistance = distance };
            from.AddChild(edge);
            _interstellar.Add(edge);
        }

        private static void ConnectApproach(StateNode from, StateNode to)
        {
            from.AddChild(new Edge { From = from, To = to, Kind = SegmentKind.Approach });
        }

        // Hang a minor-body group off its hub. Like a hub link it carries no dV, but it is a
        // leaf spoke, not part of the spine bus, so it gets its own SegmentKind (the layout
        // maps it to a column-starting transfer-class edge, never onto the horizontal hub row).
        private static Edge ConnectGroupLink(StateNode from, StateNode to)
        {
            var edge = new Edge { From = from, To = to, Kind = SegmentKind.GroupLink };
            from.AddChild(edge);
            return edge;
        }

        // The group label, e.g. "+2892 asteroids". The noun is the concrete type when the
        // collapsed bodies are uniform (a pure asteroid belt reads "asteroids"), otherwise the
        // generic "minor bodies". The count is always well above the threshold, so the plural
        // is always correct.
        private static string MinorGroupLabel(IReadOnlyList<PhysicalNode> members)
        {
            return "+" + members.Count.ToString(CultureInfo.InvariantCulture) + " " + MinorNoun(members);
        }

        private static string MinorNoun(IReadOnlyList<PhysicalNode> members)
        {
            bool allAsteroid = true;
            bool allComet = true;
            foreach (PhysicalNode m in members)
            {
                if (m.Astro is not Asteroid)
                    allAsteroid = false;
                if (m.Astro is not Comet)
                    allComet = false;
            }
            if (allAsteroid)
                return "asteroids";
            if (allComet)
                return "comets";
            return "minor bodies";
        }

        // Transfer radius of a body around its parent. Uses the shared OrbitalStates helper
        // so the open-orbit (comet) fallback matches DvCache and cannot drift. Never called
        // on a system root (which has no orbit).
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

        // Eccentricity of a body's orbit, passed to DirectTransfer so an open (comet)
        // endpoint can be velocity-matched at its perihelion. Never called on a system root
        // (which has no orbit); the spine child always orbits something.
        private static double Ecc(PhysicalNode body)
        {
            return ((IOrbiter)body.Astro).Orbit.Eccentricity;
        }

        // A within-SOI / direct transfer between two radii around a hub. Either end may be
        // open (a comet), in which case its real perihelion speed is matched; a bound pair
        // keeps the exact circular Hohmann. The open flags double as the IsApproximate mark.
        private static EdgeDv DirectTransfer(double muHub, double r1, bool r1Open, double r1Ecc, double r2, bool r2Open, double r2Ecc)
        {
            double depart;
            double arrive;
            if (r1Open || r2Open)
                DeltaVCalculator.ConicTransfer(muHub, r1, r1Open, r1Ecc, r2, r2Open, r2Ecc, out depart, out arrive);
            else
                DeltaVCalculator.Hohmann(muHub, r1, r2, out depart, out arrive);
            double time = DeltaVCalculator.TransferTimeSeconds(muHub, r1, r2);
            return new EdgeDv(depart, arrive, time, r1Open || r2Open);
        }

        // A star's or barycenter's one orbit rung is a wide parking orbit, not a low orbit above a
        // surface.
        private static string KindLabel(StateKind kind, bool hubOnly)
        {
            return kind switch
            {
                StateKind.Surface => "Surface",
                StateKind.LowOrbit => hubOnly ? "Parking Orbit" : "Low Orbit",
                StateKind.Stationary => "Stationary",
                StateKind.SoiEdge => "SOI Edge",
                StateKind.Intercept => "Intercept",
                StateKind.Hub => "Hub",
                StateKind.YouAreHere => "You Are Here",
                StateKind.MinorGroup => "Minor Bodies",
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
