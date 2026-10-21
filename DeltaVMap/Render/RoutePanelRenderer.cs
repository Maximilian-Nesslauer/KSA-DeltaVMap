using System;
using System.Collections.Generic;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using DeltaVMap.Core;
using DeltaVMap.Route;

namespace DeltaVMap.Render;

// What changed in the panel this frame, so the window knows how to react. A route-option
// change re-accumulates the selected path; a visibility change rebuilds the visual tree
// (it adds or drops bodies). Display-only settings (transfer times, body markers,
// piloting margin) need neither, since the renderer and panel read them live every frame.
internal readonly struct PanelResult
{
    public readonly bool RouteChanged;
    public readonly bool RebuildNeeded;

    public PanelResult(bool routeChanged, bool rebuildNeeded)
    {
        RouteChanged = routeChanged;
        RebuildNeeded = rebuildNeeded;
    }
}

// The right-hand route panel, in two parts. The body scrolls: the controls help, the legend,
// the route toggles and the view section, each under a header that opens and closes, then the
// per-segment breakdown, the interstellar leg when the route has one, the detail totals, and
// for an interstellar route its fuel estimate and the stock planner button. The footer holds
// the headline total and the vehicle dV bar, and the window pins it below the body so the
// answer stays in view whatever is open above it. Plain ImGui widgets carry the text; the
// colored bar is drawn with DrawList primitives so its green/yellow/red is exact and
// independent of the binding's styling helpers.
//
// Every dV figure is shown with a leading "~": the whole map is a closed-form patched-conic
// estimate, so no number is exact and saying so up front is more honest than implying
// precision. The piloting margin inflates every shown dV by (1 + percent/100); it is applied
// here at display time, so the route and layout never change.
//
// The breakdown, totals and bar strings are built when the route, the margin, an option or the
// interstellar figures change, and drawn from that cache in between.
internal sealed class RoutePanelRenderer
{
    private static readonly byte4 BarBg = new byte4(34, 40, 50, 255);
    private static readonly byte4 BarText = new byte4(245, 248, 252, 255);
    private static readonly byte4 BarTextShadow = new byte4(0, 0, 0, 220);
    private static readonly byte4 Green = new byte4(70, 168, 104, 255);
    private static readonly byte4 Yellow = new byte4(206, 176, 72, 255);
    private static readonly byte4 Red = new byte4(206, 84, 72, 255);

    private const float BarHeight = 22f;

    private readonly List<(string Text, bool Disabled)> _breakdown = new();
    private readonly List<(string Text, bool Disabled)> _totals = new();
    private TextKey? _textKey;
    private RouteTotalsText _headline;
    // ImGui.TextDisabled reads its text as a printf format, so the "%" of the margin note is
    // doubled, else "12% piloting" prints a pointer for "%p".
    private string _marginLineFormat = "";

    private string _barLabel = "";
    private float _barLabelW;
    private long _barNeededKey = long.MinValue;
    private long _barAvailableKey = long.MinValue;

    // Which sections are open. The help sections start closed so the panel opens on the route
    // controls. Reset leaves these alone, so a rebuild or a re-root keeps the player's choice.
    private bool _controlsOpen;
    private bool _legendOpen;
    private bool _routeOpen = true;
    private bool _viewOpen = true;

    private readonly record struct TextKey(
        RouteSummary Summary,
        double DvScale,
        int MarginPercent,
        bool PlaneChange,
        bool ReturnTrip,
        int SectionVersion,
        bool SectionView);

    public void Reset()
    {
        _breakdown.Clear();
        _totals.Clear();
        _textKey = null;
        _barNeededKey = long.MinValue;
    }

    // Whether the route has a footer to show: a route that goes somewhere, and for an
    // interstellar route only once its leg has an estimate.
    public static bool HasFooter(RouteSummary? summary, InterstellarSection? section)
    {
        if (summary == null || summary.IsEmpty)
            return false;
        return summary.Interstellar == null || section is { HasView: true };
    }

    // The height DrawFooter takes, with the item spacing in front of it, so the window can size
    // the scrolling body before the body is drawn. The bar row is at least one text line high,
    // so the "n/a" line fits the same room.
    public static float FooterHeight(int marginPercent)
    {
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        int lines = marginPercent > 0 ? 2 : 1;
        return spacing + (1f + spacing) + lines * ImGui.GetTextLineHeightWithSpacing() + BarRowHeight() + spacing;
    }

    // The game scales its font with the interface scale, so the bar grows with the text line and
    // its label never sticks out of it.
    private static float BarRowHeight()
    {
        return Math.Max(BarHeight, ImGui.GetTextLineHeight() + 6f);
    }

