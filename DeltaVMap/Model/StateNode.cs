using System.Collections.Generic;
using DeltaVMap.Dv;
using KSA;

namespace DeltaVMap.Model;

// One node in the re-rooted visual tree: a state you can be in (a ladder rung, a
// dynamic "you are here", a flyby intercept) or a pure hub bus (an ancestor body
// in transfer). Nodes form a tree rooted at the ego body: Parent points toward the
// root, Out lists the edges leading away from it. The Id is stable across rebuilds
// ("<body>.<state>", e.g. "Luna.LowOrbit", "Sol.Hub") so selection and layout
// caches can key off it.
internal sealed class StateNode
{
    public required string Id { get; init; }

    // The body this node belongs to. Always set, including for Hub nodes (the hub
    // is that body in transfer).
    public required Astronomical Body { get; init; }

    public required StateKind Kind { get; init; }

    // Radius from the body center used for classification and vertical ordering of
    // the ladder. Zero for Hub nodes, which are not a place at a radius.
    public double RadiusFromBody { get; init; }

    // The edge from Parent to this node, or null for the root. Stored for cheap
    // upward walks when a route is accumulated.
    public Edge? ParentEdge { get; set; }
    public StateNode? Parent { get; set; }

    public List<Edge> Out { get; } = new();

    // True when the controlled vehicle's classified state lands on (or snaps to)
    // this node. At most one node per tree carries it.
    public bool IsYouAreHere { get; set; }

    // Human-facing label, e.g. "Earth Low Orbit". The layout pass measures it for
    // node widths; for now it just drives the debug dump.
    public required string Label { get; init; }

    public void AddChild(Edge edge)
    {
        edge.To.Parent = this;
        edge.To.ParentEdge = edge;
        Out.Add(edge);
    }
}
