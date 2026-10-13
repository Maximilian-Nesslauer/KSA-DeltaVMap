using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// Picks the wide fans of a tree layout that continue in rows below their hub, and how far each
// row sits below the one above it. It is pure and deterministic, so the choice depends only on the
// tree, its measured widths and the nominal canvas of the config, never on the live window, so
// resizing the window never moves a node.
//
// In Cumulative-down the fan of a body wraps when it has more than WrapMinLanesCd lanes. The fan
// of a star or barycenter hub never wraps, because a row of planet systems costs a drop many
// bands deep that states no delta-v. Ladder rungs are no lanes and stay in row 0. A fan of N
// lanes gets min(cap, ceil(N / WrapKmaxCd)) rows, split into balanced runs that keep the hub's
// order, so the innermost moons come first and a "+N" group lands in the last row. One cap holds
// for the whole map, and one more row is taken only while it raises the fit zoom on the nominal
// canvas by WrapMinGain, so a pick in one fan never moves the rows of another. The lanes a reveal
// added do not count, neither for the row sizes nor for the score, and each one joins the row of
// the lane before it, so a reveal never moves another lane to a different row.
//
// The rows sit on shelves that the whole map shares. Every node below a lane in row r is on
// shelf r, and shelf r adds the same WrapDy to Y everywhere, one gap below the deepest node any
// wrapped fan has above it. So within one shelf a lower node still costs more delta-v, across all
// fans, and the drop from the shelf into each lane keeps its true length. The gap and the gutter
// that holds the trunk are sized in screen pixels at the nominal fit zoom, the way SceneComposer
// sizes its channel, so they stay clear on screen however large the map is.
//
// In GravityWell Y is local to each well, so a row states no false delta-v, and the wrap works at
// two levels. A fan of more than WrapMinLanesGw moons wraps by count as above, inside the block of
// its planet. The star hub of the bus, the one with the most lanes, wraps its whole planet
// systems into shelves, each system with its moon rows kept in one shelf, split by width so the
// widest shelf is as narrow as it can be. The chooser scores every pair of a moon row cap and a
// shelf count, and takes a further row or shelf only for the same gain, so on a tie it keeps
// fewer rows first and then fewer shelves. The rows of one hub sit one gap below the lowest dot
// of the row above, so the offsets are local to the hub and no shelf is shared across the map.
internal static class RowWrap
{
    // One row plan the chooser scored. Width and Height are the estimated bounds of the map, Score
    // its fit zoom on the nominal canvas, Fans and Rows the wrapped hubs and their rows in total.
    // Shelves is the shelf count asked of the GravityWell star hub, 1 in Cumulative-down.
    internal readonly record struct Candidate(int RowCap, int Fans, int Rows, double Width, double Height, double Score, double GapPx, double GutterPx, int Shelves = 1);

    // The row cap and the shelf count a plan took. Forced on the same tree and config, they give
    // the same rows again without scoring the other candidates.
    internal readonly record struct Choice(int RowCap, int Shelves);

    private sealed class Fan
    {
        public required LayoutNode Hub { get; init; }
        public required int Unrevealed { get; init; }
    }

    // Plans the rows of the tree and returns the wrapped fans. It sets WrapRow, Shelf and WrapDy on
    // every node, and needs the measured widths and the bands. rowCap forces one cap instead of
    // the chosen one, and in GravityWell shelves forces the shelf count, for tests that compare
    // the candidates and for a layout that repeats a plan.
    public static IReadOnlyList<LayoutWrap> Plan(LayoutTree tree, LayoutConfig cfg, int? rowCap = null, int? shelves = null)
    {
        return Plan(tree, cfg, rowCap, shelves, out _);
    }

    // The same, and choice tells the cap and the shelf count the plan took.
    public static IReadOnlyList<LayoutWrap> Plan(LayoutTree tree, LayoutConfig cfg, int? rowCap, int? shelves, out Choice choice)
    {
        choice = new Choice(rowCap ?? 1, shelves ?? 1);
        if (cfg.Mode == LayoutMode.GravityWell)
            return PlanWell(tree, cfg, rowCap, shelves, ref choice);
        Reset(tree);
        List<Fan> fans = FindFans(tree, cfg);
        if (fans.Count == 0)
            return Array.Empty<LayoutWrap>();

        Candidate chosen;
        if (rowCap.HasValue)
        {
            chosen = Evaluate(tree, cfg, fans, rowCap.Value) ?? Evaluate(tree, cfg, fans, 1)!.Value;
        }
        else
        {
            List<Candidate> candidates = Candidates(tree, cfg, fans);
            chosen = candidates[Choose(candidates, cfg.WrapMinGain)];
            choice = new Choice(chosen.RowCap, 1);
        }

        if (chosen.Fans == 0)
        {
            Reset(tree);
            return Array.Empty<LayoutWrap>();
        }
        return Apply(tree, cfg, fans, chosen.RowCap, chosen.GapPx, chosen.GutterPx, skipRevealed: false);
    }