    public PanelResult DrawBody(RouteOptions options, ViewOptions view, RouteSummary? summary,
        InterstellarSection? section, bool severalSystems, bool rowBreaks)
    {
        bool changed = false;
        bool rebuild = false;

        if (Section("##dvmsec_controls"u8, "Controls"u8, ref _controlsOpen))
            LegendRenderer.DrawControls(severalSystems);
        if (Section("##dvmsec_legend"u8, "Legend"u8, ref _legendOpen))
            LegendRenderer.DrawLegend(severalSystems, rowBreaks);

        bool interstellar = summary?.Interstellar != null;

        if (Section("##dvmsec_route"u8, "Route options"u8, ref _routeOpen))
            changed = DrawRouteOptions(options, summary, interstellar);
        if (Section("##dvmsec_view"u8, "View"u8, ref _viewOpen))
            rebuild = DrawViewOptions(view);

        ImGui.Separator();

        if (summary == null)
        {
            ImGui.TextDisabled(severalSystems ? "Click a body or a star system to plan a route." : "Click a body to plan a route.");
            return new PanelResult(changed, rebuild);
        }
        if (summary.IsEmpty)
        {
            ImGui.TextDisabled("You are already here.");
            return new PanelResult(changed, rebuild);
        }

        InterstellarSection? leg = interstellar ? section : null;
        EnsureText(options, view, summary, leg);

        ImGui.SeparatorText("Breakdown"u8);
        DrawLineList(_breakdown);
        if (leg != null)
        {
            leg.DrawControls();
            leg.DrawReadouts();
            // A control change re-evaluates the leg, so the totals follow at once.
            EnsureText(options, view, summary, leg);
        }

        if ((leg == null || leg.HasView) && _totals.Count > 0)
        {
            ImGui.Separator();
            DrawLineList(_totals);
        }

        if (leg != null)
        {
            if (leg.HasView && leg.Feasibility != null)
                ConsoleUi.StatusText(leg.Feasibility, leg.FeasibilityTone);
            leg.DrawFuel();
            leg.DrawStockPlannerButton();
        }

        return new PanelResult(changed, rebuild);
    }

    // The headline total, the margin note and the vehicle bar. It draws nothing when HasFooter
    // is false, so a route that went away while the body was drawn leaves an empty footer for
    // one frame instead of stale figures.
    public void DrawFooter(RouteOptions options, ViewOptions view, RouteSummary? summary,
        InterstellarSection? section, double? availableDv)
    {
        if (!HasFooter(summary, section))
            return;
        InterstellarSection? leg = summary!.Interstellar != null ? section : null;
        EnsureText(options, view, summary, leg);

        ImGui.Separator();
        ImGui.Text(_headline.Headline);
        if (_headline.MarginLine.Length > 0)
            ImGui.TextDisabled(_marginLineFormat);
        DrawVehicleBar(_headline.Needed, availableDv);
    }

    // A section header in the console look of the rest of the panel. It draws the title and a
    // rule to the right edge as SeparatorText does, with an arrow in front that shows the state,
    // and the whole row toggles. The bool is the open state, so it survives rebuilds without relying on
    // ImGui's per-id storage.
    private static bool Section(ReadOnlySpan<byte> id, ReadOnlySpan<byte> title, ref bool open)
    {
        float2 pos = ImGui.GetCursorScreenPos();
        float width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        float height = ImGui.GetFrameHeight();
        if (ImGui.InvisibleButton(id, new float2(width, height)))
            open = !open;
        bool hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        uint text = ImGui.GetColorU32(ImGuiCol.Text);
        uint rule = ImGui.GetColorU32(hovered ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator);
        float cy = pos.Y + height * 0.5f;
        float s = MathF.Max(3f, height * 0.18f);
        float ax = pos.X + s + 2f;
        if (open)
        {
            var a = new float2(ax - s, cy - s * 0.6f);
            var b = new float2(ax + s, cy - s * 0.6f);
            var c = new float2(ax, cy + s * 0.8f);
            dl.AddTriangleFilled(in a, in b, in c, text);
        }
        else
        {
            var a = new float2(ax - s * 0.6f, cy - s);
            var b = new float2(ax - s * 0.6f, cy + s);
            var c = new float2(ax + s * 0.8f, cy);
            dl.AddTriangleFilled(in a, in b, in c, text);
        }

        float2 size = ImGui.CalcTextSize(title);
        var textPos = new float2(ax + s + 6f, cy - size.Y * 0.5f);
        dl.AddText(textPos, text, title);
        float right = pos.X + width;
        float lineStart = textPos.X + size.X + 6f;
        if (lineStart < right)
        {
            var from = new float2(lineStart, cy);
            var to = new float2(right, cy);
            dl.AddLine(in from, in to, rule, 1f);
        }
        return open;
    }

