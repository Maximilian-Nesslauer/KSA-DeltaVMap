using System;
using System.Collections.Generic;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using DeltaVMap.Core;
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

// The transfer-window markers to draw: the marker node of each window (null when the body
// is not on the map), its countdown, and the cached countdown text with its measured width.
// MapWindow keeps the arrays and only rewrites a text when the shown value changes.
internal readonly struct WindowMarkerSet
{
    public readonly int Count;
    public readonly LayoutNode?[] Nodes;
    public readonly double[] Seconds;
    public readonly string[] Texts;
    public readonly float[] Widths;

    public WindowMarkerSet(int count, LayoutNode?[] nodes, double[] seconds, string[] texts, float[] widths)
    {
        Count = count;
        Nodes = nodes;
        Seconds = seconds;
        Texts = texts;
        Widths = widths;
    }
}

// The badge on the interstellar connector of the selected route: the leg's delta-v and coast
// time at the chosen cruise speed, with their measured widths. InterstellarSection rebuilds the
// strings only when a figure changes, so a speed change touches nothing but this text.
internal readonly record struct ConnectorBadge(string DestinationRootId, string DvText, float DvW, string TimeText, float TimeW);

// Draws the laid-out scene with ImGui DrawList primitives every frame the window is
// visible. Reads positions and routed polylines straight off the LayoutScene (the
// layout engine already produced overlap-free, octilinear geometry, and SceneComposer
// placed the other star systems beside it); this class only turns that into lines,
// symbols, badges and labels. The StateNode lookup gives each layout node its game body
// for the per-system color.
//
// Six passes per frame:
//  1. Edge polylines and the dashed interstellar connectors (off-route faded, then the
//     route heavy and white on top), plus the scale-break mark on the connector trunk.
//  2. Node dots, their state-kind glyphs, and the atmosphere/ring node markers. With other
//     star systems shown, each system root gets a quiet ring and a collapsed system its
//     larger square with a faint fan under it.
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
//     pass behind it, and the zoom LOD of LabelLod declutters the overview. Below its
//     FullLabelMinZoom each body collapses to one short name label, and below its
//     MoonLabelMinZoom moon names drop entirely (root-context moons and the "+N" group
//     headline exempt). A system root's
//     label is its system title instead: the name in bold white, the distance in the
//     interstellar color and, for a collapsed system, a dim summary line. Titles are never
//     shortened or culled, and take the first spot of SceneComposer.SystemTitleSpots that
//     covers no drawn text, no other dot and no connector line. No other label covers the
//     glyph of another system's root.
//  5. Transfer-window markers (only when the toggle is on): an amber clock badge near each
//     sibling with the countdown to its next window, on its own light cull pass.
//
// The renderer is an instance owned by the window, so every buffer it needs is reused frame
// to frame. What depends only on the scene (node colors, body markers) is resolved once per
// scene, and the badge strings with their measured widths once per scene and display setting,
// so a frame draws without allocating.
internal sealed class CanvasRenderer
{
    // How far off-route geometry fades when a route is highlighted.
    private const double OffRouteAlpha = 0.2;
    private const float RouteLineWidth = 5f;

    // Below this zoom the whole system is squeezed onto the screen and the dV badges only
    // smear into noise, so they are hidden entirely; names still show (governed by the
    // culling). Zooming past it brings the numbers back.
    private const double BadgeMinZoom = 0.45;

    // The background plate behind every label, the zoom-robust fix for text crossing edge
    // lines: placement runs in layout space at 100% zoom, so no placement rule can keep a
    // fixed-size label clear of lines once the geometry scales away underneath it. The plate
    // is more transparent than the badge background so a sparse map does not read as boxes.
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

    // The cell size of the screen-space cull grids.
    private const float CullBucketPx = 48f;

    internal static readonly byte4 CanvasBackground = new byte4(17, 21, 28, 255);

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
    private static readonly byte4 TitleName = new byte4(255, 255, 255, 255);
    private static readonly byte4 TitleSummary = new byte4(133, 147, 163, 255);

    // Off the selected route a system title fades less than other text, since it names where
    // the parts of the map are.
    private const double TitleOffRouteAlpha = 0.55;
    private const double SystemRingAlpha = 0.75;

    // The transfer-window markers ("Show window markers" overlay): an amber clock badge near
    // each sibling, distinct from the grey dV badges.
    private static readonly byte4 WindowBadgeBg = new byte4(20, 24, 32, 215);
    private static readonly byte4 WindowBadgeText = new byte4(240, 200, 90, 255);

    private const float ConnectorWidth = 2.2f;
    private const float ConnectorRouteWidth = 4f;

    // Per-scene caches.
    private LayoutScene? _styleScene;
    private ColorPalette? _stylePalette;
    private NodeStyle[] _styles = Array.Empty<NodeStyle>();

    // The system titles of the scene, split once per scene: the name, the distance part with
    // its offset after the name, by node index (null for a node that is no system root).
    private string?[] _titleNames = Array.Empty<string?>();
    private string?[] _titleDistances = Array.Empty<string?>();
    private float[] _titleDistanceX = Array.Empty<float>();

    // Where each system title was drawn this frame, for the window's hit test.
    private ScreenRect[] _titleRects = Array.Empty<ScreenRect>();
    private bool[] _titleDrawn = Array.Empty<bool>();
    private int _titleCount;

    private LayoutScene? _badgeScene;
    private double _badgeScale = double.NaN;
    private bool _badgeTimes;
    private BadgeText?[] _badges = Array.Empty<BadgeText?>();

    // Per-frame buffers, reused.
    private readonly List<DrawItem> _items = new();
    private readonly Dictionary<string, LayoutNode> _reps = new();
    private readonly HashSet<string> _specialBodies = new();
    private readonly ScreenGrid _dots = new(CullBucketPx);
    private readonly ScreenGrid _occupied = new(CullBucketPx);
    private readonly ScreenGrid _markerGrid = new(CullBucketPx);
    private int[] _markerOrder = Array.Empty<int>();

    private float _lineHeight;
    private float2 _clipMin;
    private float2 _clipMax;

    private static readonly Comparison<DrawItem> ByPriority = static (a, b) =>
    {
        int byPriority = a.Priority.CompareTo(b.Priority);
        if (byPriority != 0)
            return byPriority;
        int byY = a.Rect.Y.CompareTo(b.Rect.Y);
        return byY != 0 ? byY : a.Rect.X.CompareTo(b.Rect.X);
    };

