using System;
using System.Collections.Generic;
using System.IO;
using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;

// Run the layout engine on the synthetic trees, assert no overlaps, and dump SVG and
// text so the layout can be eyeballed before any in-game rendering exists. Exit code
// is non-zero if any case fails its assertions or the node-count requirement, so this
// doubles as a CI-style gate.

string outDir = args.Length > 0 ? args[0] : DebugConfig.LayoutDumpDir;
try
{
    Directory.CreateDirectory(outDir);
}
catch (Exception ex)
{
    // The default is a local dev path; on another machine (or CI) fall back to a
    // portable directory so the gate still runs instead of crashing.
    Console.WriteLine($"Cannot use '{outDir}' ({ex.Message}); falling back to ./layout-dumps");
    outDir = Path.Combine(Directory.GetCurrentDirectory(), "layout-dumps");
    Directory.CreateDirectory(outDir);
}

Console.WriteLine($"Layout dumps -> {outDir}");
Console.WriteLine();

int failures = 0;

// The 100+ node stress tree: a moon root with a three-hub horizontal bus.
failures += RunCase(SyntheticTree.BuildLargeMoonRoot(), LayoutConfig.Default, "default", outDir, minNodes: 100);

// The same stress tree on a deliberately tight grid, to force grid-snap collisions
// and confirm the nudge pass still ends overlap-free. GridPx is the tightest the dot
// radii allow (the two largest must fit one cell), and BandHeightPx the smallest whole
// multiple of it that still clears MinSegmentPx.
var tightGrid = new LayoutConfig { GridPx = 35.0, BandHeightPx = 70.0, SiblingGapPx = 12.0, BusGapPx = 24.0 };
failures += RunCase(SyntheticTree.BuildLargeMoonRoot(), tightGrid, "tight", outDir, minNodes: 100);

// A stock-like planet root, for eyeballing realism.
failures += RunCase(SyntheticTree.BuildStockLikePlanetRoot(), LayoutConfig.Default, "default", outDir, minNodes: 0);

// The interplanetary-cruise root: a star-shaped hub bus rather than a chain.
failures += RunCase(SyntheticTree.BuildCruiseRoot(), LayoutConfig.Default, "default", outDir, minNodes: 0);

// A wide, shallow fan: dense single-band siblings stressing the grid-snap nudge.
failures += RunCase(SyntheticTree.BuildWideFan(), LayoutConfig.Default, "default", outDir, minNodes: 0);

// The gravity-well arrival model: capture anchor above low orbit for destinations.
failures += RunCase(SyntheticTree.BuildArrivalDemo(), LayoutConfig.Default, "default", outDir, minNodes: 0);

// A dense system after minor-body aggregation: a "+N" group node off the hub bus and off a
// deeper local hub. Exercises the synthetic MinorGroup node's layout in all three modes.
failures += RunCase(SyntheticTree.BuildDenseAggregated(), LayoutConfig.Default, "default", outDir, minNodes: 0);

// GravityWell mode (the in-game default): the same dense trees must stay overlap-free
// with the spine / well / bus assertions holding, on a fresh tree instance each time
// since the layout pass mutates node positions.
var gravityWell = new LayoutConfig { Mode = LayoutMode.GravityWell };
failures += RunCase(SyntheticTree.BuildLargeMoonRoot(), gravityWell, "gravitywell", outDir, minNodes: 100);
failures += RunCase(SyntheticTree.BuildWideFan(), gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildArrivalDemo(), gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildStockLikePlanetRoot(), gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildCruiseRoot(), gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildDenseAggregated(), gravityWell, "gravitywell", outDir, minNodes: 0);

// The realistic cruise root: planets attach to the star hub via HubLink onto their
// Intercept (above the spine). Exercises the GravityWell spine / well / relaxed-bus
// rules on the actual in-game cruise shape, in both modes.
failures += RunCase(SyntheticTree.BuildCruiseRootArrival(), LayoutConfig.Default, "default", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildCruiseRootArrival(), gravityWell, "gravitywell", outDir, minNodes: 0);

// Multiple star systems: a barycenter root with its stars on the bus, and a star of a multiple
// system as the root, climbing to its barycenter with the sibling stars branching off it.
failures += RunCase(SyntheticTree.BuildBarycenterRoot(), LayoutConfig.Default, "default", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildBarycenterRoot(), gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildOrbitingStarRoot(), LayoutConfig.Default, "default", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildOrbitingStarRoot(), gravityWell, "gravitywell", outDir, minNodes: 0);

// Spring (force-directed): the tree-shape invariants do not apply, but the settled web
// plus the grid snap must still leave no two dots (and no two labels) overlapping.
var spring = new LayoutConfig { Mode = LayoutMode.Spring };
failures += RunCase(SyntheticTree.BuildLargeMoonRoot(), spring, "spring", outDir, minNodes: 100);
failures += RunCase(SyntheticTree.BuildStockLikePlanetRoot(), spring, "spring", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildDenseAggregated(), spring, "spring", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildBarycenterRoot(), spring, "spring", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildOrbitingStarRoot(), spring, "spring", outDir, minNodes: 0);

// Large spring stress: exercises the Barnes-Hut repulsion and the iteration cap at a scale
// (~1800 nodes) an all-pairs loop could not handle in a frame, and confirms the grid snap still
// leaves it overlap-free. No timing assertion (it would be machine-dependent and flaky); the
// value is the overlap-free check at this scale.
failures += RunCase(SyntheticTree.BuildSpringStress(), spring, "spring", outDir, minNodes: 1500);

// The closed-form delta-v kernel (pure math, no game types) gets its own asserts.
failures += RunMathChecks();

// The closed-form transfer-window math (pure, no game types) gets its own asserts.
failures += RunTransferWindowChecks();

// The interstellar leg (departure chain, capture, coast, fastest cruise for a budget) and the
// rocket-equation propellant estimate are pure as well.
failures += RunInterstellarChecks();
failures += RunCaptureChecks();
failures += RunPropellantChecks();
failures += RunForestChecks();
failures += RunCruiseSpeedChecks();
failures += RunSceneChecks(outDir);
failures += RunFormatChecks();

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CASES PASS" : $"{failures} CASE(S) FAILED");
return failures == 0 ? 0 : 1;

static int RunCase(LayoutTree tree, LayoutConfig cfg, string variant, string outDir, int minNodes)
{
    LayoutResult result = LayoutEngine.Run(tree, cfg);
    OverlapReport report = OverlapCheck.Run(result);

    string tag = variant == "default" ? tree.Name : tree.Name + "-" + variant;
    Console.WriteLine($"[{tag}] {report.Summary()}");
    PrintIssues("node", report.NodeOverlaps);
    PrintIssues("subtree", report.SubtreeOverlaps);
    PrintIssues("label", report.LabelOverlaps);
    PrintIssues("band", report.BandViolations);
    PrintIssues("bus", report.BusViolations);

    File.WriteAllText(Path.Combine(outDir, tag + ".svg"), LayoutDumpFormat.ToSvg(result));
    File.WriteAllText(Path.Combine(outDir, tag + ".txt"), LayoutDumpFormat.ToText(result));

    bool nodeCountOk = tree.Nodes.Count >= minNodes;
    if (!nodeCountOk)
        Console.WriteLine($"  FAIL: {tree.Nodes.Count} nodes < required {minNodes}");

    return report.Ok && nodeCountOk ? 0 : 1;
}

static void PrintIssues(string kind, List<string> issues)
{
    foreach (string issue in issues)
        Console.WriteLine($"  {kind}: {issue}");
}

// Assertions on the closed-form delta-v formulas, run offline (no game state). Locks the
// comet velocity-match generalization, that it reduces to the exact Hohmann for two bound
// endpoints, the intercept capture/circularize split identity, and the atmospheric landing
// fraction across the stock bodies' density range.
static int RunMathChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[math] delta-v engine checks");

    // ConicTransfer must reduce exactly to Hohmann for two bound endpoints, so the verified
    // planet-to-planet numbers do not move when comets route through the generalized form.
    {
        double mu = 1.327e20;          // ~solar gravitational parameter
        double r1 = 1.496e11;          // Earth's orbit
        double r2 = 2.279e11;          // Mars's orbit
        DeltaVCalculator.Hohmann(mu, r1, r2, out double hd, out double ha);
        DeltaVCalculator.ConicTransfer(mu, r1, false, 0.0, r2, false, 0.0, out double td, out double ta);
        fails += Approx("ConicTransfer depart == Hohmann", td, hd, 1e-6);
        fails += Approx("ConicTransfer arrive == Hohmann", ta, ha, 1e-6);
    }

    // PeriapsisSpeed: circular at e=0, escape (sqrt(2) circular) at e=1.
    {
        double mu = 3.986e14;
        double r = 7.0e6;
        double vc = DeltaVCalculator.CircularSpeed(mu, r);
        fails += Approx("PeriapsisSpeed e=0 is circular", DeltaVCalculator.PeriapsisSpeed(mu, r, 0.0), vc, 1e-9);
        fails += Approx("PeriapsisSpeed e=1 is escape", DeltaVCalculator.PeriapsisSpeed(mu, r, 1.0), Math.Sqrt(2.0) * vc, 1e-9);
    }

    // A comet (open orbit) at perihelion moves far faster than circular, so its velocity
    // match must cost strictly more than a circular-Hohmann arrival at the same radius.
    {
        double mu = 1.327e20;
        double r1 = 1.496e11;
        double rp = 0.9e11;
        double e = 3.0;
        DeltaVCalculator.Hohmann(mu, r1, rp, out _, out double circArrive);
        DeltaVCalculator.ConicTransfer(mu, r1, false, 0.0, rp, true, e, out _, out double openArrive);
        fails += Check("comet arrival dearer than circular", openArrive > circArrive);
    }

    // Intercept split: the loose Oberth capture into the SOI-edge ellipse plus the
    // circularize down to low orbit equals one Oberth capture straight to low orbit, so the
    // route never double-counts and the badge agrees with the breakdown.
    {
        double mu = 4.9e12;
        double rLo = 2.0e6;
        double rSoi = 6.6e7;
        double vInf = 900.0;
        double full = DeltaVCalculator.OberthBurn(mu, rLo, vInf);
        double circularize = DeltaVCalculator.EscapeToSoi(mu, rLo, rSoi);
        double vBurn = Math.Sqrt(vInf * vInf + 2.0 * mu / rLo);
        double vEllipsePeri = DeltaVCalculator.VisVivaSpeed(mu, rLo, (rLo + rSoi) / 2.0);
        double looseCapture = vBurn - vEllipsePeri;
        fails += Approx("loose capture + circularize == full capture", looseCapture + circularize, full, 1e-6);
    }

    // Atmospheric landing fraction: bounded, and a thin atmosphere (Mars) costs more than a
    // thick one (Venus), with Earth between. Values cross-checked against the stock bodies.
    {
        double mars = DeltaVCalculator.AtmosphericLandingFraction(0.02 / 1.225);
        double earth = DeltaVCalculator.AtmosphericLandingFraction(1.0);
        double venus = DeltaVCalculator.AtmosphericLandingFraction(65.0 / 1.225);
        fails += Check("landing fraction within clamp", mars <= 0.35 && venus >= 0.06);
        fails += Check("thin atmosphere costs more than thick", mars > earth && earth > venus);
        fails += Approx("Mars landing fraction near first-cut value", mars, 0.2119, 0.02);
    }

    // A transfer between bodies moving at their own speeds reduces to Hohmann when both move at
    // the hub's circular speed, and moves away from it when one body has its own central mass.
    {
        double mu = 1.327e20;
        double r1 = 1.496e11;
        double r2 = 2.279e11;
        DeltaVCalculator.Hohmann(mu, r1, r2, out double hd, out double ha);
        DeltaVCalculator.TransferBetweenSpeeds(mu, r1, DeltaVCalculator.CircularSpeed(mu, r1),
            r2, DeltaVCalculator.CircularSpeed(mu, r2), out double sd, out double sa);
        fails += Approx("TransferBetweenSpeeds depart == Hohmann", sd, hd, 1e-12);
        fails += Approx("TransferBetweenSpeeds arrive == Hohmann", sa, ha, 1e-12);

        DeltaVCalculator.TransferBetweenSpeeds(mu, r1, DeltaVCalculator.CircularSpeed(0.1 * mu, r1),
            r2, DeltaVCalculator.CircularSpeed(mu, r2), out double slowDepart, out _);
        fails += Check("a slower own-mass body pays more to depart", slowDepart > hd);
    }

    Console.WriteLine(fails == 0 ? "  math: OK" : $"  math: {fails} FAIL");
    return fails;
}

