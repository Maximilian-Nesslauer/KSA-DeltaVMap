using System;
using System.Collections.Generic;
using System.IO;
using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;
using DeltaVMap.Route;
using DeltaVMap.Render;

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
failures += RunBoth(SyntheticTree.BuildLargeMoonRoot, LayoutConfig.Default, "default", outDir, minNodes: 100);

// The same stress tree on a deliberately tight grid, to force grid-snap collisions
// and confirm the nudge pass still ends overlap-free. GridPx is the tightest the dot
// radii allow (the two largest must fit one cell), and BandHeightPx the smallest whole
// multiple of it that still clears MinSegmentPx.
var tightGrid = new LayoutConfig { GridPx = 35.0, BandHeightPx = 70.0, SiblingGapPx = 12.0, BusGapPx = 24.0 };
failures += RunBoth(SyntheticTree.BuildLargeMoonRoot, tightGrid, "tight", outDir, minNodes: 100);

// A stock-like planet root, for eyeballing realism.
failures += RunBoth(SyntheticTree.BuildStockLikePlanetRoot, LayoutConfig.Default, "default", outDir, minNodes: 0);

// The interplanetary-cruise root: a star-shaped hub bus rather than a chain.
failures += RunBoth(SyntheticTree.BuildCruiseRoot, LayoutConfig.Default, "default", outDir, minNodes: 0);

// A wide, shallow fan: dense single-band siblings stressing the grid-snap nudge.
failures += RunBoth(SyntheticTree.BuildWideFan, LayoutConfig.Default, "default", outDir, minNodes: 0);

// The gravity-well arrival model: capture anchor above low orbit for destinations.
failures += RunBoth(SyntheticTree.BuildArrivalDemo, LayoutConfig.Default, "default", outDir, minNodes: 0);

// A dense system after minor-body aggregation: a "+N" group node off the hub bus and off a
// deeper local hub. Exercises the synthetic MinorGroup node's layout in all three modes.
failures += RunBoth(SyntheticTree.BuildDenseAggregated, LayoutConfig.Default, "default", outDir, minNodes: 0);

// The real-like Sol map has no fan wide enough to wrap, so its Auto layout must be its Off layout.
failures += RunBoth(SyntheticTree.BuildRealLikeSolRoot, LayoutConfig.Default, "default", outDir, minNodes: 0);

// GravityWell mode is the alternative to the Cumulative-down default. The same dense trees must
// stay overlap-free with the spine / well / bus assertions holding, on a fresh tree instance
// each time since the layout pass mutates node positions.
var gravityWell = new LayoutConfig { Mode = LayoutMode.GravityWell };
failures += RunBoth(SyntheticTree.BuildLargeMoonRoot, gravityWell, "gravitywell", outDir, minNodes: 100);
failures += RunBoth(SyntheticTree.BuildWideFan, gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildArrivalDemo, gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildStockLikePlanetRoot, gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildCruiseRoot, gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildDenseAggregated, gravityWell, "gravitywell", outDir, minNodes: 0);

// The realistic cruise root: planets attach to the star hub via HubLink onto their
// Intercept (above the spine). Exercises the GravityWell spine / well / relaxed-bus
// rules on the actual in-game cruise shape, in both modes.
failures += RunBoth(SyntheticTree.BuildCruiseRootArrival, LayoutConfig.Default, "default", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildCruiseRootArrival, gravityWell, "gravitywell", outDir, minNodes: 0);

// Multiple star systems: a barycenter root with its stars on the bus, and a star of a multiple
// system as the root, climbing to its barycenter with the sibling stars branching off it.
failures += RunBoth(SyntheticTree.BuildBarycenterRoot, LayoutConfig.Default, "default", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildBarycenterRoot, gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildOrbitingStarRoot, LayoutConfig.Default, "default", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildOrbitingStarRoot, gravityWell, "gravitywell", outDir, minNodes: 0);

// Spring (force-directed): the tree-shape invariants do not apply, but the settled web
// plus the grid snap must still leave no two dots (and no two labels) overlapping.
var spring = new LayoutConfig { Mode = LayoutMode.Spring };
failures += RunBoth(SyntheticTree.BuildLargeMoonRoot, spring, "spring", outDir, minNodes: 100);
failures += RunBoth(SyntheticTree.BuildStockLikePlanetRoot, spring, "spring", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildDenseAggregated, spring, "spring", outDir, minNodes: 0);

// Giant planets with hundreds of minor moons, folded by the real MinorFold rules: the inner
// moons as lanes, the outer swarm as a group, and one hub with an inner and an outer group.
failures += RunBoth(SyntheticTree.BuildDenseMoons, LayoutConfig.Default, "default", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildDenseMoons, gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildDenseMoons, spring, "spring", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildBarycenterRoot, spring, "spring", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildOrbitingStarRoot, spring, "spring", outDir, minNodes: 0);

// Fans at the band clamp have the shape real moon transfers give. They come as flat fans of 12,
// 13, 26 and 60 lanes, as the dense giants with every moon lane clamped, and as one giant with
// 435 minor moons whose layout must stay far below the map's node ceiling.
const int MapNodeCeiling = 8000; // MapWindow.MaxLayoutNodes, which the offline tests do not compile
foreach (int lanes in new[] { 12, 13, 26, 60 })
{
    failures += RunBoth(() => SyntheticTree.BuildFlatFan(lanes), LayoutConfig.Default, "default", outDir, minNodes: 0);
    failures += RunBoth(() => SyntheticTree.BuildFlatFan(lanes), gravityWell, "gravitywell", outDir, minNodes: 0);
}
failures += RunBoth(SyntheticTree.BuildClampedGiants, LayoutConfig.Default, "default", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildClampedGiants, gravityWell, "gravitywell", outDir, minNodes: 0);
failures += RunBoth(SyntheticTree.BuildManyMoons, LayoutConfig.Default, "default", outDir, minNodes: 0, maxNodes: MapNodeCeiling, printTiming: true);
failures += RunBoth(SyntheticTree.BuildManyMoons, gravityWell, "gravitywell", outDir, minNodes: 0, maxNodes: MapNodeCeiling, printTiming: true);
failures += RunClampedFanChecks();
failures += RunNegativeFixtures();
failures += RunWrappedNegativeFixtures();
failures += RunWellWrapFixtures();

// Large spring stress: exercises the Barnes-Hut repulsion and the iteration cap at a scale
// (~1800 nodes) an all-pairs loop could not handle in a frame, and confirms the grid snap still
// leaves it overlap-free. No timing assertion (it would be machine-dependent and flaky); the
// value is the overlap-free check at this scale.
failures += RunBoth(SyntheticTree.BuildSpringStress, spring, "spring", outDir, minNodes: 1500);

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
failures += RunMinorFoldChecks();
failures += RunCruiseSpeedChecks();
failures += RunSceneChecks(outDir);
failures += RunFormatChecks();
failures += RunRouteTotalsChecks();
failures += RunEdgeGeometryChecks();
failures += RunWrapConfigChecks();
failures += RunWrapGates();
failures += RunWellWrapGates();
failures += RunChooserChecks();
failures += RunWrapStability();
failures += RunWrapParts();
failures += RunOpaqueBlock(outDir);
failures += RunTextWidthChecks();
failures += RunRowBreakChecks();
// The rebuild timing takes a few seconds and prints numbers that depend on the machine, so it
// runs only when DVMAP_TIMING is set.
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DVMAP_TIMING")))
    RunRebuildTiming();

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CASES PASS" : $"{failures} CASE(S) FAILED");
return failures == 0 ? 0 : 1;

// Every case runs twice, once with the wrap Off under the plain file name and assertions, and
// once with the wrap Auto under a "-wrap" suffix, where the acceptance check must pass too.
// Spring ignores the wrap, and an Auto layout that wraps no fan must be the Off layout to the
// byte.
static int RunBoth(Func<LayoutTree> build, LayoutConfig cfg, string variant, string outDir, int minNodes, int maxNodes = int.MaxValue, bool printTiming = false)
{
    int fails = RunCaseFull(build(), cfg, variant, outDir, minNodes, maxNodes, printTiming, out LayoutResult off);
    fails += RunCaseFull(build(), cfg.WithWrap(WrapMode.Auto), variant, outDir, minNodes, maxNodes, printTiming, out LayoutResult auto, suffix: "-wrap");
    string tag = (variant == "default" ? off.Tree.Name : off.Tree.Name + "-" + variant) + "-wrap";
    if (cfg.Mode == LayoutMode.Spring)
        fails += Check($"{tag}: Spring lays out the same with the wrap Auto", Snapshot(auto.Tree.Nodes) == Snapshot(off.Tree.Nodes) && auto.Wraps.Count == 0);
    if (auto.Wraps.Count == 0)
        fails += Check($"{tag}: with no wrapped fan the layout is the Off layout", LayoutDumpFormat.ToText(auto) == LayoutDumpFormat.ToText(off) && Snapshot(auto.Tree.Nodes) == Snapshot(off.Tree.Nodes));
    return fails;
}

static int RunCase(LayoutTree tree, LayoutConfig cfg, string variant, string outDir, int minNodes, int maxNodes = int.MaxValue, bool printTiming = false)
{
    return RunCaseFull(tree, cfg, variant, outDir, minNodes, maxNodes, printTiming, out _);
}

// maxNodes is the node ceiling a case must stay below, and printTiming prints the node count and
// the layout time on a line of its own, with no timing assertion because it is machine-dependent.
// suffix goes after the tag in the log and the dump file names.
static int RunCaseFull(LayoutTree tree, LayoutConfig cfg, string variant, string outDir, int minNodes, int maxNodes, bool printTiming, out LayoutResult result, string suffix = "")
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    result = LayoutEngine.Run(tree, cfg);
    watch.Stop();
    OverlapReport report = OverlapCheck.Run(result);

    string tag = (variant == "default" ? tree.Name : tree.Name + "-" + variant) + suffix;
    Console.WriteLine($"[{tag}] {report.Summary()}");
    PrintIssues("node", report.NodeOverlaps);
    PrintIssues("subtree", report.SubtreeOverlaps);
    PrintIssues("label", report.LabelOverlaps);
    PrintIssues("band", report.BandViolations);
    PrintIssues("bus", report.BusViolations);
    PrintIssues("wrap", report.WrapViolations);

    File.WriteAllText(Path.Combine(outDir, tag + ".svg"), LayoutDumpFormat.ToSvg(result));
    File.WriteAllText(Path.Combine(outDir, tag + ".txt"), LayoutDumpFormat.ToText(result));

    if (printTiming)
        Console.WriteLine(FormattableString.Invariant($"  {tree.Nodes.Count} nodes laid out in {watch.Elapsed.TotalMilliseconds:F1} ms"));

    bool nodeCountOk = tree.Nodes.Count >= minNodes;
    if (!nodeCountOk)
        Console.WriteLine($"  FAIL: {tree.Nodes.Count} nodes < required {minNodes}");
    bool ceilingOk = tree.Nodes.Count < maxNodes;
    if (!ceilingOk)
        Console.WriteLine($"  FAIL: {tree.Nodes.Count} nodes >= ceiling {maxNodes}");

    return report.Ok && nodeCountOk && ceilingOk ? 0 : 1;
}

// The fixtures that stand in for real moon systems must keep every transfer lane at the band
// clamp, as the game gives it, or the fans they test are not the fans the map shows. A dV-free
// group link is exempt, because it always takes the minimum step.
static int RunClampedFanChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[clamped-fans] transfer lanes sit at the band clamp");

    var cfg = LayoutConfig.Default;
    var trees = new List<LayoutTree> { SyntheticTree.BuildClampedGiants(), SyntheticTree.BuildManyMoons() };
    foreach (int lanes in new[] { 12, 13, 26, 60 })
        trees.Add(SyntheticTree.BuildFlatFan(lanes));
    foreach (LayoutTree tree in trees)
    {
        LayoutEngine.Run(tree, cfg);
        int transferLanes = 0;
        int unclamped = 0;
        foreach (LayoutNode node in tree.Nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.Class != EdgeClass.Transfer || edge.To.Kind == LayoutKind.MinorGroup)
                    continue;
                transferLanes++;
                if (edge.To.Band - node.Band != cfg.MaxBandStep)
                    unclamped++;
            }
        }
        Console.WriteLine($"  {tree.Name}: {transferLanes} transfer lanes, {unclamped} below the clamp");
        fails += Check($"{tree.Name}: every transfer lane steps {cfg.MaxBandStep} bands", transferLanes > 0 && unclamped == 0);
    }

    LayoutTree manyMoons = SyntheticTree.BuildManyMoons();
    int widest = 0;
    foreach (LayoutNode node in manyMoons.Nodes)
    {
        int lanes = 0;
        foreach (LayoutEdge edge in node.Out)
        {
            if (edge.Class != EdgeClass.Ladder)
                lanes++;
        }
        widest = Math.Max(widest, lanes);
    }
    // The 7 regular moons, the inner minor moons up to the fold threshold, and the two groups.
    int expectedWidest = 7 + BuildOptions.DefaultMinorGroupThreshold + 2;
    Console.WriteLine($"  {manyMoons.Name}: widest fan {widest} lanes");
    fails += Check($"{manyMoons.Name}: the folded fan holds {expectedWidest} lanes", widest == expectedWidest);
    return fails;
}

