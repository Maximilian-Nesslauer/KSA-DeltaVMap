using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// Greedy label placement. Each node gets a few candidate slots
// around its dot (right, left, above, below); the first slot that conflicts with
// neither an already-placed label nor any node dot wins. Conflicts are checked
// against a spatial hash so the pass stays fast at a few hundred nodes instead of
// going quadratic. Nodes are processed in a stable order, so the assignment is
// deterministic; a node with no free slot is left unplaced (the renderer can drop or
// shrink such labels later, which is acceptable at high density).
internal static class LabelPlacer
{
    public readonly record struct Result(int Placed, int Total)
    {
        public int Dropped => Total - Placed;
    }

    public static Result Place(LayoutTree tree, LayoutConfig cfg)
    {
        const double pad = 4.0;
        double bucket = Math.Max(cfg.GridPx, cfg.MinNodeWidthPx);
        var hash = new SpatialHash(bucket);

        // Dots are immovable obstacles: a label must not cover another node's dot.
        foreach (LayoutNode node in tree.Nodes)
        {
            double r = node.DotRadius;
            hash.Insert(new Rect(node.SnappedX - r, node.SnappedY - r, 2 * r, 2 * r));
        }

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

        int placed = 0;
        foreach (LayoutNode node in ordered)
        {
            double w = Math.Max(node.Width, cfg.MinNodeWidthPx);
            double h = node.Height > 0 ? node.Height : cfg.LineHeightPx;
            double cx = node.SnappedX;
            double cy = node.SnappedY;
            double r = node.DotRadius;

            // Candidate slots in priority order: right, left, above, below.
            Span<Rect> slots =
            [
                new Rect(cx + r + pad, cy - h / 2, w, h),
                new Rect(cx - r - pad - w, cy - h / 2, w, h),
                new Rect(cx - w / 2, cy - r - pad - h, w, h),
                new Rect(cx - w / 2, cy + r + pad, w, h),
            ];

            bool done = false;
            foreach (Rect slot in slots)
            {
                if (hash.AnyOverlap(slot))
                    continue;
                hash.Insert(slot);
                node.LabelPlaced = true;
                node.LabelX = slot.X;
                node.LabelY = slot.Y;
                placed++;
                done = true;
                break;
            }

            if (!done)
                node.LabelPlaced = false;
        }

        return new Result(placed, tree.Nodes.Count);
    }

    internal readonly record struct Rect(double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;

        public bool Intersects(in Rect other)
        {
            return X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
        }
    }

    // A coarse spatial hash: rectangles are filed under every grid bucket they touch,
    // so an overlap query only compares against rectangles in the same neighbourhood.
    private sealed class SpatialHash
    {
        private readonly double _bucket;
        private readonly Dictionary<(int, int), List<Rect>> _cells = new();

        public SpatialHash(double bucket)
        {
            _bucket = bucket;
        }

        public void Insert(in Rect rect)
        {
            foreach ((int, int) key in Keys(rect))
            {
                if (!_cells.TryGetValue(key, out List<Rect>? list))
                {
                    list = new List<Rect>();
                    _cells[key] = list;
                }
                list.Add(rect);
            }
        }

        public bool AnyOverlap(in Rect rect)
        {
            foreach ((int, int) key in Keys(rect))
            {
                if (_cells.TryGetValue(key, out List<Rect>? list))
                {
                    foreach (Rect existing in list)
                    {
                        if (rect.Intersects(existing))
                            return true;
                    }
                }
            }
            return false;
        }

        private IEnumerable<(int, int)> Keys(Rect rect)
        {
            int minCol = (int)Math.Floor(rect.X / _bucket);
            int maxCol = (int)Math.Floor(rect.Right / _bucket);
            int minRow = (int)Math.Floor(rect.Y / _bucket);
            int maxRow = (int)Math.Floor(rect.Bottom / _bucket);
            for (int col = minCol; col <= maxCol; col++)
            {
                for (int row = minRow; row <= maxRow; row++)
                    yield return (col, row);
            }
        }
    }
}