// Assertions on the interstellar kernel, run offline. The reference values come from the stock
// content: Sol at the origin (1 Msun), Earth's orbit, a 200 km Earth parking orbit, and the Alpha
// Centauri barycenter (A + B = 1.988 Msun, the mass the game captures around) at its authored
// light-year position. The numbers were evaluated independently from the closed forms (escape
// speed at Earth's orbit, minus Earth's speed, then the Oberth burn at the parking orbit; circular
// capture at 1 AU), so a sign or ordering slip in the fold fails here.
static int RunInterstellarChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[interstellar] interstellar leg checks");

    const double G = 6.6743e-11;
    const double sun = 1.98841e30;
    const double c = InterstellarMath.SpeedOfLight;
    const double au = InterstellarMath.AstronomicalUnit;
    const double julianYear = 365.25 * 86400.0;
    double muSol = sun * G;
    double aEarth = 1.495396277103892e11;
    double vEarth = Math.Sqrt(muSol / aEarth);
    double muEarth = 3.9860187717e14;
    double rLeo = 6.571e6;
    double muAlphaCen = (1.0788 + 0.9092) * sun * G;
    double lightYear = 9.461e15;
    double distance = Math.Sqrt(1.6240716 * 1.6240716 + 2.7548531 * 2.7548531 + 2.9400738 * 2.9400738) * lightYear;
    // The sine of the aim's angle out of the ecliptic, Earth's orbital plane.
    double aimSine = 2.9400738 * lightYear / distance;

    var leg = new InterstellarLeg
    {
        DepartSteps = new[] { new WellStep(muSol, aEarth, vEarth) },
        ArriveSteps = Array.Empty<WellStep>(),
        ArrivePark = ParkingOrbit.Circular(muAlphaCen, au),
        Distance = distance
    };
    ParkingOrbit leo = ParkingOrbit.Circular(muEarth, rLeo);

    fails += Approx("Sol to Alpha Centauri distance", distance, 4.109915256421932e16, 1e-12);

    // Earth LEO to Alpha Centauri at three cruise speeds: departure, capture, coast.
    {
        double v = 0.001 * c;
        fails += Approx("0.001c departure", leg.DepartBurn(v, in leo), 265381.43583, 1e-9);
        fails += Approx("0.001c capture", leg.CaptureBurn(v), 263623.25546, 1e-9);
        fails += Approx("0.001c coast (Julian yr)", InterstellarMath.CoastSeconds(distance, v) / julianYear, 4344.1839, 1e-7);
    }
    {
        double v = 0.01 * c;
        fails += Approx("0.01c departure", leg.DepartBurn(v, in leo), 2960662.03535, 1e-9);
        fails += Approx("0.01c capture", leg.CaptureBurn(v), 2956517.43873, 1e-9);
        fails += Approx("0.01c total", leg.TotalDv(v, in leo), 5917179.47409, 1e-9);
        fails += Approx("0.01c coast (Julian yr)", InterstellarMath.CoastSeconds(distance, v) / julianYear, 434.41839, 1e-7);
    }
    {
        double v = 0.1 * c;
        fails += Approx("0.1c departure", leg.DepartBurn(v, in leo), 29941698.43381, 1e-9);
        fails += Approx("0.1c capture", leg.CaptureBurn(v), 29937309.26701, 1e-9);
    }
    {
        double v = 100000.0;
        fails += Approx("100 km/s departure", leg.DepartBurn(v, in leo), 71700.26795, 1e-9);
        fails += Approx("100 km/s capture", leg.CaptureBurn(v), 74311.21970, 1e-9);
    }

    // The cheapest trip, at a cruise speed going to zero: Earth's excess is (sqrt(2) - 1) times
    // its orbital speed, and the capture is (sqrt(2) - 1) times the circular speed at 1 AU, the
    // stock planner's CIRCULAR SPEED AT PERIAPSIS readout.
    {
        fails += Approx("cheapest departure", leg.DepartBurn(0.0, in leo), 8751.99066, 1e-9);
        fails += Approx("cheapest capture", leg.CaptureBurn(0.0), (Math.Sqrt(2.0) - 1.0) * Math.Sqrt(muAlphaCen / au), 1e-12);
        fails += Approx("circular speed at 1 AU", Math.Sqrt(muAlphaCen / au), 41995.36064, 1e-9);
        fails += Approx("cheapest total", leg.MinimumDv(in leo, 0.9 * c), 26147.03860, 1e-9);
        fails += Approx("excess at Earth for v -> 0",
            InterstellarMath.ExcessAtBody(0.0, new WellStep(muSol, aEarth, vEarth)), (Math.Sqrt(2.0) - 1.0) * vEarth, 1e-12);
    }

    // The inverse problem: the fastest cruise a budget pays for, and that speed round-trips.
    {
        double cap = 0.9 * c;
        double v50 = leg.MaxCruiseSpeed(50000.0, in leo, cap);
        double v100 = leg.MaxCruiseSpeed(100000.0, in leo, cap);
        fails += Approx("fastest cruise for 50 km/s", v50, 38144.22993, 1e-7);
        fails += Approx("fastest cruise for 100 km/s", v100, 73049.19403, 1e-7);
        fails += Approx("fastest cruise round trip", leg.TotalDv(v100, in leo), 100000.0, 1e-7);
        fails += Check("a budget below the cheapest trip reaches nothing", leg.MaxCruiseSpeed(20000.0, in leo, cap) == 0.0);
        fails += Check("a huge budget is capped", leg.MaxCruiseSpeed(1e12, in leo, cap) == cap);
    }

    // Identities of the fold: one level from a circular orbit equals the Oberth burn, a capture
    // with no mass is the excess itself, an empty chain parks around the root, the plane limit
    // costs more than full alignment, and the cost rises with speed.
    {
        double v = 50000.0;
        double vLevel = InterstellarMath.ExcessAtBody(v, new WellStep(muSol, aEarth, vEarth));
        fails += Approx("chain of one == Oberth burn", leg.DepartBurn(v, in leo), DeltaVCalculator.OberthBurn(muEarth, rLeo, vLevel), 1e-12);
        fails += Approx("capture with no mass == v_inf",
            InterstellarMath.ChainBurn(v, ReadOnlySpan<WellStep>.Empty, new ParkingOrbit(0.0, au, 0.0)), v, 1e-12);
        fails += Approx("empty chain == Oberth at the root",
            InterstellarMath.ChainBurn(v, ReadOnlySpan<WellStep>.Empty, ParkingOrbit.Circular(muSol, au)), DeltaVCalculator.OberthBurn(muSol, au, v), 1e-12);

        var tilted = new WellStep(muSol, aEarth, vEarth, aimSine);
        double plane = InterstellarMath.ChainBurn(100000.0, new[] { tilted }, in leo);
        fails += Check("plane-limited departure costs more", plane > leg.DepartBurn(100000.0, in leo));
        fails += Approx("an aim in the plane is the free case",
            InterstellarMath.ExcessAtBody(v, new WellStep(muSol, aEarth, vEarth, 0.0)), vLevel, 1e-12);

        double previous = leg.TotalDv(0.0, in leo);
        bool rising = true;
        for (double speed = 1000.0; speed < 0.9 * c; speed *= 1.7)
        {
            double total = leg.TotalDv(speed, in leo);
            rising &= total > previous;
            previous = total;
        }
        fails += Check("total rises with cruise speed", rising);
        fails += Approx("departure tends to v_inf - v_Earth", leg.DepartBurn(0.1 * c, in leo), 0.1 * c - vEarth, 1e-3);
    }

    // The plane limit with the escape hyperbola bent by the Sun. The references come from a
    // separate brute-force search: for each departure point on Earth's orbit, the flight-path
    // angle whose Kepler hyperbola leaves along the aim (from the eccentricity vector), then the
    // best point. At high speed the hyperbola hardly bends and the result tends to the cosine of
    // the aim's angle; at low speed the bend makes the departure dearer than that, and a few km/s
    // of cruise speed make it cheaper again, so the cheapest trip is not at zero speed.
    {
        var tilted = new WellStep(muSol, aEarth, vEarth, aimSine);
        var planeLeg = new InterstellarLeg
        {
            DepartSteps = new[] { tilted },
            ArriveSteps = Array.Empty<WellStep>(),
            ArrivePark = ParkingOrbit.Circular(muAlphaCen, au),
            Distance = distance
        };
        fails += Approx("plane-limited excess at 1 km/s", InterstellarMath.ExcessAtBody(1000.0, tilted), 34878.56900, 1e-8);
        fails += Approx("plane-limited excess at 5 km/s", InterstellarMath.ExcessAtBody(5000.0, tilted), 34023.37783, 1e-8);
        fails += Approx("plane-limited excess at 100 km/s", InterstellarMath.ExcessAtBody(100000.0, tilted), 88936.28606, 1e-8);
        fails += Approx("plane-limited departure at 100 km/s", planeLeg.DepartBurn(100000.0, in leo), 81827.25836, 1e-8);

        double vFast = 0.1 * c;
        double cosAim = Math.Sqrt(1.0 - aimSine * aimSine);
        fails += Approx("fast escape tends to the plane cosine",
            InterstellarMath.BestSpeedAlongMotion(muSol, aEarth, vFast, aimSine) / InterstellarMath.SpeedForExcess(muSol, aEarth, vFast), cosAim, 1e-4);
        fails += Approx("an aim along the normal has no component along the motion",
            InterstellarMath.BestSpeedAlongMotion(muSol, aEarth, 1000.0, 1.0), 0.0, 1e-12);
        fails += Approx("no well moves straight along the aim",
            InterstellarMath.BestSpeedAlongMotion(0.0, aEarth, 1000.0, aimSine), 1000.0 * cosAim, 1e-12);
        double vSlow = InterstellarMath.SpeedForExcess(muSol, aEarth, 1000.0);
        fails += Check("a slow escape bends past the cosine bound",
            InterstellarMath.ExcessAtBody(1000.0, tilted) > InterstellarMath.RelativeExcess(vSlow, vEarth, vSlow * cosAim));

        double cap = 0.9 * c;
        double cheapest = planeLeg.CheapestSpeed(in leo, cap);
        double minimum = planeLeg.MinimumDv(in leo, cap);
        fails += Check("the departure falls from 1 to 5 km/s", planeLeg.DepartBurn(5000.0, in leo) < planeLeg.DepartBurn(1000.0, in leo));
        fails += Check("the cheapest trip needs some cruise speed", cheapest > 1000.0 && minimum < planeLeg.TotalDv(0.0, in leo));
        fails += Check("the cheapest trip is a minimum",
            minimum <= planeLeg.TotalDv(0.98 * cheapest, in leo) && minimum <= planeLeg.TotalDv(1.02 * cheapest, in leo));
        Console.WriteLine(FormattableString.Invariant($"  plane-limited cheapest trip {minimum:F3} m/s at {cheapest:F3} m/s"));

        double v60 = planeLeg.MaxCruiseSpeed(60000.0, in leo, cap);
        fails += Check("fastest cruise is on the rising branch", v60 > cheapest);
        fails += Approx("plane-limited fastest cruise round trip", planeLeg.TotalDv(v60, in leo), 60000.0, 1e-7);
        fails += Check("a budget below the plane-limited cheapest trip reaches nothing", planeLeg.MaxCruiseSpeed(0.99 * minimum, in leo, cap) == 0.0);
        fails += Check("a given cheapest speed gives the same fastest cruise",
            planeLeg.MaxCruiseSpeed(60000.0, in leo, cap, cheapest) == v60
            && planeLeg.MaxCruiseSpeed(0.99 * minimum, in leo, cap, cheapest) == 0.0
            && planeLeg.MaxCruiseSpeed(1e12, in leo, cap, cheapest) == planeLeg.MaxCruiseSpeed(1e12, in leo, cap));
    }

    // A vessel that orbits the root directly on its own orbit: with an aim in that plane the burn
    // is the plain Oberth burn at the root, and out of the plane it is the plane-limited excess.
    {
        double v = 50000.0;
        ParkingOrbit own = ParkingOrbit.Circular(muSol, au);
        fails += Approx("own orbit in the aim's plane",
            InterstellarMath.ChainBurn(v, ReadOnlySpan<WellStep>.Empty, own with { AimSine = 0.0 }),
            InterstellarMath.ChainBurn(v, ReadOnlySpan<WellStep>.Empty, in own), 1e-12);
        fails += Approx("own orbit out of the aim's plane",
            InterstellarMath.ChainBurn(v, ReadOnlySpan<WellStep>.Empty, own with { AimSine = aimSine }),
            InterstellarMath.ExcessAtBody(v, new WellStep(muSol, au, own.Speed, aimSine)), 1e-12);
    }

    // A departure body on an open orbit already moves faster than the escape. When its excess is
    // large and the capture well deep, the cost first falls with the cruise speed. The cheapest
    // trip and the inverse problem still hold.
    {
        var fast = new WellStep(muSol, aEarth, 2.5 * vEarth);
        var openLeg = new InterstellarLeg
        {
            DepartSteps = new[] { fast },
            ArriveSteps = Array.Empty<WellStep>(),
            ArrivePark = ParkingOrbit.Circular(muAlphaCen, au),
            Distance = distance
        };
        double cap = 0.9 * c;
        double cheapest = openLeg.CheapestSpeed(in leo, cap);
        double minimum = openLeg.MinimumDv(in leo, cap);
        fails += Check("an open departure body is cheapest above zero speed", cheapest > 0.0 && minimum < openLeg.TotalDv(0.0, in leo));
        double budget = 0.5 * (minimum + openLeg.TotalDv(0.0, in leo));
        double vMax = openLeg.MaxCruiseSpeed(budget, in leo, cap);
        fails += Check("an open departure body pays for a cruise below its zero-speed cost", vMax > cheapest);
        fails += Approx("open departure fastest cruise round trip", openLeg.TotalDv(vMax, in leo), budget, 1e-7);
    }

    // A two-level chain, from a low Luna orbit: Sol, then Earth's well at Luna's distance.
    {
        double muLuna = 7.349e22 * G;
        double aLuna = 3.808633349807188e8;
        double vLuna = Math.Sqrt(muEarth / aLuna);
        WellStep[] chain = { new WellStep(muSol, aEarth, vEarth), new WellStep(muEarth, aLuna, vLuna) };
        ParkingOrbit llo = ParkingOrbit.Circular(muLuna, 1.8371e6);
        fails += Approx("Luna departure, v -> 0", InterstellarMath.ChainBurn(0.0, chain, in llo), 9998.95891, 1e-9);
        fails += Approx("Luna departure, 0.001c", InterstellarMath.ChainBurn(0.001 * c, chain, in llo), 270304.42104, 1e-9);
    }

    // An eccentric parking orbit burns at periapsis from the periapsis speed.
    {
        double pe = 6.571e6;
        double e = 0.3;
        var ellipse = new ParkingOrbit(muEarth, pe, DeltaVCalculator.PeriapsisSpeed(muEarth, pe, e));
        fails += Check("an eccentric park departs cheaper", leg.DepartBurn(50000.0, in ellipse) < leg.DepartBurn(50000.0, in leo));
    }

    Console.WriteLine(fails == 0 ? "  interstellar: OK" : $"  interstellar: {fails} FAIL");
    return fails;
}