// The acceptance check must catch a broken layout, not only pass a sound one. Each fixture takes
// a clean layout, breaks one value the way a faulty pass would, and expects the check to fail.
static int RunNegativeFixtures()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[negative] the acceptance check catches a broken layout");

    foreach (LayoutConfig cfg in new[] { LayoutConfig.Default, new LayoutConfig { Mode = LayoutMode.GravityWell } })
    {
        LayoutResult result = LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), cfg);
        fails += ExpectCaught($"{cfg.Mode}: a moon moved one grid cell off its dV band", result,
            r => FindNode(r.Tree, "SaturnMajor3.LO").Y += r.Config.GridPx);

        // On a map with no wrapped fan, a stray row, a stray offset that keeps Y on its band, and
        // a stray layout prefix must each fail the wrap rules.
        fails += ExpectCaught($"{cfg.Mode}: a lane in row 1 under a hub that is not wrapped",
            LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), cfg),
            r => FindNode(r.Tree, "SaturnMajor3.LO").WrapRow = 1);
        fails += ExpectCaught($"{cfg.Mode}: a row offset with no wrapped fan",
            LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), cfg),
            r =>
            {
                LayoutNode moon = FindNode(r.Tree, "SaturnMajor3.LO");
                moon.WrapDy = r.Config.GridPx;
                moon.Y += r.Config.GridPx;
            });
        fails += ExpectCaught($"{cfg.Mode}: a layout prefix on an edge that enters no lower row",
            LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), cfg),
            r => FindNode(r.Tree, "SaturnMajor3.LO").ParentEdge!.DvStart = 1);

        // A wrap record that does not match its rows. The fan of Sol keeps every lane in row 0,
        // so a record of two rows finds an empty row.
        fails += ExpectCaughtCopy($"{cfg.Mode}: a wrap record whose rows are not in the layout",
            LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), cfg),
            r =>
            {
                foreach (LayoutEdge edge in FindNode(r.Tree, "Sol.Hub").Out)
                    edge.To.WrapRow = 0;
                return WithWraps(r, new LayoutWrap { ParentId = "Sol.Hub", Mode = r.Config.Mode, RowSizes = new[] { 4, 5 }, RowDy = new[] { 0.0, 4 * r.Config.GridPx } });
            });
    }

    LayoutResult well = LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), new LayoutConfig { Mode = LayoutMode.GravityWell });
    fails += ExpectCaught("GravityWell: a moon placed left of its planet", well,
        r =>
        {
            LayoutNode moon = FindNode(r.Tree, "SaturnMajor3.LO");
            moon.X = moon.Parent!.X - r.Config.GridPx;
        });

    fails += ExpectCaughtCopy("Spring: a wrapped fan",
        LayoutEngine.Run(SyntheticTree.BuildStockLikePlanetRoot(), new LayoutConfig { Mode = LayoutMode.Spring }),
        r => WithWraps(r, new LayoutWrap { ParentId = "Sol.Hub", Mode = LayoutMode.Spring, RowSizes = new[] { 4, 4 }, RowDy = new[] { 0.0, 4 * r.Config.GridPx } }));
    return fails;
}

// A copy of a layout result that carries the given wrap records, for the fixtures that hand the
// check a wrap the layout never made.
static LayoutResult WithWraps(LayoutResult result, params LayoutWrap[] wraps)
{
    return new LayoutResult
    {
        Tree = result.Tree,
        Config = result.Config,
        Labels = result.Labels,
        MinX = result.MinX,
        MinY = result.MinY,
        MaxX = result.MaxX,
        MaxY = result.MaxY,
        Wraps = wraps
    };
}

// Runs the acceptance check on a clean layout, applies corrupt to it, and runs the check again.
// The fixture passes only when the clean layout passes and the corrupted one fails. With expect
// given, a wrap violation must also name it, so the fixture proves that the rule it aims at
// fires and not only a rule the fault breaks along the way.
static int ExpectCaught(string name, LayoutResult result, Action<LayoutResult> corrupt, string? expect = null)
{
    bool cleanOk = OverlapCheck.Run(result).Ok;
    corrupt(result);
    return CheckCaught(name, cleanOk, OverlapCheck.Run(result), expect);
}

// The same for a fault the layout result itself carries, such as a wrap record. Here corrupt
// returns the broken copy of the clean result.
static int ExpectCaughtCopy(string name, LayoutResult result, Func<LayoutResult, LayoutResult> corrupt, string? expect = null)
{
    bool cleanOk = OverlapCheck.Run(result).Ok;
    return CheckCaught(name, cleanOk, OverlapCheck.Run(corrupt(result)), expect);
}

static int CheckCaught(string name, bool cleanOk, OverlapReport report, string? expect)
{
    PrintIssues("caught node", report.NodeOverlaps);
    PrintIssues("caught subtree", report.SubtreeOverlaps);
    PrintIssues("caught label", report.LabelOverlaps);
    PrintIssues("caught band", report.BandViolations);
    PrintIssues("caught bus", report.BusViolations);
    PrintIssues("caught wrap", report.WrapViolations);
    bool named = expect == null || report.WrapViolations.Any(m => m.Contains(expect, StringComparison.Ordinal));
    return Check($"{name} is caught", cleanOk && !report.Ok && named);
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

    // The rows with the strip of other systems, under the map config of a universe with several
    // systems, in both tree modes. In Cumulative-down the stock-like and real-like maps do not
    // wrap, the dense and the clamped giants do, and in GravityWell every one of them wraps. With
    // the strip the fit must never fall, and it must gain a tenth wherever a fan wraps. Every
    // scene passes the same screen checks as the unwrapped scenes, with an opened GravityWell
    // system wrapped like the map beside it. Two rules differ for a wrapped map. With a system
    // opened, the fit is held to the same scene with the wrap Off, not to the map alone, and with
    // every system opened beside a wrapped GravityWell map the empty band is at least 60 px.
    foreach ((string variant, LayoutMode mode, string modeName) in new[]
    {
        ("default-wrap", LayoutMode.CumulativeDown, "CD"),
        ("gravitywell-wrap", LayoutMode.GravityWell, "GW")
    })
    {
        LayoutConfig wrapCfg = LayoutConfig.ForMap(mode, hasSeveralSystems: true, WrapMode.Auto);
        var offCfg = new LayoutConfig { Mode = mode };
        foreach ((string sol, Func<LayoutTree> buildEgo) in new (string, Func<LayoutTree>)[]
        {
            ("", SyntheticTree.BuildStockLikePlanetRoot),
            ("-realsol", SyntheticTree.BuildRealLikeSolRoot),
            ("-dense", SyntheticTree.BuildDenseMoons),
            ("-clamped", SyntheticTree.BuildClampedGiants)
        })
        {
            fails += RunSceneCase(variant + sol, wrapCfg, buildEgo, outDir);

            double fsOff = StripFit(buildEgo, offCfg, out _);
            double fsAuto = StripFit(buildEgo, wrapCfg, out bool wraps);
            string name = "scene" + (sol.Length > 0 ? sol : "-planet-root");
            Console.WriteLine(FormattableString.Invariant($"[wrap] {name} {modeName} fit with the strip {fsAuto:F3} ({100.0 * (fsAuto / fsOff - 1.0):+0;-0;+0}%), Off {fsOff:F3}"));
            fails += Check($"{name} {modeName}: the rows never lower the fit with the strip", fsAuto >= fsOff - 1e-12);
            if (wraps)
                fails += Check(FormattableString.Invariant($"{name} {modeName}: the rows raise the fit with the strip by a tenth ({fsAuto / fsOff:F2})"), fsAuto >= 1.10 * fsOff);
            // The fit with the strip each wrapping map reaches with these tunables.
            double reached = (mode, sol) switch
            {
                (LayoutMode.CumulativeDown, "-dense") => 0.119,
                (LayoutMode.CumulativeDown, "-clamped") => 0.089,
                (LayoutMode.GravityWell, "") => 0.557,
                (LayoutMode.GravityWell, "-realsol") => 0.343,
                (LayoutMode.GravityWell, "-dense") => 0.270,
                (LayoutMode.GravityWell, "-clamped") => 0.240,
                _ => 0.0
            };
            if (reached > 0.0)
                fails += Check(FormattableString.Invariant($"{name} {modeName}: the fit with the strip keeps 98% of the {reached:F3} reached"), wraps && fsAuto >= reached * 0.98);
        }
    }

    Console.WriteLine(fails == 0 ? "  scene: OK" : $"  scene: {fails} FAIL");
    return fails;
}

// The fit zoom of the ego map with the three stock systems collapsed in the strip.
static double StripFit(Func<LayoutTree> buildEgo, LayoutConfig cfg, out bool wraps)
{
    int ignored = 0;
    LayoutResult ego = LayoutEngine.Run(buildEgo(), cfg);
    wraps = ego.Wraps.Count > 0;
    LayoutScene scene = SceneComposer.Compose(ego, FindNode(ego.Tree, "Sol.Hub"), StockStubs(ego, "strip-fit", ref ignored), cfg);
    return SceneFit.Fit(scene, 1600, 1000, 0.02, 4.0, out _, out _);
}

