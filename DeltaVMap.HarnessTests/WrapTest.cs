using System.Globalization;
using System.Text;
using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// Lays out the loaded system rooted at the home body in the cumulative-down and the gravity-well
// mode, each once with the row wrap Off and once with it Auto, through the config the map window
// builds. For every wide or wrapped fan it logs the lane count, the rows the wrap chose, and for
// each lane its row, the band step below the hub, the step the dV alone would give before the
// clamp at MaxBandStep, and the edge dV. Each run must pass OverlapCheck, the wrap rules included.
// On the stock Sol and on SolDense the stock moon fans stay below the cumulative-down threshold,
// so the cumulative-down map must wrap nothing and keep every node where the unwrapped map has
// it, while the gravity-well map must wrap and raise its fit zoom on a 1600x1000 canvas by at
// least a tenth. Other systems log these figures without the two gates, because their fans can
// differ. Run it with -Vehicle '' -System SolDense -Tests dvmap-wrap, and again with -System Sol.
public sealed class WrapTest : IHarnessTest
{
    private const string Tag = "[dvmap-wrap]";
    private const int WideFanLanes = 6;
    private const double MinWellGain = 1.10;

    public string Name => "dvmap-wrap";

    public bool OptIn => false;

    public int Run(HeadlessSession session)
    {
        try
        {
            CelestialSystem system = session.System;
            SystemGraph? graph = SystemGraph.Build(system);
            PhysicalNode? home = graph != null && system.HomeBody != null ? graph.Find(system.HomeBody.Id) : null;
            if (graph == null || home == null)
            {
                HarnessLog.Line($"{Tag} SKIP: no graph or home body for system '{system.Id}'.");
                return 0;
            }

            bool gated = system.Id == "Sol" || system.Id == "SolDense";
            HarnessLog.Line($"{Tag} system '{system.Id}', root {home.Id}, several systems {graph.HasSeveralSystems}, gates {(gated ? "on" : "off")}");
            bool ok = true;
            foreach ((string name, LayoutMode mode) in new[] { ("cumulative-down", LayoutMode.CumulativeDown), ("gravity-well", LayoutMode.GravityWell) })
            {
                LayoutResult off = Lay(graph, home, LayoutConfig.ForMap(mode, graph.HasSeveralSystems, WrapMode.Off));
                LayoutResult auto = Lay(graph, home, LayoutConfig.ForMap(mode, graph.HasSeveralSystems, WrapMode.Auto));
                ok &= Report(name, "off", off);
                ok &= Report(name, "auto", auto);
                LogFans(name, auto);

                double fitOff = AloneFit(off);
                double fitAuto = AloneFit(auto);
                HarnessLog.Line(string.Create(CultureInfo.InvariantCulture,
                    $"{Tag} {name}: {auto.Wraps.Count} wrapped fans, fit off {fitOff:F3}, fit auto {fitAuto:F3} ({(fitAuto / fitOff - 1.0) * 100.0:+0.0;-0.0}%)"));
                if (!gated)
                    continue;

                if (mode == LayoutMode.CumulativeDown)
                {
                    ok &= Check($"{name}: the map wraps no fan", auto.Wraps.Count == 0);
                    ok &= Check($"{name}: every node keeps the place it has without the wrap", SamePlaces(off, auto));
                }
                else
                {
                    ok &= Check($"{name}: the map wraps at least one fan", auto.Wraps.Count > 0);
                    ok &= Check(string.Create(CultureInfo.InvariantCulture, $"{name}: the fit with the wrap is at least {MinWellGain:F2} times the fit without it"), fitAuto >= MinWellGain * fitOff);
                }
            }
            HarnessLog.Line($"{Tag} {(ok ? "PASS" : "FAIL")}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"{Tag} FAIL: {ex}");
            return 1;
        }
    }

    // Each layout gets a tree of its own, because the layout passes write their results into the
    // nodes of the tree they run on.
    private static LayoutResult Lay(SystemGraph graph, PhysicalNode home, LayoutConfig cfg)
    {
        VisualTree visual = VisualTree.Build(graph, new DvCache(), home, egoState: null, BuildOptions.Default);
        return LayoutEngine.Run(VisualTreeAdapter.ToLayoutTree(visual, graph), cfg);
    }

    // Runs the layout checks before the scene composer can move a node, and logs every issue.
    private static bool Report(string name, string wrap, LayoutResult result)
    {
        OverlapReport report = OverlapCheck.Run(result);
        HarnessLog.Line($"{Tag} {name} {wrap}: {result.Width:F0}x{result.Height:F0}, {report.Summary()}");
        foreach (string issue in report.NodeOverlaps)
            HarnessLog.Line($"{Tag}   dot overlap: {issue}");
        foreach (string issue in report.SubtreeOverlaps)
            HarnessLog.Line($"{Tag}   subtree overlap: {issue}");
        foreach (string issue in report.LabelOverlaps)
            HarnessLog.Line($"{Tag}   label overlap: {issue}");
        foreach (string issue in report.BandViolations)
            HarnessLog.Line($"{Tag}   band: {issue}");
        foreach (string issue in report.BusViolations)
            HarnessLog.Line($"{Tag}   bus: {issue}");
        foreach (string issue in report.WrapViolations)
            HarnessLog.Line($"{Tag}   wrap: {issue}");
        return Check($"{name} {wrap}: the layout passes the overlap, band, bus and wrap checks", report.Ok);
    }

    // Logs each fan that has more than WideFanLanes lanes or that wraps. A lane is every child
    // that is not a ladder rung.
    private static void LogFans(string name, LayoutResult result)
    {
        LayoutConfig cfg = result.Config;
        var wraps = new Dictionary<string, LayoutWrap>();
        foreach (LayoutWrap wrap in result.Wraps)
            wraps[wrap.ParentId] = wrap;

        int wideFans = 0;
        int totalLanes = 0;
        int atClamp = 0;
        int clipped = 0;
        foreach (LayoutNode hub in result.Tree.Nodes)
        {
            var lanes = new List<LayoutEdge>();
            foreach (LayoutEdge edge in hub.Out)
            {
                if (edge.Class != EdgeClass.Ladder)
                    lanes.Add(edge);
            }
            bool wrapped = wraps.TryGetValue(hub.Id, out LayoutWrap? hubWrap);
            if (lanes.Count <= WideFanLanes && !wrapped)
                continue;

            wideFans++;
            var histogram = new SortedDictionary<int, int>();
            int hubAtClamp = 0;
            int hubClipped = 0;
            HarnessLog.Line($"{Tag} {name} hub {hub.Id} (band {hub.Band}): {lanes.Count} lanes, {Rows(hubWrap)}");
            foreach (LayoutEdge lane in lanes)
            {
                int step = lane.To.Band - hub.Band;
                int rawStep = RawStep(lane, cfg);
                histogram[step] = histogram.TryGetValue(step, out int n) ? n + 1 : 1;
                if (step == cfg.MaxBandStep)
                    hubAtClamp++;
                if (rawStep > cfg.MaxBandStep)
                    hubClipped++;
                string row = wrapped ? $"row {lane.To.WrapRow}, " : "";
                HarnessLog.Line($"{Tag}   {lane.To.Id}: {lane.Class}, {row}step {step}, raw step {rawStep}, dv {lane.Dv:F0} m/s, route dv {lane.RouteDv:F0} m/s");
            }
            HarnessLog.Line($"{Tag}   {hub.Id} summary: steps {Histogram(histogram)}, {hubAtClamp} at the max step, {hubClipped} cut by the clamp");
            totalLanes += lanes.Count;
            atClamp += hubAtClamp;
            clipped += hubClipped;
        }
        HarnessLog.Line($"{Tag} {name} total: {wideFans} wide or wrapped fans, {totalLanes} lanes, {atClamp} at the max step {cfg.MaxBandStep}, {clipped} cut by the clamp");
    }

    private static string Rows(LayoutWrap? wrap)
    {
        if (wrap == null)
            return "1 row";
        var sizes = new StringBuilder();
        var offsets = new StringBuilder();
        for (int i = 0; i < wrap.RowSizes.Count; i++)
        {
            if (i > 0)
            {
                sizes.Append('/');
                offsets.Append(", ");
            }
            sizes.Append(wrap.RowSizes[i].ToString(CultureInfo.InvariantCulture));
            offsets.Append(wrap.RowDy[i].ToString("F0", CultureInfo.InvariantCulture));
        }
        string balance = wrap.Balanced ? "balanced" : "split by width";
        return string.Create(CultureInfo.InvariantCulture, $"{wrap.RowSizes.Count} rows of {sizes} lanes ({balance}), offsets {offsets}, trunk x {wrap.TrunkX:F0}");
    }

    // The fit zoom of the map alone on a 1600x1000 canvas, down to the zoom floor of the map
    // window, so the gain of the rows on a dense map is not clipped by a higher floor.
    private static double AloneFit(LayoutResult result)
    {
        LayoutScene scene = SceneComposer.Compose(result, null, new List<LayoutPart>(), result.Config);
        return SceneFit.Fit(scene, 1600, 1000, SceneFit.MinZoom, 4.0, out _, out _);
    }

    // Whether two layouts of the same tree put every node at the same snapped place.
    private static bool SamePlaces(LayoutResult a, LayoutResult b)
    {
        IReadOnlyList<LayoutNode> nodesA = a.Tree.Nodes;
        IReadOnlyList<LayoutNode> nodesB = b.Tree.Nodes;
        if (nodesA.Count != nodesB.Count)
            return false;
        for (int i = 0; i < nodesA.Count; i++)
        {
            LayoutNode na = nodesA[i];
            LayoutNode nb = nodesB[i];
            if (na.Id != nb.Id || na.SnappedX != nb.SnappedX || na.SnappedY != nb.SnappedY)
                return false;
        }
        return true;
    }

    private static bool Check(string what, bool passed)
    {
        HarnessLog.Line($"{Tag} {(passed ? "ok" : "FAIL")}: {what}");
        return passed;
    }

    // The band step the dV gives before the clamp at MaxBandStep, with the same rounding as the
    // band pass. A hub link always adds zero bands.
    private static int RawStep(LayoutEdge edge, LayoutConfig cfg)
    {
        if (edge.IsHubLink)
            return 0;
        if (double.IsNaN(edge.Dv) || edge.Dv <= 0.0)
            return cfg.MinBandStep;
        int step = (int)Math.Round(edge.Dv / cfg.BandQuantumDv, MidpointRounding.AwayFromZero);
        return Math.Max(step, cfg.MinBandStep);
    }

    private static string Histogram(SortedDictionary<int, int> histogram)
    {
        var text = new StringBuilder();
        foreach (KeyValuePair<int, int> pair in histogram)
        {
            if (text.Length > 0)
                text.Append(", ");
            text.Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append(" bands x").Append(pair.Value.ToString(CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }
}