    private static bool DrawRouteOptions(RouteOptions options, RouteSummary? summary, bool interstellar)
    {
        bool changed = false;
        changed |= ConsoleUi.CheckboxRow("FROM SURFACE".AsSpan(), "DvmFromSurface".AsSpan(), ref options.FromSurface);
        changed |= ConsoleUi.CheckboxRow("LAND AT DESTINATION".AsSpan(), "DvmLand".AsSpan(), ref options.LandAtDestination);

        // An interstellar route has no aerobrake (arrival speeds that high are not modelled),
        // no separate plane change (the leg already aims through every plane) and no return
        // leg, so those toggles show as hints and keep their values for the next route.
        if (interstellar)
        {
            ImGui.TextDisabled("Aerobraking (not on an interstellar arrival)");
            ImGui.TextDisabled("Plane change (in the interstellar estimate)");
            ImGui.TextDisabled("Return trip (not for interstellar routes)");
            return changed;
        }

        // Aerobraking only makes sense when the route captures into a body with a usable
        // atmosphere; show a disabled hint otherwise so the toggle does not look broken.
        if (summary != null && !summary.HasAerobrakeOption)
            ImGui.TextDisabled("Aerobraking (no atmosphere on route)");
        else
            changed |= ConsoleUi.CheckboxRow("AEROBRAKING AT ARRIVAL".AsSpan(), "DvmAero".AsSpan(), ref options.Aerobraking);

        changed |= ConsoleUi.CheckboxRow("INCLUDE PLANE CHANGE".AsSpan(), "DvmPlaneChange".AsSpan(), ref options.IncludePlaneChange);
        changed |= ConsoleUi.CheckboxRow("SHOW RETURN TRIP".AsSpan(), "DvmReturn".AsSpan(), ref options.ShowReturnTrip);

        // The return aerobrakes at the origin body, a separate burn from the outbound
        // capture, so it gets its own toggle, shown only once a return trip is on.
        if (options.ShowReturnTrip)
        {
            if (summary != null && !summary.HasReturnAerobrakeOption)
                ImGui.TextDisabled("Aerobraking on return (airless origin)");
            else
                changed |= ConsoleUi.CheckboxRow("AEROBRAKING ON RETURN".AsSpan(), "DvmAeroReturn".AsSpan(), ref options.AerobrakingReturn);
        }
        return changed;
    }

    // The view section: which bodies are shown (rebuilds the tree) and the display-only
    // settings (transfer times, body markers, piloting margin). Returns whether a
    // visibility change requires a rebuild; the display-only settings are read live and
    // signal nothing.
    private static bool DrawViewOptions(ViewOptions view)
    {
        bool rebuild = false;

        rebuild |= ConsoleUi.CheckboxRow("SHOW MINOR BODIES".AsSpan(), "DvmMinor".AsSpan(), ref view.ShowMinorBodies);
        // Comets are a subset of minor bodies, so the comet toggle only bites while minor
        // bodies are shown; otherwise they are already hidden.
        if (view.ShowMinorBodies)
            rebuild |= ConsoleUi.CheckboxRow("SHOW COMETS".AsSpan(), "DvmComets".AsSpan(), ref view.ShowComets);
        else
            ImGui.TextDisabled("Show comets (minor bodies hidden)");

        ConsoleUi.CheckboxRow("SHOW TRANSFER TIMES".AsSpan(), "DvmTimes".AsSpan(), ref view.ShowTransferTimes);
        // These are the ring ellipse, the atmosphere/jet halo and the aerobrake arrow - body
        // feature markers, not just "feasibility", so the label says what it actually hides.
        ConsoleUi.CheckboxRow("SHOW BODY MARKERS".AsSpan(), "DvmBodyMarkers".AsSpan(), ref view.ShowBodyMarkers);

        // Piloting margin: a full-width 0-50% slider that inflates every shown dV (display
        // only). The label rides above a full-width slider that shows the current percent in
        // its grab.
        ImGui.Text("Piloting margin"u8);
        int margin = view.PilotingMarginPercent;
        ImGui.PushItemWidth(-1f);
        if (ImGui.SliderInt("##dvmargin"u8, ref margin, 0, 50, "%u%%"))
            view.PilotingMarginPercent = Math.Clamp(margin, 0, 50);
        ImGui.PopItemWidth();

        return rebuild;
    }