static int RunSceneCase(string tag, LayoutConfig cfg, Func<LayoutTree> buildEgo, string outDir)
{
    int fails = 0;
    LayoutResult alone = LayoutEngine.Run(buildEgo(), cfg);
    LayoutResult ego = LayoutEngine.Run(buildEgo(), cfg);
    LayoutNode hub = FindNode(ego.Tree, "Sol.Hub");
    double aloneFit = SceneFit.Fit(SceneComposer.Compose(alone, null, new List<LayoutPart>(), cfg), 1600, 1000, SceneFit.MinZoom, 4.0, out _, out _);

    List<LayoutPart> parts = StockStubs(ego, tag, ref fails);
    LayoutPart alpha = parts[0];
    LayoutPart barnard = parts[1];
    LayoutPart tau = parts[2];

    LayoutScene scene = SceneComposer.Compose(ego, hub, parts, cfg);
    fails += Check($"{tag}: the ego map is laid out as alone", Snapshot(ego.Tree.Nodes) == Snapshot(alone.Tree.Nodes));
    // The strip goes below a wide ego map and to the right of a tall one. In the tree modes the
    // map of a universe with several systems is at least 1.5 times as wide as it is tall, so the
    // strip stays below, wrapped or not.
    double egoAspect = ego.Width / ego.Height;
    Console.WriteLine(FormattableString.Invariant($"  {tag}: ego {ego.Width:F0}x{ego.Height:F0}, aspect {egoAspect:F2}, strip {scene.Side}, fans wrapped {ego.Wraps.Count}"));
    fails += Check($"{tag}: the strip side follows the ego shape", scene.Side == SceneComposer.SideOf(ego));
    if (cfg.Mode != LayoutMode.Spring)
        fails += Check(FormattableString.Invariant($"{tag}: the ego map with several systems is at least 1.5 times as wide as tall ({egoAspect:F2})"), egoAspect >= 1.5);
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
    // An unwrapped map keeps 80% of its fit alone with a system opened. A wrap raises the fit of
    // the map alone so far that this share falls, while the opened view itself grows, so a
    // wrapped map is held to the same scene with the wrap Off instead.
    bool wrapApplies = ego.Wraps.Count > 0 || alphaOpen.Result.Wraps.Count > 0;
    fails += CheckAtFit(tag + "-opened", opened, aloneFit, minRatio: wrapApplies ? null : 0.80);
    if (cfg.Wrap == WrapMode.Auto)
    {
        LayoutScene openedOff = OpenedSceneWrapOff(buildEgo, cfg);
        foreach ((double w, double h) in new[] { (1600.0, 1000.0), (1300.0, 860.0) })
        {
            string at = FormattableString.Invariant($"{tag}-opened at {w}x{h}");
            double openedFit = FitPreview.Build(opened, w, h).Zoom;
            double openedOffFit = FitPreview.Build(openedOff, w, h).Zoom;
            Console.WriteLine(FormattableString.Invariant($"  {at}: fit zoom {openedFit:F3}, {openedOffFit:F3} with the wrap Off"));
            fails += Check(FormattableString.Invariant($"{at}: the rows never lower the fit with a system opened ({openedFit:F3} against {openedOffFit:F3})"), openedFit >= openedOffFit - 1e-12);
            if (wrapApplies)
                fails += Check(FormattableString.Invariant($"{at}: the rows raise the fit with a system opened by a tenth ({openedFit / openedOffFit:F2})"), openedFit >= 1.10 * openedOffFit);
        }
    }
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
    bool wrappedWell = cfg.Mode == LayoutMode.GravityWell
        && (ego.Wraps.Count > 0 || alphaOpen.Result.Wraps.Count > 0 || barnardOpen.Result.Wraps.Count > 0 || tauOpen.Result.Wraps.Count > 0);
    double minBand = wrappedWell ? SceneChecks.MinBandAllOpenWrappedPx : SceneChecks.MinBandPx;
    fails += Expect($"{tag}-allopen: the fit look holds at 1600x1000", SceneChecks.Fit(allOpen, 1600, 1000, out double allBand, minBand));
    Console.WriteLine(FormattableString.Invariant($"  {tag}-allopen at 1600x1000: empty band {allBand:F0} px, at least {minBand:F0}"));
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

// The scene with Alpha Centauri opened and the wrap Off, for the home map and every destination
// alike.
static LayoutScene OpenedSceneWrapOff(Func<LayoutTree> buildEgo, LayoutConfig cfg)
{
    int ignored = 0;
    LayoutConfig off = cfg.WithWrap(WrapMode.Off);
    LayoutResult ego = LayoutEngine.Run(buildEgo(), off);
    List<LayoutPart> parts = StockStubs(ego, "wrap-off", ref ignored);
    parts[0] = ScenePart(() => SyntheticTree.BuildBarycenterSystem("AlphaCentauri", "4.344 ly", "3 stars, 2 planets"), ego, "AlphaCentauri.Hub", true, ref ignored, "wrap-off");
    return SceneComposer.Compose(ego, FindNode(ego.Tree, "Sol.Hub"), parts, off);
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
    LayoutResult result = SceneComposer.LayOutPart(build, ego, null, expanded);
    LayoutTree tree = result.Tree;
    OverlapReport report = OverlapCheck.Run(result);
    PrintIssues("node", report.NodeOverlaps);
    PrintIssues("label", report.LabelOverlaps);
    PrintIssues("band", report.BandViolations);
    PrintIssues("bus", report.BusViolations);
    PrintIssues("wrap", report.WrapViolations);
    fails += Check($"{variant}: part {tree.Name} lays out cleanly", report.Ok);
    return new LayoutPart { RootId = rootId, Result = result, Expanded = expanded };
}

// The SceneChecks rules on a 1600x1000 and a 1300x860 canvas, and at 1600x1000 the fit zoom
// against the ego map alone, which must keep minRatio of it when one is given.
static int CheckAtFit(string tag, LayoutScene scene, double aloneFit, double? minRatio)
{
    int fails = 0;
    foreach ((double w, double h) in new[] { (1600.0, 1000.0), (1300.0, 860.0) })
    {
        string at = FormattableString.Invariant($"{tag} at {w}x{h}");
        if (w == 1600.0)
        {
            double ratio = FitPreview.Build(scene, w, h).Zoom / aloneFit;
            Console.WriteLine(FormattableString.Invariant($"  {at}: fit zoom {100 * ratio:F1}% of the ego map alone"));
            if (minRatio is double min)
                fails += Check(FormattableString.Invariant($"{at}: the fit zoom keeps {100 * min:F0}% of the ego map alone ({100 * ratio:F1}%)"), ratio >= min);
        }
        fails += Expect($"{at}: the connectors keep clear", SceneChecks.Connectors(scene, w, h));
        fails += Expect($"{at}: the row breaks keep clear", SceneChecks.WrapLines(scene, w, h));
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

// The route totals the panel shows: the headline is the figure the vehicle bar compares
// against (the round trip when the return is shown), it is never repeated in the detail lines,
// and the plane change is paid on both legs of a round trip.
static int RunRouteTotalsChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[totals] route totals headline and detail lines");
    var details = new List<(string Text, bool Disabled)>();

    static TotalsInput Input(double outbound = 3000.0, double ret = 0.0, double plane = 0.0, double time = 0.0,
        string? aero = null, string? returnAero = null, bool returnTrip = false, int margin = 0,
        bool interstellar = false, string? coast = null)
    {
        return new TotalsInput(outbound, ret, plane, time, aero, returnAero, returnTrip, margin, interstellar, coast);
    }

    RouteTotalsText plain = RouteTotals.Build(Input(), details);
    fails += Check("a plain route heads with its total", plain.Headline == "Total: " + Format.Dv(3000.0) && plain.Needed == 3000.0);
    fails += Check("a plain route has no detail lines and no margin note", details.Count == 0 && plain.MarginLine.Length == 0);

    RouteTotalsText plane = RouteTotals.Build(Input(plane: 200.0, time: 7200.0), details);
    fails += Check("the plane change is in the total", plane.Needed == 3200.0 && plane.Headline == "Total: " + Format.Dv(3200.0));
    fails += Check("the plane change and the transfer time are details",
        details.Count == 2 && details[0].Text == "+ plane change: " + Format.Dv(200.0) && details[1].Text == "Transfer time: 2.0 h");

    RouteTotalsText round = RouteTotals.Build(Input(ret: 2500.0, plane: 200.0, time: 7200.0, returnTrip: true), details);
    fails += Check("a round trip pays the plane change twice", round.Needed == 3200.0 + 2700.0);
    fails += Check("a round trip heads with the round trip", round.Headline == "Round trip: " + Format.Dv(5900.0));
    fails += Check("a round trip lists both legs and a one-way time",
        details.Exists(d => d.Text == "Outbound: " + Format.Dv(3200.0))
        && details.Exists(d => d.Text == "Return: " + Format.Dv(2700.0))
        && details.Exists(d => d.Text == "Transfer time: 2.0 h (one way)"));

    RouteTotalsText margin = RouteTotals.Build(Input(margin: 10, aero: "Mars", returnAero: "Earth", returnTrip: true), details);
    fails += Check("a margin is named under the headline", margin.MarginLine == "incl. +10% piloting margin");
    fails += Check("the aerobrakes are details",
        details.Exists(d => d.Text == "Aerobrake at Mars") && details.Exists(d => d.Text == "Aerobrake on return at Earth"));

    RouteTotalsText leg = RouteTotals.Build(Input(outbound: 90000.0, ret: 500.0, plane: 300.0, returnTrip: true,
        interstellar: true, coast: "Coast: 43.44 yr"), details);
    fails += Check("an interstellar route heads with its trip total", leg.Headline == "Total: " + Format.Dv(90000.0) && leg.Needed == 90000.0);
    fails += Check("an interstellar route lists only its coast time", details.Count == 1 && details[0].Text == "Coast: 43.44 yr");

    bool neverRepeated = true;
    foreach (TotalsInput input in new[] { Input(), Input(plane: 1.0, returnTrip: true, ret: 5.0), Input(interstellar: true, coast: "c") })
    {
        RouteTotalsText text = RouteTotals.Build(in input, details);
        neverRepeated &= !details.Exists(d => d.Text == text.Headline || d.Text.StartsWith("Total:") || d.Text.StartsWith("Round trip:"));
    }
    fails += Check("the headline is never a detail line", neverRepeated);

    Console.WriteLine(fails == 0 ? "  totals: OK" : $"  totals: {fails} FAIL");
    return fails;
}

// The fold of a hub's minor bodies: nothing folds up to the threshold, a star hub folds every
// minor child into one group, a planet hub keeps its inner band as lanes up to the threshold and
// folds the rest by band, a revealed body is always a lane, and no group holds a single body.
static int RunMinorFoldChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[fold] minor-body groups and the bands of a planet hub");
    const int threshold = BuildOptions.DefaultMinorGroupThreshold;
    const double soi = 50e9;
    double edge = MinorFold.InnerBandSoiFraction * soi;

    static FoldChild Minor(double radius, MinorKind kind = MinorKind.Other, bool included = true, bool revealed = false)
    {
        return new FoldChild(included, true, revealed, kind, radius);
    }

    static int Count<T>(T[] slots, T slot) where T : struct, Enum
    {
        int n = 0;
        foreach (T s in slots)
        {
            if (s.Equals(slot))
                n++;
        }
        return n;
    }

    static List<FoldChild> Many(int count, double from, double to, MinorKind kind = MinorKind.Other)
    {
        var list = new List<FoldChild>(count);
        for (int i = 0; i < count; i++)
            list.Add(Minor(from + (to - from) * i / Math.Max(1, count - 1), kind));
        return list;
    }

    FoldSlot[] atThreshold = MinorFold.Plan(Many(threshold, 2.0 * edge, 4.0 * edge), threshold, soi);
    fails += Check("up to the threshold every minor body is a lane", Count(atThreshold, FoldSlot.Lane) == threshold);

    List<FoldChild> hidden = Many(threshold, 2.0 * edge, 4.0 * edge);
    hidden.Add(Minor(3.0 * edge, included: false));
    fails += Check("a hidden body counts toward nothing", Count(MinorFold.Plan(hidden, threshold, soi), FoldSlot.Hidden) == 1
        && Count(MinorFold.Plan(hidden, threshold, soi), FoldSlot.Lane) == threshold);

    List<FoldChild> belt = Many(2892, 1e11, 5e11, MinorKind.Asteroid);
    belt.Insert(0, new FoldChild(true, false, false, MinorKind.Other, 5e10));
    FoldSlot[] starHub = MinorFold.Plan(belt, threshold, hubSoi: null);
    fails += Check("a star hub folds every minor body into one group",
        Count(starHub, FoldSlot.Group) == 2892 && starHub[0] == FoldSlot.Lane && Count(starHub, FoldSlot.OuterGroup) == 0);
    fails += Check("an asteroid belt is labelled as asteroids", MinorFold.Label(FoldSlot.Group, Enumerable.Repeat(MinorKind.Asteroid, 2892).ToArray()) == "+2892 asteroids");
    fails += Check("a mixed star group is minor bodies", MinorFold.Label(FoldSlot.Group, new[] { MinorKind.Asteroid, MinorKind.Comet, MinorKind.Other }) == "+3 minor bodies");
    fails += Check("a comet group is comets", MinorFold.Label(FoldSlot.Group, new[] { MinorKind.Comet, MinorKind.Comet }) == "+2 comets");

    // Saturn as a custom system has it: ten moons inside the band, 250 far outside.
    List<FoldChild> saturn = Many(10, 0.01 * edge, 0.9 * edge);
    saturn.AddRange(Many(250, 2.0 * edge, 5.0 * edge));
    FoldSlot[] saturnSlots = MinorFold.Plan(saturn, threshold, soi);
    fails += Check("the inner band of a planet is lanes", Count(saturnSlots, FoldSlot.Lane) == 10);
    fails += Check("the outer swarm of a planet is one group", Count(saturnSlots, FoldSlot.OuterGroup) == 250 && Count(saturnSlots, FoldSlot.InnerGroup) == 0);
    fails += Check("an outer group is labelled as outer moons", MinorFold.Label(FoldSlot.OuterGroup, new MinorKind[250]) == "+250 outer moons");

    List<FoldChild> crowded = Many(30, 0.1 * edge, 0.9 * edge);
    crowded.AddRange(Many(20, 2.0 * edge, 3.0 * edge));
    crowded.Reverse();
    FoldSlot[] crowdedSlots = MinorFold.Plan(crowded, threshold, soi);
    bool innermostAreLanes = true;
    for (int i = 0; i < crowded.Count; i++)
    {
        if (crowdedSlots[i] == FoldSlot.InnerGroup)
        {
            foreach (int j in Enumerable.Range(0, crowded.Count))
                innermostAreLanes &= !(crowdedSlots[j] == FoldSlot.Lane && crowded[j].OrbitRadius > crowded[i].OrbitRadius);
        }
    }
    fails += Check("a full inner band keeps the threshold as lanes and folds the rest",
        Count(crowdedSlots, FoldSlot.Lane) == threshold && Count(crowdedSlots, FoldSlot.InnerGroup) == 6 && Count(crowdedSlots, FoldSlot.OuterGroup) == 20);
    fails += Check("the innermost moons get the lanes, whatever the input order", innermostAreLanes);
    fails += Check("the band edge belongs to the outer band",
        MinorFold.Plan(Prepend(Many(threshold, 2.0 * edge, 3.0 * edge), Minor(edge)), threshold, soi)[0] == FoldSlot.OuterGroup);

    List<FoldChild> revealed = Many(40, 2.0 * edge, 3.0 * edge);
    revealed[5] = Minor(revealed[5].OrbitRadius, revealed: true);
    FoldSlot[] revealedSlots = MinorFold.Plan(revealed, threshold, soi);
    fails += Check("a revealed body is a lane outside the group", revealedSlots[5] == FoldSlot.Lane && Count(revealedSlots, FoldSlot.OuterGroup) == 39);

    List<FoldChild> pair = Many(threshold - 1, 0.1 * edge, 0.9 * edge);
    pair.AddRange(Many(2, 2.0 * edge, 3.0 * edge));
    FoldSlot[] pairSlots = MinorFold.Plan(pair, threshold, soi);
    pair[threshold - 1] = pair[threshold - 1] with { Revealed = true };
    FoldSlot[] pairRevealed = MinorFold.Plan(pair, threshold, soi);
    LaneRole[] pairRoles = MinorFold.Roles(pair.ConvertAll(c => c.Revealed), pairRevealed, pairSlots);
    fails += Check("revealing one member of a group of two empties the group", Count(pairSlots, FoldSlot.OuterGroup) == 2 && Count(pairRevealed, FoldSlot.OuterGroup) == 0);
    fails += Check("the revealed member is a revealed lane and the other one stands in for the group",
        pairRoles[threshold - 1] == LaneRole.Revealed && pairRoles[threshold] == LaneRole.StandIn && Count(pairRoles, LaneRole.Plain) == threshold - 1);

    List<FoldChild> budget = Many(threshold + 3, 0.1 * edge, 0.9 * edge);
    FoldSlot[] budgetSlots = MinorFold.Plan(budget, threshold, soi);
    budget[0] = budget[0] with { Revealed = true };
    LaneRole[] budgetRoles = MinorFold.Roles(budget.ConvertAll(c => c.Revealed), MinorFold.Plan(budget, threshold, soi), budgetSlots);
    fails += Check("a reveal that frees a lane of the budget marks the lane it adds as revealed and keeps the group",
        budgetRoles[0] == LaneRole.Plain && budgetRoles[threshold] == LaneRole.Revealed && Count(budgetRoles, LaneRole.StandIn) == 0);

    List<FoldChild> single = Many(threshold, 0.1 * edge, 0.9 * edge);
    single.Add(Minor(2.0 * edge));
    FoldSlot[] singleSlots = MinorFold.Plan(single, threshold, soi);
    fails += Check("a group of one keeps its lane", Count(singleSlots, FoldSlot.Lane) == threshold + 1);

    fails += Check("the group ids differ per band", MinorFold.IdSuffix(FoldSlot.Group) == ".MinorGroup"
        && MinorFold.IdSuffix(FoldSlot.InnerGroup) != MinorFold.IdSuffix(FoldSlot.OuterGroup));

    // The member list of a dense belt draws a capped number of rows and counts the rest.
    var names = new List<string>();
    for (int i = 0; i < 3170; i++)
        names.Add("S" + (2000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture) + "_S_07");
    var shown = new List<int>();
    int all = GroupListFilter.Apply(names, "  ", 200, shown);
    fails += Check("a blank filter matches every row and draws the cap", all == 3170 && shown.Count == 200 && shown[199] == 199);
    int some = GroupListFilter.Apply(names, "s2004_", 200, shown);
    fails += Check("the filter ignores case", some == 1 && shown.Count == 1 && names[shown[0]] == "S2004_S_07");
    fails += Check("no match draws nothing", GroupListFilter.Apply(names, "Titan", 200, shown) == 0 && shown.Count == 0);

    Console.WriteLine(fails == 0 ? "  fold: OK" : $"  fold: {fails} FAIL");
    return fails;

    static List<FoldChild> Prepend(List<FoldChild> list, FoldChild first)
    {
        list.Insert(0, first);
        return list;
    }
}

// The badge and aerobrake anchors on laid-out trees in every mode. A ladder edge reads on its
// last segment, a GravityWell transfer on its first, and the aerobrake chord runs from the
// first polyline point to the last.
static int RunEdgeGeometryChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[edge-geometry] badge segment and aerobrake chord");

    foreach (LayoutMode mode in new[] { LayoutMode.CumulativeDown, LayoutMode.GravityWell, LayoutMode.Spring })
    {
        var cfg = new LayoutConfig { Mode = mode };
        int edges = 0;
        int segmentFails = 0;
        int chordFails = 0;
        foreach (Func<LayoutTree> build in new Func<LayoutTree>[] { SyntheticTree.BuildStockLikePlanetRoot, SyntheticTree.BuildCruiseRootArrival, SyntheticTree.BuildDenseMoons })
        {
            LayoutResult result = LayoutEngine.Run(build(), cfg);
            foreach (LayoutNode node in result.Tree.Nodes)
            {
                foreach (LayoutEdge edge in node.Out)
                {
                    IReadOnlyList<LayoutPoint> pts = edge.Polyline;
                    if (edge.IsHubLink || pts.Count < 2)
                        continue;
                    edges++;
                    int expected = mode == LayoutMode.GravityWell && edge.Class == EdgeClass.Transfer ? 0 : pts.Count - 2;
                    int segment = EdgeGeometry.DvSegment(edge, mode);
                    LayoutPoint mid = EdgeGeometry.DvSegmentMidpoint(edge, mode);
                    bool midOk = mid.X == (pts[expected].X + pts[expected + 1].X) / 2.0
                        && mid.Y == (pts[expected].Y + pts[expected + 1].Y) / 2.0;
                    if (segment != expected || !midOk)
                        segmentFails++;
                    (LayoutPoint from, LayoutPoint to) = EdgeGeometry.DvChord(edge);
                    if (from != pts[0] || to != pts[pts.Count - 1])
                        chordFails++;
                }
            }
        }
        Console.WriteLine($"  {mode}: {edges} edges checked");
        fails += Check($"{mode} edges exist", edges > 0);
        fails += Check($"{mode} badge sits on the metric segment", segmentFails == 0);
        fails += Check($"{mode} aerobrake chord runs from the first point to the last", chordFails == 0);
    }

    // An edge into a lower row of a wrapped fan runs from its hub over to the trunk, down the
    // trunk to the row, then along the run and the drop of its dV part. The badge and the chord
    // must stay on the dV part in every mode.
    var hub = new LayoutNode { Id = "Hub", Label = "Hub", Kind = LayoutKind.LowOrbit };
    var moon = new LayoutNode { Id = "Moon", Label = "Moon", Kind = LayoutKind.LowOrbit };
    var wrapped = new LayoutEdge
    {
        From = hub,
        To = moon,
        Class = EdgeClass.Transfer,
        Polyline = new[]
        {
            new LayoutPoint(0, 0), new LayoutPoint(-70, 0), new LayoutPoint(-70, 420),
            new LayoutPoint(210, 420), new LayoutPoint(210, 840)
        },
        DvStart = 2
    };
    foreach (LayoutMode mode in new[] { LayoutMode.CumulativeDown, LayoutMode.GravityWell, LayoutMode.Spring })
    {
        int segment = EdgeGeometry.DvSegment(wrapped, mode);
        int expected = mode == LayoutMode.GravityWell ? 2 : 3;
        fails += Check($"{mode}: the badge of a wrapped edge sits on its dV part", segment == expected && segment >= wrapped.DvStart);
    }
    (LayoutPoint chordFrom, LayoutPoint chordTo) = EdgeGeometry.DvChord(wrapped);
    fails += Check("the aerobrake chord of a wrapped edge starts where its dV part starts",
        chordFrom == new LayoutPoint(-70, 420) && chordTo == new LayoutPoint(210, 840));
    return fails;
}

