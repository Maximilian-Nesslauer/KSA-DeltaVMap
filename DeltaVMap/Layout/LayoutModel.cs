using System.Collections.Generic;

namespace DeltaVMap.Layout;

// The role a node plays in the laid-out tree. This mirrors the model's StateKind but
// is kept independent on purpose: the layout engine carries no game types, so it can
// be exercised offline against synthetic trees. The adapter maps StateKind onto this.
internal enum LayoutKind
{
    Surface,
    LowOrbit,
    Stationary,
    SoiEdge,
    YouAreHere,
    Hub,
    Intercept,
    MinorGroup
}

// How an edge behaves for layout purposes. Ladder and Transfer edges carry a dV that
// drives the band (vertical) spacing; HubLink carries none and keeps both endpoints
// on the same band, which is what lays the hub spine out horizontally instead of
// collapsing it into a vertical stack.
internal enum EdgeClass
{
    Ladder,
    Transfer,
    HubLink
}

internal readonly record struct LayoutPoint(double X, double Y);

// What a hub node stands for, for its glyph only. The layout rules read LayoutKind.Hub and
// never this field, so a star and a barycenter lay out exactly like any other hub bus.
internal enum HubRole
{
    None,
    Star,
    Barycenter
}

// One edge in the layout tree. From is the node nearer the root, To the node further
// out. Dv is the representative cost used for band placement (the exact route burns
// are derived later in routing); it is zero for HubLink edges.
internal sealed class LayoutEdge
{
    public required LayoutNode From { get; init; }
    public required LayoutNode To { get; init; }
    public required EdgeClass Class { get; init; }
    public double Dv { get; init; }
    public bool IsApproximate { get; init; }

    // The dV shown on the badge. For a ladder edge this equals Dv (the exact
    // self-contained cost). For a transfer it is the real Oberth-coupled per-leg burn
    // (depart + capture), derived from the v_inf legs and each end's r_lo, which differs
    // from the band-placement Dv (the representative v_inf sum) on purpose: bands stay
    // on the frame-consistent figure, the badge shows the figure a route actually pays.
    public double RouteDv { get; init; }

    // The two halves of a Transfer's RouteDv, shown individually on the badge before the
    // total: InjectionDv is the departure / ejection burn (leaving the origin onto the
    // transfer), CaptureDv the arrival burn (capture at the destination, into the loose
    // ellipse when the transfer lands on an Intercept). Their sum is RouteDv. Both zero for
    // ladder and hub-link edges, and zero on a transfer when no live ladder was available to
    // derive the Oberth burns (the offline dump, which renders no badges).
    public double InjectionDv { get; init; }
    public double CaptureDv { get; init; }

    // The cheaper landing cost for an Ascent edge on an atmospheric body, so the badge can
    // show both directions (ascent up, descent down). Zero on every other edge, and equal
    // to RouteDv on an airless body (where landing costs the same as ascending), in which
    // case the badge stays single-valued.
    public double DescentDv { get; init; }

    // Hohmann transfer time in seconds, mirrored from the game edge so the renderer can show
    // a time line under a transfer badge without re-reading game state. Zero for ladder and
    // hub-link edges (the offline dump leaves it zero, which is fine: it shows no times).
    public double TransferTimeSeconds { get; init; }

    // Display-only marker flags set by the adapter from the game edge; the layout math
    // ignores them. Aerobrake marks a capture into a body with a usable atmosphere, drawn
    // as a directional aerobrake-possible triangle. PlaneChangeDv is the sibling transfer's
    // inclination cost, drawn as a number when the plane-change toggle is on (zero
    // otherwise, and for every non-sibling edge).
    public bool Aerobrake { get; init; }
    public double PlaneChangeDv { get; init; }

    // An edge inside another star system, which carries no dV of its own (the arrival is one
    // chain into the body the route ends at). Drawn as a thin line with no badge.
    public bool IsApproach { get; init; }

    // Lane index among the parallel edges leaving From, assigned by the router so
    // sibling tracks get distinct perpendicular offsets.
    public int Lane { get; set; }

    // Octilinear one-bend polyline in snapped layout space, filled by the EdgeRouter.
    public IReadOnlyList<LayoutPoint> Polyline { get; set; } = System.Array.Empty<LayoutPoint>();

    public bool IsHubLink => Class == EdgeClass.HubLink;
}

