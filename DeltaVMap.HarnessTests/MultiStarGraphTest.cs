using DeltaVMap.Dv;
using DeltaVMap.Model;
using DeltaVMap.Route;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// Checks the physical forest against the loaded system: one tree per independent root, every
// orbiting celestial (planets, moons, the stars of a multiple system and their planets) in exactly
// the tree IIndependentRoot.RootOf names, stars and barycenters without a surface rung, and a
// finite interstellar estimate from the home body to every other system that measures the same
// distance as the stock planner.
//
// The visual tree must hold every other star system below one Interstellar edge each from the
// home system's root hub, and a route there must fold into one interstellar leg. A system with one
// root builds no Interstellar edge at all.
//
// When the stock interstellar bodies are loaded (SolSystemInterstellar or EarthOnlyInterstellar),
// it also pins the real tree from InterstellarAstronomicals.xml: the root types and order, the
// Alpha Centauri barycenter with its three stars and its mass, the exoplanets, and the absolute
// distances and capture speeds; and it routes to an exoplanet, a star system's parking orbit and
// a surface, checks every exoplanet's arrival chain, and checks that a capture at a planet never
// costs more than parking at its star and dropping in. The checks key on the bodies, not on the
// system id, so both stock interstellar systems run them. A system with one root, such as the
// default Sol, logs a SKIP for the multi-star part. The harness loads another system through its
// run script, for example -System SolSystemInterstellar.
public sealed class MultiStarGraphTest : IHarnessTest
{
    private const string Tag = "[dvmap-multistar-graph]";

    private const double G = 6.6743E-11;

    // DistanceReference converts light-years with this factor, and the library positions are in
    // light-years around Sol at the origin.
    private const double LightYear = 9461000000000000.0;

    private static readonly (string Id, double Distance)[] StockRoots =
    {
        ("AlphaCentauri", 4.109915256421932e16),
        ("BarnardsStar", 5.641501695728401e16),
        ("TauCeti", 1.1269744997650078e17)
    };

    private static readonly string[] AlphaCentauriStars = { "AlphaCentauriA", "AlphaCentauriB", "ProximaCentauri" };

    private static readonly (string Id, string Parent)[] Exoplanets =
    {
        ("ProximaCentaurib", "ProximaCentauri"),
        ("ProximaCentaurid", "ProximaCentauri"),
        ("BarnardsStarb", "BarnardsStar"),
        ("BarnardsStarc", "BarnardsStar"),
        ("BarnardsStard", "BarnardsStar"),
        ("BarnardsStare", "BarnardsStar"),
        ("TauCetif", "TauCeti"),
        ("TauCetig", "TauCeti"),
        ("TauCetih", "TauCeti")
    };

    public string Name => "dvmap-multistar-graph";