    // Every distinct row cap from one row up to the mode's maximum, scored without the revealed
    // lanes. A cap that wraps no fan more than the cap below it gives the same layout and is left
    // out. Empty when the config does not wrap or no fan is wide enough.
    public static List<Candidate> Candidates(LayoutTree tree, LayoutConfig cfg)
    {
        Reset(tree);
        if (cfg.Mode == LayoutMode.GravityWell)
        {
            WellPlan? plan = FindWell(tree, cfg);
            List<Candidate> wellCandidates = plan == null ? new List<Candidate>() : WellCandidates(tree, cfg, plan);
            Reset(tree);
            return wellCandidates;
        }
        List<Fan> fans = FindFans(tree, cfg);
        List<Candidate> candidates = fans.Count == 0 ? new List<Candidate>() : Candidates(tree, cfg, fans);
        Reset(tree);
        return candidates;
    }

    // The index of the candidate to take. Starting from one row, a candidate replaces the current
    // one only when its score is at least WrapMinGain above it, so a tie keeps fewer rows.
    public static int Choose(IReadOnlyList<Candidate> candidates, double minGain)
    {
        int chosen = 0;
        for (int i = 1; i < candidates.Count; i++)
        {
            if (candidates[i].Score >= candidates[chosen].Score * (1.0 + minGain))
                chosen = i;
        }
        return chosen;
    }

    private static List<Candidate> Candidates(LayoutTree tree, LayoutConfig cfg, List<Fan> fans)
    {
        var candidates = new List<Candidate>();
        int[] lastRows = RowCounts(fans, 1, cfg);
        candidates.Add(Evaluate(tree, cfg, fans, 1)!.Value);
        for (int cap = 2; cap <= cfg.WrapMaxRowsCd; cap++)
        {
            int[] rows = RowCounts(fans, cap, cfg);
            if (rows.AsSpan().SequenceEqual(lastRows))
                continue;
            lastRows = rows;
            Candidate? c = Evaluate(tree, cfg, fans, cap);
            if (c.HasValue)
                candidates.Add(c.Value);
        }
        return candidates;
    }

    // Scores one cap. The gap and the gutter depend on the zoom the map fits at, and that zoom on
    // the gap, so the cap is laid out once with the plain band gap and gutter, and once more with
    // the gap and gutter that the first zoom asks for.
    private static Candidate? Evaluate(LayoutTree tree, LayoutConfig cfg, List<Fan> fans, int cap)
    {
        if (cap <= 1)
        {
            Reset(tree);
            (double w1, double h1) = Measure(tree, cfg, Array.Empty<LayoutWrap>());
            return new Candidate(1, 0, 0, w1, h1, Score(w1, h1, cfg), 0.0, 0.0);
        }

        List<LayoutWrap> first = Apply(tree, cfg, fans, cap, cfg.BandHeightPx, cfg.WrapGutterPx, skipRevealed: true);
        if (first.Count == 0)
            return null;
        (double w0, double h0) = Measure(tree, cfg, first);
        double zNom = ScreenZoom(w0, h0, cfg);
        double g = cfg.GridPx;
        double gap = Math.Max(cfg.BandHeightPx, SnapUp(cfg.ShelfClearScreenPx / zNom, g));
        double gutter = Gutter(cfg, zNom * cfg.WrapLineZoomShare);

        List<LayoutWrap> wraps = Apply(tree, cfg, fans, cap, gap, gutter, skipRevealed: true);
        (double w, double h) = Measure(tree, cfg, wraps);
        int rows = 0;
        foreach (LayoutWrap wrap in wraps)
            rows += wrap.RowSizes.Count;
        Reset(tree);
        return new Candidate(cap, wraps.Count, rows, w, h, Score(w, h, cfg), gap, gutter);
    }

    // The gutter holds a moon dot and the line clearance on each side of the trunk at the given
    // zoom. In Cumulative-down the hub's rungs hang inside row 0 right beside the trunk, so the
    // gutter is sized for the lowest zoom the map is drawn at, WrapLineZoomShare of its nominal
    // one. In GravityWell the rungs stay in the hub's column, which HubGutter already keeps a
    // rung label away from the trunk, so the nominal zoom is enough there.
    private static double Gutter(LayoutConfig cfg, double zoom)
    {
        return Math.Max(cfg.WrapGutterPx, SnapUp(2.0 * (cfg.MoonDotRadius + OverlapCheck.WrapLineClearPx) / zoom, cfg.GridPx));
    }

    // The zoom the gap and the gutter of a candidate are sized for, which is its own nominal fit
    // zoom, or the zoom of the map it is drawn beside when the config names one.
    private static double ScreenZoom(double width, double height, LayoutConfig cfg)
    {
        return cfg.WrapScreenZoom > 0.0 ? cfg.WrapScreenZoom : SceneComposer.NominalZoom(width, height);
    }

    // The fit zoom of bounds on the nominal canvas at the config's target aspect. The canvas is
    // as wide as the one SceneComposer sizes its gaps for, and no taller than that width over the
    // aspect, which keeps room below the map for the strip of other star systems.
    private static double Score(double width, double height, LayoutConfig cfg)
    {
        double w = SceneComposer.NominalWidthPx;
        double h = Math.Min(SceneComposer.NominalHeightPx, w / cfg.WrapTargetAspect);
        return Math.Min(w / Math.Max(1.0, width), h / Math.Max(1.0, height));
    }