// The map config comes from ForMap, which must lay out exactly like a plain config of the same
// mode while the wrap is Off, and a destination system must never inherit the wrap of the ego map.
static int RunWrapConfigChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[wrap-config] map config factory and destination parts");

    foreach (LayoutMode mode in new[] { LayoutMode.CumulativeDown, LayoutMode.GravityWell, LayoutMode.Spring })
    {
        LayoutConfig one = LayoutConfig.ForMap(mode, hasSeveralSystems: false, WrapMode.Auto);
        LayoutConfig several = LayoutConfig.ForMap(mode, hasSeveralSystems: true, WrapMode.Off);
        fails += Check($"{mode}: ForMap keeps the mode and the wrap", one.Mode == mode && one.Wrap == WrapMode.Auto
            && several.Mode == mode && several.Wrap == WrapMode.Off);
        fails += Check($"{mode}: ForMap targets 1.6 for one system and 2.4 for several", one.WrapTargetAspect == 1.6 && several.WrapTargetAspect == 2.4);

        LayoutResult plain = LayoutEngine.Run(SyntheticTree.BuildStockLikePlanetRoot(), new LayoutConfig { Mode = mode });
        LayoutResult map = LayoutEngine.Run(SyntheticTree.BuildStockLikePlanetRoot(), several);
        fails += Check($"{mode}: ForMap with the wrap Off lays out as a plain config", Snapshot(map.Tree.Nodes) == Snapshot(plain.Tree.Nodes));

        LayoutConfig spread = one.WithSpread(2.0, 2, 4);
        LayoutConfig copy = one.WithoutWrap();
        fails += Check($"{mode}: WithSpread leaves the wrap Off", spread.Wrap == WrapMode.Off);
        fails += Check($"{mode}: WithoutWrap is an exact copy with the wrap Off", copy.Wrap == WrapMode.Off && copy.Mode == mode
            && copy.SpreadX == one.SpreadX && copy.MinBandStep == one.MinBandStep
            && copy.MaxBandStep == one.MaxBandStep && copy.WellMaxBandStep == one.WellMaxBandStep);

        LayoutResult ego = LayoutEngine.Run(SyntheticTree.BuildStockLikePlanetRoot(), LayoutConfig.ForMap(mode, hasSeveralSystems: true, WrapMode.Auto));
        LayoutResult part = SceneComposer.LayOutPart(() => SyntheticTree.BuildSystemStub("AlphaCentauri", true, "4.344 ly", "3 stars, 2 planets"), ego, null, expanded: false);
        fails += Check($"{mode}: a collapsed destination has the wrap Off under a wrapping ego map", ego.Config.Wrap == WrapMode.Auto && part.Config.Wrap == WrapMode.Off);
        LayoutResult opened = SceneComposer.LayOutPart(() => SyntheticTree.BuildStarSystem("TauCeti", "11.91 ly", "1 star, 4 planets"), ego, null, expanded: true);
        WrapMode openedWrap = mode == LayoutMode.GravityWell ? WrapMode.Auto : WrapMode.Off;
        fails += Check($"{mode}: an opened destination has the wrap {openedWrap} under a wrapping ego map", opened.Config.Wrap == openedWrap
            && opened.Config.WrapTargetAspect == ego.Config.WrapTargetAspect);
    }

    bool offGrid = false;
    try
    {
        new LayoutConfig { WrapGutterPx = 50.0 }.Validate();
    }
    catch (ArgumentException)
    {
        offGrid = true;
    }
    fails += Check("Validate rejects a wrap gutter that is not a whole multiple of GridPx", offGrid);

    Console.WriteLine(fails == 0 ? "  wrap-config: OK" : $"  wrap-config: {fails} FAIL");
    return fails;
}

// The fit zoom of a map shown alone on a 1600x1000 canvas, down to the lowest zoom the map
// window allows, so the fit of a dense map is not clamped and the gain of its rows shows in full.
static double AloneFit(LayoutResult result)
{
    return SceneFit.Fit(SceneComposer.Compose(result, null, new List<LayoutPart>(), result.Config), 1600, 1000, 0.02, 4.0, out _, out _);
}

// The fit zoom of a laid-out map on the nominal canvas the row chooser scores against.
static double TargetFit(LayoutResult result)
{
    double w = SceneComposer.NominalWidthPx;
    double h = Math.Min(SceneComposer.NominalHeightPx, w / result.Config.WrapTargetAspect);
    return Math.Min(w / result.Width, h / result.Height);
}

// The median screen distance at a zoom between two neighbouring transfer lanes of one hub in one row.
static double MedianLanePitch(LayoutResult result, double zoom)
{
    var pitches = new List<double>();
    foreach (LayoutNode node in result.Tree.Nodes)
    {
        var byRow = new Dictionary<int, List<double>>();
        foreach (LayoutEdge edge in node.Out)
        {
            if (edge.Class != EdgeClass.Transfer)
                continue;
            if (!byRow.TryGetValue(edge.To.WrapRow, out List<double>? xs))
            {
                xs = new List<double>();
                byRow[edge.To.WrapRow] = xs;
            }
            xs.Add(edge.To.SnappedX);
        }
        foreach (List<double> xs in byRow.Values)
        {
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i++)
                pitches.Add((xs[i + 1] - xs[i]) * zoom);
        }
    }
    if (pitches.Count == 0)
        return 0.0;
    pitches.Sort();
    return pitches[pitches.Count / 2];
}

static int TotalRows(LayoutResult result)
{
    int rows = 0;
    foreach (LayoutWrap wrap in result.Wraps)
        rows += wrap.RowSizes.Count;
    return rows;
}

static string WrapState(LayoutResult result)
{
    var sb = new System.Text.StringBuilder();
    foreach (LayoutNode node in result.Tree.Nodes)
        sb.Append(FormattableString.Invariant($"{node.Id}:{node.WrapRow},{node.Shelf},{node.WrapDy};"));
    foreach (LayoutWrap wrap in result.Wraps)
        sb.Append(FormattableString.Invariant($"{wrap.ParentId}:{string.Join("/", wrap.RowSizes)}:{string.Join("/", wrap.RowDy)}:{wrap.TrunkX}|"));
    return sb.ToString();
}

// The Cumulative-down maps the row wrap is judged on, by a short name.
static (string Name, Func<LayoutTree> Build)[] WrapCases()
{
    return new (string, Func<LayoutTree>)[]
    {
        ("planet-root", SyntheticTree.BuildStockLikePlanetRoot),
        ("dense-aggregated", SyntheticTree.BuildDenseAggregated),
        ("real-like-sol", SyntheticTree.BuildRealLikeSolRoot),
        ("moon-root", SyntheticTree.BuildLargeMoonRoot),
        ("dense-moons", SyntheticTree.BuildDenseMoons),
        ("clamped-giants", SyntheticTree.BuildClampedGiants),
        ("wide-fan", SyntheticTree.BuildWideFan),
        ("flat-fan-12", () => SyntheticTree.BuildFlatFan(12)),
        ("flat-fan-13", () => SyntheticTree.BuildFlatFan(13)),
        ("flat-fan-26", () => SyntheticTree.BuildFlatFan(26)),
        ("flat-fan-60", () => SyntheticTree.BuildFlatFan(60)),
        ("many-moons", SyntheticTree.BuildManyMoons),
        ("opaque-block", SyntheticTree.BuildOpaqueBlock)
    };
}