// One node in the layout tree. Inputs (Id, Label, Kind, Rank, tree links) are set by
// the caller or adapter; everything else is filled by the layout pipeline. Width and
// Height are the approximate rendered box used for tidy-tree spacing; X/Y are the
// pre-snap layout position; Col/Row/SnappedX/SnappedY are the grid-snapped result.
internal sealed class LayoutNode
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required LayoutKind Kind { get; init; }

    // Short display text for the zoomed-out overview: the body name alone ("Saturn"
    // instead of "Saturn Low Orbit"), since the rung is already carried by the glyph
    // at that zoom. A minor-body group keeps its "+N" headline. Empty falls back to
    // Label in the renderer (synthetic offline trees do not set it).
    public string ShortLabel { get; init; } = "";

    // Groups every rung of one body under one key so the overview can show a single
    // short label per body instead of one per rung. Empty falls back to Id in the
    // renderer (again for synthetic trees).
    public string BodyId { get; init; } = "";

    // 0 ego root, 1 planet-level, 2 moon-level, 3 minor. Drives the dot radius and is
    // purely cosmetic; it does not affect positioning.
    public int Rank { get; init; }

    // The body / well this node belongs to: every rung of one body shares a column key.
    // Assigned in FromRoot as a ladder-connected component (a node inherits its parent's
    // column unless the connecting edge is a Transfer or HubLink, which starts a new one),
    // so a body that is both an ancestor hub and reached as its own destination splits
    // into two columns the way the topology demands. The GravityWell X pass packs whole
    // columns horizontally and stacks a column's rungs at one X; CumulativeDown ignores it.
    public string Column { get; set; } = string.Empty;

    // Orbital radius from the body center, mirrored from the model (zero for hubs). Used
    // only to sign a "you are here" node's well offset (above or below low orbit); the
    // other rungs take their direction from their kind.
    public double Radius { get; set; }

    public bool IsRoot { get; set; }
    public bool IsYouAreHere { get; set; }

    // Cosmetic: the star or barycenter glyph for a hub, and the collapsed star-system glyph
    // for the one-node part that stands for a whole destination system. Set only when the
    // loaded universe holds several star systems, so a single-system map keeps its plain hub
    // glyphs. Neither changes the layout.
    public HubRole HubRole { get; init; }
    public bool IsSystemStub { get; init; }

    // The root of a star system's graph when the scene holds several systems: every
    // destination part root, and the ego system's hub. The renderer draws its label as a
    // system title with a ring around the dot. Unlike IsRoot it changes no size, halo or fit
    // anchor, and SceneComposer clears it on the ego hub when no other system is shown.
    public bool IsSystemRoot { get; set; }

    // What a destination system holds ("1 star, 4 planets"), set on every destination root and
    // empty for every other node, with its measured width from LayoutEngine.MeasureNodes. A
    // collapsed system shows it as a dim second title line. An opened one keeps it too, so the
    // strip can size the system's slot the same way whether it is open or not.
    public string Summary { get; init; } = "";
    public double SummaryTextW { get; set; }

    // The node's position in LayoutScene.Nodes, set when the scene is composed. The renderer
    // indexes its per-node caches with it, and the badge of an edge with edge.To.Index (in a
    // tree every edge is the parent edge of its To node).
    public int Index { get; set; } = -1;

    // The measured text widths of Label and ShortLabel, from LayoutEngine.MeasureNodes, so the
    // renderer never measures a label per frame.
    public double LabelTextW { get; set; }
    public double ShortLabelTextW { get; set; }

    public LayoutNode? Parent { get; set; }
    public LayoutEdge? ParentEdge { get; set; }
    public List<LayoutEdge> Out { get; } = new();

    // Tree depth from the root (root = 0). Used by the sibling-subtree overlap check,
    // which is a per-level guarantee, not a per-band one.
    public int Depth { get; set; }

    public double Width { get; set; }
    public double Height { get; set; }
    public double DotRadius { get; set; }

    // Band index (0 at the root) and the resulting pre-snap position.
    public int Band { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    // Grid-snapped result.
    public int Col { get; set; }
    public int Row { get; set; }
    public double SnappedX { get; set; }
    public double SnappedY { get; set; }

    // Greedy label placement result. LabelX/LabelY is the box top-left in snapped
    // space; LabelPlaced is false when no candidate slot was free.
    public bool LabelPlaced { get; set; }
    public double LabelX { get; set; }
    public double LabelY { get; set; }

    public void AddChild(LayoutEdge edge)
    {
        edge.To.Parent = this;
        edge.To.ParentEdge = edge;
        Out.Add(edge);
    }
}

// A complete layout tree: the root plus every node in a stable pre-order. Name is a
// label for dumps. The node list order is deterministic so the dump and the
// assertions are reproducible.
internal sealed class LayoutTree
{
    public required string Name { get; init; }
    public required LayoutNode Root { get; init; }
    public required IReadOnlyList<LayoutNode> Nodes { get; init; }

