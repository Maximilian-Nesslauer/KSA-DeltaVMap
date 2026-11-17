using DeltaVMap.Dv;
using DeltaVMap.Model;

namespace DeltaVMap.Mission;

// Finds the node of the visual tree that a mission goal selects. A body does not always have the
// node the goal asks for: a body too small to hold an orbit has an intercept and a surface but no
// low orbit, a body without a surface has no landing, and a flyby target has an intercept only at
// full detail. The resolver then takes the nearest node the map has and says which end the route
// to it has, so the route still counts as the mission's.
internal static class MissionRouteResolver
{
    // The node a goal selects and the end the route to it has, or null when the map has no node
    // of the body to route to. The ego system's root star has no low orbit, so a goal there has
    // no route, as a click on that hub has none.
    internal static (StateNode Node, RouteEnd EndOnMap)? Resolve(VisualTree tree, in MissionRouteGoal goal)
    {
        string id = goal.BodyId;
        if (string.IsNullOrEmpty(id))
            return null;

        StateNode? node;
        switch (goal.End)
        {
            case RouteEnd.Surface:
                node = tree.FindBodyNode(id, StateKind.Surface)
                    ?? tree.FindBodyNode(id, StateKind.LowOrbit)
                    ?? tree.FindBodyNode(id, StateKind.Intercept);
                break;
            case RouteEnd.Flyby:
                node = tree.FindBodyNode(id, StateKind.Intercept)
                    ?? tree.FindBodyNode(id, StateKind.LowOrbit);
                break;
            default:
                node = tree.FindBodyNode(id, StateKind.LowOrbit)
                    ?? tree.FindBodyNode(id, StateKind.Intercept);
                break;
        }
        return node != null ? (node, EndOf(node.Kind)) : null;
    }

    // The end of a route whose target node has this kind.
    internal static RouteEnd EndOf(StateKind kind)
    {
        return kind switch
        {
            StateKind.Surface => RouteEnd.Surface,
            StateKind.Intercept => RouteEnd.Flyby,
            _ => RouteEnd.Orbit,
        };
    }
}
