using System;
using System.IO;
using System.Text;
using Brutal.Logging;
using DeltaVMap.Dv;
using DeltaVMap.Model;
using KSA;

namespace DeltaVMap.Layout;

// Debug-only dump, run once per session from the draw hook alongside the delta-v and
// visual-tree dumps. It exercises the layout engine end to end with no rendering: it
// lays out a synthetic 100+ node tree and the real loaded system at a few roots,
// asserts no overlaps, logs the verdict, and writes an SVG plus a text tree to disk
// so the layout can be eyeballed in a browser before any in-game canvas exists.
internal static class LayoutDump
{
    private const string Tag = "[DvMap]";

    internal static void Run()
    {
        string outDir = OutputDirectory();
        DefaultCategory.Log.Info($"{Tag} === Layout dump; writing to '{outDir}' ===");

        try
        {
            Directory.CreateDirectory(outDir);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"{Tag} Could not create dump directory '{outDir}': {ex.Message}. SVG/text files will be skipped.");
            outDir = string.Empty;
        }

        // The game-independent acceptance case: a 100+ node tree with a three-hub bus.
        DumpLayout(SyntheticTree.BuildLargeMoonRoot(), LayoutConfig.Default, outDir, "synthetic 100+ node tree");

        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
        {
            DefaultCategory.Log.Warning($"{Tag} Layout dump: no current system loaded; stock-system cases skipped.");
            return;
        }

        SystemGraph? graph = SystemGraph.Build(system);
        if (graph == null)
        {
            DefaultCategory.Log.Warning($"{Tag} Layout dump: could not build system graph; stock-system cases skipped.");
            return;
        }

        var cache = new DvCache();
        DumpStockRoot(graph, cache, "Earth", "planet root", outDir);
        DumpStockRoot(graph, cache, "Luna", "moon root", outDir);
        DumpGasGiant(graph, cache, outDir);
        DumpEgoRoot(graph, cache, outDir);

        DefaultCategory.Log.Info($"{Tag} === End layout dump ===");
    }

    private static void DumpStockRoot(SystemGraph graph, DvCache cache, string id, string role, string outDir)
    {
        PhysicalNode? node = graph.Find(id);
        if (node == null)
        {
            DefaultCategory.Log.Info($"{Tag} Layout root '{id}' ({role}): not present in this system, skipping.");
            return;
        }
        DumpStockTree(graph, cache, node, egoState: null, role, outDir);
    }

    private static void DumpGasGiant(SystemGraph graph, DvCache cache, string outDir)
    {
        PhysicalNode? chosen = graph.Find("Jupiter") ?? graph.Find("Saturn");
        if (chosen == null || chosen.Ladder.HasSurface)
        {
            chosen = null;
            foreach (PhysicalNode node in graph.AllNodes)
            {
                if (node.IsStar || node.Ladder.HasSurface)
                    continue;
                if (chosen == null || node.Ladder.MeanRadius > chosen.Ladder.MeanRadius
                    || (node.Ladder.MeanRadius == chosen.Ladder.MeanRadius && string.CompareOrdinal(node.Id, chosen.Id) < 0))
                    chosen = node;
            }
        }

        if (chosen == null)
        {
            DefaultCategory.Log.Info($"{Tag} Layout root (gas giant): none found, skipping.");
            return;
        }
        DumpStockTree(graph, cache, chosen, egoState: null, "gas giant root", outDir);
    }

    private static void DumpEgoRoot(SystemGraph graph, DvCache cache, string outDir)
    {
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle?.Parent == null)
        {
            DefaultCategory.Log.Info($"{Tag} Layout ego root: no controlled vehicle, skipping.");
            return;
        }

        PhysicalNode? node = graph.Find(vehicle.Parent.Id);
        if (node == null)
        {
            DefaultCategory.Log.Info($"{Tag} Layout ego root: vehicle parent '{vehicle.Parent.Id}' not in graph, skipping.");
            return;
        }

        ClassifiedState state = StateClassifier.Classify(vehicle, node.Ladder);
        DumpStockTree(graph, cache, node, state, "ego root", outDir);
    }

    private static void DumpStockTree(SystemGraph graph, DvCache cache, PhysicalNode root, ClassifiedState? egoState, string role, string outDir)
    {
        try
        {
            VisualTree visual = VisualTree.Build(graph, cache, root, egoState, fullLadderEverywhere: false);
            LayoutTree tree = VisualTreeAdapter.ToLayoutTree(visual);
            DumpLayout(tree, LayoutConfig.Default, outDir, role);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"{Tag} Layout dump failed for root '{root.Id}' ({role}): {ex}");
        }
    }

    private static void DumpLayout(LayoutTree tree, LayoutConfig cfg, string outDir, string role)
    {
        LayoutResult result = LayoutEngine.Run(tree, cfg);
        OverlapReport report = OverlapCheck.Run(result);

        DefaultCategory.Log.Info($"{Tag} [{tree.Name}] ({role}) {report.Summary()}");
        LogIssues("dot", report.NodeOverlaps);
        LogIssues("subtree", report.SubtreeOverlaps);
        LogIssues("label", report.LabelOverlaps);
        LogIssues("band", report.BandViolations);
        LogIssues("bus", report.BusViolations);

        if (string.IsNullOrEmpty(outDir))
            return;

        string stem = Sanitize(tree.Name);
        WriteFile(Path.Combine(outDir, stem + ".svg"), LayoutDumpFormat.ToSvg(result));
        WriteFile(Path.Combine(outDir, stem + ".txt"), LayoutDumpFormat.ToText(result));
    }

    private static void LogIssues(string kind, System.Collections.Generic.List<string> issues)
    {
        foreach (string issue in issues)
            DefaultCategory.Log.Warning($"{Tag}   {kind} overlap: {issue}");
    }

    private static void WriteFile(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"{Tag} Could not write '{path}': {ex.Message}");
        }
    }

    private static string OutputDirectory()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "My Games", "Kitten Space Agency", "DeltaVMap");
    }

    // Make a filesystem-safe stem from a tree name (body Ids are usually plain words,
    // but a modded system could include spaces or punctuation).
    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }
}
