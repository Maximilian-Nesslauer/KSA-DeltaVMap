using System;
using System.Collections.Generic;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;
using DeltaVMap.Route;
using KSA;

namespace DeltaVMap.Render;

// The Delta-V map window. Extends the stock ImGuiWindow base for Begin/End, the menu
// bar and pin/focus handling, and renders an interactive metro map on a pannable,
// zoomable canvas. It owns the build pipeline: physical graph and dV cache per loaded
// system, a re-rooted visual tree and a laid-out result per root. The graph and cache
// survive while the same system is loaded; the visual tree and layout are rebuilt on
// re-root or on a detail change, and the layout is measured with real ImGui text
// metrics (only available here, inside the draw loop).
internal sealed class MapWindow : ImGuiWindow
{
    // The design quotes a 25%-400% manual range, but the default Earth root is a
    // ~132-node tree that does not fit at 25%; allow zooming out further so auto-fit
    // can show the whole map on open. Revisit once detail collapsing (minor-body
    // grouping) lands and dense roots no longer need such a low floor.
    private const double MinZoom = 0.1;
    private const double MaxZoom = 4.0;
    private const double HoverRadiusPx = 14.0;

    // How close (screen px) the cursor must be to an edge polyline to show its tooltip.
    // Smaller than the node radius so a node always wins a hover near a junction.
    private const double EdgeHoverPx = 7.0;

    private static readonly byte4 BackgroundColor = new byte4(17, 21, 28, 255);

    private static MapWindow? _instance;

    // Per-system state (rebuilt when Universe.CurrentSystem changes).
    private SystemGraph? _graph;
    private DvCache? _cache;
    private ColorPalette? _palette;

    // Per-root state (rebuilt on re-root or detail change). _visualTree backs routing:
    // RouteFinder walks its StateNode tree from the origin to the clicked target, and
    // _lookup resolves a clicked node Id to its StateNode.
    private VisualTree? _visualTree;
    private LayoutResult? _layout;
    private Dictionary<string, StateNode>? _lookup;
    private string? _currentRootId;

    // Root whose last build threw, so EnsureBuilt does not re-attempt it every frame.
    private string? _buildFailedRootId;
    private bool _built;

    // On the next build, re-evaluate the ego root (set when the window is opened so a
    // SOI or system change since last time is picked up; a mid-flight SOI change is
    // deliberately not applied until reopen, per the design).
    private bool _reevaluateRoot;

    // Detail toggle: full ladder on every body instead of core rungs on distant ones.
    private bool _fullLadder;

    // Active layout strategy. CumulativeDown (the dV-down tidy tree) is the default; it
    // read clearest in testing. GravityWell (a row of vertical wells on a horizontal
    // low-orbit spine) stays one click away on the layout toggle. Switching rebuilds via
    // RebuildAt, the cached relayout path the full-ladder toggle uses.
    private LayoutMode _layoutMode = LayoutMode.CumulativeDown;

    // View-only "center view": when on, auto-fit and the Fit button center the root node
    // in the viewport rather than centering the layout bounding box. No engine change.
    private bool _centerOnRoot;

    // View state.
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private bool _needsFit;
    private float2 _lastSize;

    // Interaction state.
    private string? _hoverId;

    // The clicked route target Id (null = no route). Plain click sets it; the route is
    // accumulated from it into _routeSummary, and _routeNodeIds is the set of node Ids
    // on the path, used by the renderer to highlight the route and dim everything else.
    private string? _selectedId;
    private RouteSummary? _routeSummary;
    private HashSet<string>? _routeNodeIds;
    private readonly RouteOptions _options = new();

    // Display and visibility settings (visibility rebuilds the tree; the rest are display
    // only). Shared with the panel, which edits it, and read by the canvas renderer.
    private readonly ViewOptions _view = new();
    private bool _middlePanning;
    private bool _titleApplied;

    public static MapWindow Instance => _instance ??= new MapWindow();

    // Draw the window if it exists and is shown. Does not create the instance, so the
    // draw hook never touches ImGui state before the user first opens the map.
    public static void DrawActive(Viewport viewport)
    {
        if (_instance != null && _instance.IsShown)
        {
            _instance.EnsureTitle();
            _instance.OnDrawUi(viewport);
        }
    }

    // Re-assert the window title at top level just before the first draw. The
    // constructor (the conventional place, and where TargetTrackWindow does it) runs
    // nested inside the game's View-menu draw, where the assignment did not take effect
    // in-game; setting it here, right before the base class calls ImGui.Begin, does.
    private void EnsureTitle()
    {
        if (_titleApplied)
            return;
        SetWindowTitle("Delta-V Map");
        _titleApplied = true;
    }

