using System.Collections.Generic;

namespace DeltaVMap.Model;

// One ancestor hub on the spine from the ego root up to the star. The hub body is
// drawn as a horizontal bus, never a point node, which is what eliminates the
// starburst. SpineChild is the body we ascended through (it is reached from below,
// so it is not branched again); OtherChildren are its siblings, each of which
// branches off the bus as its own destination lane. The hub's own ladder is
// attached separately by the visual tree.
internal sealed class HubLevel
{
    public required PhysicalNode Hub { get; init; }
    public required PhysicalNode SpineChild { get; init; }
    public required IReadOnlyList<PhysicalNode> OtherChildren { get; init; }
}

// The structural result of re-rooting at a body: the ego root plus the ordered
// chain of ancestor hubs leading up to the star (nearest hub first, star last).
internal sealed class ReRootResult
{
    public required PhysicalNode Root { get; init; }
    public required IReadOnlyList<HubLevel> Spine { get; init; }
}

// Re-roots the physical tree at any body by walking its parent chain. Each ancestor
// becomes a hub bus with the siblings of the body we came up through hanging off
// it. Re-rooting at a moon therefore climbs moon -> planet hub -> star hub, exactly
// the spine the visual tree then materializes. Surface-only bodies (no orbit
// ladder) re-root the same way; they simply contribute no rungs of their own.
internal static class ReRooter
{
    public static ReRootResult ReRoot(PhysicalNode root)
    {
        var spine = new List<HubLevel>();

        PhysicalNode spineChild = root;
        PhysicalNode? hub = root.Parent;
        while (hub != null)
        {
            // Children are already sorted deterministically in the SystemGraph, so
            // filtering the spine child out preserves that order for the siblings.
            var others = new List<PhysicalNode>(hub.Children.Count);
            foreach (PhysicalNode child in hub.Children)
            {
                if (!ReferenceEquals(child, spineChild))
                    others.Add(child);
            }

            spine.Add(new HubLevel
            {
                Hub = hub,
                SpineChild = spineChild,
                OtherChildren = others
            });

            spineChild = hub;
            hub = hub.Parent;
        }

        return new ReRootResult { Root = root, Spine = spine };
    }
}
