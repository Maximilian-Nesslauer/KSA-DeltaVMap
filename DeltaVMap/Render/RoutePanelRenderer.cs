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

// The right-hand route panel, top to bottom: the legend (controls + symbol key), the route
// toggles, a view section (visibility + piloting margin), the per-segment breakdown, the
// interstellar leg when the route has one, the totals and transfer time, the vehicle dV bar,
// and for an interstellar route its fuel estimate and the stock planner button. Plain ImGui
// widgets carry the text; the colored bar is drawn with DrawList primitives so its
// green/yellow/red is exact and independent of the binding's styling helpers.
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
    private double _needed;

    private string _barLabel = "";
    private float _barLabelW;
    private long _barNeededKey = long.MinValue;
    private long _barAvailableKey = long.MinValue;

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

    public PanelResult Draw(RouteOptions options, ViewOptions view, RouteSummary? summary, double? availableDv,
        InterstellarSection? section, bool severalSystems)
    {
        bool changed = false;
        bool rebuild = false;

        // The legend (controls + symbol key) sits at the top, so the panel opens by
        // explaining the map before the route controls.
        LegendRenderer.Draw(severalSystems);

        bool interstellar = summary?.Interstellar != null;

        ImGui.SeparatorText("Route options"u8);
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
        }
        else
        {
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
        }

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
            // A control change re-evaluates the leg, so the totals below follow at once.
            EnsureText(options, view, summary, leg);
        }

        if (leg == null || leg.HasView)
        {
            ImGui.Separator();
            DrawLineList(_totals);
            DrawVehicleBar(_needed, availableDv);
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

    // The view section: which bodies are shown (rebuilds the tree) and the display-only
    // settings (transfer times, body markers, piloting margin). Returns whether a
    // visibility change requires a rebuild; the display-only settings are read live and
    // signal nothing.
    private static bool DrawViewOptions(ViewOptions view)
    {
        bool rebuild = false;
        ImGui.SeparatorText("View"u8);

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
        _totals.Clear();

        double scale = view.DvScale;
        InterstellarRoute? route = summary.Interstellar;
        for (int i = 0; i <= summary.Segments.Count; i++)
        {
            if (route != null && i == route.BreakdownIndex && leg != null && leg.HasView)
            {
                _breakdown.Add((leg.DepartLine, false));
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

        // A non-zero piloting margin silently inflates every figure, which reads as "the
        // numbers are wrong" if you forget it is on, so call it out on the total line.
        int marginPercent = view.PilotingMarginPercent;
        string marginNote = marginPercent > 0 ? " (incl. +" + Format.Percent(marginPercent) + "% margin)" : "";

        if (route != null)
        {
            _needed = leg is { HasView: true } ? leg.RouteTotal : summary.OutboundDv * scale;
            _totals.Add(("Total: " + Format.Dv(_needed) + marginNote, false));
            if (leg is { HasView: true })
                _totals.Add((leg.CoastTimeLine, true));
            return;
        }

        // Plane change is additive: it is incurred per interplanetary leg, so a round trip
        // pays it on both the outbound and the return. Every figure is scaled by the piloting
        // margin so the panel and the on-map badges agree.
        double planeChange = (options.IncludePlaneChange ? summary.PlaneChangeDv : 0.0) * scale;
        double outboundTotal = summary.OutboundDv * scale + planeChange;
        double returnTotal = summary.ReturnDv * scale + planeChange;
        double roundTrip = outboundTotal + returnTotal;
        _needed = options.ShowReturnTrip ? roundTrip : outboundTotal;

        // Plane change sits above the total and is rolled into it (the user opted in).
        if (planeChange > 0.0)
            _totals.Add(("+ plane change: " + Format.Dv(planeChange), true));
        _totals.Add(("Total: " + Format.Dv(outboundTotal) + marginNote, false));
        if (options.ShowReturnTrip)
        {
            _totals.Add(("Return: " + Format.Dv(returnTotal), true));
            _totals.Add(("Round trip: " + Format.Dv(roundTrip), false));
        }
        if (summary.TransferTimeSeconds > 0.0)
        {
            // The summed transfer time is the outbound coast; a round trip is roughly
            // twice it, so label it one-way when the return is shown.
            string suffix = options.ShowReturnTrip ? " (one way)" : "";
            _totals.Add(("Transfer time: " + Format.Duration(summary.TransferTimeSeconds) + suffix, true));
        }
        if (summary.AerobrakeApplied && summary.AerobrakeBodyId != null)
            _totals.Add(("Aerobrake at " + summary.AerobrakeBodyId, true));
        if (summary.ReturnAerobrakeApplied && summary.ReturnAerobrakeBodyId != null)
            _totals.Add(("Aerobrake on return at " + summary.ReturnAerobrakeBodyId, true));
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
        ImGui.Separator();
        if (availableDv is not double available)
        {
            // Null when there is no controlled vehicle (e.g. the editor) or its dV reads
            // non-finite.
            ImGui.TextDisabled("Vehicle dV: n/a");
            return;
        }

        ImGui.Text("Vehicle dV"u8);

        byte4 fill = (needed <= 1.0 || available >= 1.1 * needed) ? Green
            : available >= 0.9 * needed ? Yellow
            : Red;

        float2 pos = ImGui.GetCursorScreenPos();
        float width = Math.Max(40f, ImGui.GetContentRegionAvail().X);

        // The bar reads "needed out of available": it fills with the fraction of the
        // vehicle's dV this route spends, so a cheap route barely fills it and a route that
        // exceeds the vehicle's dV fills it completely (and reads red).
        double ratio = available > 1.0 ? needed / available : (needed > 0.0 ? 1.0 : 0.0);
        float fillWidth = (float)(width * Math.Clamp(ratio, 0.0, 1.0));

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float2 barMax = pos + new float2(width, BarHeight);
        float2 fillMax = pos + new float2(fillWidth, BarHeight);
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
        float2 textPos = pos + new float2((width - _barLabelW) * 0.5f, (BarHeight - textH) * 0.5f);
        // A drop shadow keeps the label legible over green, yellow or red fill alike.
        float2 shadowPos = textPos + new float2(1f, 1f);
        dl.AddText(in shadowPos, BarTextShadow, label);
        dl.AddText(in textPos, BarText, label);

        // Reserve the row so widgets after the bar do not draw over it.
        ImGui.Dummy(new float2(width, BarHeight));
    }
}
