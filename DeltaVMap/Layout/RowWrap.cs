using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// Picks the wide fans of a tree layout that continue in rows below their hub, and how far each
// row sits below the one above it. It is pure and deterministic: the choice depends only on the
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
internal static class RowWrap
{
    // One row cap the chooser scored. Width and Height are the estimated bounds of the map, Score
    // its fit zoom on the nominal canvas, Fans and Rows the wrapped fans and their rows in total.
    internal readonly record struct Candidate(int RowCap, int Fans, int Rows, double Width, double Height, double Score, double GapPx, double GutterPx);

    private sealed class Fan
    {
        public required LayoutNode Hub { get; init; }
        public required int Unrevealed { get; init; }
    }

    // Plans the rows of the tree and returns the wrapped fans. It sets WrapRow, Shelf and WrapDy on
    // every node, and needs the measured widths and the bands. rowCap forces one cap instead of
    // the chosen one, for tests that compare the candidates.
    public static IReadOnlyList<LayoutWrap> Plan(LayoutTree tree, LayoutConfig cfg, int? rowCap = null)
    {
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
        double zNom = SceneComposer.NominalZoom(w0, h0);
        double g = cfg.GridPx;
        double gap = Math.Max(cfg.BandHeightPx, SnapUp(cfg.ShelfClearScreenPx / zNom, g));
        double gutter = Math.Max(cfg.WrapGutterPx, SnapUp(2.0 * (cfg.MoonDotRadius + OverlapCheck.WrapLineClearPx) / zNom, g));

        List<LayoutWrap> wraps = Apply(tree, cfg, fans, cap, gap, gutter, skipRevealed: true);
        (double w, double h) = Measure(tree, cfg, wraps);
        int rows = 0;
        foreach (LayoutWrap wrap in wraps)
            rows += wrap.RowSizes.Count;
        Reset(tree);
        return new Candidate(cap, wraps.Count, rows, w, h, Score(w, h, cfg), gap, gutter);
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

    // The bounds of the pre-snap layout: node boxes, the trunk gutters and the band heights with
    // the shelf offsets. A revealed lane and everything below it are left out.
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

    // The fans that may wrap, in pre-order: the bodies whose lanes, not counting the revealed
    // ones, are more than the minimum. Only Cumulative-down wraps, and only with Wrap Auto.
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
}
