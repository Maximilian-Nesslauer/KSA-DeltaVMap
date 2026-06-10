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
// Five passes per frame:
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
//     Every label sits on a dim background plate so it stays legible where edge lines
//     pass behind it, and a zoom LOD declutters the overview: below FullLabelMinZoom each
//     body collapses to one short name label, below MoonLabelMinZoom moon names drop
//     entirely (root-context moons and the "+N" group headline exempt).
//  5. Transfer-window markers (only when the toggle is on): an amber clock badge near each
//     sibling with the countdown to its next window, on its own light cull pass.
internal static class CanvasRenderer
{
    // How far off-route geometry fades when a route is highlighted.
    private const double OffRouteAlpha = 0.2;
    private const float RouteLineWidth = 5f;

    // Below this zoom the whole system is squeezed onto the screen and the dV badges only
    // smear into noise, so they are hidden entirely; names still show (governed by the
    // culling). Zooming past it brings the numbers back.
    private const double BadgeMinZoom = 0.45;

    // Below this zoom minor-body (rank 3) labels are dropped from the culling pass: at a
    // zoomed-out overview their names are noise, and the major bodies plus any selected route
    // read better without them. The root, the hovered node and on-route nodes are exempt.
    private const double MinorLabelMinZoom = 0.5;

    // Below this zoom labels drop their rung suffix and collapse to one short body name per
    // body ("Saturn" instead of three "Saturn <rung>" labels): at overview the rung is
    // already carried by the glyph, and the duplicates would only repeat the name down the
    // ladder. Deliberately its own constant (not reusing MinorLabelMinZoom) so the two
    // transitions can be tuned apart. Special labels (hover, root, you-are-here, on-route)
    // always draw in full.
    private const double FullLabelMinZoom = 0.5;

    // Below this zoom moon-level (rank 2) labels are dropped entirely, leaving the far-out
    // overview to planets, the route and the "+N" group headlines. Moons in the root's
    // immediate context are exempt (Luna stays named on an Earth-rooted overview), as is
    // the minor-body group label itself.
    private const double MoonLabelMinZoom = 0.3;

    // The background plate behind every label, the zoom-robust fix for text crossing edge
    // lines: placement runs in layout space at 100% zoom, so no placement rule can keep a
    // fixed-size label clear of lines once the geometry scales away underneath it. Always
    // on (no UI toggle); the switch stays as a code escape hatch. The plate is more
    // transparent than the badge background so a sparse map does not read as boxes.
    private const bool LabelPlateEnabled = true;
    private const float LabelPadX = 3f;
    private const float LabelPadY = 1f;

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
    private static readonly byte4 BadgeSubText = new byte4(150, 162, 176, 255);
    // The with-margin figures, drawn in a warm amber to the right of the line so they read
    // apart from the canonical (grey) figures on the left. Only shown when a margin is set.
    private static readonly byte4 BadgeMarginText = new byte4(235, 192, 116, 255);
    private static readonly byte4 BadgeMarginSub = new byte4(192, 158, 104, 255);
    private static readonly byte4 LabelText = new byte4(205, 214, 224, 255);
    private static readonly byte4 LabelShadow = new byte4(0, 0, 0, 200);
    private static readonly byte4 LabelPlateBg = new byte4(16, 20, 28, 200);
    private static readonly byte4 RootRing = new byte4(255, 210, 194, 255);
    private static readonly byte4 RootHalo = new byte4(255, 170, 120, 60);
    private static readonly byte4 YouAreHereRing = new byte4(255, 210, 63, 255);
    private static readonly byte4 HoverRing = new byte4(255, 255, 255, 255);
    // The searched/focused body's distinct highlight: a bright cyan double ring, set apart
    // from the orange root, yellow you-are-here and white hover rings.
    private static readonly byte4 FocusRing = new byte4(96, 226, 232, 255);
    private static readonly byte4 RouteLine = new byte4(255, 255, 255, 255);

    // The transfer-window markers ("Show window markers" overlay): an amber clock badge near
    // each sibling, distinct from the grey dV badges.
    private static readonly byte4 WindowBadgeBg = new byte4(20, 24, 32, 215);
    private static readonly byte4 WindowBadgeText = new byte4(240, 200, 90, 255);