    private void EnsureText(RouteOptions options, ViewOptions view, RouteSummary summary, InterstellarSection? leg)
    {
        var key = new TextKey(summary, view.DvScale, view.PilotingMarginPercent, options.IncludePlaneChange,
            options.ShowReturnTrip, leg?.ViewVersion ?? 0, leg?.HasView ?? false);
        if (_textKey == key)
            return;
        _textKey = key;
        _breakdown.Clear();

        double scale = view.DvScale;
        InterstellarRoute? route = summary.Interstellar;
        bool legView = leg is { HasView: true };
        for (int i = 0; i <= summary.Segments.Count; i++)
        {
            if (route != null && i == route.BreakdownIndex && legView)
            {
                _breakdown.Add((leg!.DepartLine, false));
                _breakdown.Add((leg.CoastLine, true));
                _breakdown.Add((leg.CaptureLine, false));
            }
            if (i == summary.Segments.Count)
                break;
            RouteSegment seg = summary.Segments[i];
            _breakdown.Add(seg.Aerobraked
                ? (seg.Label + ": aerobrake (0)", true)
                : (seg.Label + ": " + Format.Dv(seg.Dv * scale), false));
        }

        var input = new TotalsInput(
            OutboundDv: route != null && legView ? leg!.RouteTotal : summary.OutboundDv * scale,
            ReturnDv: summary.ReturnDv * scale,
            PlaneChangeDv: options.IncludePlaneChange ? summary.PlaneChangeDv * scale : 0.0,
            TransferTimeSeconds: summary.TransferTimeSeconds,
            AerobrakeBodyId: summary.AerobrakeApplied ? summary.AerobrakeBodyId : null,
            ReturnAerobrakeBodyId: summary.ReturnAerobrakeApplied ? summary.ReturnAerobrakeBodyId : null,
            ShowReturnTrip: options.ShowReturnTrip,
            MarginPercent: view.PilotingMarginPercent,
            Interstellar: route != null,
            CoastTimeLine: legView ? leg!.CoastTimeLine : null);
        _headline = RouteTotals.Build(in input, _totals);
        _marginLineFormat = _headline.MarginLine.Replace("%", "%%");
    }

    // The lines wrap at the panel edge, so a long label never pushes its figure out of view.
    private static void DrawLineList(List<(string Text, bool Disabled)> lines)
    {
        ImGui.PushTextWrapPos(0f);
        for (int i = 0; i < lines.Count; i++)
        {
            (string text, bool disabled) = lines[i];
            if (disabled)
                ImGui.TextDisabled(text);
            else
                ImGui.Text(text);
        }
        ImGui.PopTextWrapPos();
    }

    private void DrawVehicleBar(double needed, double? availableDv)
    {
        if (availableDv is not double available)
        {
            // Null when there is no controlled vehicle (e.g. the editor) or its dV reads
            // non-finite.
            ImGui.TextDisabled("Vehicle dV: n/a");
            return;
        }

        byte4 fill = (needed <= 1.0 || available >= 1.1 * needed) ? Green
            : available >= 0.9 * needed ? Yellow
            : Red;

        float2 pos = ImGui.GetCursorScreenPos();
        float width = Math.Max(40f, ImGui.GetContentRegionAvail().X);
        float height = BarRowHeight();

        // The bar reads "needed out of available": it fills with the fraction of the
        // vehicle's dV this route spends, so a cheap route barely fills it and a route that
        // exceeds the vehicle's dV fills it completely (and reads red).
        double ratio = available > 1.0 ? needed / available : (needed > 0.0 ? 1.0 : 0.0);
        float fillWidth = (float)(width * Math.Clamp(ratio, 0.0, 1.0));

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float2 barMax = pos + new float2(width, height);
        float2 fillMax = pos + new float2(fillWidth, height);
        dl.AddRectFilled(in pos, in barMax, BarBg, 3f);
        dl.AddRectFilled(in pos, in fillMax, fill, 3f);

        // "needed out of available": the needed figure is our estimate (~), the available
        // is the vehicle's total staged dV.
        long neededKey = (long)Math.Round(Math.Clamp(needed, -1e15, 1e15));
        long availableKey = (long)Math.Round(Math.Clamp(available, -1e15, 1e15));
        if (neededKey != _barNeededKey || availableKey != _barAvailableKey)
        {
            _barNeededKey = neededKey;
            _barAvailableKey = availableKey;
            _barLabel = "~" + Format.DvNumber(needed) + " / " + Format.DvNumber(available) + " m/s";
            _barLabelW = ImGui.CalcTextSize(_barLabel).X;
        }
        string label = _barLabel;
        float textH = ImGui.GetTextLineHeight();
        float2 textPos = pos + new float2((width - _barLabelW) * 0.5f, (height - textH) * 0.5f);
        // A drop shadow keeps the label legible over green, yellow or red fill alike.
        float2 shadowPos = textPos + new float2(1f, 1f);
        dl.AddText(in shadowPos, BarTextShadow, label);
        dl.AddText(in textPos, BarText, label);

        // Reserve the row so widgets after the bar do not draw over it. The bar has no label
        // of its own, so hovering it says what the two figures are.
        ImGui.Dummy(new float2(width, height));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("This route's dV / your vehicle's total dV"u8);
    }
}
