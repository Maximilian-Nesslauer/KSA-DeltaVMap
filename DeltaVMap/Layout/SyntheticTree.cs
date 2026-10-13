using System;
using System.Collections.Generic;
using System.Globalization;
using DeltaVMap.Model;

namespace DeltaVMap.Layout;

// Deterministic synthetic layout trees for testing the engine offline, with no game
// dependency. They are not physically accurate; the dV values are plausible stand-ins
// chosen to exercise the band logic. Two shapes matter for the overlap tests:
//
//  - A large moon-rooted tree whose spine climbs root -> planet hub -> star hub, so
//    all three hubs share the root band and the hub bus must be laid out horizontally
//    This is the 100+ node stress tree.
//  - A planet-rooted tree resembling the stock Sol map, for eyeballing realism.
//
// Both are built purely from index arithmetic, so the output never varies between
// runs.
internal static class SyntheticTree
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // A moon-rooted system with a three-hub spine and a deliberately wide, deep set of
    // spokes, sized comfortably above 100 nodes. The root moon, its planet hub and the
    // star hub all land on one band; everything else hangs below.
    public static LayoutTree BuildLargeMoonRoot()
    {
        LayoutNode root = Node("Root.LO", "Kerbin Low Orbit", LayoutKind.LowOrbit, rank: 2);
        root.IsRoot = true;
        Ladder(root, "Root", surfaceDv: 1870, stationaryDv: null, soiDv: 410);

        // A you-are-here marker hanging just off the root low orbit (medium orbit).
        LayoutNode here = Node("Root.YOU", "You Are Here", LayoutKind.YouAreHere, rank: 2);
        here.IsYouAreHere = true;
        Ladder(root, here, raiseDv: 120);

        // First hub: the planet the root moon orbits.
        LayoutNode planetHub = Node("Planet.Hub", "Planet", LayoutKind.Hub, rank: 1);
        HubLink(root, planetHub);

        // The hub's own ladder, reached by dropping into the planet's low orbit.
        LayoutNode planetLo = Node("Planet.LO", "Planet Low Orbit", LayoutKind.LowOrbit, rank: 1);
        Transfer(planetHub, planetLo, depart: 3100, arrive: 880);
        Ladder(planetLo, "Planet", surfaceDv: 9400, stationaryDv: 1490, soiDv: 3210);

        // Sibling moons of the root, each its own destination lane off the planet hub.
        for (int i = 0; i < 6; i++)
        {
            string id = "Moon" + i.ToString(Inv);
            LayoutNode moonLo = Node(id + ".LO", MoonName(i) + " Low Orbit", LayoutKind.LowOrbit, rank: 2);
            Transfer(planetHub, moonLo, depart: 900 + i * 130, arrive: 200 + i * 55);
            Ladder(moonLo, id, surfaceDv: 600 + i * 90, stationaryDv: null, soiDv: 90 + i * 10);

            // Some moons carry a small sub-moon to add depth.
            if (i % 2 == 0)
            {
                LayoutNode subLo = Node(id + ".Sub.LO", MoonName(i) + " B Orbit", LayoutKind.LowOrbit, rank: 3);
                Transfer(moonLo, subLo, depart: 240, arrive: 70);
                Ladder(subLo, id + ".Sub", surfaceDv: 180, stationaryDv: null, soiDv: null);
            }
        }

        // Second hub: the star. Sibling planets branch off it.
        LayoutNode starHub = Node("Star.Hub", "Star", LayoutKind.Hub, rank: 0);
        HubLink(planetHub, starHub);
        AttachPlanets(starHub, count: 12);

        return LayoutTree.FromRoot("synthetic-moon-root", root);
    }

    // A planet-rooted system resembling the stock map: the root planet with its moons,
    // then a star hub carrying the other planets. Modest size, for eyeballing.
    public static LayoutTree BuildStockLikePlanetRoot()
    {
        LayoutNode root = Node("Earth.LO", "Earth Low Orbit", LayoutKind.LowOrbit, rank: 1);
        root.IsRoot = true;
        Ladder(root, "Earth", surfaceDv: 9400, stationaryDv: 1490, soiDv: 3210);

        LayoutNode lunaLo = Node("Luna.LO", "Luna Low Orbit", LayoutKind.LowOrbit, rank: 2);
        Transfer(root, lunaLo, depart: 3100, arrive: 850);
        Ladder(lunaLo, "Luna", surfaceDv: 1870, stationaryDv: null, soiDv: 410);

        LayoutNode starHub = Node("Sol.Hub", "Sol", LayoutKind.Hub, rank: 0);
        HubLink(root, starHub);
        AttachPlanets(starHub, count: 7);

        return LayoutTree.FromRoot("synthetic-planet-root", root);
    }

    // The interplanetary-cruise root: the star is the root and the planets hang off
    // it through zero-dV HubLink edges (matching VisualTree's star-root branch), so
    // the whole star-plus-planets row shares one band and the bus is star-shaped
    // rather than a chain. Exercises the multi-child hub-bus path.
    public static LayoutTree BuildCruiseRoot()
    {
        LayoutNode starHub = Node("Star.Hub", "Star", LayoutKind.Hub, rank: 0);
        starHub.IsRoot = true;

        LayoutNode here = Node("Star.YOU", "You Are Here", LayoutKind.YouAreHere, rank: 0);
        here.IsYouAreHere = true;
        HubLink(starHub, here);

        for (int i = 0; i < 9; i++)
        {
            string id = "Planet" + i.ToString(Inv);
            string name = PlanetName(i);
            LayoutNode lo = Node(id + ".LO", name + " Low Orbit", LayoutKind.LowOrbit, rank: 1);
            HubLink(starHub, lo);

            Ladder(lo, id, surfaceDv: 3000 + i * 500, stationaryDv: i % 2 == 0 ? 800 + i * 100 : (double?)null, soiDv: 700 + i * 80);
            int moons = i % 3;
            for (int m = 0; m < moons; m++)
                AttachMoon(lo, id, name, m);
        }

        return LayoutTree.FromRoot("synthetic-cruise-root", starHub);
    }

    // The interplanetary-cruise root as the real VisualTree builds it: planets attach to
    // the star hub by a HubLink that lands on the planet's arrival Intercept (full detail),
    // not directly on its low orbit. This is the shape the in-game cruise root produces, so
    // it exercises what BuildCruiseRoot's simplified LowOrbit-HubLink does not: in
    // GravityWell the Intercepts ride above the spine while the low orbits anchor it, which
    // the well-band and (relaxed) hub-bus checks must accept.
    public static LayoutTree BuildCruiseRootArrival()
    {
        LayoutNode starHub = Node("Star.Hub", "Star", LayoutKind.Hub, rank: 0);
        starHub.IsRoot = true;

        LayoutNode here = Node("Star.YOU", "You Are Here", LayoutKind.YouAreHere, rank: 0);
        here.IsYouAreHere = true;
        HubLink(starHub, here);

        for (int i = 0; i < 8; i++)
        {
            string id = "Planet" + i.ToString(Inv);
            string name = PlanetName(i);

            // HubLink lands on the capture anchor; circularize down to the spine low orbit.
            LayoutNode capture = Node(id + ".Capture", name + " Intercept", LayoutKind.Intercept, rank: 1);
            HubLink(starHub, capture);
            LayoutNode lo = Node(id + ".LO", name + " Low Orbit", LayoutKind.LowOrbit, rank: 1);
            LadderEdge(capture, lo, 800 + i * 70);
            LadderEdge(lo, Node(id + ".Surface", name + " Surface", LayoutKind.Surface, rank: 1), 3000 + i * 500);
            if (i % 2 == 0)
                LadderEdge(lo, Node(id + ".Stationary", name + " Stationary", LayoutKind.Stationary, rank: 1), 700 + i * 90);

            int moons = i % 3;
            for (int m = 0; m < moons; m++)
                Arrive(lo, id + ".M" + m.ToString(Inv), name + " " + MoonSuffix(m), rank: 2, depart: 600 + m * 200, arrive: 180 + m * 60, circularizeDv: 90 + m * 20, surfaceDv: 300 + m * 120, stationaryDv: null);
        }

        return LayoutTree.FromRoot("synthetic-cruise-arrival", starHub);
    }

    // The root of a multiple star system: a barycenter hub with no rungs of its own, its stars
    // linked to it like the planets of a cruise root (onto their capture anchor, then down to a
    // wide parking orbit), and the planets of one star reached by transfers from that star's
    // parking orbit. This is what the map builds when a vessel orbits the barycenter itself.
    public static LayoutTree BuildBarycenterRoot()
    {
        LayoutNode bary = Node("Bary.Hub", "Bary", LayoutKind.Hub, rank: 0);
        bary.IsRoot = true;

        LayoutNode here = Node("Bary.YOU", "You Are Here", LayoutKind.YouAreHere, rank: 0);
        here.IsYouAreHere = true;
        HubLink(bary, here);

        for (int i = 0; i < 3; i++)
        {
            string id = "Star" + i.ToString(Inv);
            LayoutNode capture = Node(id + ".Capture", id + " Intercept", LayoutKind.Intercept, rank: 0);
            HubLink(bary, capture);
            LayoutNode lo = Node(id + ".LO", id + " Low Orbit", LayoutKind.LowOrbit, rank: 0);
            LadderEdge(capture, lo, 9000 + i * 1500);
            if (i == 2)
            {
                Arrive(lo, id + ".b", id + " b", rank: 1, depart: 21000, arrive: 64000, circularizeDv: 900, surfaceDv: 4000, stationaryDv: null);
                Arrive(lo, id + ".d", id + " d", rank: 1, depart: 23000, arrive: 70000, circularizeDv: 700, surfaceDv: 2500, stationaryDv: null);
            }
        }

        return LayoutTree.FromRoot("synthetic-barycenter-root", bary);
    }

    // A vessel orbiting one star of a multiple system directly: that star's hub is the root with
    // its planets linked to it, and the spine climbs to the barycenter hub, from which the
    // sibling stars branch off by transfers.
    public static LayoutTree BuildOrbitingStarRoot()
    {
        LayoutNode star = Node("Star.Hub", "Star", LayoutKind.Hub, rank: 0);
        star.IsRoot = true;

        LayoutNode here = Node("Star.YOU", "You Are Here", LayoutKind.YouAreHere, rank: 0);
        here.IsYouAreHere = true;
        HubLink(star, here);

        for (int i = 0; i < 2; i++)
        {
            string id = "Exo" + i.ToString(Inv);
            LayoutNode capture = Node(id + ".Capture", id + " Intercept", LayoutKind.Intercept, rank: 1);
            HubLink(star, capture);
            LayoutNode lo = Node(id + ".LO", id + " Low Orbit", LayoutKind.LowOrbit, rank: 1);
            LadderEdge(capture, lo, 1200 + i * 300);
            LadderEdge(lo, Node(id + ".Surface", id + " Surface", LayoutKind.Surface, rank: 1), 5000 + i * 900);
        }

        LayoutNode bary = Node("Bary.Hub", "Bary", LayoutKind.Hub, rank: 0);
        HubLink(star, bary);
        for (int i = 0; i < 2; i++)
        {
            string id = "Sibling" + i.ToString(Inv);
            LayoutNode capture = Node(id + ".Capture", id + " Intercept", LayoutKind.Intercept, rank: 0);
            Transfer(bary, capture, depart: 4000 + i * 900, arrive: 9000 + i * 700, approximate: true);
            LadderEdge(capture, Node(id + ".LO", id + " Low Orbit", LayoutKind.LowOrbit, rank: 0), 12000 + i * 1000);
        }

        return LayoutTree.FromRoot("synthetic-orbiting-star-root", star);
    }

    // A dense-system root after minor-body aggregation: a planet root with a star hub
    // carrying the other planets plus a single huge "+N asteroids" group standing in for the
    // collapsed belt, and one moon that has collapsed minor bodies of its own. The in-game
    // collapse decision is game-typed and verified live; this exercises its LAYOUT output -
    // that a MinorGroup node and its dV-free spoke lay out overlap-free in every mode, both
    // off a hub bus and off a deeper local hub.
    public static LayoutTree BuildDenseAggregated()
    {
        LayoutNode root = Node("Earth.LO", "Earth Low Orbit", LayoutKind.LowOrbit, rank: 1);
        root.IsRoot = true;
        Ladder(root, "Earth", surfaceDv: 9400, stationaryDv: 1490, soiDv: 3210);

        LayoutNode lunaLo = Node("Luna.LO", "Luna Low Orbit", LayoutKind.LowOrbit, rank: 2);
        Transfer(root, lunaLo, depart: 3100, arrive: 850);
        Ladder(lunaLo, "Luna", surfaceDv: 1870, stationaryDv: null, soiDv: 410);
        // Luna's own collapsed minor bodies hang off it as a local hub.
        MinorGroup(lunaLo, "Luna", "+57 minor bodies");

        LayoutNode starHub = Node("Sol.Hub", "Sol", LayoutKind.Hub, rank: 0);
        HubLink(root, starHub);
        AttachPlanets(starHub, count: 7);
        // The dense asteroid belt collapsed to one group off the Sol hub.
        MinorGroup(starHub, "Sol", "+2892 asteroids");

        return LayoutTree.FromRoot("synthetic-dense-aggregated", root);
    }

    // A Sol-like map whose giant planets carry hundreds of minor moons, folded by the real
    // MinorFold rules: Saturn with 300 (10 inner, the rest far out), Jupiter with 93, Uranus with
    // 28 whose 19 inner ones become lanes, a planet whose 30 inner moons overflow the lane budget
    // into an inner group next to its outer group, and Sol with 50 minor bodies in one group.
    public static LayoutTree BuildDenseMoons()
    {
        LayoutNode root = Node("Earth.LO", "Earth Low Orbit", LayoutKind.LowOrbit, rank: 1);
        root.IsRoot = true;
        Ladder(root, "Earth", surfaceDv: 9400, stationaryDv: 1490, soiDv: 3210);

        LayoutNode sol = Node("Sol.Hub", "Sol", LayoutKind.Hub, rank: 0);
        HubLink(root, sol);

        // Name, SOI (km), major moons, inner minor moons, outer minor moons.
        (string Name, double SoiKm, int Major, int Inner, int Outer)[] giants =
        {
            ("Jupiter", 48.2e6, 4, 4, 89),
            ("Saturn", 54.8e6, 14, 10, 290),
            ("Uranus", 51.8e6, 0, 19, 9),
            ("Neptune", 86.7e6, 1, 30, 20)
        };
        for (int i = 0; i < giants.Length; i++)
        {
            (string name, double soiKm, int major, int inner, int outer) = giants[i];
            LayoutNode lo = Node(name + ".LO", name + " Low Orbit", LayoutKind.LowOrbit, rank: 1);
            Transfer(sol, lo, depart: 5000 + i * 500, arrive: 3000 + i * 400);
            LadderEdge(lo, Node(name + ".SoiEdge", name + " SOI Edge", LayoutKind.SoiEdge, rank: 1), 4000 + i * 100);
            AttachFoldedMoons(lo, name, soiKm, major, inner, outer, clamped: false);
        }
        GroupNode(sol, "Sol" + MinorFold.IdSuffix(FoldSlot.Group), MinorFold.Label(FoldSlot.Group, new MinorKind[50]));

        return LayoutTree.FromRoot("synthetic-dense-moons", root);
    }

    // The giants of BuildDenseMoons with the band steps a real system gives. In game a transfer
    // between a planet and its moon costs several km/s, so every moon lane hits the MaxBandStep
    // clamp and a whole fan sits in one flat row six bands below its planet, while only the large
    // regular moons carry a surface rung, six bands lower again. The planets, Pluto and the
    // root's own moon clamp the same way. This is the shape a wide fan has on the real map, as
    // opposed to the staircase of the dense-moons tree.
    public static LayoutTree BuildClampedGiants()
    {
        return BuildClampedGiants(reveal: null);
    }

    // The same map with one minor moon revealed, as a search or a pick from a group list reveals
    // it. reveal names the moon by its id without the rung suffix, for example "S2010_S_05".
    public static LayoutTree BuildClampedGiants(string? reveal)
    {
        return BuildClampedGiants(reveal, saturnInner: 10, saturnOuter: 290, "synthetic-clamped-giants");
    }

    // The clamped giants with a Saturn of 23 inner and 2 outer minor moons. That is one minor moon
    // over the fold threshold, so every inner moon keeps its lane and the outer group holds just
    // two members, and revealing one of them empties the group. reveal is as in
    // BuildClampedGiants.
    public static LayoutTree BuildPairGroup(string? reveal)
    {
        return BuildClampedGiants(reveal, saturnInner: 23, saturnOuter: 2, "synthetic-pair-group");
    }

    private static LayoutTree BuildClampedGiants(string? reveal, int saturnInner, int saturnOuter, string treeName)
    {
        LayoutNode root = Node("Earth.LO", "Earth Low Orbit", LayoutKind.LowOrbit, rank: 1);
        root.IsRoot = true;
        Ladder(root, "Earth", surfaceDv: 9400, stationaryDv: 1490, soiDv: 3210);
        LayoutNode luna = Node("Luna.LO", "Luna Low Orbit", LayoutKind.LowOrbit, rank: 2);
        Transfer(root, luna, depart: 3100, arrive: 850);
        Ladder(luna, "Luna", surfaceDv: 1870, stationaryDv: null, soiDv: 410);

        LayoutNode sol = Node("Sol.Hub", "Sol", LayoutKind.Hub, rank: 0);
        HubLink(root, sol);

        (string Name, double SurfaceDv, string[] Moons)[] rocky =
        {
            ("Mercury", 3100, new string[0]),
            ("Venus", 9900, new string[0]),
            ("Mars", 4100, new[] { "Phobos", "Deimos" })
        };
        for (int i = 0; i < rocky.Length; i++)
        {
            (string name, double surfaceDv, string[] moons) = rocky[i];
            LayoutNode lo = Node(name + ".LO", name + " Low Orbit", LayoutKind.LowOrbit, rank: 1);
            Transfer(sol, lo, depart: 3500 + i * 400, arrive: 2500 + i * 300);
            Ladder(lo, name, surfaceDv: surfaceDv, stationaryDv: 1100 + i * 100, soiDv: 1800 + i * 150);
            for (int m = 0; m < moons.Length; m++)
            {
                LayoutNode moon = Node(moons[m] + ".LO", moons[m] + " Low Orbit", LayoutKind.LowOrbit, rank: 2);
                Transfer(lo, moon, depart: 1700 + m * 150, arrive: 400 + m * 60);
                LadderEdge(moon, Node(moons[m] + ".Surface", moons[m] + " Surface", LayoutKind.Surface, rank: 2), 1550 + m * 40);
            }
        }

        (string Name, double SoiKm, int Major, int Inner, int Outer)[] giants =
        {
            ("Jupiter", 48.2e6, 4, 4, 89),
            ("Saturn", 54.8e6, 14, saturnInner, saturnOuter),
            ("Uranus", 51.8e6, 0, 19, 9),
            ("Neptune", 86.7e6, 1, 30, 20)
        };
        for (int i = 0; i < giants.Length; i++)
        {
            (string giant, double soiKm, int major, int inner, int outer) = giants[i];
            LayoutNode lo = Node(giant + ".LO", giant + " Low Orbit", LayoutKind.LowOrbit, rank: 1);
            Transfer(sol, lo, depart: 6300 + i * 700, arrive: 8100 + i * 900);
            LadderEdge(lo, Node(giant + ".SoiEdge", giant + " SOI Edge", LayoutKind.SoiEdge, rank: 1), 4000 + i * 100);
            AttachFoldedMoons(lo, giant, soiKm, major, inner, outer, clamped: true, reveal);
        }

        LayoutNode pluto = Node("Pluto.LO", "Pluto Low Orbit", LayoutKind.LowOrbit, rank: 1);
        Transfer(sol, pluto, depart: 11000, arrive: 6400);
        Ladder(pluto, "Pluto", surfaceDv: 1600, stationaryDv: null, soiDv: 1650);
        LayoutNode charon = Node("Charon.LO", "Charon Low Orbit", LayoutKind.LowOrbit, rank: 2);
        Transfer(pluto, charon, depart: 1500, arrive: 300);
        LadderEdge(charon, Node("Charon.Surface", "Charon Surface", LayoutKind.Surface, rank: 2), 1580);

        GroupNode(sol, "Sol" + MinorFold.IdSuffix(FoldSlot.Group), MinorFold.Label(FoldSlot.Group, new MinorKind[50]));

        return LayoutTree.FromRoot(treeName, root);
    }

    // One fan of a given number of moon lanes under a giant planet root, with every lane at the
    // MaxBandStep clamp as real moon transfers are. The giant's own rungs are ladder members and
    // not lanes, and every third moon carries a surface rung. Fans of 12 and 13 lanes bracket
    // the size where a fan starts to be too wide for one row.
    public static LayoutTree BuildFlatFan(int lanes)
    {
        if (lanes < 1)
            throw new ArgumentOutOfRangeException(nameof(lanes), lanes, "a fan needs at least one lane");

        LayoutNode root = Node("Giant.LO", "Giant Low Orbit", LayoutKind.LowOrbit, rank: 1);
        root.IsRoot = true;
        LadderEdge(root, Node("Giant.Stationary", "Giant Stationary", LayoutKind.Stationary, rank: 1), 900);
        LadderEdge(root, Node("Giant.SoiEdge", "Giant SOI Edge", LayoutKind.SoiEdge, rank: 1), 3200);

        for (int i = 0; i < lanes; i++)
        {
            string name = ProvisionalName("Giant", i);
            LayoutNode moon = Node(name + ".LO", name + " Low Orbit", LayoutKind.LowOrbit, rank: 2);
            Transfer(root, moon, depart: 1600 + i * 25, arrive: 300 + i * 10);
            if (i % 3 == 0)
                LadderEdge(moon, Node(name + ".Surface", name + " Surface", LayoutKind.Surface, rank: 2), 1600 + i * 10);
        }

        return LayoutTree.FromRoot("synthetic-flat-fan-" + lanes.ToString(Inv), root);
    }

    // A single giant with 435 minor moons, folded by the real MinorFold rules. Its 7 regular
    // moons and the innermost minor moons up to the fold threshold keep their lanes, the other
    // inner minor moons fold into an inner group and the outer swarm into an outer group, so the
    // fan holds the most lanes MinorFold ever gives one hub. Moon lanes clamp as in
    // BuildClampedGiants.
    public static LayoutTree BuildManyMoons()
    {
        LayoutNode root = Node("Earth.LO", "Earth Low Orbit", LayoutKind.LowOrbit, rank: 1);
        root.IsRoot = true;
        Ladder(root, "Earth", surfaceDv: 9400, stationaryDv: 1490, soiDv: 3210);

        LayoutNode sol = Node("Sol.Hub", "Sol", LayoutKind.Hub, rank: 0);
        HubLink(root, sol);

        LayoutNode lo = Node("Saturn.LO", "Saturn Low Orbit", LayoutKind.LowOrbit, rank: 1);
        Transfer(sol, lo, depart: 7000, arrive: 9000);
        LadderEdge(lo, Node("Saturn.Stationary", "Saturn Stationary", LayoutKind.Stationary, rank: 1), 9000);
        LadderEdge(lo, Node("Saturn.SoiEdge", "Saturn SOI Edge", LayoutKind.SoiEdge, rank: 1), 4100);
        AttachFoldedMoons(lo, "Saturn", 54.8e6, major: 7, inner: 40, outer: 395, clamped: true);

        GroupNode(sol, "Sol" + MinorFold.IdSuffix(FoldSlot.Group), MinorFold.Label(FoldSlot.Group, new MinorKind[22]));

        return LayoutTree.FromRoot("synthetic-many-moons", root);
    }

    // A wide clamped fan next to a planet whose moons nest five deep, each at the band clamp below the
    // last, so the deep lanes sit centred under their planet at the heights of the fan's lower
    // rows. When the fan wraps, the X pass must keep the deep subtree clear of the whole block of
    // rows, not only of the depths the fan itself reaches.
    public static LayoutTree BuildOpaqueBlock()
    {
        LayoutNode root = Node("Earth.LO", "Earth Low Orbit", LayoutKind.LowOrbit, rank: 1);
        root.IsRoot = true;
        Ladder(root, "Earth", surfaceDv: 9400, stationaryDv: 1490, soiDv: 3210);

        LayoutNode sol = Node("Sol.Hub", "Sol", LayoutKind.Hub, rank: 0);
        HubLink(root, sol);

        LayoutNode giant = Node("Giant.LO", "Giant Low Orbit", LayoutKind.LowOrbit, rank: 1);
        Transfer(sol, giant, depart: 6300, arrive: 8100);
        LadderEdge(giant, Node("Giant.SoiEdge", "Giant SOI Edge", LayoutKind.SoiEdge, rank: 1), 4000);
        for (int i = 0; i < 26; i++)
        {
            string name = ProvisionalName("Giant", i);
            LayoutNode moon = Node(name + ".LO", name + " Low Orbit", LayoutKind.LowOrbit, rank: 2);
            Transfer(giant, moon, depart: 6000 + i * 120, arrive: 2000 + i * 40);
            if (i % 4 == 0)
                LadderEdge(moon, Node(name + ".Surface", name + " Surface", LayoutKind.Surface, rank: 2), 1500 + i * 20);
        }

        LayoutNode deep = Node("Deep.LO", "Deep Low Orbit", LayoutKind.LowOrbit, rank: 1);
        Transfer(sol, deep, depart: 7000, arrive: 6000);
        LayoutNode parent = deep;
        for (int level = 0; level < 5; level++)
        {
            string id = "Deep" + level.ToString(Inv);
            LayoutNode moon = Node(id + ".LO", id + " Low Orbit", LayoutKind.LowOrbit, rank: 2);
            Transfer(parent, moon, depart: 1200, arrive: 400);
            LadderEdge(moon, Node(id + ".Surface", id + " Surface", LayoutKind.Surface, rank: 2), 120);
            parent = moon;
        }

        return LayoutTree.FromRoot("synthetic-opaque-block", root);
    }

    // The moons of one giant under the real MinorFold rules. The major moons stay lanes, the inner
    // minor moons (below a tenth of the SOI) become lanes up to the fold threshold and fold into an
    // inner group beyond it, and the outer swarm (between a fifth and a half of the SOI, as
    // captured moons sit) folds into an outer group. Unclamped lanes cost a few hundred m/s that
    // grow with the orbit, so the fan is a staircase of bands, each moon with a cheap surface rung.
    // Clamped lanes cost several km/s and all reach MaxBandStep, and only the major moons carry a
    // surface rung. A revealed moon is a lane, and the lanes take the roles MinorFold.Roles gives
    // them in the order VisualTree builds them, so a lane that only the reveal gives is marked
    // revealed and the stand-in of an emptied group sits where that group stood.
    private static void AttachFoldedMoons(LayoutNode lo, string name, double soiKm, int major, int inner, int outer, bool clamped, string? reveal = null)
    {
        var moons = new List<(string Id, FoldChild Child, int Rank)>();
        for (int m = 0; m < major; m++)
            moons.Add((name + "Major" + m.ToString(Inv), new FoldChild(true, false, false, MinorKind.Other, (0.2 + m * 0.1) * 1e9), 2));
        for (int m = 0; m < inner; m++)
            moons.Add((ProvisionalName(name, m), new FoldChild(true, true, false, MinorKind.Other, soiKm * 1e3 * 0.09 * (m + 1) / inner), 3));
        for (int m = 0; m < outer; m++)
            moons.Add((ProvisionalName(name, inner + m), new FoldChild(true, true, false, MinorKind.Other, soiKm * 1e3 * (0.2 + 0.3 * m / outer)), 3));
        moons.Sort((a, b) => a.Child.OrbitRadius.CompareTo(b.Child.OrbitRadius));

        var children = new List<FoldChild>(moons.Count);
        var plain = new List<FoldChild>(moons.Count);
        foreach (var moon in moons)
        {
            plain.Add(moon.Child);
            children.Add(moon.Child with { Revealed = moon.Id == reveal });
        }
        FoldSlot[] slots = MinorFold.Plan(children, BuildOptions.DefaultMinorGroupThreshold, soiKm * 1e3);
        FoldSlot[] plainSlots = MinorFold.Plan(plain, BuildOptions.DefaultMinorGroupThreshold, soiKm * 1e3);
        LaneRole[] roles = MinorFold.Roles(children.ConvertAll(c => c.Revealed), slots, plainSlots);

        void AddLane(int m)
        {
            LayoutNode moon = Node(moons[m].Id + ".LO", moons[m].Id + " Low Orbit", LayoutKind.LowOrbit, moons[m].Rank, revealed: roles[m] == LaneRole.Revealed);
            LayoutNode surface = Node(moons[m].Id + ".Surface", moons[m].Id + " Surface", LayoutKind.Surface, moons[m].Rank);
            if (!clamped)
            {
                Transfer(lo, moon, depart: 400 + m * 40, arrive: 150 + m * 10);
                LadderEdge(moon, surface, 50 + m);
            }
            else
            {
                Transfer(lo, moon, depart: 6000 + m * 120, arrive: 2000 + m * 40);
                if (!children[m].IsMinor)
                    LadderEdge(moon, surface, 1500 + m * 20);
            }
        }

        int inGroup = 0;
        int outGroup = 0;
        for (int m = 0; m < moons.Count; m++)
        {
            if (slots[m] == FoldSlot.InnerGroup)
                inGroup++;
            else if (slots[m] == FoldSlot.OuterGroup)
                outGroup++;
            else if (slots[m] == FoldSlot.Lane && roles[m] != LaneRole.StandIn)
                AddLane(m);
        }
        foreach ((FoldSlot slot, int count) in new[] { (FoldSlot.InnerGroup, inGroup), (FoldSlot.OuterGroup, outGroup) })
        {
            if (count > 0)
            {
                GroupNode(lo, name + MinorFold.IdSuffix(slot), MinorFold.Label(slot, new MinorKind[count]));
                continue;
            }
            for (int m = 0; m < moons.Count; m++)
            {
                if (roles[m] == LaneRole.StandIn && plainSlots[m] == slot)
                    AddLane(m);
            }
        }
    }

    // A provisional designation as the long-tail moons carry ("S2004_S_07"), so label widths
    // are realistic.
    private static string ProvisionalName(string planet, int m)
    {
        return planet.Substring(0, 1) + (2000 + m % 24).ToString(Inv) + "_" + planet.Substring(0, 1) + "_" + (m / 24 + 1).ToString("00", Inv);
    }

    // A planet root at the size of the stock Sol map: thirteen planets and dwarf planets, the
    // gas giants with their many moons, and the asteroid belt as one group. Its bounds come
    // close to the real map's, so a scene check on it judges the look at a realistic fit zoom.
    public static LayoutTree BuildRealLikeSolRoot()
    {
        LayoutNode root = Node("Earth.LO", "Earth Low Orbit", LayoutKind.LowOrbit, rank: 1);
        root.IsRoot = true;
        Ladder(root, "Earth", surfaceDv: 9400, stationaryDv: 1490, soiDv: 3210);
        LayoutNode luna = Node("Luna.LO", "Luna Low Orbit", LayoutKind.LowOrbit, rank: 2);
        Transfer(root, luna, depart: 3100, arrive: 850);
        Ladder(luna, "Luna", surfaceDv: 1870, stationaryDv: null, soiDv: 410);

        LayoutNode sol = Node("Sol.Hub", "Sol", LayoutKind.Hub, rank: 0);
        HubLink(root, sol);
        (string Name, bool Gas, string[] Moons, bool Stationary)[] planets =
        {
            ("Mercury", false, new string[0], false),
            ("Venus", false, new string[0], true),
            ("Mars", false, new[] { "Phobos", "Deimos" }, true),
            ("Ceres", false, new string[0], false),
            ("Vesta", false, new string[0], false),
            ("Jupiter", true, new[] { "Io", "Europa", "Ganymede", "Callisto", "Amalthea", "Himalia" }, true),
            ("Saturn", true, new[] { "Mimas", "Enceladus", "Tethys", "Dione", "Rhea", "Titan", "Iapetus" }, true),
            ("Uranus", true, new[] { "Miranda", "Ariel", "Umbriel", "Titania", "Oberon" }, true),
            ("Neptune", true, new[] { "Triton", "Proteus" }, true),
            ("Pluto", false, new[] { "Charon" }, false),
            ("Eris", false, new[] { "Dysnomia" }, false),
            ("Haumea", false, new string[0], false),
            ("Makemake", false, new string[0], false)
        };
        for (int i = 0; i < planets.Length; i++)
        {
            (string name, bool gas, string[] moons, bool stationary) = planets[i];
            LayoutNode lo = Node(name + ".LO", name + " Low Orbit", LayoutKind.LowOrbit, rank: 1);
            Transfer(sol, lo, depart: 3000 + i * 500, arrive: 2000 + i * 400);
            if (gas)
            {
                LadderEdge(lo, Node(name + ".Stationary", name + " Stationary", LayoutKind.Stationary, rank: 1), 9000);
                LadderEdge(lo, Node(name + ".SoiEdge", name + " SOI Edge", LayoutKind.SoiEdge, rank: 1), 4000 + i * 100);
            }
            else
            {
                Ladder(lo, name, surfaceDv: 2000 + i * 500, stationaryDv: stationary ? 900 + i * 50 : (double?)null, soiDv: 600 + i * 80);
            }
            for (int m = 0; m < moons.Length; m++)
            {
                LayoutNode moon = Node(moons[m] + ".LO", moons[m] + " Low Orbit", LayoutKind.LowOrbit, rank: 2);
                Transfer(lo, moon, depart: 500 + m * 180, arrive: 200 + m * 80);
                Ladder(moon, moons[m], surfaceDv: 300 + m * 180, stationaryDv: null, soiDv: null);
            }
        }
        MinorGroup(sol, "Sol", "+2892 asteroids");

        return LayoutTree.FromRoot("synthetic-real-like-sol", root);
    }

    // Another star system collapsed to one node, the way the map shows it until it is opened:
    // its root hub as a star-system stub, labelled with its distance, with the summary of what
    // it holds.
    public static LayoutTree BuildSystemStub(string id, bool barycenter, string distance = "4.344 ly", string summary = "")
    {
        LayoutNode root = new LayoutNode
        {
            Id = id + ".Hub",
            Label = id + "  " + distance,
            ShortLabel = id + "  " + distance,
            BodyId = id,
            Kind = LayoutKind.Hub,
            Rank = 0,
            HubRole = barycenter ? HubRole.Barycenter : HubRole.Star,
            IsSystemStub = true,
            Summary = summary
        };
        return Part(root, "system-" + id);
    }

    // An opened multiple star system: a barycenter hub with its parking orbit, three stars each
    // with a parking orbit, and two planets under the last star, every hop a dV-free Approach
    // (a transfer-class edge of zero dV, as VisualTreeAdapter maps it). The root carries its
    // distance and summary like the collapsed stub of the same system.
    public static LayoutTree BuildBarycenterSystem(string id, string distance = "4.344 ly", string summary = "")
    {
        LayoutNode bary = HubNode(id, id + "  " + distance, HubRole.Barycenter, summary);
        Approach(bary, Node(id + ".Parking", id + " Parking Orbit", LayoutKind.LowOrbit, rank: 0));
        for (int i = 0; i < 3; i++)
        {
            string star = id + "Star" + i.ToString(Inv);
            LayoutNode hub = HubNode(star, star + " Hub", HubRole.Star);
            Approach(bary, hub);
            Approach(hub, Node(star + ".Parking", star + " Parking Orbit", LayoutKind.LowOrbit, rank: 0));
            if (i == 2)
            {
                for (int p = 0; p < 2; p++)
                {
                    string planet = star + (p == 0 ? "b" : "d");
                    LayoutNode lo = Node(planet + ".LO", planet + " Low Orbit", LayoutKind.LowOrbit, rank: 1);
                    Approach(hub, lo);
                    LadderEdge(lo, Node(planet + ".Surface", planet + " Surface", LayoutKind.Surface, rank: 1), 4000 + p * 900);
                }
            }
        }
        return Part(bary, "system-" + id);
    }

    // An opened single-star system: the star hub with its parking orbit and four planets.
    public static LayoutTree BuildStarSystem(string id, string distance = "5.963 ly", string summary = "")
    {
        LayoutNode star = HubNode(id, id + "  " + distance, HubRole.Star, summary);
        Approach(star, Node(id + ".Parking", id + " Parking Orbit", LayoutKind.LowOrbit, rank: 0));
        for (int p = 0; p < 4; p++)
        {
            string planet = id + (char)('b' + p);
            LayoutNode lo = Node(planet + ".LO", planet + " Low Orbit", LayoutKind.LowOrbit, rank: 1);
            Approach(star, lo);
            LadderEdge(lo, Node(planet + ".Surface", planet + " Surface", LayoutKind.Surface, rank: 1), 3000 + p * 700);
        }
        return Part(star, "system-" + id);
    }

    // A hub keyed by its body like the adapter keys it. A system root keeps its whole title as
    // the short label, any other hub its body name.
    private static LayoutNode HubNode(string id, string label, HubRole role, string summary = "")
    {
        return new LayoutNode
        {
            Id = id + ".Hub",
            Label = label,
            ShortLabel = label.Contains("  ", StringComparison.Ordinal) ? label : id,
            BodyId = id,
            Kind = LayoutKind.Hub,
            Rank = 0,
            HubRole = role,
            Summary = summary
        };
    }

    // A part is laid out as its own tree, but only the ego root is the map's root.
    private static LayoutTree Part(LayoutNode root, string name)
    {
        LayoutTree tree = LayoutTree.FromRoot(name, root);
        root.IsRoot = false;
        root.IsSystemRoot = true;
        return tree;
    }

    private static void Approach(LayoutNode from, LayoutNode to)
    {
        from.AddChild(new LayoutEdge { From = from, To = to, Class = EdgeClass.Transfer, Dv = 0.0, IsApproach = true });
    }

    // A collapsed minor-body group hanging off a hub: a MinorGroup node reached by a dV-free
    // transfer-class edge (what VisualTreeAdapter maps a GroupLink to), with no children. The
    // zero dV lands it one band below the hub (cumulative) or in its own well on the spine
    // (gravity-well), and the renderer draws no badge for it.
    private static void MinorGroup(LayoutNode hub, string bodyId, string label)
    {
        GroupNode(hub, bodyId + ".MinorGroup", label);
    }

    private static void GroupNode(LayoutNode hub, string id, string label)
    {
        LayoutNode group = Node(id, label, LayoutKind.MinorGroup, rank: 2);
        hub.AddChild(new LayoutEdge { From = hub, To = group, Class = EdgeClass.Transfer, Dv = 0.0 });
    }

    // A deliberately large tree (~1800 nodes) to stress the Barnes-Hut spring repulsion and the
    // iteration cap offline. An all-pairs repulsion would be ~1800^2 per iteration on the draw
    // thread; Barnes-Hut keeps it near O(n log n). Only the dot-overlap and label checks run for
    // Spring, and the grid snap must still leave no two dots sharing a cell at this scale.
    public static LayoutTree BuildSpringStress()
    {
        LayoutNode root = Node("Hub.LO", "Hub Low Orbit", LayoutKind.LowOrbit, rank: 1);
        root.IsRoot = true;
        Ladder(root, "Hub", surfaceDv: 3000, stationaryDv: 1200, soiDv: 800);

        LayoutNode star = Node("Star.Hub", "Star", LayoutKind.Hub, rank: 0);
        HubLink(root, star);

        for (int i = 0; i < 600; i++)
        {
            string id = "Body" + i.ToString(Inv);
            LayoutNode lo = Node(id + ".LO", "Body " + i.ToString(Inv) + " Orbit", LayoutKind.LowOrbit, rank: 2);
            Transfer(star, lo, depart: 1000 + (i % 11) * 60, arrive: 300 + (i % 7) * 40);
            Ladder(lo, id, surfaceDv: 500 + (i % 9) * 70, stationaryDv: 900 + (i % 5) * 50, soiDv: null);
        }

        return LayoutTree.FromRoot("synthetic-spring-stress", root);
    }

    // A deliberately wide, shallow fan: one body with many sibling destinations that
    // all land on a narrow band, stressing sibling separation and the grid-snap
    // collision pass on a dense row (a gas giant with many moons, or a busy hub).
    public static LayoutTree BuildWideFan()
    {
        LayoutNode root = Node("GasGiant.LO", "Gas Giant Low Orbit", LayoutKind.LowOrbit, rank: 1);
        root.IsRoot = true;

        for (int i = 0; i < 40; i++)
        {
            string id = "Sat" + i.ToString(Inv);
            LayoutNode lo = Node(id + ".LO", "Satellite " + i.ToString(Inv) + " Orbit", LayoutKind.LowOrbit, rank: 2);
            // Similar transfer costs so most land on the same band, the dense case.
            Transfer(root, lo, depart: 900 + (i % 5) * 40, arrive: 200 + (i % 3) * 30);
            Ladder(lo, id, surfaceDv: 500 + (i % 7) * 50, stationaryDv: null, soiDv: null);
        }

        return LayoutTree.FromRoot("synthetic-wide-fan", root);
    }

    // Demonstrates the gravity-well arrival orientation. The root keeps its fork - its
    // surface, stationary and outbound SOI-edge all hang below its low orbit - while
    // every destination is entered at a capture (Intercept) anchor that circularizes
    // down to low orbit, with surface and stationary below that. Small, for eyeballing
    // the arrival model offline; the real shapes come from VisualTree in-game.
    public static LayoutTree BuildArrivalDemo()
    {
        LayoutNode root = Node("Home.LO", "Home Low Orbit", LayoutKind.LowOrbit, rank: 1);
        root.IsRoot = true;
        Ladder(root, "Home", surfaceDv: 9400, stationaryDv: 1490, soiDv: 3200);

        // A moon of home: capture into a loose ellipse, then circularize down.
        Arrive(root, "Moon", "Moon", rank: 2, depart: 900, arrive: 250, circularizeDv: 410, surfaceDv: 600, stationaryDv: null);

        LayoutNode star = Node("Star.Hub", "Star", LayoutKind.Hub, rank: 0);
        HubLink(root, star);

        LayoutNode marsLo = Arrive(star, "Mars", "Mars", rank: 1, depart: 3600, arrive: 2100, circularizeDv: 980, surfaceDv: 4100, stationaryDv: 1140);
        Arrive(marsLo, "Phobos", "Phobos", rank: 2, depart: 700, arrive: 120, circularizeDv: 90, surfaceDv: 200, stationaryDv: null);

        Arrive(star, "Venus", "Venus", rank: 1, depart: 3500, arrive: 2700, circularizeDv: 890, surfaceDv: 12000, stationaryDv: null);

        return LayoutTree.FromRoot("synthetic-arrival", root);
    }

    // Reach a destination from `hub`: capture (Intercept) -> circularize -> low orbit ->
    // surface (+ stationary). Returns the low orbit, the local hub this body's moons
    // branch from. Destinations carry no outbound SoiEdge rung.
    private static LayoutNode Arrive(LayoutNode hub, string id, string name, int rank, double depart, double arrive, double circularizeDv, double surfaceDv, double? stationaryDv)
    {
        LayoutNode capture = Node(id + ".Capture", name + " Intercept", LayoutKind.Intercept, rank);
        Transfer(hub, capture, depart, arrive);

        LayoutNode lo = Node(id + ".LO", name + " Low Orbit", LayoutKind.LowOrbit, rank);
        LadderEdge(capture, lo, circularizeDv);
        LadderEdge(lo, Node(id + ".Surface", name + " Surface", LayoutKind.Surface, rank), surfaceDv);
        if (stationaryDv.HasValue)
            LadderEdge(lo, Node(id + ".Stationary", name + " Stationary", LayoutKind.Stationary, rank), stationaryDv.Value);

        return lo;
    }

    private static void AttachPlanets(LayoutNode starHub, int count)
    {
        for (int i = 0; i < count; i++)
        {
            string id = "Planet" + i.ToString(Inv);
            string name = PlanetName(i);
            bool gasGiant = i >= count - 4;

            LayoutNode lo = Node(id + ".LO", name + " Low Orbit", LayoutKind.LowOrbit, rank: 1);
            Transfer(starHub, lo, depart: 3500 + i * 850, arrive: 2100 + i * 720, approximate: i == count - 1);

            if (gasGiant)
            {
                // No solid surface; richer moon system instead.
                int moons = 2 + i % 3;
                for (int m = 0; m < moons; m++)
                    AttachMoon(lo, id, name, m);
            }
            else
            {
                Ladder(lo, id, surfaceDv: 3100 + i * 700, stationaryDv: i % 2 == 0 ? 900 + i * 120 : (double?)null, soiDv: 800 + i * 90);
                int moons = i % 3;
                for (int m = 0; m < moons; m++)
                    AttachMoon(lo, id, name, m);
            }
        }
    }

    private static void AttachMoon(LayoutNode planetLo, string planetId, string planetName, int m)
    {
        string id = planetId + ".M" + m.ToString(Inv);
        LayoutNode lo = Node(id + ".LO", planetName + " " + MoonSuffix(m) + " Orbit", LayoutKind.LowOrbit, rank: 2);
        Transfer(planetLo, lo, depart: 600 + m * 220, arrive: 180 + m * 60);
        Ladder(lo, id, surfaceDv: 400 + m * 150, stationaryDv: null, soiDv: null);
    }

    // Build the standard rungs hanging off a body's low orbit: surface below it, and
    // optional stationary and SOI-edge rungs raised above it.
    private static void Ladder(LayoutNode lowOrbit, string bodyId, double surfaceDv, double? stationaryDv, double? soiDv)
    {
        LayoutNode surface = Node(bodyId + ".Surface", LabelFor(lowOrbit, "Surface"), LayoutKind.Surface, lowOrbit.Rank);
        LadderEdge(lowOrbit, surface, surfaceDv);

        if (stationaryDv.HasValue)
        {
            LayoutNode stat = Node(bodyId + ".Stationary", LabelFor(lowOrbit, "Stationary"), LayoutKind.Stationary, lowOrbit.Rank);
            LadderEdge(lowOrbit, stat, stationaryDv.Value);
        }

        if (soiDv.HasValue)
        {
            LayoutNode soi = Node(bodyId + ".SoiEdge", LabelFor(lowOrbit, "SOI Edge"), LayoutKind.SoiEdge, lowOrbit.Rank);
            LadderEdge(lowOrbit, soi, soiDv.Value);
        }
    }

    private static string LabelFor(LayoutNode lowOrbit, string rung)
    {
        // Reuse the body word from the low-orbit label so sibling rung labels vary in
        // length the way real ones do, exercising the variable-width spacing.
        string baseName = lowOrbit.Label.Replace(" Low Orbit", "").Replace(" Orbit", "");
        return baseName + " " + rung;
    }

    // The overview label and body key are the label without its rung, as the adapter gives
    // them in game, so a check at fit zoom sees the labels the map really shows.
    private static LayoutNode Node(string id, string label, LayoutKind kind, int rank, bool revealed = false)
    {
        string body = BodyName(label);
        return new LayoutNode { Id = id, Label = label, ShortLabel = body, BodyId = body, Kind = kind, Rank = rank, IsRevealed = revealed };
    }

    private static readonly string[] Rungs = { " Low Orbit", " Parking Orbit", " Orbit", " Surface", " Stationary", " SOI Edge", " Intercept" };

    private static string BodyName(string label)
    {
        foreach (string rung in Rungs)
        {
            if (label.EndsWith(rung, System.StringComparison.Ordinal))
                return label.Substring(0, label.Length - rung.Length);
        }
        return label;
    }

    private static void Transfer(LayoutNode from, LayoutNode to, double depart, double arrive, bool approximate = false)
    {
        from.AddChild(new LayoutEdge { From = from, To = to, Class = EdgeClass.Transfer, Dv = depart + arrive, IsApproximate = approximate });
    }

    private static void LadderEdge(LayoutNode from, LayoutNode to, double dv)
    {
        from.AddChild(new LayoutEdge { From = from, To = to, Class = EdgeClass.Ladder, Dv = dv });
    }

    // A ladder edge used for the surface/raise links from a low orbit.
    private static void Ladder(LayoutNode from, LayoutNode to, double raiseDv)
    {
        LadderEdge(from, to, raiseDv);
    }

    private static void HubLink(LayoutNode from, LayoutNode to)
    {
        from.AddChild(new LayoutEdge { From = from, To = to, Class = EdgeClass.HubLink });
    }

    private static string PlanetName(int i)
    {
        string[] names = { "Mercury", "Venus", "Mars", "Ceres", "Jupiter", "Saturn", "Uranus", "Neptune", "Pluto", "Eris", "Haumea", "Makemake" };
        return i < names.Length ? names[i] : "Planet" + i.ToString(Inv);
    }

    private static string MoonName(int i)
    {
        string[] names = { "Io", "Europa", "Ganymede", "Callisto", "Titan", "Rhea", "Mimas", "Dione" };
        return i < names.Length ? names[i] : "Moon" + i.ToString(Inv);
    }

    private static string MoonSuffix(int m)
    {
        string[] s = { "I", "II", "III", "IV", "V" };
        return m < s.Length ? s[m] : "M" + m.ToString(Inv);
    }
}
