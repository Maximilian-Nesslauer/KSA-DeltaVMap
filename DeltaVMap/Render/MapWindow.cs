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
// system, a re-rooted visual tree and a laid-out scene per root. The graph and cache
// survive while the same system is loaded; the visual tree and the ego layout are rebuilt
// on re-root or on a detail change, and the layout is measured with real ImGui text
// metrics (only available here, inside the draw loop). The other star systems are laid out
// as separate parts beside the ego map, collapsed to one node unless they hold the selected
// route target or the searched body, so opening one lays out only that part.
internal sealed class MapWindow : ImGuiWindow
{
    // The default Earth root is a ~132-node tree that does not fit at 25%, so the zoom
    // floor sits lower and auto-fit can show the whole map on open.
    private const double MinZoom = 0.1;
    private const double MaxZoom = 4.0;
    private const double HoverRadiusPx = 14.0;

    // A search pick zooms in to at least this so the found body is legible, and pans it to the
    // viewport center.
    private const double FocusZoom = 1.0;
    // Universal never-hang ceiling: above this many laid-out nodes the map refuses to build
    // (any mode) and shows a note, so no build or per-frame loop can freeze the game. Set well
    // above any real aggregated system (stock and the dense system both land near stock size
    // after minor-body aggregation); only a pathological all-major system would reach it.
    private const int MaxLayoutNodes = 8000;

    // Cap on listed search matches; a broader query shows a "+N more" note and asks to refine,
    // so the panel never renders thousands of rows.
    private const int MaxSearchResults = 40;
    // Visible result rows before the list scrolls (the list child is sized to this).
    private const int MaxVisibleSearchRows = 7;
    // Search text buffer capacity in characters; body names are short.
    private const int SearchBufferCapacity = 64;

    // How close (screen px) the cursor must be to an edge polyline to show its tooltip.
    // Smaller than the node radius so a node always wins a hover near a junction.
    private const double EdgeHoverPx = 7.0;

    // How far inside the canvas edge (screen px) the anchor of a scene change must stay for the
    // view to hold still when a star system opens or closes.
    private const double AnchorMarginPx = 40.0;

    // Thickness (screen px) of the invisible drag strips on the transfer-window panel edges.
    // Sits just outside the bordered child so the child window cannot occlude the grab.
    private const float ResizeGrip = 8f;

    // The Transfer-windows overlay fill: a touch lighter than the canvas, near-opaque so the
    // map does not bleed through the text.
    private static readonly byte4 TransferOverlayBg = new byte4(24, 30, 40, 238);

    // The overlay resize grips: a faint hint on the edge, brightening while hovered or dragged.
    private static readonly byte4 ResizeGripIdle = new byte4(120, 140, 165, 90);
    private static readonly byte4 ResizeGripHot = new byte4(185, 208, 236, 235);

    private static MapWindow? _instance;

    // Per-system state (rebuilt when Universe.CurrentSystem changes).
    private SystemGraph? _graph;
    private DvCache? _cache;
    private ColorPalette? _palette;

    // Per-root state (rebuilt on re-root or detail change). _visualTree backs routing:
    // RouteFinder walks its StateNode tree from the origin to the clicked target, and
    // _lookup resolves a clicked node Id to its StateNode. _egoLayout is the ego system as
    // LayoutEngine laid it out; _scene adds the other star systems beside it.
    private VisualTree? _visualTree;
    private LayoutResult? _egoLayout;
    private LayoutNode? _egoHub;
    private LayoutScene? _scene;
    private Dictionary<string, StateNode>? _lookup;
    private string? _currentRootId;
    private string? _rootLabel;

    // The destination parts, laid out once per root build and kept until the next one, so
    // opening or closing a system relays out only that part. _sceneById resolves a node Id to
    // its node in the composed scene. The two expanded systems are the one holding the
    // selected node and the one holding the searched body.
    private readonly Dictionary<(string RootId, bool Expanded), LayoutPart> _parts = new();
    private readonly List<LayoutPart> _shownParts = new();
    private readonly Dictionary<string, LayoutNode> _sceneById = new();
    private string? _expandedA;
    private string? _expandedB;

    // The star system the map shows, by its root Id, or null to follow the controlled vessel
    // (its system, else the home system). Set from the System menu; Reset Root returns to
    // following.
    private string? _mapSystemId;

    // The interstellar leg of the selected route, and the reusable drawing and panel parts.
    private readonly InterstellarSection _interstellar = new();
    private readonly CanvasRenderer _canvas = new();
    private readonly TooltipRenderer _tooltips = new();
    private readonly RoutePanelRenderer _routePanel = new();
    private readonly TransferWindowRenderer _windowRenderer = new();
    private readonly TransferWindowMapOverlay _mapOverlay = new();

    // Root whose last build threw, so EnsureBuilt does not re-attempt it every frame.
    private string? _buildFailedRootId;
    private bool _built;

    // On the next build, re-evaluate the ego root. Set when the window is opened, on Reset
    // Root, on a System pick, and when the controlled vehicle changes. While the map follows
    // the vessel it also re-roots when the vessel crosses into another star system (the
    // reference compares in FollowVessel); an SOI change inside one system waits for the next
    // of these.
    private bool _reevaluateRoot;

    // The controlled vehicle and its parent body as last seen, so FollowVessel acts only on a
    // change and never overrides a manual re-root or a System pick on its own.
    private Vehicle? _egoVehicle;
    private IParentBody? _egoParent;

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

    // Search / focus / isolate. _searchBuffer backs the panel input; matches are taken over the
    // full graph (every body, even ones aggregated into a "+N" group). A pick adds the body to
    // _revealedBodyIds (force-shown as its own lane on the next build), sets _focusNodeId (the
    // highlighted, centered node), and isolate strips the map down to the spine, the major
    // bodies, the revealed bodies and the selected route.
    private readonly ImInputString _searchBuffer = new ImInputString(SearchBufferCapacity);
    private string _lastSearchQuery = "";
    private readonly List<PhysicalNode> _searchResults = new();
    private readonly List<string> _searchLabels = new();
    private int _searchMatchTotal;
    private readonly HashSet<string> _revealedBodyIds = new();
    private string? _focusBodyId;
    private string? _focusNodeId;
    private bool _isolate;

    // Set (to the node count) when a build was refused for exceeding MaxLayoutNodes; drives the
    // "too large" note in place of the canvas. Zero when the current build laid out normally.
    private int _oversizedCount;
    private string _oversizedNote = "";

    // Texts kept until the number they show changes.
    private readonly CachedText _zoomText = new();
    private readonly CachedText _moreText = new();
    private bool _queryBlank = true;

    // The clicked route target Id (null = no route). Plain click sets it; the route is
    // accumulated from it into _routeSummary, and _routeNodeIds is the set of node Ids
    // on the path, used by the renderer to highlight the route and dim everything else.
    private string? _selectedId;
    private RouteSummary? _routeSummary;
    private HashSet<string>? _routeNodeIds;
    private readonly RouteOptions _options = new();

    // Transfer-window timing overlay, separate from the route / layout pipeline. The list is
    // rebuilt on root or visibility change (it mirrors the bodies the map shows); its two live
    // fields (current phase, countdown) refresh every frame. The overlay floats bottom-left in
    // the canvas; its collapsed / expanded state is its own.
    private readonly List<TransferWindowInfo> _windows = new();
    private bool _windowsOverlayExpanded;

    // The on-map window markers, parallel to _windows: the marker node of each window (resolved
    // once per build), its countdown, and the countdown text with its width, rewritten only when
    // the shown value moves.
    private LayoutNode?[] _markerNodes = Array.Empty<LayoutNode?>();
    private double[] _markerSeconds = Array.Empty<double>();
    private string[] _markerTexts = Array.Empty<string>();
    private float[] _markerWidths = Array.Empty<float>();
    private long[] _markerKeys = Array.Empty<long>();
    // The sibling whose clock dot or list row the overlay is hovering, or null. Drives the map
    // highlight, and is fed back in next frame so the hovered body also lights its clock dot.
    private string? _windowsHoverBodyId;
    // Whether the inline transfer-window markers are drawn on the map (off by default; the toggle
    // lives in the list panel). Dense roots are why it defaults off.
    private bool _showWindowMarkers;
    // Whether the map-mode (the game's 3D orbit view) overlay is drawn: the optimal-departure
    // markers on the real orbits plus the ejection-angle gizmo. Off by default; its toggle also
    // lives in the list panel. Independent of _showWindowMarkers (the metro-map badges); this one
    // only draws while the game camera is in map mode.
    private bool _showMapMarkers;
    // The sibling on the selected route's interplanetary leg, or null. Biases the clock / list
    // emphasis when nothing is hovered (emphasis order: hovered, else this, else soonest).
    private string? _routeSiblingId;

    // User-chosen overlay panel sizes from dragging their edges; null = auto (the per-frame
    // computed size). Reset to auto on a root or system change (in RebuildAt); clamped to the
    // canvas and sane minimums each frame for use, without writing the clamp back, so shrinking
    // then re-enlarging the window restores the chosen size. _resizeStartValue holds the panel
    // size captured when a drag begins, so the drag tracks the cumulative mouse delta from a
    // stable origin even as the panel repositions under it.
    private float? _listWidthOverride;
    private float? _listHeightOverride;
    private float? _clockSideOverride;
    private float _resizeStartValue;

