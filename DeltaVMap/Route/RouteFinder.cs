using System.Collections.Generic;
using DeltaVMap.Model;

namespace DeltaVMap.Route;

// One edge on a route, plus the direction it is traversed. Forward means From -> To
// (away from the tree root, the natural edge direction); a backward step climbs from
// To up to From toward the root. Ladder dV is symmetric so direction does not change
// its cost, and transfers are only ever traversed forward on a real route (the origin
// always sits on the root body's ladder), but the flag keeps the labels right and lets
// the return trip reuse the same steps reversed.
internal readonly struct RouteStep
{
    public readonly Edge Edge;
    public readonly bool Forward;

    public RouteStep(Edge edge, bool forward)
    {
        Edge = edge;
        Forward = forward;
    }
}

// A resolved route: the ordered node chain from origin to target and the edge steps
// between them. Nodes has exactly Steps.Count + 1 entries.
internal sealed class RoutePath
{
    public required StateNode Origin { get; init; }
    public required StateNode Target { get; init; }
    public required IReadOnlyList<RouteStep> Steps { get; init; }
    public required IReadOnlyList<StateNode> Nodes { get; init; }
}

// Finds the unique path between two nodes of the re-rooted tree. Because it is a tree,
// the path is origin -> lowest common ancestor -> target: walk up from each to the LCA
// and stitch the two halves. The origin is the route origin ("you are here", or the
// root surface when from-surface is on); the target is the clicked destination.
internal static class RouteFinder
{
    public static RoutePath? FindPath(StateNode origin, StateNode target)
    {
        if (ReferenceEquals(origin, target))
            return new RoutePath
            {
                Origin = origin,
                Target = target,
                Steps = System.Array.Empty<RouteStep>(),
                Nodes = new[] { origin }
            };

        // Index the origin's ancestor chain (origin first, root last) so the target's
        // upward walk can stop at the first shared node, the LCA.
        var originChain = new List<StateNode>();
        var originIndex = new Dictionary<StateNode, int>();
        for (StateNode? n = origin; n != null; n = n.Parent)
        {
            originIndex[n] = originChain.Count;
            originChain.Add(n);
        }

        var targetChain = new List<StateNode>();
        StateNode? cursor = target;
        while (cursor != null && !originIndex.ContainsKey(cursor))
        {
            targetChain.Add(cursor);
            cursor = cursor.Parent;
        }

        // No shared ancestor means the two nodes are in different trees, which cannot
        // happen for one re-rooted system; guard rather than crash.
        if (cursor == null)
            return null;

        StateNode lca = cursor;
        int lcaIndex = originIndex[lca];

        var steps = new List<RouteStep>(lcaIndex + targetChain.Count);
        var nodes = new List<StateNode>(lcaIndex + targetChain.Count + 1);

        // Up from the origin to the LCA: each node's ParentEdge connects its parent
        // (From) to it (To), so we traverse it backward (To -> From).
        for (int i = 0; i < lcaIndex; i++)
        {
            nodes.Add(originChain[i]);
            steps.Add(new RouteStep(originChain[i].ParentEdge!, forward: false));
        }
        nodes.Add(lca);

        // Down from the LCA to the target: walk the target chain in reverse (child of
        // the LCA first), traversing each node's ParentEdge forward (From -> To).
        for (int i = targetChain.Count - 1; i >= 0; i--)
        {
            steps.Add(new RouteStep(targetChain[i].ParentEdge!, forward: true));
            nodes.Add(targetChain[i]);
        }

        return new RoutePath
        {
            Origin = origin,
            Target = target,
            Steps = steps,
            Nodes = nodes
        };
    }
}