// What the rows gain in Cumulative-down, as the fit zoom of the map alone with the wrap Auto
// against Off. The stock-like and real-like maps have no fan wide enough, so they must not change
// at all. The dense and the clamped giants must gain clearly. Every map that wraps is locked
// within two percent of the fit it reaches with these tunables.
static int RunWrapGates()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[wrap] fit zoom of the map alone, wrap Auto against Off, Cumulative-down");

    var locked = new Dictionary<string, double>
    {
        ["dense-moons"] = 0.143,
        ["clamped-giants"] = 0.098,
        ["wide-fan"] = 0.291,
        ["flat-fan-26"] = 0.262,
        ["flat-fan-60"] = 0.184,
        ["many-moons"] = 0.258
    };

    LayoutConfig auto = LayoutConfig.Default.WithWrap(WrapMode.Auto);
    foreach ((string name, Func<LayoutTree> build) in WrapCases())
    {
        LayoutResult off = LayoutEngine.Run(build(), LayoutConfig.Default);
        LayoutResult wrapped = LayoutEngine.Run(build(), auto);
        double fOff = AloneFit(off);
        double fAuto = AloneFit(wrapped);
        double fill = wrapped.Height * fAuto / 936.0;
        double pitch = MedianLanePitch(wrapped, fAuto);
        Console.WriteLine(FormattableString.Invariant(
            $"[wrap] {name} CD fit {fAuto:F3} ({100.0 * (fAuto / fOff - 1.0):+0;-0;+0}%), Off {fOff:F3}, {wrapped.Wraps.Count} wrapped fans in {TotalRows(wrapped)} rows, bounds {wrapped.Width:F0}x{wrapped.Height:F0}, fill {100.0 * fill:F0}%, lane pitch {pitch:F1} px"));
        fails += Check($"{name}: the rows never lower the fit", fAuto >= fOff - 1e-12);
        if (locked.TryGetValue(name, out double reached))
        {
            fails += Check(FormattableString.Invariant($"{name}: the fit keeps 98% of the {reached:F3} reached"), fAuto >= reached * 0.98);
            fails += Check($"{name}: a fan wraps", wrapped.Wraps.Count > 0);
        }

        switch (name)
        {
            case "planet-root":
            case "dense-aggregated":
            case "real-like-sol":
            case "moon-root":
            case "flat-fan-12":
                fails += Check($"{name}: no fan wraps and the layout is the Off layout",
                    wrapped.Wraps.Count == 0 && LayoutDumpFormat.ToText(wrapped) == LayoutDumpFormat.ToText(off));
                fails += Check($"{name}: no fan wraps even with the most rows forced",
                    LayoutEngine.RunWithRowCap(build(), auto, auto.WrapMaxRowsCd).Wraps.Count == 0);
                break;
            case "flat-fan-13":
                // One lane over the minimum makes the fan a candidate, and with two rows forced it
                // wraps cleanly. Alone it gains no tenth of fit from a second row, so Auto must keep
                // it in one row, laid out exactly as with the wrap Off.
                fails += Check($"{name}: Auto keeps the fan in one row, because a second row does not pay for itself",
                    wrapped.Wraps.Count == 0 && LayoutDumpFormat.ToText(wrapped) == LayoutDumpFormat.ToText(off));
                LayoutResult forced = LayoutEngine.RunWithRowCap(build(), auto, 2);
                fails += Check($"{name}: the fan wraps into two clean rows when two rows are forced",
                    forced.Wraps.Count == 1 && forced.Wraps[0].RowSizes.Count == 2 && OverlapCheck.Run(forced).Ok);
                break;
            case "dense-moons":
                fails += Check(FormattableString.Invariant($"{name}: the fit is at least 0.136 ({fAuto:F3})"), fAuto >= 0.136);
                fails += Check(FormattableString.Invariant($"{name}: the map fills at least 40% of the height ({100.0 * fill:F0}%)"), fill >= 0.40);
                fails += Check(FormattableString.Invariant($"{name}: the median lane pitch at fit is at least 24 px ({pitch:F1})"), pitch >= 24.0);
                break;
            case "clamped-giants":
                fails += Check(FormattableString.Invariant($"{name}: the fit is at least 1.6 times Off ({fAuto / fOff:F2})"), fAuto >= 1.6 * fOff);
                break;
        }
    }
    return fails;
}

// The GravityWell maps the rows are judged on. Next to the Cumulative-down cases they hold the
// cruise roots, whose planets are bus nodes, and the roots of multiple star systems.
static (string Name, Func<LayoutTree> Build)[] WellWrapCases()
{
    return new (string, Func<LayoutTree>)[]
    {
        ("planet-root", SyntheticTree.BuildStockLikePlanetRoot),
        ("dense-aggregated", SyntheticTree.BuildDenseAggregated),
        ("real-like-sol", SyntheticTree.BuildRealLikeSolRoot),
        ("moon-root", SyntheticTree.BuildLargeMoonRoot),
        ("arrival", SyntheticTree.BuildArrivalDemo),
        ("cruise-root", SyntheticTree.BuildCruiseRoot),
        ("cruise-arrival", SyntheticTree.BuildCruiseRootArrival),
        ("barycenter-root", SyntheticTree.BuildBarycenterRoot),
        ("orbiting-star-root", SyntheticTree.BuildOrbitingStarRoot),
        ("dense-moons", SyntheticTree.BuildDenseMoons),
        ("clamped-giants", SyntheticTree.BuildClampedGiants),
        ("wide-fan", SyntheticTree.BuildWideFan),
        ("flat-fan-12", () => SyntheticTree.BuildFlatFan(12)),
        ("flat-fan-13", () => SyntheticTree.BuildFlatFan(13)),
        ("flat-fan-26", () => SyntheticTree.BuildFlatFan(26)),
        ("flat-fan-60", () => SyntheticTree.BuildFlatFan(60)),
        ("many-moons", SyntheticTree.BuildManyMoons)
    };
}

// What the rows and shelves gain in GravityWell, as the fit zoom of the map alone with the wrap
// Auto against Off. The stock-like, the real-like and the dense map must each gain clearly, and
// every map that wraps is locked within two percent of the fit it reaches with these tunables.
static int RunWellWrapGates()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[wrap] fit zoom of the map alone, wrap Auto against Off, GravityWell");

    var locked = new Dictionary<string, double>
    {
        ["planet-root"] = 0.693,
        ["dense-aggregated"] = 0.624,
        ["real-like-sol"] = 0.399,
        ["moon-root"] = 0.343,
        ["cruise-root"] = 0.566,
        ["cruise-arrival"] = 0.675,
        ["barycenter-root"] = 1.286,
        ["orbiting-star-root"] = 1.284,
        ["dense-moons"] = 0.344,
        ["clamped-giants"] = 0.293,
        ["wide-fan"] = 0.646,
        ["flat-fan-12"] = 0.842,
        ["flat-fan-13"] = 0.742,
        ["flat-fan-26"] = 0.691,
        ["flat-fan-60"] = 0.452,
        ["many-moons"] = 0.632
    };

    var plain = new LayoutConfig { Mode = LayoutMode.GravityWell };
    LayoutConfig auto = plain.WithWrap(WrapMode.Auto);
    foreach ((string name, Func<LayoutTree> build) in WellWrapCases())
    {
        LayoutResult off = LayoutEngine.Run(build(), plain);
        LayoutResult wrapped = LayoutEngine.Run(build(), auto);
        double fOff = AloneFit(off);
        double fAuto = AloneFit(wrapped);
        double fill = wrapped.Height * fAuto / 936.0;
        double pitch = MedianLanePitch(wrapped, fAuto);
        int shelves = 1;
        foreach (LayoutWrap wrap in wrapped.Wraps)
        {
            if (!wrap.Balanced)
                shelves = wrap.RowSizes.Count;
        }
        Console.WriteLine(FormattableString.Invariant(
            $"[wrap] {name} GW fit {fAuto:F3} ({100.0 * (fAuto / fOff - 1.0):+0;-0;+0}%), Off {fOff:F3}, {wrapped.Wraps.Count} wrapped hubs in {TotalRows(wrapped)} rows, {shelves} shelves, bounds {wrapped.Width:F0}x{wrapped.Height:F0}, fill {100.0 * fill:F0}%, lane pitch {pitch:F1} px"));
        fails += Check($"{name}: the rows never lower the fit in GravityWell", fAuto >= fOff - 1e-12);
        if (locked.TryGetValue(name, out double reached))
        {
            fails += Check(FormattableString.Invariant($"{name}: the GravityWell fit keeps 98% of the {reached:F3} reached"), fAuto >= reached * 0.98);
            fails += Check($"{name}: a GravityWell hub wraps", wrapped.Wraps.Count > 0);
        }

        switch (name)
        {
            case "arrival":
                fails += Check($"{name}: no GravityWell hub wraps and the layout is the Off layout",
                    wrapped.Wraps.Count == 0 && LayoutDumpFormat.ToText(wrapped) == LayoutDumpFormat.ToText(off));
                break;
            case "planet-root":
                fails += Check(FormattableString.Invariant($"{name}: the GravityWell fit is at least 0.42 ({fAuto:F3})"), fAuto >= 0.42);
                break;
            case "real-like-sol":
                fails += Check(FormattableString.Invariant($"{name}: the GravityWell fit is at least 0.29 ({fAuto:F3})"), fAuto >= 0.29);
                break;
            case "dense-moons":
                fails += Check(FormattableString.Invariant($"{name}: the GravityWell fit is at least 0.24 ({fAuto:F3})"), fAuto >= 0.24);
                fails += Check(FormattableString.Invariant($"{name}: the GravityWell median lane pitch at fit is at least 24 px ({pitch:F1})"), pitch >= 24.0);
                break;
        }
    }
    return fails;
}

// The chooser scores each row cap once on the nominal canvas and takes one more row only for a
// gain of WrapMinGain, so the chosen cap scores best of the caps up to it, no later cap beats it
// by the gain, and it beats one row by the gain unless it is one row. The full layouts of the
// three best caps confirm the score, because each one fits within 5% of its score, and the
// chosen one fits within 1% of the best fit short of the gain a further row must bring. In
// GravityWell a candidate is a pair of a moon row cap and a shelf count, in the order the
// chooser reads them.
static int RunChooserChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[wrap-chooser] row caps scored on the nominal canvas");

    foreach ((LayoutMode mode, bool several) in new[]
    {
        (LayoutMode.CumulativeDown, false), (LayoutMode.CumulativeDown, true),
        (LayoutMode.GravityWell, false), (LayoutMode.GravityWell, true)
    })
    {
        bool well = mode == LayoutMode.GravityWell;
        LayoutConfig cfg = LayoutConfig.ForMap(mode, several, WrapMode.Auto);
        LayoutResult Forced(Func<LayoutTree> build, RowWrap.Candidate c) => well
            ? LayoutEngine.RunWithRows(build(), cfg, c.RowCap, c.Shelves)
            : LayoutEngine.RunWithRowCap(build(), cfg, c.RowCap);
        foreach ((string name, Func<LayoutTree> build) in well ? WellWrapCases() : WrapCases())
        {
            LayoutTree tree = build();
            LayoutResult result = LayoutEngine.Run(tree, cfg);
            List<RowWrap.Candidate> candidates = RowWrap.Candidates(tree, cfg);
            if (candidates.Count == 0)
                continue;
            int chosen = RowWrap.Choose(candidates, cfg.WrapMinGain);
            string at = FormattableString.Invariant($"{name}{(well ? " GW" : "")} at aspect {cfg.WrapTargetAspect}");
            foreach (RowWrap.Candidate c in candidates)
            {
                string shelves = well ? FormattableString.Invariant($" shelves {c.Shelves}") : "";
                bool isChosen = c.RowCap == candidates[chosen].RowCap && c.Shelves == candidates[chosen].Shelves;
                Console.WriteLine(FormattableString.Invariant(
                    $"  {at}: cap {c.RowCap}{shelves}{(isChosen ? " (chosen)" : "")}: {c.Fans} fans in {c.Rows} rows, {c.Width:F0}x{c.Height:F0}, score {c.Score:F4}, gap {c.GapPx:F0}, gutter {c.GutterPx:F0}"));
            }

            RowWrap.Candidate pick = candidates[chosen];
            bool rules = chosen == 0 || pick.Score >= candidates[0].Score * (1.0 + cfg.WrapMinGain);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i < chosen)
                    rules &= candidates[i].Score < pick.Score;
                else if (i > chosen)
                    rules &= candidates[i].Score < pick.Score * (1.0 + cfg.WrapMinGain);
            }
            fails += Check($"{at}: the chosen cap scores best, gains enough and keeps fewer rows on a tie", rules);
            fails += Check($"{at}: the layout wraps the chosen cap", result.Wraps.Count == pick.Fans && TotalRows(result) == pick.Rows);

            var top = new List<RowWrap.Candidate>(candidates);
            top.Sort((a, b) => b.Score.CompareTo(a.Score));
            double best = 0.0;
            bool agree = true;
            for (int i = 0; i < Math.Min(3, top.Count); i++)
            {
                double fit = TargetFit(Forced(build, top[i]));
                best = Math.Max(best, fit);
                agree &= Math.Abs(fit / top[i].Score - 1.0) <= 0.05;
            }
            double picked = TargetFit(Forced(build, pick));
            fails += Check($"{at}: the full layouts of the three best caps fit within 5% of their scores", agree);
            fails += Check(FormattableString.Invariant($"{at}: the full layout of the chosen cap fits within 1% of the best short of the gain ({picked:F4} of {best:F4})"),
                picked * (1.0 + cfg.WrapMinGain) >= 0.99 * best);
        }
    }
    return fails;
}

// A reveal must not move any other lane between rows or shelves, and the plan is the same on
// every run.
static int RunWrapStability()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[wrap-stability] a reveal and repeated runs keep the rows");

    foreach (LayoutMode mode in new[] { LayoutMode.CumulativeDown, LayoutMode.GravityWell })
        fails += RunWrapStabilityIn(new LayoutConfig { Mode = mode }.WithWrap(WrapMode.Auto));
    return fails;
}