    // Measures the pre-snap layout over the node boxes, the trunk gutters and the band heights
    // with the shelf offsets. A revealed lane and everything below it are left out.
    private static (double Width, double Height) Measure(LayoutTree tree, LayoutConfig cfg, IReadOnlyList<LayoutWrap> wraps)
    {
        TidyTree.AssignX(tree, cfg, wraps, skipRevealed: true);
        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;
        foreach (LayoutNode node in tree.Nodes)
        {
            if (UnderReveal(node))
                continue;
            double y = node.Band * cfg.BandHeightPx + node.WrapDy;
            double half = Math.Max(node.DotRadius, node.Height / 2.0);
            minX = Math.Min(minX, node.X - node.Width / 2.0);
            maxX = Math.Max(maxX, node.X + node.Width / 2.0);
            minY = Math.Min(minY, y - half);
            maxY = Math.Max(maxY, y + half);
        }

        var hubs = new Dictionary<string, LayoutNode>();
        foreach (LayoutNode node in tree.Nodes)
            hubs.TryAdd(node.Id, node);
        foreach (LayoutWrap wrap in wraps)
        {
            LayoutNode hub = hubs[wrap.ParentId];
            foreach (LayoutEdge edge in hub.Out)
            {
                if (edge.IsHubLink || edge.To.IsRevealed)
                    continue;
                foreach (LayoutNode node in LayoutTree.PreOrder(edge.To))
                    minX = Math.Min(minX, node.X - node.Width / 2.0 - wrap.GutterPx / 2.0);
            }
        }
        return (maxX - minX, maxY - minY);
    }

    private static bool UnderReveal(LayoutNode node)
    {
        for (LayoutNode? n = node; n != null; n = n.Parent)
        {
            if (n.IsRevealed)
                return true;
        }
        return false;
    }

    private static void Reset(LayoutTree tree)
    {
        foreach (LayoutNode node in tree.Nodes)
        {
            node.WrapRow = -1;
            node.Shelf = 0;
            node.WrapDy = 0.0;
        }
    }

    // Lists the fans that may wrap in pre-order. Such a fan is a body whose lanes, not counting
    // the revealed ones, are more than the minimum. Only Cumulative-down wraps, and only with
    // Wrap Auto.
    private static List<Fan> FindFans(LayoutTree tree, LayoutConfig cfg)
    {
        var fans = new List<Fan>();
        if (cfg.Wrap != WrapMode.Auto || cfg.Mode != LayoutMode.CumulativeDown)
            return fans;
        foreach (LayoutNode node in tree.Nodes)
        {
            if (node.Kind == LayoutKind.Hub)
                continue;
            int unrevealed = 0;
            foreach (LayoutEdge edge in node.Out)
            {
                if (IsLane(edge) && !edge.To.IsRevealed)
                    unrevealed++;
            }
            if (unrevealed > cfg.WrapMinLanesCd)
                fans.Add(new Fan { Hub = node, Unrevealed = unrevealed });
        }
        return fans;
    }

    private static bool IsLane(LayoutEdge edge)
    {
        return edge.Class != EdgeClass.Ladder && !edge.IsHubLink;
    }

    private static int[] RowCounts(List<Fan> fans, int cap, LayoutConfig cfg)
    {
        var rows = new int[fans.Count];
        for (int i = 0; i < fans.Count; i++)
            rows[i] = RowSizes(fans[i].Unrevealed, cap, cfg.WrapKmaxCd)?.Length ?? 1;
        return rows;
    }

    // Balanced row sizes for n lanes, the first rows taking the one extra lane, or null when the
    // fan keeps one row. No row holds a single lane.
    private static int[]? RowSizes(int n, int cap, int kmax)
    {
        int rows = Math.Min(cap, (n + kmax - 1) / kmax);
        while (rows >= 2 && n / rows < 2)
            rows--;
        if (rows < 2)
            return null;
        var sizes = new int[rows];
        for (int r = 0; r < rows; r++)
            sizes[r] = n / rows + (r < n % rows ? 1 : 0);
        return sizes;
    }

    // Sets the rows and shelves of one cap and returns its wrap records. A fan inside a lane of a
    // wrapped fan does not wrap, so every wrapped hub is on shelf 0 and its row r is shelf r.
    private static List<LayoutWrap> Apply(LayoutTree tree, LayoutConfig cfg, List<Fan> fans, int cap, double gapPx, double gutterPx, bool skipRevealed)
    {
        Reset(tree);
        var wrapped = new List<(LayoutNode Hub, int Rows)>();
        var wrappedHubs = new HashSet<LayoutNode>();
        foreach (Fan fan in fans)
        {
            if (InsideWrappedFan(fan.Hub, wrappedHubs))
                continue;
            int[]? sizes = RowSizes(fan.Unrevealed, cap, cfg.WrapKmaxCd);
            if (sizes == null)
                continue;
            if (!AssignRows(fan.Hub, sizes))
            {
                foreach (LayoutEdge edge in fan.Hub.Out)
                    edge.To.WrapRow = -1;
                continue;
            }
            wrapped.Add((fan.Hub, sizes.Length));
            wrappedHubs.Add(fan.Hub);
        }
        var wraps = new List<LayoutWrap>(wrapped.Count);
        if (wrapped.Count == 0)
            return wraps;

        // Pre-order puts every parent before its children.
        int shelves = 1;
        foreach (LayoutNode node in tree.Nodes)
        {
            node.Shelf = node.Parent == null ? 0 : Math.Max(node.Parent.Shelf, node.WrapRow);
            shelves = Math.Max(shelves, node.Shelf + 1);
        }

        double h = cfg.BandHeightPx;
        var dy = new double[shelves];
        for (int r = 1; r < shelves; r++)
        {
            double need = dy[r - 1];
            foreach ((LayoutNode hub, int rows) in wrapped)
            {
                if (rows <= r)
                    continue;
                double deepest = double.NegativeInfinity;
                foreach (LayoutEdge edge in hub.Out)
                {
                    if (edge.IsHubLink || edge.To.WrapRow >= r)
                        continue;
                    foreach (LayoutNode node in LayoutTree.PreOrder(edge.To))
                    {
                        if (!skipRevealed || !UnderReveal(node))
                            deepest = Math.Max(deepest, node.Band * h + dy[node.Shelf]);
                    }
                }
                need = Math.Max(need, deepest - hub.Band * h + gapPx);
            }
            dy[r] = SnapUp(need, cfg.GridPx);
        }
        foreach (LayoutNode node in tree.Nodes)
            node.WrapDy = dy[node.Shelf];

        foreach ((LayoutNode hub, int rows) in wrapped)
        {
            var sizes = new int[rows];
            foreach (LayoutEdge edge in hub.Out)
            {
                if (IsLane(edge))
                    sizes[edge.To.WrapRow]++;
            }
            var rowDy = new double[rows];
            for (int r = 0; r < rows; r++)
                rowDy[r] = dy[r] - hub.WrapDy;
            wraps.Add(new LayoutWrap
            {
                ParentId = hub.Id,
                Mode = cfg.Mode,
                RowSizes = sizes,
                RowDy = rowDy,
                GutterPx = gutterPx
            });
        }
        return wraps;
    }