    public void Draw(
        ImDrawListPtr dl,
        LayoutScene scene,
        IReadOnlyDictionary<string, StateNode> lookup,
        ColorPalette palette,
        in CanvasTransform t,
        float2 canvasMin,
        float2 canvasMax,
        string? hoverId,
        string? focusId,
        IReadOnlySet<string>? routeNodes,
        bool showPlaneChange,
        double dvScale,
        bool showTransferTimes,
        bool showBodyMarkers,
        in WindowMarkerSet windowMarkers,
        ConnectorBadge? connectorBadge)
    {
        _clipMin = canvasMin;
        _clipMax = canvasMax;
        _lineHeight = ImGui.GetTextLineHeight();
        EnsureStyles(scene, lookup, palette);
        if (t.Zoom >= BadgeMinZoom)
            EnsureBadges(scene, dvScale, showTransferTimes);

        DrawEdgeLines(dl, scene, in t, routeNodes);
        DrawNodeDots(dl, scene, in t, hoverId, focusId, routeNodes, showBodyMarkers);
        DrawEdgeMarkers(dl, scene, in t, routeNodes, showPlaneChange, dvScale, showBodyMarkers);
        DrawLabelsAndBadges(dl, scene, in t, hoverId, routeNodes, dvScale, connectorBadge);
        if (windowMarkers.Count > 0)
            DrawWindowMarkers(dl, in t, in windowMarkers);
    }

    // Drop the per-scene caches, for a new system or an unload.
    public void Reset()
    {
        _styleScene = null;
        _stylePalette = null;
        _styles = Array.Empty<NodeStyle>();
        _badgeScene = null;
        _badges = Array.Empty<BadgeText?>();
        _titleNames = Array.Empty<string?>();
        _titleDistances = Array.Empty<string?>();
        _titleDistanceX = Array.Empty<float>();
        _titleDrawn = Array.Empty<bool>();
        _titleRects = Array.Empty<ScreenRect>();
        _titleCount = 0;
        _items.Clear();
        _reps.Clear();
        _specialBodies.Clear();
    }

    // The colors and body markers of every node, resolved once per scene: the game body gives
    // the system color and whether the body has a usable atmosphere or rings.
    private void EnsureStyles(LayoutScene scene, IReadOnlyDictionary<string, StateNode> lookup, ColorPalette palette)
    {
        if (ReferenceEquals(scene, _styleScene) && ReferenceEquals(palette, _stylePalette))
            return;
        IReadOnlyList<LayoutNode> nodes = scene.Nodes;
        if (_styles.Length < nodes.Count)
            _styles = new NodeStyle[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode node = nodes[i];
            lookup.TryGetValue(node.Id, out StateNode? state);
            byte4 baseColor = state != null ? palette.ColorFor(state.Body) : palette.ColorFor(node.Id);
            // The atmosphere/ring markers belong on a body's in-atmosphere rungs (the ones you
            // can fly a jet at or see the rings from), not on its high orbits or its hub bus.
            bool bodyNode = node.Kind == LayoutKind.Surface || node.Kind == LayoutKind.LowOrbit;
            _styles[i] = new NodeStyle(
                baseColor,
                Lighten(baseColor, 0.45),
                Lighten(baseColor, 0.4),
                bodyNode && state != null && OrbitalStates.HasUsableAtmosphere(state.Body),
                bodyNode && state != null && OrbitalStates.HasRings(state.Body));
        }
        EnsureTitles(scene);
        _styleScene = scene;
        _stylePalette = palette;
    }