static int RunWrapStabilityIn(LayoutConfig cfg)
{
    int fails = 0;
    string at = cfg.Mode == LayoutMode.GravityWell ? "GravityWell: " : "";
    const string revealedMoon = "S2014_S_05";
    LayoutResult plain = LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), cfg);
    LayoutResult revealed = LayoutEngine.Run(SyntheticTree.BuildClampedGiants(revealedMoon), cfg);
    LayoutNode lane = FindNode(revealed.Tree, revealedMoon + ".LO");
    fails += Check($"{at}the revealed moon is a revealed lane of Saturn", lane.IsRevealed && lane.Parent!.Id == "Saturn.LO");

    var before = new Dictionary<string, (int Row, int Shelf)>();
    foreach (LayoutNode node in plain.Tree.Nodes)
        before[node.Id] = (node.WrapRow, node.Shelf);
    int moved = 0;
    foreach (LayoutNode node in revealed.Tree.Nodes)
    {
        if (before.TryGetValue(node.Id, out (int Row, int Shelf) was) && was != (node.WrapRow, node.Shelf))
        {
            moved++;
            Console.WriteLine($"  moved: {node.Id} from row {was.Row} shelf {was.Shelf} to row {node.WrapRow} shelf {node.Shelf}");
        }
    }
    LayoutWrap saturn = revealed.Wraps.First(w => w.ParentId == "Saturn.LO");
    int previousRow = -1;
    foreach (LayoutEdge edge in lane.Parent!.Out)
    {
        if (edge.To == lane)
            break;
        if (edge.Class != EdgeClass.Ladder && !edge.IsHubLink)
            previousRow = edge.To.WrapRow;
    }
    Console.WriteLine($"  {at}the revealed lane sits in row {lane.WrapRow} of {saturn.RowSizes.Count}, {moved} other nodes changed row or shelf");
    fails += Check($"{at}revealing one moon of Saturn's group moves no other node to another row or shelf", moved == 0 && plain.Wraps.Count > 0);
    fails += Check($"{at}the revealed lane joins the row of the lane before it", lane.WrapRow == previousRow && lane.WrapRow >= 0);

    string first = WrapState(LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), cfg));
    bool same = true;
    for (int run = 0; run < 2; run++)
        same &= WrapState(LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), cfg)) == first;
    LayoutTree tree = SyntheticTree.BuildClampedGiants();
    LayoutResult once = LayoutEngine.Run(tree, cfg);
    string planned = WrapState(once);
    for (int run = 0; run < 3; run++)
    {
        IReadOnlyList<LayoutWrap> wraps = RowWrap.Plan(tree, cfg);
        same &= wraps.Count == once.Wraps.Count;
        for (int i = 0; same && i < wraps.Count; i++)
            same &= wraps[i].ParentId == once.Wraps[i].ParentId && wraps[i].RowSizes.SequenceEqual(once.Wraps[i].RowSizes) && wraps[i].RowDy.SequenceEqual(once.Wraps[i].RowDy);
    }
    fails += Check($"{at}three runs give the same rows, shelves and offsets", same && planned == first);

    fails += RunPairGroupReveal(cfg, at, "S2023_S_01", "S2000_S_02");
    fails += RunPairGroupReveal(cfg, at, "S2000_S_02", "S2023_S_01");
    return fails;
}

// Revealing one member of a group of two empties the group, and its other member becomes the
// stand-in that takes the group's place. That lane counts like the group did, so no other node
// changes row or shelf, and the stand-in sits in the row and the shelf the group had.
static int RunPairGroupReveal(LayoutConfig cfg, string at, string revealedMoon, string standInMoon)
{
    int fails = 0;
    const string groupId = "Saturn.MinorGroup.Outer";
    LayoutResult plain = LayoutEngine.Run(SyntheticTree.BuildPairGroup(null), cfg);
    LayoutResult revealed = LayoutEngine.Run(SyntheticTree.BuildPairGroup(revealedMoon), cfg);
    LayoutNode group = FindNode(plain.Tree, groupId);
    LayoutNode lane = FindNode(revealed.Tree, revealedMoon + ".LO");
    LayoutNode standIn = FindNode(revealed.Tree, standInMoon + ".LO");
    fails += Check($"{at}the pair group wraps Saturn with the group of two in the last row",
        plain.Wraps.Any(w => w.ParentId == "Saturn.LO") && group.Label == "+2 outer moons" && group.WrapRow >= 1);
    fails += Check($"{at}revealing {revealedMoon} empties the group and leaves {standInMoon} as an unrevealed lane",
        revealed.Tree.Nodes.All(n => n.Id != groupId) && lane.IsRevealed && !standIn.IsRevealed && standIn.Parent!.Id == "Saturn.LO");

    IEnumerable<string> plainOrder = FindNode(plain.Tree, "Saturn.LO").Out.Select(e => e.To.Id);
    IEnumerable<string> revealedOrder = standIn.Parent!.Out.Where(e => e.To != lane).Select(e => e.To == standIn ? groupId : e.To.Id);
    fails += Check($"{at}the stand-in {standInMoon} takes the place of the group in Saturn's order", revealedOrder.SequenceEqual(plainOrder));

    var before = new Dictionary<string, (int Row, int Shelf)>();
    foreach (LayoutNode node in plain.Tree.Nodes)
        before[node.Id] = (node.WrapRow, node.Shelf);
    int moved = 0;
    foreach (LayoutNode node in revealed.Tree.Nodes)
    {
        if (before.TryGetValue(node.Id, out (int Row, int Shelf) was) && was != (node.WrapRow, node.Shelf))
        {
            moved++;
            Console.WriteLine($"  moved: {node.Id} from row {was.Row} shelf {was.Shelf} to row {node.WrapRow} shelf {node.Shelf}");
        }
    }
    Console.WriteLine($"  {at}revealing {revealedMoon} of a group of two: {moved} other nodes changed row or shelf, the stand-in is in row {standIn.WrapRow} shelf {standIn.Shelf}");
    fails += Check($"{at}revealing {revealedMoon} of a group of two moves no other node to another row or shelf", moved == 0);
    fails += Check($"{at}the stand-in {standInMoon} takes the row and the shelf of the group", (standIn.WrapRow, standIn.Shelf) == (group.WrapRow, group.Shelf));
    return fails;
}

// A collapsed destination never wraps, and in Cumulative-down an opened one does not either, the
// plain layout included, even next to a wrapped ego map. An opened GravityWell destination wraps
// by the same rules as the ego map, and its trunks move with it into the strip. Opening and
// closing a destination keeps the ego map as it is.
static int RunWrapParts()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[wrap-parts] destination systems wrap only when opened in GravityWell");

    foreach (LayoutMode mode in new[] { LayoutMode.CumulativeDown, LayoutMode.GravityWell })
        fails += RunWrapPartsIn(LayoutConfig.ForMap(mode, hasSeveralSystems: true, WrapMode.Auto));
    return fails;
}

static int RunWrapPartsIn(LayoutConfig cfg)
{
    int fails = 0;
    bool well = cfg.Mode == LayoutMode.GravityWell;
    string at = well ? "GravityWell: " : "";
    LayoutResult ego = LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), cfg);
    string egoBefore = Snapshot(ego.Tree.Nodes);
    static LayoutTree BuildDense()
    {
        LayoutTree tree = SyntheticTree.BuildFlatFan(26);
        tree.Root.IsRoot = false;
        tree.Root.IsSystemRoot = true;
        return tree;
    }
    static bool Unwrapped(LayoutResult r)
    {
        bool none = r.Wraps.Count == 0 && r.Config.Wrap == WrapMode.Off;
        foreach (LayoutNode node in r.Tree.Nodes)
            none &= node.WrapRow == -1 && node.Shelf == 0 && node.WrapDy == 0.0;
        return none;
    }

    LayoutResult stub = SceneComposer.LayOutPart(BuildDense, ego, null, expanded: false);
    LayoutResult part = SceneComposer.LayOutPart(BuildDense, ego, null, expanded: true);
    Console.WriteLine($"  {at}ego: {ego.Wraps.Count} wrapped fans, collapsed part: {stub.Wraps.Count}, opened part: {part.Wraps.Count} wrapped fans, spread {part.Config.SpreadX}");
    fails += Check($"{at}the ego map wraps", ego.Wraps.Count > 0);
    fails += Check($"{at}a collapsed destination next to a wrapped ego map has no row", Unwrapped(stub));
    if (well)
    {
        fails += Check($"{at}an opened dense destination next to a wrapped ego map wraps", part.Wraps.Count > 0 && part.Config.Wrap == WrapMode.Auto);
        fails += Check($"{at}the rows of the opened destination pass the acceptance check", OverlapCheck.Run(part).Ok);
    }
    else
    {
        fails += Check($"{at}an opened dense destination next to a wrapped ego map has no row", Unwrapped(part));
    }

    var parts = new List<LayoutPart> { new LayoutPart { RootId = "Giant.LO", Result = part, Expanded = true } };
    LayoutScene scene = SceneComposer.Compose(ego, FindNode(ego.Tree, "Sol.Hub"), parts, cfg);
    fails += Check($"{at}opening the destination keeps the ego map", Snapshot(ego.Tree.Nodes) == egoBefore && scene.Parts.Count == 1);
    bool trunksMoved = true;
    foreach (LayoutWrap wrap in part.Wraps)
    {
        LayoutNode hub = FindNode(part.Tree, wrap.ParentId);
        trunksMoved &= wrap.Trunk.Count > 0 && wrap.Trunk[0] == new LayoutPoint(hub.SnappedX, hub.SnappedY) && wrap.Trunk[^1].X == wrap.TrunkX;
    }
    fails += Check($"{at}the trunks of the opened destination move with it into the strip", trunksMoved && (part.Wraps.Count == 0 || parts[0].AppliedDy != 0.0));
    LayoutScene closed = SceneComposer.Compose(ego, FindNode(ego.Tree, "Sol.Hub"), new List<LayoutPart> { new LayoutPart { RootId = "Giant.LO", Result = stub } }, cfg);
    fails += Check($"{at}closing the destination keeps the ego map", Snapshot(ego.Tree.Nodes) == egoBefore && closed.Parts.Count == 1);
    return fails;
}

// A right neighbour whose deep moons sit at the heights of the lower rows of a wrapped fan. The X
// pass must keep the whole block of rows opaque, so no node needs a grid nudge to clear another.
// The deep neighbour makes the map tall, so the chooser alone keeps one row, and the rows are
// forced here.
static int RunOpaqueBlock(string outDir)
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[wrap-block] a wrapped fan is opaque to its neighbours at every depth");

    fails += RunCase(SyntheticTree.BuildOpaqueBlock(), LayoutConfig.Default, "default", outDir, 0);
    LayoutResult result = LayoutEngine.RunWithRowCap(SyntheticTree.BuildOpaqueBlock(), LayoutConfig.Default.WithWrap(WrapMode.Auto), 3);
    OverlapReport report = OverlapCheck.Run(result);
    string tag = result.Tree.Name + "-wrap";
    Console.WriteLine($"[{tag}] {report.Summary()}");
    PrintIssues("node", report.NodeOverlaps);
    PrintIssues("subtree", report.SubtreeOverlaps);
    PrintIssues("label", report.LabelOverlaps);
    PrintIssues("band", report.BandViolations);
    PrintIssues("bus", report.BusViolations);
    PrintIssues("wrap", report.WrapViolations);
    File.WriteAllText(Path.Combine(outDir, tag + ".svg"), LayoutDumpFormat.ToSvg(result));
    File.WriteAllText(Path.Combine(outDir, tag + ".txt"), LayoutDumpFormat.ToText(result));
    fails += Check($"{tag} passes the acceptance check", report.Ok);
    double g = result.Config.GridPx;
    double worst = 0.0;
    foreach (LayoutNode node in result.Tree.Nodes)
        worst = Math.Max(worst, Math.Max(Math.Abs(node.SnappedX - node.X), Math.Abs(node.SnappedY - node.Y)));

    LayoutNode giant = FindNode(result.Tree, "Giant.LO");
    double blockRight = double.NegativeInfinity;
    double rowsTop = double.PositiveInfinity;
    double rowsBottom = double.NegativeInfinity;
    foreach (LayoutEdge edge in giant.Out)
    {
        foreach (LayoutNode node in LayoutTree.PreOrder(edge.To))
        {
            blockRight = Math.Max(blockRight, node.X + node.Width / 2.0);
            if (edge.To.WrapRow >= 1)
            {
                rowsTop = Math.Min(rowsTop, node.Y);
                rowsBottom = Math.Max(rowsBottom, node.Y);
            }
        }
    }
    double deepLeft = double.PositiveInfinity;
    bool deepAtRowHeight = false;
    foreach (LayoutNode node in LayoutTree.PreOrder(FindNode(result.Tree, "Deep.LO")))
    {
        deepLeft = Math.Min(deepLeft, node.X - node.Width / 2.0);
        deepAtRowHeight |= node.Y >= rowsTop && node.Y <= rowsBottom;
    }
    Console.WriteLine(FormattableString.Invariant($"  largest snap move {worst:F1} px, block right edge {blockRight:F0}, deep subtree left edge {deepLeft:F0}"));
    fails += Check("the giant's fan wraps", result.Wraps.Any(w => w.ParentId == "Giant.LO"));
    fails += Check("the deep subtree reaches the heights of the lower rows", deepAtRowHeight);
    fails += Check("the deep subtree stays right of the whole block", deepLeft >= blockRight + result.Config.SiblingGapPx - 1e-6);
    fails += Check(FormattableString.Invariant($"no node moves more than one grid cell in the snap ({worst:F1} px)"), worst <= g + 1e-6);
    return fails;
}

