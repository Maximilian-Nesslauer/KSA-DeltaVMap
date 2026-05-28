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
    public static LayoutResult Run(LayoutTree tree, LayoutConfig cfg)
    {
        cfg.Validate();
        MeasureNodes(tree, cfg);
        BandLayout.AssignBands(tree, cfg);
        TidyTree.AssignX(tree, cfg);
        GridSnap.Snap(tree, cfg);
        EdgeRouter.Route(tree, cfg);
        LabelPlacer.Result labels = LabelPlacer.Place(tree, cfg);
        return BuildResult(tree, cfg, labels);
    }

    // Approximate node box and dot size. Real widths come from ImGui.CalcTextSize in
    // the draw loop later; here a character-count estimate is enough to verify the
    // topology and the overlap logic.
    private static void MeasureNodes(LayoutTree tree, LayoutConfig cfg)
    {
        foreach (LayoutNode node in tree.Nodes)
        {
            double textWidth = Math.Max(cfg.MinNodeWidthPx, node.Label.Length * cfg.CharWidthPx);
            node.Width = textWidth + cfg.BadgePaddingPx;
            node.Height = cfg.LineHeightPx;
            node.DotRadius = DotRadiusFor(node, cfg);
        }
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

    private static LayoutResult BuildResult(LayoutTree tree, LayoutConfig cfg, LabelPlacer.Result labels)
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

        return new LayoutResult
        {
            Tree = tree,
            Config = cfg,
            Labels = labels,
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY
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