    // Drop all state so a fresh load rebuilds cleanly. Called from [StarMapUnload];
    // must not touch ImGui (it can run outside a frame).
    public static void ResetStatic()
    {
        _instance = null;
    }

    private MapWindow()
        : base(ComputeInitialSize(), lockAspectRatio: false, show: false)
    {
        SetWindowTitle("Delta-V Map");
        // The canvas handles its own panning, so suppress the window scrollbar.
        _scrollbar = false;
    }

    // Default the window to 75% of the viewport. Runs inside an ImGui frame (the
    // instance is created from the menu hook), so GetMainViewport is valid; fall back
    // to a fixed size if it is not.
    private static float2 ComputeInitialSize()
    {
        try
        {
            float2 vp = ImGui.GetMainViewport().Size;
            if (vp.X > 200f && vp.Y > 200f)
                return new float2(vp.X * 0.75f, vp.Y * 0.75f);
        }
        catch
        {
            // GetMainViewport is unavailable outside a frame; use the fallback.
        }
        return new float2(1100f, 760f);
    }

    public void Open()
    {
        _show = true;
        _reevaluateRoot = true;
    }

    public void Close()
    {
        _show = false;
    }

    public override void DrawMenuBar()
    {
        base.DrawMenuBar();

        if (ImGui.BeginMenu("Zoom"u8))
        {
            if (ImGui.SmallButton("-"u8))
                ZoomAboutCenter(1.0 / 1.25);
            ImGui.SameLine(0f, 6f);
            ImGui.Text(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0}%", _zoom * 100.0));
            ImGui.SameLine(0f, 6f);
            if (ImGui.SmallButton("+"u8))
                ZoomAboutCenter(1.25);
            ImGui.SameLine(0f, 10f);
            if (ImGui.SmallButton("Fit"u8))
                _needsFit = true;
            ImGui.EndMenu();
        }

        if (ImGui.MenuItem("Fit"u8, default(ImString)))
            _needsFit = true;

        if (ImGui.MenuItem("Reset Root"u8, default(ImString)))
            _reevaluateRoot = true;

        if (ImGui.BeginMenu("Options"u8))
        {
            if (ImGui.BeginMenu("Layout"u8))
            {
                // Two engine layouts as radio choices, plus a view-only "center view".
                if (ImGui.MenuItem("Cumulative-down"u8, default(ImString), _layoutMode == LayoutMode.CumulativeDown))
                    SetLayoutMode(LayoutMode.CumulativeDown);
                if (ImGui.MenuItem("Gravity-well"u8, default(ImString), _layoutMode == LayoutMode.GravityWell))
                    SetLayoutMode(LayoutMode.GravityWell);
                if (ImGui.MenuItem("Spring"u8, default(ImString), _layoutMode == LayoutMode.Spring))
                    SetLayoutMode(LayoutMode.Spring);
                ImGui.Separator();
                if (ImGui.MenuItem("Center view on root"u8, default(ImString), _centerOnRoot))
                {
                    _centerOnRoot = !_centerOnRoot;
                    _needsFit = true;
                }
                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Full ladder everywhere"u8, default(ImString), _fullLadder))
            {
                _fullLadder = !_fullLadder;
                if (_currentRootId != null)
                    RebuildAt(_currentRootId);
            }
            ImGui.EndMenu();
        }

        if (_currentRootId != null)
            ImGui.TextDisabled(string.Concat("Root: ", _currentRootId));
    }

    // Switch the layout strategy and relayout the current root through the cached path.
    // A no-op if the mode is unchanged so reselecting the active radio item does nothing.
    private void SetLayoutMode(LayoutMode mode)
    {
        if (mode == _layoutMode)
            return;
        _layoutMode = mode;
        if (_currentRootId != null)
            RebuildAt(_currentRootId);
    }

    public override void DrawContent(Viewport viewport)
    {
        // The base class draws us between ImGui.Begin and ImGui.End. An exception
        // escaping here would skip End and leave the window/clip stacks unbalanced,
        // corrupting later frames, so contain everything (the same guarantee the
        // debug dumps already get for the render path). Each BeginChild is paired with
        // an EndChild in a finally so a throw inside one column cannot unbalance the
        // child stack either.
        try
        {
            if (!EnsureBuilt())
            {
                ImGui.TextDisabled("Delta-V map unavailable (no system loaded)."u8);
                return;
            }

            float2 avail = ImGui.GetContentRegionAvail();
            // Wide enough that the longest breakdown line ("Capture at <body> (ellipse):
            // ~X,XXX m/s") does not clip on the right.
            float panelWidth = (float)Math.Clamp(avail.X * 0.30, 320.0, 470.0);
            float canvasWidth = avail.X - panelWidth - 8f;

            // Below a sensible width the split is not worth it; show the canvas alone.
            if (canvasWidth < 220f)
            {
                DrawCanvas();
                return;
            }

            ImGui.BeginChild("##dvmap_canvas_col"u8, new float2?(new float2(canvasWidth, 0f)));
            try
            {
                DrawCanvas();
            }
            finally
            {
                ImGui.EndChild();
            }

            ImGui.SameLine();

            ImGui.BeginChild("##dvmap_panel_col"u8, new float2?(new float2(panelWidth, 0f)));
            try
            {
                DrawPanel();
            }
            finally
            {
                ImGui.EndChild();
            }
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("map-draw", $"[DvMap] Map draw failed: {ex}");
        }
    }

    // The route + options panel. A toggle here changes the options and recomputes the
    // route, so its effect lands from the next frame. The vehicle dV bar shows whenever a
    // vehicle is controlled: it is the vehicle's own total dV, useful context even when the
    // map is re-rooted away from where the vehicle currently is.
    private void DrawPanel()
    {
        PanelResult result = RoutePanelRenderer.Draw(_options, _view, _routeSummary, TryGetAvailableDv());
        // A visibility change rebuilds the tree (and re-resolves the selected route against
        // the new node set); a plain route-toggle change only re-accumulates the path.
        if (result.RebuildNeeded && _currentRootId != null)
            RebuildPreservingSelection(_currentRootId);
        else if (result.RouteChanged)
            RecomputeRoute();
    }

    // The canvas column. Assumes the build succeeded (DrawContent gates on EnsureBuilt);
    // a defensive null check keeps a throw out of the render path regardless.
    private void DrawCanvas()
    {
        LayoutResult? built = _layout;
        if (built == null)
            return;

        LayoutResult layout = built;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float2 origin = ImGui.GetCursorScreenPos();
        float2 size = ImGui.GetContentRegionAvail();
        _lastSize = size;
        if (size.X < 8f || size.Y < 8f)
            return;

        float2 canvasMax = origin + size;
        dl.AddRectFilled(in origin, in canvasMax, BackgroundColor);

        if (_needsFit)
        {
            FitToView(size);
            _needsFit = false;
        }

        var transform = new CanvasTransform(origin, _zoom, _panX, _panY, layout.MinX, layout.MinY);

        dl.PushClipRect(in origin, in canvasMax, intersectWithCurrentClipRect: true);
        try
        {
            CanvasRenderer.Draw(dl, layout, _lookup!, _palette!, in transform, _hoverId, _routeNodeIds,
                _options.IncludePlaneChange, _view.DvScale, _view.ShowTransferTimes, _view.ShowBodyMarkers);
        }
        finally
        {
            // Always pop, even on an unexpected throw, so the clip stack stays balanced.
            dl.PopClipRect();
        }

        // An invisible button over the whole canvas captures the left button, so a
        // left-drag pans the map instead of moving the window (only the title bar
        // moves it). Its return value is a click without a drag, used for selection.
        // AllowOverlap lets the layout switcher (submitted just after, on top) take its
        // own clicks instead of the canvas swallowing them.
        ImGui.SetNextItemAllowOverlap();
        bool clicked = ImGui.InvisibleButton("##dvmap_canvas"u8, in size);
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        if (hovered)
            ImGui.SetItemKeyOwner(ImGuiKey.MouseWheelY);

        // A compact layout switcher pinned to the top-left of the map, so the gravity-well
        // and cumulative-down layouts can be compared in one click without diving into the
        // Options menu. Its rect also suppresses canvas hover / pan / select underneath it,
        // so a click on the combo never leaks through to a node or a pan.
        bool overOverlay = DrawLayoutOverlay(origin);

        HandleInput(hovered && !overOverlay, active, clicked && !overOverlay, origin, in transform);
    }

    // Draw the on-canvas layout toggle: a small icon button in the top-left corner whose
    // glyph represents the active layout (a branching tree for CumulativeDown, a spine of
    // wells for GravityWell, a force-directed web for Spring). Clicking cycles to the next
    // layout via RebuildAt. Returns whether the mouse is over it, so the canvas suppresses
    // hover / pan / select beneath. Submitted after the canvas button (which allowed
    // overlap) so it takes its own clicks.
    private bool DrawLayoutOverlay(float2 origin)
    {
        const float margin = 8f;
        const float btn = 60f;

        ImGui.SetCursorScreenPos(origin + new float2(margin, margin));
        bool clicked = ImGui.InvisibleButton("##dvmap_layout"u8, new float2(btn, btn));
        bool hovered = ImGui.IsItemHovered();
        float2 min = ImGui.GetItemRectMin();
        float2 max = ImGui.GetItemRectMax();

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        DrawLayoutIcon(dl, min, max, _layoutMode, hovered);

        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.Text(string.Concat("Layout: ", LayoutModeLabel(_layoutMode), " (click to switch)"));
            ImGui.EndTooltip();
        }

        if (clicked)
            SetLayoutMode(NextLayoutMode(_layoutMode));

        float2 mouse = ImGui.GetMousePos();
        return mouse.X >= min.X && mouse.X <= max.X && mouse.Y >= min.Y && mouse.Y <= max.Y;
    }

