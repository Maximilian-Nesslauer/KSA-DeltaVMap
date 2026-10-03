using System;
using System.Collections.Generic;
using Brutal.Numerics;
using DeltaVMap.Dv;
using KSA;

namespace DeltaVMap.Model;

// One body in the physical celestial tree, mirroring the game hierarchy: a system root (a star or
// a barycenter), the stars and planets under it, moons under those. Children are sorted once, so
// every consumer (re-rooter, visual tree, dump) sees the same deterministic order. The BodyLadder
// is built once here and reused, so ladders are not recomputed per re-root.
internal sealed class PhysicalNode
{
    public required IParentBody Body { get; init; }
    public required Astronomical Astro { get; init; }
    public required BodyLadder Ladder { get; init; }
    public required BodyRole Role { get; init; }

    // A minor body in the map's sense: hidden by the minor-body toggles and folded into "+N"
    // groups. Every MinorBody is minor, except the MinorBody children of a star or barycenter
    // that has no other planets (ForestRules.MinorChildrenArePlanets), so a system's planets do
    // not vanish with the asteroids.
    public required bool IsMinor { get; init; }

    public PhysicalNode? Parent { get; set; }
    public List<PhysicalNode> Children { get; } = new();

    public string Id => Astro.Id;
    public bool IsStar => Role == BodyRole.Star;
    public bool IsHubOnly => Role != BodyRole.Body;
    public bool IsSystemRoot => Parent == null;

    // A moon in the map's sense: a body that orbits another body. Celestial.IsMoon() is true for
    // anything whose parent is no StellarBody, so it would also call a planet that circles a
    // barycenter a moon.
    public bool IsMoon => Role == BodyRole.Body && Parent != null && !Parent.IsHubOnly;
}

// The physical forest built once from a loaded CelestialSystem: one tree per independent root
// (IIndependentRoot, a fixed star or a barycenter), because one CelestialSystem can hold several
// star systems that space splits between them. It is the immutable backbone the visual tree is
// re-rooted against. The captured system reference lets the caller detect a system change (load
// or scene swap) and rebuild, since Keplerian orbits are otherwise fixed for the lifetime of a
// loaded system. Vehicles are intentionally excluded: they are transient and handled separately
// as the "you are here" marker.
internal sealed class SystemGraph
{
    public CelestialSystem System { get; }
    public string SystemId => System.Id;

    // Every system root, the home system first, then the others nearest first.
    public IReadOnlyList<PhysicalNode> Roots { get; }

    // The root of the system that holds the home body.
    public PhysicalNode HomeRoot => Roots[0];

    private readonly Dictionary<string, PhysicalNode> _byId;