    // Display and visibility settings (visibility rebuilds the tree; the rest are display
    // only). Shared with the panel, which edits it, and read by the canvas renderer.
    private readonly ViewOptions _view = new();
    private bool _middlePanning;
    private bool _titleApplied;

    public static MapWindow Instance => _instance ??= new MapWindow();

    // Never creates the instance: the draw hook must not touch ImGui before the user opens the map.
    public static void DrawActive(IGameViewport viewport)
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
            long percent = (long)Math.Round(_zoom * 100.0);
            ImGui.Text(_zoomText.IsStale(null, percent)
                ? _zoomText.Set(null, percent, percent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%")
                : _zoomText.Text);
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
        {
            _mapSystemId = null;
            _reevaluateRoot = true;
        }

        if (_graph != null && _graph.HasSeveralSystems && ImGui.BeginMenu("System"u8))
        {
            if (ImGui.MenuItem("Follow vessel"u8, default(ImString), _mapSystemId == null))
                SelectSystem(null);
            ImGui.Separator();
            string? shown = CurrentSystemRoot()?.Id;
            foreach (PhysicalNode root in _graph.Roots)
            {
                if (ImGui.MenuItem(root.Id, default(ImString), root.Id == shown))
                    SelectSystem(root.Id);
            }
            ImGui.EndMenu();
        }

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

        if (_rootLabel != null)
            ImGui.TextDisabled(_rootLabel);
    }

    // Show another star system (or follow the vessel again with null). The search and focus
    // belong to the old view, so they are cleared without a rebuild of their own; the next
    // EnsureBuilt picks the root through DesiredEgoRootId and rebuilds once.
    private void SelectSystem(string? rootId)
    {
        _mapSystemId = rootId;
        ClearSearch(rebuild: false);
        _reevaluateRoot = true;
    }

    // The system root of the current map root, or null before the first build.
    private PhysicalNode? CurrentSystemRoot()
    {
        PhysicalNode? node = _currentRootId != null ? _graph?.Find(_currentRootId) : null;
        return node != null ? SystemGraph.SystemRootOf(node) : null;
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

    public override void DrawContent(IViewport viewport)
    {
        // A throw escaping here skips the base class's ImGui.End and corrupts the window,
        // clip and child stacks for every later frame. Hence the catch and the finally pairs.
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

            // Too narrow to split; canvas alone.
            if (canvasWidth < 220f)
            {
                DrawCanvas(viewport);
                return;
            }

            ImGui.BeginChild("##dvmap_canvas_col"u8, new float2?(new float2(canvasWidth, 0f)));
            try
            {
                DrawCanvas(viewport);
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

    // The find section and the route panel. A toggle here changes the options and recomputes
    // the route, so its effect lands from the next frame. The vehicle dV bar shows whenever a
    // vehicle is controlled: it is the vehicle's own total dV, useful context even when the
    // map is re-rooted away from where the vehicle currently is. A route with an interstellar
    // leg first brings the leg up to date for this frame, so its breakdown lines, the totals
    // and the bar include it; the cruise speed and the vessel's burn change only the leg.
    private void DrawPanel()
    {
#if DEBUG
        using var perf = new PerfTracker.Scope("DvMap.Draw.Panel");
#endif
        double? availableDv = TryGetAvailableDv();
        DrawSearchPanel();

        InterstellarSection? section = null;
        bool routeStale = false;
        if (_routeSummary?.Interstellar is InterstellarRoute leg && _graph != null)
        {
            routeStale = _interstellar.Update(_graph, leg, availableDv, _view.DvScale, _options.FromSurface);
            section = _interstellar;
        }

        PanelResult result = _routePanel.Draw(_options, _view, _routeSummary, availableDv, section, _graph?.HasSeveralSystems == true);
        // A visibility change rebuilds the tree (and re-resolves the selected route against
        // the new node set); a plain route-toggle change only re-accumulates the path, as does
        // a vessel that parked at or left the departure body of an interstellar route.
        if (result.RebuildNeeded && _currentRootId != null)
            RebuildPreservingSelection(_currentRootId);
        else if (result.RouteChanged || routeStale)
            RecomputeRoute();
    }

    // The Transfer-windows overlay, floated bottom-left inside the canvas (mirroring the top-
    // left layout toggle): a collapsible list panel plus, when expanded, a second square panel
    // beside it (to the right, falling back to above) holding the clock-face large enough to
    // read. Collapsed it is a one-line footer toggle naming the next window; expanded the
    // list panel adds the markers toggle and the compact per-sibling list above the toggle, and
    // the clock panel shows the polar diagram. Returns whether the mouse is over either panel so
    // the canvas suppresses hover / pan / select underneath. Submitted after the canvas button
    // (which allowed overlap) so it takes its own clicks; each panel is guarded on its own so a
    // throw never reaches the render path, and the child / table / style stacks stay balanced.
    private bool DrawTransferWindowOverlay(float2 origin, float2 size)
    {
        // The live fields were refreshed once in DrawCanvas (before the on-map markers). The
        // emphasized sibling is the one being hovered, else the one the selected route departs
        // to, else (in the renderer) the soonest. _routeSiblingId is cleared wherever the route
        // is, so it is non-null only while a route to a sibling is selected.
        string? emphasisHint = _windowsHoverBodyId ?? _routeSiblingId;

        const float margin = 8f;
        const float gap = 6f;

        // The canvas envelope every panel size is clamped into, and the per-panel minimums. A
        // manual resize (the drag handles below) may push a panel past its auto size but never
        // past these bounds.
        float canvasMaxW = size.X - 2f * margin;
        float canvasMaxH = size.Y - 2f * margin;
        const float listMinW = 240f;
        const float listMinH = 90f;
        const float clockMin = 180f;

        // List width: the user's dragged width if set, else 30% of the canvas (the original auto
        // rule), clamped to the canvas.
        float autoWidth = Math.Clamp(size.X * 0.30f, 340f, 480f);
        float width = ClampSize(_listWidthOverride ?? autoWidth, listMinW, canvasMaxW);
        if (width < 220f || size.Y < 160f)
            return false;

        float f = ImGui.GetFrameHeight();
        float collapsedH = f + 16f;
        float topRoom = size.Y - 2f * margin - 76f;

        // The clock panel is a square. Auto-sized it is as large as the canvas height allows; the
        // user can drag it larger (past the auto 460 cap) or smaller, never past the canvas. When
        // it fits to the right of the list, the list panel is grown to the same height so the two
        // read as a matched pair (bottom-aligned), unless the user has dragged the list to its own
        // height; otherwise the list keeps its content height and the clock stacks above it (or is
        // dropped on a tiny canvas).
        float autoClock = Math.Clamp(Math.Min(topRoom, 460f), 200f, 460f);
        float clockSide = ClampSize(_clockSideOverride ?? autoClock, clockMin, Math.Min(canvasMaxW, canvasMaxH));
        bool clockRight = _windowsOverlayExpanded && _windows.Count > 0
            && origin.X + margin + width + gap + clockSide <= origin.X + size.X - margin;

        // List-panel content height: room for the markers toggle, the source line, the list and
        // the footer toggle. The list scrolls internally if it would exceed this. A dragged list
        // height overrides both the matched-pair height and the content height.
        int rows = Math.Min(_windows.Count, 10);
        float autoContentH = Math.Clamp((rows + 5) * f + 28f, collapsedH, Math.Min(topRoom, 460f));
        float baseListH = (clockRight && _listHeightOverride == null) ? clockSide : autoContentH;
        float expandedH = ClampSize(_listHeightOverride ?? baseListH, listMinH, canvasMaxH);
        float height = !_windowsOverlayExpanded ? collapsedH : expandedH;

        // Anchor the list panel's bottom-left near the canvas corner; the toggle is pinned at its
        // bottom, so it stays put while the detail above grows upward on expand or resize.
        var listPos = new float2(origin.X + margin, origin.Y + size.Y - margin - height);

        float2? clockPos = null;
        if (_windowsOverlayExpanded && _windows.Count > 0)
        {
            if (clockRight)
                clockPos = new float2(listPos.X + width + gap, origin.Y + size.Y - margin - clockSide);
            else if (listPos.Y - gap - clockSide >= origin.Y + margin)
                clockPos = new float2(listPos.X, listPos.Y - gap - clockSide);
            else if (_clockSideOverride != null)
            {
                // A manually enlarged clock that will not fit stacked is shrunk to the room above
                // the list rather than hidden, so its resize handle stays reachable and the user
                // can drag it back down. (Auto-sized, it is simply dropped on a tiny canvas, as
                // before - the two branches above are unchanged.)
                float stackRoom = listPos.Y - gap - (origin.Y + margin);
                if (stackRoom >= clockMin)
                {
                    clockSide = stackRoom;
                    clockPos = new float2(listPos.X, listPos.Y - gap - clockSide);
                }
            }
        }
        bool clockHidden = _windowsOverlayExpanded && _windows.Count > 0 && clockPos == null;

        string? hover = null;
        bool hotEdge = false;

        ImGui.SetCursorScreenPos(listPos);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, TransferOverlayBg);
        try
        {
            ImGui.BeginChild("##dvtwlistpanel"u8, new float2?(new float2(width, height)), ImGuiChildFlags.Borders);
            try
            {
                OverlayResult result = _windowRenderer.DrawOverlay(
                    _windows, ref _showWindowMarkers, ref _showMapMarkers, _windowsOverlayExpanded, emphasisHint, clockHidden);
                hover = result.HoverBodyId;
                if (result.Toggled)
                    _windowsOverlayExpanded = !_windowsOverlayExpanded;
            }
            finally
            {
                ImGui.EndChild();
            }
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("twindow-overlay", $"[DvMap] Transfer window overlay failed: {ex}");
        }
        finally
        {
            // Pop even if BeginChild threw, so the style-color stack stays balanced for the frame.
            ImGui.PopStyleColor();
        }

        // Drag handles on the list panel's free edges, only while expanded (collapsed it is a one-
        // line footer with nothing to resize). The right edge grows the width; the top edge grows
        // the height upward so the bottom-pinned footer never moves. Placed just outside the
        // border so the bordered child window does not occlude their hover, and submitted after
        // the canvas button (which allowed overlap) so a drag takes its own clicks instead of
        // panning the map or selecting a node. Signature: (id, pos, size, resizesWidth,
        // startValue, grow, min, max, ref hot).
        if (_windowsOverlayExpanded)
        {
            float? nw = EdgeHandle("##dvtw_listw"u8, new float2(listPos.X + width, listPos.Y),
                new float2(ResizeGrip, height), true, width, +1f, listMinW, canvasMaxW, ref hotEdge);
            if (nw.HasValue)
                _listWidthOverride = nw;

            float? nh = EdgeHandle("##dvtw_listh"u8, new float2(listPos.X, listPos.Y - ResizeGrip),
                new float2(width, ResizeGrip), false, height, -1f, listMinH, canvasMaxH, ref hotEdge);
            if (nh.HasValue)
                _listHeightOverride = nh;
        }

        bool overList = MouseInRect(listPos, width, height);
        bool overClock = false;

        // The clock panel (placement decided above), a square frame holding the diagram.
        if (clockPos is float2 cp)
        {
            ImGui.SetCursorScreenPos(cp);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, TransferOverlayBg);
            try
            {
                ImGui.BeginChild("##dvtwclockpanel"u8, new float2?(new float2(clockSide, clockSide)), ImGuiChildFlags.Borders);
                try
                {
                    string? ch = _windowRenderer.DrawClockPanel(_windows, _palette, emphasisHint);
                    if (ch != null)
                        hover = ch;
                }
                finally
                {
                    ImGui.EndChild();
                }
            }
            catch (Exception ex)
            {
                LogHelper.ErrorOnce("twindow-clock", $"[DvMap] Transfer window clock failed: {ex}");
            }
            finally
            {
                ImGui.PopStyleColor();
            }
            overClock = MouseInRect(cp, clockSide, clockSide);

            // The clock's top edge resizes the square (drag up to enlarge), keeping its bottom-
            // left corner anchored; the side also drives the matched list height. Placed just
            // above the frame so the bordered child does not occlude the grab.
            float? ns = EdgeHandle("##dvtw_clock"u8, new float2(cp.X, cp.Y - ResizeGrip),
                new float2(clockSide, ResizeGrip), false, clockSide, -1f, clockMin,
                Math.Min(canvasMaxW, canvasMaxH), ref hotEdge);
            if (ns.HasValue)
                _clockSideOverride = ns;
        }

        _windowsHoverBodyId = hover;
        return overList || overClock || hotEdge;
    }

    // Clamp a panel dimension into [min, max], except when the canvas is smaller than the minimum,
    // where the canvas wins so the panel shrinks to fit rather than overflow off the edge.
    private static float ClampSize(float value, float min, float max)
    {
        if (max < min)
            return max;
        return Math.Clamp(value, min, max);
    }

    // A thin invisible drag strip straddling one free edge of an overlay panel. While hovered or
    // held it shows the matching resize cursor and a brighter grip line, and marks `hot` so the
    // canvas underneath stays suppressed. On the frame the drag begins it captures the panel's
    // current size; each later frame it returns that captured size plus the cumulative mouse delta
    // along the handle's axis (so the drag tracks a stable origin even as the panel moves under
    // it). resizesWidth picks the horizontal axis and the east-west cursor; grow is +1 when a drag
    // toward larger screen coordinates enlarges the panel (the list's right edge) and -1 when it
    // shrinks them (a top edge of a bottom-anchored panel, where dragging up enlarges). Returns the
    // clamped new size while held, else null.
    private float? EdgeHandle(ReadOnlySpan<byte> id, float2 pos, float2 sz, bool resizesWidth,
        float startValue, float grow, float min, float max, ref bool hot)
    {
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton(id, in sz);
        bool active = ImGui.IsItemActive();
        bool thisHot = ImGui.IsItemHovered() || active;
        if (thisHot)
        {
            ImGui.SetMouseCursor(resizesWidth ? ImGuiMouseCursor.ResizeEW : ImGuiMouseCursor.ResizeNS);
            hot = true;
        }

        // A grip hint centered on the strip: a short faint segment normally, a brighter full-length
        // line while hovered or dragged, so the affordance reads without cluttering the border.
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        byte4 col = thisHot ? ResizeGripHot : ResizeGripIdle;
        float thickness = thisHot ? 2.5f : 2f;
        if (resizesWidth)
        {
            float x = pos.X + sz.X * 0.5f;
            float y0 = thisHot ? pos.Y : (pos.Y + sz.Y * 0.5f - 14f);
            float y1 = thisHot ? pos.Y + sz.Y : (pos.Y + sz.Y * 0.5f + 14f);
            var a = new float2(x, y0);
            var b = new float2(x, y1);
            dl.AddLine(in a, in b, col, thickness);
        }
        else
        {
            float y = pos.Y + sz.Y * 0.5f;
            float x0 = thisHot ? pos.X : (pos.X + sz.X * 0.5f - 14f);
            float x1 = thisHot ? pos.X + sz.X : (pos.X + sz.X * 0.5f + 14f);
            var a = new float2(x0, y);
            var b = new float2(x1, y);
            dl.AddLine(in a, in b, col, thickness);
        }

        if (ImGui.IsItemActivated())
            _resizeStartValue = startValue;
        if (active)
        {
            float2 d = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left, 0f);
            float delta = resizesWidth ? d.X : d.Y;
            return Math.Clamp(_resizeStartValue + grow * delta, min, max);
        }
        return null;
    }

