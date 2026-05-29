using System;
using System.Collections.Generic;
using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using DeltaVMap.Layout;
using DeltaVMap.Model;

namespace DeltaVMap.Render;

// Maps layout-space coordinates (pixels at 100% zoom) to absolute screen pixels.
// Positions scale with zoom and shift with pan; node dots, labels and line thickness
// stay a fixed screen size (so only the spacing zooms, the metro look does not
// balloon). Pan is stored in canvas-relative pixels (origin excluded). Hit-testing
// compares screen positions directly, so no inverse transform is needed here.
internal readonly struct CanvasTransform
{
    public readonly float2 Origin;
    public readonly double Zoom;
    public readonly double PanX;
    public readonly double PanY;
    public readonly double MinX;
    public readonly double MinY;

    public CanvasTransform(float2 origin, double zoom, double panX, double panY, double minX, double minY)
    {
        Origin = origin;
        Zoom = zoom;
        PanX = panX;
        PanY = panY;
        MinX = minX;
        MinY = minY;
    }

    public float2 ToScreen(double lx, double ly)
    {
        double sx = Origin.X + (lx - MinX) * Zoom + PanX;
        double sy = Origin.Y + (ly - MinY) * Zoom + PanY;
        return new float2((float)sx, (float)sy);
    }
}

// Draws the laid-out tree with ImGui DrawList primitives every frame the window is
// visible. Reads positions and routed polylines straight off the LayoutResult (the
// layout engine already produced overlap-free, octilinear geometry); this class only
// turns that into lines, symbols, badges and labels. The StateNode lookup gives each
// layout node its game body for the per-system color.
//
// When a route is selected, routeNodes holds every node Id on it: the route path is
// drawn heavy and white, everything else fades to a fifth of its opacity, so the eye
// follows the one line from "you are here" to the destination.
internal static class CanvasRenderer
{
    // Show badges and minor labels only when zoomed in enough that they do not clutter.
    private const double LabelMinZoom = 0.5;
    private const double BadgeMinZoom = 0.5;

    // How far off-route geometry fades when a route is highlighted.
    private const double OffRouteAlpha = 0.2;
    private const float RouteLineWidth = 5f;

    private static readonly byte4 HubBus = new byte4(122, 138, 152, 255);
    private static readonly byte4 BadgeBg = new byte4(16, 20, 28, 210);
    private static readonly byte4 BadgeText = new byte4(198, 208, 220, 255);
    private static readonly byte4 LabelText = new byte4(205, 214, 224, 255);
    private static readonly byte4 LabelShadow = new byte4(0, 0, 0, 200);
    private static readonly byte4 RootRing = new byte4(255, 210, 194, 255);
    private static readonly byte4 YouAreHereRing = new byte4(255, 210, 63, 255);
    private static readonly byte4 HoverRing = new byte4(255, 255, 255, 255);
    private static readonly byte4 RouteLine = new byte4(255, 255, 255, 255);

    public static void Draw(
        ImDrawListPtr dl,
        LayoutResult layout,
        IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette,
        in CanvasTransform t,
        string? hoverId,
        IReadOnlySet<string>? routeNodes)
    {
        DrawEdges(dl, layout, lookup, palette, in t, routeNodes);
        DrawNodes(dl, layout, lookup, palette, in t, hoverId, routeNodes);
    }