    private SystemGraph(CelestialSystem system, List<PhysicalNode> roots, Dictionary<string, PhysicalNode> byId)
    {
        System = system;
        Roots = roots;
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

    public bool HasSeveralSystems => Roots.Count > 1;

    // How many other star systems the map draws as their own destination before the rest fold
    // into one "+N star systems" group. Stock has three; a modded universe can have many more.
    public const int MaxShownSystems = 8;

    private readonly Dictionary<PhysicalNode, List<PhysicalNode>> _destinations = new();

    // Every system root except sourceRoot, nearest to it first with the Id as a stable
    // tie-breaker. Roots never move, so the order is cached per source for the loaded system.
    public IReadOnlyList<PhysicalNode> DestinationsFrom(PhysicalNode sourceRoot)
    {
        if (_destinations.TryGetValue(sourceRoot, out List<PhysicalNode>? cached))
            return cached;
        var list = new List<PhysicalNode>(Roots.Count);
        foreach (PhysicalNode root in Roots)
        {
            if (!ReferenceEquals(root, sourceRoot))
                list.Add(root);
        }
        list.Sort((a, b) =>
        {
            int byDistance = Distance(a, sourceRoot).CompareTo(Distance(b, sourceRoot));
            return byDistance != 0 ? byDistance : string.CompareOrdinal(a.Id, b.Id);
        });
        _destinations[sourceRoot] = list;
        return list;
    }

    // The root of the system a node belongs to, the same answer as IIndependentRoot.RootOf.
    public static PhysicalNode SystemRootOf(PhysicalNode node)
    {
        PhysicalNode current = node;
        while (current.Parent != null)
            current = current.Parent;
        return current;
    }

    // Straight-line distance between two bodies now, in meters. Roots never move (a fixed star
    // or a barycenter returns a constant position), so the distance between two systems is
    // fixed for the loaded system.
    public static double Distance(PhysicalNode a, PhysicalNode b)
    {
        return (a.Astro.GetPositionEcl() - b.Astro.GetPositionEcl()).Length();
    }

    // Build one tree per independent root. Roots come from this system's own body list (the
    // same enumeration Universe.LoadSystem uses for Universe.Roots), so a CelestialSystem that
    // is not the current one builds correctly too. Returns null for a system with no root,
    // which cannot happen for a loaded system: every top-level body is a fixed star or a
    // barycenter.
    public static SystemGraph? Build(CelestialSystem system)
    {
        var byId = new Dictionary<string, PhysicalNode>();
        var roots = new List<PhysicalNode>();
        foreach (IIndependentRoot root in system.All.OfType<IIndependentRoot>())
        {
            if (root is not Astronomical astro)
                continue;
            roots.Add(BuildNode(root, astro, parent: null, isMinor: false, byId));
        }
        if (roots.Count == 0)
            return null;

        PhysicalNode home = HomeRootOf(system, byId) ?? roots[0];
        double3 homePos = home.Astro.GetPositionEcl();
        roots.Sort((a, b) => ForestRules.CompareRoots(
            ReferenceEquals(a, home), (a.Astro.GetPositionEcl() - homePos).LengthSquared(), a.Id,
            ReferenceEquals(b, home), (b.Astro.GetPositionEcl() - homePos).LengthSquared(), b.Id));

        return new SystemGraph(system, roots, byId);
    }

    private static PhysicalNode? HomeRootOf(CelestialSystem system, Dictionary<string, PhysicalNode> byId)
    {
        IParentBody? home = system.HomeBody;
        if (home == null)
            return null;
        IIndependentRoot? root = IIndependentRoot.RootOf(home);
        return root != null && byId.TryGetValue(root.Id, out PhysicalNode? node) ? node : null;
    }

    private static BodyRole RoleOf(Astronomical astro)
    {
        return ForestRules.RoleOf(astro is StellarBody, astro is IIndependentRoot);
    }

    // True for a star or a barycenter, the bodies the map treats as hub-only. Use this rather
    // than Astronomical.IsStar(), which is false for a barycenter.
    public static bool IsHubOnlyBody(Astronomical astro)
    {
        return RoleOf(astro) != BodyRole.Body;
    }

    private static PhysicalNode BuildNode(IParentBody body, Astronomical astro, PhysicalNode? parent, bool isMinor, Dictionary<string, PhysicalNode> byId)
    {
        BodyRole role = RoleOf(astro);
        var node = new PhysicalNode
        {
            Body = body,
            Astro = astro,
            Ladder = role == BodyRole.Body ? OrbitalStates.BuildLadder(body) : OrbitalStates.BuildHubLadder(body),
            Role = role,
            IsMinor = isMinor,
            Parent = parent
        };
        byId[astro.Id] = node;

        // Every orbiting celestial extends the tree: planets, moons, comets (Celestial) and the
        // stars of a multiple system (OrbitingStar). Vehicles also live in Children, but they are
        // not an IParentBody, not destinations, and are skipped.
        var childBodies = new List<IOrbiter>();
        bool hasMajorPlanet = false;
        foreach (IOrbiter child in body.Children)
        {
            if (child is not IParentBody || child is not Astronomical)
                continue;
            childBodies.Add(child);
            if (child is Celestial and not MinorBody)
                hasMajorPlanet = true;
        }
        bool minorChildrenArePlanets = ForestRules.MinorChildrenArePlanets(role, hasMajorPlanet);

        // Deterministic order, innermost first, with Id as a stable tie-breaker so
        // co-orbital bodies never reshuffle between rebuilds. Bound bodies sort by
        // semi-major axis; open (hyperbolic / parabolic, e >= 1) orbits have a negative
        // or undefined SMA, so they would otherwise sort to the very front. They are an
        // odd, expensive special case (interstellar comets), so push them past every
        // bound body and order them among themselves by periapsis (closest approach).
        childBodies.Sort(static (a, b) =>
        {
            Orbit oa = a.Orbit;
            Orbit ob = b.Orbit;
            bool aOpen = oa.Eccentricity >= 1.0;
            bool bOpen = ob.Eccentricity >= 1.0;
            if (aOpen != bOpen)
                return aOpen ? 1 : -1;
            double ka = aOpen ? oa.Periapsis : oa.SemiMajorAxis;
            double kb = bOpen ? ob.Periapsis : ob.SemiMajorAxis;
            int byKey = ka.CompareTo(kb);
            return byKey != 0 ? byKey : string.CompareOrdinal(a.Id, b.Id);
        });

        foreach (IOrbiter child in childBodies)
        {
            var childAstro = (Astronomical)child;
            bool childMinor = ForestRules.IsMinor(childAstro is MinorBody, minorChildrenArePlanets);
            node.Children.Add(BuildNode((IParentBody)child, childAstro, node, childMinor, byId));
        }

        return node;
    }
}
