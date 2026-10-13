using System;

namespace DeltaVMap.Layout;

// The outcome of a layout run: the laid-out tree, the config used, the label
// placement summary, and the snapped drawing bounds (handy for sizing a dump or a
// canvas). Positions live on the nodes themselves.
internal sealed class LayoutResult
{
    public required LayoutTree Tree { get; init; }
    public required LayoutConfig Config { get; init; }
    public required LabelPlacer.Result Labels { get; init; }
    public double MinX { get; init; }
    public double MinY { get; init; }
    public double MaxX { get; init; }
    public double MaxY { get; init; }

    // Every wrapped fan of the tree, empty when no wrap applies. An opened GravityWell destination
    // wraps too, and SceneComposer moves its trunks together with its nodes.
    public IReadOnlyList<LayoutWrap> Wraps { get; init; } = Array.Empty<LayoutWrap>();

    // The row cap and the shelf count the row plan took, null in Spring, which has no rows.
    public RowWrap.Choice? Rows { get; init; }

    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
}

// Runs the full layout pipeline on a prepared layout tree. The order matters only in
// that band Y and tidy X are computed before the grid snap that consumes both, and
// routing and labels come last because they read snapped positions. X and Y are
// otherwise independent: Y is data (cumulative dV), X is structure (the tidy tree),
// and neither is allowed to leak into the other.
internal static class LayoutEngine
{
    // measureText, when supplied, returns the real rendered width of a label in pixels
    // (from ImGui.CalcTextSize). The map passes it so the tidy tree spaces nodes by
    // their true on-screen size; the offline dump passes null and falls back to the
    // character-count estimate, which is enough to verify topology and overlap logic.
    public static LayoutResult Run(LayoutTree tree, LayoutConfig cfg, Func<string, double>? measureText = null)
    {
        return Run(tree, cfg, measureText, rowCap: null, shelves: null);
    }

    // The same run with the row cap of the wrap forced instead of chosen, for tests that compare
    // the full layouts of the caps the chooser scored.
    public static LayoutResult RunWithRowCap(LayoutTree tree, LayoutConfig cfg, int rowCap)
    {
        return Run(tree, cfg, null, rowCap, null);
    }

    // The same with the GravityWell shelf count of the star hub forced as well.
    public static LayoutResult RunWithRows(LayoutTree tree, LayoutConfig cfg, int rowCap, int shelves)
    {
        return Run(tree, cfg, null, rowCap, shelves);
    }

    // A run that takes the rows another run of the same tree and config chose, which lays out
    // exactly as that run did.
    public static LayoutResult Run(LayoutTree tree, LayoutConfig cfg, Func<string, double>? measureText, RowWrap.Choice rows)
    {
        return Run(tree, cfg, measureText, rows.RowCap, rows.Shelves);
    }

    // The row plan needs the measured widths and the bands, and its shelf offsets join Y before
    // the spread and the snap, so a wrapped row lands on the grid like any other node.
    private static LayoutResult Run(LayoutTree tree, LayoutConfig cfg, Func<string, double>? measureText, int? rowCap, int? shelves)
    {
        cfg.Validate();
        MeasureNodes(tree, cfg, measureText);
        IReadOnlyList<LayoutWrap> wraps = Array.Empty<LayoutWrap>();
        RowWrap.Choice? rows = null;
        if (cfg.Mode == LayoutMode.Spring)
        {
            // Force-directed sets both axes at once; the tidy tree and band passes do not run.
            SpringLayout.Assign(tree, cfg);
        }
        else
        {
            if (cfg.Mode == LayoutMode.GravityWell)
                WellLayout.Assign(tree, cfg);
            else
                BandLayout.AssignBands(tree, cfg);
            wraps = RowWrap.Plan(tree, cfg, rowCap, shelves, out RowWrap.Choice choice);
            rows = choice;
            TidyTree.AssignX(tree, cfg, wraps);
            foreach (LayoutNode node in tree.Nodes)
            {
                if (node.WrapDy != 0.0)
                    node.Y += node.WrapDy;
            }
        }
        Spread(tree, cfg);
        GridSnap.Snap(tree, cfg);
        EdgeRouter.Route(tree, cfg, wraps);
        LabelPlacer.Result labels = LabelPlacer.Place(tree, cfg, wraps);
        return BuildResult(tree, cfg, labels, wraps, rows);
    }

