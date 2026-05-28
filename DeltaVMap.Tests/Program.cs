using System;
using System.Collections.Generic;
using System.IO;
using DeltaVMap.Layout;

// Run the layout engine on the synthetic trees, assert no overlaps, and dump SVG and
// text so the layout can be eyeballed before any in-game rendering exists. Exit code
// is non-zero if any case fails its assertions or the node-count requirement, so this
// doubles as a CI-style gate.

string outDir = args.Length > 0
    ? args[0]
    : Path.Combine(Directory.GetCurrentDirectory(), "layout-dumps");
Directory.CreateDirectory(outDir);

Console.WriteLine($"Layout dumps -> {outDir}");
Console.WriteLine();

int failures = 0;

// The 100+ node stress tree: a moon root with a three-hub horizontal bus.
failures += RunCase(SyntheticTree.BuildLargeMoonRoot(), LayoutConfig.Default, "default", outDir, minNodes: 100);

// The same stress tree on a deliberately tight grid, to force grid-snap collisions
// and confirm the nudge pass still ends overlap-free.
var tightGrid = new LayoutConfig { GridPx = 32.0, BandHeightPx = 64.0, SiblingGapPx = 12.0, BusGapPx = 24.0 };
failures += RunCase(SyntheticTree.BuildLargeMoonRoot(), tightGrid, "tight", outDir, minNodes: 100);

// A stock-like planet root, for eyeballing realism.
failures += RunCase(SyntheticTree.BuildStockLikePlanetRoot(), LayoutConfig.Default, "default", outDir, minNodes: 0);

// The interplanetary-cruise root: a star-shaped hub bus rather than a chain.
failures += RunCase(SyntheticTree.BuildCruiseRoot(), LayoutConfig.Default, "default", outDir, minNodes: 0);

// A wide, shallow fan: dense single-band siblings stressing the grid-snap nudge.
failures += RunCase(SyntheticTree.BuildWideFan(), LayoutConfig.Default, "default", outDir, minNodes: 0);

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
