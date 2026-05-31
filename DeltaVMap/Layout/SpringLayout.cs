using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// A force-directed (spring) placement for both X and Y, an organic alternative to the
// tidy-tree modes. Every node repels every other node, every edge pulls its endpoints
// together like a spring, and the whole thing is settled with a fixed number of cooling
// iterations into an equilibrium web. The root is pinned at the origin so the map stays
// centered on where you are.
//
// This is the classic Fruchterman-Reingold model: at the ideal edge length the spring
// pull and the pairwise repulsion balance, so connected bodies sit about one ideal length
// apart and unconnected ones drift away. Edge classes get different rest lengths so a
// body's own rungs cluster tight, hubs sit close to their bodies, and cross-body transfers
// stretch out. It is fully deterministic - positions start on a fixed spiral by node order
// and there is no randomness - so it does not jitter between frames and only runs once per
// (re)build, never per frame. Unlike the tidy-tree modes it carries no dV-as-position
// meaning (the exact dV stays on the badge); a grid snap afterwards keeps dots from sharing
// a cell, and edges are drawn straight rather than octilinear.
//
// Repulsion is computed with a Barnes-Hut quadtree (~O(n log n) per iteration) instead of
// the original all-pairs O(n^2), and the iteration count is scaled down as the node count
// grows. Together these keep the sim bounded on dense systems, where the old all-pairs loop
// over thousands of nodes froze the game on the draw thread.
internal static class SpringLayout
{
    // Golden angle, for a low-clumping deterministic initial spiral.
    private const double GoldenAngle = 2.399963229728653;

    // Iteration scaling: keep the configured count for small graphs, and shrink toward a floor
    // as the node count grows, so a large settle never stretches into the old quadratic-cost
    // freeze. The budget is the target product of iterations and node count, held between the
    // two clamps below; past the floor the per-build work grows only linearly again, and the
    // absolute node ceiling that caps it lives render-side (MapWindow.MaxLayoutNodes).
    private const int IterationWorkBudget = 120_000;
    private const int MinIterations = 60;

    public static void Assign(LayoutTree tree, LayoutConfig cfg)
    {
        IReadOnlyList<LayoutNode> nodes = tree.Nodes;
        int n = nodes.Count;
        if (n == 0)
            return;

        double k = cfg.SpringIdealLengthPx;
        var px = new double[n];
        var py = new double[n];
        var dispX = new double[n];
        var dispY = new double[n];
        var index = new Dictionary<LayoutNode, int>(n);

        for (int i = 0; i < n; i++)
        {
            index[nodes[i]] = i;
            double radius = k * Math.Sqrt(i + 1);
            double angle = i * GoldenAngle;
            px[i] = radius * Math.Cos(angle);
            py[i] = radius * Math.Sin(angle);
            nodes[i].Band = 0;
        }

        int rootIndex = index[tree.Root];
        px[rootIndex] = 0.0;
        py[rootIndex] = 0.0;

        // Flatten the edges to index pairs with their rest length once, so the inner loops
        // touch no objects.
        var edges = new List<(int A, int B, double Ideal)>();
        foreach (LayoutNode node in nodes)
        {
            foreach (LayoutEdge edge in node.Out)
                edges.Add((index[edge.From], index[edge.To], k * RestLengthFactor(edge.Class)));
        }

        // Cooling schedule: cap each node's per-step move at the temperature, which starts
        // wide (a couple of ideal lengths, so early steps untangle the spiral) and decays
        // linearly toward zero, letting the web freeze into its equilibrium instead of
        // oscillating around it.
        int iterations = ScaleIterations(cfg.SpringIterations, n);
        double temperature = k * 2.0;
        double cooling = temperature / (iterations + 1);

        var tree2d = new BarnesHut();

        for (int step = 0; step < iterations; step++)
        {
            Array.Clear(dispX);
            Array.Clear(dispY);

            // Repulsion via a Barnes-Hut quadtree: a distant cluster of nodes is summed as one
            // body, so each node gathers force from O(log n) cells instead of all n. This is
            // what scales the sim to thousands of nodes; the all-pairs version froze the game.
            tree2d.Build(px, py, n);
            for (int i = 0; i < n; i++)
            {
                tree2d.Repulsion(i, px[i], py[i], k, out double rfx, out double rfy);
                dispX[i] += rfx;
                dispY[i] += rfy;
            }

            // Attraction along edges: distance^2 / ideal, pulling endpoints together.
            foreach ((int a, int b, double ideal) in edges)
            {
                double ex = px[b] - px[a];
                double ey = py[b] - py[a];
                double dist = Math.Sqrt(ex * ex + ey * ey);
                if (dist < 1e-4)
                    continue;
                double force = dist * dist / ideal;
                double ux = ex / dist;
                double uy = ey / dist;
                dispX[a] += ux * force;
                dispY[a] += uy * force;
                dispX[b] -= ux * force;
                dispY[b] -= uy * force;
            }

            // Move each node along its net force, capped by the cooling temperature so the
            // layout settles instead of oscillating. The root stays pinned at the center.
            for (int i = 0; i < n; i++)
            {
                if (i == rootIndex)
                    continue;
                double len = Math.Sqrt(dispX[i] * dispX[i] + dispY[i] * dispY[i]);
                if (len < 1e-9)
                    continue;
                double move = Math.Min(len, temperature);
                px[i] += dispX[i] / len * move;
                py[i] += dispY[i] / len * move;
            }

            temperature = Math.Max(1.0, temperature - cooling);
        }

        for (int i = 0; i < n; i++)
        {
            nodes[i].X = px[i];
            nodes[i].Y = py[i];
        }
    }

