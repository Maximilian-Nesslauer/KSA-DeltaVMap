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
    Intercept
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

    // The cheaper landing cost for an Ascent edge on an atmospheric body, so the badge can
    // show both directions (ascent up, descent down). Zero on every other edge, and equal
    // to RouteDv on an airless body (where landing costs the same as ascending), in which
    // case the badge stays single-valued.
    public double DescentDv { get; init; }

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

    // 0 ego root, 1 planet-level, 2 moon-level, 3 minor. Drives the dot radius and is
    // purely cosmetic; it does not affect positioning.
    public int Rank { get; init; }

    public bool IsRoot { get; set; }
    public bool IsYouAreHere { get; set; }

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

    // Assemble a tree from a fully wired root, computing depths and the pre-order
    // node list in one pass.
    public static LayoutTree FromRoot(string name, LayoutNode root)
    {
        root.IsRoot = true;
        var nodes = new List<LayoutNode>();
        AssignDepth(root, 0, nodes);
        return new LayoutTree { Name = name, Root = root, Nodes = nodes };
    }

    private static void AssignDepth(LayoutNode node, int depth, List<LayoutNode> nodes)
    {
        node.Depth = depth;
        nodes.Add(node);
        foreach (LayoutEdge edge in node.Out)
            AssignDepth(edge.To, depth + 1, nodes);
    }
}