    // Split every system title at its double space into the name and the distance, and measure
    // where the distance starts.
    private void EnsureTitles(LayoutScene scene)
    {
        IReadOnlyList<LayoutNode> nodes = scene.Nodes;
        if (_titleNames.Length < nodes.Count)
        {
            _titleNames = new string?[nodes.Count];
            _titleDistances = new string?[nodes.Count];
            _titleDistanceX = new float[nodes.Count];
            _titleRects = new ScreenRect[nodes.Count];
            _titleDrawn = new bool[nodes.Count];
        }
        Array.Clear(_titleNames);
        Array.Clear(_titleDistances);
        Array.Clear(_titleDrawn);
        _titleCount = nodes.Count;
        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode node = nodes[i];
            if (!node.IsSystemRoot)
                continue;
            string title = node.ShortLabel.Length > 0 ? node.ShortLabel : node.Label;
            int cut = title.IndexOf("  ", StringComparison.Ordinal);
            _titleNames[i] = cut > 0 ? title.Substring(0, cut) : title;
            if (cut > 0)
            {
                _titleDistances[i] = title.Substring(cut + 2);
                _titleDistanceX[i] = ImGui.CalcTextSize(title.Substring(0, cut + 2)).X + 1f;
            }
        }
    }

    // The system root whose title box holds the point, from the titles drawn last frame, or -1.
    public int TitleAt(float2 point)
    {
        for (int i = 0; i < _titleCount; i++)
        {
            if (!_titleDrawn[i])
                continue;
            ref readonly ScreenRect r = ref _titleRects[i];
            if (point.X >= r.X && point.X <= r.Right && point.Y >= r.Y && point.Y <= r.Bottom)
                return i;
        }
        return -1;
    }

    private void DrawEdgeLines(ImDrawListPtr dl, LayoutScene scene, in CanvasTransform t, IReadOnlySet<string>? routeNodes)
    {
        bool routing = routeNodes != null;
        IReadOnlyList<LayoutNode> nodes = scene.Nodes;
        IReadOnlyList<LayoutConnector> connectors = scene.Connectors;
        byte4 lane = routing ? Fade(ColorPalette.InterstellarLine, OffRouteAlpha) : ColorPalette.InterstellarLine;

        // Base pass: every edge except the highlighted route (deferred so the heavy
        // white line draws on top). Off-route edges fade when a route is active.
        for (int n = 0; n < nodes.Count; n++)
        {
            LayoutNode node = nodes[n];
            for (int e = 0; e < node.Out.Count; e++)
            {
                LayoutEdge edge = node.Out[e];
                if (edge.Polyline.Count < 2)
                    continue;
                if (routing && OnRoute(edge, routeNodes!))
                    continue;

                byte4 color;
                float width;
                if (edge.IsHubLink)
                {
                    color = HubBus;
                    width = 3.5f;
                }
                else
                {
                    // Color the line by the body it leads to, so a transfer reads in the
                    // destination's system color and a ladder edge in its own body's color. An
                    // Approach edge inside another system is a thin line of the same color.
                    color = _styles[edge.To.Index].Fill;
                    width = edge.IsApproach ? 1.4f : edge.Class == EdgeClass.Transfer ? 2.2f : 1.6f;
                }
                DrawPolyline(dl, edge.Polyline, in t, routing ? Fade(color, OffRouteAlpha) : color, width);
            }
        }

        // The shared trunk and buses once, then the branch of every connector off the route.
        IReadOnlyList<IReadOnlyList<LayoutPoint>> network = scene.Network;
        for (int i = 0; i < network.Count; i++)
            DrawDashedPolyline(dl, network[i], in t, lane, ConnectorWidth);
        for (int i = 0; i < connectors.Count; i++)
        {
            LayoutConnector connector = connectors[i];
            if (routing && OnRoute(connector, routeNodes!))
                continue;
            DrawDashedBranch(dl, connector, in t, lane, ConnectorWidth);
        }
        if (scene.HasBreakMark)
            NodeGlyphs.ScaleBreak(dl, t.ToScreen(scene.BreakMark.X, scene.BreakMark.Y), lane, CanvasBackground, scene.BreakMarkVertical);

        if (!routing)
            return;

        // Route pass: the selected path, heavy and white.
        for (int n = 0; n < nodes.Count; n++)
        {
            LayoutNode node = nodes[n];
            for (int e = 0; e < node.Out.Count; e++)
            {
                LayoutEdge edge = node.Out[e];
                if (edge.Polyline.Count < 2 || !OnRoute(edge, routeNodes!))
                    continue;
                DrawPolyline(dl, edge.Polyline, in t, RouteLine, RouteLineWidth);
            }
        }
        for (int i = 0; i < connectors.Count; i++)
        {
            LayoutConnector connector = connectors[i];
            if (OnRoute(connector, routeNodes!))
                DrawDashedPolyline(dl, connector.Polyline, in t, RouteLine, ConnectorRouteWidth);
        }
    }

    // In a tree, two route nodes are adjacent only if the edge between them is on the path, so
    // membership of both endpoints is enough. A connector stands for the Interstellar edge
    // between the same two nodes.
    private static bool OnRoute(LayoutEdge edge, IReadOnlySet<string> routeNodes)
    {
        return routeNodes.Contains(edge.From.Id) && routeNodes.Contains(edge.To.Id);
    }

    internal static bool OnRoute(LayoutConnector connector, IReadOnlySet<string> routeNodes)
    {
        return routeNodes.Contains(connector.From.Id) && routeNodes.Contains(connector.To.Id);
    }

    private static void DrawPolyline(ImDrawListPtr dl, IReadOnlyList<LayoutPoint> line, in CanvasTransform t, byte4 color, float width)
    {
        for (int i = 1; i < line.Count; i++)
        {
            float2 a = t.ToScreen(line[i - 1].X, line[i - 1].Y);
            float2 b = t.ToScreen(line[i].X, line[i].Y);
            dl.AddLine(in a, in b, color, width);
        }
    }

    private void DrawDashedPolyline(ImDrawListPtr dl, IReadOnlyList<LayoutPoint> line, in CanvasTransform t, byte4 color, float width)
    {
        for (int i = 1; i < line.Count; i++)
        {
            float2 a = t.ToScreen(line[i - 1].X, line[i - 1].Y);
            float2 b = t.ToScreen(line[i].X, line[i].Y);
            NodeGlyphs.DashedSegment(dl, a, b, color, width, _clipMin, _clipMax);
        }
    }

    // A connector's own branch: from its badge anchor on the bus to its root.
    private void DrawDashedBranch(ImDrawListPtr dl, LayoutConnector connector, in CanvasTransform t, byte4 color, float width)
    {
        IReadOnlyList<LayoutPoint> line = connector.Polyline;
        float2 a = t.ToScreen(connector.BadgeAnchor.X, connector.BadgeAnchor.Y);
        for (int i = connector.BranchStart; i < line.Count; i++)
        {
            float2 b = t.ToScreen(line[i].X, line[i].Y);
            NodeGlyphs.DashedSegment(dl, a, b, color, width, _clipMin, _clipMax);
            a = b;
        }
    }

    private void DrawNodeDots(
        ImDrawListPtr dl,
        LayoutScene scene,
        in CanvasTransform t,
        string? hoverId,
        string? focusId,
        IReadOnlySet<string>? routeNodes,
        bool showBodyMarkers)
    {
        bool routing = routeNodes != null;
        IReadOnlyList<LayoutNode> nodes = scene.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode node = nodes[i];
            float2 p = t.ToScreen(node.SnappedX, node.SnappedY);
            float r = (float)SceneComposer.GlyphRadiusPx(node);
            if (p.X + r + 16f < _clipMin.X || p.X - r - 16f > _clipMax.X || p.Y + r + 16f < _clipMin.Y || p.Y - r - 16f > _clipMax.Y)
                continue;
            bool onRoute = !routing || routeNodes!.Contains(node.Id);
            double alpha = onRoute ? 1.0 : OffRouteAlpha;

            ref readonly NodeStyle style = ref _styles[i];
            byte4 fill = Fade(style.Fill, alpha);
            byte4 stroke = Fade(style.Stroke, alpha);
            bool atmoBody = showBodyMarkers && style.Atmosphere;
            bool ringedBody = showBodyMarkers && style.Rings;

            // A soft filled halo behind the root so the ego anchor pops out of a dense
            // cluster even when zoomed out. Drawn before the dot so it sits underneath.
            if (node.IsRoot)
                dl.AddCircleFilled(in p, r + 10f, RootHalo);

            // Rings sit behind the body disc, like a planet seen against its ring plane.
            if (ringedBody)
                NodeGlyphs.RingEllipse(dl, p, r, stroke);

            if (node.IsSystemStub)
                NodeGlyphs.SystemFan(dl, p, r, Fade(style.Fill, 0.35 * alpha));
            if (node.IsSystemRoot && !node.IsRoot)
                dl.AddCircle(in p, r + 6f, Fade(style.Stroke, SystemRingAlpha * alpha), 32, 1.5f);

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
    // carries the planetary system color, the stroke a lightened accent of it. With several
    // star systems a hub draws as its star or barycenter, and a collapsed system as its stub.
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
                if (node.IsSystemStub)
                    NodeGlyphs.SystemStub(dl, p, r, fill, stroke, node.HubRole == HubRole.Barycenter);
                else if (node.HubRole == HubRole.Star)
                    NodeGlyphs.Star(dl, p, r, fill, stroke);
                else if (node.HubRole == HubRole.Barycenter)
                    NodeGlyphs.Barycenter(dl, p, r, fill, stroke);
                else
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
    private void DrawEdgeMarkers(
        ImDrawListPtr dl,
        LayoutScene scene,
        in CanvasTransform t,
        IReadOnlySet<string>? routeNodes,
        bool showPlaneChange,
        double dvScale,
        bool showBodyMarkers)
    {
        if (t.Zoom < BadgeMinZoom)
            return;

        bool routing = routeNodes != null;
        IReadOnlyList<LayoutNode> nodes = scene.Nodes;
        for (int n = 0; n < nodes.Count; n++)
        {
            LayoutNode node = nodes[n];
            for (int e = 0; e < node.Out.Count; e++)
            {
                LayoutEdge edge = node.Out[e];
                if (edge.Polyline.Count < 2)
                    continue;
                bool onRoute = !routing || OnRoute(edge, routeNodes!);
                double alpha = onRoute ? 1.0 : OffRouteAlpha;

                if (showBodyMarkers && edge.Aerobrake)
                    DrawAerobrake(dl, edge, in t, alpha);

                // The plane-change figure scales with the piloting margin like the dV badges,
                // so the on-map number agrees with the breakdown the panel inflates.
                if (showPlaneChange && edge.Class == EdgeClass.Transfer && edge.PlaneChangeDv * dvScale >= PlaneChangeMinDv
                    && _badges[edge.To.Index]?.PlaneText is string planeText)
                    DrawPlaneChange(dl, edge, in t, alpha, planeText);
            }
        }
    }

    // A filled triangle partway along the capture edge, pointing the way the capture runs
    // (from the loose ellipse down into low orbit), in the destination's lightened color.
    private void DrawAerobrake(ImDrawListPtr dl, LayoutEdge edge, in CanvasTransform t, double alpha)
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
        NodeGlyphs.AerobrakeTriangle(dl, at, dx, dy, 8f, Fade(_styles[edge.To.Index].Accent, alpha));
    }

    // The plane-change figure near the transfer's arrival node, offset up and to the right
    // to sit clear of the dV badge. Drawn as a plain number (DrawList text cannot rotate, so
    // there is no literal KSP slant); its warm color and the legend entry key its meaning.
    private static void DrawPlaneChange(ImDrawListPtr dl, LayoutEdge edge, in CanvasTransform t, double alpha, string text)
    {
        float2 to = t.ToScreen(edge.To.SnappedX, edge.To.SnappedY);
        var pos = to + new float2(8f, -(float)edge.To.DotRadius - 16f);
        var shadow = pos + new float2(1f, 1f);
        dl.AddText(in shadow, Fade(LabelShadow, alpha), text);
        dl.AddText(in pos, Fade(NodeGlyphs.PlaneChangeColor, alpha), text);
    }

    // The badge strings and their measured widths, once per scene, margin and transfer-time
    // setting. A frame then only positions them.
    private void EnsureBadges(LayoutScene scene, double dvScale, bool showTransferTimes)
    {
        if (ReferenceEquals(scene, _badgeScene) && dvScale == _badgeScale && showTransferTimes == _badgeTimes)
            return;
        IReadOnlyList<LayoutNode> nodes = scene.Nodes;
        if (_badges.Length < nodes.Count)
            _badges = new BadgeText?[nodes.Count];
        Array.Clear(_badges);
        bool paired = dvScale > 1.0001;
        for (int n = 0; n < nodes.Count; n++)
        {
            LayoutNode node = nodes[n];
            for (int e = 0; e < node.Out.Count; e++)
            {
                LayoutEdge edge = node.Out[e];
                string? plane = edge.Class == EdgeClass.Transfer && edge.PlaneChangeDv * dvScale >= PlaneChangeMinDv
                    ? "i ~" + Format.DvNumber(edge.PlaneChangeDv * dvScale) + " m/s"
                    : null;
                bool badge = !edge.IsHubLink && edge.RouteDv >= 1.0;
                if (badge || plane != null)
                    _badges[edge.To.Index] = BuildBadgeText(edge, dvScale, paired, showTransferTimes, badge, plane);
            }
        }
        _badgeScene = scene;
        _badgeScale = dvScale;
        _badgeTimes = showTransferTimes;
    }

    // An Ascent edge on an atmospheric body shows both directions: an up triangle with the
    // ascent dV, then a down triangle with the cheaper descent dV. Unlike the stacked badges the
    // dual badge cannot show base | margin side by side, so when a margin is set it is tinted
    // amber instead. Every other badge is a stack of rows. A transfer shows its injection (the
    // departure / ejection burn) and its capture (the arrival burn) individually, then the
    // coupled total, matching the panel breakdown. A ladder edge is just its single cost. The
    // transfer-time toggle appends a dimmer coast-time line. Each dV row carries the canonical
    // (no-margin) figure and, when a piloting margin is set, the inflated figure too.
    private static BadgeText BuildBadgeText(LayoutEdge edge, double dvScale, bool paired, bool showTransferTimes, bool badge, string? plane)
    {
        var text = new BadgeText { PlaneText = plane, HasBadge = badge, Paired = paired };
        if (!badge)
            return text;

        text.Dual = edge.DescentDv > 1.0 && Math.Abs(edge.DescentDv - edge.RouteDv) > 1.0;
        if (text.Dual)
        {
            text.AscText = "~" + Format.DvNumber(edge.RouteDv * dvScale);
            text.DescText = "~" + Format.DvNumber(edge.DescentDv * dvScale) + " m/s";
            text.AscW = ImGui.CalcTextSize(text.AscText).X;
            text.DescW = ImGui.CalcTextSize(text.DescText).X;
            return text;
        }

        if (edge.Class == EdgeClass.Transfer)
        {
            // Show the injection / capture split only when both legs are non-trivial;
            // otherwise the total already equals the single leg.
            if (edge.InjectionDv >= 1.0 && edge.CaptureDv >= 1.0)
            {
                text.AddDvRow("inj ~", edge.InjectionDv, dvScale, paired, unit: false, main: false);
                text.AddDvRow("cap ~", edge.CaptureDv, dvScale, paired, unit: false, main: false);
            }
            text.AddDvRow("~", edge.RouteDv, dvScale, paired, unit: true, main: true);
            if (showTransferTimes && edge.TransferTimeSeconds > 0.0)
                text.AddRow(Format.TransferTime(edge.TransferTimeSeconds), null, false);
        }
        else
        {
            text.AddDvRow("~", edge.RouteDv, dvScale, paired, unit: true, main: true);
        }
        return text;
    }

    // The screen-space label + badge culling pass (see the class summary). Builds a
    // candidate for every placed label and (above a zoom floor) every dV badge, sorts them
    // by priority, then draws greedily, skipping any whose screen box overlaps one already
    // drawn. Names are NOT blocked by node dots (only by other text), so the highest-rank
    // names - the root first - show even at the zoomed-out auto-fit view; a name resting on
    // a dot is fine and beats hiding it. dV badges are hidden entirely below BadgeMinZoom
    // and avoid dots when shown. Candidates wholly off the canvas are skipped.
    private void DrawLabelsAndBadges(
        ImDrawListPtr dl,
        LayoutScene scene,
        in CanvasTransform t,
        string? hoverId,
        IReadOnlySet<string>? routeNodes,
        double dvScale,
        ConnectorBadge? connectorBadge)
    {
        bool routing = routeNodes != null;
        bool showBadges = t.Zoom >= BadgeMinZoom;
        bool shortLabels = t.Zoom < LabelLod.FullLabelMinZoom;
        Array.Clear(_titleDrawn, 0, _titleCount);
        LayoutMode mode = scene.Config.Mode;
        IReadOnlyList<LayoutNode> nodes = scene.Nodes;
        _items.Clear();

        // At overview zoom every body collapses to one short label, carried by its most
        // recognizable rung; pick that representative per body first. A body whose label
        // is special (hover, root, you-are-here, on-route - always drawn in full) needs
        // no representative: the special stands in, so the map never shows "Earth" next
        // to "Earth Low Orbit".
        _reps.Clear();
        _specialBodies.Clear();
        if (shortLabels)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                LayoutNode node = nodes[i];
                if (!node.LabelPlaced)
                    continue;
                if (LabelLod.IsSpecial(node, hoverId, routing, routeNodes))
                {
                    _specialBodies.Add(LabelLod.BodyKey(node));
                    continue;
                }
                if (!LabelLod.RankShows(node, t.Zoom))
                    continue;
                string key = LabelLod.BodyKey(node);
                if (!_reps.TryGetValue(key, out LayoutNode? cur) || LabelLod.RungPreference(node.Kind) < LabelLod.RungPreference(cur.Kind))
                    _reps[key] = node;
            }
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode node = nodes[i];
            if (node.IsSystemRoot)
            {
                TryAddTitleItem(node, in t, hoverId, routing, routeNodes);
            }
            else if (node.LabelPlaced)
            {
                LabelChoice which = SelectLabelText(node, t.Zoom, shortLabels, hoverId, routing, routeNodes);
                if (which != LabelChoice.None)
                    TryAddLabelItem(node, which, in t, hoverId, routing, routeNodes);
            }

            if (!showBadges)
                continue;
            for (int e = 0; e < node.Out.Count; e++)
            {
                LayoutEdge edge = node.Out[e];
                if (edge.IsHubLink || edge.RouteDv < 1.0 || edge.Polyline.Count < 2)
                    continue;
                if (_badges[edge.To.Index] is { HasBadge: true } text)
                    TryAddBadgeItem(edge, text, in t, mode, routing, routeNodes);
            }
        }

        if (connectorBadge is ConnectorBadge cb && routing)
            TryAddConnectorBadge(scene, cb, in t, routeNodes!);

        // Highest priority (smallest number) first; a stable screen-position tiebreak
        // keeps the survivor set from flickering between frames at equal priority.
        _items.Sort(ByPriority);

        // A badge or a title must not cover a node dot; labels may (they identify the very dots
        // they sit near). So only badges and titles test against this dot grid. The glyph of
        // another system's root is the one dot no label covers, so it also goes in as drawn.
        _dots.Reset(_clipMin, _clipMax);
        _occupied.Reset(_clipMin, _clipMax);
        int egoCount = scene.Ego.Tree.Nodes.Count;
        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode node = nodes[i];
            float2 p = t.ToScreen(node.SnappedX, node.SnappedY);
            float r = (float)SceneComposer.GlyphRadiusPx(node);
            var dot = new ScreenRect(p.X - r, p.Y - r, 2f * r, 2f * r);
            if (!Visible(in dot))
                continue;
            _dots.Insert(in dot);
            if (node.IsSystemRoot && i >= egoCount)
                _occupied.Insert(in dot);
        }

        Span<(double X, double Y)> spots = stackalloc (double, double)[SceneComposer.TitleSpotCount];
        for (int i = 0; i < _items.Count; i++)
        {
            DrawItem item = _items[i];
            if (item.Kind == ItemKind.Title)
            {
                PlaceTitle(dl, scene, in t, ref item, nodes[item.NodeIndex], spots);
                continue;
            }
            if (item.Kind == ItemKind.ConnectorBadge)
            {
                PlaceConnectorBadge(dl, in item, connectorBadge!.Value);
                continue;
            }
            if (_occupied.AnyOverlap(in item.Rect))
                continue;
            if (item.Kind != ItemKind.Label && _dots.AnyOverlap(in item.Rect))
                continue;
            _occupied.Insert(in item.Rect);
            switch (item.Kind)
            {
                case ItemKind.Label:
                    DrawLabelItem(dl, in item, nodes[item.NodeIndex]);
                    break;
                case ItemKind.Badge:
                    DrawBadgeItem(dl, in item, _badges[item.NodeIndex]!);
                    break;
            }
        }
    }

    private enum LabelChoice
    {
        None,
        Full,
        Short
    }

    // Decides whether a node's label draws this frame and with which text. Special labels
    // always draw in full: they are the user's current anchors, so the zoom LOD never
    // hides or shortens them. Everything else passes the per-rank zoom floors, and at
    // overview zoom only the per-body representative survives, carrying the short text.
    private LabelChoice SelectLabelText(
        LayoutNode node, double zoom, bool shortLabels, string? hoverId,
        bool routing, IReadOnlySet<string>? routeNodes)
    {
        if (LabelLod.IsSpecial(node, hoverId, routing, routeNodes))
            return LabelChoice.Full;
        if (!LabelLod.RankShows(node, zoom))
            return LabelChoice.None;
        if (!shortLabels)
            return LabelChoice.Full;
        string key = LabelLod.BodyKey(node);
        if (_specialBodies.Contains(key) || !_reps.TryGetValue(key, out LayoutNode? rep) || !ReferenceEquals(rep, node))
            return LabelChoice.None;
        return node.ShortLabel.Length > 0 ? LabelChoice.Short : LabelChoice.Full;
    }

    private void TryAddLabelItem(
        LayoutNode node, LabelChoice which, in CanvasTransform t, string? hoverId, bool routing, IReadOnlySet<string>? routeNodes)
    {
        float2 pos = t.ToScreen(node.LabelX, node.LabelY);
        float width = (float)(which == LabelChoice.Short ? node.ShortLabelTextW : node.LabelTextW);
        // The cull rect spans the plate, not just the text, so neighbouring plates never touch.
        var bgMin = new float2(pos.X - LabelPadX, pos.Y - LabelPadY);
        var bgMax = new float2(pos.X + width + LabelPadX, pos.Y + _lineHeight + LabelPadY);
        var rect = new ScreenRect(bgMin.X, bgMin.Y, bgMax.X - bgMin.X, bgMax.Y - bgMin.Y);
        if (!Visible(in rect))
            return;
        bool onRoute = !routing || routeNodes!.Contains(node.Id);
        _items.Add(new DrawItem
        {
            Kind = ItemKind.Label,
            Priority = LabelLod.Priority(node, hoverId, routing, routeNodes),
            Rect = rect,
            NodeIndex = node.Index,
            ShortText = which == LabelChoice.Short,
            TextPos = pos,
            BgMin = bgMin,
            BgMax = bgMax,
            Alpha = onRoute ? 1.0 : OffRouteAlpha
        });
    }

    // A system title competes for room ahead of every other label. Its rect here is the first
    // spot, for the sort; PlaceTitle picks the spot when the item's turn comes.
    private void TryAddTitleItem(LayoutNode node, in CanvasTransform t, string? hoverId, bool routing, IReadOnlySet<string>? routeNodes)
    {
        float2 p = t.ToScreen(node.SnappedX, node.SnappedY);
        float r = (float)SceneComposer.GlyphRadiusPx(node);
        float w = (float)SceneComposer.TitleTextW(node) + 2f * LabelPadX;
        float h = (SceneComposer.ShowsSummary(node) ? 2f : 1f) * _lineHeight + 2f * LabelPadY;
        float reach = r + 12f + w + h;
        if (p.X + reach < _clipMin.X || p.X - reach > _clipMax.X || p.Y + reach < _clipMin.Y || p.Y - reach > _clipMax.Y)
            return;
        bool onRoute = !routing || routeNodes!.Contains(node.Id);
        _items.Add(new DrawItem
        {
            Kind = ItemKind.Title,
            Priority = node.Id == hoverId ? 0 : 1,
            Rect = new ScreenRect(p.X + r, p.Y - 0.5f * h, w, h),
            NodeIndex = node.Index,
            Anchor = p,
            Size = new float2(w, h),
            Alpha = onRoute ? 1.0 : TitleOffRouteAlpha
        });
    }

    // Draw a system title in the first of its spots that lies on the canvas and covers no drawn
    // text, no other dot and no connector line, or in the first spot when none is free: a system
    // never loses its name. The title's own glyph is in both grids, and every spot keeps clear
    // of it.
    private void PlaceTitle(ImDrawListPtr dl, LayoutScene scene, in CanvasTransform t, ref DrawItem item, LayoutNode node, Span<(double X, double Y)> spots)
    {
        float w = item.Size.X;
        float h = item.Size.Y;
        SceneComposer.SystemTitleSpots(node, item.Anchor.X, item.Anchor.Y, w, h, _lineHeight, spots);
        double originX = t.Origin.X + t.PanX - t.MinX * t.Zoom;
        double originY = t.Origin.Y + t.PanY - t.MinY * t.Zoom;
        var chosen = new ScreenRect((float)spots[0].X, (float)spots[0].Y, w, h);
        for (int s = 0; s < spots.Length; s++)
        {
            var rect = new ScreenRect((float)spots[s].X, (float)spots[s].Y, w, h);
            bool inside = rect.X >= _clipMin.X && rect.Right <= _clipMax.X && rect.Y >= _clipMin.Y && rect.Bottom <= _clipMax.Y;
            if (inside && !_occupied.AnyOverlap(in rect) && !_dots.AnyOverlap(in rect)
                && !SceneComposer.ConnectorsHitScreenRect(scene, t.Zoom, originX, originY, rect.X, rect.Y, rect.Right, rect.Bottom))
            {
                chosen = rect;
                break;
            }
        }
        _occupied.Insert(in chosen);
        _titleRects[item.NodeIndex] = chosen;
        _titleDrawn[item.NodeIndex] = true;

        var bgMin = new float2(chosen.X, chosen.Y);
        var bgMax = new float2(chosen.Right, chosen.Bottom);
        dl.AddRectFilled(in bgMin, in bgMax, Fade(LabelPlateBg, item.Alpha), 3f);
        var pos = new float2(chosen.X + LabelPadX, chosen.Y + LabelPadY);
        string name = _titleNames[item.NodeIndex] ?? node.Label;
        var shadow = pos + new float2(1f, 1f);
        dl.AddText(in shadow, Fade(LabelShadow, item.Alpha), name);
        dl.AddText(in pos, Fade(TitleName, item.Alpha), name);
        var bold = pos + new float2(1f, 0f);
        dl.AddText(in bold, Fade(TitleName, item.Alpha), name);
        if (_titleDistances[item.NodeIndex] is string distance)
        {
            var distPos = new float2(pos.X + _titleDistanceX[item.NodeIndex], pos.Y);
            dl.AddText(in distPos, Fade(ColorPalette.InterstellarLine, item.Alpha), distance);
        }
        if (SceneComposer.ShowsSummary(node))
        {
            var sumPos = new float2(pos.X, pos.Y + _lineHeight);
            dl.AddText(in sumPos, Fade(TitleSummary, item.Alpha), node.Summary);
        }
    }

    private void TryAddBadgeItem(LayoutEdge edge, BadgeText text, in CanvasTransform t, LayoutMode mode, bool routing, IReadOnlySet<string>? routeNodes)
    {
        float2 anchor = BadgeAnchorScreen(edge, in t, mode);
        bool onRoute = routing && OnRoute(edge, routeNodes!);
        double alpha = routing && !onRoute ? OffRouteAlpha : 1.0;
        int priority = BadgePriority(edge, routing, routeNodes);
        float2 bgMin;
        float2 bgMax;
        float2 textPos;

        if (text.Dual)
        {
            float dw = ArrowW + ArrowGap + text.AscW + DualSegGap + ArrowW + ArrowGap + text.DescW;
            float dh = _lineHeight;
            textPos = new float2(anchor.X + 6f, anchor.Y - dh * 0.5f);
            bgMin = new float2(textPos.X - BadgePadX, textPos.Y - BadgePadY);
            bgMax = new float2(textPos.X + dw + BadgePadX, textPos.Y + dh + BadgePadY);
        }
        else
        {
            float totalH = text.RowCount * _lineHeight;
            float top = anchor.Y - totalH * 0.5f;
            float bgLeft;
            float bgRight;
            if (!text.Paired)
            {
                // No margin: one left-aligned block sitting just right of the line.
                bgLeft = anchor.X + 6f;
                bgRight = bgLeft + text.BaseMaxW;
            }
            else
            {
                // Margin set: base figures right-aligned to the left of the line, the with-
                // margin figures left-aligned to the right of it, so the line splits them.
                bgLeft = anchor.X - BadgeGutter - text.BaseMaxW;
                bgRight = anchor.X + BadgeGutter + text.MarginMaxW;
            }
            textPos = new float2(anchor.X, top);
            bgMin = new float2(bgLeft - BadgePadX, top - BadgePadY);
            bgMax = new float2(bgRight + BadgePadX, top + totalH + BadgePadY);
        }

        var rect = new ScreenRect(bgMin.X, bgMin.Y, bgMax.X - bgMin.X, bgMax.Y - bgMin.Y);
        if (!Visible(in rect))
            return;
        _items.Add(new DrawItem
        {
            Kind = ItemKind.Badge,
            Priority = priority,
            Rect = rect,
            NodeIndex = edge.To.Index,
            TextPos = textPos,
            BgMin = bgMin,
            BgMax = bgMax,
            Alpha = alpha
        });
    }

    private const float BadgeGutter = 5f;

    // The badge of the interstellar connector on the selected route: the leg's delta-v and
    // coast time, where the connector leaves the shared bus for the destination root. Only
    // drawn while the connector is on the route, and ahead of every other badge. Its rect here
    // is the first spot, for the sort; PlaceConnectorBadge picks the spot when its turn comes.
    private void TryAddConnectorBadge(LayoutScene scene, ConnectorBadge badge, in CanvasTransform t, IReadOnlySet<string> routeNodes)
    {
        IReadOnlyList<LayoutConnector> connectors = scene.Connectors;
        for (int i = 0; i < connectors.Count; i++)
        {
            LayoutConnector connector = connectors[i];
            if (connector.To.Id != badge.DestinationRootId || !OnRoute(connector, routeNodes))
                continue;
            float2 anchor = t.ToScreen(connector.BadgeAnchor.X, connector.BadgeAnchor.Y);
            float w = Math.Max(badge.DvW, badge.TimeW) + 2f * BadgePadX;
            float h = 2f * _lineHeight + 2f * BadgePadY;
            (double x, double y) = SceneComposer.BadgeSpot(0, anchor.X, anchor.Y, w, h);
            float reach = w + h;
            if (anchor.X + reach < _clipMin.X || anchor.X - reach > _clipMax.X || anchor.Y + reach < _clipMin.Y || anchor.Y - reach > _clipMax.Y)
                return;
            _items.Add(new DrawItem
            {
                Kind = ItemKind.ConnectorBadge,
                Priority = 3,
                Rect = new ScreenRect((float)x, (float)y, w, h),
                Anchor = anchor,
                Size = new float2(w, h),
                Alpha = 1.0
            });
            return;
        }
    }

    // Draw the route badge in the first spot of SceneComposer.BadgeSpot that lies on the canvas
    // and covers no drawn text and no dot. With none free it is skipped like any other badge.
    private void PlaceConnectorBadge(ImDrawListPtr dl, in DrawItem item, ConnectorBadge badge)
    {
        float w = item.Size.X;
        float h = item.Size.Y;
        for (int s = 0; s < SceneComposer.BadgeSpotCount; s++)
        {
            (double x, double y) = SceneComposer.BadgeSpot(s, item.Anchor.X, item.Anchor.Y, w, h);
            var rect = new ScreenRect((float)x, (float)y, w, h);
            bool inside = rect.X >= _clipMin.X && rect.Right <= _clipMax.X && rect.Y >= _clipMin.Y && rect.Bottom <= _clipMax.Y;
            if (!inside || _occupied.AnyOverlap(in rect) || _dots.AnyOverlap(in rect))
                continue;
            _occupied.Insert(in rect);
            DrawConnectorBadge(dl, new float2(rect.X, rect.Y), new float2(rect.Right, rect.Bottom), badge);
            return;
        }
    }

    private void DrawConnectorBadge(ImDrawListPtr dl, float2 bgMin, float2 bgMax, ConnectorBadge badge)
    {
        dl.AddRectFilled(in bgMin, in bgMax, BadgeBg, 3f);
        var pos = new float2(bgMin.X + BadgePadX, bgMin.Y + BadgePadY);
        dl.AddText(in pos, BadgeTextColor, badge.DvText);
        var timePos = new float2(pos.X, pos.Y + _lineHeight);
        dl.AddText(in timePos, BadgeSubText, badge.TimeText);
    }

    // The transfer-window markers: a small amber clock badge near each sibling that has a
    // window, showing the countdown to its next departure window. Gated by the same zoom floor
    // as the dV badges, drawn soonest-first and skipped where it would overlap an already-
    // placed marker (its own light cull pass), so a dense root does not smear. Sits up-left of
    // the dot, clear of the dV / transfer-time badges on the right. Drawn at full strength even
    // when a route dims the rest of the map: the timing layer is orthogonal to the route.
    private void DrawWindowMarkers(ImDrawListPtr dl, in CanvasTransform t, in WindowMarkerSet markers)
    {
        if (t.Zoom < BadgeMinZoom)
            return;

        if (_markerOrder.Length < markers.Count)
            _markerOrder = new int[markers.Count];
        int count = 0;
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers.Nodes[i] != null && double.IsFinite(markers.Seconds[i]))
                _markerOrder[count++] = i;
        }
        // Soonest first; a handful of windows, so an insertion sort without a comparer.
        for (int i = 1; i < count; i++)
        {
            int value = _markerOrder[i];
            int j = i - 1;
            while (j >= 0 && markers.Seconds[_markerOrder[j]] > markers.Seconds[value])
            {
                _markerOrder[j + 1] = _markerOrder[j];
                j--;
            }
            _markerOrder[j + 1] = value;
        }

        _markerGrid.Reset(_clipMin, _clipMax);
        for (int k = 0; k < count; k++)
        {
            int i = _markerOrder[k];
            LayoutNode node = markers.Nodes[i]!;
            string text = markers.Texts[i];
            float2 p = t.ToScreen(node.SnappedX, node.SnappedY);
            float r = (float)node.DotRadius;
            float textW = markers.Widths[i];
            float textH = _lineHeight;
            float clockR = MathF.Max(4f, textH * 0.42f);
            const float gap = 4f;
            float w = clockR * 2f + gap + textW;

            var pos = new float2(p.X - r - 6f - w, p.Y - r - 6f - textH);
            var bgMin = new float2(pos.X - BadgePadX, pos.Y - BadgePadY);
            var bgMax = new float2(pos.X + w + BadgePadX, pos.Y + textH + BadgePadY);
            var rect = new ScreenRect(bgMin.X, bgMin.Y, bgMax.X - bgMin.X, bgMax.Y - bgMin.Y);
            if (!Visible(in rect) || _markerGrid.AnyOverlap(in rect))
                continue;
            _markerGrid.Insert(in rect);

            dl.AddRectFilled(in bgMin, in bgMax, WindowBadgeBg, 3f);

            // A tiny clock glyph: a ring with an hour and a minute hand.
            var c = new float2(pos.X + clockR, pos.Y + textH * 0.5f);
            dl.AddCircle(in c, clockR, WindowBadgeText, 12, 1.4f);
            var hand1 = new float2(c.X, c.Y - clockR * 0.6f);
            var hand2 = new float2(c.X + clockR * 0.55f, c.Y + clockR * 0.1f);
            dl.AddLine(in c, in hand1, WindowBadgeText, 1.3f);
            dl.AddLine(in c, in hand2, WindowBadgeText, 1.3f);

            var tp = new float2(pos.X + clockR * 2f + gap, pos.Y);
            var sh = new float2(tp.X + 1f, tp.Y + 1f);
            dl.AddText(in sh, LabelShadow, text);
            dl.AddText(in tp, WindowBadgeText, text);
        }
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

    private static void DrawLabelItem(ImDrawListPtr dl, in DrawItem item, LayoutNode node)
    {
        string text = item.ShortText ? node.ShortLabel : node.Label;
        float2 bgMin = item.BgMin;
        float2 bgMax = item.BgMax;
        dl.AddRectFilled(in bgMin, in bgMax, Fade(LabelPlateBg, item.Alpha), 3f);
        float2 shadow = item.TextPos + new float2(1f, 1f);
        dl.AddText(in shadow, Fade(LabelShadow, item.Alpha), text);
        dl.AddText(in item.TextPos, Fade(LabelText, item.Alpha), text);
    }

    private void DrawBadgeItem(ImDrawListPtr dl, in DrawItem item, BadgeText text)
    {
        float2 bgMin = item.BgMin;
        float2 bgMax = item.BgMax;
        dl.AddRectFilled(in bgMin, in bgMax, Fade(BadgeBg, item.Alpha), 3f);

        if (!text.Dual)
        {
            float anchorX = item.TextPos.X;
            float top = item.TextPos.Y;
            for (int i = 0; i < text.RowCount; i++)
            {
                float y = top + i * _lineHeight;
                byte4 baseColor = text.Main[i] ? BadgeTextColor : BadgeSubText;
                var basePos = text.Paired
                    ? new float2(anchorX - BadgeGutter - text.BaseW[i], y)
                    : new float2(anchorX + 6f, y);
                dl.AddText(in basePos, Fade(baseColor, item.Alpha), text.Base[i]);
                if (text.Paired && text.Margin[i] is string margin)
                {
                    var marginPos = new float2(anchorX + BadgeGutter, y);
                    dl.AddText(in marginPos, Fade(text.Main[i] ? BadgeMarginText : BadgeMarginSub, item.Alpha), margin);
                }
            }
            return;
        }

        // Lay the dual badge out left to right with the same element widths the box was
        // sized to: up triangle, ascent dV, down triangle, descent dV, vertically centered.
        // Amber when a margin inflates it, so it reads as inflated like the stacked badges.
        byte4 col = Fade(text.Paired ? BadgeMarginText : BadgeTextColor, item.Alpha);
        float cy = item.TextPos.Y + _lineHeight * 0.5f;
        float x = item.TextPos.X;

        DrawTriangle(dl, x, cy, up: true, col);
        x += ArrowW + ArrowGap;
        var ascPos = new float2(x, item.TextPos.Y);
        dl.AddText(in ascPos, col, text.AscText);
        x += text.AscW + DualSegGap;

        DrawTriangle(dl, x, cy, up: false, col);
        x += ArrowW + ArrowGap;
        var descPos = new float2(x, item.TextPos.Y);
        dl.AddText(in descPos, col, text.DescText);
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

    private static int BadgePriority(LayoutEdge edge, bool routing, IReadOnlySet<string>? routeNodes)
    {
        if (routing && OnRoute(edge, routeNodes!))
            return 4;
        bool major = LabelLod.IsMajor(edge.To);
        if (edge.Class == EdgeClass.Transfer)
            return major ? 8 : 9;
        return major ? 9 : 10;
    }

    private bool Visible(in ScreenRect rect)
    {
        return rect.Right >= _clipMin.X && rect.X <= _clipMax.X && rect.Bottom >= _clipMin.Y && rect.Y <= _clipMax.Y;
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

    // The resolved look of one node: its system color, the lightened stroke and marker accent,
    // and which body markers its body carries.
    private readonly record struct NodeStyle(byte4 Fill, byte4 Stroke, byte4 Accent, bool Atmosphere, bool Rings);

    private enum ItemKind
    {
        Label,
        Badge,
        ConnectorBadge,
        Title
    }

    // One label or badge competing for screen room. Rect is the screen-space box used for
    // overlap. NodeIndex is the node of a label, or the To node of a badge's edge, which keys
    // the cached badge text.
    private struct DrawItem
    {
        public ItemKind Kind;
        public int Priority;
        public ScreenRect Rect;
        public int NodeIndex;
        public bool ShortText;
        public float2 TextPos;
        public double Alpha;
        public float2 BgMin;
        public float2 BgMax;

        // A system title's or a route badge's anchor point on screen and its plate size.
        public float2 Anchor;
        public float2 Size;
    }

    // The strings of one edge's badge with their measured widths, built when the scene or a
    // display setting changes. A stacked badge holds at most four rows.
    private sealed class BadgeText
    {
        public bool HasBadge;
        public bool Paired;
        public bool Dual;
        public string AscText = "";
        public string DescText = "";
        public float AscW;
        public float DescW;
        public int RowCount;
        public readonly string[] Base = new string[4];
        public readonly string?[] Margin = new string?[4];
        public readonly float[] BaseW = new float[4];
        public readonly bool[] Main = new bool[4];
        public float BaseMaxW;
        public float MarginMaxW;
        public string? PlaneText;

        // One dV row: the canonical figure ("inj ~3,617"), plus the with-margin figure
        // ("~3,979") when a margin is set. unit appends " m/s" (on the total / single value).
        public void AddDvRow(string prefix, double baseDv, double dvScale, bool paired, bool unit, bool main)
        {
            string suffix = unit ? " m/s" : "";
            AddRow(prefix + Format.DvNumber(baseDv) + suffix, paired ? "~" + Format.DvNumber(baseDv * dvScale) + suffix : null, main);
        }

        public void AddRow(string baseText, string? marginText, bool main)
        {
            int i = RowCount++;
            Base[i] = baseText;
            Margin[i] = marginText;
            Main[i] = main;
            BaseW[i] = ImGui.CalcTextSize(baseText).X;
            BaseMaxW = Math.Max(BaseMaxW, BaseW[i]);
            if (marginText != null)
                MarginMaxW = Math.Max(MarginMaxW, ImGui.CalcTextSize(marginText).X);
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

    // A flat screen-space grid over the canvas so the per-frame culling stays linear in the
    // number of labels and badges. Each cell heads a linked list of entries in shared arrays,
    // which grow when needed and are otherwise reused, so a frame allocates nothing. A rect
    // reaching past the canvas lands in the border cells; the overlap test is exact anyway.
    private sealed class ScreenGrid
    {
        private readonly float _bucket;
        private float _originX;
        private float _originY;
        private int _cols;
        private int _rows;
        private int[] _head = Array.Empty<int>();
        private int[] _next = new int[256];
        private int[] _entryRect = new int[256];
        private ScreenRect[] _rects = new ScreenRect[128];
        private int _entryCount;
        private int _rectCount;

        public ScreenGrid(float bucket)
        {
            _bucket = bucket;
        }

        public void Reset(float2 min, float2 max)
        {
            _originX = min.X;
            _originY = min.Y;
            _cols = Math.Max(1, (int)MathF.Ceiling((max.X - min.X) / _bucket) + 1);
            _rows = Math.Max(1, (int)MathF.Ceiling((max.Y - min.Y) / _bucket) + 1);
            int cells = _cols * _rows;
            if (_head.Length < cells)
                _head = new int[cells];
            Array.Fill(_head, -1, 0, cells);
            _entryCount = 0;
            _rectCount = 0;
        }

        public void Insert(in ScreenRect rect)
        {
            if (_rectCount == _rects.Length)
                Array.Resize(ref _rects, _rects.Length * 2);
            int index = _rectCount++;
            _rects[index] = rect;
            CellRange(in rect, out int c0, out int c1, out int r0, out int r1);
            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    if (_entryCount == _next.Length)
                    {
                        Array.Resize(ref _next, _next.Length * 2);
                        Array.Resize(ref _entryRect, _entryRect.Length * 2);
                    }
                    int cell = r * _cols + c;
                    int entry = _entryCount++;
                    _entryRect[entry] = index;
                    _next[entry] = _head[cell];
                    _head[cell] = entry;
                }
            }
        }

        public bool AnyOverlap(in ScreenRect rect)
        {
            CellRange(in rect, out int c0, out int c1, out int r0, out int r1);
            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    for (int entry = _head[r * _cols + c]; entry >= 0; entry = _next[entry])
                    {
                        if (rect.Intersects(in _rects[_entryRect[entry]]))
                            return true;
                    }
                }
            }
            return false;
        }

        private void CellRange(in ScreenRect rect, out int c0, out int c1, out int r0, out int r1)
        {
            c0 = Cell(rect.X - _originX, _cols);
            c1 = Cell(rect.Right - _originX, _cols);
            r0 = Cell(rect.Y - _originY, _rows);
            r1 = Cell(rect.Bottom - _originY, _rows);
        }

        private int Cell(float v, int count)
        {
            return Math.Clamp((int)MathF.Floor(v / _bucket), 0, count - 1);
        }
    }
}