// Assertions on the arrival chain with the coast plane correction, from the stock Barnard's Star
// and Proxima Centauri content: Barnard b (0.0229 AU around a 0.162 Msun star, 4550 km radius,
// 0.299 Earth masses) and Proxima b (0.0485 AU around 0.1221 Msun, itself 8600 AU from the
// Alpha Centauri barycenter of A + B, 7000 km, 1.07 Earth masses), both captured into their
// 1.05 R low orbit. The references are hand-checkable: at v_inf -> 0 an arrival has no
// direction, so the capture is the free chain ((sqrt(2) - 1) times the planet's speed, then the
// Oberth burn); at 0.01 c a correction never pays, so it is the plain plane-limited chain.
static int RunCaptureChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[capture] arrival chain with coast plane correction");

    const double G = 6.6743e-11;
    const double sun = 1.98841e30;
    const double earthMass = 5.97219e24;
    const double c = InterstellarMath.SpeedOfLight;
    const double au = InterstellarMath.AstronomicalUnit;
    double cap = 0.9 * c;

    double muB = 0.162 * sun * G;
    double aB = 0.0229 * au;
    double vB = Math.Sqrt(muB / aB);
    double sineB = 0.471496;
    var planeB = new WellStep(muB, aB, vB, sineB);
    var freeB = new WellStep(muB, aB, vB);
    var parkB = ParkingOrbit.Circular(0.299 * earthMass * G, 1.05 * 4550e3);
    WellStep[] chainB = { planeB };
    double[] speeds = { 0.0, 10000.0, 30000.0, 100000.0, 0.001 * c, 0.01 * c, 0.1 * c };

    // (a) The free case at v -> 0, and the plain chain once the arrival is fast.
    fails += Approx("Barnard b at v = 0 is the free chain", InterstellarMath.CaptureChainBurn(0.0, chainB, in parkB), 28570.8, 0.5 / 28570.8);
    fails += Approx("Barnard b at 1 m/s swings into the plane", InterstellarMath.CaptureChainBurn(1.0, chainB, in parkB), 28570.8, 2.0 / 28570.8);
    fails += Approx("Barnard b free chain at v = 0", InterstellarMath.ChainBurn(0.0, new[] { freeB }, in parkB), 28570.8, 0.5 / 28570.8);
    fails += Approx("Barnard b at 0.01c is the plain chain", InterstellarMath.CaptureChainBurn(0.01 * c, chainB, in parkB),
        InterstellarMath.ChainBurn(0.01 * c, chainB, in parkB), 1e-12);
    fails += Approx("Barnard b plain chain at 0.01c", InterstellarMath.ChainBurn(0.01 * c, chainB, in parkB), 2925407.9, 0.5 / 2925407.9);

    // (b) Never above the plain plane-limited chain, and (c) never above parking at the star
    // 1 AU out and dropping in by a Hohmann with the Oberth capture at b.
    DeltaVCalculator.Hohmann(muB, au, aB, out double inwardDepart, out double inwardArrive);
    double inward = inwardDepart + DeltaVCalculator.OberthBurn(parkB.Mu, parkB.Radius, inwardArrive);
    fails += Approx("inward Hohmann from 1 AU to Barnard b", inward, 36790.4, 0.5 / 36790.4);
    var star1Au = ParkingOrbit.Circular(muB, au);
    fails += Approx("Barnard star 1 AU capture at v = 0", InterstellarMath.ChainBurn(0.0, ReadOnlySpan<WellStep>.Empty, in star1Au),
        (Math.Sqrt(2.0) - 1.0) * Math.Sqrt(muB / au), 1e-12);
    bool belowPlain = true;
    bool belowPark = true;
    foreach (double v in speeds)
    {
        double corrected = InterstellarMath.CaptureChainBurn(v, chainB, in parkB);
        belowPlain &= corrected <= InterstellarMath.ChainBurn(v, chainB, in parkB) + 1e-9;
        belowPark &= corrected <= InterstellarMath.ChainBurn(v, ReadOnlySpan<WellStep>.Empty, in star1Au) + inward;
    }
    fails += Check("the corrected capture is never above the plain chain", belowPlain);
    fails += Check("the corrected capture is never above the star park plus inward Hohmann", belowPark);

    // (d) Proxima b: both levels plane-limited and corrected.
    {
        double muAB = (1.0788 + 0.9092) * sun * G;
        double muP = 0.1221 * sun * G;
        double aP = 8600 * au;
        double vP = Math.Sqrt((1.0788 + 0.9092 + 0.1221) * sun * G / aP);
        double ab = 0.0485 * au;
        double vb = Math.Sqrt(muP / ab);
        double sine = -0.676803;
        WellStep[] chain = { new WellStep(muAB, aP, vP, sine), new WellStep(muP, ab, vb, sine, sharesOuterPlane: true) };
        var park = ParkingOrbit.Circular(1.07 * earthMass * G, 1.05 * 7000e3);
        fails += Approx("Proxima b at v = 0", InterstellarMath.CaptureChainBurn(0.0, chain, in park), 14726.0, 2.0 / 14726.0);
        fails += Approx("Proxima b at 0.1c is the plain chain", InterstellarMath.CaptureChainBurn(0.1 * c, chain, in park),
            InterstellarMath.ChainBurn(0.1 * c, chain, in park), 1e-12);
        bool proximaBelowPlain = true;
        foreach (double v in speeds)
            proximaBelowPlain &= InterstellarMath.CaptureChainBurn(v, chain, in park) <= InterstellarMath.ChainBurn(v, chain, in park) + 1e-9;
        fails += Check("Proxima b is never above the plain chain", proximaBelowPlain);
        foreach (double v in new[] { 10000.0, 30000.0 })
            Console.WriteLine(FormattableString.Invariant($"  Proxima b capture at {v / 1000.0:F0} km/s: {InterstellarMath.CaptureChainBurn(v, chain, in park):F1} m/s"));

        // Levels in different planes: each keeps its own sine, so without a correction the result
        // is the plain chain, never above it, and no jump appears where the outer sine reaches 0.
        foreach ((double outer, double inner) in new[] { (0.9, 0.05), (0.05, 0.9), (0.4, -0.7) })
        {
            WellStep[] tilted = { new WellStep(muAB, aP, vP, outer), new WellStep(muP, ab, vb, inner) };
            bool notAbove = true;
            foreach (double v in speeds)
                notAbove &= InterstellarMath.CaptureChainBurn(v, tilted, in park) <= InterstellarMath.ChainBurn(v, tilted, in park) + 1e-6;
            fails += Check(FormattableString.Invariant($"planes {outer}/{inner}: never above the plain chain"), notAbove);
            fails += Approx(FormattableString.Invariant($"planes {outer}/{inner}: the plain chain at 0.1c"),
                InterstellarMath.CaptureChainBurn(0.1 * c, tilted, in park), InterstellarMath.ChainBurn(0.1 * c, tilted, in park), 1e-12);
        }
        WellStep[] flatOuter = { new WellStep(muAB, aP, vP, 0.0), new WellStep(muP, ab, vb, 0.7) };
        WellStep[] nearlyFlatOuter = { new WellStep(muAB, aP, vP, 1e-9), new WellStep(muP, ab, vb, 0.7) };
        fails += Approx("an outer sine of 0 and of 1e-9 give the same capture",
            InterstellarMath.CaptureChainBurn(10000.0, flatOuter, in park), InterstellarMath.CaptureChainBurn(10000.0, nearlyFlatOuter, in park), 1e-6);
    }

    // (e) Without a plane limit on any level the correction has nothing to do.
    {
        WellStep[] free = { new WellStep(muB, aB, vB), new WellStep(muB * 0.01, aB * 0.1, vB * 0.3) };
        bool same = true;
        foreach (double v in speeds)
            same &= InterstellarMath.CaptureChainBurn(v, free, in parkB) == InterstellarMath.ChainBurn(v, free, in parkB);
        fails += Check("no plane limit gives exactly the plain chain", same);
        fails += Check("an empty chain gives exactly the plain chain",
            InterstellarMath.CaptureChainBurn(5000.0, ReadOnlySpan<WellStep>.Empty, in star1Au) == InterstellarMath.ChainBurn(5000.0, ReadOnlySpan<WellStep>.Empty, in star1Au));
    }

    // (f) A whole Barnard b leg from a low Earth orbit with the plane-limited departure.
    {
        double muSol = sun * G;
        double aEarth = 1.495396277103892e11;
        double vEarth = Math.Sqrt(muSol / aEarth);
        var leo = ParkingOrbit.Circular(3.9860187717e14, 6.571e6);
        var leg = new InterstellarLeg
        {
            DepartSteps = new[] { new WellStep(muSol, aEarth, vEarth, sineB) },
            ArriveSteps = chainB,
            ArrivePark = parkB,
            Distance = 5.641502e16
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double cheapest = leg.CheapestSpeed(in leo, cap);
        double firstMs = watch.Elapsed.TotalMilliseconds;
        // A burn moves the departure park, which reruns both searches on the built table.
        var burning = new ParkingOrbit(leo.Mu, leo.Radius, leo.Speed + 1.0);
        watch.Restart();
        double burningCheapest = leg.CheapestSpeed(in burning, cap);
        leg.MaxCruiseSpeed(100000.0, in burning, cap, burningCheapest);
        double rerunMs = watch.Elapsed.TotalMilliseconds;
        double minimum = leg.MinimumDv(in leo, cap);
        double v100 = leg.MaxCruiseSpeed(100000.0, in leo, cap, cheapest);
        watch.Stop();
        fails += Check("the Barnard b cheapest trip is a minimum",
            minimum <= leg.TotalDv(0.98 * cheapest, in leo) + 1e-9 && minimum <= leg.TotalDv(1.02 * cheapest + 1.0, in leo) + 1e-9);
        fails += Approx("Barnard b fastest cruise round trip", leg.TotalDv(v100, in leo), 100000.0, 1e-7);
        fails += Check("a budget below the Barnard b cheapest trip reaches nothing", leg.MaxCruiseSpeed(0.99 * minimum, in leo, cap) == 0.0);
        Console.WriteLine(FormattableString.Invariant(
            $"  Barnard b leg: cheapest {minimum:F1} m/s at {cheapest:F1} m/s, fastest cruise for 100 km/s {v100:F1} m/s, first search {firstMs:F2} ms, rerun {rerunMs:F2} ms"));

        // The speed searches read the capture from a table; it stays close to the exact capture.
        double worst = 0.0;
        bool finite = true;
        for (double v = 0.5; v < cap; v = Math.Min(cap, v * 1.037))
        {
            double search = leg.SearchCapture(v);
            finite &= double.IsFinite(search);
            worst = Math.Max(worst, Math.Abs(search - leg.CaptureBurn(v)) / leg.CaptureBurn(v));
        }
        finite &= double.IsFinite(leg.SearchCapture(cap)) && double.IsFinite(leg.SearchCapture(0.8955 * c));
        fails += Check(FormattableString.Invariant($"the search table is within 0.5 % of the exact capture ({worst:E1})"), worst < 5e-3);
        fails += Check("the search table is finite up to the cap", finite);
        fails += Check("the search table is exact at zero speed", leg.SearchCapture(0.0) == leg.CaptureBurn(0.0));
    }

    // (g) The measured excess of an open orbit, and (h) the budget and total folds.
    {
        double mu = 3.986004418e14;
        double pe = 7.0e6;
        fails += Approx("hyperbolic excess", InterstellarMath.HyperbolicExcess(mu, pe, 1.5), Math.Sqrt(mu * 0.5 / pe), 1e-12);
        fails += Check("a parabola has no excess", InterstellarMath.HyperbolicExcess(mu, pe, 1.0) == 0.0);
        fails += Check("a bound orbit has no excess", InterstellarMath.HyperbolicExcess(mu, pe, 0.3) == 0.0);
        fails += Approx("leg budget", InterstellarMath.LegBudget(12000.0, 1.2, 4000.0), 6000.0, 1e-12);
        fails += Check("leg budget clamps at zero", InterstellarMath.LegBudget(1000.0, 1.0, 4000.0) == 0.0);
        fails += Approx("route total", InterstellarMath.RouteTotal(1000.0, 2000.0, 3000.0, 500.0, 1.1), 5500.0 * 1.1, 1e-12);
    }

    // (i) The outgoing asymptote of an open orbit and the turn of an excess toward a new aim.
    {
        // mu = 1, periapsis at (1, 0, 0) moving at 2 along y: e = 3, so the asymptote lies at the
        // true anomaly with cosine -1/3.
        bool open = InterstellarMath.OutgoingAsymptote(1.0, (1.0, 0.0, 0.0), (0.0, 2.0, 0.0), out (double X, double Y, double Z) u);
        fails += Check("an open orbit has an outgoing asymptote", open);
        fails += Approx("asymptote x", u.X, -1.0 / 3.0, 1e-12);
        fails += Approx("asymptote y", u.Y, Math.Sqrt(8.0) / 3.0, 1e-12);
        fails += Check("asymptote z", Math.Abs(u.Z) < 1e-15);
        // The same orbit at the true anomaly 90 degrees: h = 2 and p = h^2 / mu = 4, so the vessel
        // sits at (0, 4, 0) and moves at (mu / h) (-sin, e + cos) = (-0.5, 1.5, 0). The asymptote is
        // a constant of the orbit.
        bool later = InterstellarMath.OutgoingAsymptote(1.0, (0.0, 4.0, 0.0), (-0.5, 1.5, 0.0), out (double X, double Y, double Z) u2);
        fails += Check("the asymptote is a constant of the orbit", later && Math.Abs(u2.X - u.X) < 1e-12 && Math.Abs(u2.Y - u.Y) < 1e-12);
        fails += Check("a bound orbit has no asymptote", !InterstellarMath.OutgoingAsymptote(1.0, (1.0, 0.0, 0.0), (0.0, 1.2, 0.0), out _));
        fails += Check("no turn costs nothing", InterstellarMath.CourseChangeBurn(30000.0, 1.0) == 0.0);
        fails += Approx("a reversal costs twice the excess", InterstellarMath.CourseChangeBurn(30000.0, -1.0), 60000.0, 1e-12);
        fails += Approx("a right angle costs sqrt(2) times the excess", InterstellarMath.CourseChangeBurn(30000.0, 0.0), 30000.0 * Math.Sqrt(2.0), 1e-12);
    }

    Console.WriteLine(fails == 0 ? "  capture: OK" : $"  capture: {fails} FAIL");
    return fails;
}

