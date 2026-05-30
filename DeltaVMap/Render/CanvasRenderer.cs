using System;
using System.Collections.Generic;
using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using DeltaVMap.Dv;
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
// Four passes per frame:
//  1. Edge polylines (off-route faded, then the route heavy and white on top).
//  2. Node dots, their state-kind glyphs, and the atmosphere/ring node markers.
//  3. Edge markers: aerobrake triangles, plus plane-change numbers when that toggle is on.
//  4. Labels and dV badges through a screen-space culling pass: the layout placement is
//     overlap-free at 100% zoom, but text renders at a fixed screen size while positions
//     scale with zoom, so below 100% (and the default view is auto-fit, well below it)
//     labels and badges would smear. The pass draws the highest-priority text first
//     (hovered, root, you-are-here, on-route, then body names by rank, then dV badges) and
//     skips anything that would cover an already-drawn label, badge or a foreign node dot
//     at the current zoom; zooming in spreads the anchors apart and reveals more. Names are
//     ranked ahead of plain dV badges, so the map reads as a labelled diagram first. A
//     label is never culled by its own dot (only foreign dots), or zooming out far enough
//     that the fixed-size dot swallows the scaled-in label gap would hide every name.
internal static class CanvasRenderer
{
    // How far off-route geometry fades when a route is highlighted.
    private const double OffRouteAlpha = 0.2;
    private const float RouteLineWidth = 5f;

    // Below this zoom the whole system is squeezed onto the screen and the dV badges only
    // smear into noise, so they are hidden entirely; names still show (governed by the
    // culling). Zooming past it brings the numbers back.
    private const double BadgeMinZoom = 0.45;

    // Plane-change numbers below this are noise (a near-coplanar leg), so they are not
    // drawn even when the toggle is on. Matches the calculator's own half-degree floor in
    // spirit: tiny inclinations cost almost nothing.
    private const double PlaneChangeMinDv = 10.0;

    // Badge box padding and, for the dual ascent/descent badge, the little filled
    // direction triangles drawn instead of "^"/"v" text: triangle width and height (kept
    // wider than tall so they read as arrowheads, not stretched slivers), the
    // triangle-to-number gap, and the gap between the ascent group and the descent group.
    private const float BadgePadX = 3f;
    private const float BadgePadY = 1f;
    private const float ArrowW = 9f;
    private const float ArrowH = 7f;
    private const float ArrowGap = 3f;
    private const float DualSegGap = 8f;