    private static bool MouseInRect(float2 pos, float w, float h)
    {
        float2 m = ImGui.GetMousePos();
        return m.X >= pos.X && m.X <= pos.X + w && m.Y >= pos.Y && m.Y <= pos.Y + h;
    }

    // Recompute the two live fields (current phase, countdown) for the current windows at the
    // current sim time. Cheap (~2 Kepler reads per sibling); a failed time read leaves the
    // last values in place rather than throwing into the panel.
    private void RefreshTransferWindows()
    {
        if (_windows.Count == 0)
            return;
        try
        {
            TransferWindows.RefreshAll(_windows, Universe.GetElapsedTime());
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("twindow-refresh", $"[DvMap] Transfer window refresh failed: {ex}");
        }
    }

    // The on-map window markers for this frame: the countdowns refreshed, and a text rebuilt
    // only for a window whose shown countdown moved. Empty when the toggle is off, so the canvas
    // skips the marker pass entirely.
    private WindowMarkerSet BuildWindowMarkers()
    {
        if (!_showWindowMarkers || _windows.Count == 0 || _markerNodes.Length < _windows.Count)
            return default;
        for (int i = 0; i < _windows.Count; i++)
        {
            double seconds = _windows[i].TimeToWindowSeconds;
            _markerSeconds[i] = seconds;
            long key = Format.WindowTimeKey(seconds);
            if (key != _markerKeys[i] || _markerTexts[i] == null)
            {
                _markerKeys[i] = key;
                _markerTexts[i] = "~" + Format.WindowTime(seconds);
                _markerWidths[i] = ImGui.CalcTextSize(_markerTexts[i]).X;
            }
        }
        return new WindowMarkerSet(_windows.Count, _markerNodes, _markerSeconds, _markerTexts, _markerWidths);
    }

    // Resolve each window's marker node once per build, the body's most central rung on the map.
    private void ResolveWindowMarkers()
    {
        int count = _windows.Count;
        _markerNodes = new LayoutNode?[count];
        _markerSeconds = new double[count];
        _markerTexts = new string[count];
        _markerWidths = new float[count];
        _markerKeys = new long[count];
        for (int i = 0; i < count; i++)
        {
            _markerNodes[i] = FindFocusNode(_windows[i].TargetId);
            _markerKeys[i] = long.MinValue;
        }
    }

