using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// The fit-to-view zoom and pan of a scene on a canvas, shared by the map window and the
// offline fit preview. Positions scale with the zoom while dots and text keep a fixed screen
// size, so with other star systems shown the fit also keeps the screen-fixed labels and system
// titles at the right edge, and the system glyphs and titles at the bottom edge, inside the
// canvas. When the plain fit would put the root or the title of another system under the
// transfer-window footer at the bottom left, the fit keeps the footer's height free below the
// whole scene instead. With the ego map alone it is the plain bounds fit.
internal static class SceneFit
{
    public const double PadPx = 32.0;

    // The zoom floor of the map window. A map with hundreds of moons fits only far below the zoom
    // of a stock map, so the fit preview judges the canvas at this floor too.
    public const double MinZoom = 0.02;

    // The collapsed transfer-window footer the canvas draws at its bottom left.
    public const double FooterW = 480.0;
    public const double FooterH = 56.0;

    public static double Fit(LayoutScene scene, double canvasW, double canvasH, double minZoom, double maxZoom, out double panX, out double panY)
    {
        double zoom = Fit(scene, canvasW, canvasH, minZoom, maxZoom, 0.0, out panX, out panY);
        if (UnderFooter(scene, zoom, panX, panY, canvasH))
            zoom = Fit(scene, canvasW, canvasH, minZoom, maxZoom, FooterH, out panX, out panY);
        return zoom;
    }

    // Whether the glyph, ring or title block of a destination root reaches into the footer.
    private static bool UnderFooter(LayoutScene scene, double zoom, double panX, double panY, double canvasH)
    {
        foreach (LayoutPart part in scene.Parts)
        {
            LayoutNode root = part.Root;
            double x = panX + (root.SnappedX - scene.MinX) * zoom;
            double y = panY + (root.SnappedY - scene.MinY) * zoom;
            double left = x - SceneComposer.GlyphRadiusPx(root) - SceneComposer.SystemRingPx;
            if (left < FooterW && y + DownReachPx(root) > canvasH - FooterH)
                return true;
        }
        return false;
    }

    private static double Fit(LayoutScene scene, double canvasW, double canvasH, double minZoom, double maxZoom, double footer, out double panX, out double panY)
    {
        double contentW = Math.Max(1.0, scene.Width);
        double contentH = Math.Max(1.0, scene.Height);
        bool overhang = scene.Parts.Count > 0;
        double usableW = canvasW - 2.0 * PadPx;
        double usableH = canvasH - 2.0 * PadPx - footer;
        double zoom = Math.Min(usableW / contentW, usableH / contentH);
        IReadOnlyList<LayoutNode> nodes = scene.Nodes;
        if (overhang)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                double reach = TextReachPx(nodes[i]);
                double dx = nodes[i].SnappedX - scene.MinX;
                if (reach > 0.0 && dx > 0.0 && dx * zoom + reach > usableW)
                    zoom = (usableW - reach) / dx;
                double down = DownReachPx(nodes[i]);
                double dy = nodes[i].SnappedY - scene.MinY;
                if (down > 0.0 && dy > 0.0 && dy * zoom + down > usableH)
                    zoom = (usableH - down) / dy;
            }
        }
        zoom = Math.Clamp(zoom, minZoom, maxZoom);

        double right = contentW * zoom;
        double bottom = contentH * zoom;
        if (overhang)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                right = Math.Max(right, (nodes[i].SnappedX - scene.MinX) * zoom + TextReachPx(nodes[i]));
                bottom = Math.Max(bottom, (nodes[i].SnappedY - scene.MinY) * zoom + DownReachPx(nodes[i]));
            }
        }
        panX = (canvasW - right) / 2.0;
        panY = (canvasH - footer - bottom) / 2.0;
        return zoom;
    }

    // How far below a system root's center its glyph, fan and title block reach on screen.
    public static double DownReachPx(LayoutNode node)
    {
        if (!node.IsSystemRoot)
            return 0.0;
        double glyph = SceneComposer.GlyphRadiusPx(node) + (node.IsSystemStub ? SceneComposer.StubFanPx : SceneComposer.SystemRingPx);
        double title = -0.5 * SceneComposer.TitleLinePx + SceneComposer.TitleHeightPx(node) + SceneComposer.TitlePadY;
        return Math.Max(glyph, title) + 2.0;
    }

    // How far right of a node's center its screen-fixed text can reach: a system title, or the
    // short overview label every other node shows at a fit zoom.
    public static double TextReachPx(LayoutNode node)
    {
        if (node.IsSystemRoot)
            return SceneComposer.GlyphRadiusPx(node) + SceneComposer.TitleGapPx + SceneComposer.TitleTextW(node) + SceneComposer.TitlePadX + 1.0;
        if (!node.LabelPlaced)
            return 0.0;
        return node.DotRadius + 8.0 + node.ShortLabelTextW;
    }
}