// Assertions on the cruise-speed selection: MAX stays selected and follows a moving budget, falls
// back to the cheapest speed when the budget cannot pay for it, and gives way to a custom speed
// when the vessel has no MAX at all. A preset never moves with the budget.
static int RunCruiseSpeedChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[cruise] cruise-speed selection");
    double c = InterstellarMath.SpeedOfLight;
    double cap = 0.9 * c;

    var speed = new CruiseSpeed();
    fails += Check("the default is the 0.01 c preset", speed.Mode == SpeedMode.Preset && speed.Resolve(0.0, 0.0, cap).Speed == 0.01 * c);

    speed.SelectMax();
    fails += Check("MAX follows the budget", speed.Resolve(40000.0, 5000.0, cap).Speed == 40000.0);
    fails += Check("MAX follows a falling budget and stays selected", speed.Resolve(35000.0, 5000.0, cap).Speed == 35000.0 && speed.Mode == SpeedMode.Max);
    CruiseChoice poor = speed.Resolve(0.0, 5000.0, cap);
    fails += Check("MAX without the cheapest trip shows the cheapest speed", poor.Speed == 5000.0 && poor.CannotPayCheapest && speed.Mode == SpeedMode.Max);
    fails += Check("MAX is capped", speed.Resolve(2.0 * c, 5000.0, cap).Speed == cap);
    CruiseChoice poorAtRest = speed.Resolve(0.0, 0.0, cap, floor: 3000.0);
    fails += Check("MAX without the cheapest trip never resolves below the slider floor", poorAtRest.Speed == 3000.0 && poorAtRest.CannotPayCheapest);
    fails += Check("the floor leaves a faster cheapest speed alone", speed.Resolve(0.0, 5000.0, cap, floor: 3000.0).Speed == 5000.0);

    speed.SelectPreset(2);
    fails += Check("a preset stays fixed while the budget moves",
        speed.Resolve(40000.0, 5000.0, cap).Speed == 0.1 * c && speed.Resolve(1000.0, 5000.0, cap).Speed == 0.1 * c);

    speed.SetCustom(12345.0);
    fails += Check("a slider edit is a custom speed", speed.Mode == SpeedMode.Custom && speed.Resolve(40000.0, 5000.0, cap).Speed == 12345.0);

    speed.SelectMax();
    speed.DropMaxIfUnavailable(maxAvailable: false, currentSpeed: 777.0);
    fails += Check("a drive vessel falls back from MAX to its current speed", speed.Mode == SpeedMode.Custom && speed.Resolve(40000.0, 5000.0, cap).Speed == 777.0);
    speed.SelectMax();
    speed.DropMaxIfUnavailable(maxAvailable: true, currentSpeed: 777.0);
    fails += Check("MAX stays where it exists", speed.Mode == SpeedMode.Max);

    Console.WriteLine(fails == 0 ? "  cruise: OK" : $"  cruise: {fails} FAIL");
    return fails;
}