    // Rebuild the transfer-window list for a root. Called from RebuildAt after a successful
    // build. The list mirrors the bodies the map shows as their own lane, so it reads that set
    // off the freshly built visual tree; it is therefore rebuilt on visibility changes too.
    // Self-clearing, so it is safe to call on its own.
    private void RebuildTransferWindows(PhysicalNode root)
    {
        _windows.Clear();
        if (_cache == null || _visualTree == null)
        {
            ResolveWindowMarkers();
            return;
        }
        try
        {
            HashSet<string> shown = CollectShownBodyIds();
            List<TransferWindowInfo> built = TransferWindows.Build(_cache, root, shown, Universe.GetElapsedTime());
            _windows.AddRange(built);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("twindow-build-" + root.Id, $"[DvMap] Transfer window build failed for '{root.Id}': {ex}");
        }
        ResolveWindowMarkers();
    }

    // The body Ids the map currently shows as their own lane (every rung node), excluding the
    // hub buses and the aggregated minor-body group. The transfer-window table filters its
    // siblings to this set so it lists exactly what the canvas does.
    private HashSet<string> CollectShownBodyIds()
    {
        var ids = new HashSet<string>();
        if (_visualTree == null)
            return ids;
        foreach (StateNode n in _visualTree.Nodes)
        {
            if (n.Kind == StateKind.Hub || n.Kind == StateKind.MinorGroup)
                continue;
            ids.Add(n.Body.Id);
        }
        return ids;
    }