    // Draw the toggle's frame plus a tiny glyph of the active layout. CumulativeDown is a
    // root branching down to two children; GravityWell is a horizontal spine with wells
    // hanging below; Spring is a central node with spokes to a ring of nodes. Pure DrawList
    // primitives so it needs no font glyphs and stays ASCII.
    private static void DrawLayoutIcon(ImDrawListPtr dl, float2 min, float2 max, LayoutMode mode, bool hovered)
    {
        byte4 bg = hovered ? new byte4(44, 54, 68, 245) : new byte4(28, 34, 44, 235);
        byte4 border = new byte4(120, 140, 160, 255);
        byte4 fg = new byte4(212, 222, 236, 255);
        dl.AddRectFilled(in min, in max, bg, 5f);
        dl.AddRect(in min, in max, border, 5f);

        // Glyph proportions scale with the button so doubling its size doubles the icon.
        float size = max.X - min.X;
        float pad = size * 0.24f;
        float dot = size * 0.07f;
        float line = MathF.Max(1.5f, size * 0.045f);
        float left = min.X + pad;
        float right = max.X - pad;
        float top = min.Y + pad;
        float bottom = max.Y - pad;

        if (mode == LayoutMode.GravityWell)
        {
            // A horizontal spine with wells hanging below it.
            float midY = (min.Y + max.Y) * 0.5f;
            var a = new float2(left, midY);
            var b = new float2(right, midY);
            dl.AddLine(in a, in b, fg, line);
            float span = right - left;
            for (int i = 0; i < 3; i++)
            {
                float x = left + span * (0.15f + 0.35f * i);
                var c = new float2(x, midY);
                var stub = new float2(x, bottom);
                dl.AddLine(in c, in stub, fg, line);
                dl.AddCircleFilled(in c, dot, fg);
            }
        }
        else if (mode == LayoutMode.Spring)
        {
            // A central node with spokes to a ring of nodes (a force-directed web).
            float cx = (min.X + max.X) * 0.5f;
            float cy = (min.Y + max.Y) * 0.5f;
            var center = new float2(cx, cy);
            float ringR = (right - left) * 0.42f;
            for (int i = 0; i < 3; i++)
            {
                double a = -1.5707963 + i * 2.0943951; // start up, 120 deg apart
                var outer = new float2(cx + ringR * (float)Math.Cos(a), cy + ringR * (float)Math.Sin(a));
                dl.AddLine(in center, in outer, fg, line);
                dl.AddCircleFilled(in outer, dot, fg);
            }
            dl.AddCircleFilled(in center, dot * 1.3f, fg);
        }
        else
        {
            // A root branching down to two children.
            float cx = (min.X + max.X) * 0.5f;
            var root = new float2(cx, top);
            var leftChild = new float2(left, bottom);
            var rightChild = new float2(right, bottom);
            dl.AddLine(in root, in leftChild, fg, line);
            dl.AddLine(in root, in rightChild, fg, line);
            dl.AddCircleFilled(in root, dot, fg);
            dl.AddCircleFilled(in leftChild, dot, fg);
            dl.AddCircleFilled(in rightChild, dot, fg);
        }
    }