// Assertions on SceneComposer, in all three layout modes and on two home maps (the stock-like
// planet root and one at the size of the real Sol map): the ego map is laid out exactly as
// alone, the other systems form a strip on the ego map's free side beyond an empty channel
// with their roots on one line, and the rules of SceneChecks hold at fit zoom on a 1600x1000
// and a 1300x860 canvas. Composing again moves nothing, opening a system keeps the side, the
// trunk, the bus, the root line and every part nearer the trunk, and closing it again puts
// every root back. At fit zoom the systems keep most of the fit zoom of the ego map alone. A
// scene without parts is the ego layout.
static int RunSceneChecks(string outDir)
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[scene] destination systems in a strip beside the ego map");

    foreach ((string variant, LayoutConfig cfg) in new[]
    {
        ("default", LayoutConfig.Default),
        ("gravitywell", new LayoutConfig { Mode = LayoutMode.GravityWell }),
        ("spring", new LayoutConfig { Mode = LayoutMode.Spring })
    })
    {
        foreach ((string sol, Func<LayoutTree> buildEgo) in new (string, Func<LayoutTree>)[]
        {
            ("", SyntheticTree.BuildStockLikePlanetRoot),
            ("-realsol", SyntheticTree.BuildRealLikeSolRoot)
        })
        {
            string tag = variant + sol;
            fails += RunSceneCase(tag, cfg, buildEgo, outDir);
        }
    }

    fails += RunWrapCheck(outDir);

    Console.WriteLine(fails == 0 ? "  scene: OK" : $"  scene: {fails} FAIL");
    return fails;
}