    // The find / isolate section at the top of the panel: a body-name search over the full
    // system graph (every body, even ones collapsed into a "+N" group), a results list whose
    // pick reveals + centers + highlights the body, and an isolate toggle that strips the map
    // to the spine, the major bodies, the revealed bodies and the selected route.
    private void DrawSearchPanel()
    {
        ImGui.SeparatorText("Find"u8);

        ImGui.PushItemWidth(-1f);
        // The binding returns true when the text was edited this frame, so the buffer is read
        // into a string and searched only then.
        bool edited = ImGui.InputTextWithHint("##dvsearch"u8, "Search bodies..."u8, _searchBuffer);
        ImGui.PopItemWidth();

        if (edited)
        {
            string query = _searchBuffer.ToString();
            if (query != _lastSearchQuery)
            {
                _lastSearchQuery = query;
                _queryBlank = query.Trim().Length == 0;
                UpdateSearchResults(query);
            }
        }

        PhysicalNode? pick = null;
        if (_searchResults.Count > 0)
        {
            // A bounded, scrollable list so even a broad query stays compact.
            float listH = Math.Min(_searchResults.Count, MaxVisibleSearchRows) * ImGui.GetFrameHeight() + 6f;
            ImGui.BeginChild("##dvsearchresults"u8, new float2?(new float2(0f, listH)), ImGuiChildFlags.Borders);
            try
            {
                for (int i = 0; i < _searchResults.Count; i++)
                {
                    PhysicalNode body = _searchResults[i];
                    bool isFocus = body.Id == _focusBodyId;
                    if (ImGui.Selectable(_searchLabels[i], isFocus, ImGuiSelectableFlags.None, (float2?)null))
                        pick = body;
                }
            }
            finally
            {
                // EndChild must always run, even if the region was clipped, to keep the
                // window stack balanced.
                ImGui.EndChild();
            }

            int more = _searchMatchTotal - _searchResults.Count;
            if (more > 0)
            {
                ImGui.TextDisabled(_moreText.IsStale(null, more)
                    ? _moreText.Set(null, more, "+" + more.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more, refine search")
                    : _moreText.Text);
            }
        }
        else if (!_queryBlank)
        {
            ImGui.TextDisabled("No bodies match.");
        }

        bool isolate = _isolate;
        if (ConsoleUi.CheckboxRow("ISOLATE TO FOUND BODIES".AsSpan(), "DvmIsolate".AsSpan(), ref isolate))
        {
            _isolate = isolate;
            // The effect only exists once something is revealed, so only rebuild when isolate
            // would actually change the current view.
            if (_revealedBodyIds.Count > 0 && _currentRootId != null)
                RebuildPreservingSelection(_currentRootId);
        }
        if (_isolate && _revealedBodyIds.Count == 0)
            ImGui.TextDisabled("Search a body to isolate it");

        if ((_revealedBodyIds.Count > 0 || _focusBodyId != null || _lastSearchQuery.Length > 0)
            && ImGui.SmallButton("Clear find"u8))
            ClearSearch();

        // Act on a pick after the list/child scope closes, so the rebuild it triggers does not
        // run mid-child or mutate anything the loop above is iterating.
        if (pick != null)
            RevealAndFocus(pick);
    }

    // Refresh the match list from the full graph, every star system included. Matching is a
    // case-insensitive substring of the body Id (the same string the map labels use). The root
    // of the shown system is skipped (it is the hub bus, not a destination); a body of another
    // system is listed with its system and opens that system on the map when picked. Results
    // rank exact, then prefix, then substring, alphabetical within, and are capped so a broad
    // query does not build thousands of rows (the surplus is noted).
    private void UpdateSearchResults(string query)
    {
        _searchResults.Clear();
        _searchLabels.Clear();
        _searchMatchTotal = 0;
        if (_graph == null)
            return;
        string q = query.Trim();
        if (q.Length == 0)
            return;

        PhysicalNode? shownSystem = CurrentSystemRoot();
        foreach (PhysicalNode n in _graph.AllNodes)
        {
            if (ReferenceEquals(n, shownSystem))
                continue;
            if (n.Astro.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            _searchMatchTotal++;
            _searchResults.Add(n);
        }

        _searchResults.Sort((a, b) => CompareMatch(a.Astro.Id, b.Astro.Id, q));
        if (_searchResults.Count > MaxSearchResults)
            _searchResults.RemoveRange(MaxSearchResults, _searchResults.Count - MaxSearchResults);

        foreach (PhysicalNode n in _searchResults)
        {
            PhysicalNode system = SystemGraph.SystemRootOf(n);
            _searchLabels.Add(ReferenceEquals(system, shownSystem) || ReferenceEquals(system, n)
                ? n.Id
                : n.Id + " (" + system.Id + ")");
        }
    }

    private static int CompareMatch(string a, string b, string query)
    {
        int byRank = MatchRank(a, query).CompareTo(MatchRank(b, query));
        return byRank != 0 ? byRank : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static int MatchRank(string id, string query)
    {
        if (id.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (id.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        return 2;
    }

    // Reveal a searched body (so it leaves its "+N" group / survives isolate), open its star
    // system when it lies in another one, then center and highlight it. A body already in the
    // visual tree needs no rebuild, only the focus; opening a system lays out that part alone.
    private void RevealAndFocus(PhysicalNode body)
    {
        _focusBodyId = body.Id;
        bool inTree = BestBodyNode(body.Id) != null;
        bool wasEmpty = _revealedBodyIds.Count == 0;
        bool added = _revealedBodyIds.Add(body.Id);
        // Rebuild when the body must be materialized (it was collapsed, hidden or in a system
        // past the shown cap), or when this first reveal arms isolate (which is inert until
        // something has been revealed).
        bool isolateActivates = _isolate && wasEmpty && added;
        if ((!inTree || isolateActivates) && _currentRootId != null)
            RebuildPreservingSelection(_currentRootId);
        else
            UpdateScene(anchorBodyId: body.Id);
        // Center on the body. When the lines above rebuilt or recomposed, this overrides the
        // pan they anchored; that anchor only stands as a fallback when the focus node cannot
        // be resolved (FocusOnBody leaves the pan alone then).
        FocusOnBody(body.Id);
    }

    // Center the view on a body's most central node and mark it as the focus highlight. Zooms
    // in to at least FocusZoom so the found body is legible; keeps a higher zoom if already in.
    private void FocusOnBody(string bodyId)
    {
        LayoutNode? node = FindFocusNode(bodyId);
        if (node == null || _scene == null)
        {
            _focusNodeId = null;
            return;
        }

        _focusNodeId = node.Id;
        _zoom = Math.Clamp(Math.Max(_zoom, FocusZoom), MinZoom, MaxZoom);
        if (_lastSize.X > 8f && _lastSize.Y > 8f)
        {
            _panX = _lastSize.X / 2.0 - (node.SnappedX - _scene.MinX) * _zoom;
            _panY = _lastSize.Y / 2.0 - (node.SnappedY - _scene.MinY) * _zoom;
        }
        // Clear any pending auto-fit (RebuildAt sets one) so it does not run on the next draw
        // and discard this centering.
        _needsFit = false;
    }

    // The layout node to center / highlight for a body: its most central rung (low orbit, then
    // intercept, then surface, ...). Returns null when the body is not currently on the map
    // (still inside a group, or in a collapsed star system), in which case the caller rebuilds
    // or opens that system first.
    private LayoutNode? FindFocusNode(string bodyId)
    {
        for (int i = 0; i < FocusKinds.Length; i++)
        {
            StateNode? state = _visualTree?.FindBodyNode(bodyId, FocusKinds[i]);
            if (state != null && _sceneById.TryGetValue(state.Id, out LayoutNode? node))
                return node;
        }
        return null;
    }

    // The body's most central node in the visual tree, whether or not its system is open.
    private StateNode? BestBodyNode(string bodyId)
    {
        for (int i = 0; i < FocusKinds.Length; i++)
        {
            StateNode? state = _visualTree?.FindBodyNode(bodyId, FocusKinds[i]);
            if (state != null)
                return state;
        }
        return null;
    }

    private static readonly StateKind[] FocusKinds =
    {
        StateKind.LowOrbit,
        StateKind.Intercept,
        StateKind.Surface,
        StateKind.Stationary,
        StateKind.SoiEdge,
        StateKind.YouAreHere,
        StateKind.Hub
    };

    // Reset the find state: clear the query, the focus highlight and the revealed set, so the
    // map returns to its adaptive aggregated view. Rebuilds only when bodies were revealed and
    // the caller does not rebuild anyway.
    private void ClearSearch(bool rebuild = true)
    {
        _searchBuffer.Clear();
        _lastSearchQuery = "";
        _searchResults.Clear();
        _searchLabels.Clear();
        _searchMatchTotal = 0;
        _queryBlank = true;
        _focusBodyId = null;
        _focusNodeId = null;
        bool hadRevealed = _revealedBodyIds.Count > 0;
        _revealedBodyIds.Clear();
        // Clearing the find resets isolate too: with nothing revealed it would be inert anyway,
        // and unticking it keeps the checkbox honest.
        _isolate = false;
        if (rebuild && hadRevealed && _currentRootId != null)
            RebuildPreservingSelection(_currentRootId);
        else if (rebuild)
            UpdateScene(anchorBodyId: null);
    }

    private void DrawCanvas(IViewport viewport)
    {
        LayoutScene? built = _scene;
        if (built == null)
        {
            // A refused (too-large) build leaves no layout; explain why instead of a blank
            // canvas. The panel still draws, so the visibility / isolate controls remain usable.
            if (_oversizedCount > 0)
                ImGui.TextWrapped(_oversizedNote);
            return;
        }

#if DEBUG
        using var perf = new PerfTracker.Scope("DvMap.Draw.Canvas");
#endif
        LayoutScene scene = built;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float2 origin = ImGui.GetCursorScreenPos();
        float2 size = ImGui.GetContentRegionAvail();
        _lastSize = size;
        if (size.X < 8f || size.Y < 8f)
            return;

        float2 canvasMax = origin + size;
        dl.AddRectFilled(in origin, in canvasMax, CanvasRenderer.CanvasBackground);

        if (_needsFit)
        {
            FitToView(size);
            _needsFit = false;
        }

        var transform = new CanvasTransform(origin, _zoom, _panX, _panY, scene.MinX, scene.MinY);

        // Refresh the live transfer-window fields once per frame here, so both the on-map markers
        // (below) and the overlay read the same fresh countdowns.
        RefreshTransferWindows();
        WindowMarkerSet windowMarkers = BuildWindowMarkers();
        ConnectorBadge? connectorBadge = _routeSummary?.Interstellar != null && _interstellar.HasView ? _interstellar.Badge : null;

        dl.PushClipRect(in origin, in canvasMax, intersectWithCurrentClipRect: true);
        try
        {
            _canvas.Draw(dl, scene, _lookup!, _palette!, in transform, origin, canvasMax, _hoverId, _focusNodeId, _routeNodeIds,
                _options.IncludePlaneChange, _view.DvScale, _view.ShowTransferTimes, _view.ShowBodyMarkers, in windowMarkers, connectorBadge);
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

        // The Transfer-windows overlay floats bottom-left and likewise claims the input under it,
        // so neither a click on its rows nor a drag over it leaks through to a node or a pan.
        bool overWindows = DrawTransferWindowOverlay(origin, size);

        bool overAny = overOverlay || overWindows;
        HandleInput(hovered && !overAny, active, clicked && !overAny, origin, in transform);

        // A clock dot or list row hovered in the overlay highlights that sibling's node on the
        // map, reusing the canvas hover glow. Applied after HandleInput, which clears _hoverId
        // while the cursor is over the overlay.
        if (_windowsHoverBodyId != null)
            _hoverId = FindFocusNode(_windowsHoverBodyId)?.Id;

        // Draws on the viewport overlay list, so it escapes this window's clip rect.
        if (_showMapMarkers)
            _mapOverlay.Draw(viewport, _windows, _windowsHoverBodyId ?? _routeSiblingId);
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
            ImGui.Text(LayoutModeTooltip(_layoutMode));
            ImGui.EndTooltip();
        }

        if (clicked)
            SetLayoutMode(NextLayoutMode(_layoutMode));

        return MouseInRect(min, max.X - min.X, max.Y - min.Y);
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

    private static string LayoutModeTooltip(LayoutMode mode)
    {
        return mode switch
        {
            LayoutMode.GravityWell => "Layout: Gravity-well (click to switch)",
            LayoutMode.Spring => "Layout: Spring (click to switch)",
            _ => "Layout: Cumulative-down (click to switch)"
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
        if (hovered && !panning)
            near = NearestNode(mouse, in transform);
        _hoverId = near?.Id;

        if (near != null)
        {
            ShowNodeTooltip(near);
        }
        else if (hovered && !panning)
        {
            // Not over a node: a hover near an edge or an interstellar connector shows its
            // tooltip instead.
            LayoutEdge? edge = NearestEdge(mouse, in transform, out LayoutConnector? connector, out double edgeDist);
            if (edgeDist <= EdgeHoverPx)
            {
                if (connector != null)
                    ShowConnectorTooltip(connector);
                else if (edge != null)
                    ShowEdgeTooltip(edge);
            }
        }

        // A click without a drag selects a route (or shift-clicks to re-root). A plain
        // click on a body highlights the path from "you are here" to it; clicking the
        // same node again clears it. A click on empty space is ignored, so a stray click
        // or a click meant to refocus the window does not throw away the selected route.
        if (clicked)
        {
            LayoutNode? hit = NearestNode(mouse, in transform);
            if (hit != null)
            {
                if (io.KeyShift)
                {
                    ReRootTo(hit);
                }
                else if (hit.Kind == LayoutKind.MinorGroup)
                {
                    // A group is not routable. A plain click is a no-op (search / isolate
                    // pulls out a specific body), so it never throws away a selected route by
                    // clearing the selection.
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
                    // Selecting a node of another star system opens that system, and clearing
                    // it closes it again; the clicked node stays where it is on screen.
                    UpdateScene(anchorNodeId: hit.Id);
                }
            }
        }
    }

    // Resolve the selected target into a route from the origin and accumulate it. Called
    // on selection and whenever a toggle changes. Clears the route when nothing valid is
    // selected (empty space, the star hub, or a build that has not produced a tree yet).
    private void RecomputeRoute()
    {
#if DEBUG
        using var perf = new PerfTracker.Scope("DvMap.Route.Recompute");
#endif
        _routeSummary = null;
        _routeNodeIds = null;
        _routeSiblingId = null;

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
            RouteContext context = BuildRouteContext(path);
            RouteSummary summary = RouteAccumulator.Accumulate(path, _graph, _options, in context);
            _routeSummary = summary;

            // A zero-step path means the target is the origin; keep the summary (the panel
            // says "already here") but do not dim the map for an empty highlight.
            if (path.Steps.Count == 0)
                return;

            var ids = new HashSet<string>(path.Nodes.Count);
            foreach (StateNode n in path.Nodes)
                ids.Add(n.Id);
            _routeNodeIds = ids;

            // The route's interplanetary leg: the first body on the ordered path past the origin
            // that is a transfer-window sibling of the root. Used to bias the clock / list
            // emphasis to the route's destination when nothing is hovered. Skipping the origin
            // body covers the rare case where the route itself starts at a sibling (re-rooted
            // away from the vehicle), so the emphasis lands on the destination, not the origin.
            var siblingIds = new HashSet<string>(_windows.Count);
            foreach (TransferWindowInfo w in _windows)
                siblingIds.Add(w.TargetId);
            foreach (StateNode n in path.Nodes)
            {
                if (n.Body.Id == origin.Body.Id)
                    continue;
                if (siblingIds.Contains(n.Body.Id))
                {
                    _routeSiblingId = n.Body.Id;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Routing must never unwind into the render path; surface and drop the route.
            LogHelper.ErrorOnce("route-accumulate", $"[DvMap] Route accumulation failed: {ex}");
        }
    }

    // What the accumulator needs from the controlled vessel: whether an interstellar departure
    // burns from the vessel's own orbit at the origin body. The live transit state (excess and
    // direction on an open orbit) is read by the interstellar section on every vessel refresh.
    private RouteContext BuildRouteContext(RoutePath path)
    {
        Vehicle? vehicle = TryGetControlledVehicle();
        PhysicalNode? originBody = _graph?.Find(path.Origin.Body.Id);
        if (vehicle == null || originBody == null)
            return RouteContext.None;
        try
        {
            bool fromVessel = !_options.FromSurface && InterstellarLegs.VesselParksAt(originBody, vehicle);
            return new RouteContext(fromVessel);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("route-context", $"[DvMap] Could not read the vessel's orbit for the route: {ex.Message}");
            return RouteContext.None;
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
        StateNode? surface = tree.FindBodyNode(tree.RootBodyId, StateKind.Surface);
        // A star or barycenter root has no low orbit to start from; the parking orbit an open
        // cruise root carries is a destination, never the origin.
        StateNode? lowOrbit = SystemGraph.IsHubOnlyBody(tree.Root.Body) ? null : tree.FindBodyNode(tree.RootBodyId, StateKind.LowOrbit);

        if (_options.FromSurface)
            return surface ?? lowOrbit ?? you ?? tree.Root;
        return you ?? lowOrbit ?? surface ?? tree.Root;
    }

    // Resolve the clicked node into the node the route should actually reach. A hub bus
    // routes to that hub body's low orbit. The ego system's root star or barycenter has none,
    // so there is nothing to route to; an orbiting star, and every star and root of another
    // star system (its stub included), routes to its parking orbit. "Land at destination"
    // extends an orbit click down to the body's surface.
    private StateNode? ResolveTarget(StateNode clicked)
    {
        // A group is a synthetic aggregate, not a real destination, so it plans no route.
        // Search / isolate pulls out a specific body instead.
        if (clicked.Kind == StateKind.MinorGroup)
            return null;

        VisualTree tree = _visualTree!;
        StateNode target = clicked;

        if (target.Kind == StateKind.Hub)
        {
            StateNode? lo = tree.FindBodyNode(target.Body.Id, StateKind.LowOrbit);
            if (lo == null)
                return null;
            target = lo;
        }

        if (_options.LandAtDestination
            && (target.Kind == StateKind.LowOrbit || target.Kind == StateKind.Intercept))
        {
            StateNode? surface = tree.FindBodyNode(target.Body.Id, StateKind.Surface);
            if (surface != null)
                return surface;
        }

        return target;
    }

    // The vehicle's total staged dV for the comparison bar: the controlled vehicle in flight, else
    // the vehicle under construction in the editor. Both go through the game's own staged model.
    // Null (bar shows n/a) when neither exists.
    private static double? TryGetAvailableDv()
    {
        double? dv = StagedDv.TryTotalDv();
        return dv is double value && double.IsFinite(value) ? value : null;
    }

    // The node under the cursor: the nearest one whose dot, plus a small margin, or the fixed
    // hover radius, whichever is larger, holds the cursor. A large dot (the root, a star-system
    // stub) is therefore clickable all the way to its rim.
    private LayoutNode? NearestNode(float2 mouse, in CanvasTransform transform)
    {
        LayoutNode? best = null;
        double bestSq = double.MaxValue;
        IReadOnlyList<LayoutNode> nodes = _scene!.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode node = nodes[i];
            float2 p = transform.ToScreen(node.SnappedX, node.SnappedY);
            double dx = p.X - mouse.X;
            double dy = p.Y - mouse.Y;
            double sq = dx * dx + dy * dy;
            double reach = Math.Max(HoverRadiusPx, SceneComposer.GlyphRadiusPx(node) + 3.0);
            if (sq < bestSq && sq <= reach * reach)
            {
                bestSq = sq;
                best = node;
            }
        }
        // A system title stands for its root, so hovering or clicking it acts on the system.
        if (best == null)
        {
            int title = _canvas.TitleAt(mouse);
            if (title >= 0 && title < nodes.Count)
                best = nodes[title];
        }
        return best;
    }

    // The edge or interstellar connector whose polyline runs nearest the cursor (hub links
    // excluded, they carry no dV). Used for the hover tooltip when the cursor is not over a node.
    private LayoutEdge? NearestEdge(float2 mouse, in CanvasTransform transform, out LayoutConnector? connector, out double distPx)
    {
        LayoutEdge? best = null;
        connector = null;
        double bestSq = double.MaxValue;
        IReadOnlyList<LayoutNode> nodes = _scene!.Nodes;
        for (int n = 0; n < nodes.Count; n++)
        {
            LayoutNode node = nodes[n];
            for (int e = 0; e < node.Out.Count; e++)
            {
                LayoutEdge edge = node.Out[e];
                // Skip hub links (no dV) and the group connector (its target is a synthetic
                // aggregate, so an edge tooltip there would be meaningless).
                if (edge.IsHubLink || edge.To.Kind == LayoutKind.MinorGroup || edge.Polyline.Count < 2)
                    continue;
                double sq = DistanceSqToPolyline(mouse, edge.Polyline, in transform);
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = edge;
                }
            }
        }

        // Each connector's own branch, then the shared trunk and buses once. The shared lines
        // stand for the one connector there is, or for the one on the selected route.
        IReadOnlyList<LayoutConnector> connectors = _scene.Connectors;
        for (int i = 0; i < connectors.Count; i++)
        {
            LayoutConnector c = connectors[i];
            double sq = DistanceSqToPoint(mouse, c.BadgeAnchor, in transform);
            LayoutPoint from = c.BadgeAnchor;
            for (int p = c.BranchStart; p < c.Polyline.Count; p++)
            {
                sq = Math.Min(sq, DistanceSqToSegment(mouse, transform.ToScreen(from.X, from.Y), transform.ToScreen(c.Polyline[p].X, c.Polyline[p].Y)));
                from = c.Polyline[p];
            }
            if (sq < bestSq)
            {
                bestSq = sq;
                best = null;
                connector = c;
            }
        }
        IReadOnlyList<IReadOnlyList<LayoutPoint>> network = _scene.Network;
        for (int i = 0; i < network.Count; i++)
        {
            double sq = DistanceSqToPolyline(mouse, network[i], in transform);
            if (sq < bestSq)
            {
                bestSq = sq;
                best = null;
                connector = SharedConnector(connectors);
            }
        }
        distPx = Math.Sqrt(bestSq);
        return best;
    }

    private LayoutConnector? SharedConnector(IReadOnlyList<LayoutConnector> connectors)
    {
        if (connectors.Count == 1)
            return connectors[0];
        if (_routeNodeIds == null)
            return null;
        for (int i = 0; i < connectors.Count; i++)
        {
            if (CanvasRenderer.OnRoute(connectors[i], _routeNodeIds))
                return connectors[i];
        }
        return null;
    }

    private static double DistanceSqToPoint(float2 mouse, LayoutPoint p, in CanvasTransform transform)
    {
        float2 s = transform.ToScreen(p.X, p.Y);
        double dx = s.X - mouse.X;
        double dy = s.Y - mouse.Y;
        return dx * dx + dy * dy;
    }

    private static double DistanceSqToPolyline(float2 mouse, IReadOnlyList<LayoutPoint> line, in CanvasTransform transform)
    {
        double best = double.MaxValue;
        for (int i = 1; i < line.Count; i++)
        {
            float2 a = transform.ToScreen(line[i - 1].X, line[i - 1].Y);
            float2 b = transform.ToScreen(line[i].X, line[i].Y);
            best = Math.Min(best, DistanceSqToSegment(mouse, a, b));
        }
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
        if (_lookup == null || !_lookup.TryGetValue(node.Id, out StateNode? state))
            return;
        if (state.Kind == StateKind.MinorGroup)
            _tooltips.MinorGroup(state);
        else
            _tooltips.Node(state, _graph?.LadderFor(state.Body.Id), _graph?.HasSeveralSystems == true);
    }

    // Rich edge tooltip: resolve the layout edge back to its game edge for the fine segment
    // kind and formula; the layout edge supplies the displayed dV figures.
    private void ShowEdgeTooltip(LayoutEdge edge)
    {
        Edge? game = ResolveGameEdge(edge.From.Id, edge.To.Id);
        if (game != null)
            _tooltips.Edge(game, edge, _view.DvScale, _view.ShowTransferTimes);
    }

    private void ShowConnectorTooltip(LayoutConnector connector)
    {
        Edge? game = ResolveGameEdge(connector.From.Id, connector.To.Id);
        if (game != null)
            _tooltips.Connector(game);
    }

    // Find the game edge between two node Ids in the visual tree.
    private Edge? ResolveGameEdge(string fromId, string toId)
    {
        if (_lookup == null || !_lookup.TryGetValue(fromId, out StateNode? from))
            return null;
        for (int i = 0; i < from.Out.Count; i++)
        {
            if (from.Out[i].To.Id == toId)
                return from.Out[i];
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

        // Latched on the system instance, not its Id: loading the same system again builds new
        // bodies, and nodes holding the old ones would read orbits that no longer update.
        if (_graph == null || !ReferenceEquals(_graph.System, system))
        {
            SystemGraph? graph = SystemGraph.Build(system);
            if (graph == null)
                return false;
            _graph = graph;
            _cache = new DvCache();
            _palette = ColorPalette.Build(graph);
            _currentRootId = null;
            _rootLabel = null;
            _mapSystemId = null;
            _interstellar.Reset();
            _canvas.Reset();
            _tooltips.Reset();
            _routePanel.Reset();
            _egoVehicle = null;
            _egoParent = null;
            _buildFailedRootId = null;
            _selectedId = null;
            _routeSummary = null;
            _routeNodeIds = null;
            _routeSiblingId = null;
            _built = false;

            // A new system invalidates the search state (the bodies are different).
            _revealedBodyIds.Clear();
            _searchResults.Clear();
            _searchLabels.Clear();
            _searchMatchTotal = 0;
            _searchBuffer.Clear();
            _lastSearchQuery = "";
            _focusBodyId = null;
            _focusNodeId = null;
            _isolate = false;
            _queryBlank = true;
            _oversizedCount = 0;
        }

        FollowVessel(system);

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

    // The ego root. Following the vessel, it is the controlled vehicle's current SOI body in
    // whatever star system that is, or the home body when there is no vehicle. With a system
    // chosen in the System menu, it is the vessel's SOI body when the vessel is in that system,
    // else the home body when that system holds it, else the system's own root hub, never a body
    // of another system.
    private string? DesiredEgoRootId(CelestialSystem system)
    {
        SystemGraph graph = _graph!;
        string? parentId = TryGetEgoParentId();
        PhysicalNode? parent = parentId != null ? graph.Find(parentId) : null;
        PhysicalNode? home = system.HomeBody?.Id is string homeId ? graph.Find(homeId) : null;

        PhysicalNode? chosen = _mapSystemId != null ? graph.Find(_mapSystemId) : null;
        if (chosen == null)
        {
            _mapSystemId = null;
            return (parent ?? home ?? graph.HomeRoot).Id;
        }

        if (parent != null && ReferenceEquals(SystemGraph.SystemRootOf(parent), chosen))
            return parent.Id;
        if (home != null && ReferenceEquals(SystemGraph.SystemRootOf(home), chosen))
            return home.Id;
        return chosen.Id;
    }

    // Follow the controlled vessel while the map follows it (no System pick). A vehicle switch
    // re-evaluates the root on the next build. A parent change that crosses into another star
    // system (PhysicsStates.CheckSoiTransitions hands the vessel to IIndependentRoot.FindOwner,
    // the root Universe.NearestRoot names) re-roots the map there and keeps the selected route
    // when its target still exists. Only a change of either reference acts, so a manual
    // Shift+click re-root stays until the vessel moves on.
    private void FollowVessel(CelestialSystem system)
    {
#if DEBUG
        using var perf = new PerfTracker.Scope("DvMap.Follow");
#endif
        Vehicle? vehicle;
        IParentBody? parent;
        try
        {
            vehicle = Program.ControlledVehicle;
            parent = vehicle?.Parent;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("follow-vessel", $"[DvMap] Could not read the controlled vehicle: {ex.Message}");
            return;
        }

        bool vehicleChanged = !ReferenceEquals(vehicle, _egoVehicle);
        bool parentChanged = !ReferenceEquals(parent, _egoParent);
        _egoVehicle = vehicle;
        _egoParent = parent;
        if (_mapSystemId != null || !_built)
            return;
        if (vehicleChanged)
        {
            _reevaluateRoot = true;
            return;
        }
        if (!parentChanged || parent == null || _graph == null || !_graph.HasSeveralSystems)
            return;

        PhysicalNode? node = _graph.Find(parent.Id);
        PhysicalNode? shown = CurrentSystemRoot();
        if (node == null || shown == null || ReferenceEquals(SystemGraph.SystemRootOf(node), shown))
            return;
        string? root = DesiredEgoRootId(system);
        if (root != null && root != _buildFailedRootId)
            FollowTo(root);
    }

    // Re-root at the vessel's new star system, then put the selected route back when its
    // target is still on the map: body Ids are unique across every star system, so a target in
    // the system the vessel just entered becomes an in-system route there.
    private void FollowTo(string rootId)
    {
        string? selected = _selectedId;
        RebuildAt(rootId);
        if (selected != null && _lookup != null && _lookup.ContainsKey(selected))
        {
            _selectedId = selected;
            RecomputeRoute();
            UpdateScene(anchorNodeId: null);
        }
    }

    private static Vehicle? TryGetControlledVehicle()
    {
        try
        {
            Vehicle? vehicle = Program.ControlledVehicle;
            return vehicle?.Parent != null ? vehicle : null;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("controlled-vehicle", $"[DvMap] Could not read the controlled vehicle: {ex.Message}");
            return null;
        }
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
        bool haveAnchor = _scene != null;
        if (haveAnchor)
        {
            LayoutNode oldRoot = _scene!.Root;
            anchorX = (oldRoot.SnappedX - _scene.MinX) * zoom + _panX;
            anchorY = (oldRoot.SnappedY - _scene.MinY) * zoom + _panY;
        }

        RebuildAt(rootId);

        if (selected != null && _lookup != null && _lookup.ContainsKey(selected))
        {
            _selectedId = selected;
            RecomputeRoute();
            UpdateScene(anchorNodeId: null);
        }

        _zoom = zoom;
        if (haveAnchor && _scene != null)
        {
            LayoutNode newRoot = _scene.Root;
            _panX = anchorX - (newRoot.SnappedX - _scene.MinX) * zoom;
            _panY = anchorY - (newRoot.SnappedY - _scene.MinY) * zoom;
            // We re-anchored the view ourselves, so cancel the auto-fit RebuildAt requested.
            // Without a prior layout to anchor to (e.g. recovering from a refused build), leave
            // the auto-fit on so the fresh map is framed instead of shown at a stale pan/zoom.
            _needsFit = false;
        }
    }

    private void RebuildAt(string rootId)
    {
        if (_graph == null || _cache == null)
            return;

        PhysicalNode node = _graph.Find(rootId) ?? _graph.HomeRoot;

        // A genuine root change (a re-root, or a system change, which first nulls _currentRootId)
        // resets the manual overlay panel sizes back to auto. A same-root rebuild (a visibility,
        // detail or layout toggle) keeps the user's chosen sizes.
        if (node.Id != _currentRootId)
        {
            _listWidthOverride = null;
            _listHeightOverride = null;
            _clockSideOverride = null;
        }

        ClassifiedState? egoState = null;
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle != null && TryGetEgoParentId() == node.Id)
        {
            try
            {
                egoState = node.IsHubOnly
                    ? StateClassifier.ClassifyCruise(vehicle)
                    : StateClassifier.Classify(vehicle, node.Ladder);
            }
            catch (Exception ex)
            {
                LogHelper.WarnOnce("classify-" + node.Id, $"[DvMap] Could not classify vehicle state at '{node.Id}': {ex.Message}");
            }
        }

        // Clear the oversized flag up front so only the oversized branch below can raise it; if
        // the build instead throws, the catch leaves it clear and the canvas does not wrongly
        // claim "too many bodies" when the real cause was a build failure.
        _oversizedCount = 0;

        // Drop the old transfer windows up front so a refused or failed build leaves the
        // section empty rather than showing stale entries; a successful build repopulates it.
        _windows.Clear();

        try
        {
            // Isolate only takes effect once a body has been revealed (searched). With nothing
            // revealed it would just hide every minor body, duplicating the "Show minor bodies"
            // toggle, so it stays inert until there is something to isolate to.
            bool effectiveIsolate = _isolate && _revealedBodyIds.Count > 0;
            var buildOptions = new BuildOptions(_fullLadder, _view.ShowMinorBodies, _view.ShowComets,
                effectiveIsolate, _revealedBodyIds);
            VisualTree visual;
            {
#if DEBUG
                using var perf = new PerfTracker.Scope("DvMap.Build.VisualTree");
#endif
                visual = VisualTree.Build(_graph, _cache, node, egoState, buildOptions);
            }

            // Universal never-hang guard: above the ceiling, refuse to lay out (any mode) and
            // show a note instead of risking a multi-second build or unresponsive frames. The
            // visual-tree build above is O(nodes) and cheap; the expensive passes (the spring
            // settle, label placement) and the per-frame hover/render loops are what this caps.
            // Running the build off the draw thread is not an option here: the layout measures
            // labels via ImGui, which is only valid on the draw thread. Realistically only a
            // system with thousands of MAJOR bodies reaches this - minor bodies aggregate away.
            if (visual.Nodes.Count > MaxLayoutNodes)
            {
                _visualTree = null;
                _egoLayout = null;
                _egoHub = null;
                _scene = null;
                _parts.Clear();
                _sceneById.Clear();
                _lookup = null;
                _oversizedCount = visual.Nodes.Count;
                _oversizedNote = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "This system has {0:#,##0} bodies to lay out - too many to render without "
                    + "risking a freeze, so the map is disabled for it.", _oversizedCount);
                _currentRootId = node.Id;
                _rootLabel = RootLabel(node);
                _buildFailedRootId = null;
                _selectedId = null;
                _routeSummary = null;
                _routeNodeIds = null;
                _routeSiblingId = null;
                _focusNodeId = null;
                _hoverId = null;
                _needsFit = false;
                _built = true;
                return;
            }

            LayoutTree tree = VisualTreeAdapter.ToLayoutTree(visual, _graph);
            var cfg = new LayoutConfig { Mode = _layoutMode };
            LayoutResult result;
            {
#if DEBUG
                using var perf = new PerfTracker.Scope("DvMap.Layout.Ego");
#endif
                result = LayoutEngine.Run(tree, cfg, MeasureText);
            }

            var lookup = new Dictionary<string, StateNode>(visual.Nodes.Count);
            foreach (StateNode n in visual.Nodes)
                lookup[n.Id] = n;

            _visualTree = visual;
            _egoLayout = result;
            _egoHub = FindLayoutNode(result.Tree, visual.SystemHub.Id);
            _lookup = lookup;
            _parts.Clear();
            _expandedA = null;
            _expandedB = null;
            ComposeScene();
            PhysicalNode? previousSystem = CurrentSystemRoot();
            _currentRootId = node.Id;
            _rootLabel = RootLabel(node);
            _buildFailedRootId = null;
            _selectedId = null;
            _routeSummary = null;
            _routeNodeIds = null;
            _routeSiblingId = null;
            _needsFit = true;
            _built = true;

            // Open the searched body's star system again and keep the focus highlight on the
            // body across rebuilds; the chosen node Id can shift with detail, so re-resolve it
            // from the body (no re-center here).
            UpdateScene();
            if (_focusBodyId != null)
                _focusNodeId = FindFocusNode(_focusBodyId)?.Id;

            // Rebuild the transfer-window list for the new root. It mirrors the bodies the map
            // shows as their own lane, so it runs here, after the visual tree is built.
            RebuildTransferWindows(node);

            // The search results name the other systems and skip the shown root, so a move to
            // another star system relabels them.
            if (!_queryBlank && !ReferenceEquals(previousSystem, CurrentSystemRoot()))
                UpdateSearchResults(_lastSearchQuery);
        }
        catch (Exception ex)
        {
            _buildFailedRootId = node.Id;
            LogHelper.ErrorOnce("map-build-" + rootId, $"[DvMap] Map build failed for root '{rootId}': {ex}");
        }
    }

    // The other star systems to open: the one holding the selected node and the one holding the
    // searched body. When the set changes, only the changed parts are laid out (each part once
    // per root build) and the scene is composed again; the visual tree, the ego layout and the
    // route stay. The home map keeps its place on screen, because the composer never moves it.
    // The anchor (anchorNodeId or the body's node, else its system's root when the node closed
    // away) only moves the view when it would otherwise leave the canvas: then it goes back to
    // the screen spot it had.
    private void UpdateScene(string? anchorNodeId = null, string? anchorBodyId = null)
    {
        if (_visualTree == null || _egoLayout == null)
            return;
        string? a = DestinationRootHubOf(_selectedId != null && _lookup != null && _lookup.TryGetValue(_selectedId, out StateNode? selected) ? selected : null);
        string? b = _focusBodyId != null ? DestinationRootHubOf(BestBodyNode(_focusBodyId)) : null;
        if (b == a)
            b = null;
        if (a == _expandedA && b == _expandedB)
            return;

        if (anchorNodeId == null && anchorBodyId != null)
            anchorNodeId = BestBodyNode(anchorBodyId)?.Id;
        string? fallback = anchorNodeId != null && _lookup != null && _lookup.TryGetValue(anchorNodeId, out StateNode? anchorState)
            ? DestinationRootHubOf(anchorState)
            : null;

        bool haveNode = TryScreenOf(anchorNodeId, out double nodeX, out double nodeY);
        bool haveRoot = TryScreenOf(fallback, out double rootX, out double rootY);
        LayoutScene? before = _scene;
        _expandedA = a;
        _expandedB = b;
        ComposeScene();

        if (before != null && _scene != null)
        {
            _panX += (_scene.MinX - before.MinX) * _zoom;
            _panY += (_scene.MinY - before.MinY) * _zoom;
            LayoutNode? node = null;
            double sx = 0.0;
            double sy = 0.0;
            if (haveNode && anchorNodeId != null && _sceneById.TryGetValue(anchorNodeId, out LayoutNode? n))
            {
                node = n;
                sx = nodeX;
                sy = nodeY;
            }
            else if (haveRoot && fallback != null && _sceneById.TryGetValue(fallback, out LayoutNode? f))
            {
                node = f;
                sx = rootX;
                sy = rootY;
            }
            if (node != null)
            {
                double x = (node.SnappedX - _scene.MinX) * _zoom + _panX;
                double y = (node.SnappedY - _scene.MinY) * _zoom + _panY;
                bool onCanvas = _lastSize.X <= 0f
                    || (x >= AnchorMarginPx && x <= _lastSize.X - AnchorMarginPx && y >= AnchorMarginPx && y <= _lastSize.Y - AnchorMarginPx);
                if (!onCanvas)
                {
                    _panX = sx - (node.SnappedX - _scene.MinX) * _zoom;
                    _panY = sy - (node.SnappedY - _scene.MinY) * _zoom;
                }
            }
        }
        if (_focusBodyId != null)
            _focusNodeId = FindFocusNode(_focusBodyId)?.Id;
    }

    // The canvas-relative position a scene node sits at now, before the scene changes.
    private bool TryScreenOf(string? nodeId, out double x, out double y)
    {
        x = 0.0;
        y = 0.0;
        if (nodeId == null || _scene == null || !_sceneById.TryGetValue(nodeId, out LayoutNode? node))
            return false;
        x = (node.SnappedX - _scene.MinX) * _zoom + _panX;
        y = (node.SnappedY - _scene.MinY) * _zoom + _panY;
        return true;
    }

    // The root hub Id of the other star system a node belongs to, or null for a node of the
    // ego system.
    private string? DestinationRootHubOf(StateNode? state)
    {
        if (state == null || _graph == null || _visualTree == null)
            return null;
        PhysicalNode? body = _graph.Find(state.Body.Id);
        PhysicalNode? ego = CurrentSystemRoot();
        if (body == null || ego == null)
            return null;
        PhysicalNode system = SystemGraph.SystemRootOf(body);
        return ReferenceEquals(system, ego) ? null : system.Id + "." + StateKind.Hub;
    }

    // Lay out each shown destination system as its own part (cached per root build) and place
    // them beside the ego map.
    private void ComposeScene()
    {
        if (_visualTree == null || _egoLayout == null || _graph == null)
            return;
#if DEBUG
        using var perf = new PerfTracker.Scope("DvMap.Compose");
#endif
        _shownParts.Clear();
        IReadOnlyList<Edge> interstellar = _visualTree.InterstellarEdges;
        for (int i = 0; i < interstellar.Count; i++)
        {
            Edge edge = interstellar[i];
            string rootId = edge.To.Id;
            bool expanded = rootId == _expandedA || rootId == _expandedB;
            if (!_parts.TryGetValue((rootId, expanded), out LayoutPart? part))
            {
#if DEBUG
                using var partPerf = new PerfTracker.Scope("DvMap.Layout.Part");
#endif
                SystemGraph graph = _graph;
                LayoutResult laid = SceneComposer.LayOutPart(() => VisualTreeAdapter.ToPartTree(edge, graph, expanded), _egoLayout, MeasureText);
                part = new LayoutPart { RootId = rootId, Result = laid, Expanded = expanded };
                _parts[(rootId, expanded)] = part;
            }
            _shownParts.Add(part);
        }

        LayoutScene scene = SceneComposer.Compose(_egoLayout, _egoHub, _shownParts, _egoLayout.Config);
        _scene = scene;
        _sceneById.Clear();
        IReadOnlyList<LayoutNode> nodes = scene.Nodes;
        for (int i = 0; i < nodes.Count; i++)
            _sceneById[nodes[i].Id] = nodes[i];
    }

    private static LayoutNode? FindLayoutNode(LayoutTree tree, string id)
    {
        IReadOnlyList<LayoutNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Id == id)
                return nodes[i];
        }
        return null;
    }

    // The menu-bar note naming the root, plus its star system when the map holds several.
    private string RootLabel(PhysicalNode node)
    {
        if (_graph == null || !_graph.HasSeveralSystems)
            return "Root: " + node.Id;
        PhysicalNode system = SystemGraph.SystemRootOf(node);
        return ReferenceEquals(system, node) ? "Root: " + node.Id : "Root: " + node.Id + " (" + system.Id + ")";
    }

    // Real label width from the active ImGui font. Valid because every RebuildAt call
    // happens inside the draw loop (DrawContent, the menu bar, or a canvas click).
    private static double MeasureText(string label)
    {
        return ImGui.CalcTextSize(label).X;
    }

    // With other star systems shown the fit also keeps the screen-fixed titles and labels at the
    // right edge on the canvas (SceneFit); with the ego map alone it is the plain bounds fit.
    private void FitToView(float2 size)
    {
        LayoutScene layout = _scene!;
        _zoom = SceneFit.Fit(layout, size.X, size.Y, MinZoom, MaxZoom, out double panX, out double panY);

        if (_centerOnRoot)
        {
            // Put the root node at the viewport center instead of centering the bounding
            // box. The transform maps (lx - MinX) * zoom + pan to screen-relative pixels,
            // so solve pan to land the root on the center. In GravityWell the root sits on
            // the spine, so this also vertically centers the whole spine.
            LayoutNode root = layout.Root;
            _panX = size.X / 2.0 - (root.SnappedX - layout.MinX) * _zoom;
            _panY = size.Y / 2.0 - (root.SnappedY - layout.MinY) * _zoom;
        }
        else
        {
            _panX = panX;
            _panY = panY;
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