    public int Run(HeadlessSession session)
    {
        bool ok = true;
        try
        {
            CelestialSystem system = session.System;
            SystemGraph? graph = SystemGraph.Build(system);
            if (graph == null)
            {
                HarnessLog.Line($"{Tag} FAIL: no graph for system '{system.Id}'.");
                return 1;
            }

            int rootCount = 0;
            foreach (IIndependentRoot root in system.All.OfType<IIndependentRoot>())
            {
                rootCount++;
                PhysicalNode? node = graph.Find(root.Id);
                ok &= Check($"root '{root.Id}' is a graph root", node != null && node.IsSystemRoot && node.IsHubOnly);
            }
            ok &= Check($"{rootCount} roots in the forest", graph.Roots.Count == rootCount);
            ok &= Check($"the forest has the {Universe.Roots.Length} roots of Universe.Roots", graph.Roots.Count == Universe.Roots.Length);
            HarnessLog.Line($"{Tag} system '{system.Id}': {graph.Roots.Count} star system(s), {graph.AllNodes.Count} bodies");

            int missing = 0;
            int misplaced = 0;
            foreach (Astronomical body in system.All.OfType<Astronomical>())
            {
                if (body is not IOrbiter || body is not IParentBody parentBody)
                    continue;
                PhysicalNode? node = graph.Find(body.Id);
                if (node == null)
                {
                    missing++;
                    continue;
                }
                IIndependentRoot? expected = IIndependentRoot.RootOf(parentBody);
                if (expected == null || SystemGraph.SystemRootOf(node).Id != expected.Id)
                    misplaced++;
            }
            ok &= Check($"every orbiting celestial is in the forest ({missing} missing)", missing == 0);
            ok &= Check($"every body sits under its own root ({misplaced} misplaced)", misplaced == 0);

            bool hubsClean = true;
            foreach (PhysicalNode node in graph.AllNodes)
            {
                if (node.IsHubOnly && (node.Ladder.HasSurface || !node.Ladder.IsHubOnly || !(node.Ladder.LowOrbitRadius > 0.0)))
                    hubsClean = false;
            }
            ok &= Check("stars and barycenters have a parking orbit and no surface", hubsClean);

            ok &= CheckRoles(graph);

            ok &= CheckVisualTree(system, graph);

            if (Universe.Roots.Length <= 1)
            {
                HarnessLog.Line($"{Tag} SKIP multi-star: system '{system.Id}' has {Universe.Roots.Length} root, so the multi-star forest and the interstellar legs are NOT covered by this run. Run it with -System SolSystemInterstellar (KSA_HEADLESS_SYSTEM) to cover them.");
            }
            else
            {
                ok &= CheckInterstellar(system, graph);
                ok &= CheckStockInterstellar(system, graph);
                ok &= CheckStockRoutes(system, graph);
                ok &= CheckExoplanetChains(system, graph);
                ok &= CheckArrivalMode(graph);
            }
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"{Tag} FAIL: {ex}");
            ok = false;
        }