static int RunSceneCase(string tag, LayoutConfig cfg, Func<LayoutTree> buildEgo, string outDir)
{
    int fails = 0;
    LayoutResult alone = LayoutEngine.Run(buildEgo(), cfg);
    LayoutResult ego = LayoutEngine.Run(buildEgo(), cfg);
    LayoutNode hub = FindNode(ego.Tree, "Sol.Hub");
    double aloneFit = SceneFit.Fit(SceneComposer.Compose(alone, null, new List<LayoutPart>(), cfg), 1600, 1000, 0.1, 4.0, out _, out _);

    List<LayoutPart> parts = StockStubs(ego, tag, ref fails);
    LayoutPart alpha = parts[0];
    LayoutPart barnard = parts[1];
    LayoutPart tau = parts[2];

    LayoutScene scene = SceneComposer.Compose(ego, hub, parts, cfg);
    fails += Check($"{tag}: the ego map is laid out as alone", Snapshot(ego.Tree.Nodes) == Snapshot(alone.Tree.Nodes));
    fails += Check($"{tag}: the strip side follows the ego shape",
        scene.Side == (cfg.Mode == LayoutMode.Spring ? StripSide.Right : StripSide.Below));
    fails += Expect($"{tag}: the strip geometry holds", SceneChecks.Strip(scene));
    fails += CheckAtFit(tag, scene, aloneFit, minRatio: 0.85);
    fails += CheckStubSpacing(tag, scene, parts);
    if (tag == "default-realsol")
        fails += Check($"{tag}: the fit zoom is not clamped at the minimum", FitPreview.Build(scene, 1600, 1000).Zoom > 0.1);

    OverlapReport report = OverlapCheck.RunScene(scene);
    PrintIssues("node", report.NodeOverlaps);
    PrintIssues("label", report.LabelOverlaps);
    fails += Check($"{tag}: no dot or label of one part covers another", report.NodeOverlaps.Count == 0 && report.LabelOverlaps.Count == 0);

    string connectors = ConnectorSnapshot(scene);
    LayoutScene again = SceneComposer.Compose(ego, hub, parts, cfg);
    fails += Check($"{tag}: composing again moves nothing", ConnectorSnapshot(again) == connectors && Snapshot(again.Nodes) == Snapshot(scene.Nodes));

    // The connectors depend only on the laid-out parts, never on anything priced at display
    // time: fresh layouts of the same trees compose to the same connectors.
    int freshFails = 0;
    LayoutResult freshEgo = LayoutEngine.Run(buildEgo(), cfg);
    LayoutScene fresh = SceneComposer.Compose(freshEgo, FindNode(freshEgo.Tree, "Sol.Hub"), StockStubs(freshEgo, tag, ref freshFails), cfg);
    fails += freshFails;
    fails += Check($"{tag}: fresh layouts compose to the same connectors", ConnectorSnapshot(fresh) == connectors);

    File.WriteAllText(Path.Combine(outDir, "scene-" + tag + ".svg"), LayoutDumpFormat.ToSvg(scene));
    File.WriteAllText(Path.Combine(outDir, "scene-" + tag + ".txt"), LayoutDumpFormat.ToText(scene));
    File.WriteAllText(Path.Combine(outDir, "scene-" + tag + "-fit.svg"), LayoutDumpFormat.ToFitSvg(scene, 1600, 1000));
    File.WriteAllText(Path.Combine(outDir, "scene-" + tag + "-fit-small.svg"), LayoutDumpFormat.ToFitSvg(scene, 1300, 860));

    // Opening Alpha Centauri keeps the ego map, the side, the trunk, the bus, the root line and
    // every part nearer the trunk. In the tree modes the opened root keeps its place too; in
    // Spring the opened graph spreads round its root, so the root moves out of the channel.
    string egoBefore = Snapshot(ego.Tree.Nodes);
    SceneChecks.Places before = SceneChecks.Take(scene);
    LayoutPart alphaOpen = ScenePart(() => SyntheticTree.BuildBarycenterSystem("AlphaCentauri", "4.344 ly", "3 stars, 2 planets"), ego, "AlphaCentauri.Hub", true, ref fails, tag);
    var openedParts = new List<LayoutPart> { alphaOpen, barnard, tau };
    LayoutScene opened = SceneComposer.Compose(ego, hub, openedParts, cfg);
    fails += Check($"{tag}: opening a system keeps the ego map", Snapshot(ego.Tree.Nodes) == egoBefore);
    fails += Expect($"{tag}: opening a system keeps the strip", SceneChecks.Stability(before, opened, "AlphaCentauri.Hub", out double along, out double across));
    Console.WriteLine(FormattableString.Invariant($"  {tag}: opening the first system moves its root by ({along:F0}, {across:F0}) along and across the strip"));
    if (cfg.Mode != LayoutMode.Spring)
        fails += Check($"{tag}: the opened root keeps its place", along == 0.0 && across == 0.0);
    else
        fails += Check($"{tag}: the opened root keeps its place along the strip", along == 0.0);
    bool below = opened.Side == StripSide.Below;
    double partShare = below ? alphaOpen.Result.Height / ego.Height : alphaOpen.Result.Width / ego.Width;
    fails += Check(FormattableString.Invariant($"{tag}: the opened system stays within 0.6 of the ego map ({partShare:F2})"), partShare <= 0.6 + 1e-9);

    fails += Expect($"{tag}-opened: the strip geometry holds", SceneChecks.Strip(opened));
    fails += CheckAtFit(tag + "-opened", opened, aloneFit, minRatio: 0.80);
    fails += CheckExpandedDots(tag + "-opened", opened, alphaOpen);
    OverlapReport openedReport = OverlapCheck.RunScene(opened);
    PrintIssues("node", openedReport.NodeOverlaps);
    PrintIssues("label", openedReport.LabelOverlaps);
    fails += Check($"{tag}: the opened scene has no overlap", openedReport.NodeOverlaps.Count == 0 && openedReport.LabelOverlaps.Count == 0);
    File.WriteAllText(Path.Combine(outDir, "scene-" + tag + "-opened.svg"), LayoutDumpFormat.ToSvg(opened));
    File.WriteAllText(Path.Combine(outDir, "scene-" + tag + "-opened.txt"), LayoutDumpFormat.ToText(opened));
    File.WriteAllText(Path.Combine(outDir, "scene-" + tag + "-opened-fit.svg"), LayoutDumpFormat.ToFitSvg(opened, 1600, 1000));
    File.WriteAllText(Path.Combine(outDir, "scene-" + tag + "-opened-fit-small.svg"), LayoutDumpFormat.ToFitSvg(opened, 1300, 860));

    // Closing it again puts every root back where it was.
    LayoutScene closed = SceneComposer.Compose(ego, hub, parts, cfg);
    SceneChecks.Places after = SceneChecks.Take(closed);
    bool restored = after.Trunk == before.Trunk;
    foreach (KeyValuePair<string, (double X, double Y)> root in before.Roots)
        restored &= after.Roots[root.Key] == root.Value;
    fails += Check($"{tag}: closing the system again puts every root back", restored);

    // Every system opened at once (two is the most the map opens, a selection and a search).
    LayoutPart barnardOpen = ScenePart(() => SyntheticTree.BuildStarSystem("BarnardsStar", "5.963 ly", "1 star, 4 planets"), ego, "BarnardsStar.Hub", true, ref fails, tag);
    LayoutPart tauOpen = ScenePart(() => SyntheticTree.BuildStarSystem("TauCeti", "11.91 ly", "1 star, 4 planets"), ego, "TauCeti.Hub", true, ref fails, tag);
    LayoutScene allOpen = SceneComposer.Compose(ego, hub, new List<LayoutPart> { alphaOpen, barnardOpen, tauOpen }, cfg);
    OverlapReport allReport = OverlapCheck.RunScene(allOpen);
    fails += Check($"{tag}: every system opened has no overlap", allReport.NodeOverlaps.Count == 0 && allReport.LabelOverlaps.Count == 0);
    fails += Expect($"{tag}-allopen: the strip geometry holds", SceneChecks.Strip(allOpen));
    fails += Expect($"{tag}-allopen: the connectors keep clear at 1600x1000", SceneChecks.Connectors(allOpen, 1600, 1000));
    fails += Expect($"{tag}-allopen: the fit look holds at 1600x1000", SceneChecks.Fit(allOpen, 1600, 1000, out _));
    File.WriteAllText(Path.Combine(outDir, "scene-" + tag + "-allopen-fit.svg"), LayoutDumpFormat.ToFitSvg(allOpen, 1600, 1000));

    // Without parts the scene is exactly the ego layout, and the hub is no system title.
    LayoutResult solo = LayoutEngine.Run(buildEgo(), cfg);
    LayoutNode soloHub = FindNode(solo.Tree, "Sol.Hub");
    soloHub.IsSystemRoot = true;
    LayoutScene plain = SceneComposer.Compose(solo, soloHub, new List<LayoutPart>(), cfg);
    fails += Check($"{tag}: without parts the scene is the ego layout",
        plain.MinX == solo.MinX && plain.MinY == solo.MinY && plain.MaxX == solo.MaxX && plain.MaxY == solo.MaxY
        && plain.Nodes.Count == solo.Tree.Nodes.Count && plain.Connectors.Count == 0 && plain.Network.Count == 0 && !plain.HasBreakMark && !soloHub.IsSystemRoot
        && LayoutDumpFormat.ToText(plain) == LayoutDumpFormat.ToText(solo));
    return fails;
}

// The three stock destinations collapsed, nearest first.
static List<LayoutPart> StockStubs(LayoutResult ego, string tag, ref int fails)
{
    return new List<LayoutPart>
    {
        ScenePart(() => SyntheticTree.BuildSystemStub("AlphaCentauri", true, "4.344 ly", "3 stars, 2 planets"), ego, "AlphaCentauri.Hub", false, ref fails, tag),
        ScenePart(() => SyntheticTree.BuildSystemStub("BarnardsStar", false, "5.963 ly", "1 star, 4 planets"), ego, "BarnardsStar.Hub", false, ref fails, tag),
        ScenePart(() => SyntheticTree.BuildSystemStub("TauCeti", false, "11.91 ly", "1 star, 4 planets"), ego, "TauCeti.Hub", false, ref fails, tag)
    };
}

// A destination laid out the way the map lays it out: spread for the ego map's fit zoom. build
// makes a fresh tree on every call, because LayOutPart may try several layouts.
static LayoutPart ScenePart(Func<LayoutTree> build, LayoutResult ego, string rootId, bool expanded, ref int fails, string variant)
{
    LayoutResult result = SceneComposer.LayOutPart(build, ego, null);
    LayoutTree tree = result.Tree;
    OverlapReport report = OverlapCheck.Run(result);
    PrintIssues("node", report.NodeOverlaps);
    PrintIssues("label", report.LabelOverlaps);
    PrintIssues("band", report.BandViolations);
    PrintIssues("bus", report.BusViolations);
    fails += Check($"{variant}: part {tree.Name} lays out cleanly", report.Ok);
    return new LayoutPart { RootId = rootId, Result = result, Expanded = expanded };
}

// The SceneChecks rules on a 1600x1000 and a 1300x860 canvas, and at 1600x1000 the fit zoom
// against the ego map alone.
static int CheckAtFit(string tag, LayoutScene scene, double aloneFit, double minRatio)
{
    int fails = 0;
    foreach ((double w, double h) in new[] { (1600.0, 1000.0), (1300.0, 860.0) })
    {
        string at = FormattableString.Invariant($"{tag} at {w}x{h}");
        if (w == 1600.0)
        {
            double ratio = FitPreview.Build(scene, w, h).Zoom / aloneFit;
            Console.WriteLine(FormattableString.Invariant($"  {at}: fit zoom {100 * ratio:F1}% of the ego map alone"));
            fails += Check(FormattableString.Invariant($"{at}: the fit zoom keeps {100 * minRatio:F0}% of the ego map alone ({100 * ratio:F1}%)"), ratio >= minRatio);
        }
        fails += Expect($"{at}: the connectors keep clear", SceneChecks.Connectors(scene, w, h));
        fails += Expect($"{at}: the fit look holds", SceneChecks.Fit(scene, w, h, out double band));
        Console.WriteLine(FormattableString.Invariant($"  {at}: empty band {band:F0} px"));
    }
    return fails;
}

static int Expect(string name, List<string> problems)
{
    foreach (string problem in problems)
        Console.WriteLine($"  {name}: {problem}");
    return Check(name, problems.Count == 0);
}

// Two collapsed systems next to each other sit at least their wider title plus 24 px apart.
static int CheckStubSpacing(string tag, LayoutScene scene, List<LayoutPart> parts)
{
    int fails = 0;
    bool below = scene.Side == StripSide.Below;
    foreach ((double w, double h) in new[] { (1600.0, 1000.0), (1300.0, 860.0) })
    {
        FitPreview fit = FitPreview.Build(scene, w, h);
        bool ok = true;
        for (int i = 0; i + 1 < parts.Count; i++)
        {
            LayoutNode a = parts[i].Root;
            LayoutNode b = parts[i + 1].Root;
            if (!a.IsSystemStub || !b.IsSystemStub)
                continue;
            (double ax, double ay) = fit.ToScreen(a.SnappedX, a.SnappedY);
            (double bx, double by) = fit.ToScreen(b.SnappedX, b.SnappedY);
            double need = below ? Math.Max(SceneComposer.TitleTextW(a), SceneComposer.TitleTextW(b)) + 24.0 : 2.0 * FitPreview.LinePx + 24.0;
            ok &= Math.Abs(below ? bx - ax : by - ay) >= need;
        }
        fails += Check(FormattableString.Invariant($"{tag} at {w}x{h}: collapsed systems keep their titles apart"), ok);
    }
    return fails;
}

// The dots of an opened system do not touch at fit zoom.
static int CheckExpandedDots(string tag, LayoutScene scene, LayoutPart part)
{
    FitPreview fit = FitPreview.Build(scene, 1600, 1000);
    IReadOnlyList<LayoutNode> nodes = part.Result.Tree.Nodes;
    double worst = double.PositiveInfinity;
    for (int i = 0; i < nodes.Count; i++)
    {
        for (int j = i + 1; j < nodes.Count; j++)
        {
            (double ax, double ay) = fit.ToScreen(nodes[i].SnappedX, nodes[i].SnappedY);
            (double bx, double by) = fit.ToScreen(nodes[j].SnappedX, nodes[j].SnappedY);
            double gap = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)) - SceneComposer.GlyphRadiusPx(nodes[i]) - SceneComposer.GlyphRadiusPx(nodes[j]);
            worst = Math.Min(worst, gap);
        }
    }
    return Check(FormattableString.Invariant($"{tag}: the opened system's dots keep apart at fit ({worst:F1} px)"), worst >= 2.0);
}