// A screen-space imitation of the canvas at fit zoom, for the offline checks and the fit
// preview SVG: positions scaled by the fit zoom, dots and text at a fixed size, the label LOD
// and priorities of LabelLod with the greedy culling of the renderer, and the system titles
// placed first in the spots of SystemTitleSpots, clear of the connector lines. A tree without
// short labels gets the rung cut off a label for the overview text the game would show.
internal sealed class FitPreview
{
    public const double LinePx = SceneComposer.TitleLinePx;
    private const double PlatePadX = SceneComposer.TitlePadX;
    private const double PlatePadY = SceneComposer.TitlePadY;

    public double CanvasW { get; private init; }
    public double CanvasH { get; private init; }
    public double Zoom { get; private init; }
    public double PanX { get; private init; }
    public double PanY { get; private init; }
    public double MinX { get; private init; }
    public double MinY { get; private init; }
    public List<PreviewText> Texts { get; } = new();

    public (double X, double Y) ToScreen(double x, double y)
    {
        return (PanX + (x - MinX) * Zoom, PanY + (y - MinY) * Zoom);
    }

    public static FitPreview Build(LayoutScene scene, double canvasW, double canvasH)
    {
        double zoom = SceneFit.Fit(scene, canvasW, canvasH, SceneFit.MinZoom, 4.0, out double panX, out double panY);
        var preview = new FitPreview { CanvasW = canvasW, CanvasH = canvasH, Zoom = zoom, PanX = panX, PanY = panY, MinX = scene.MinX, MinY = scene.MinY };
        preview.PlaceTexts(scene);
        return preview;
    }