    private static readonly byte4 HubBus = new byte4(122, 138, 152, 255);
    private static readonly byte4 BadgeBg = new byte4(16, 20, 28, 210);
    private static readonly byte4 BadgeTextColor = new byte4(198, 208, 220, 255);
    private static readonly byte4 LabelText = new byte4(205, 214, 224, 255);
    private static readonly byte4 LabelShadow = new byte4(0, 0, 0, 200);
    private static readonly byte4 RootRing = new byte4(255, 210, 194, 255);
    private static readonly byte4 RootHalo = new byte4(255, 170, 120, 60);
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
        IReadOnlySet<string>? routeNodes,
        bool showPlaneChange)
    {
        DrawEdgeLines(dl, layout, lookup, palette, in t, routeNodes);
        DrawNodeDots(dl, layout, lookup, palette, in t, hoverId, routeNodes);
        DrawEdgeMarkers(dl, layout, lookup, palette, in t, routeNodes, showPlaneChange);
        DrawLabelsAndBadges(dl, layout, lookup, palette, in t, hoverId, routeNodes);
    }

    private static void DrawEdgeLines(
        ImDrawListPtr dl,
        LayoutResult layout,
        IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette,
        in CanvasTransform t,
        IReadOnlySet<string>? routeNodes)
    {
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
            }
        }

        if (!routing)
            return;

        // Route pass: the selected path, heavy and white.
        foreach (LayoutNode node in layout.Tree.Nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.Polyline.Count < 2 || !OnRoute(edge, routeNodes!))
                    continue;
                DrawPolyline(dl, edge, in t, RouteLine, RouteLineWidth);
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

    private static void DrawNodeDots(
        ImDrawListPtr dl,
        LayoutResult layout,
        IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette,
        in CanvasTransform t,
        string? hoverId,
        IReadOnlySet<string>? routeNodes)
    {
        bool routing = routeNodes != null;

        foreach (LayoutNode node in layout.Tree.Nodes)
        {
            float2 p = t.ToScreen(node.SnappedX, node.SnappedY);
            float r = (float)node.DotRadius;
            bool onRoute = !routing || routeNodes!.Contains(node.Id);
            double alpha = onRoute ? 1.0 : OffRouteAlpha;

            // Resolve the node's game body once: it supplies the system color and the
            // body-property markers (a usable atmosphere -> jet halo, a ring system -> ring
            // ellipse). Hubs resolve too (they back a real body), but the markers below skip
            // them via the kind check; only a purely synthetic node misses the lookup.
            lookup.TryGetValue(node.Id, out StateNode? state);
            byte4 baseColor = state != null ? palette.ColorFor(state.Body) : palette.ColorFor(node.Id);
            byte4 fill = Fade(baseColor, alpha);
            byte4 stroke = Fade(Lighten(baseColor, 0.45), alpha);

            // The atmosphere/ring markers belong on a body's in-atmosphere rungs (the ones
            // you can fly a jet at or see the rings from), not on its high orbits or its
            // hub bus, so they key off the surface and low-orbit glyphs.
            bool bodyNode = node.Kind == LayoutKind.Surface || node.Kind == LayoutKind.LowOrbit;
            bool atmoBody = bodyNode && state != null && OrbitalStates.HasUsableAtmosphere(state.Body);
            bool ringedBody = bodyNode && state != null && OrbitalStates.HasRings(state.Body);

            // A soft filled halo behind the root so the ego anchor pops out of a dense
            // cluster even when zoomed out. Drawn before the dot so it sits underneath.
            if (node.IsRoot)
                dl.AddCircleFilled(in p, r + 10f, RootHalo);

            // Rings sit behind the body disc, like a planet seen against its ring plane.
            if (ringedBody)
                NodeGlyphs.RingEllipse(dl, p, r, stroke);

            DrawSymbol(dl, node, p, r, fill, stroke);

            // The bold jet/atmosphere halo wraps the glyph from just outside it.
            if (atmoBody)
                NodeGlyphs.AtmosphereHalo(dl, p, r, stroke);

            // Orientation rings stay full strength: the root and "you are here" anchor
            // the map, the hover ring follows the cursor, regardless of the route fade.
            if (node.IsRoot)
                dl.AddCircle(in p, r + 4f, RootRing, 24, 2.5f);
            if (node.IsYouAreHere)
                dl.AddCircle(in p, r + 4f, YouAreHereRing, 24, 2.5f);
            if (node.Id == hoverId)
                dl.AddCircle(in p, r + 6f, HoverRing, 28, 2.5f);
        }
    }

    // The node shape carries the state kind (the KSP concentric-ring vocabulary); the fill
    // carries the planetary system color, the stroke a lightened accent of it.
    private static void DrawSymbol(ImDrawListPtr dl, LayoutNode node, float2 p, float r, byte4 fill, byte4 stroke)
    {
        switch (node.Kind)
        {
            case LayoutKind.Surface:
                NodeGlyphs.Surface(dl, p, r, fill, stroke);
                break;
            case LayoutKind.LowOrbit:
                NodeGlyphs.LowOrbit(dl, p, r, fill, stroke);
                break;
            case LayoutKind.Stationary:
                NodeGlyphs.Stationary(dl, p, r, fill, stroke);
                break;
            case LayoutKind.SoiEdge:
                NodeGlyphs.SoiEdge(dl, p, r, fill, stroke);
                break;
            case LayoutKind.Intercept:
                NodeGlyphs.Intercept(dl, p, r, fill, stroke);
                break;
            case LayoutKind.Hub:
                NodeGlyphs.Hub(dl, p, r, fill, stroke);
                break;
            default:
                // YouAreHere and any future kind: a solid disc (the yellow ring above
                // distinguishes the "you are here" anchor).
                NodeGlyphs.Solid(dl, p, r, fill, stroke);
                break;
        }
    }

    // Edge-borne markers drawn over the lines and dots: the aerobrake-possible triangle on
    // any capture into an atmospheric body (a static capability cue, shown regardless of the
    // aerobrake toggle), and the sibling-transfer plane-change number, shown only when the
    // plane-change toggle is on. Both fade with the off-route dim so a selected route stays
    // legible. They are gated by the same zoom floor as the dV badges, so the zoomed-out
    // auto-fit stays a clean diagram of glyphs and names; zooming in reveals them. There are
    // few of either, so neither joins the label/badge culling pass.
    private static void DrawEdgeMarkers(
        ImDrawListPtr dl,
        LayoutResult layout,
        IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette,
        in CanvasTransform t,
        IReadOnlySet<string>? routeNodes,
        bool showPlaneChange)
    {
        if (t.Zoom < BadgeMinZoom)
            return;

        bool routing = routeNodes != null;
        foreach (LayoutNode node in layout.Tree.Nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.Polyline.Count < 2)
                    continue;
                bool onRoute = !routing || OnRoute(edge, routeNodes!);
                double alpha = onRoute ? 1.0 : OffRouteAlpha;

                if (edge.Aerobrake)
                    DrawAerobrake(dl, edge, lookup, palette, in t, alpha);

                if (showPlaneChange && edge.Class == EdgeClass.Transfer && edge.PlaneChangeDv >= PlaneChangeMinDv)
                    DrawPlaneChange(dl, edge, in t, alpha);
            }
        }
    }

    // A filled triangle partway along the capture edge, pointing the way the capture runs
    // (from the loose ellipse down into low orbit), in the destination's lightened color.
    private static void DrawAerobrake(
        ImDrawListPtr dl, LayoutEdge edge, IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette, in CanvasTransform t, double alpha)
    {
        IReadOnlyList<LayoutPoint> pts = edge.Polyline;
        float2 a = t.ToScreen(pts[0].X, pts[0].Y);
        float2 b = t.ToScreen(pts[pts.Count - 1].X, pts[pts.Count - 1].Y);
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f)
            return;
        dx /= len;
        dy /= len;
        var at = new float2(a.X + (b.X - a.X) * 0.45f, a.Y + (b.Y - a.Y) * 0.45f);
        byte4 color = Fade(Lighten(BodyColor(edge.To, lookup, palette), 0.4), alpha);
        NodeGlyphs.AerobrakeTriangle(dl, at, dx, dy, 8f, color);
    }

    // The plane-change figure near the transfer's arrival node, offset up and to the right
    // to sit clear of the dV badge. Drawn as a plain number (DrawList text cannot rotate, so
    // there is no literal KSP slant); its warm color and the legend entry key its meaning.
    private static void DrawPlaneChange(ImDrawListPtr dl, LayoutEdge edge, in CanvasTransform t, double alpha)
    {
        float2 to = t.ToScreen(edge.To.SnappedX, edge.To.SnappedY);
        var pos = to + new float2(8f, -(float)edge.To.DotRadius - 16f);
        string text = "i ~" + Math.Round(edge.PlaneChangeDv).ToString("0", CultureInfo.InvariantCulture);
        var shadow = pos + new float2(1f, 1f);
        dl.AddText(in shadow, Fade(LabelShadow, alpha), text);
        dl.AddText(in pos, Fade(NodeGlyphs.PlaneChangeColor, alpha), text);
    }

    // The screen-space label + badge culling pass (see the class summary). Builds a
    // candidate for every placed label and (above a zoom floor) every dV badge, sorts them
    // by priority, then draws greedily, skipping any whose screen box overlaps one already
    // drawn. Names are NOT blocked by node dots (only by other text), so the highest-rank
    // names - the root first - show even at the zoomed-out auto-fit view; a name resting on
    // a dot is fine and beats hiding it. dV badges are hidden entirely below BadgeMinZoom
    // (they only clutter when the whole system is squeezed onto the screen) and avoid dots
    // when shown. Zooming in spreads the anchors apart and reveals more of both.
    private static void DrawLabelsAndBadges(
        ImDrawListPtr dl,
        LayoutResult layout,
        IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette,
        in CanvasTransform t,
        string? hoverId,
        IReadOnlySet<string>? routeNodes)
    {
        bool routing = routeNodes != null;
        bool showBadges = t.Zoom >= BadgeMinZoom;
        LayoutMode mode = layout.Config.Mode;
        var items = new List<DrawItem>();

        foreach (LayoutNode node in layout.Tree.Nodes)
        {
            if (node.LabelPlaced)
                items.Add(BuildLabelItem(node, in t, hoverId, routing, routeNodes));

            if (!showBadges)
                continue;
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.IsHubLink || edge.RouteDv < 1.0 || edge.Polyline.Count < 2)
                    continue;
                items.Add(BuildBadgeItem(edge, in t, mode, routing, routeNodes));
            }
        }

        // Highest priority (smallest number) first; a stable screen-position tiebreak
        // keeps the survivor set from flickering between frames at equal priority.
        items.Sort(static (a, b) =>
        {
            int byPriority = a.Priority.CompareTo(b.Priority);
            if (byPriority != 0)
                return byPriority;
            int byY = a.Rect.Y.CompareTo(b.Rect.Y);
            return byY != 0 ? byY : a.Rect.X.CompareTo(b.Rect.X);
        });

        // A badge must not cover a node dot; labels may (they identify the very dots they
        // sit near). So only badges test against this dot hash.
        var dots = new ScreenHash(48.0);
        foreach (LayoutNode node in layout.Tree.Nodes)
        {
            float2 p = t.ToScreen(node.SnappedX, node.SnappedY);
            float r = (float)node.DotRadius;
            dots.Insert(new ScreenRect(p.X - r, p.Y - r, 2f * r, 2f * r));
        }

        var occupied = new ScreenHash(48.0);
        foreach (DrawItem item in items)
        {
            if (occupied.AnyOverlap(item.Rect))
                continue;
            if (item.IsBadge && dots.AnyOverlap(item.Rect))
                continue;
            occupied.Insert(item.Rect);
            if (item.IsBadge)
                DrawBadgeItem(dl, item);
            else
                DrawLabelItem(dl, item);
        }
    }

    private static DrawItem BuildLabelItem(
        LayoutNode node, in CanvasTransform t, string? hoverId, bool routing, IReadOnlySet<string>? routeNodes)
    {
        float2 pos = t.ToScreen(node.LabelX, node.LabelY);
        float2 size = ImGui.CalcTextSize(node.Label);
        bool onRoute = !routing || routeNodes!.Contains(node.Id);

        return new DrawItem
        {
            Priority = LabelPriority(node, hoverId, routing, routeNodes),
            Rect = new ScreenRect(pos.X, pos.Y, size.X, size.Y),
            IsBadge = false,
            Text = node.Label,
            TextPos = pos,
            Alpha = onRoute ? 1.0 : OffRouteAlpha
        };
    }

    private static DrawItem BuildBadgeItem(
        LayoutEdge edge, in CanvasTransform t, LayoutMode mode, bool routing, IReadOnlySet<string>? routeNodes)
    {
        float2 anchor = BadgeAnchorScreen(edge, in t, mode);
        bool onRoute = routing && OnRoute(edge, routeNodes!);
        bool offRoute = routing && !onRoute;

        // An Ascent edge on an atmospheric body shows both directions: an up triangle with
        // the ascent dV, then a down triangle with the cheaper descent dV. Drawn as filled
        // triangles (DrawBadgeItem) rather than text arrows, so they read as real arrows
        // while the source stays ASCII. Every other edge shows a single "~dV".
        bool dual = edge.DescentDv > 1.0 && Math.Abs(edge.DescentDv - edge.RouteDv) > 1.0;
        string asc = "~" + Math.Round(edge.RouteDv).ToString("0", CultureInfo.InvariantCulture);
        string desc = "~" + Math.Round(edge.DescentDv).ToString("0", CultureInfo.InvariantCulture);

        float contentW;
        float contentH;
        if (dual)
        {
            float2 sa = ImGui.CalcTextSize(asc);
            float2 sd = ImGui.CalcTextSize(desc);
            contentW = ArrowW + ArrowGap + sa.X + DualSegGap + ArrowW + ArrowGap + sd.X;
            contentH = Math.Max(sa.Y, sd.Y);
        }
        else
        {
            float2 s = ImGui.CalcTextSize(asc);
            contentW = s.X;
            contentH = s.Y;
        }

        var pad = new float2(BadgePadX, BadgePadY);
        float2 contentMin = anchor + new float2(6f, (0f - contentH) * 0.5f);
        float2 bgMin = contentMin - pad;
        float2 bgMax = contentMin + new float2(contentW, contentH) + pad;

        return new DrawItem
        {
            Priority = BadgePriority(edge, routing, routeNodes),
            Rect = new ScreenRect(bgMin.X, bgMin.Y, bgMax.X - bgMin.X, bgMax.Y - bgMin.Y),
            IsBadge = true,
            Dual = dual,
            Text = asc,
            AscText = asc,
            DescText = desc,
            TextPos = contentMin,
            BgMin = bgMin,
            BgMax = bgMax,
            Alpha = offRoute ? OffRouteAlpha : 1.0
        };
    }

    // Where the badge anchors on the edge. The badge sits on the segment that carries the
    // metric in that mode: the vertical ladder drop for ladder edges in either mode, but
    // for a GravityWell transfer the metric is the horizontal spine run (the transfer is a
    // horizontal hop between wells), so the badge rides the first segment there.
    private static float2 BadgeAnchorScreen(LayoutEdge edge, in CanvasTransform t, LayoutMode mode)
    {
        IReadOnlyList<LayoutPoint> pts = edge.Polyline;
        LayoutPoint a, b;
        if (mode == LayoutMode.GravityWell && edge.Class == EdgeClass.Transfer)
        {
            a = pts[0];
            b = pts[1];
        }
        else
        {
            int n = pts.Count;
            a = pts[n - 2];
            b = pts[n - 1];
        }
        return t.ToScreen((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
    }

    private static void DrawLabelItem(ImDrawListPtr dl, in DrawItem item)
    {
        float2 shadow = item.TextPos + new float2(1f, 1f);
        dl.AddText(in shadow, Fade(LabelShadow, item.Alpha), item.Text);
        dl.AddText(in item.TextPos, Fade(LabelText, item.Alpha), item.Text);
    }

    private static void DrawBadgeItem(ImDrawListPtr dl, in DrawItem item)
    {
        float2 bgMin = item.BgMin;
        float2 bgMax = item.BgMax;
        byte4 col = Fade(BadgeTextColor, item.Alpha);
        dl.AddRectFilled(in bgMin, in bgMax, Fade(BadgeBg, item.Alpha), 3f);

        if (!item.Dual)
        {
            dl.AddText(in item.TextPos, col, item.Text);
            return;
        }

        // Lay the dual badge out left to right with the same element widths the box was
        // sized to: up triangle, ascent dV, down triangle, descent dV, vertically centered.
        float contentH = item.BgMax.Y - item.BgMin.Y - 2f * BadgePadY;
        float cy = item.TextPos.Y + contentH * 0.5f;
        float x = item.TextPos.X;

        DrawTriangle(dl, x, cy, up: true, col);
        x += ArrowW + ArrowGap;
        var ascPos = new float2(x, item.TextPos.Y);
        dl.AddText(in ascPos, col, item.AscText);
        x += ImGui.CalcTextSize(item.AscText).X + DualSegGap;

        DrawTriangle(dl, x, cy, up: false, col);
        x += ArrowW + ArrowGap;
        var descPos = new float2(x, item.TextPos.Y);
        dl.AddText(in descPos, col, item.DescText);
    }

    // A small filled direction triangle inside a badge: apex up for ascent, apex down for
    // descent, ArrowW wide by ArrowH tall (wider than tall so it reads as an arrowhead),
    // vertically centered on cy.
    private static void DrawTriangle(ImDrawListPtr dl, float x, float cy, bool up, byte4 col)
    {
        float top = cy - ArrowH * 0.5f;
        float bottom = cy + ArrowH * 0.5f;
        if (up)
        {
            var p1 = new float2(x + ArrowW * 0.5f, top);
            var p2 = new float2(x, bottom);
            var p3 = new float2(x + ArrowW, bottom);
            dl.AddTriangleFilled(in p1, in p2, in p3, col);
        }
        else
        {
            var p1 = new float2(x, top);
            var p2 = new float2(x + ArrowW, top);
            var p3 = new float2(x + ArrowW * 0.5f, bottom);
            dl.AddTriangleFilled(in p1, in p2, in p3, col);
        }
    }

    // Lower number = drawn first = wins the screen room. Names come before plain dV badges
    // so the map reads as a labelled diagram first: hovered, root, you-are-here, on-route
    // name; the selected route's dV; then every body name by rank; then dV badges by rank.
    // Only the on-route dV outranks the names, since a chosen route wants both at once.
    private static int LabelPriority(LayoutNode node, string? hoverId, bool routing, IReadOnlySet<string>? routeNodes)
    {
        if (node.Id == hoverId)
            return 0;
        if (node.IsRoot)
            return 1;
        if (node.IsYouAreHere)
            return 2;
        if (routing && routeNodes!.Contains(node.Id))
            return 3;
        if (IsMajor(node))
            return 5;
        if (node.Rank == 2)
            return 6;
        return 7;
    }

    private static int BadgePriority(LayoutEdge edge, bool routing, IReadOnlySet<string>? routeNodes)
    {
        if (routing && OnRoute(edge, routeNodes!))
            return 4;
        bool major = IsMajor(edge.To);
        if (edge.Class == EdgeClass.Transfer)
            return major ? 8 : 9;
        return major ? 9 : 10;
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

    // One label or badge competing for screen room. Rect is the screen-space box used
    // for overlap; the draw fields carry what each kind needs to render.
    private struct DrawItem
    {
        public int Priority;
        public ScreenRect Rect;
        public bool IsBadge;
        public bool Dual;
        public string Text;
        public string AscText;
        public string DescText;
        public float2 TextPos;
        public double Alpha;
        public float2 BgMin;
        public float2 BgMax;
    }

    private readonly struct ScreenRect
    {
        public readonly float X;
        public readonly float Y;
        public readonly float W;
        public readonly float H;

        public ScreenRect(float x, float y, float w, float h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public float Right => X + W;
        public float Bottom => Y + H;

        public bool Intersects(in ScreenRect o)
        {
            return X < o.Right && Right > o.X && Y < o.Bottom && Bottom > o.Y;
        }
    }

    // A coarse screen-space spatial hash so the per-frame culling stays linear in the
    // number of labels and badges instead of comparing every pair.
    private sealed class ScreenHash
    {
        private readonly float _bucket;
        private readonly Dictionary<(int, int), List<ScreenRect>> _cells = new();

        public ScreenHash(double bucket)
        {
            _bucket = (float)bucket;
        }

        public void Insert(in ScreenRect rect)
        {
            int minCol = Col(rect.X);
            int maxCol = Col(rect.Right);
            int minRow = Col(rect.Y);
            int maxRow = Col(rect.Bottom);
            for (int c = minCol; c <= maxCol; c++)
            {
                for (int r = minRow; r <= maxRow; r++)
                {
                    var key = (c, r);
                    if (!_cells.TryGetValue(key, out List<ScreenRect>? list))
                    {
                        list = new List<ScreenRect>();
                        _cells[key] = list;
                    }
                    list.Add(rect);
                }
            }
        }

        public bool AnyOverlap(in ScreenRect rect)
        {
            int minCol = Col(rect.X);
            int maxCol = Col(rect.Right);
            int minRow = Col(rect.Y);
            int maxRow = Col(rect.Bottom);
            for (int c = minCol; c <= maxCol; c++)
            {
                for (int r = minRow; r <= maxRow; r++)
                {
                    if (!_cells.TryGetValue((c, r), out List<ScreenRect>? list))
                        continue;
                    foreach (ScreenRect existing in list)
                    {
                        if (rect.Intersects(existing))
                            return true;
                    }
                }
            }
            return false;
        }

        private int Col(float v) => (int)MathF.Floor(v / _bucket);
    }
}