    private static LayoutMode NextLayoutMode(LayoutMode mode)
    {
        return mode switch
        {
            LayoutMode.CumulativeDown => LayoutMode.GravityWell,
            LayoutMode.GravityWell => LayoutMode.Spring,
            _ => LayoutMode.CumulativeDown
        };
    }

    private static string LayoutModeLabel(LayoutMode mode)
    {
        return mode switch
        {
            LayoutMode.GravityWell => "Gravity-well",
            LayoutMode.Spring => "Spring",
            _ => "Cumulative-down"
        };
    }

    private void HandleInput(bool hovered, bool active, bool clicked, float2 origin, in CanvasTransform transform)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        float2 mouse = ImGui.GetMousePos();

        // Mouse-wheel zoom, centered on the cursor so the point under it stays put.
        if (hovered)
        {
            float wheel = io.MouseWheel;
            if (wheel != 0f)
            {
                io.MouseWheel = 0f;
                ZoomAbout(Math.Pow(1.1, wheel), mouse.X - origin.X, mouse.Y - origin.Y);
            }
        }

        // Pan with a left-drag (the invisible button is active and dragging) or a
        // middle-drag. The middle button does not activate the button, so latch it
        // from the press on the canvas until release.
        bool leftPan = active && ImGui.IsMouseDragging(ImGuiMouseButton.Left);
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
            _middlePanning = true;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle))
            _middlePanning = false;
        bool middlePan = _middlePanning && ImGui.IsMouseDragging(ImGuiMouseButton.Middle);
        if (leftPan || middlePan)
        {
            float2 delta = io.MouseDelta;
            _panX += delta.X;
            _panY += delta.Y;
        }

        // Hover highlight and tooltip, suppressed while panning.
        bool panning = leftPan || middlePan;
        LayoutNode? near = null;
        double distPx = double.MaxValue;
        if (hovered && !panning)
            near = NearestNode(mouse, in transform, out distPx);
        bool onNode = near != null && distPx <= HoverRadiusPx;
        _hoverId = onNode ? near!.Id : null;

        if (onNode)
        {
            ShowNodeTooltip(near!);
        }
        else if (hovered && !panning)
        {
            // Not over a node: a hover near an edge shows that segment's tooltip instead.
            LayoutEdge? edge = NearestEdge(mouse, in transform, out double edgeDist);
            if (edge != null && edgeDist <= EdgeHoverPx)
                ShowEdgeTooltip(edge);
        }

        // A click without a drag selects a route (or shift-clicks to re-root). A plain
        // click on a body highlights the path from "you are here" to it; clicking the
        // same node again clears it. A click on empty space is ignored, so a stray click
        // or a click meant to refocus the window does not throw away the selected route.
        if (clicked)
        {
            LayoutNode? hit = NearestNode(mouse, in transform, out double hitDist);
            if (hit != null && hitDist <= HoverRadiusPx)
            {
                if (io.KeyShift)
                {
                    ReRootTo(hit);
                }
                else
                {
                    bool selecting = hit.Id != _selectedId;
                    _selectedId = selecting ? hit.Id : null;
                    // Selecting a surface implies you want to land there, so tick the
                    // toggle to match (it then reads correctly and the bar includes it).
                    if (selecting && hit.Kind == LayoutKind.Surface)
                        _options.LandAtDestination = true;
                    RecomputeRoute();
                }
            }
        }
    }

    // Resolve the selected target into a route from the origin and accumulate it. Called
    // on selection and whenever a toggle changes. Clears the route when nothing valid is
    // selected (empty space, the star hub, or a build that has not produced a tree yet).
    private void RecomputeRoute()
    {
        _routeSummary = null;
        _routeNodeIds = null;

        if (_selectedId == null || _visualTree == null || _graph == null || _lookup == null)
            return;
        if (!_lookup.TryGetValue(_selectedId, out StateNode? clicked))
            return;

        StateNode? target = ResolveTarget(clicked);
        if (target == null)
            return;

        StateNode origin = ResolveOrigin();
        RoutePath? path = RouteFinder.FindPath(origin, target);
        if (path == null)
            return;

        try
        {
            RouteSummary summary = RouteAccumulator.Accumulate(path, _graph, _options);
            _routeSummary = summary;

            // A zero-step path means the target is the origin; keep the summary (the panel
            // says "already here") but do not dim the map for an empty highlight.
            if (path.Steps.Count == 0)
                return;

            var ids = new HashSet<string>(path.Nodes.Count);
            foreach (StateNode n in path.Nodes)
                ids.Add(n.Id);
            _routeNodeIds = ids;
        }
        catch (Exception ex)
        {
            // Routing must never unwind into the render path; surface and drop the route.
            LogHelper.ErrorOnce("route-accumulate", $"[DvMap] Route accumulation failed: {ex}");
        }
    }

    // The route origin: the root body's surface when "from surface" is on, otherwise the
    // "you are here" state, falling back to low orbit (or the tree root) when there is no
    // vehicle (e.g. in the editor), so the map still routes from a sensible default rather
    // than refusing to route.
    private StateNode ResolveOrigin()
    {
        VisualTree tree = _visualTree!;
        StateNode? you = tree.YouAreHere;
        StateNode? surface = FindBodyNode(tree.RootBodyId, StateKind.Surface);
        StateNode? lowOrbit = FindBodyNode(tree.RootBodyId, StateKind.LowOrbit);

        if (_options.FromSurface)
            return surface ?? lowOrbit ?? you ?? tree.Root;
        return you ?? lowOrbit ?? surface ?? tree.Root;
    }

    // Resolve the clicked node into the node the route should actually reach. A hub bus
    // routes to that hub body's low orbit (the star hub has none, so there is nothing to
    // route to). "Land at destination" extends an orbit click down to the body's surface.
    private StateNode? ResolveTarget(StateNode clicked)
    {
        StateNode target = clicked;

        if (target.Kind == StateKind.Hub)
        {
            StateNode? lo = FindBodyNode(target.Body.Id, StateKind.LowOrbit);
            if (lo == null)
                return null;
            target = lo;
        }

        if (_options.LandAtDestination
            && (target.Kind == StateKind.LowOrbit || target.Kind == StateKind.Intercept))
        {
            StateNode? surface = FindBodyNode(target.Body.Id, StateKind.Surface);
            if (surface != null)
                return surface;
        }

        return target;
    }

    private StateNode? FindBodyNode(string bodyId, StateKind kind)
    {
        if (_visualTree == null)
            return null;
        foreach (StateNode n in _visualTree.Nodes)
        {
            if (n.Kind == kind && n.Body.Id == bodyId)
                return n;
        }
        return null;
    }

    // The vehicle's total staged vacuum dV for the comparison bar: the controlled vehicle
    // in flight, else the vehicle under construction in the editor. Uses the mod's own
    // staged analyzer, not NavBallData.DeltaVInVacuum (a single-stage blend that badly
    // understates a staged vehicle). Null (bar shows n/a) when neither exists.
    private static double? TryGetAvailableDv()
    {
        double? dv = VehicleDvAnalyzer.TryControlledVehicleDv() ?? VehicleDvAnalyzer.TryEditorVehicleDv();
        return dv is double value && double.IsFinite(value) ? value : null;
    }

    private LayoutNode? NearestNode(float2 mouse, in CanvasTransform transform, out double distPx)
    {
        LayoutNode? best = null;
        double bestSq = double.MaxValue;
        foreach (LayoutNode node in _layout!.Tree.Nodes)
        {
            float2 p = transform.ToScreen(node.SnappedX, node.SnappedY);
            double dx = p.X - mouse.X;
            double dy = p.Y - mouse.Y;
            double sq = dx * dx + dy * dy;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = node;
            }
        }
        distPx = Math.Sqrt(bestSq);
        return best;
    }

    // The edge whose routed polyline runs nearest the cursor (hub links excluded, they carry
    // no dV). Used for the edge hover tooltip when the cursor is not over a node.
    private LayoutEdge? NearestEdge(float2 mouse, in CanvasTransform transform, out double distPx)
    {
        LayoutEdge? best = null;
        double bestSq = double.MaxValue;
        foreach (LayoutNode node in _layout!.Tree.Nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.IsHubLink || edge.Polyline.Count < 2)
                    continue;
                for (int i = 1; i < edge.Polyline.Count; i++)
                {
                    float2 a = transform.ToScreen(edge.Polyline[i - 1].X, edge.Polyline[i - 1].Y);
                    float2 b = transform.ToScreen(edge.Polyline[i].X, edge.Polyline[i].Y);
                    double sq = DistanceSqToSegment(mouse, a, b);
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        best = edge;
                    }
                }
            }
        }
        distPx = Math.Sqrt(bestSq);
        return best;
    }

    private static double DistanceSqToSegment(float2 p, float2 a, float2 b)
    {
        double abx = b.X - a.X;
        double aby = b.Y - a.Y;
        double apx = p.X - a.X;
        double apy = p.Y - a.Y;
        double len2 = abx * abx + aby * aby;
        double t = len2 > 0.0 ? Math.Clamp((apx * abx + apy * aby) / len2, 0.0, 1.0) : 0.0;
        double cx = a.X + t * abx;
        double cy = a.Y + t * aby;
        double dx = p.X - cx;
        double dy = p.Y - cy;
        return dx * dx + dy * dy;
    }

    // Rich node tooltip: resolve the layout node back to its game StateNode and the graph's
    // cached ladder. A purely synthetic node (no game body in the lookup) shows nothing.
    private void ShowNodeTooltip(LayoutNode node)
    {
        if (_lookup != null && _lookup.TryGetValue(node.Id, out StateNode? state))
            TooltipRenderer.Node(state, _graph?.LadderFor(state.Body.Id));
    }

    // Rich edge tooltip: resolve the layout edge back to its game edge for the fine segment
    // kind and formula; the layout edge supplies the displayed dV figures.
    private void ShowEdgeTooltip(LayoutEdge edge)
    {
        Edge? game = ResolveGameEdge(edge);
        if (game != null)
            TooltipRenderer.Edge(game, edge, _view.DvScale, _view.ShowTransferTimes);
    }

    // Find the game edge backing a layout edge by matching its endpoints in the visual tree.
    private Edge? ResolveGameEdge(LayoutEdge layoutEdge)
    {
        if (_lookup == null || !_lookup.TryGetValue(layoutEdge.From.Id, out StateNode? from))
            return null;
        foreach (Edge e in from.Out)
        {
            if (e.To.Id == layoutEdge.To.Id)
                return e;
        }
        return null;
    }

    // Shift-click re-root: rebuild the visual tree and layout at the clicked body. The
    // node Id is "<body>.<state>"; resolve the body through the lookup and the graph.
    private void ReRootTo(LayoutNode node)
    {
        if (_lookup == null || _graph == null)
            return;
        if (!_lookup.TryGetValue(node.Id, out StateNode? state))
            return;
        string bodyId = state.Body.Id;
        if (bodyId == _currentRootId || _graph.Find(bodyId) == null)
            return;
        RebuildAt(bodyId);
    }

    // Ensure the graph, cache and a laid-out tree exist for the current system and
    // root. Returns false only when there is nothing to draw (no system yet).
    private bool EnsureBuilt()
    {
        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
            return false;

        if (_graph == null || _graph.SystemId != system.Id)
        {
            SystemGraph? graph = SystemGraph.Build(system);
            if (graph == null)
                return false;
            _graph = graph;
            _cache = new DvCache();
            _palette = ColorPalette.Build(graph);
            _currentRootId = null;
            _buildFailedRootId = null;
            _selectedId = null;
            _routeSummary = null;
            _routeNodeIds = null;
            _built = false;
        }

        if (!_built || _reevaluateRoot)
        {
            _reevaluateRoot = false;
            string? desired = DesiredEgoRootId(system);
            if (desired == null)
                return _built;
            // Skip a root whose last build threw so we do not rebuild a ~130-node tree
            // every frame behind the "unavailable" message. A detail toggle or a
            // system/root change clears the latch and retries.
            if ((!_built || desired != _currentRootId) && desired != _buildFailedRootId)
                RebuildAt(desired);
        }

        return _built;
    }

    // The ego root: the controlled vehicle's current SOI body, or the system home body
    // when there is no vehicle. Falls back to the star if neither resolves in the graph.
    private string? DesiredEgoRootId(CelestialSystem system)
    {
        string? parentId = TryGetEgoParentId();
        if (parentId != null && _graph!.Find(parentId) != null)
            return parentId;
        if (system.HomeBody?.Id is string homeId && _graph!.Find(homeId) != null)
            return homeId;
        return _graph!.Root.Id;
    }

    // Reading the controlled vehicle's parent body walks its orbit / flight-plan chain,
    // which can throw for a vehicle without a materialized patch (freshly spawned, mid
    // transition). This runs inside the ImGui Begin/End block, so a throw must not
    // escape; treat any failure as "no ego root".
    private static string? TryGetEgoParentId()
    {
        try
        {
            return Program.ControlledVehicle?.Parent?.Id;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("ego-parent", $"[DvMap] Could not read controlled vehicle parent: {ex.Message}");
            return null;
        }
    }

    // Rebuild at a root while keeping the view anchored and the selected route. A visibility
    // toggle should declutter in place, but RebuildAt re-runs the layout: removing or adding
    // bodies reflows the tidy tree (sibling columns repack) and shifts the bounding box, so
    // restoring the raw pan verbatim would make the whole map jump under a fixed viewport.
    // Instead capture where the root sits on screen now and solve pan after the rebuild to
    // keep it on that same point, so the root stays put and only the rest reflows around it.
    // (screen = origin + (layout - min) * zoom + pan; origin is constant here, so it cancels.)
    private void RebuildPreservingSelection(string rootId)
    {
        string? selected = _selectedId;
        double zoom = _zoom;

        double anchorX = 0.0;
        double anchorY = 0.0;
        bool haveAnchor = _layout != null;
        if (haveAnchor)
        {
            LayoutNode oldRoot = _layout!.Tree.Root;
            anchorX = (oldRoot.SnappedX - _layout.MinX) * zoom + _panX;
            anchorY = (oldRoot.SnappedY - _layout.MinY) * zoom + _panY;
        }

        RebuildAt(rootId);

        _zoom = zoom;
        if (haveAnchor && _layout != null)
        {
            LayoutNode newRoot = _layout.Tree.Root;
            _panX = anchorX - (newRoot.SnappedX - _layout.MinX) * zoom;
            _panY = anchorY - (newRoot.SnappedY - _layout.MinY) * zoom;
        }
        _needsFit = false;

        if (selected != null && _lookup != null && _lookup.ContainsKey(selected))
        {
            _selectedId = selected;
            RecomputeRoute();
        }
    }

    private void RebuildAt(string rootId)
    {
        if (_graph == null || _cache == null)
            return;

        PhysicalNode node = _graph.Find(rootId) ?? _graph.Root;

        ClassifiedState? egoState = null;
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle != null && TryGetEgoParentId() == node.Id)
        {
            try
            {
                egoState = StateClassifier.Classify(vehicle, node.Ladder);
            }
            catch (Exception ex)
            {
                LogHelper.WarnOnce("classify-" + node.Id, $"[DvMap] Could not classify vehicle state at '{node.Id}': {ex.Message}");
            }
        }

        try
        {
            var buildOptions = new BuildOptions(_fullLadder, _view.ShowMinorBodies, _view.ShowComets);
            VisualTree visual = VisualTree.Build(_graph, _cache, node, egoState, buildOptions);
            LayoutTree tree = VisualTreeAdapter.ToLayoutTree(visual, _graph);
            var cfg = new LayoutConfig { Mode = _layoutMode };
            LayoutResult result = LayoutEngine.Run(tree, cfg, MeasureText);

            var lookup = new Dictionary<string, StateNode>(visual.Nodes.Count);
            foreach (StateNode n in visual.Nodes)
                lookup[n.Id] = n;

            _visualTree = visual;
            _layout = result;
            _lookup = lookup;
            _currentRootId = node.Id;
            _buildFailedRootId = null;
            _selectedId = null;
            _routeSummary = null;
            _routeNodeIds = null;
            _needsFit = true;
            _built = true;
        }
        catch (Exception ex)
        {
            _buildFailedRootId = node.Id;
            LogHelper.ErrorOnce("map-build-" + rootId, $"[DvMap] Map build failed for root '{rootId}': {ex}");
        }
    }

    // Real label width from the active ImGui font. Valid because every RebuildAt call
    // happens inside the draw loop (DrawContent, the menu bar, or a canvas click).
    private static double MeasureText(string label)
    {
        return ImGui.CalcTextSize(label).X;
    }

    private void FitToView(float2 size)
    {
        LayoutResult layout = _layout!;
        const double pad = 32.0;
        double contentW = Math.Max(1.0, layout.Width);
        double contentH = Math.Max(1.0, layout.Height);
        double zx = (size.X - 2.0 * pad) / contentW;
        double zy = (size.Y - 2.0 * pad) / contentH;
        _zoom = Math.Clamp(Math.Min(zx, zy), MinZoom, MaxZoom);

        if (_centerOnRoot)
        {
            // Put the root node at the viewport center instead of centering the bounding
            // box. The transform maps (lx - MinX) * zoom + pan to screen-relative pixels,
            // so solve pan to land the root on the center. In GravityWell the root sits on
            // the spine, so this also vertically centers the whole spine.
            LayoutNode root = layout.Tree.Root;
            _panX = size.X / 2.0 - (root.SnappedX - layout.MinX) * _zoom;
            _panY = size.Y / 2.0 - (root.SnappedY - layout.MinY) * _zoom;
        }
        else
        {
            _panX = (size.X - contentW * _zoom) / 2.0;
            _panY = (size.Y - contentH * _zoom) / 2.0;
        }
    }

    private void ZoomAboutCenter(double factor)
    {
        if (_lastSize.X > 0f)
            ZoomAbout(factor, _lastSize.X / 2.0, _lastSize.Y / 2.0);
        else
            _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
    }

    // Zoom keeping the layout point currently under (relX, relY) - canvas-relative
    // pixels, i.e. excluding the origin - fixed on screen.
    private void ZoomAbout(double factor, double relX, double relY)
    {
        double newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (newZoom == _zoom)
            return;
        _panX = relX - (relX - _panX) / _zoom * newZoom;
        _panY = relY - (relY - _panY) / _zoom * newZoom;
        _zoom = newZoom;
    }
}