    // Stretch the pre-snap positions by LayoutConfig.SpreadX about the root. Scaling keeps
    // the order of every node, so the separations the X pass guaranteed only grow.
    private static void Spread(LayoutTree tree, LayoutConfig cfg)
    {
        double k = cfg.SpreadX;
        if (k <= 1.0)
            return;
        bool both = cfg.Mode == LayoutMode.Spring;
        double rx = tree.Root.X;
        double ry = tree.Root.Y;
        foreach (LayoutNode node in tree.Nodes)
        {
            node.X = rx + (node.X - rx) * k;
            if (both)
                node.Y = ry + (node.Y - ry) * k;
        }
    }

    // Node box and dot size. Width comes from real text metrics when measureText is
    // given, otherwise from a character-count estimate (the offline pass cannot reach
    // ImGui.CalcTextSize, which only exists inside the draw loop). The measured widths of
    // both labels are kept on the node, so the renderer never measures them again.
    private static void MeasureNodes(LayoutTree tree, LayoutConfig cfg, Func<string, double>? measureText)
    {
        foreach (LayoutNode node in tree.Nodes)
        {
            double rawWidth = Measure(node.Label, cfg, measureText);
            node.LabelTextW = rawWidth;
            node.ShortLabelTextW = node.ShortLabel.Length > 0 && node.ShortLabel != node.Label
                ? Measure(node.ShortLabel, cfg, measureText)
                : rawWidth;
            node.SummaryTextW = node.Summary.Length > 0 ? Measure(node.Summary, cfg, measureText) : 0.0;
            double textWidth = Math.Max(cfg.MinNodeWidthPx, rawWidth);
            node.Width = textWidth + cfg.BadgePaddingPx;
            node.Height = cfg.LineHeightPx;
            node.DotRadius = DotRadiusFor(node, cfg);
        }
    }

    private static double Measure(string text, LayoutConfig cfg, Func<string, double>? measureText)
    {
        return measureText != null ? measureText(text) : text.Length * cfg.CharWidthPx;
    }

    private static double DotRadiusFor(LayoutNode node, LayoutConfig cfg)
    {
        if (node.IsRoot)
            return cfg.RootDotRadius;
        if (node.IsYouAreHere)
            return cfg.YouAreHereDotRadius;
        if (node.Kind == LayoutKind.Hub)
            return cfg.HubDotRadius;
        return node.Rank switch
        {
            <= 1 => cfg.PlanetDotRadius,
            2 => cfg.MoonDotRadius,
            _ => cfg.MinorDotRadius
        };
    }

    // The bounds hold every dot, every placed label plate and the trunk of every wrapped fan.
    private static LayoutResult BuildResult(LayoutTree tree, LayoutConfig cfg, LabelPlacer.Result labels, IReadOnlyList<LayoutWrap> wraps, RowWrap.Choice? rows)
    {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        foreach (LayoutNode node in tree.Nodes)
        {
            double r = node.DotRadius;
            Extend(ref minX, ref minY, ref maxX, ref maxY, node.SnappedX - r, node.SnappedY - r);
            Extend(ref minX, ref minY, ref maxX, ref maxY, node.SnappedX + r, node.SnappedY + r);

            if (node.LabelPlaced)
            {
                Extend(ref minX, ref minY, ref maxX, ref maxY, node.LabelX, node.LabelY);
                Extend(ref minX, ref minY, ref maxX, ref maxY, node.LabelX + node.Width, node.LabelY + node.Height);
            }
        }
        foreach (LayoutWrap wrap in wraps)
        {
            foreach (LayoutPoint p in wrap.Trunk)
                Extend(ref minX, ref minY, ref maxX, ref maxY, p.X, p.Y);
        }

        return new LayoutResult
        {
            Tree = tree,
            Config = cfg,
            Labels = labels,
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            Wraps = wraps,
            Rows = rows
        };
    }

    private static void Extend(ref double minX, ref double minY, ref double maxX, ref double maxY, double x, double y)
    {
        if (x < minX) minX = x;
        if (y < minY) minY = y;
        if (x > maxX) maxX = x;
        if (y > maxY) maxY = y;
    }
}
