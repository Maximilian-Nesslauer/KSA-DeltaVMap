using System.Collections.Generic;
using DeltaVMap.Dv;
using KSA;

namespace DeltaVMap.Model;

// One body in the physical celestial tree, mirroring the game hierarchy: the star
// at the root, planets under it, moons under those. Children are sorted once, so
// every consumer (re-rooter, visual tree, dump) sees the same deterministic order.
// The BodyLadder is built once here and reused, so ladders are not recomputed per
// re-root.
internal sealed class PhysicalNode
{
    public required IParentBody Body { get; init; }
    public required Astronomical Astro { get; init; }
    public required BodyLadder Ladder { get; init; }
    public required bool IsStar { get; init; }
    public PhysicalNode? Parent { get; set; }
    public List<PhysicalNode> Children { get; } = new();

    public string Id => Astro.Id;
}

// The physical tree built once from a loaded CelestialSystem. It is the immutable
// backbone the visual tree is re-rooted against. The captured SystemId lets the
// caller detect a system change (load / scene swap) and rebuild, since Keplerian
// orbits are otherwise fixed for the lifetime of a loaded system. Vehicles are
// intentionally excluded: they are transient and handled separately as the "you
// are here" marker.
internal sealed class SystemGraph
{
    public string SystemId { get; }
    public PhysicalNode Root { get; }

    private readonly Dictionary<string, PhysicalNode> _byId;

    private SystemGraph(string systemId, PhysicalNode root, Dictionary<string, PhysicalNode> byId)
    {
        SystemId = systemId;
        Root = root;
        _byId = byId;
    }

    public PhysicalNode? Find(string id)
    {
        return _byId.TryGetValue(id, out PhysicalNode? node) ? node : null;
    }

    // The cached ladder (mu, r_lo, r_soi, CanHoldOrbit) for a body Id, or null if the
    // body is not in this system. Routing and the badge math derive Oberth burns from
    // it, so both read radii from the one ladder the graph built per body.
    public BodyLadder? LadderFor(string id)
    {
        return Find(id)?.Ladder;
    }

    public IReadOnlyCollection<PhysicalNode> AllNodes => _byId.Values;

    // Build the physical tree from the star down. Returns null if the system has no
    // identifiable star to root at (no StellarBody and no HomeBody); callers treat
    // that as "cannot build a map for this system".
    public static SystemGraph? Build(CelestialSystem system)
    {
        IParentBody? rootBody = system.GetWorldSun();
        rootBody ??= system.HomeBody;
        if (rootBody is not Astronomical rootAstro)
            return null;

        var byId = new Dictionary<string, PhysicalNode>();
        PhysicalNode root = BuildNode(rootBody, rootAstro, parent: null, isStar: rootBody is StellarBody, byId);
        return new SystemGraph(system.Id, root, byId);
    }

    private static PhysicalNode BuildNode(IParentBody body, Astronomical astro, PhysicalNode? parent, bool isStar, Dictionary<string, PhysicalNode> byId)
    {
        var node = new PhysicalNode
        {
            Body = body,
            Astro = astro,
            Ladder = OrbitalStates.BuildLadder(body),
            IsStar = isStar,
            Parent = parent
        };
        byId[astro.Id] = node;

        // Only Celestials (planets, moons, comets) extend the tree. Vehicles also
        // live in Children, but they are not destinations and are skipped.
        var childBodies = new List<Celestial>();
        foreach (IOrbiter child in body.Children)
        {
            if (child is Celestial celestial)
                childBodies.Add(celestial);
        }

        // Deterministic order by semi-major axis, innermost first, with Id as a
        // stable tie-breaker so co-orbital bodies never reshuffle between rebuilds.
        childBodies.Sort(static (a, b) =>
        {
            int bySma = a.SemiMajorAxis.CompareTo(b.SemiMajorAxis);
            return bySma != 0 ? bySma : string.CompareOrdinal(a.Id, b.Id);
        });

        foreach (Celestial child in childBodies)
            node.Children.Add(BuildNode(child, child, node, isStar: false, byId));

        return node;
    }
}