    // Rest length per edge class, as a multiple of the ideal length: a body's own ladder
    // rungs sit tight, a hub hugs the body it links, and cross-body transfers stretch out
    // so separate systems read as separate clusters.
    private static double RestLengthFactor(EdgeClass edgeClass)
    {
        return edgeClass switch
        {
            EdgeClass.Ladder => 0.55,
            EdgeClass.HubLink => 0.7,
            _ => 1.35
        };
    }

    // Iterations to run for a graph of n nodes: the configured count for small graphs, scaled
    // down toward MinIterations as n grows so a large system cannot stretch the settle into a
    // multi-second build. Past about n = IterationWorkBudget / MinIterations the floor holds, so
    // per-build work grows linearly again (MinIterations * O(n log n), not the old quadratic);
    // the hard node bound is the render-side MaxLayoutNodes ceiling, not this. Never returns
    // more than the configured count.
    private static int ScaleIterations(int configured, int n)
    {
        int byBudget = IterationWorkBudget / Math.Max(1, n);
        return Math.Min(configured, Math.Max(MinIterations, byBudget));
    }

    // A Barnes-Hut quadtree over the node positions, rebuilt each iteration. Cells are pooled
    // and reused across iterations, so the per-iteration rebuild allocates nothing after the
    // first few. Each internal cell aggregates its subtree's body count and centre of mass; the
    // Repulsion traversal treats a far enough cell (cell width / distance < theta) as one body,
    // so a node gathers force from O(log n) cells instead of all n - the whole point of the
    // rewrite. Subdivision depth is capped so near-coincident points still terminate.
    private sealed class BarnesHut
    {
        // Opening angle: larger approximates more aggressively (faster, rougher). The layout is
        // a cosmetic web, so some approximation costs nothing meaningful.
        private const double Theta = 0.9;
        private const double ThetaSq = Theta * Theta;
        private const int MaxDepth = 24;
        private const double MinDistSq = 1e-6;

        private Cell[] _cells = new Cell[128];
        private int _used;
        private int _root;
        private double[] _px = Array.Empty<double>();
        private double[] _py = Array.Empty<double>();

        // Per-query state (one traversal runs at a time, single-threaded).
        private int _selfIndex;
        private double _qx;
        private double _qy;
        private double _k;
        private double _accX;
        private double _accY;

        public void Build(double[] px, double[] py, int n)
        {
            _px = px;
            _py = py;
            _used = 0;

            double minX = px[0];
            double maxX = px[0];
            double minY = py[0];
            double maxY = py[0];
            for (int i = 1; i < n; i++)
            {
                if (px[i] < minX) minX = px[i];
                if (px[i] > maxX) maxX = px[i];
                if (py[i] < minY) minY = py[i];
                if (py[i] > maxY) maxY = py[i];
            }

            double half = Math.Max(maxX - minX, maxY - minY) * 0.5;
            if (half <= 0.0)
                half = 1.0;
            half += 1e-6;
            _root = NewCell((minX + maxX) * 0.5, (minY + maxY) * 0.5, half, 0);
            for (int i = 0; i < n; i++)
                Insert(_root, i, px[i], py[i]);
        }

        public void Repulsion(int selfIndex, double x, double y, double k, out double fx, out double fy)
        {
            _selfIndex = selfIndex;
            _qx = x;
            _qy = y;
            _k = k;
            _accX = 0.0;
            _accY = 0.0;
            Accumulate(_root);
            fx = _accX;
            fy = _accY;
        }

