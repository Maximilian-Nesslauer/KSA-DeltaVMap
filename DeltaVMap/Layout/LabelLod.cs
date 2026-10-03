using System.Collections.Generic;

namespace DeltaVMap.Layout;

// The zoom level of detail and the draw priority of the node labels, shared by the canvas and
// the offline fit preview so both cull the same labels. Pure rules on LayoutNode, no ImGui.
internal static class LabelLod
{
    // Below this zoom minor-body (rank 3) labels are dropped: at a zoomed-out overview their
    // names are noise, and the major bodies plus any selected route read better without them.
    public const double MinorLabelMinZoom = 0.5;

    // Below this zoom labels drop their rung suffix and collapse to one short body name per
    // body ("Saturn" instead of three "Saturn <rung>" labels), because the glyph already
    // carries the rung. It is its own constant so the two transitions can be tuned apart.
    public const double FullLabelMinZoom = 0.5;

    // Below this zoom moon-level (rank 2) labels are dropped entirely, leaving the far-out
    // overview to planets, the route and the "+N" group headlines. Moons in the root's
    // immediate context are exempt (Luna stays named on an Earth-rooted overview), as is
    // the minor-body group label itself.
    public const double MoonLabelMinZoom = 0.3;

    // The zoom floors by cosmetic rank: minor-body (rank 3) names go first, then moon-level
    // (rank 2) names, leaving the far-out overview to planets. A minor-body group is exempt
    // (its "+N" count is the headline of a dense overview), as are moons in the root's
    // immediate context.
    public static bool RankShows(LayoutNode node, double zoom)
    {
        if (node.Rank >= 3)
            return zoom >= MinorLabelMinZoom;
        if (node.Rank == 2 && node.Kind != LayoutKind.MinorGroup && !IsNearRootContext(node))
            return zoom >= MoonLabelMinZoom;
        return true;
    }

    // Whether a node's body is immediate context of the root: its well hangs off the root
    // itself or off the root's nearest hub (a spine node at depth <= 1). That covers the
    // root's own moons (Luna on an Earth root) and, when rooted on a moon, its sibling
    // moons; a distant planet's moons attach much deeper down the spine and fail the test.
    public static bool IsNearRootContext(LayoutNode node)
    {
        string body = BodyKey(node);
        for (LayoutNode? p = node.Parent; p != null; p = p.Parent)
        {
            if (BodyKey(p) != body)
                return p.Depth <= 1;
        }
        return true;
    }

    public static string BodyKey(LayoutNode node)
    {
        return node.BodyId.Length > 0 ? node.BodyId : node.Id;
    }

    // Which rung carries a body's single short label at overview zoom. Low orbit is the
    // canonical "go here" rung; the rest order by how strongly they read as the body itself.
    public static int RungPreference(LayoutKind kind)
    {
        return kind switch
        {
            LayoutKind.LowOrbit => 0,
            LayoutKind.Hub => 1,
            LayoutKind.Surface => 2,
            LayoutKind.Intercept => 3,
            LayoutKind.Stationary => 4,
            LayoutKind.SoiEdge => 5,
            _ => 6
        };
    }

    // A special label always draws in full, because it is one of the user's current anchors.
    public static bool IsSpecial(LayoutNode node, string? hoverId, bool routing, IReadOnlySet<string>? routeNodes)
    {
        return node.IsRoot || node.IsYouAreHere || node.IsSystemRoot || node.Id == hoverId
            || (routing && routeNodes!.Contains(node.Id));
    }

    // Lower number = drawn first = wins the screen room. Names come before plain dV badges
    // so the map reads as a labelled diagram first: hovered, root, you-are-here, on-route
    // name; the selected route's dV; then every body name by rank; then dV badges by rank.
    // Only the on-route dV outranks the names, since a chosen route wants both at once.
    public static int Priority(LayoutNode node, string? hoverId, bool routing, IReadOnlySet<string>? routeNodes)
    {
        if (node.Id == hoverId)
            return 0;
        if (node.IsRoot || node.IsSystemRoot)
            return 1;
        if (node.IsYouAreHere)
            return 2;
        if (routing && routeNodes!.Contains(node.Id))
            return 3;
        // A minor-body group's "+N" count is the headline of a dense overview, so it ranks
        // just under the route, ahead of every individual body name.
        if (node.Kind == LayoutKind.MinorGroup)
            return 4;
        if (IsMajor(node))
            return 5;
        if (node.Rank == 2)
            return 6;
        return 7;
    }

    public static bool IsMajor(LayoutNode node)
    {
        return node.IsRoot || node.IsYouAreHere || node.Kind == LayoutKind.Hub || node.Rank <= 1;
    }
}
