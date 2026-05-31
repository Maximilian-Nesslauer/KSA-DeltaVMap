using System;
using System.Collections.Generic;
using System.IO;
using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Layout;

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

// Spring (force-directed): the tree-shape invariants do not apply, but the settled web
// plus the grid snap must still leave no two dots (and no two labels) overlapping.
var spring = new LayoutConfig { Mode = LayoutMode.Spring };
failures += RunCase(SyntheticTree.BuildLargeMoonRoot(), spring, "spring", outDir, minNodes: 100);
failures += RunCase(SyntheticTree.BuildStockLikePlanetRoot(), spring, "spring", outDir, minNodes: 0);
failures += RunCase(SyntheticTree.BuildDenseAggregated(), spring, "spring", outDir, minNodes: 0);

// Large spring stress: exercises the Barnes-Hut repulsion and the iteration cap at a scale
// (~1800 nodes) the old all-pairs loop could not handle, and confirms the grid snap still
// leaves it overlap-free. No timing assertion (it would be machine-dependent and flaky); the
// value is the overlap-free check at this scale.
failures += RunCase(SyntheticTree.BuildSpringStress(), spring, "spring", outDir, minNodes: 1500);

// The closed-form delta-v kernel (pure math, no game types) gets its own asserts.
failures += RunMathChecks();

// The closed-form transfer-window math (pure, no game types) gets its own asserts.
failures += RunTransferWindowChecks();

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
// new Phase 7 math: the comet velocity-match generalization, that it reduces to the exact
// Hohmann for two bound endpoints, the intercept capture/circularize split identity, and
// the atmospheric landing fraction across the stock bodies' density range.
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
    // match must cost strictly more than the circular-Hohmann arrival the old fallback used.
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

    Console.WriteLine(fails == 0 ? "  math: OK" : $"  math: {fails} FAIL");
    return fails;
}

// Assertions on the closed-form transfer-window formulas, run offline (no game state). Locks
// the Phase 1 math: the Hohmann lead angle against the stock Earth->Mars (+44 deg) and
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
    // AlignmentTime prograde branch (decompiled lines 377-388) for the stock Earth->Mars pair,
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
