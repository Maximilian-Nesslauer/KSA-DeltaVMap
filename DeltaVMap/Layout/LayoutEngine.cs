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
    // measureText, when supplied, returns the real rendered width of a label in pixels
    // (from ImGui.CalcTextSize). The map passes it so the tidy tree spaces nodes by
    // their true on-screen size; the offline dump passes null and falls back to the
    // character-count estimate, which is enough to verify topology and overlap logic.
    public static LayoutResult Run(LayoutTree tree, LayoutConfig cfg, Func<string, double>? measureText = null)
    {
        cfg.Validate();
        MeasureNodes(tree, cfg, measureText);
        BandLayout.AssignBands(tree, cfg);
        TidyTree.AssignX(tree, cfg);
        GridSnap.Snap(tree, cfg);
        EdgeRouter.Route(tree, cfg);
        LabelPlacer.Result labels = LabelPlacer.Place(tree, cfg);
        return BuildResult(tree, cfg, labels);
    }

    // Node box and dot size. Width comes from real text metrics when measureText is
    // given, otherwise from a character-count estimate (the offline pass cannot reach
    // ImGui.CalcTextSize, which only exists inside the draw loop).
    private static void MeasureNodes(LayoutTree tree, LayoutConfig cfg, Func<string, double>? measureText)
    {
        foreach (LayoutNode node in tree.Nodes)
        {
            double rawWidth = measureText != null ? measureText(node.Label) : node.Label.Length * cfg.CharWidthPx;
            double textWidth = Math.Max(cfg.MinNodeWidthPx, rawWidth);
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
