using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// Composes the real map scene of the loaded universe in all three layout modes, rooted at the
// home body, once with every other star system collapsed and once with the nearest one opened,
// and holds both to the rules of SceneChecks at fit zoom on a 1600x1000 and a 1300x860 canvas:
// the strip geometry, connectors clear of the home map and of each part, the empty band, the
// titles on the canvas, and a strip that keeps its places when the system opens. It writes
// the scene text and fit-zoom preview SVGs of each to %TEMP%\dvmap-scene. Labels are measured
// by the character-count estimate, since there is no ImGui font here. Opt-in, since it serves
// to look at the real layout: run it with -System SolSystemInterstellar -Tests dvmap-scene-dump.
// With a single star system it logs a SKIP.
public sealed class SceneDumpTest : IHarnessTest
{
    private const string Tag = "[dvmap-scene-dump]";

    public string Name => "dvmap-scene-dump";

    public bool OptIn => true;

    public int Run(HeadlessSession session)
    {
        try
        {
            CelestialSystem system = session.System;
            SystemGraph? graph = SystemGraph.Build(system);
            PhysicalNode? home = graph != null && system.HomeBody != null ? graph.Find(system.HomeBody.Id) : null;
            if (graph == null || home == null)
            {
                HarnessLog.Line($"{Tag} FAIL: no graph or home body for system '{system.Id}'.");
                return 1;
            }
            if (!graph.HasSeveralSystems)
            {
                HarnessLog.Line($"{Tag} SKIP: system '{system.Id}' has one star system.");
                return 0;
            }

            string outDir = Path.Combine(Path.GetTempPath(), "dvmap-scene");
            Directory.CreateDirectory(outDir);
            bool ok = true;
            foreach ((string name, LayoutMode mode) in new[] { ("default", LayoutMode.CumulativeDown), ("gravitywell", LayoutMode.GravityWell), ("spring", LayoutMode.Spring) })
            {
                VisualTree visual = VisualTree.Build(graph, new DvCache(), home, egoState: null, BuildOptions.Default);
                LayoutResult ego = LayoutEngine.Run(VisualTreeAdapter.ToLayoutTree(visual, graph), new LayoutConfig { Mode = mode });
                LayoutNode? hub = null;
                foreach (LayoutNode node in ego.Tree.Nodes)
                {
                    if (node.Id == visual.SystemHub.Id)
                        hub = node;
                }
                var parts = new List<LayoutPart>();
                for (int i = 0; i < visual.InterstellarEdges.Count; i++)
                {
                    Edge edge = visual.InterstellarEdges[i];
                    LayoutResult part = SceneComposer.LayOutPart(() => VisualTreeAdapter.ToPartTree(edge, graph, false), ego, null);
                    parts.Add(new LayoutPart { RootId = edge.To.Id, Result = part, Expanded = false });
                }
                LayoutScene scene = SceneComposer.Compose(ego, hub, parts, ego.Config);
                ok &= Report(name, scene, hub, parts, outDir);
                if (parts.Count == 0)
                    continue;

                SceneChecks.Places before = SceneChecks.Take(scene);
                Edge first = visual.InterstellarEdges[0];
                LayoutResult openedPart = SceneComposer.LayOutPart(() => VisualTreeAdapter.ToPartTree(first, graph, true), ego, null);
                var openedParts = new List<LayoutPart>(parts);
                openedParts[0] = new LayoutPart { RootId = first.To.Id, Result = openedPart, Expanded = true };
                LayoutScene opened = SceneComposer.Compose(ego, hub, openedParts, ego.Config);
                ok &= Report(name + "-opened", opened, hub, openedParts, outDir);
                List<string> moved = SceneChecks.Stability(before, opened, first.To.Id, out double along, out double across);
                HarnessLog.Line($"{Tag} scene-{name}-opened: the opened root moved by ({along:F0}, {across:F0}) along and across the strip");
                ok &= Fail($"scene-{name}-opened", moved);
            }
            HarnessLog.Line($"{Tag} wrote the scenes to '{outDir}'");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"{Tag} FAIL: {ex}");
            return 1;
        }
    }

    private static bool Report(string name, LayoutScene scene, LayoutNode? hub, List<LayoutPart> parts, string outDir)
    {
        string stem = "scene-" + name;
        OverlapReport report = OverlapCheck.RunScene(scene);
        bool titled = hub != null && hub.IsSystemRoot;
        foreach (LayoutPart part in parts)
            titled &= part.Root.IsSystemRoot;
        FitPreview fit = FitPreview.Build(scene, 1600, 1000);
        HarnessLog.Line($"{Tag} {stem}: ego {scene.Ego.Width:F0}x{scene.Ego.Height:F0}, strip {scene.Side}, {parts.Count} systems, fit zoom {fit.Zoom:F3}, {report.Summary()}");
        File.WriteAllText(Path.Combine(outDir, stem + ".txt"), LayoutDumpFormat.ToText(scene));
        File.WriteAllText(Path.Combine(outDir, stem + "-fit.svg"), LayoutDumpFormat.ToFitSvg(scene, 1600, 1000));
        File.WriteAllText(Path.Combine(outDir, stem + "-fit-small.svg"), LayoutDumpFormat.ToFitSvg(scene, 1300, 860));

        var problems = new List<string>();
        if (report.NodeOverlaps.Count > 0 || report.LabelOverlaps.Count > 0)
            problems.Add("overlaps at zoom 1");
        if (!titled)
            problems.Add("a missing system title flag");
        problems.AddRange(SceneChecks.Strip(scene));
        foreach ((double w, double h) in new[] { (1600.0, 1000.0), (1300.0, 860.0) })
        {
            problems.AddRange(SceneChecks.Connectors(scene, w, h));
            problems.AddRange(SceneChecks.Fit(scene, w, h, out double band));
            HarnessLog.Line($"{Tag} {stem} at {w}x{h}: empty band {band:F0} px");
        }
        return Fail(stem, problems);
    }

    private static bool Fail(string stem, List<string> problems)
    {
        foreach (string problem in problems)
            HarnessLog.Line($"{Tag} FAIL {stem}: {problem}");
        return problems.Count == 0;
    }
}