    private static bool InsideWrappedFan(LayoutNode hub, HashSet<LayoutNode> wrappedHubs)
    {
        for (LayoutNode? n = hub.Parent; n != null; n = n.Parent)
        {
            if (wrappedHubs.Contains(n))
                return true;
        }
        return false;
    }

    // Fills the rows in the hub's order. The rungs take row 0, every unrevealed lane the next free
    // place, and a revealed lane the row of the lane before it. A "+N" group that does not land
    // in the last row leaves the fan unwrapped.
    private static bool AssignRows(LayoutNode hub, int[] sizes)
    {
        int row = 0;
        int filled = 0;
        int previous = 0;
        foreach (LayoutEdge edge in hub.Out)
        {
            LayoutNode child = edge.To;
            if (edge.Class == EdgeClass.Ladder)
            {
                child.WrapRow = 0;
                continue;
            }
            if (edge.IsHubLink)
                continue;
            if (child.IsRevealed)
            {
                child.WrapRow = previous;
                continue;
            }
            while (filled == sizes[row])
            {
                row++;
                filled = 0;
            }
            child.WrapRow = row;
            filled++;
            previous = row;
            if (child.Kind == LayoutKind.MinorGroup && row != sizes.Length - 1)
                return false;
        }
        return true;
    }

    private static double SnapUp(double v, double g) => Math.Ceiling(v / g - 1e-9) * g;

    #region GravityWell

    // The hubs a GravityWell plan may wrap, which are the star hub whose lanes wrap into shelves
    // and the fans of moons that wrap into rows by count, with the columns they are measured on.
    // The rest is the same for every candidate, so it is found once. GutterFloor holds the
    // gutter a hub needs for the label plates beside its trunk, for each hub that has one.
    // Shown holds every node outside a revealed lane with how far its label plate reaches right
    // of its dot, and Half the largest half height of a shown node.
    private sealed class WellPlan
    {
        public required LayoutNode? ShelfHub { get; init; }
        public required int ShelfLanes { get; init; }
        public required List<Fan> Fans { get; init; }
        public required TidyTree.WellColumns Columns { get; init; }
        public required Dictionary<LayoutNode, double> GutterFloor { get; init; }
        public required List<(LayoutNode Node, double Plate)> Shown { get; init; }
        public required double Half { get; init; }
    }

    // What the candidates of one sweep share. FanMeasures holds the measures with only the moon
    // fans wrapped, keyed by the cap that wraps them, the gutter and the gap, because the shelves
    // of the star hub are split on such a measure and a pair that wraps no shelf is measured on
    // it too. Bounds holds every measured plan with its bounds, keyed by the plan's key, the
    // gutter and the gap, which fix both, because several shelf counts often split the star hub
    // alike.
    private sealed class WellCache
    {
        public readonly Dictionary<(int Cap, double Gutter, double Gap), TidyTree.WellMeasure> FanMeasures = new();
        public readonly Dictionary<(string Key, double Gutter, double Gap), (WellPass Pass, double Width, double Height)> Bounds = new();
    }

    // The rows of one plan, which hold the row count and the gutter of every wrapped hub, the
    // measure of the map with them, and a key that tells plans with the same rows apart from
    // the others.
    private sealed class WellPass
    {
        public required Dictionary<string, (int Rows, double Gutter)> Wrapped { get; init; }
        public required TidyTree.WellMeasure Measure { get; init; }
        public required string Key { get; init; }
    }