        HarnessLog.Line($"{Tag} {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    // The visual tree rooted at the home body: one Interstellar edge per other system (up to the
    // shown cap and one group for the rest), every edge from the home system's root hub, and
    // unique node Ids across all systems. A single system has no Interstellar edge.
    private static bool CheckVisualTree(CelestialSystem system, SystemGraph graph)
    {
        PhysicalNode? home = system.HomeBody != null ? graph.Find(system.HomeBody.Id) : null;
        if (home == null)
            return Check("the home body is in the forest", false);
        VisualTree tree = VisualTree.Build(graph, new DvCache(), home, egoState: null, BuildOptions.Default);
        if (!graph.HasSeveralSystems)
        {
            // The node count is logged as a baseline, so a later run shows any change to the
            // single-system map.
            bool noApproach = true;
            foreach (StateNode node in tree.Nodes)
            {
                foreach (Edge edge in node.Out)
                    noApproach &= edge.Kind != SegmentKind.Approach && edge.Kind != SegmentKind.Interstellar;
            }
            HarnessLog.Line($"{Tag} single-system tree at {home.Id}: {tree.Nodes.Count} nodes");
            return Check("a single star system builds no Interstellar or Approach edge", tree.InterstellarEdges.Count == 0 && noApproach);
        }

        int others = graph.Roots.Count - 1;
        bool ok = Check($"one Interstellar edge per shown system ({tree.InterstellarEdges.Count} of {others})",
            tree.InterstellarEdges.Count == Math.Min(others, SystemGraph.MaxShownSystems));
        PhysicalNode homeRoot = SystemGraph.SystemRootOf(home);
        bool fromHub = true;
        foreach (Edge edge in tree.InterstellarEdges)
            fromHub &= ReferenceEquals(edge.From, tree.SystemHub) && edge.From.Body.Id == homeRoot.Id && edge.InterstellarDistance > 0.0;
        ok &= Check($"every Interstellar edge leaves {homeRoot.Id}.Hub", fromHub);
        var ids = new HashSet<string>();
        bool unique = true;
        foreach (StateNode node in tree.Nodes)
            unique &= ids.Add(node.Id);
        ok &= Check($"all {tree.Nodes.Count} node Ids are unique across the systems", unique);
        return ok;
    }

    // Routes into the stock interstellar systems from Earth, through the real router and
    // accumulator: the leg folds into one InterstellarRoute with the right bodies and roots, no
    // transfer is charged inside it, a vessel parked at Earth drops the ladder steps to low
    // orbit, and at full ladder detail the capture splits at the Intercept anchor.
    private static bool CheckStockRoutes(CelestialSystem system, SystemGraph graph)
    {
        PhysicalNode? earth = system.HomeBody != null ? graph.Find(system.HomeBody.Id) : null;
        if (earth == null || graph.Find("ProximaCentaurib") == null || graph.Find("BarnardsStarb") == null)
        {
            HarnessLog.Line($"{Tag} SKIP stock routes: the stock interstellar bodies are not loaded.");
            return true;
        }

        bool ok = true;
        var options = new RouteOptions();
        double medium = earth.Ladder.LowOrbitRadius * 3.0;
        VisualTree tree = VisualTree.Build(graph, new DvCache(), earth,
            new ClassifiedState(StateKind.YouAreHere, medium), BuildOptions.Default);
        StateNode? origin = tree.YouAreHere;
        if (origin == null)
            return Check("the medium-orbit you-are-here node exists", false);

        foreach ((string bodyId, StateKind kind, string root) in new[]
        {
            ("ProximaCentaurib", StateKind.LowOrbit, "AlphaCentauri"),
            ("BarnardsStarb", StateKind.Surface, "BarnardsStar"),
            ("AlphaCentauri", StateKind.LowOrbit, "AlphaCentauri")
        })
        {
            StateNode? target = tree.FindBodyNode(bodyId, kind);
            if (target == null && kind == StateKind.Surface)
            {
                HarnessLog.Line($"{Tag} SKIP {bodyId} surface route: the body has no surface rung.");
                continue;
            }
            RoutePath? path = target != null ? RouteFinder.FindPath(origin, target) : null;
            ok &= Check($"a path reaches {bodyId}.{kind}", path != null);
            if (path == null)
                continue;

            RouteSummary vessel = RouteAccumulator.Accumulate(path, graph, options, new RouteContext(true));
            RouteSummary lowOrbit = RouteAccumulator.Accumulate(path, graph, options, new RouteContext(false));
            InterstellarRoute? leg = vessel.Interstellar;
            ok &= Check($"{bodyId}: the route has one interstellar leg", leg != null);
            if (leg == null)
                continue;
            ok &= Check($"{bodyId}: departs {earth.Id} for {bodyId} in {root}",
                leg.DepartBody.Id == earth.Id && leg.Target.Id == bodyId && leg.SourceRoot.Id == graph.HomeRoot.Id && leg.DestinationRoot?.Id == root);
            bool noTransfer = true;
            foreach (RouteSegment segment in vessel.Segments)
                noTransfer &= segment.Kind != SegmentKind.Transfer;
            ok &= Check($"{bodyId}: no transfer is charged inside the leg", noTransfer);
            ok &= Check($"{bodyId}: from the vessel's orbit there is no lowering to low orbit", !HasLabel(vessel, "Lower to low orbit"));
            ok &= Check($"{bodyId}: from the low orbit the lowering is charged", HasLabel(lowOrbit, "Lower to low orbit"));
            ok &= Check($"{bodyId}: no return, plane change or aerobrake",
                vessel.ReturnDv == 0.0 && vessel.PlaneChangeDv == 0.0 && !vessel.HasAerobrakeOption && !vessel.HasReturnAerobrakeOption);
            if (kind == StateKind.Surface)
                ok &= Check($"{bodyId}: the landing after the capture is charged", HasLabel(vessel, "Land on " + bodyId));
        }

        VisualTree full = VisualTree.Build(graph, new DvCache(), earth, null,
            new BuildOptions(fullLadder: true, showMinorBodies: true, showComets: true));
        StateNode? fullOrigin = full.FindBodyNode(earth.Id, StateKind.LowOrbit);
        StateNode? intercept = full.FindBodyNode("ProximaCentaurib", StateKind.Intercept);
        StateNode? fullTarget = full.FindBodyNode("ProximaCentaurib", StateKind.LowOrbit);
        RoutePath? fullPath = fullOrigin != null && fullTarget != null ? RouteFinder.FindPath(fullOrigin, fullTarget) : null;
        InterstellarRoute? split = fullPath != null ? RouteAccumulator.Accumulate(fullPath, graph, options, RouteContext.None).Interstellar : null;
        ok &= Check("at full detail the capture splits at the Intercept anchor",
            intercept != null && split != null && split.CaptureSplitDv > 0.0);
        return ok;
    }

    private static bool HasLabel(RouteSummary summary, string label)
    {
        foreach (RouteSegment segment in summary.Segments)
        {
            if (segment.Label == label)
                return true;
        }
        return false;
    }

    // Every exoplanet's arrival chain: one level per step below its root, each with a plane
    // sine, captured into the planet's own low orbit whatever periapsis a star would use, and a
    // coast distance to the planet's star. At Barnard b the capture is never above parking at
    // the star and dropping in by a Hohmann with the Oberth capture at the planet.
    private static bool CheckExoplanetChains(CelestialSystem system, SystemGraph graph)
    {
        PhysicalNode? earth = system.HomeBody != null ? graph.Find(system.HomeBody.Id) : null;
        if (earth == null)
            return Check("the home body is in the forest", false);
        bool ok = true;
        foreach ((string id, string parent) in Exoplanets)
        {
            PhysicalNode? planet = graph.Find(id);
            PhysicalNode? star = graph.Find(parent);
            if (planet == null || star == null)
                continue;
            int depth = 0;
            for (PhysicalNode? n = planet; n != null && n.Parent != null; n = n.Parent)
                depth++;
            InterstellarLeg near = InterstellarLegs.Build(earth, planet, 0.1 * InterstellarMath.AstronomicalUnit);
            InterstellarLeg far = InterstellarLegs.Build(earth, planet, 10.0 * InterstellarMath.AstronomicalUnit);
            bool sines = true;
            foreach (WellStep step in near.ArriveSteps)
                sines &= step.AimSine.HasValue;
            ok &= Check($"{id}: {depth} arrival levels, each with a plane sine", near.ArriveSteps.Length == depth && sines);
            ok &= Check($"{id}: captured into its own low orbit", near.ArrivePark.Radius == planet.Ladder.LowOrbitRadius && Relative(near.ArrivePark.Mu, planet.Ladder.Mu) <= 1e-12);
            ok &= Check($"{id}: the capture does not depend on the periapsis",
                near.CaptureBurn(30000.0) == far.CaptureBurn(30000.0));
            double toStar = (star.Astro.GetPositionEcl() - graph.HomeRoot.Astro.GetPositionEcl()).Length();
            ok &= Check($"{id}: the coast ends at its star", Relative(near.Distance, toStar) <= 1e-12);
            HarnessLog.Line(FormattableString.Invariant(
                $"{Tag} {id}: low orbit {planet.Ladder.LowOrbitRadius / 1000.0:F1} km (mean radius {planet.Ladder.MeanRadius / 1000.0:F1} km), capture at 0, 30 km/s, 0.01 c: {near.CaptureBurn(0.0):F0} / {near.CaptureBurn(30000.0):F0} / {near.CaptureBurn(0.01 * InterstellarMath.SpeedOfLight):F0} m/s"));
        }

        PhysicalNode? barnardB = graph.Find("BarnardsStarb");
        PhysicalNode? barnard = graph.Find("BarnardsStar");
        if (barnardB != null && barnard != null)
        {
            InterstellarLeg toPlanet = InterstellarLegs.Build(earth, barnardB, InterstellarMath.AstronomicalUnit);
            InterstellarLeg toStar = InterstellarLegs.Build(earth, barnard, InterstellarMath.AstronomicalUnit);
            double radius = OrbitalStates.TransferRadius(((IOrbiter)barnardB.Astro).Orbit);
            DeltaVCalculator.Hohmann(barnard.Ladder.Mu, toStar.ArrivePark.Radius, radius, out double inwardDepart, out double inwardArrive);
            double inward = inwardDepart + DeltaVCalculator.OberthBurn(barnardB.Ladder.Mu, barnardB.Ladder.LowOrbitRadius, inwardArrive);
            bool below = true;
            foreach (double v in new[] { 0.0, 10000.0, 30000.0, 100000.0, 0.001 * InterstellarMath.SpeedOfLight, 0.01 * InterstellarMath.SpeedOfLight })
                below &= toPlanet.CaptureBurn(v) <= toStar.CaptureBurn(v) + inward;
            ok &= Check("a capture at Barnard b never costs more than the star park plus the drop in", below);
        }
        return ok;
    }

    // A vessel coasting on an open orbit around a root: the root's bodies hang from Approach
    // edges, so their captures are priced from the measured excess.
    private static bool CheckArrivalMode(SystemGraph graph)
    {
        PhysicalNode root = graph.Roots[1];
        VisualTree tree = VisualTree.Build(graph, new DvCache(), root,
            new ClassifiedState(StateKind.YouAreHere, 1e15, isOpenCruise: true), BuildOptions.Default);
        bool approach = false;
        bool noHubLinkToBodies = true;
        foreach (Edge edge in tree.Root.Out)
        {
            if (edge.Kind == SegmentKind.Approach)
                approach = true;
            if (edge.Kind == SegmentKind.HubLink && edge.To.Kind != StateKind.YouAreHere)
                noHubLinkToBodies = false;
        }
        bool ok = Check($"an open cruise at {root.Id} reaches its bodies by Approach edges", approach && noHubLinkToBodies && tree.YouAreHere != null);
        // The root's parking orbit stays a target, so a capture chosen at its stub survives the
        // crossing into that system.
        ok &= Check($"an open cruise at {root.Id} keeps its parking orbit", tree.FindBodyNode(root.Id, StateKind.LowOrbit) != null);
        return ok;
    }

    // Content-free checks for any system with several roots.
    private static bool CheckInterstellar(CelestialSystem system, SystemGraph graph)
    {
        PhysicalNode? home = system.HomeBody != null ? graph.Find(system.HomeBody.Id) : null;
        if (home == null)
            return Check("the home body is in the forest", false);

        bool ok = Check("the home root comes first", ReferenceEquals(graph.HomeRoot, SystemGraph.SystemRootOf(home)));

        // The stock planner measures from the star nearest the vessel (TransferPlanner.SourceStar)
        // to the destination root (TransferPlanner.DrawInterstellarPlan, DISTANCE).
        StellarBody? sourceStar = Universe.NearestStar(home.Astro.GetPositionEcl());
        if (sourceStar == null)
            return Check("a star is nearest the home body", false);

        ParkingOrbit park = ParkingOrbit.Circular(home.Ladder.Mu, home.Ladder.LowOrbitRadius);
        foreach (PhysicalNode root in graph.Roots)
        {
            if (ReferenceEquals(root, SystemGraph.SystemRootOf(home)))
                continue;
            InterstellarLeg leg = InterstellarLegs.Build(home, root, InterstellarMath.AstronomicalUnit);
            double v = 0.01 * InterstellarMath.SpeedOfLight;
            double total = leg.TotalDv(v, in park);
            HarnessLog.Line(FormattableString.Invariant(
                $"{Tag} {home.Id} to {root.Id}: {leg.Distance / LightYear:F3} ly, at 0.01 c departure {leg.DepartBurn(v, in park) / 1000.0:F1} km/s, capture {leg.CaptureBurn(v) / 1000.0:F1} km/s, coast {InterstellarMath.CoastSeconds(leg.Distance, v) / 3.15576e7:F1} yr"));
            ok &= Check($"finite estimate to {root.Id}", double.IsFinite(total) && total > v && leg.Distance > 0.0);

            double stockDistance = (root.Astro.GetPositionEcl() - sourceStar.GetPositionEcl()).Length();
            ok &= Check($"distance to {root.Id} is the stock planner's", Relative(leg.Distance, stockDistance) <= 1e-12);
            double captureRadius = InterstellarLegs.CaptureRadius(root, InterstellarMath.AstronomicalUnit);
            double circular = Math.Sqrt(root.Body.Mass * G / captureRadius);
            ok &= Check($"capture at {root.Id} is around its own mass", Relative(leg.ArrivePark.Speed, circular) <= 1e-9);
            ok &= Check($"a root target {root.Id} has no arrival chain", leg.ArriveSteps.Length == 0);
            ok &= Check($"departure chain to {root.Id} is plane-limited at the top level",
                leg.DepartSteps.Length > 0 && leg.DepartSteps[0].AimSine is double sine && Math.Abs(sine) <= 1.0);
            ok &= Check($"cheapest trip to {root.Id} costs at most the trip at 0.01 c",
                leg.MinimumDv(in park, OrbitalTransfers.MAX_INTERSTELLAR_SPEED) <= total);
        }
        return ok;
    }

    // The stock tree from InterstellarAstronomicals.xml, when the loaded system has it.
    private static bool CheckStockInterstellar(CelestialSystem system, SystemGraph graph)
    {
        foreach ((string id, _) in StockRoots)
        {
            if (graph.Find(id) == null)
            {
                HarnessLog.Line($"{Tag} SKIP stock interstellar: system '{system.Id}' has several roots but no '{id}', so the stock interstellar bodies are not loaded.");
                return true;
            }
        }

        bool ok = true;
        PhysicalNode sol = graph.HomeRoot;
        ok &= Check("the home root is Sol, a fixed star", sol.Id == "Sol" && sol.Astro is FixedStar && sol.Role == BodyRole.Star);

        int previous = 0;
        foreach ((string id, _) in StockRoots)
        {
            int index = IndexOfRoot(graph, id);
            ok &= Check($"{id} follows the nearer roots", index > previous);
            previous = index;
        }

        PhysicalNode alpha = graph.Find("AlphaCentauri")!;
        ok &= Check("AlphaCentauri is a barycenter root", alpha.Astro is Barycenter && alpha.Role == BodyRole.Barycenter && alpha.IsSystemRoot);
        foreach (string id in new[] { "BarnardsStar", "TauCeti" })
        {
            PhysicalNode star = graph.Find(id)!;
            ok &= Check($"{id} is a fixed star root", star.Astro is FixedStar && star.Role == BodyRole.Star && star.IsSystemRoot);
        }

        var children = new HashSet<string>(alpha.Children.Select(c => c.Id));
        ok &= Check("AlphaCentauri holds exactly A, B and Proxima", children.SetEquals(AlphaCentauriStars));
        foreach (string id in AlphaCentauriStars)
        {
            PhysicalNode? star = graph.Find(id);
            ok &= Check($"{id} is an orbiting star under the barycenter",
                star != null && star.Astro is OrbitingStar && star.Role == BodyRole.Star && star.Parent?.Id == "AlphaCentauri");
        }

        foreach ((string id, string parent) in Exoplanets)
        {
            PhysicalNode? planet = graph.Find(id);
            ok &= Check($"{id} is a planet of {parent}",
                planet != null && planet.Astro is MinorBody && planet.Parent?.Id == parent
                && planet.Role == BodyRole.Body && !planet.IsMinor && !planet.IsMoon);
        }

        // Barycenter.MassOf counts only the stars authored as barycentric, A and B. Proxima is
        // authored as a two-body orbit and stays out.
        double a = graph.Find("AlphaCentauriA")!.Body.Mass;
        double b = graph.Find("AlphaCentauriB")!.Body.Mass;
        double proxima = graph.Find("ProximaCentauri")!.Body.Mass;
        ok &= Check("the barycenter mass is A plus B", Relative(alpha.Body.Mass, a + b) <= 1e-9);
        ok &= Check("the barycenter mass leaves Proxima out", Relative(alpha.Body.Mass, a + b + proxima) > 1e-6);

        PhysicalNode? earth = system.HomeBody != null ? graph.Find(system.HomeBody.Id) : null;
        if (earth == null)
            return Check("the home body is in the forest", false);

        foreach ((string id, double distance) in StockRoots)
        {
            InterstellarLeg leg = InterstellarLegs.Build(earth, graph.Find(id)!, InterstellarMath.AstronomicalUnit);
            ok &= Check(FormattableString.Invariant($"distance from {earth.Id} to {id} is {distance / LightYear:F6} ly"), Relative(leg.Distance, distance) <= 1e-9);
            ok &= Check($"capture radius at {id} is the stock 1 AU",
                InterstellarLegs.CaptureRadius(graph.Find(id)!, InterstellarMath.AstronomicalUnit) == InterstellarMath.AstronomicalUnit);
            ok &= Check($"departure from {earth.Id} to {id} climbs one plane-limited level",
                leg.DepartSteps.Length == 1 && leg.DepartSteps[0].AimSine.HasValue);
        }

        // A departure from a planet of an orbiting star climbs through that star into the
        // barycenter's well, and the coast starts at the star. Only the top level is plane-limited.
        PhysicalNode? proximaB = graph.Find("ProximaCentaurib");
        PhysicalNode? proximaStar = graph.Find("ProximaCentauri");
        if (proximaB != null && proximaStar != null)
        {
            InterstellarLeg back = InterstellarLegs.Build(proximaB, sol, InterstellarMath.AstronomicalUnit);
            double fromProxima = (sol.Astro.GetPositionEcl() - proximaStar.Astro.GetPositionEcl()).Length();
            ok &= Check("ProximaCentaurib to Sol starts at Proxima", Relative(back.Distance, fromProxima) <= 1e-12);
            ok &= Check("ProximaCentaurib to Sol climbs two levels, plane-limited at the top",
                back.DepartSteps.Length == 2 && back.DepartSteps[0].AimSine.HasValue && !back.DepartSteps[1].AimSine.HasValue);

            InterstellarLeg toPlanet = InterstellarLegs.Build(earth, proximaB, InterstellarMath.AstronomicalUnit);
            ok &= Check("a planet target is reached through its star", toPlanet.ArriveSteps.Length == 2);
        }
        return ok;
    }

    // Role and minor-body checks that hold in every system, and on the stock bodies where the
    // loaded system has them: Ceres stays minor beside the planets of Sol.
    private static bool CheckRoles(SystemGraph graph)
    {
        bool ok = true;
        foreach (PhysicalNode node in graph.AllNodes)
        {
            BodyRole expected = node.Astro is StellarBody ? BodyRole.Star
                : node.Astro is IIndependentRoot ? BodyRole.Barycenter
                : BodyRole.Body;
            if (node.Role != expected)
                ok &= Check($"{node.Id} has role {expected}", false);
        }
        ok &= CheckIf(graph, "Ceres", n => n.IsMinor, "stays a minor body");
        ok &= CheckIf(graph, "Luna", n => n.IsMoon, "is a moon");
        return ok;
    }

    private static bool CheckIf(SystemGraph graph, string id, Func<PhysicalNode, bool> rule, string what)
    {
        PhysicalNode? node = graph.Find(id);
        return node == null || Check($"{id} {what}", rule(node));
    }

    private static int IndexOfRoot(SystemGraph graph, string id)
    {
        for (int i = 0; i < graph.Roots.Count; i++)
        {
            if (graph.Roots[i].Id == id)
                return i;
        }
        return -1;
    }

    private static double Relative(double value, double expected)
    {
        return Math.Abs(value - expected) / Math.Abs(expected);
    }

    private static bool Check(string what, bool pass)
    {
        HarnessLog.Line($"{Tag} TEST {what} => {TestSupport.Verdict(pass)}");
        return pass;
    }
}