    // Walk the tree from a given root in pre-order, returning every node. Children are
    // visited in their Out order, so the result is fully determined by the tree shape.
    public static List<LayoutNode> PreOrder(LayoutNode root)
    {
        var result = new List<LayoutNode>();
        var stack = new Stack<LayoutNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            LayoutNode node = stack.Pop();
            result.Add(node);
            // Push children in reverse so they pop in their natural Out order.
            for (int i = node.Out.Count - 1; i >= 0; i--)
                stack.Push(node.Out[i].To);
        }
        return result;
    }

    // Assemble a tree from a fully wired root, computing depths, column keys and the
    // pre-order node list in one pass.
    public static LayoutTree FromRoot(string name, LayoutNode root)
    {
        root.IsRoot = true;
        root.Column = root.Id;
        var nodes = new List<LayoutNode>();
        AssignDepth(root, 0, nodes);
        return new LayoutTree { Name = name, Root = root, Nodes = nodes };
    }

    private static void AssignDepth(LayoutNode node, int depth, List<LayoutNode> nodes)
    {
        node.Depth = depth;
        nodes.Add(node);
        foreach (LayoutEdge edge in node.Out)
        {
            // A ladder edge stays within one body's well, so the child shares the column;
            // a Transfer or HubLink crosses into another body, so the child starts a new
            // column keyed by its own Id (the well's entry node).
            edge.To.Column = edge.Class == EdgeClass.Ladder ? node.Column : edge.To.Id;
            AssignDepth(edge.To, depth + 1, nodes);
        }
    }
}

// One separately laid-out part of the scene: another star system, collapsed to its root hub
// or expanded to its whole tree. The part is laid out on its own at the origin and moved
// next to the ego system by SceneComposer, which translates its nodes in place and records
// the offset it applied, so a later compose moves it only by the difference.
internal sealed class LayoutPart
{
    // The Id of the destination system's root hub node, the node the connector ends at.
    public required string RootId { get; init; }
    public required LayoutResult Result { get; init; }
    public bool Expanded { get; init; }

    public double AppliedDx { get; set; }
    public double AppliedDy { get; set; }

    public LayoutNode Root => Result.Tree.Root;
    public double MinX => Result.MinX + AppliedDx;
    public double MinY => Result.MinY + AppliedDy;
    public double MaxX => Result.MaxX + AppliedDx;
    public double MaxY => Result.MaxY + AppliedDy;
}

// The interstellar line from the ego system's root hub to one destination root. Its geometry
// depends only on the bounds of the parts, never on a delta-v, so the cruise speed can never
// move it. BadgeAnchor is the point where the line leaves the shared network (trunk and bus)
// for its own branch, where the route badge of the leg sits. The branch is BadgeAnchor followed
// by Polyline[BranchStart..]; everything before it is drawn once as LayoutScene.Network.
internal sealed class LayoutConnector
{
    public required LayoutNode From { get; init; }
    public required LayoutNode To { get; init; }
    public required IReadOnlyList<LayoutPoint> Polyline { get; init; }
    public LayoutPoint BadgeAnchor { get; init; }
    public int BranchStart { get; init; }
}

// Which side of the ego map the strip of other star systems runs along.
internal enum StripSide
{
    Below,
    Right
}

// Everything the canvas draws: the ego system's layout as LayoutEngine produced it, the
// destination parts in a strip beside it, the connectors between them and a scale-break mark
// on the connector trunk. Nodes holds every node of every part in draw order, with
// LayoutNode.Index set to the position in it. With no parts the scene is the ego layout alone.
internal sealed class LayoutScene
{
    public required LayoutResult Ego { get; init; }
    public required IReadOnlyList<LayoutPart> Parts { get; init; }
    public required IReadOnlyList<LayoutNode> Nodes { get; init; }
    public required IReadOnlyList<LayoutConnector> Connectors { get; init; }
    public LayoutNode? EgoHub { get; init; }
    public StripSide Side { get; init; }

    // The line in the channel between the ego map and the first strip row (a Y for Below, an
    // X for Right) that every connector runs along before it branches into its root, and the
    // shared trunk from the ego hub to the point where it meets that line.
    public double Bus { get; init; }
    public IReadOnlyList<LayoutPoint> Trunk { get; init; } = System.Array.Empty<LayoutPoint>();

    // The connector lines every destination shares, each drawn once: the trunk, the bus of each
    // strip row and the link that runs on to a wrapped row. Each connector adds only its branch.
    public IReadOnlyList<IReadOnlyList<LayoutPoint>> Network { get; init; } = System.Array.Empty<IReadOnlyList<LayoutPoint>>();

    public bool HasBreakMark { get; init; }
    public LayoutPoint BreakMark { get; init; }
    public bool BreakMarkVertical { get; init; }
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }

    public LayoutConfig Config => Ego.Config;
    public LayoutNode Root => Ego.Tree.Root;
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
}
