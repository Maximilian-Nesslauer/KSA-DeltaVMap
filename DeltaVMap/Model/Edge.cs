using System;
using DeltaVMap.Dv;

namespace DeltaVMap.Model;

// What a visual edge represents physically. Ladder edges (Ascent, Raise, Land,
// Capture) carry a single self-contained dV in LadderDv; Capture is the inbound
// circularize from a loose capture ellipse (an arrival Intercept node) down to low
// orbit. Transfer edges carry the coupled Hohmann as an EdgeDv and never collapse it
// to one number, because the actual departure and capture burns depend on which
// endpoint is the hub and on each body's low-orbit radius; those are derived when a
// route is accumulated, not baked in here. HubLink is a structural connector with no
// dV: it stitches a body's low orbit to its parent hub bus so the tree stays
// connected without double-counting the transfer, which lives on the hub's outgoing
// spokes instead. GroupLink likewise carries no dV: it hangs a synthetic "+N" minor-body
// group off its hub. It is a spoke, not part of the spine bus, so it is kept distinct
// from HubLink (which the layout treats as the horizontal hub row).
internal enum SegmentKind
{
    Ascent,
    Raise,
    Land,
    Capture,
    Transfer,
    HubLink,
    GroupLink
}

// Display-only feasibility markers, filled in by the routing code. Kept here so the
// edge shape is stable; for now they stay None.
[Flags]
internal enum EdgeFlags
{
    None = 0,
    AerobrakePossible = 1 << 0,
    JetPossible = 1 << 1,
    IonPossible = 1 << 2
}

// One edge in the visual tree. From is the node closer to the root, To the node
// further out. The cost lives in exactly one of two places depending on Kind: a
// ladder edge uses LadderDv, a Transfer edge uses Transfer (the two v_inf legs).
// HubLink edges carry neither. PlaneChangeDv is additive and optional, excluded
// from the baseline total and only shown when the plane-change toggle is on.
internal sealed class Edge
{
    public required StateNode From { get; init; }
    public required StateNode To { get; init; }
    public required SegmentKind Kind { get; init; }

    // Within-SOI ladder edges (Ascent, Raise, Land, Capture): a single self-contained
    // cost. Zero for Transfer and HubLink edges.
    public double LadderDv { get; init; }

    // Descent cost for an Ascent edge (low orbit -> surface), which is cheaper than the
    // ascent on a body with an atmosphere (drag does most of the braking). Set only for
    // Ascent edges; the route uses it when traversing the edge downward (landing) and
    // LadderDv when traversing it upward (ascent). Zero on every other edge.
    public double DescentDv { get; init; }

    // Cross-hub transfer edges: the coupled Hohmann, both v_inf legs. Null for every
    // non-Transfer edge. The displayed and accumulated burns are derived from these
    // legs plus each endpoint's r_lo at accumulation time, so they are deliberately
    // not stored here.
    public EdgeDv? Transfer { get; init; }

    // Hohmann transfer time, taken from the EdgeDv. Zero for ladder and hub links.
    public double TransferTimeSeconds { get; init; }

    // Comet or hyperbolic transfer, drawn with a "~" prefix and a legend note.
    public bool IsApproximate { get; init; }

    // Additive plane-change dV, 0 below the half-degree threshold.
    public double PlaneChangeDv { get; init; }

    public EdgeFlags Flags { get; init; }

    public bool IsTransfer => Kind == SegmentKind.Transfer;
    public bool IsStructural => Kind == SegmentKind.HubLink;
}