// Many collapsed systems wrap into a second row with its own bus and root line, reached through
// the gap after the first system of the row before it.
static int RunWrapCheck(string outDir)
{
    int fails = 0;
    LayoutConfig cfg = LayoutConfig.Default;
    LayoutResult ego = LayoutEngine.Run(SyntheticTree.BuildStockLikePlanetRoot(), cfg);
    LayoutNode hub = FindNode(ego.Tree, "Sol.Hub");
    var parts = new List<LayoutPart>();
    for (int i = 0; i < 9; i++)
    {
        string id = "System" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        parts.Add(ScenePart(() => SyntheticTree.BuildSystemStub(id, i % 3 == 0, "1" + i + ".2 ly", "1 star, 3 planets"), ego, id + ".Hub", false, ref fails, "wrap"));
    }
    LayoutScene scene = SceneComposer.Compose(ego, hub, parts, cfg);
    var lines = new SortedSet<double>();
    bool within = true;
    foreach (LayoutPart part in parts)
    {
        lines.Add(part.Root.SnappedY);
        within &= part.MinX >= ego.MinX - 1e-9 && part.MaxX <= ego.MaxX + 1e-9;
    }
    fails += Check($"wrap: nine systems wrap into two rows ({lines.Count})", lines.Count == 2);
    fails += Check("wrap: every row stays within the ego width", within);
    OverlapReport report = OverlapCheck.RunScene(scene);
    fails += Check("wrap: the wrapped strip has no overlap", report.NodeOverlaps.Count == 0 && report.LabelOverlaps.Count == 0);
    fails += Expect("wrap: the connectors keep clear", SceneChecks.Connectors(scene, 1600, 1000, overlays: false));
    File.WriteAllText(Path.Combine(outDir, "scene-wrap-fit.svg"), LayoutDumpFormat.ToFitSvg(scene, 1600, 1000));
    return fails;
}

static LayoutNode FindNode(LayoutTree tree, string id)
{
    foreach (LayoutNode node in tree.Nodes)
    {
        if (node.Id == id)
            return node;
    }
    throw new InvalidOperationException($"No node '{id}' in {tree.Name}.");
}

static string Snapshot(IReadOnlyList<LayoutNode> nodes)
{
    var sb = new System.Text.StringBuilder();
    foreach (LayoutNode node in nodes)
    {
        sb.Append(FormattableString.Invariant($"{node.Id}:{node.SnappedX},{node.SnappedY},{node.LabelPlaced},{node.LabelX},{node.LabelY};"));
        foreach (LayoutEdge edge in node.Out)
        {
            foreach (LayoutPoint p in edge.Polyline)
                sb.Append(FormattableString.Invariant($"{p.X},{p.Y} "));
        }
    }
    return sb.ToString();
}

static string ConnectorSnapshot(LayoutScene scene)
{
    var sb = new System.Text.StringBuilder();
    foreach (LayoutConnector connector in scene.Connectors)
    {
        foreach (LayoutPoint p in connector.Polyline)
            sb.Append(FormattableString.Invariant($"{p.X},{p.Y} "));
        sb.Append('|');
    }
    return sb.ToString();
}

// Assertions on the shared number formats, the strings every panel, badge and tooltip shows,
// and on the keys that let a caller keep a string until the shown value moves.
static int RunFormatChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[format] shared number formats");
    double c = InterstellarMath.SpeedOfLight;
    double year = 365.25 * 86400.0;

    fails += Check("dV", Format.Dv(1234.4) == "~1,234 m/s" && Format.DvNumber(29949571.2) == "29,949,571");
    fails += Check("minutes", Format.Duration(120.0) == "2 min");
    fails += Check("hours", Format.Duration(7200.0) == "2.0 h");
    fails += Check("days", Format.Duration(3.0 * 86400.0) == "3.0 d");
    fails += Check("years", Format.Duration(43.44 * year) == "43.44 yr");
    fails += Check("thousands of years", Format.Duration(4344.18 * year) == "4,344 yr");
    fails += Check("no duration", Format.Duration(double.PositiveInfinity) == "-");
    fails += Check("light-years", Format.Distance(4.109915256421932e16) == "4.344 ly");
    fails += Check("astronomical units", Format.Distance(InterstellarMath.AstronomicalUnit) == "1.00 AU");
    fails += Check("kilometers", Format.Distance(5.0e6) == "5,000 km");
    fails += Check("a fraction of c", Format.Speed(0.1 * c) == "0.1000 c (29,979 km/s)");
    fails += Check("km/s", Format.Speed(5000.0) == "5.0 km/s");
    fails += Check("kilograms", Format.Mass(500.0) == "500.0 kg");
    fails += Check("tonnes", Format.Mass(2500.0) == "2.50 t");
    fails += Check("degrees", Format.Degrees(Math.PI / 4.0) == "45" && Format.DegreesSigned(-Math.PI / 4.0) == "-45" && Format.DegreesSigned(Math.PI / 4.0) == "+45");
    fails += Check("window countdown", Format.WindowTime(2.5 * 86400.0) == "3d" && Format.WindowTime(2.0 * year) == "2.0yr");
    // The map badge keeps one decimal of years at every size, so the single-system map reads as
    // it always has.
    fails += Check("badge transfer time", Format.TransferTime(30.61 * year) == "30.6 yr" && Format.TransferTime(1234.5 * year) == "1234.5 yr"
        && Format.TransferTime(3.0 * 86400.0) == "3.0 d" && Format.TransferTime(7200.0) == "2.0 h" && Format.TransferTime(120.0) == "2 min");

    // A key changes exactly when its string does, over a sweep through every unit.
    bool durationKeys = true;
    bool windowKeys = true;
    double previous = 1.0;
    for (double s = 1.0; s < 5000.0 * year; s *= 1.0007)
    {
        durationKeys &= (Format.DurationKey(s) == Format.DurationKey(previous)) == (Format.Duration(s) == Format.Duration(previous));
        windowKeys &= (Format.WindowTimeKey(s) == Format.WindowTimeKey(previous)) == (Format.WindowTime(s) == Format.WindowTime(previous));
        previous = s;
    }
    fails += Check("the duration key follows the string", durationKeys);
    fails += Check("the window key follows the string", windowKeys);

    Console.WriteLine(fails == 0 ? "  format: OK" : $"  format: {fails} FAIL");
    return fails;
}

// Assertions on the rocket-equation propellant estimate: the burned share matches the closed form
// of a single stage, shares add up across stages, and a shortfall is paid by extra propellant in the
// last stage that the rocket equation round-trips.
static int RunPropellantChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[propellant] rocket-equation checks");

    double ve = 3000.0;
    var stage = new StageMass(ve * Math.Log(10000.0 / 2000.0), 10000.0, 8000.0);
    fails += Approx("stage delta-v", stage.DeltaV, 4828.313737, 1e-9);
    fails += Approx("share at 0", RocketPropellant.BurnedShare(in stage, 0.0), 0.0, 1e-12);
    fails += Approx("share at full", RocketPropellant.BurnedShare(in stage, stage.DeltaV), 1.0, 1e-12);

    {
        PropellantNeed half = RocketPropellant.Needed(new[] { stage }, stage.DeltaV / 2.0);
        fails += Approx("half the delta-v", half.Needed, 10000.0 * (1.0 - Math.Exp(-stage.DeltaV / 2.0 / ve)), 1e-9);
        fails += Approx("half the delta-v (value)", half.Needed, 5527.864045, 1e-9);
        fails += Check("half the delta-v is feasible", half.Feasible);
        fails += Approx("available propellant", half.Available, 8000.0, 1e-12);
    }
    {
        PropellantNeed full = RocketPropellant.Needed(new[] { stage }, stage.DeltaV);
        fails += Approx("full delta-v burns the load", full.Needed, 8000.0, 1e-9);
        fails += Check("full delta-v is feasible", full.Feasible);
    }
    {
        double extraDv = 500.0;
        PropellantNeed over = RocketPropellant.Needed(new[] { stage }, stage.DeltaV + extraDv);
        fails += Check("over the budget is not feasible", !over.Feasible);
        fails += Approx("shortfall", over.ShortfallDv, extraDv, 1e-9);
        double wet = stage.WetMass + over.ExtraPropellant;
        double dry = stage.WetMass - stage.BurnedMass;
        fails += Approx("extra propellant round-trips", ve * Math.Log(wet / dry), stage.DeltaV + extraDv, 1e-9);
    }
    {
        // Two stages: shares add up in firing order, and a stage with no delta-v is skipped.
        var upper = new StageMass(2000.0, 3000.0, 1500.0);
        var inert = new StageMass(0.0, 500.0, 0.0);
        StageMass[] stages = { stage, inert, upper };
        PropellantNeed into = RocketPropellant.Needed(stages, stage.DeltaV + 1000.0);
        double expected = 8000.0 + 1500.0 * RocketPropellant.BurnedShare(in upper, 1000.0);
        fails += Approx("two stages add up", into.Needed, expected, 1e-9);
        fails += Approx("two stages available", into.Available, 9500.0, 1e-12);

        // An inert sequence last: the extra load still goes to the last stage that burns.
        StageMass[] inertLast = { stage, upper, inert };
        double shortfall = 700.0;
        PropellantNeed past = RocketPropellant.Needed(inertLast, stage.DeltaV + upper.DeltaV + shortfall);
        fails += Check("an inert last sequence is skipped", double.IsFinite(past.ExtraPropellant));
        fails += Approx("the extra load goes to the last burning stage", past.ExtraPropellant, RocketPropellant.ExtraPropellant(in upper, shortfall), 1e-12);

        PropellantNeed none = RocketPropellant.Needed(new[] { inert }, 100.0);
        fails += Check("no burning stage is not feasible", !none.Feasible && double.IsPositiveInfinity(none.ExtraPropellant));
    }
    {
        // An interstellar delta-v on a chemical stage overflows the extra propellant, but the
        // mass ratio stays finite in log space.
        double dv = 5.9e6;
        PropellantNeed far = RocketPropellant.Needed(new[] { stage }, dv);
        fails += Check("an interstellar delta-v overflows the extra propellant", double.IsPositiveInfinity(far.ExtraPropellant));
        fails += Approx("its mass ratio stays finite", far.Log10MassRatio, dv / ve / Math.Log(10.0), 1e-9);
        PropellantNeed near = RocketPropellant.Needed(new[] { stage }, stage.DeltaV + 500.0);
        fails += Approx("the mass ratio matches the extra propellant",
            Math.Pow(10.0, near.Log10MassRatio), (stage.WetMass + near.ExtraPropellant) / (stage.WetMass - stage.BurnedMass), 1e-9);
    }

    Console.WriteLine(fails == 0 ? "  propellant: OK" : $"  propellant: {fails} FAIL");
    return fails;
}