// The real text widths differ from the offline estimate, so the rows must hold for narrower and
// wider text, and wider text never gives fewer rows. The dense and the clamped giants keep at
// every width the gain the rows must bring them, at least 1.6 times the fit with the wrap Off.
static int RunTextWidthChecks()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[wrap-text] the rows at other text widths");

    LayoutConfig cfg = LayoutConfig.Default.WithWrap(WrapMode.Auto);
    foreach ((string name, Func<LayoutTree> build, bool gated) in new (string, Func<LayoutTree>, bool)[]
    {
        ("dense-moons", SyntheticTree.BuildDenseMoons, true),
        ("clamped-giants", SyntheticTree.BuildClampedGiants, true),
        ("wide-fan", SyntheticTree.BuildWideFan, false)
    })
    {
        int lastRows = 0;
        foreach (double charW in new[] { 5.5, 7.0, 9.5 })
        {
            Func<string, double> measure = s => s.Length * charW;
            LayoutResult result = LayoutEngine.Run(build(), cfg, measure);
            OverlapReport report = OverlapCheck.Run(result);
            PrintIssues("wrap", report.WrapViolations);
            int rows = TotalRows(result);
            double fit = AloneFit(result);
            double offFit = AloneFit(LayoutEngine.Run(build(), LayoutConfig.Default, measure));
            Console.WriteLine(FormattableString.Invariant($"  {name} at {charW} px per character: {result.Wraps.Count} wrapped fans in {rows} rows, fit {fit:F3}, Off {offFit:F3} ({fit / offFit:F2} times), {report.Summary()}"));
            fails += Check(FormattableString.Invariant($"{name} at {charW} px per character passes the acceptance check"), report.Ok && result.Wraps.Count > 0);
            fails += Check(FormattableString.Invariant($"{name} at {charW} px per character has no fewer rows than with narrower text"), rows >= lastRows);
            if (gated)
                fails += Check(FormattableString.Invariant($"{name} at {charW} px per character fits at least 1.6 times Off ({fit / offFit:F2})"), fit >= 1.6 * offFit);
            lastRows = rows;
        }
    }
    return fails;
}

// The two GravityWell shapes the rows must handle besides plain moon fans. On the clamped giants
// the star hub wraps its planet systems into shelves while moon fans wrap inside them, and on the
// cruise root the planets are bus nodes, so the bus itself wraps and the bus check keys on the
// grid cell. Each shape passes the acceptance check and fails it when one value is broken the way
// a faulty pass would break it.
static int RunWellWrapFixtures()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[negative-wrap-gw] nested rows and bus shelves in GravityWell");

    LayoutConfig cfg = new LayoutConfig { Mode = LayoutMode.GravityWell }.WithWrap(WrapMode.Auto);
    static List<LayoutEdge> Lanes(LayoutResult r, LayoutWrap wrap, int row)
    {
        var lanes = new List<LayoutEdge>();
        foreach (LayoutEdge edge in FindNode(r.Tree, wrap.ParentId).Out)
        {
            if (edge.Class != EdgeClass.Ladder && edge.To.WrapRow == row)
                lanes.Add(edge);
        }
        return lanes;
    }
    static LayoutWrap Shelves(LayoutResult r) => r.Wraps.First(w => !w.Balanced);
    // A moon fan whose planet sits in a lower shelf of the star hub.
    static LayoutWrap? Nested(LayoutResult r) => r.Wraps.FirstOrDefault(w => w.Balanced && FindNode(r.Tree, w.ParentId).WrapDy > 0.0);

    LayoutResult Giants() => LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), cfg);
    LayoutResult probe = Giants();
    foreach (LayoutWrap wrap in probe.Wraps)
        Console.WriteLine($"  {probe.Tree.Name}: '{wrap.ParentId}' rows {string.Join("/", wrap.RowSizes)}, offsets {string.Join(" ", wrap.RowDy)}{(wrap.Balanced ? "" : ", shelves")}");
    LayoutWrap? shelves = probe.Wraps.FirstOrDefault(w => !w.Balanced);
    fails += Check("the star hub wraps its planet systems into shelves", shelves != null && shelves.ParentId == "Sol.Hub" && shelves.RowSizes.Count >= 2);
    fails += Check("a moon fan wraps inside a lower shelf", Nested(probe) != null);
    fails += Check("the nested rows pass the acceptance check", OverlapCheck.Run(probe).Ok);
    if (shelves == null || Nested(probe) == null)
        return fails;

    fails += ExpectCaught("GravityWell: a moon in the last row of a nested fan moved one grid cell off its dV band", Giants(), r =>
    {
        LayoutWrap fan = Nested(r)!;
        Lanes(r, fan, fan.RowSizes.Count - 1)[0].To.Y += r.Config.GridPx;
    });
    fails += ExpectCaught("GravityWell: a planet in the middle of the first shelf put into the second", Giants(), r =>
    {
        List<LayoutEdge> first = Lanes(r, Shelves(r), 0);
        first[first.Count / 2].To.WrapRow = 1;
    });
    fails += ExpectCaught("GravityWell: the trunk of the star hub moved into its second shelf", Giants(), r =>
    {
        LayoutWrap wrap = Shelves(r);
        wrap.TrunkX = Lanes(r, wrap, 1)[0].To.SnappedX;
    });
    fails += ExpectCaught("GravityWell: a planet system of the first shelf reaching into the second", Giants(), r =>
    {
        LayoutWrap wrap = Shelves(r);
        foreach (LayoutNode node in LayoutTree.PreOrder(Lanes(r, wrap, 0)[0].To))
            node.SnappedY += wrap.RowDy[1];
    });
    fails += ExpectCaught("GravityWell: a moon of a nested fan placed left of its planet", Giants(), r =>
    {
        LayoutNode moon = Lanes(r, Nested(r)!, 1)[0].To;
        moon.X = moon.Parent!.X - r.Config.GridPx;
    });

    LayoutResult Cruise() => LayoutEngine.Run(SyntheticTree.BuildCruiseRoot(), cfg);
    LayoutResult cruise = Cruise();
    LayoutWrap? bus = cruise.Wraps.FirstOrDefault(w => w.ParentId == "Star.Hub");
    int lowerBus = 0;
    foreach (LayoutEdge edge in FindNode(cruise.Tree, "Star.Hub").Out)
    {
        if (edge.IsHubLink && edge.To.WrapRow > 0)
            lowerBus++;
    }
    Console.WriteLine($"  {cruise.Tree.Name}: '{bus?.ParentId}' rows {(bus == null ? "-" : string.Join("/", bus.RowSizes))}, {lowerBus} bus nodes below the first shelf");
    fails += Check("the cruise root wraps its bus into shelves", bus != null && !bus.Balanced && bus.RowSizes.Count >= 2 && lowerBus > 0);
    fails += Check("the bus shelves pass the acceptance check", OverlapCheck.Run(cruise).Ok);
    if (bus == null || lowerBus == 0)
        return fails;
    fails += ExpectCaught("GravityWell: a bus node of the second shelf on the grid cell of one in the first", Cruise(), r =>
    {
        LayoutWrap wrap = r.Wraps.First(w => w.ParentId == "Star.Hub");
        LayoutNode upper = Lanes(r, wrap, 0)[1].To;
        LayoutNode lower = Lanes(r, wrap, 1)[0].To;
        lower.Col = upper.Col;
        lower.Row = upper.Row;
    });
    return fails;
}

// The wrap rules must catch a broken wrapped layout too. Each fixture breaks one value of a
// clean wrapped map the way a faulty pass would.
static int RunWrappedNegativeFixtures()
{
    int fails = 0;
    Console.WriteLine();
    Console.WriteLine("[negative-wrap] the acceptance check catches a broken wrapped layout");

    LayoutConfig cfg = LayoutConfig.Default.WithWrap(WrapMode.Auto);
    LayoutResult Fresh() => LayoutEngine.Run(SyntheticTree.BuildClampedGiants(), cfg);
    static LayoutWrap WidestWrap(LayoutResult r)
    {
        LayoutWrap widest = r.Wraps[0];
        foreach (LayoutWrap wrap in r.Wraps)
        {
            if (wrap.RowSizes.Count > widest.RowSizes.Count)
                widest = wrap;
        }
        return widest;
    }
    static List<LayoutEdge> Lanes(LayoutResult r, LayoutWrap wrap, int row)
    {
        var lanes = new List<LayoutEdge>();
        foreach (LayoutEdge edge in FindNode(r.Tree, wrap.ParentId).Out)
        {
            if (edge.Class != EdgeClass.Ladder && edge.To.WrapRow == row)
                lanes.Add(edge);
        }
        return lanes;
    }

    LayoutResult probe = Fresh();
    fails += Check("the clamped giants wrap a fan into two rows or more", probe.Wraps.Count > 0);
    if (probe.Wraps.Count == 0)
        return fails;
    Console.WriteLine($"  the widest wrap is '{WidestWrap(probe).ParentId}' with rows {string.Join("/", WidestWrap(probe).RowSizes)}");

    fails += ExpectCaught("a moon of the lowest row moved one grid cell off its dV band", Fresh(), r =>
    {
        LayoutWrap wrap = WidestWrap(r);
        Lanes(r, wrap, wrap.RowSizes.Count - 1)[0].To.Y += r.Config.GridPx;
    });
    fails += ExpectCaught("a lane in the middle of row 0 put into row 1", Fresh(), r =>
    {
        List<LayoutEdge> row0 = Lanes(r, WidestWrap(r), 0);
        row0[row0.Count / 2].To.WrapRow = 1;
    });
    fails += ExpectCaught("the trunk moved into row 1", Fresh(), r =>
    {
        LayoutWrap wrap = WidestWrap(r);
        wrap.TrunkX = Lanes(r, wrap, 1)[0].To.SnappedX;
    });
    fails += ExpectCaught("a lane of row 1 off the offset of its shelf", Fresh(), r =>
    {
        LayoutNode lane = Lanes(r, WidestWrap(r), 1)[0].To;
        lane.WrapDy += r.Config.GridPx;
        lane.Y += r.Config.GridPx;
    });
    fails += ExpectCaught("row 1 lifted into the height of row 0", Fresh(), r =>
    {
        LayoutWrap wrap = WidestWrap(r);
        foreach (LayoutEdge edge in Lanes(r, wrap, 1))
        {
            foreach (LayoutNode node in LayoutTree.PreOrder(edge.To))
                node.SnappedY -= wrap.RowDy[1];
        }
    });
    fails += ExpectCaught("the dV part of a lane that does not start on the trunk", Fresh(), r =>
    {
        LayoutEdge edge = Lanes(r, WidestWrap(r), 1)[0];
        var pts = new List<LayoutPoint>(edge.Polyline);
        pts[edge.DvStart] = new LayoutPoint(pts[edge.DvStart].X + r.Config.GridPx, pts[edge.DvStart].Y);
        edge.Polyline = pts;
    });
    fails += ExpectCaught("a dot on the trunk", Fresh(), r =>
    {
        LayoutWrap wrap = WidestWrap(r);
        LayoutNode hub = FindNode(r.Tree, wrap.ParentId);
        LayoutNode rung = hub.Out.First(e => e.Class == EdgeClass.Ladder).To;
        rung.SnappedX = wrap.TrunkX;
        rung.SnappedY = hub.SnappedY + wrap.RowDy[1] / 2.0;
    });

    // A "+N" group belongs in the last row, and a balanced fan has no row of a single lane and
    // no two rows that differ by more than one lane.
    static LayoutWrap? GroupWrap(LayoutResult r) => r.Wraps.FirstOrDefault(w =>
        FindNode(r.Tree, w.ParentId).Out.Any(e => e.To.Kind == LayoutKind.MinorGroup && e.To.WrapRow == w.RowSizes.Count - 1));
    fails += Check("a wrapped fan holds a \"+N\" group in its last row", GroupWrap(probe) != null);
    if (GroupWrap(probe) is LayoutWrap groupWrap)
    {
        fails += ExpectCaught("a \"+N\" group in a row before the last", Fresh(), r =>
        {
            LayoutWrap wrap = GroupWrap(r)!;
            FindNode(r.Tree, wrap.ParentId).Out.First(e => e.To.Kind == LayoutKind.MinorGroup).To.WrapRow = wrap.RowSizes.Count - 2;
        }, $"is in row {groupWrap.RowSizes.Count - 2}, not in the last row");
    }
    int lastSize = WidestWrap(probe).RowSizes[^1];
    fails += Check("the last row of the widest wrap holds four lanes or more", lastSize >= 4);
    if (lastSize >= 4)
    {
        fails += ExpectCaught("a balanced fan with a row of a single lane", Fresh(), r =>
        {
            LayoutWrap wrap = WidestWrap(r);
            List<LayoutEdge> last = Lanes(r, wrap, wrap.RowSizes.Count - 1);
            for (int i = 0; i + 1 < last.Count; i++)
                last[i].To.WrapRow = wrap.RowSizes.Count - 2;
        }, "holds a single lane");
        fails += ExpectCaught("a balanced fan with unbalanced rows", Fresh(), r =>
        {
            LayoutWrap wrap = WidestWrap(r);
            List<LayoutEdge> last = Lanes(r, wrap, wrap.RowSizes.Count - 1);
            for (int i = 0; i < 2; i++)
                last[i].To.WrapRow = wrap.RowSizes.Count - 2;
        }, "are not balanced");
    }

    // A row offset only grows on the way down, the prefix runs only horizontally and vertically,
    // and the trunk leaves from its hub.
    fails += ExpectCaught("a lane whose row offset is above its hub's", Fresh(), r =>
    {
        LayoutWrap wrap = WidestWrap(r);
        LayoutNode hub = FindNode(r.Tree, wrap.ParentId);
        LayoutNode lane = Lanes(r, wrap, 1)[0].To;
        double up = lane.WrapDy - (hub.WrapDy - r.Config.GridPx);
        lane.WrapDy -= up;
        lane.Y -= up;
    }, "is above its parent's");
    fails += ExpectCaught("a prefix segment that is neither horizontal nor vertical", Fresh(), r =>
    {
        LayoutEdge edge = Lanes(r, WidestWrap(r), 1)[0];
        var pts = new List<LayoutPoint>(edge.Polyline);
        double g = r.Config.GridPx;
        pts[1] = new LayoutPoint(pts[1].X + g, pts[1].Y + g);
        edge.Polyline = pts;
    }, "prefix segment ");
    fails += ExpectCaught("a trunk that does not start at its hub", Fresh(), r =>
    {
        LayoutWrap wrap = WidestWrap(r);
        var trunk = new List<LayoutPoint>(wrap.Trunk);
        trunk[0] = new LayoutPoint(trunk[0].X + r.Config.GridPx, trunk[0].Y);
        wrap.Trunk = trunk;
    }, $"the trunk of '{WidestWrap(probe).ParentId}' does not start at its hub");
    return fails;
}