    private static IReadOnlyList<LayoutWrap> PlanWell(LayoutTree tree, LayoutConfig cfg, int? rowCap, int? shelves, ref Choice choice)
    {
        Reset(tree);
        WellPlan? plan = FindWell(tree, cfg);
        if (plan == null)
            return Array.Empty<LayoutWrap>();

        Candidate chosen;
        if (rowCap.HasValue || shelves.HasValue)
        {
            chosen = EvaluateWell(tree, cfg, plan, rowCap ?? 1, shelves ?? 1, null).Candidate;
        }
        else
        {
            // With one shelf and no moon fan that wraps at any cap, the only candidate wraps
            // nothing.
            List<int> caps = DistinctCaps(tree, cfg, plan, out bool anyFan);
            if (!anyFan && MaxShelves(plan, cfg) == 1)
            {
                Reset(tree);
                return Array.Empty<LayoutWrap>();
            }
            List<Candidate> candidates = WellCandidates(tree, cfg, plan, caps);
            chosen = candidates[Choose(candidates, cfg.WrapMinGain)];
            choice = new Choice(chosen.RowCap, chosen.Shelves);
        }

        if (chosen.Fans == 0)
        {
            Reset(tree);
            return Array.Empty<LayoutWrap>();
        }
        return ApplyWell(tree, cfg, plan, chosen);
    }