    private static void DrawEdges(
        ImDrawListPtr dl,
        LayoutResult layout,
        IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette,
        in CanvasTransform t,
        IReadOnlySet<string>? routeNodes)
    {
        bool showBadges = t.Zoom >= BadgeMinZoom;
        bool routing = routeNodes != null;

        // Base pass: every edge except the highlighted route (deferred so the heavy
        // white line draws on top). Off-route edges fade when a route is active.
        foreach (LayoutNode node in layout.Tree.Nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.Polyline.Count < 2)
                    continue;
                if (routing && OnRoute(edge, routeNodes!))
                    continue;

                (byte4 color, float width) = EdgeStyle(edge, lookup, palette);
                double alpha = routing ? OffRouteAlpha : 1.0;

                DrawPolyline(dl, edge, in t, Fade(color, alpha), width);
                if (showBadges && !edge.IsHubLink && edge.RouteDv >= 1.0)
                    DrawBadge(dl, edge, in t, alpha);
            }
        }

        if (!routing)
            return;

        // Route pass: the selected path, heavy and white, with bright badges.
        foreach (LayoutNode node in layout.Tree.Nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.Polyline.Count < 2 || !OnRoute(edge, routeNodes!))
                    continue;

                DrawPolyline(dl, edge, in t, RouteLine, RouteLineWidth);
                if (showBadges && !edge.IsHubLink && edge.RouteDv >= 1.0)
                    DrawBadge(dl, edge, in t, 1.0);
            }
        }
    }

    private static bool OnRoute(LayoutEdge edge, IReadOnlySet<string> routeNodes)
    {
        // In a tree, two route nodes are adjacent only if the edge between them is on
        // the path, so membership of both endpoints is enough.
        return routeNodes.Contains(edge.From.Id) && routeNodes.Contains(edge.To.Id);
    }

    private static void DrawPolyline(ImDrawListPtr dl, LayoutEdge edge, in CanvasTransform t, byte4 color, float width)
    {
        for (int i = 1; i < edge.Polyline.Count; i++)
        {
            float2 a = t.ToScreen(edge.Polyline[i - 1].X, edge.Polyline[i - 1].Y);
            float2 b = t.ToScreen(edge.Polyline[i].X, edge.Polyline[i].Y);
            dl.AddLine(in a, in b, color, width);
        }
    }

    private static (byte4 Color, float Width) EdgeStyle(
        LayoutEdge edge,
        IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette)
    {
        if (edge.IsHubLink)
            return (HubBus, 3.5f);

        // Color the line by the body it leads to, so a transfer reads in the
        // destination's system color and a ladder edge in its own body's color.
        byte4 color = BodyColor(edge.To, lookup, palette);
        float width = edge.Class == EdgeClass.Transfer ? 2.2f : 1.6f;
        return (color, width);
    }

    private static void DrawBadge(ImDrawListPtr dl, LayoutEdge edge, in CanvasTransform t, double alpha)
    {
        // Sit the dV at the middle of the final vertical drop, which is the lane that
        // carries the metric (the horizontal traverse is only the connector).
        int n = edge.Polyline.Count;
        LayoutPoint p0 = edge.Polyline[n - 2];
        LayoutPoint p1 = edge.Polyline[n - 1];
        float2 mid = t.ToScreen((p0.X + p1.X) / 2.0, (p0.Y + p1.Y) / 2.0);

        // RouteDv is the real per-leg burn: a transfer's Oberth-coupled depart+capture,
        // a ladder edge's exact cost. Every badge carries a leading "~": the whole map is
        // a closed-form patched-conic estimate, so no figure is exact. An Ascent edge on an
        // atmospheric body shows both directions, "^" ascent over "v" descent (landing is
        // cheaper there); ASCII arrows because the source is ASCII-only.
        string text;
        if (edge.DescentDv > 1.0 && Math.Abs(edge.DescentDv - edge.RouteDv) > 1.0)
        {
            string up = Math.Round(edge.RouteDv).ToString("0", CultureInfo.InvariantCulture);
            string down = Math.Round(edge.DescentDv).ToString("0", CultureInfo.InvariantCulture);
            text = "^~" + up + " v~" + down;
        }
        else
        {
            text = "~" + Math.Round(edge.RouteDv).ToString("0", CultureInfo.InvariantCulture);
        }
        float2 size = ImGui.CalcTextSize(text);
        float2 pad = new float2(3f, 1f);
        float2 min = mid + new float2(6f, (0f - size.Y) * 0.5f) - pad;
        float2 max = min + size + pad + pad;
        dl.AddRectFilled(in min, in max, Fade(BadgeBg, alpha), 3f);
        float2 textPos = min + pad;
        dl.AddText(in textPos, Fade(BadgeText, alpha), text);
    }

    private static void DrawNodes(
        ImDrawListPtr dl,
        LayoutResult layout,
        IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette,
        in CanvasTransform t,
        string? hoverId,
        IReadOnlySet<string>? routeNodes)
    {
        bool showMinorLabels = t.Zoom >= LabelMinZoom;
        bool routing = routeNodes != null;

        foreach (LayoutNode node in layout.Tree.Nodes)
        {
            float2 p = t.ToScreen(node.SnappedX, node.SnappedY);
            float r = (float)node.DotRadius;
            bool onRoute = !routing || routeNodes!.Contains(node.Id);
            double alpha = onRoute ? 1.0 : OffRouteAlpha;

            byte4 baseColor = BodyColor(node, lookup, palette);
            byte4 fill = Fade(baseColor, alpha);
            byte4 stroke = Fade(Lighten(baseColor, 0.45), alpha);

            DrawSymbol(dl, node, p, r, fill, stroke);

            // Orientation rings stay full strength: the root and "you are here" anchor
            // the map, the hover ring follows the cursor, regardless of the route fade.
            if (node.IsRoot)
                dl.AddCircle(in p, r + 4f, RootRing, 24, 2.5f);
            if (node.IsYouAreHere)
                dl.AddCircle(in p, r + 4f, YouAreHereRing, 24, 2.5f);
            if (node.Id == hoverId)
                dl.AddCircle(in p, r + 6f, HoverRing, 28, 2.5f);

            if (node.LabelPlaced && (showMinorLabels || IsMajor(node)))
                DrawLabel(dl, node, in t, alpha);
        }
    }

    // The node shape carries the state kind; the fill carries the planetary system.
    private static void DrawSymbol(ImDrawListPtr dl, LayoutNode node, float2 p, float r, byte4 fill, byte4 stroke)
    {
        switch (node.Kind)
        {
            case LayoutKind.Intercept:
                // A loose capture point: a hollow target ring rather than a solid body,
                // distinct from every filled rung and from the gas-giant ring.
                dl.AddCircle(in p, r, fill, 24, 2.5f);
                dl.AddCircleFilled(in p, Math.Max(2f, r * 0.4f), fill);
                break;
            case LayoutKind.Hub:
                dl.AddCircleFilled(in p, r, fill);
                dl.AddCircle(in p, r, stroke, 20, 1.5f);
                break;
            default:
                dl.AddCircleFilled(in p, r, fill);
                dl.AddCircle(in p, r, stroke, 20, 1.5f);
                break;
        }
    }

    private static void DrawLabel(ImDrawListPtr dl, LayoutNode node, in CanvasTransform t, double alpha)
    {
        float2 p = t.ToScreen(node.LabelX, node.LabelY);
        float2 shadow = p + new float2(1f, 1f);
        dl.AddText(in shadow, Fade(LabelShadow, alpha), node.Label);
        dl.AddText(in p, Fade(LabelText, alpha), node.Label);
    }

    private static bool IsMajor(LayoutNode node)
    {
        return node.IsRoot || node.IsYouAreHere || node.Kind == LayoutKind.Hub || node.Rank <= 1;
    }

    private static byte4 BodyColor(LayoutNode node, IReadOnlyDictionary<string, StateNode> lookup, ColorPalette palette)
    {
        if (lookup.TryGetValue(node.Id, out StateNode? state))
            return palette.ColorFor(state.Body);
        return palette.ColorFor(node.Id);
    }

    private static byte4 Lighten(byte4 c, double f)
    {
        return new byte4(LightenChannel(c.X, f), LightenChannel(c.Y, f), LightenChannel(c.Z, f), c.W);
    }

    private static byte LightenChannel(byte v, double f)
    {
        return (byte)Math.Clamp((int)Math.Round(v + (255.0 - v) * f), 0, 255);
    }

    // Scale a color's alpha, used to fade everything that is not on the selected route.
    private static byte4 Fade(byte4 c, double a)
    {
        if (a >= 1.0)
            return c;
        return new byte4(c.X, c.Y, c.Z, (byte)Math.Clamp((int)Math.Round(c.W * a), 0, 255));
    }
}