// Assertions on the structural rules of the physical forest, the shapes the stock multi-star
// content gives: a barycenter root (Alpha Centauri) with stars under it, a star whose only
// children are MinorBody exoplanets (Proxima Centauri), and Sol, where Ceres stays minor beside
// the major planets.
static int RunForestChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[forest] multi-star forest rules");

    fails += Check("a stellar body is a star", ForestRules.RoleOf(isStellarBody: true, isIndependentRoot: true) == BodyRole.Star);
    fails += Check("an orbiting star is a star", ForestRules.RoleOf(isStellarBody: true, isIndependentRoot: false) == BodyRole.Star);
    fails += Check("a root that is no star is a barycenter", ForestRules.RoleOf(isStellarBody: false, isIndependentRoot: true) == BodyRole.Barycenter);
    fails += Check("anything else is a body", ForestRules.RoleOf(isStellarBody: false, isIndependentRoot: false) == BodyRole.Body);

    bool proximaPlanets = ForestRules.MinorChildrenArePlanets(BodyRole.Star, hasMajorPlanet: false);
    fails += Check("the exoplanets of a star are planets", proximaPlanets && !ForestRules.IsMinor(isMinorBody: true, proximaPlanets));
    bool solPlanets = ForestRules.MinorChildrenArePlanets(BodyRole.Star, hasMajorPlanet: true);
    fails += Check("Ceres stays minor beside major planets", ForestRules.IsMinor(isMinorBody: true, solPlanets));
    fails += Check("the minor bodies of a barycenter without planets are planets",
        ForestRules.MinorChildrenArePlanets(BodyRole.Barycenter, hasMajorPlanet: false));
    fails += Check("the minor moons of a planet stay minor", !ForestRules.MinorChildrenArePlanets(BodyRole.Body, hasMajorPlanet: false));
    fails += Check("a major body is never minor", !ForestRules.IsMinor(isMinorBody: false, minorChildrenArePlanets: false));

    var roots = new List<(bool Home, double D2, string Id)>
    {
        (false, 1.2e34, "TauCeti"),
        (false, 1.7e33, "AlphaCentauri"),
        (true, 0.0, "Sol"),
        (false, 3.2e33, "BarnardsStar"),
        (false, 1.7e33, "Zeta")
    };
    roots.Sort((a, b) => ForestRules.CompareRoots(a.Home, a.D2, a.Id, b.Home, b.D2, b.Id));
    string order = string.Join(",", roots.ConvertAll(r => r.Id));
    fails += Check("roots are home first, then nearest, then by Id", order == "Sol,AlphaCentauri,Zeta,BarnardsStar,TauCeti");

    Console.WriteLine(fails == 0 ? "  forest: OK" : $"  forest: {fails} FAIL");
    return fails;
}

// Assertions on the closed-form transfer-window formulas, run offline (no game state). Locks
// the Hohmann lead angle against the stock Earth->Mars (+44 deg) and
// Earth->Venus (-54 deg) values, the synodic period (Earth/Mars ~2.14 yr), the time-to-window
// wrap (zero at alignment, a full synodic period a hair before it), the ejection geometry
// (hyperbolic, rising with v_inf, in (0,90) deg), the retrograde SUM rate, and a hand
// evaluation of the AlignmentTime formula for one stock pair.
static int RunTransferWindowChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[window] transfer-window engine checks");

    // Stock heliocentric semi-major axes (m) and the solar gravitational parameter, matching
    // the values the delta-v checks above use.
    const double muSun = 1.327e20;
    const double aEarth = 1.496e11;
    const double aMars = 2.279e11;
    const double aVenus = 1.082e11;
    double tEarth = 2.0 * Math.PI * Math.Sqrt(aEarth * aEarth * aEarth / muSun);
    double tMars = 2.0 * Math.PI * Math.Sqrt(aMars * aMars * aMars / muSun);
    double rad2deg = 180.0 / Math.PI;

    // Lead angle: Earth (inner) to Mars (outer) leads by ~+44 deg; Earth to Venus (inner) by
    // ~-54 deg. The single formula gives the sign from the wrap.
    {
        double mars = TransferWindow.TargetPhaseAngle(aEarth, aMars, false) * rad2deg;
        double venus = TransferWindow.TargetPhaseAngle(aEarth, aVenus, false) * rad2deg;
        fails += Approx("Earth->Mars target phase ~ +44 deg", mars, 44.0, 0.05);
        fails += Approx("Earth->Venus target phase ~ -54 deg", venus, -54.0, 0.05);
    }

    // Synodic period Earth/Mars is ~2.14 yr (sanity, approximate). Compared in Julian years.
    {
        double julianYear = 3.15576e7;
        double synodicYears = TransferWindow.SynodicPeriod(tEarth, tMars, false) / julianYear;
        fails += Approx("Earth/Mars synodic period ~2.14 yr", synodicYears, 2.14, 0.02);
    }

    // Time to window is zero exactly at alignment (current phase equals target phase), for any
    // non-zero synodic rate.
    {
        double rate = TransferWindow.SynodicRate(tEarth, tMars, false);
        double t = TransferWindow.TimeToWindowSeconds(0.5, 0.5, rate);
        fails += Check("time-to-window is 0 at alignment", t == 0.0);
    }

    // A hair before alignment the countdown wraps to a full synodic period: just past the
    // window, you must wait one whole recurrence. Earth/Mars has a negative rate (target
    // outer), so a tiny negative gap folds up to nearly 2*PI.
    {
        double rate = TransferWindow.SynodicRate(tEarth, tMars, false);
        double synodic = TransferWindow.SynodicPeriod(tEarth, tMars, false);
        double targetPhase = 0.5;
        double t = TransferWindow.TimeToWindowSeconds(targetPhase - 1e-9, targetPhase, rate);
        fails += Approx("time-to-window ~ synodic period just before alignment", t, synodic, 1e-6);
    }

    // Ejection geometry: the departure conic is hyperbolic (e > 1), and ejectionAngle =
    // PI - acos(-1/e) is in (0,90) deg and rises with v_inf. This convention has no stock
    // anchor (AlignmentTime computes no ejection angle), so it is a chosen approximation to be
    // re-checked against the in-game planner when the builder wires the angle to the UI.
    {
        double muEarth = 3.986e14;
        double rPark = 6.671e6;        // ~200 km circular parking orbit
        double eLow = TransferWindow.EjectionEccentricity(1000.0, rPark, muEarth);
        double eHigh = TransferWindow.EjectionEccentricity(3000.0, rPark, muEarth);
        fails += Check("ejection conic is hyperbolic", eLow > 1.0 && eHigh > 1.0);
        fails += Check("ejection eccentricity rises with v_inf", eHigh > eLow);

        double angLow = TransferWindow.EjectionAngle(1000.0, rPark, muEarth) * rad2deg;
        double angHigh = TransferWindow.EjectionAngle(3000.0, rPark, muEarth) * rad2deg;
        fails += Check("ejection angle in (0,90) deg", angLow > 0.0 && angLow < 90.0 && angHigh > 0.0 && angHigh < 90.0);
        fails += Check("ejection angle rises with v_inf", angHigh > angLow);
    }

    // Retrograde uses the SUM of the rates, not the difference, so it is larger in magnitude
    // and recurs faster than the prograde window for the same pair.
    {
        double pro = TransferWindow.SynodicRate(tEarth, tMars, false);
        double retro = TransferWindow.SynodicRate(tEarth, tMars, true);
        double expectedSum = 2.0 * Math.PI / tMars + 2.0 * Math.PI / tEarth;
        fails += Approx("retrograde rate is the SUM", retro, expectedSum, 1e-9);
        fails += Check("retrograde rate exceeds prograde rate", Math.Abs(retro) > Math.Abs(pro));
    }

    // Cross-check the target phase and time-to-window against an inline hand-evaluation of the
    // prograde branch of OrbitalTransfers.AlignmentTime for the stock Earth->Mars pair,
    // confirming the functions evaluate the same expression transcribed from AlignmentTime. This
    // is a regression lock against future desync, not an independent check of the transcription;
    // the transcription itself is verified by reading the decompiled source.
    {
        double currentPhase = 0.3;     // an arbitrary current Target-minus-Source phase, radians
        double refTarget = ToOrbitAngleLocal(Math.PI * (1.0 - Math.Pow((aEarth + aMars) / (2.0 * aMars), 1.5)));
        double refRate = 2.0 * Math.PI / tMars - 2.0 * Math.PI / tEarth;
        double gap = currentPhase - refTarget;
        if (gap > 0.0 && refRate > 0.0)
            gap -= 2.0 * Math.PI;
        if (gap < 0.0)
            gap += 2.0 * Math.PI;
        double refTime = Math.Abs(gap / refRate);

        double target = TransferWindow.TargetPhaseAngle(aEarth, aMars, false);
        double time = TransferWindow.TimeToWindowSeconds(currentPhase, target, refRate);
        fails += Approx("target phase matches AlignmentTime", target, refTarget, 1e-12);
        fails += Approx("time-to-window matches AlignmentTime", time, refTime, 1e-9);
    }

    Console.WriteLine(fails == 0 ? "  window: OK" : $"  window: {fails} FAIL");
    return fails;
}

// Inline copy of MathEx.ToDeviationAngle (wrap to (-PI, PI]) for the AlignmentTime cross-check,
// so the reference path is independent of TransferWindow's own private helper.
static double ToOrbitAngleLocal(double inRadians)
{
    double num = inRadians % (2.0 * Math.PI);
    if (num < -Math.PI)
        return num + 2.0 * Math.PI;
    if (num > Math.PI)
        return num - 2.0 * Math.PI;
    return num;
}

static int Approx(string name, double actual, double expected, double relTolerance)
{
    double denom = Math.Max(1.0, Math.Abs(expected));
    if (Math.Abs(actual - expected) / denom <= relTolerance)
        return 0;
    Console.WriteLine($"  FAIL {name}: got {actual}, expected {expected}");
    return 1;
}

static int Check(string name, bool ok)
{
    if (ok)
        return 0;
    Console.WriteLine($"  FAIL {name}");
    return 1;
}