// The drawn row breaks of every wrapped map in both tree modes, alone on the canvas. Each gap
// between two rows has one break, its "//" mark sits on the vertical run of the trunk inside the
// empty height between the rows, and its fold rule crosses no dot. At fit zoom on a 1600x1000
// and a 1300x860 canvas, the trunk and the prefixes keep their screen clearance from the dots.
// The SVG dump draws every trunk and leaves out every prefix, as the canvas does.
static int RunRowBreakChecks()
{
    int fails = 0;
    int fans = 0;
    int breaks = 0;
    var cases = new List<(string Name, Func<LayoutTree> Build, LayoutConfig Cfg)>();
    foreach ((string name, Func<LayoutTree> build) in WrapCases())
        cases.Add((name + " CD", build, LayoutConfig.Default.WithWrap(WrapMode.Auto)));
    foreach ((string name, Func<LayoutTree> build) in WellWrapCases())
        cases.Add((name + " GW", build, new LayoutConfig { Mode = LayoutMode.GravityWell }.WithWrap(WrapMode.Auto)));

    foreach ((string name, Func<LayoutTree> build, LayoutConfig cfg) in cases)
    {
        LayoutResult result = LayoutEngine.Run(build(), cfg);
        if (result.Wraps.Count == 0)
            continue;
        var hubs = new List<(LayoutNode Hub, LayoutWrap Wrap)>();
        WrapGeometry.Collect(result, hubs);
        fails += Check($"{name}: every wrap has its hub", hubs.Count == result.Wraps.Count);
        foreach ((LayoutNode hub, LayoutWrap wrap) in hubs)
        {
            fans++;
            var found = new List<RowBreak>();
            WrapGeometry.AddBreaks(hub, wrap, wrap.Mode, found);
            breaks += found.Count;
            fails += Check($"{name}: '{hub.Id}' has one break per gap between rows ({found.Count})", found.Count == wrap.RowSizes.Count - 1);
            RowBox[] boxes = WrapGeometry.RowBoxes(hub, wrap, wrap.Mode);
            for (int r = 1; r <= found.Count && r < boxes.Length; r++)
            {
                RowBreak brk = found[r - 1];
                bool onTrunk = false;
                for (int i = 0; i + 1 < wrap.Trunk.Count; i++)
                {
                    bool vertical = Math.Abs(wrap.Trunk[i].X - wrap.Trunk[i + 1].X) < 1e-9;
                    onTrunk |= vertical && SceneComposer.SegmentPointDistance(wrap.Trunk[i], wrap.Trunk[i + 1], brk.Mark) < 1e-6;
                }
                fails += Check($"{name}: the break above row {r} of '{hub.Id}' sits on the vertical trunk", onTrunk);
                double below = Math.Min(boxes[r].MinY, hub.SnappedY + wrap.RowDy[r]);
                fails += Check(FormattableString.Invariant($"{name}: the break above row {r} of '{hub.Id}' lies in the gap ({boxes[r - 1].MaxY:F0} < {brk.Mark.Y:F0} < {below:F0})"),
                    boxes[r - 1].MaxY < brk.Mark.Y && brk.Mark.Y < below);
                var ruleA = new LayoutPoint(brk.RuleLeft, brk.Mark.Y);
                var ruleB = new LayoutPoint(brk.RuleRight, brk.Mark.Y);
                var crossed = new List<string>();
                foreach (LayoutNode node in result.Tree.Nodes)
                {
                    if (SceneComposer.SegmentPointDistance(ruleA, ruleB, new LayoutPoint(node.SnappedX, node.SnappedY)) < node.DotRadius)
                        crossed.Add(node.Id);
                }
                fails += Check($"{name}: the fold rule above row {r} of '{hub.Id}' crosses no dot ({string.Join(", ", crossed)})", crossed.Count == 0);
            }
        }

        LayoutScene alone = SceneComposer.Compose(result, null, new List<LayoutPart>(), cfg);
        foreach ((double w, double h) in new[] { (1600.0, 1000.0), (1300.0, 860.0) })
            fails += Expect(FormattableString.Invariant($"{name} at {w}x{h}: the row breaks keep clear"), SceneChecks.WrapLines(alone, w, h));

        string svg = LayoutDumpFormat.ToSvg(result);
        int trunks = CountOf(svg, "stroke=\"#7a8a98\" stroke-width=\"1.6\" stroke-dasharray=\"7 5\"");
        fails += Check($"{name}: the SVG dump draws every trunk once ({trunks})", trunks == result.Wraps.Count);
        bool dvPartsOnly = true;
        foreach (LayoutNode node in result.Tree.Nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.DvStart <= 0)
                    continue;
                dvPartsOnly &= svg.Contains(SvgPoints(edge.Polyline, edge.DvStart, result), StringComparison.Ordinal)
                    && !svg.Contains(SvgPoints(edge.Polyline, 0, result), StringComparison.Ordinal);
            }
        }
        fails += Check($"{name}: the SVG dump draws each edge into a lower row from its dV part on", dvPartsOnly);
    }
    Console.WriteLine(FormattableString.Invariant($"  row breaks: {breaks} breaks on {fans} wrapped fans drawn and checked"));
    return fails;
}

// Prints how long a map rebuild takes, which is the ego layout, the three destination systems
// laid out as parts and the scene composed from them, once with all of them collapsed and once
// with the first one opened, and how much the ego layout and the collapsed compose allocate. It
// asserts nothing, because the time depends on the machine, and it runs only when the
// DVMAP_TIMING environment variable is set. Every case runs a while untimed
// first, so the JIT has optimized the code before any run is timed, and each time is the median
// of the timed runs.
static void RunRebuildTiming()
{
    const int Warmup = 25;
    const int Runs = 15;
    Console.WriteLine();
    Console.WriteLine(FormattableString.Invariant($"[rebuild-timing] median of {Runs} runs in ms, ego layout, part layouts, compose"));

    var cases = new List<(string Name, LayoutConfig Cfg, Func<LayoutTree> Build)>();
    foreach ((string modeName, LayoutMode mode) in new[] { ("GW", LayoutMode.GravityWell), ("CD", LayoutMode.CumulativeDown) })
    {
        LayoutConfig cfg = LayoutConfig.ForMap(mode, hasSeveralSystems: true, WrapMode.Auto);
        foreach ((string egoName, Func<LayoutTree> buildEgo) in new (string, Func<LayoutTree>)[]
        {
            ("realsol", SyntheticTree.BuildRealLikeSolRoot),
            ("planet-root", SyntheticTree.BuildStockLikePlanetRoot),
            ("dense", SyntheticTree.BuildDenseMoons),
            ("clamped", SyntheticTree.BuildClampedGiants)
        })
        {
            cases.Add((egoName + " " + modeName, cfg, buildEgo));
        }
    }

    foreach ((string _, LayoutConfig cfg, Func<LayoutTree> buildEgo) in cases)
    {
        for (int run = 0; run < Warmup; run++)
            TimeRebuild(cfg, buildEgo);
    }
    foreach ((string name, LayoutConfig cfg, Func<LayoutTree> buildEgo) in cases)
    {
        var runs = new List<RebuildTime>(Runs);
        for (int run = 0; run < Runs; run++)
            runs.Add(TimeRebuild(cfg, buildEgo));
        RebuildTime last = runs[^1];
        Console.WriteLine(FormattableString.Invariant(
            $"  {name}: {last.Nodes} nodes, ego {Median(runs, r => r.Ego):F2} (row candidates {last.Candidates} scored in {Median(runs, r => r.CandidatesMs):F2}), collapsed parts {Median(runs, r => r.CollapsedParts):F2} + compose {Median(runs, r => r.CollapsedCompose):F2}, opened parts {Median(runs, r => r.OpenedParts):F2} + compose {Median(runs, r => r.OpenedCompose):F2}, allocated ego {last.EgoBytes / 1024} KB, compose {last.ComposeBytes / 1024} KB"));
    }

    static double Median(List<RebuildTime> runs, Func<RebuildTime, double> value)
    {
        var values = new List<double>(runs.Count);
        foreach (RebuildTime r in runs)
            values.Add(value(r));
        values.Sort();
        return values[values.Count / 2];
    }
}

// One rebuild of the timing run, in milliseconds and bytes, with the row candidates scored on a
// tree of its own.
static RebuildTime TimeRebuild(LayoutConfig cfg, Func<LayoutTree> buildEgo)
{
    LayoutTree tree = buildEgo();
    long bytes = GC.GetAllocatedBytesForCurrentThread();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    LayoutResult ego = LayoutEngine.Run(tree, cfg);
    double egoTime = watch.Elapsed.TotalMilliseconds;
    long egoBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;

    LayoutTree probe = buildEgo();
    LayoutEngine.Run(probe, cfg.WithWrap(WrapMode.Off));
    watch.Restart();
    int candidates = RowWrap.Candidates(probe, cfg).Count;
    double candidatesTime = watch.Elapsed.TotalMilliseconds;

    LayoutNode hub = FindNode(ego.Tree, "Sol.Hub");
    var partTimes = new double[2];
    var composeTimes = new double[2];
    long composeBytes = 0;
    for (int opened = 0; opened < 2; opened++)
    {
        watch.Restart();
        var parts = new List<LayoutPart>
        {
            new()
            {
                RootId = "AlphaCentauri.Hub",
                Expanded = opened == 1,
                Result = SceneComposer.LayOutPart(opened == 1
                    ? () => SyntheticTree.BuildBarycenterSystem("AlphaCentauri", "4.344 ly", "3 stars, 2 planets")
                    : () => SyntheticTree.BuildSystemStub("AlphaCentauri", true, "4.344 ly", "3 stars, 2 planets"), ego, null, opened == 1)
            },
            new()
            {
                RootId = "BarnardsStar.Hub",
                Result = SceneComposer.LayOutPart(() => SyntheticTree.BuildSystemStub("BarnardsStar", false, "5.963 ly", "1 star, 4 planets"), ego, null, false)
            },
            new()
            {
                RootId = "TauCeti.Hub",
                Result = SceneComposer.LayOutPart(() => SyntheticTree.BuildSystemStub("TauCeti", false, "11.91 ly", "1 star, 4 planets"), ego, null, false)
            }
        };
        partTimes[opened] = watch.Elapsed.TotalMilliseconds;
        bytes = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        SceneComposer.Compose(ego, hub, parts, cfg);
        composeTimes[opened] = watch.Elapsed.TotalMilliseconds;
        if (opened == 0)
            composeBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
    }
    return new RebuildTime(tree.Nodes.Count, egoTime, egoBytes, candidates, candidatesTime, partTimes[0], composeTimes[0], partTimes[1], composeTimes[1], composeBytes);
}

// The points attribute the tree SVG dump writes for a polyline from point start on.
static string SvgPoints(IReadOnlyList<LayoutPoint> line, int start, LayoutResult result)
{
    var sb = new System.Text.StringBuilder("points=\"");
    for (int i = start; i < line.Count; i++)
    {
        if (i > start)
            sb.Append(' ');
        sb.Append(FormattableString.Invariant($"{line[i].X + 40.0 - result.MinX:0.0},{line[i].Y + 40.0 - result.MinY:0.0}"));
    }
    return sb.Append('"').ToString();
}

static int CountOf(string text, string part)
{
    int count = 0;
    for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        count++;
    return count;
}

static int Check(string name, bool ok)
{
    if (ok)
        return 0;
    Console.WriteLine($"  FAIL {name}");
    return 1;
}

// The times of one rebuild in milliseconds, with the node count, the row candidates and the bytes
// the ego layout and the collapsed compose allocate.
readonly record struct RebuildTime(int Nodes, double Ego, long EgoBytes, int Candidates, double CandidatesMs, double CollapsedParts, double CollapsedCompose, double OpenedParts, double OpenedCompose, long ComposeBytes);
