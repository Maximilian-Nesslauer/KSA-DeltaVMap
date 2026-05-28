using System.Collections.Generic;
using DeltaVMap.Dv;

namespace DeltaVMap.Model;

// How much of a body's ladder to materialize. Full shows every rung; Core keeps
// only the rungs a distant body needs as a destination (its landing surface and
// low orbit), dropping the stationary and SOI-edge rungs that only matter when you
// are operating there.
internal enum NodeDetail
{
    Full,
    Core
}

// Context-dependent detail. Bodies near the root render their full ladder; distant
// bodies collapse to core rungs. The set of "near" bodies is the
// root, its moons, its siblings on the nearest hub, and the spine ancestors
// themselves. A "full ladder everywhere" override promotes every body to Full.
internal sealed class DetailLevel
{
    private readonly HashSet<string> _fullBodies;
    private readonly bool _fullEverywhere;

    public DetailLevel(ReRootResult reroot, bool fullLadderEverywhere)
    {
        _fullEverywhere = fullLadderEverywhere;
        _fullBodies = new HashSet<string>();

        // The ego root and its own children (moons) are immediate context.
        _fullBodies.Add(reroot.Root.Id);
        foreach (PhysicalNode child in reroot.Root.Children)
            _fullBodies.Add(child.Id);

        // Every spine ancestor is a hub the map is built around, and the siblings
        // on the nearest hub are the root's peers, so both stay full.
        for (int i = 0; i < reroot.Spine.Count; i++)
        {
            HubLevel level = reroot.Spine[i];
            _fullBodies.Add(level.Hub.Id);
            if (i == 0)
            {
                foreach (PhysicalNode sibling in level.OtherChildren)
                    _fullBodies.Add(sibling.Id);
            }
        }
    }

    public NodeDetail For(string bodyId)
    {
        if (_fullEverywhere || _fullBodies.Contains(bodyId))
            return NodeDetail.Full;
        return NodeDetail.Core;
    }

    // Whether a given ladder rung is shown at a detail level. Core keeps the
    // surface and low orbit (the destinations that define "land here" / "park
    // here") and drops the stationary and SOI-edge rungs.
    public static bool IncludeRung(StateKind kind, NodeDetail detail)
    {
        if (detail == NodeDetail.Full)
            return true;
        return kind == StateKind.Surface || kind == StateKind.LowOrbit;
    }
}
