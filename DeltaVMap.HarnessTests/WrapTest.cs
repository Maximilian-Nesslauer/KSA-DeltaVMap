using System.Globalization;
using System.Text;
using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// Lays out the loaded system in the cumulative-down mode, rooted at the home body, and logs every
// wide fan, which is a node with more than six lanes (children that are not ladder rungs). For each
// lane it logs the band step below the hub, the step the dV alone would give before the clamp at
// MaxBandStep, and the edge dV, so a run shows how deep the moon lanes of a dense planet really
// drop and how many of them stop at the clamp. It reports and does not judge, so it always passes.
public sealed class WrapTest : IHarnessTest
{
    private const string Tag = "[dvmap-wrap]";
    private const int WideFanLanes = 6;

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

            VisualTree visual = VisualTree.Build(graph, new DvCache(), home, egoState: null, BuildOptions.Default);
            LayoutConfig cfg = LayoutConfig.ForMap(LayoutMode.CumulativeDown, graph.HasSeveralSystems, WrapMode.Off);
            LayoutResult result = LayoutEngine.Run(VisualTreeAdapter.ToLayoutTree(visual, graph), cfg);
            HarnessLog.Line($"{Tag} system '{system.Id}', root {home.Id}: {result.Tree.Nodes.Count} nodes, quantum {cfg.BandQuantumDv:F0} m/s, max band step {cfg.MaxBandStep}");

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
                if (lanes.Count <= WideFanLanes)
                    continue;

                wideFans++;
                var histogram = new SortedDictionary<int, int>();
                int hubAtClamp = 0;
                int hubClipped = 0;
                HarnessLog.Line($"{Tag} hub {hub.Id} (band {hub.Band}): {lanes.Count} lanes");
                foreach (LayoutEdge lane in lanes)
                {
                    int step = lane.To.Band - hub.Band;
                    int rawStep = RawStep(lane, cfg);
                    histogram[step] = histogram.TryGetValue(step, out int n) ? n + 1 : 1;
                    if (step == cfg.MaxBandStep)
                        hubAtClamp++;
                    if (rawStep > cfg.MaxBandStep)
                        hubClipped++;
                    HarnessLog.Line($"{Tag}   {lane.To.Id}: {lane.Class}, step {step}, raw step {rawStep}, dv {lane.Dv:F0} m/s, route dv {lane.RouteDv:F0} m/s");
                }
                HarnessLog.Line($"{Tag}   {hub.Id} summary: steps {Histogram(histogram)}, {hubAtClamp} at the max step, {hubClipped} cut by the clamp");
                totalLanes += lanes.Count;
                atClamp += hubAtClamp;
                clipped += hubClipped;
            }
            HarnessLog.Line($"{Tag} total on '{system.Id}': {wideFans} wide fans, {totalLanes} lanes, {atClamp} at the max step {cfg.MaxBandStep}, {clipped} cut by the clamp");
            HarnessLog.Line($"{Tag} PASS");
            return 0;
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"{Tag} FAIL: {ex}");
            return 1;
        }
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
