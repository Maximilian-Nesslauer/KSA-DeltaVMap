using System;
using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using DeltaVMap.Route;

namespace DeltaVMap.Render;

// The right-hand route panel: the toggles, the per-segment breakdown, the totals and
// transfer time, and the vehicle dV comparison bar. Returns true when a toggle changed
// so the window recomputes the route next frame. Plain ImGui widgets carry the text;
// the colored bar is drawn with DrawList primitives so its green/yellow/red is exact
// and independent of the binding's styling helpers.
//
// Every dV figure is shown with a leading "~": the whole map is a closed-form
// patched-conic estimate, so no number is exact and saying so up front is more honest
// than implying precision.
internal static class RoutePanelRenderer
{
    private static readonly byte4 BarBg = new byte4(34, 40, 50, 255);
    private static readonly byte4 BarText = new byte4(245, 248, 252, 255);
    private static readonly byte4 BarTextShadow = new byte4(0, 0, 0, 220);
    private static readonly byte4 Green = new byte4(70, 168, 104, 255);
    private static readonly byte4 Yellow = new byte4(206, 176, 72, 255);
    private static readonly byte4 Red = new byte4(206, 84, 72, 255);

    private const float BarHeight = 22f;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool Draw(RouteOptions options, RouteSummary? summary, double? availableDv)
    {
        bool changed = false;

        ImGui.SeparatorText("Route options"u8);
        changed |= ImGui.Checkbox("From surface"u8, ref options.FromSurface);
        changed |= ImGui.Checkbox("Land at destination"u8, ref options.LandAtDestination);

        // Aerobraking only makes sense when the route captures into a body with a usable
        // atmosphere; show a disabled hint otherwise so the toggle does not look broken.
        if (summary != null && !summary.HasAerobrakeOption)
            ImGui.TextDisabled("Aerobraking (no atmosphere on route)");
        else
            changed |= ImGui.Checkbox("Aerobraking at arrival"u8, ref options.Aerobraking);

        changed |= ImGui.Checkbox("Include plane change"u8, ref options.IncludePlaneChange);
        changed |= ImGui.Checkbox("Show return trip"u8, ref options.ShowReturnTrip);

        // The return aerobrakes at the origin body, a separate burn from the outbound
        // capture, so it gets its own toggle, shown only once a return trip is on.
        if (options.ShowReturnTrip)
        {
            if (summary != null && !summary.HasReturnAerobrakeOption)
                ImGui.TextDisabled("Aerobraking on return (airless origin)");
            else
                changed |= ImGui.Checkbox("Aerobraking on return"u8, ref options.AerobrakingReturn);
        }

        ImGui.Separator();

        if (summary == null)
        {
            ImGui.TextDisabled("Click a body to plan a route.");
            LegendRenderer.Draw();
            return changed;
        }
        if (summary.IsEmpty)
        {
            ImGui.TextDisabled("You are already here.");
            LegendRenderer.Draw();
            return changed;
        }

        // Plane change is additive: it is incurred per interplanetary leg, so a round trip
        // pays it on both the outbound and the return.
        double planeChange = options.IncludePlaneChange ? summary.PlaneChangeDv : 0.0;
        double outboundTotal = summary.OutboundDv + planeChange;
        double returnTotal = summary.ReturnDv + planeChange;
        double roundTrip = outboundTotal + returnTotal;
        double needed = options.ShowReturnTrip ? roundTrip : outboundTotal;

        DrawBreakdown(summary);
        DrawTotals(options, summary, planeChange, outboundTotal, returnTotal, roundTrip);
        DrawVehicleBar(needed, availableDv);
        LegendRenderer.Draw();

        return changed;
    }

    private static void DrawBreakdown(RouteSummary summary)
    {
        ImGui.SeparatorText("Breakdown"u8);
        foreach (RouteSegment seg in summary.Segments)
        {
            // Build each line into a string local first: the binding picks ImGui's string
            // overload, whereas an interpolated literal would bind a String8 overload from
            // an assembly the mod does not reference.
            if (seg.Aerobraked)
            {
                string aero = seg.Label + ": aerobrake (0)";
                ImGui.TextDisabled(aero);
                continue;
            }
            string line = seg.Label + ": " + DvText(seg.Dv);
            ImGui.Text(line);
        }
    }

    private static void DrawTotals(
        RouteOptions options, RouteSummary summary,
        double planeChange, double outboundTotal, double returnTotal, double roundTrip)
    {
        ImGui.Separator();

        // Plane change sits above the total and is rolled into it (the user opted in).
        if (planeChange > 0.0)
        {
            string plane = "+ plane change: " + DvText(planeChange);
            ImGui.TextDisabled(plane);
        }

        string total = "Total: " + DvText(outboundTotal);
        ImGui.Text(total);

        if (options.ShowReturnTrip)
        {
            string ret = "Return: " + DvText(returnTotal);
            string round = "Round trip: " + DvText(roundTrip);
            ImGui.TextDisabled(ret);
            ImGui.Text(round);
        }

        if (summary.TransferTimeSeconds > 0.0)
        {
            // The summed transfer time is the outbound coast; a round trip is roughly
            // twice it, so label it one-way when the return is shown.
            string suffix = options.ShowReturnTrip ? " (one way)" : "";
            string transit = "Transfer time: " + FormatTime(summary.TransferTimeSeconds) + suffix;
            ImGui.TextDisabled(transit);
        }

        if (summary.AerobrakeApplied && summary.AerobrakeBodyId != null)
        {
            string aero = "Aerobrake at " + summary.AerobrakeBodyId;
            ImGui.TextDisabled(aero);
        }
        if (summary.ReturnAerobrakeApplied && summary.ReturnAerobrakeBodyId != null)
        {
            string aero = "Aerobrake on return at " + summary.ReturnAerobrakeBodyId;
            ImGui.TextDisabled(aero);
        }
    }

    private static void DrawVehicleBar(double needed, double? availableDv)
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
        // is the staged-analyzer total.
        string label = "~" + Fmt(needed) + " / " + Fmt(available) + " m/s";
        float2 ts = ImGui.CalcTextSize(label);
        float2 textPos = pos + new float2((width - ts.X) * 0.5f, (BarHeight - ts.Y) * 0.5f);
        // A drop shadow keeps the label legible over green, yellow or red fill alike.
        float2 shadowPos = textPos + new float2(1f, 1f);
        dl.AddText(in shadowPos, BarTextShadow, label);
        dl.AddText(in textPos, BarText, label);

        // Reserve the row so widgets after the bar do not draw over it.
        ImGui.Dummy(new float2(width, BarHeight));
    }

    // A dV figure for display: leading "~" (everything on the map is an estimate).
    private static string DvText(double dv)
    {
        return "~" + Fmt(dv) + " m/s";
    }

    private static string Fmt(double dv)
    {
        return Math.Round(dv).ToString("#,##0", Inv);
    }

    // Auto-scaled transfer time: minutes, hours, days, then years.
    private static string FormatTime(double seconds)
    {
        if (seconds < 3600.0)
            return $"{seconds / 60.0:0} min";
        if (seconds < 86400.0)
            return $"{seconds / 3600.0:0.0} h";
        double days = seconds / 86400.0;
        if (days < 365.25)
            return $"{days:0.0} d";
        return $"{days / 365.25:0.00} yr";
    }
}
