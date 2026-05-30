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
internal static class SpringLayout
{
    // Golden angle, for a low-clumping deterministic initial spiral.
    private const double GoldenAngle = 2.399963229728653;

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
        int iterations = cfg.SpringIterations;
        double temperature = k * 2.0;
        double cooling = temperature / (iterations + 1);

        for (int step = 0; step < iterations; step++)
        {
            Array.Clear(dispX);
            Array.Clear(dispY);

            // Repulsion between every pair: k*k / distance, pushing apart.
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double ex = px[i] - px[j];
                    double ey = py[i] - py[j];
                    double dist = Math.Sqrt(ex * ex + ey * ey);
                    if (dist < 1e-4)
                    {
                        // Coincident nodes: nudge deterministically by their index so they
                        // separate instead of dividing by zero.
                        ex = (i - j) * 1e-3 + 1e-3;
                        ey = (i + j) * 1e-3 + 1e-3;
                        dist = Math.Sqrt(ex * ex + ey * ey);
                    }
                    double force = k * k / dist;
                    double ux = ex / dist;
                    double uy = ey / dist;
                    dispX[i] += ux * force;
                    dispY[i] += uy * force;
                    dispX[j] -= ux * force;
                    dispY[j] -= uy * force;
                }
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
}