    private void PlaceTexts(LayoutScene scene)
    {
        IReadOnlyList<LayoutNode> nodes = scene.Nodes;
        var dots = new List<PreviewRect>(nodes.Count);
        foreach (LayoutNode node in nodes)
        {
            (double x, double y) = ToScreen(node.SnappedX, node.SnappedY);
            double r = SceneComposer.GlyphRadiusPx(node);
            dots.Add(new PreviewRect(x - r, y - r, x + r, y + r));
        }
        var occupied = new List<PreviewRect>();

        // System titles first; when every spot is taken the title still draws right of its
        // root, because a system must never lose its name.
        var spots = new (double X, double Y)[SceneComposer.TitleSpotCount];
        double originX = PanX - MinX * Zoom;
        double originY = PanY - MinY * Zoom;
        foreach (LayoutNode node in nodes)
        {
            if (!node.IsSystemRoot)
                continue;
            (double x, double y) = ToScreen(node.SnappedX, node.SnappedY);
            double w = SceneComposer.TitleTextW(node) + 2.0 * PlatePadX;
            double h = SceneComposer.TitleHeightPx(node) + 2.0 * PlatePadY;
            SceneComposer.SystemTitleSpots(node, x, y, w, h, LinePx, spots);
            PreviewRect chosen = new PreviewRect(spots[0].X, spots[0].Y, spots[0].X + w, spots[0].Y + h);
            for (int s = 0; s < spots.Length; s++)
            {
                var rect = new PreviewRect(spots[s].X, spots[s].Y, spots[s].X + w, spots[s].Y + h);
                bool onCanvas = rect.X0 >= 0 && rect.Y0 >= 0 && rect.X1 <= CanvasW && rect.Y1 <= CanvasH;
                if (!onCanvas || AnyOverlap(occupied, rect) || AnyOverlapExcept(dots, rect, node.Index)
                    || SceneComposer.ConnectorsHitScreenRect(scene, Zoom, originX, originY, rect.X0, rect.Y0, rect.X1, rect.Y1))
                    continue;
                chosen = rect;
                break;
            }
            occupied.Add(chosen);
            Texts.Add(new PreviewText(node, chosen, true));
        }

        // A label never covers the glyph of another system's root.
        foreach (LayoutNode node in nodes)
        {
            if (node.IsSystemRoot && node.Index >= scene.Ego.Tree.Nodes.Count)
                occupied.Add(dots[node.Index]);
        }

        // Then every other label by priority, with the label LOD of the renderer. A body whose
        // label is special (the root, you-are-here, a system title) needs no short label.
        bool shortLabels = Zoom < LabelLod.FullLabelMinZoom;
        Dictionary<string, LayoutNode> reps = shortLabels ? LabelLod.ShortLabelReps(nodes, Zoom) : new Dictionary<string, LayoutNode>();
        var candidates = new List<(int Priority, LayoutNode Node, PreviewRect Rect, bool Short)>();
        foreach (LayoutNode node in nodes)
        {
            if (!node.LabelPlaced || node.IsSystemRoot)
                continue;
            bool useShort = false;
            if (!LabelLod.IsSpecial(node, null, false, null))
            {
                if (!LabelLod.RankShows(node, Zoom))
                    continue;
                if (shortLabels)
                {
                    string key = LabelLod.BodyKey(node);
                    if (!reps.TryGetValue(key, out LayoutNode? rep) || !ReferenceEquals(rep, node))
                        continue;
                    useShort = true;
                }
            }
            (double lx, double ly) = ToScreen(node.LabelX, node.LabelY);
            double w = useShort ? ShortWidth(node) : node.LabelTextW;
            var rect = new PreviewRect(lx - PlatePadX, ly - PlatePadY, lx + w + PlatePadX, ly + LinePx + PlatePadY);
            candidates.Add((LabelLod.Priority(node, null, false, null), node, rect, useShort));
        }
        candidates.Sort((a, b) =>
        {
            int c = a.Priority.CompareTo(b.Priority);
            if (c != 0)
                return c;
            c = a.Rect.Y0.CompareTo(b.Rect.Y0);
            return c != 0 ? c : a.Rect.X0.CompareTo(b.Rect.X0);
        });
        foreach ((int _, LayoutNode node, PreviewRect rect, bool useShort) in candidates)
        {
            if (rect.X1 < 0 || rect.X0 > CanvasW || rect.Y1 < 0 || rect.Y0 > CanvasH || AnyOverlap(occupied, rect))
                continue;
            occupied.Add(rect);
            Texts.Add(new PreviewText(node, rect, false) { Short = useShort });
        }
    }

    private static readonly string[] Rungs = { " Low Orbit", " Parking Orbit", " Orbit", " Surface", " Stationary", " SOI Edge", " Intercept", " Hub" };

    public static string ShortText(LayoutNode node)
    {
        if (node.ShortLabel.Length > 0)
            return node.ShortLabel;
        foreach (string rung in Rungs)
        {
            if (node.Label.EndsWith(rung, StringComparison.Ordinal))
                return node.Label.Substring(0, node.Label.Length - rung.Length);
        }
        return node.Label;
    }

    private static double ShortWidth(LayoutNode node)
    {
        if (node.ShortLabel.Length > 0)
            return node.ShortLabelTextW;
        return node.Label.Length == 0 ? 0.0 : node.LabelTextW * ShortText(node).Length / node.Label.Length;
    }

    private static bool AnyOverlap(List<PreviewRect> rects, PreviewRect r)
    {
        foreach (PreviewRect o in rects)
        {
            if (r.Intersects(o))
                return true;
        }
        return false;
    }

    private static bool AnyOverlapExcept(List<PreviewRect> rects, PreviewRect r, int except)
    {
        for (int i = 0; i < rects.Count; i++)
        {
            if (i != except && r.Intersects(rects[i]))
                return true;
        }
        return false;
    }
}

internal readonly record struct PreviewRect(double X0, double Y0, double X1, double Y1)
{
    public bool Intersects(PreviewRect o) => X0 < o.X1 && X1 > o.X0 && Y0 < o.Y1 && Y1 > o.Y0;
}

// One drawn text of a fit preview: a system title (with its summary line) or a node label.
internal sealed record PreviewText(LayoutNode Node, PreviewRect Rect, bool Title)
{
    public bool Short { get; init; }
}
