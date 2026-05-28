using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// Snaps every node onto a square integer grid and resolves the rare residual
// collision by nudging to the nearest free cell. The square grid is
// what gives the octilinear 0/45/90 metro look; it is also the last line of defence
// for overlaps. The tidy tree separates sibling subtrees and the bands separate
// parent from child, but a node deep in one subtree can still land on the same band
// as a shallow node in another at a close X. Because the grid cell is wider than the
// largest dot, two nodes in distinct cells can never overlap, so we only have to make
// every node's cell unique.
internal static class GridSnap
{
    public static void Snap(LayoutTree tree, LayoutConfig cfg)
    {
        double g = cfg.GridPx;

        foreach (LayoutNode node in tree.Nodes)
        {
            node.Col = (int)Math.Round(node.X / g, MidpointRounding.AwayFromZero);
            node.Row = (int)Math.Round(node.Y / g, MidpointRounding.AwayFromZero);
        }

        // Resolve collisions in a stable order so the result is deterministic: a node
        // earlier in (row, col, id) order keeps its cell, later ones get nudged.
        var ordered = new List<LayoutNode>(tree.Nodes);
        ordered.Sort(static (a, b) =>
        {
            int byRow = a.Row.CompareTo(b.Row);
            if (byRow != 0)
                return byRow;
            int byCol = a.Col.CompareTo(b.Col);
            if (byCol != 0)
                return byCol;
            return string.CompareOrdinal(a.Id, b.Id);
        });

        var occupied = new HashSet<(int Col, int Row)>();
        foreach (LayoutNode node in ordered)
        {
            var cell = (node.Col, node.Row);
            if (occupied.Contains(cell))
                cell = FindNearestFree(cell, occupied);

            node.Col = cell.Item1;
            node.Row = cell.Item2;
            occupied.Add(cell);

            node.SnappedX = node.Col * g;
            node.SnappedY = node.Row * g;
        }
    }

    // Search outward in rings of growing Chebyshev radius for the closest empty cell.
    // Within a ring, cells are tried in a fixed order that prefers staying on the same
    // row (smallest vertical move first), so a nudged node keeps its band where it
    // can. The radius is capped well above any realistic node count as a safety net.
    private static (int, int) FindNearestFree((int Col, int Row) cell, HashSet<(int Col, int Row)> occupied)
    {
        const int maxRadius = 1024;
        for (int r = 1; r <= maxRadius; r++)
        {
            (int, int)? best = null;
            int bestKey = int.MaxValue;

            for (int dRow = -r; dRow <= r; dRow++)
            {
                for (int dCol = -r; dCol <= r; dCol++)
                {
                    // Only the ring at exactly this radius; inner rings were tried.
                    if (Math.Max(Math.Abs(dRow), Math.Abs(dCol)) != r)
                        continue;

                    var candidate = (cell.Col + dCol, cell.Row + dRow);
                    if (occupied.Contains(candidate))
                        continue;

                    // Rank: prefer the smallest total move, then the smallest vertical
                    // move (stay on band), then a stable left/up bias. Packed into one
                    // comparable int with small bounded fields.
                    int key = (Math.Abs(dRow) + Math.Abs(dCol)) * 1_000_000
                        + Math.Abs(dRow) * 4_000
                        + (dRow + r) * 64
                        + (dCol + r);
                    if (key < bestKey)
                    {
                        bestKey = key;
                        best = candidate;
                    }
                }
            }

            if (best.HasValue)
                return best.Value;
        }

        // Unreachable for any sane tree; return the original cell rather than throw.
        return cell;
    }
}