    // Finds the star hub of the bus with the most lanes and every other node with more moon lanes
    // than the minimum. Null when the config does not wrap or nothing can wrap.
    private static WellPlan? FindWell(LayoutTree tree, LayoutConfig cfg)
    {
        if (cfg.Wrap != WrapMode.Auto || cfg.Mode != LayoutMode.GravityWell)
            return null;

        LayoutNode? shelfHub = null;
        int shelfLanes = 1;
        var bus = new List<LayoutNode>();
        CollectBus(tree.Root, bus);
        foreach (LayoutNode node in bus)
        {
            if (node.Kind != LayoutKind.Hub)
                continue;
            int lanes = 0;
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.Class != EdgeClass.Ladder && !edge.To.IsRevealed)
                    lanes++;
            }
            if (lanes > shelfLanes)
            {
                shelfHub = node;
                shelfLanes = lanes;
            }
        }

        var fans = new List<Fan>();
        foreach (LayoutNode node in tree.Nodes)
        {
            if (node == shelfHub)
                continue;
            int unrevealed = 0;
            foreach (LayoutEdge edge in node.Out)
            {
                if (IsLane(edge) && !edge.To.IsRevealed)
                    unrevealed++;
            }
            if (unrevealed > cfg.WrapMinLanesGw)
                fans.Add(new Fan { Hub = node, Unrevealed = unrevealed });
        }

        if (shelfHub == null && fans.Count == 0)
            return null;

        TidyTree.WellColumns columns = TidyTree.WellColumns.Build(tree);
        var gutterFloor = new Dictionary<LayoutNode, double>();
        if (shelfHub != null)
            AddGutterFloor(gutterFloor, columns, shelfHub, cfg);
        foreach (Fan fan in fans)
            AddGutterFloor(gutterFloor, columns, fan.Hub, cfg);

        var shown = new List<(LayoutNode Node, double Plate)>();
        double half = 0.0;
        foreach (LayoutNode node in tree.Nodes)
        {
            if (UnderReveal(node))
                continue;
            shown.Add((node, node.DotRadius + LabelPlacer.LabelPadPx + Math.Max(node.Width, cfg.MinNodeWidthPx)));
            half = Math.Max(half, Math.Max(node.DotRadius, node.Height / 2.0));
        }
        return new WellPlan
        {
            ShelfHub = shelfHub,
            ShelfLanes = shelfLanes,
            Fans = fans,
            Columns = columns,
            GutterFloor = gutterFloor,
            Shown = shown,
            Half = half
        };
    }

    // The gutter of a wrapped hub is the plain one, unless a label right of a rung below the
    // hub's spine reaches further. Then the gutter holds that plate with the line clearance and
    // one grid cell of snap on both sides of the trunk, which is the floor kept here.
    private static void AddGutterFloor(Dictionary<LayoutNode, double> floors, TidyTree.WellColumns columns, LayoutNode hub, LayoutConfig cfg)
    {
        double reach = columns.LabelReach(hub, cfg);
        if (reach <= 0.0)
            return;
        double clear = OverlapCheck.WrapLineClearPx + 1.0;
        floors[hub] = SnapUp(2.0 * (reach + clear + cfg.GridPx), cfg.GridPx);
    }

    private static void CollectBus(LayoutNode node, List<LayoutNode> bus)
    {
        bus.Add(node);
        foreach (LayoutEdge edge in node.Out)
        {
            if (edge.IsHubLink)
                CollectBus(edge.To, bus);
        }
    }

    private static int MaxShelves(WellPlan plan, LayoutConfig cfg)
    {
        return plan.ShelfHub == null ? 1 : Math.Min(cfg.WrapMaxRowsGw, plan.ShelfLanes);
    }

    // Every pair of a moon row cap and a shelf count, fewer moon rows first and then fewer
    // shelves, so the chooser keeps fewer rows on a tie. A pair that gives the rows of a pair
    // before it is left out, and the first candidate wraps nothing. A pair is scored from its
    // moon rows and its shelf count alone, so a cap that gives the moon rows of a smaller cap
    // would only repeat its pairs and is not scored at all.
    private static List<Candidate> WellCandidates(LayoutTree tree, LayoutConfig cfg, WellPlan plan)
    {
        return WellCandidates(tree, cfg, plan, DistinctCaps(tree, cfg, plan, out _));
    }

    private static List<Candidate> WellCandidates(LayoutTree tree, LayoutConfig cfg, WellPlan plan, List<int> caps)
    {
        var candidates = new List<Candidate>();
        var seen = new HashSet<string>();
        var cache = new WellCache();
        int maxShelves = MaxShelves(plan, cfg);
        foreach (int cap in caps)
        {
            for (int s = 1; s <= maxShelves; s++)
            {
                (Candidate candidate, string key) = EvaluateWell(tree, cfg, plan, cap, s, cache);
                if (seen.Add(key))
                    candidates.Add(candidate);
            }
        }
        return candidates;
    }

    // The caps from one row up whose moon rows differ from those of every smaller cap. anyFan
    // tells whether a moon fan wraps at any cap.
    private static List<int> DistinctCaps(LayoutTree tree, LayoutConfig cfg, WellPlan plan, out bool anyFan)
    {
        var caps = new List<int>();
        var seen = new HashSet<string>();
        anyFan = false;
        for (int cap = 1; cap <= cfg.WrapMaxRowsGw; cap++)
        {
            Reset(tree);
            string key = WrapFans(plan, cfg, cap, null, 0.0);
            anyFan |= key.Length > 0;
            if (seen.Add(key))
                caps.Add(cap);
        }
        Reset(tree);
        return caps;
    }

    // Scores one pair the way Evaluate scores a cap, with the gap and the gutters sized again for
    // the zoom the first measure gives.
    private static (Candidate Candidate, string Key) EvaluateWell(LayoutTree tree, LayoutConfig cfg, WellPlan plan, int cap, int shelves, WellCache? cache)
    {
        (WellPass first, double w0, double h0) = MeasureWell(tree, cfg, plan, cap, shelves, cfg.WellBandHeightPx, cfg.WrapGutterPx, cache);
        if (first.Wrapped.Count == 0)
        {
            Reset(tree);
            return (new Candidate(cap, 0, 0, w0, h0, Score(w0, h0, cfg), 0.0, 0.0, shelves), first.Key);
        }

        double zNom = ScreenZoom(w0, h0, cfg);
        double g = cfg.GridPx;
        double gap = Math.Max(cfg.WellBandHeightPx, SnapUp(cfg.ShelfClearScreenPx / zNom, g));
        double gutter = Gutter(cfg, zNom);

        (WellPass pass, double w, double h) = MeasureWell(tree, cfg, plan, cap, shelves, gap, gutter, cache);
        int rows = 0;
        foreach ((int r, double _) in pass.Wrapped.Values)
            rows += r;
        Reset(tree);
        return (new Candidate(cap, pass.Wrapped.Count, rows, w, h, Score(w, h, cfg), gap, gutter, shelves), pass.Key);
    }

    // Sets the rows of one pair, measures the map with them without the revealed lanes and finds
    // its bounds. The plan's key, the gutter and the gap fix the measure and the bounds, so a
    // plan the cache already holds is taken from there.
    private static (WellPass Pass, double Width, double Height) MeasureWell(LayoutTree tree, LayoutConfig cfg, WellPlan plan, int cap, int shelves, double gapPx, double gutterPx, WellCache? cache)
    {
        (Dictionary<string, (int Rows, double Gutter)> wrapped, string key, bool shelved) = SetRows(tree, cfg, plan, cap, shelves, gapPx, gutterPx, cache);
        if (cache != null && cache.Bounds.TryGetValue((key, gutterPx, gapPx), out (WellPass Pass, double Width, double Height) known))
            return known;
        var pass = new WellPass
        {
            Wrapped = wrapped,
            Measure = MeasureRows(cfg, plan, cap, wrapped, shelved, gapPx, gutterPx, skipRevealed: true, cache),
            Key = key
        };
        (double w, double h) = WellBounds(tree, cfg, plan, pass);
        if (cache != null)
            cache.Bounds[(key, gutterPx, gapPx)] = (pass, w, h);
        return (pass, w, h);
    }

    // Sets the rows of one pair and measures the map with them.
    private static WellPass AssignWell(LayoutTree tree, LayoutConfig cfg, WellPlan plan, int cap, int shelves, double gapPx, double gutterPx, bool skipRevealed)
    {
        (Dictionary<string, (int Rows, double Gutter)> wrapped, string key, bool shelved) = SetRows(tree, cfg, plan, cap, shelves, gapPx, gutterPx, null);
        return new WellPass { Wrapped = wrapped, Measure = MeasureRows(cfg, plan, cap, wrapped, shelved, gapPx, gutterPx, skipRevealed, null), Key = key };
    }

    // Sets the rows of one pair and returns the row count and the gutter of every wrapped hub,
    // the key of the rows, and whether the star hub wraps into shelves. The moon fans take their
    // rows by count first. The shelves of the star hub then split its lanes by the width of their
    // blocks, moon rows included, which is why the blocks are measured before the split.
    private static (Dictionary<string, (int Rows, double Gutter)> Wrapped, string Key, bool Shelved) SetRows(LayoutTree tree, LayoutConfig cfg, WellPlan plan, int cap, int shelves, double gapPx, double gutterPx, WellCache? cache)
    {
        Reset(tree);
        var wrapped = new Dictionary<string, (int Rows, double Gutter)>(plan.Fans.Count + 1);
        var key = new System.Text.StringBuilder(WrapFans(plan, cfg, cap, wrapped, gutterPx));
        if (plan.ShelfHub != null && shelves >= 2)
        {
            int rows = AssignShelves(plan.ShelfHub, FanMeasure(cfg, plan, cap, wrapped, gapPx, gutterPx, cache), shelves, cfg);
            if (rows >= 2)
            {
                wrapped[plan.ShelfHub.Id] = (rows, HubGutter(plan, plan.ShelfHub, gutterPx));
                key.Append(plan.ShelfHub.Id).Append(':');
                foreach (LayoutEdge edge in plan.ShelfHub.Out)
                {
                    if (edge.Class != EdgeClass.Ladder)
                        key.Append(edge.To.WrapRow).Append(',');
                }
                return (wrapped, key.ToString(), true);
            }
        }
        return (wrapped, key.ToString(), false);
    }

    // Measures the rows SetRows set. When only the moon fans wrap, the map measures as the
    // blocks the shelves are split on.
    private static TidyTree.WellMeasure MeasureRows(LayoutConfig cfg, WellPlan plan, int cap, Dictionary<string, (int Rows, double Gutter)> wrapped, bool shelved, double gapPx, double gutterPx, bool skipRevealed, WellCache? cache)
    {
        if (shelved)
            return plan.Columns.Measure(cfg, wrapped, gapPx, skipRevealed, blockWidths: false);
        return skipRevealed ? FanMeasure(cfg, plan, cap, wrapped, gapPx, gutterPx, cache) : plan.Columns.Measure(cfg, wrapped, gapPx, skipRevealed);
    }

    // The measure without the revealed lanes while only the moon fans of the cap are wrapped,
    // from the cache when one is given.
    private static TidyTree.WellMeasure FanMeasure(LayoutConfig cfg, WellPlan plan, int cap, Dictionary<string, (int Rows, double Gutter)> wrapped, double gapPx, double gutterPx, WellCache? cache)
    {
        if (cache == null)
            return plan.Columns.Measure(cfg, wrapped, gapPx, skipRevealed: true);
        if (!cache.FanMeasures.TryGetValue((cap, gutterPx, gapPx), out TidyTree.WellMeasure? measure))
        {
            measure = plan.Columns.Measure(cfg, wrapped, gapPx, skipRevealed: true);
            cache.FanMeasures[(cap, gutterPx, gapPx)] = measure;
        }
        return measure;
    }

    // Wraps the moon fans of one cap by count on a tree with no rows set, and returns the key of
    // their rows. wrapped, when given, takes the row count and the gutter of every wrapped fan.
    private static string WrapFans(WellPlan plan, LayoutConfig cfg, int cap, Dictionary<string, (int Rows, double Gutter)>? wrapped, double gutterPx)
    {
        var key = new System.Text.StringBuilder();
        foreach (Fan fan in plan.Fans)
        {
            int[]? sizes = RowSizes(fan.Unrevealed, cap, cfg.WrapKmaxGw);
            if (sizes == null)
                continue;
            if (!AssignRows(fan.Hub, sizes))
            {
                foreach (LayoutEdge edge in fan.Hub.Out)
                    edge.To.WrapRow = -1;
                continue;
            }
            if (wrapped != null)
                wrapped[fan.Hub.Id] = (sizes.Length, HubGutter(plan, fan.Hub, gutterPx));
            key.Append(fan.Hub.Id).Append(':').Append(sizes.Length).Append(';');
        }
        return key.ToString();
    }

    private static double HubGutter(WellPlan plan, LayoutNode hub, double gutterPx)
    {
        return plan.GutterFloor.TryGetValue(hub, out double floor) ? Math.Max(gutterPx, floor) : gutterPx;
    }

    // Splits the lanes of the star hub into at most the given number of shelves, contiguous in the
    // hub's order, so the widest shelf is as narrow as it can be. Among the splits that reach that
    // width, each shelf takes as many systems as fit, so a smaller count that fits as narrow wins.
    // A revealed lane joins the shelf of the lane before it and the rungs stay in row 0. Returns
    // the shelf count, and leaves the hub unwrapped when that is one or when a "+N" group would
    // not land in the last shelf.
    private static int AssignShelves(LayoutNode hub, TidyTree.WellMeasure blocks, int shelves, LayoutConfig cfg)
    {
        var widths = new List<double>();
        var gaps = new List<double>();
        foreach (LayoutEdge edge in hub.Out)
        {
            if (edge.Class == EdgeClass.Ladder || edge.To.IsRevealed)
                continue;
            widths.Add(blocks.BlockWidth[edge]);
            gaps.Add(edge.IsHubLink ? cfg.BusGapPx : cfg.SiblingGapPx);
        }
        int[] shelfOf = Partition(widths, gaps, shelves);
        int count = shelfOf.Length == 0 ? 1 : shelfOf[^1] + 1;
        if (count < 2)
            return 1;

        int lane = 0;
        int previous = 0;
        foreach (LayoutEdge edge in hub.Out)
        {
            LayoutNode child = edge.To;
            if (edge.Class == EdgeClass.Ladder)
            {
                child.WrapRow = 0;
                continue;
            }
            if (child.IsRevealed)
            {
                child.WrapRow = previous;
                continue;
            }
            child.WrapRow = shelfOf[lane++];
            previous = child.WrapRow;
            if (child.Kind == LayoutKind.MinorGroup && child.WrapRow != count - 1)
            {
                foreach (LayoutEdge e in hub.Out)
                    e.To.WrapRow = -1;
                return 1;
            }
        }
        return count;
    }

    // The narrowest widest row of a contiguous split of the blocks into at most k rows, found by
    // dynamic programming, then filled greedily from the left at that width. gaps[i] is the gap in
    // front of block i when it is not the first of its row. Returns the row of every block.
    private static int[] Partition(List<double> widths, List<double> gaps, int k)
    {
        int n = widths.Count;
        var rowOf = new int[n];
        if (n == 0)
            return rowOf;
        k = Math.Clamp(k, 1, n);

        // rowWidth[from, to] is the width of the blocks from index from to index to in one row. It
        // is summed from the left, so every entry adds its terms in the order a sum from scratch
        // would.
        var rowWidth = new double[n, n];
        for (int from = 0; from < n; from++)
        {
            double w = widths[from];
            rowWidth[from, from] = w;
            for (int i = from + 1; i < n; i++)
            {
                w += gaps[i] + widths[i];
                rowWidth[from, i] = w;
            }
        }
        double RowWidth(int from, int to) => rowWidth[from, to];

        // best[r, j] is the narrowest widest row of the first j blocks in r + 1 rows.
        var best = new double[k, n + 1];
        for (int j = 1; j <= n; j++)
            best[0, j] = RowWidth(0, j - 1);
        for (int r = 1; r < k; r++)
        {
            for (int j = r + 1; j <= n; j++)
            {
                double b = double.PositiveInfinity;
                for (int i = r; i < j; i++)
                    b = Math.Min(b, Math.Max(best[r - 1, i], RowWidth(i, j - 1)));
                best[r, j] = b;
            }
        }
        double limit = best[k - 1, n] + 1e-6;

        int row = 0;
        double current = widths[0];
        for (int i = 1; i < n; i++)
        {
            double next = current + gaps[i] + widths[i];
            if (next > limit)
            {
                row++;
                current = widths[i];
            }
            else
            {
                current = next;
            }
            rowOf[i] = row;
        }
        return rowOf;
    }

    // Sets the rows of the chosen pair, then the offsets of the full map with the revealed lanes,
    // and returns the wrap records in pre-order. A node's WrapDy is its parent's plus the offset
    // of its row when it is a lane in a lower row, so the offsets add up through nested rows.
    private static List<LayoutWrap> ApplyWell(LayoutTree tree, LayoutConfig cfg, WellPlan plan, Candidate chosen)
    {
        WellPass pass = AssignWell(tree, cfg, plan, chosen.RowCap, chosen.Shelves, chosen.GapPx, chosen.GutterPx, skipRevealed: true);
        TidyTree.WellMeasure measure = plan.Columns.Measure(cfg, pass.Wrapped, chosen.GapPx, skipRevealed: false, blockWidths: false);

        foreach (LayoutNode node in tree.Nodes)
        {
            LayoutNode? parent = node.Parent;
            if (parent == null)
                continue;
            double dy = parent.WrapDy;
            if (node.WrapRow >= 1 && node.ParentEdge!.Class != EdgeClass.Ladder && measure.RowDy.TryGetValue(parent.Id, out double[]? rowDy))
                dy += rowDy[node.WrapRow];
            node.WrapDy = dy;
        }

        var wraps = new List<LayoutWrap>(pass.Wrapped.Count);
        foreach (LayoutNode hub in tree.Nodes)
        {
            if (!pass.Wrapped.TryGetValue(hub.Id, out (int Rows, double Gutter) wrapped))
                continue;
            var sizes = new int[wrapped.Rows];
            foreach (LayoutEdge edge in hub.Out)
            {
                if (edge.Class != EdgeClass.Ladder && edge.To.WrapRow >= 0)
                    sizes[edge.To.WrapRow]++;
            }
            wraps.Add(new LayoutWrap
            {
                ParentId = hub.Id,
                Mode = cfg.Mode,
                RowSizes = sizes,
                RowDy = measure.RowDy[hub.Id],
                Balanced = hub != plan.ShelfHub,
                GutterPx = wrapped.Gutter
            });
        }
        return wraps;
    }

    // The bounds of a plan without the revealed lanes, which hold the dots and the label plates as
    // the layout's bounds do. The width comes from placing the columns, which costs as little as
    // the measure. A label sits right of its dot, except on a wrapped root, whose spine the trunk
    // leaves to the right, so its plate moves to the left of its dot.
    private static (double Width, double Height) WellBounds(LayoutTree tree, LayoutConfig cfg, WellPlan plan, WellPass pass)
    {
        var wrapsByHub = new Dictionary<string, LayoutWrap>();
        foreach ((string hubId, (int rows, double gutter)) in pass.Wrapped)
        {
            wrapsByHub[hubId] = new LayoutWrap
            {
                ParentId = hubId,
                Mode = cfg.Mode,
                RowSizes = new int[rows],
                RowDy = pass.Measure.RowDy[hubId],
                GutterPx = gutter
            };
        }
        plan.Columns.Place(cfg, wrapsByHub, skipRevealed: true);

        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        bool rootWrapped = wrapsByHub.ContainsKey(tree.Root.Id);
        foreach ((LayoutNode node, double plate) in plan.Shown)
        {
            minX = Math.Min(minX, node.X - node.DotRadius);
            maxX = Math.Max(maxX, node.X + plate);
            if (rootWrapped && node == tree.Root)
                minX = Math.Min(minX, node.X - plate);
        }
        TidyTree.WellMeasure measure = pass.Measure;
        return (maxX - minX, measure.Bottom - measure.Top + 2.0 * plan.Half);
    }

    #endregion
}