    public static void Draw(
        ImDrawListPtr dl,
        LayoutResult layout,
        IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette,
        in CanvasTransform t,
        string? hoverId,
        string? focusId,
        IReadOnlySet<string>? routeNodes,
        bool showPlaneChange,
        double dvScale,
        bool showTransferTimes,
        bool showBodyMarkers,
        IReadOnlyDictionary<string, double>? windowMarkers)
    {
        DrawEdgeLines(dl, layout, lookup, palette, in t, routeNodes);
        DrawNodeDots(dl, layout, lookup, palette, in t, hoverId, focusId, routeNodes, showBodyMarkers);
        DrawEdgeMarkers(dl, layout, lookup, palette, in t, routeNodes, showPlaneChange, dvScale, showBodyMarkers);
        DrawLabelsAndBadges(dl, layout, lookup, palette, in t, hoverId, routeNodes, dvScale, showTransferTimes);
        if (windowMarkers != null)
            DrawWindowMarkers(dl, layout, in t, windowMarkers);
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
        string? focusId,
        IReadOnlySet<string>? routeNodes,
        bool showBodyMarkers)
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
            // hub bus, so they key off the surface and low-orbit glyphs. The body-markers
            // toggle hides them for a plainer map.
            bool bodyNode = node.Kind == LayoutKind.Surface || node.Kind == LayoutKind.LowOrbit;
            bool atmoBody = showBodyMarkers && bodyNode && state != null && OrbitalStates.HasUsableAtmosphere(state.Body);
            bool ringedBody = showBodyMarkers && bodyNode && state != null && OrbitalStates.HasRings(state.Body);

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
            // The searched/focused body: a bright cyan double ring, full strength regardless
            // of the route fade so it stays findable after the search centers on it.
            if (node.Id == focusId)
            {
                dl.AddCircle(in p, r + 7f, FocusRing, 32, 3f);
                dl.AddCircle(in p, r + 11f, FocusRing, 32, 1.5f);
            }
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
            case LayoutKind.MinorGroup:
                NodeGlyphs.MinorGroup(dl, p, r, fill, stroke);
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
        bool showPlaneChange,
        double dvScale,
        bool showBodyMarkers)
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

                if (showBodyMarkers && edge.Aerobrake)
                    DrawAerobrake(dl, edge, lookup, palette, in t, alpha);

                // The plane-change figure scales with the piloting margin like the dV badges,
                // so the on-map number agrees with the breakdown the panel inflates.
                if (showPlaneChange && edge.Class == EdgeClass.Transfer && edge.PlaneChangeDv * dvScale >= PlaneChangeMinDv)
                    DrawPlaneChange(dl, edge, in t, alpha, dvScale);
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
    private static void DrawPlaneChange(ImDrawListPtr dl, LayoutEdge edge, in CanvasTransform t, double alpha, double dvScale)
    {
        float2 to = t.ToScreen(edge.To.SnappedX, edge.To.SnappedY);
        var pos = to + new float2(8f, -(float)edge.To.DotRadius - 16f);
        string text = "i ~" + DvNumber(edge.PlaneChangeDv * dvScale) + " m/s";
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
    // when shown. Zooming in spreads the anchors apart and reveals more of both. Which
    // labels even become candidates, and with which text, is the zoom LOD decided by
    // SelectLabelText: per-rank zoom floors plus the overview collapse to one short
    // body-name label per body.
    private static void DrawLabelsAndBadges(
        ImDrawListPtr dl,
        LayoutResult layout,
        IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette,
        in CanvasTransform t,
        string? hoverId,
        IReadOnlySet<string>? routeNodes,
        double dvScale,
        bool showTransferTimes)
    {
        bool routing = routeNodes != null;
        bool showBadges = t.Zoom >= BadgeMinZoom;
        bool shortLabels = t.Zoom < FullLabelMinZoom;
        LayoutMode mode = layout.Config.Mode;
        var items = new List<DrawItem>();

        // At overview zoom every body collapses to one short label, carried by its most
        // recognizable rung; pick that representative per body first. A body whose label
        // is special (hover, root, you-are-here, on-route - always drawn in full) needs
        // no representative: the special stands in, so the map never shows "Earth" next
        // to "Earth Low Orbit".
        Dictionary<string, LayoutNode>? reps = null;
        HashSet<string>? specialBodies = null;
        if (shortLabels)
        {
            reps = new Dictionary<string, LayoutNode>();
            specialBodies = new HashSet<string>();
            foreach (LayoutNode node in layout.Tree.Nodes)
            {
                if (!node.LabelPlaced)
                    continue;
                if (IsSpecialLabel(node, hoverId, routing, routeNodes))
                {
                    specialBodies.Add(BodyKey(node));
                    continue;
                }
                if (!RankShowsLabel(node, t.Zoom))
                    continue;
                string key = BodyKey(node);
                if (!reps.TryGetValue(key, out LayoutNode? cur) || RungPreference(node.Kind) < RungPreference(cur.Kind))
                    reps[key] = node;
            }
        }

        foreach (LayoutNode node in layout.Tree.Nodes)
        {
            if (node.LabelPlaced
                && SelectLabelText(node, t.Zoom, shortLabels, hoverId, routing, routeNodes, reps, specialBodies) is { } text)
                items.Add(BuildLabelItem(node, text, in t, hoverId, routing, routeNodes));

            if (!showBadges)
                continue;
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.IsHubLink || edge.RouteDv < 1.0 || edge.Polyline.Count < 2)
                    continue;
                items.Add(BuildBadgeItem(edge, in t, mode, routing, routeNodes, dvScale, showTransferTimes));
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

    // Decides whether a node's label draws this frame and with which text. Special labels
    // always draw in full: they are the user's current anchors, so the zoom LOD never
    // hides or shortens them. Everything else passes the per-rank zoom floors, and at
    // overview zoom only the per-body representative survives, carrying the short text.
    private static string? SelectLabelText(
        LayoutNode node, double zoom, bool shortLabels, string? hoverId,
        bool routing, IReadOnlySet<string>? routeNodes,
        Dictionary<string, LayoutNode>? reps, HashSet<string>? specialBodies)
    {
        if (IsSpecialLabel(node, hoverId, routing, routeNodes))
            return node.Label;
        if (!RankShowsLabel(node, zoom))
            return null;
        if (!shortLabels)
            return node.Label;
        string key = BodyKey(node);
        if (specialBodies!.Contains(key) || !ReferenceEquals(reps![key], node))
            return null;
        return node.ShortLabel.Length > 0 ? node.ShortLabel : node.Label;
    }

    private static bool IsSpecialLabel(LayoutNode node, string? hoverId, bool routing, IReadOnlySet<string>? routeNodes)
    {
        return node.IsRoot || node.IsYouAreHere || node.Id == hoverId
            || (routing && routeNodes!.Contains(node.Id));
    }

    // The zoom floors by cosmetic rank: minor-body (rank 3) names go first, then moon-level
    // (rank 2) names, leaving the far-out overview to planets. A minor-body group is exempt
    // (its "+N" count is the headline of a dense overview), as are moons in the root's
    // immediate context.
    private static bool RankShowsLabel(LayoutNode node, double zoom)
    {
        if (node.Rank >= 3)
            return zoom >= MinorLabelMinZoom;
        if (node.Rank == 2 && node.Kind != LayoutKind.MinorGroup && !IsNearRootContext(node))
            return zoom >= MoonLabelMinZoom;
        return true;
    }

    // Whether a node's body is immediate context of the root: its well hangs off the root
    // itself or off the root's nearest hub (a spine node at depth <= 1). That covers the
    // root's own moons (Luna on an Earth root) and, when rooted on a moon, its sibling
    // moons; a distant planet's moons attach much deeper down the spine and fail the test.
    private static bool IsNearRootContext(LayoutNode node)
    {
        string body = BodyKey(node);
        for (LayoutNode? p = node.Parent; p != null; p = p.Parent)
        {
            if (BodyKey(p) != body)
                return p.Depth <= 1;
        }
        return true;
    }

    private static string BodyKey(LayoutNode node)
    {
        return node.BodyId.Length > 0 ? node.BodyId : node.Id;
    }

    // Which rung carries a body's single short label at overview zoom. Low orbit is the
    // canonical "go here" rung; the rest order by how strongly they read as the body itself.
    private static int RungPreference(LayoutKind kind)
    {
        return kind switch
        {
            LayoutKind.LowOrbit => 0,
            LayoutKind.Hub => 1,
            LayoutKind.Surface => 2,
            LayoutKind.Intercept => 3,
            LayoutKind.Stationary => 4,
            LayoutKind.SoiEdge => 5,
            _ => 6
        };
    }

    private static DrawItem BuildLabelItem(
        LayoutNode node, string text, in CanvasTransform t, string? hoverId, bool routing, IReadOnlySet<string>? routeNodes)
    {
        float2 pos = t.ToScreen(node.LabelX, node.LabelY);
        float2 size = ImGui.CalcTextSize(text);
        bool onRoute = !routing || routeNodes!.Contains(node.Id);
        // The cull rect spans the plate, not just the text, so neighbouring plates never touch.
        float2 bgMin = pos - new float2(LabelPadX, LabelPadY);
        float2 bgMax = pos + size + new float2(LabelPadX, LabelPadY);

        return new DrawItem
        {
            Priority = LabelPriority(node, hoverId, routing, routeNodes),
            Rect = new ScreenRect(bgMin.X, bgMin.Y, bgMax.X - bgMin.X, bgMax.Y - bgMin.Y),
            IsBadge = false,
            Text = text,
            TextPos = pos,
            BgMin = bgMin,
            BgMax = bgMax,
            Alpha = onRoute ? 1.0 : OffRouteAlpha
        };
    }

    private static DrawItem BuildBadgeItem(
        LayoutEdge edge, in CanvasTransform t, LayoutMode mode, bool routing, IReadOnlySet<string>? routeNodes,
        double dvScale, bool showTransferTimes)
    {
        float2 anchor = BadgeAnchorScreen(edge, in t, mode);
        bool onRoute = routing && OnRoute(edge, routeNodes!);
        bool offRoute = routing && !onRoute;
        var pad = new float2(BadgePadX, BadgePadY);
        double alpha = offRoute ? OffRouteAlpha : 1.0;
        int priority = BadgePriority(edge, routing, routeNodes);
        bool paired = dvScale > 1.0001;

        // An Ascent edge on an atmospheric body shows both directions: an up triangle with
        // the ascent dV, then a down triangle with the cheaper descent dV. Drawn as filled
        // triangles (DrawBadgeItem) rather than text arrows, so they read as real arrows
        // while the source stays ASCII. The unit rides the descent (the last) of the pair.
        // Unlike the stacked badges the dual badge cannot show base | margin side by side, so
        // when a margin is set it is tinted amber instead, so the inflated value still reads
        // as inflated (matching the legend) rather than passing for the canonical cost.
        bool dual = edge.DescentDv > 1.0 && Math.Abs(edge.DescentDv - edge.RouteDv) > 1.0;
        if (dual)
        {
            string asc = "~" + DvNumber(edge.RouteDv * dvScale);
            string desc = "~" + DvNumber(edge.DescentDv * dvScale) + " m/s";
            float2 sa = ImGui.CalcTextSize(asc);
            float2 sd = ImGui.CalcTextSize(desc);
            float dw = ArrowW + ArrowGap + sa.X + DualSegGap + ArrowW + ArrowGap + sd.X;
            float dh = Math.Max(sa.Y, sd.Y);
            float2 dMin = anchor + new float2(6f, (0f - dh) * 0.5f);
            float2 dBgMin = dMin - pad;
            float2 dBgMax = dMin + new float2(dw, dh) + pad;
            return new DrawItem
            {
                Priority = priority,
                Rect = new ScreenRect(dBgMin.X, dBgMin.Y, dBgMax.X - dBgMin.X, dBgMax.Y - dBgMin.Y),
                IsBadge = true,
                Dual = true,
                Margined = paired,
                AscText = asc,
                DescText = desc,
                TextPos = dMin,
                BgMin = dBgMin,
                BgMax = dBgMax,
                Alpha = alpha
            };
        }

        // Every other badge is a stack of rows. A transfer shows its injection (the departure
        // / ejection burn, the pure escape onto the transfer) and its capture (the arrival
        // burn) individually, then the coupled total - matching the panel breakdown. A ladder
        // edge is just its single cost. The transfer-time toggle appends a dimmer coast-time
        // line. Each dV row carries the canonical (no-margin) figure and, when a piloting
        // margin is set, the inflated figure too: base grey on the left of the line, with-
        // margin amber on the right, so both are visible at once. The unit rides the total.
        var rows = new List<BadgeRow>(4);
        if (edge.Class == EdgeClass.Transfer)
        {
            // Show the injection / capture split only when both legs are non-trivial;
            // otherwise the total already equals the single leg and a detail row would just
            // repeat it.
            if (edge.InjectionDv >= 1.0 && edge.CaptureDv >= 1.0)
            {
                rows.Add(DvRow("inj ~", edge.InjectionDv, dvScale, paired, unit: false, main: false));
                rows.Add(DvRow("cap ~", edge.CaptureDv, dvScale, paired, unit: false, main: false));
            }
            rows.Add(DvRow("~", edge.RouteDv, dvScale, paired, unit: true, main: true));
            if (showTransferTimes && edge.TransferTimeSeconds > 0.0)
                rows.Add(new BadgeRow(FormatTransferTime(edge.TransferTimeSeconds), null, false));
        }
        else
        {
            rows.Add(DvRow("~", edge.RouteDv, dvScale, paired, unit: true, main: true));
        }

        float lineH = ImGui.GetTextLineHeight();
        float totalH = rows.Count * lineH;
        float top = anchor.Y - totalH * 0.5f;
        var lines = new List<BadgeLine>(rows.Count * 2);
        float bgLeft;
        float bgRight;

        if (!paired)
        {
            // No margin: one left-aligned block sitting just right of the line, as before.
            float left = anchor.X + 6f;
            float maxW = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                BadgeRow r = rows[i];
                lines.Add(new BadgeLine(r.BaseText, r.Main ? BadgeTextColor : BadgeSubText, new float2(left, top + i * lineH)));
                maxW = Math.Max(maxW, ImGui.CalcTextSize(r.BaseText).X);
            }
            bgLeft = left;
            bgRight = left + maxW;
        }
        else
        {
            // Margin set: base figures right-aligned to the left of the line, the with-margin
            // figures left-aligned to the right of it, so the line splits the two readings.
            const float gutter = 5f;
            float baseRight = anchor.X - gutter;
            float marginLeft = anchor.X + gutter;
            float baseMaxW = 0f;
            float marginMaxW = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                BadgeRow r = rows[i];
                float y = top + i * lineH;
                float bw = ImGui.CalcTextSize(r.BaseText).X;
                baseMaxW = Math.Max(baseMaxW, bw);
                lines.Add(new BadgeLine(r.BaseText, r.Main ? BadgeTextColor : BadgeSubText, new float2(baseRight - bw, y)));
                if (r.MarginText != null)
                {
                    marginMaxW = Math.Max(marginMaxW, ImGui.CalcTextSize(r.MarginText).X);
                    lines.Add(new BadgeLine(r.MarginText, r.Main ? BadgeMarginText : BadgeMarginSub, new float2(marginLeft, y)));
                }
            }
            bgLeft = baseRight - baseMaxW;
            bgRight = marginLeft + marginMaxW;
        }

        float2 bgMin = new float2(bgLeft - BadgePadX, top - BadgePadY);
        float2 bgMax = new float2(bgRight + BadgePadX, top + totalH + BadgePadY);

        return new DrawItem
        {
            Priority = priority,
            Rect = new ScreenRect(bgMin.X, bgMin.Y, bgMax.X - bgMin.X, bgMax.Y - bgMin.Y),
            IsBadge = true,
            Dual = false,
            Lines = lines,
            TextPos = new float2(bgLeft, top),
            BgMin = bgMin,
            BgMax = bgMax,
            Alpha = alpha
        };
    }

    // One dV row of a badge: the canonical figure ("inj ~3,617"), plus the with-margin figure
    // ("~3,979") when a margin is set. unit appends " m/s" (used on the total / single value).
    private static BadgeRow DvRow(string prefix, double baseDv, double dvScale, bool paired, bool unit, bool main)
    {
        string suffix = unit ? " m/s" : "";
        string baseText = prefix + DvNumber(baseDv) + suffix;
        string? marginText = paired ? "~" + DvNumber(baseDv * dvScale) + suffix : null;
        return new BadgeRow(baseText, marginText, main);
    }

    // A rounded dV magnitude with thousands separators (no unit, no "~"), matching the panel
    // breakdown's number style so the map and the panel read alike.
    private static string DvNumber(double dv)
    {
        return Math.Round(dv).ToString("#,##0", CultureInfo.InvariantCulture);
    }

    // Compact coast-time string for the transfer-badge subline (minutes / hours / days /
    // years), invariant so it stays ASCII regardless of locale.
    private static string FormatTransferTime(double seconds)
    {
        if (seconds < 3600.0)
            return string.Format(CultureInfo.InvariantCulture, "{0:0} min", seconds / 60.0);
        if (seconds < 86400.0)
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} h", seconds / 3600.0);
        double days = seconds / 86400.0;
        if (days < 365.25)
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} d", days);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0} yr", days / 365.25);
    }

    // The transfer-window markers: a small amber clock badge near each sibling that has a
    // window, showing the countdown to its next departure window. Keyed by the sibling's
    // representative node id (resolved in MapWindow). Gated by the same zoom floor as the dV
    // badges, drawn soonest-first and skipped where it would overlap an already-placed marker
    // (its own light cull pass), so a dense root does not smear. Sits up-left of the dot, clear
    // of the dV / transfer-time badges on the right. Drawn at full strength even when a route
    // dims the rest of the map: the timing layer is orthogonal to the chosen route.
    private static void DrawWindowMarkers(
        ImDrawListPtr dl, LayoutResult layout, in CanvasTransform t,
        IReadOnlyDictionary<string, double> markers)
    {
        if (t.Zoom < BadgeMinZoom)
            return;

        // markers only ever holds finite countdowns (the builder filters non-finite), so the
        // entries here need no further guard.
        var ordered = new List<(LayoutNode Node, double Secs)>();
        foreach (LayoutNode node in layout.Tree.Nodes)
        {
            if (markers.TryGetValue(node.Id, out double secs))
                ordered.Add((node, secs));
        }
        ordered.Sort(static (a, b) => a.Secs.CompareTo(b.Secs));

        var placed = new ScreenHash(48.0);
        foreach ((LayoutNode node, double secs) in ordered)
        {
            float2 p = t.ToScreen(node.SnappedX, node.SnappedY);
            float r = (float)node.DotRadius;
            string text = "~" + FormatWindowTime(secs);
            float2 ts = ImGui.CalcTextSize(text);
            float clockR = MathF.Max(4f, ts.Y * 0.42f);
            const float gap = 4f;
            float w = clockR * 2f + gap + ts.X;

            var pos = new float2(p.X - r - 6f - w, p.Y - r - 6f - ts.Y);
            float2 bgMin = pos - new float2(BadgePadX, BadgePadY);
            float2 bgMax = pos + new float2(w, ts.Y) + new float2(BadgePadX, BadgePadY);
            var rect = new ScreenRect(bgMin.X, bgMin.Y, bgMax.X - bgMin.X, bgMax.Y - bgMin.Y);
            if (placed.AnyOverlap(rect))
                continue;
            placed.Insert(rect);

            dl.AddRectFilled(in bgMin, in bgMax, WindowBadgeBg, 3f);

            // A tiny clock glyph: a ring with an hour and a minute hand.
            var c = new float2(pos.X + clockR, pos.Y + ts.Y * 0.5f);
            dl.AddCircle(in c, clockR, WindowBadgeText, 12, 1.4f);
            var hand1 = new float2(c.X, c.Y - clockR * 0.6f);
            var hand2 = new float2(c.X + clockR * 0.55f, c.Y + clockR * 0.1f);
            dl.AddLine(in c, in hand1, WindowBadgeText, 1.3f);
            dl.AddLine(in c, in hand2, WindowBadgeText, 1.3f);

            var tp = new float2(pos.X + clockR * 2f + gap, pos.Y);
            float2 sh = tp + new float2(1f, 1f);
            dl.AddText(in sh, LabelShadow, text);
            dl.AddText(in tp, WindowBadgeText, text);
        }
    }

    // Compact countdown for the window marker: minutes, hours, days, then years, so an imminent
    // sub-day window does not collapse to "0d" (and stays consistent with the overlay, which
    // shows the same countdown in min / h). No spaces, to keep the badge small; invariant so it
    // stays ASCII regardless of locale.
    private static string FormatWindowTime(double seconds)
    {
        if (seconds < 3600.0)
            return string.Format(CultureInfo.InvariantCulture, "{0:0}m", seconds / 60.0);
        if (seconds < 86400.0)
            return string.Format(CultureInfo.InvariantCulture, "{0:0}h", seconds / 3600.0);
        double days = seconds / 86400.0;
        if (days < 365.25)
            return string.Format(CultureInfo.InvariantCulture, "{0:0}d", days);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0}yr", days / 365.25);
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
        if (LabelPlateEnabled)
        {
            float2 bgMin = item.BgMin;
            float2 bgMax = item.BgMax;
            dl.AddRectFilled(in bgMin, in bgMax, Fade(LabelPlateBg, item.Alpha), 3f);
        }
        float2 shadow = item.TextPos + new float2(1f, 1f);
        dl.AddText(in shadow, Fade(LabelShadow, item.Alpha), item.Text);
        dl.AddText(in item.TextPos, Fade(LabelText, item.Alpha), item.Text);
    }

    private static void DrawBadgeItem(ImDrawListPtr dl, in DrawItem item)
    {
        float2 bgMin = item.BgMin;
        float2 bgMax = item.BgMax;
        // Used only by the dual (ascent/descent) badge: amber when a margin inflates it, so it
        // reads as inflated like the with-margin figures on the stacked badges.
        byte4 col = Fade(item.Margined ? BadgeMarginText : BadgeTextColor, item.Alpha);
        dl.AddRectFilled(in bgMin, in bgMax, Fade(BadgeBg, item.Alpha), 3f);

        if (!item.Dual)
        {
            // Each fragment is pre-positioned and pre-colored (base grey left, with-margin
            // amber right); just blit them, applying the route-fade alpha.
            foreach (BadgeLine ln in item.Lines!)
            {
                float2 pos = ln.Pos;
                dl.AddText(in pos, Fade(ln.Color, item.Alpha), ln.Text);
            }
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
        // A minor-body group's "+N" count is the headline of a dense overview, so it ranks
        // just under the route, ahead of every individual body name.
        if (node.Kind == LayoutKind.MinorGroup)
            return 4;
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
    // for overlap; the draw fields carry what each kind needs to render. A label uses Text;
    // a dual (ascent/descent) badge uses AscText/DescText with drawn triangles; every other
    // badge uses Lines (a stack: a transfer's injection / capture / total, a ladder's cost).
    private struct DrawItem
    {
        public int Priority;
        public ScreenRect Rect;
        public bool IsBadge;
        public bool Dual;
        // A dual (ascent/descent) badge whose figures are inflated by a piloting margin; drawn
        // amber so it reads as inflated. Non-dual badges show base | margin per row instead.
        public bool Margined;
        public string Text;
        public string AscText;
        public string DescText;
        public List<BadgeLine>? Lines;
        public float2 TextPos;
        public double Alpha;
        public float2 BgMin;
        public float2 BgMax;
    }

    // One positioned text fragment of a stacked (non-dual) badge: the resolved (pre-fade)
    // color and the absolute screen position, so the draw pass just blits it. Layout (left/
    // right alignment around the line, base vs margin coloring) is all decided in BuildBadgeItem.
    private readonly struct BadgeLine
    {
        public readonly string Text;
        public readonly byte4 Color;
        public readonly float2 Pos;

        public BadgeLine(string text, byte4 color, float2 pos)
        {
            Text = text;
            Color = color;
            Pos = pos;
        }
    }

    // One dV row of a badge before positioning: the canonical figure, the optional with-margin
    // figure (null when no margin), and whether it is the bright headline (total / single cost)
    // or a dim detail line (a transfer's injection / capture, or the coast time).
    private readonly struct BadgeRow
    {
        public readonly string BaseText;
        public readonly string? MarginText;
        public readonly bool Main;

        public BadgeRow(string baseText, string? marginText, bool main)
        {
            BaseText = baseText;
            MarginText = marginText;
            Main = main;
        }
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