        private int NewCell(double cx, double cy, double half, int depth)
        {
            if (_used == _cells.Length)
                Array.Resize(ref _cells, _cells.Length * 2);
            Cell c = _cells[_used] ??= new Cell();
            c.CenterX = cx;
            c.CenterY = cy;
            c.Half = half;
            c.Depth = depth;
            c.Count = 0;
            c.SumX = 0.0;
            c.SumY = 0.0;
            c.PointIndex = -1;
            c.Internal = false;
            c.C0 = c.C1 = c.C2 = c.C3 = -1;
            return _used++;
        }

        private void Insert(int cellIdx, int point, double x, double y)
        {
            // Aggregate the body into this cell on the way down. (Cell objects are pooled and
            // their references survive a NewCell array resize, so holding one across recursion
            // is safe.)
            Cell c = _cells[cellIdx];
            c.Count++;
            c.SumX += x;
            c.SumY += y;
            if (c.Count == 1)
            {
                c.PointIndex = point;   // first point: a leaf
                return;
            }

            if (!c.Internal)
            {
                if (c.Depth >= MaxDepth)
                {
                    // Too deep to split (near-coincident points): keep it an aggregate leaf.
                    c.PointIndex = -1;
                    return;
                }
                int stored = c.PointIndex;
                c.PointIndex = -1;
                c.Internal = true;
                if (stored >= 0)
                    InsertChild(c, stored, _px[stored], _py[stored]);
            }
            InsertChild(c, point, x, y);
        }

        private void InsertChild(Cell cell, int point, double x, double y)
        {
            int q = (x >= cell.CenterX ? 1 : 0) | (y >= cell.CenterY ? 2 : 0);
            int childIdx = cell.Child(q);
            if (childIdx < 0)
            {
                double childHalf = cell.Half * 0.5;
                double ox = (q & 1) == 1 ? childHalf : -childHalf;
                double oy = (q & 2) == 2 ? childHalf : -childHalf;
                childIdx = NewCell(cell.CenterX + ox, cell.CenterY + oy, childHalf, cell.Depth + 1);
                cell.SetChild(q, childIdx);
            }
            Insert(childIdx, point, x, y);
        }

        private void Accumulate(int cellIdx)
        {
            Cell c = _cells[cellIdx];
            if (c.Count == 0)
                return;

            double comX = c.SumX / c.Count;
            double comY = c.SumY / c.Count;
            double dx = _qx - comX;
            double dy = _qy - comY;
            double d2 = dx * dx + dy * dy;

            if (!c.Internal)
            {
                if (c.PointIndex == _selfIndex)
                    return;   // a single-point leaf that is the query node itself
                AddForce(dx, dy, d2, c.Count);
                return;
            }

            // A cell narrow relative to its distance is summed as one body; otherwise recurse.
            // A cell containing the query node is usually wide relative to its (small) distance,
            // so it fails this test and recurses to the leaf where the node skips itself. The
            // rare exception (the node alone in a sparse corner of an otherwise far, clustered
            // cell) folds the node's own mass into the aggregate - a negligible self-repulsion,
            // the standard bounded Barnes-Hut approximation, and the grid snap fixes overlap
            // regardless.
            double width = c.Half * 2.0;
            if (width * width < ThetaSq * d2)
            {
                AddForce(dx, dy, d2, c.Count);
                return;
            }

            if (c.C0 >= 0) Accumulate(c.C0);
            if (c.C1 >= 0) Accumulate(c.C1);
            if (c.C2 >= 0) Accumulate(c.C2);
            if (c.C3 >= 0) Accumulate(c.C3);
        }

        private void AddForce(double dx, double dy, double d2, int count)
        {
            if (d2 < MinDistSq)
            {
                // (Near-)coincident bodies: nudge deterministically so the force stays finite.
                dx = MinDistSq;
                dy = 0.0;
                d2 = MinDistSq;
            }
            double dist = Math.Sqrt(d2);
            double force = count * _k * _k / dist;
            _accX += dx / dist * force;
            _accY += dy / dist * force;
        }

        // One quadtree cell. A leaf holds a single PointIndex (or, only at MaxDepth, an
        // aggregate of coincident points with PointIndex -1); an internal cell holds up to four
        // child indices. Count / SumX / SumY aggregate the whole subtree for the approximation.
        private sealed class Cell
        {
            public double CenterX;
            public double CenterY;
            public double Half;
            public int Depth;
            public int Count;
            public double SumX;
            public double SumY;
            public int PointIndex;
            public bool Internal;
            public int C0;
            public int C1;
            public int C2;
            public int C3;

            public int Child(int q)
            {
                return q switch { 0 => C0, 1 => C1, 2 => C2, _ => C3 };
            }

            public void SetChild(int q, int idx)
            {
                switch (q)
                {
                    case 0: C0 = idx; break;
                    case 1: C1 = idx; break;
                    case 2: C2 = idx; break;
                    default: C3 = idx; break;
                }
            }
        }
    }
}
