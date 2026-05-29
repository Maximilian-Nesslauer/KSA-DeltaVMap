using System;
using System.Collections.Generic;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;
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

    private static readonly byte4 BackgroundColor = new byte4(17, 21, 28, 255);

    private static MapWindow? _instance;

    // Per-system state (rebuilt when Universe.CurrentSystem changes).
    private SystemGraph? _graph;
    private DvCache? _cache;
    private ColorPalette? _palette;

    // Per-root state (rebuilt on re-root or detail change).
    // _visualTree is retained for Phase 5 routing (RouteFinder walks the StateNode
    // tree); the layout and lookup already cover everything the renderer needs today.
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

    // View state.
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;
    private bool _needsFit;
    private float2 _lastSize;

    // Interaction state.
    private string? _hoverId;
    private string? _selectedId;
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

    public override void DrawContent(Viewport viewport)
    {
        // The base class draws us between ImGui.Begin and ImGui.End. An exception
        // escaping here would skip End and leave the window/clip stacks unbalanced,
        // corrupting later frames, so contain everything (the same guarantee the
        // debug dumps already get for the render path).
        try
        {
            DrawCanvas();
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("map-draw", $"[DvMap] Map draw failed: {ex}");
        }
    }

    private void DrawCanvas()
    {
        if (!EnsureBuilt())
        {
            ImGui.TextDisabled("Delta-V map unavailable (no system loaded)."u8);
            return;
        }

        LayoutResult layout = _layout!;
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
            CanvasRenderer.Draw(dl, layout, _lookup!, _palette!, in transform, _hoverId, _selectedId);
        }
        finally
        {
            // Always pop, even on an unexpected throw, so the clip stack stays balanced.
            dl.PopClipRect();
        }

        // An invisible button over the whole canvas captures the left button, so a
        // left-drag pans the map instead of moving the window (only the title bar
        // moves it). Its return value is a click without a drag, used for selection.
        bool clicked = ImGui.InvisibleButton("##dvmap_canvas"u8, in size);
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        if (hovered)
            ImGui.SetItemKeyOwner(ImGuiKey.MouseWheelY);

        HandleInput(hovered, active, clicked, origin, in transform);
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
            ImGui.BeginTooltip();
            ImGui.Text(near!.Label);
            ImGui.TextDisabled(near.Kind.ToString());
            ImGui.EndTooltip();
        }

        // A click without a drag selects (or shift-clicks to re-root).
        if (clicked)
        {
            LayoutNode? hit = NearestNode(mouse, in transform, out double hitDist);
            if (hit != null && hitDist <= HoverRadiusPx)
            {
                if (io.KeyShift)
                    ReRootTo(hit);
                else
                    _selectedId = hit.Id == _selectedId ? null : hit.Id;
            }
            else
            {
                _selectedId = null;
            }
        }
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
            VisualTree visual = VisualTree.Build(_graph, _cache, node, egoState, _fullLadder);
            LayoutTree tree = VisualTreeAdapter.ToLayoutTree(visual);
            LayoutResult result = LayoutEngine.Run(tree, LayoutConfig.Default, MeasureText);

            var lookup = new Dictionary<string, StateNode>(visual.Nodes.Count);
            foreach (StateNode n in visual.Nodes)
                lookup[n.Id] = n;

            _visualTree = visual;
            _layout = result;
            _lookup = lookup;
            _currentRootId = node.Id;
            _buildFailedRootId = null;
            _selectedId = null;
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
        _panX = (size.X - contentW * _zoom) / 2.0;
        _panY = (size.Y - contentH * _zoom) / 2.0;
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
