using System;
using System.Collections.Generic;
using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using DeltaVMap.Model;

namespace DeltaVMap.Render;

// Which pairs the transfer-window section shows. Overview lists every sibling of the root;
// Selected route would focus the selected route's interplanetary leg. Overview is wired now;
// the route-focused mode is scaffolded and lands in a later step.
internal enum TransferWindowMode
{
    Overview,
    SelectedRoute
}

// The content of the "Transfer windows" overlay (the floating frame itself is positioned by
// MapWindow, bottom-left in the canvas): a footer toggle (a real arrow) pinned at the bottom
// so it does not move when the overlay expands upward, a mode selector, and a narrow per-
// sibling list of the target, the countdown to the next window and the ejection angle. The
// soonest window is highlighted; the rest of the figures (lead and current phase, synodic
// period, transfer time, relative inclination) are one hover away on each row. It reuses the
// panel's time formatting and color vocabulary; the clock-face diagram and the on-map markers
// are separate later steps.
internal static class TransferWindowRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // A subtle wash on the soonest window's row and a brighter name, reusing the panel green.
    private static readonly byte4 SoonestRowBg = new byte4(70, 168, 104, 60);
    private static readonly byte4 SoonestText = new byte4(150, 226, 178, 255);

    private const ImGuiTableFlags TableFlags =
        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersV | ImGuiTableFlags.SizingStretchProp
        | ImGuiTableFlags.ScrollY;

    private static readonly byte4 ArrowColor = new byte4(210, 220, 235, 255);

    // Draw the overlay content inside the frame MapWindow positions. When expanded it draws the
    // mode selector and the compact per-sibling list from the top; the toggle is drawn last and
    // pinned to the bottom of the frame, so collapsing / expanding never moves it. Returns
    // whether the toggle was clicked this frame, so the caller can flip the expanded state.
    public static bool DrawOverlay(IReadOnlyList<TransferWindowInfo> windows, ref TransferWindowMode mode, bool expanded)
    {
        if (expanded)
        {
            DrawModeSelector(ref mode);

            if (mode == TransferWindowMode.SelectedRoute)
                ImGui.TextDisabled("Selected-route timing is coming in a later step. Showing Overview for now.");

            if (windows.Count == 0)
            {
                ImGui.TextDisabled("No sibling destinations from this root.");
            }
            else
            {
                // Source (the root) named once; the "*" marker and the dropped figures are
                // explained here and in the per-row hover.
                ImGui.TextDisabled("From " + windows[0].SourceId + " - * eccentric; hover a row for detail");
                float footerH = ImGui.GetFrameHeight() + 4f;
                float listH = ImGui.GetContentRegionAvail().Y - footerH;
                DrawCompactList(windows, listH);
            }

            // Push the toggle to the bottom edge so it stays put across collapse / expand.
            float bottomY = ImGui.GetWindowHeight() - ImGui.GetFrameHeight() - 4f;
            if (bottomY > ImGui.GetCursorPosY())
                ImGui.SetCursorPosY(bottomY);
        }

        return DrawToggle(windows, expanded);
    }

    // The footer toggle: a full-width clickable row (stable id via "###" so the live label can
    // change between press and release without dropping the click) with a real triangle arrow
    // drawn at its left.
    private static bool DrawToggle(IReadOnlyList<TransferWindowInfo> windows, bool expanded)
    {
        string label = "     " + CollapsedHeaderLabel(windows) + "###dvtwtoggle";
        bool clicked = ImGui.Selectable(label, false, ImGuiSelectableFlags.None, (float2?)null);

        float2 rmin = ImGui.GetItemRectMin();
        float2 rmax = ImGui.GetItemRectMax();
        DrawArrow(rmin, rmax, expanded);
        return clicked;
    }

    // A small filled triangle at the left of the toggle row: pointing down when expanded
    // (click to collapse) and up when collapsed (click to expand the frame upward).
    private static void DrawArrow(float2 rmin, float2 rmax, bool expanded)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float cx = rmin.X + 9f;
        float cy = (rmin.Y + rmax.Y) * 0.5f;
        float s = MathF.Max(3f, (rmax.Y - rmin.Y) * 0.20f);

        if (expanded)
        {
            var a = new float2(cx - s, cy - s * 0.6f);
            var b = new float2(cx + s, cy - s * 0.6f);
            var c = new float2(cx, cy + s * 0.8f);
            dl.AddTriangleFilled(in a, in b, in c, ArrowColor);
        }
        else
        {
            var a = new float2(cx - s, cy + s * 0.6f);
            var b = new float2(cx + s, cy + s * 0.6f);
            var c = new float2(cx, cy - s * 0.8f);
            dl.AddTriangleFilled(in a, in b, in c, ArrowColor);
        }
    }

    private static void DrawModeSelector(ref TransferWindowMode mode)
    {
        if (ImGui.RadioButton("Overview"u8, mode == TransferWindowMode.Overview))
            mode = TransferWindowMode.Overview;
        ImGui.SameLine();
        if (ImGui.RadioButton("Selected route"u8, mode == TransferWindowMode.SelectedRoute))
            mode = TransferWindowMode.SelectedRoute;
    }

    private static void DrawCompactList(IReadOnlyList<TransferWindowInfo> windows, float height)
    {
        int soonest = SoonestIndex(windows);

        // Fill the space between the header line and the footer toggle; the table scrolls
        // internally when a dense hub has more siblings than fit.
        var outer = new float2(0f, Math.Max(height, ImGui.GetFrameHeight() * 2f));

        if (!ImGui.BeginTable("##dvtwlist"u8, 3, TableFlags, (float2?)outer))
            return;
        try
        {
            ImGui.TableSetupColumn("Target"u8, ImGuiTableColumnFlags.WidthStretch, 1.7f, 0);
            ImGui.TableSetupColumn("In"u8, ImGuiTableColumnFlags.WidthStretch, 1.1f, 1);
            ImGui.TableSetupColumn("Eject"u8, ImGuiTableColumnFlags.WidthStretch, 0.9f, 2);
            // Keep the header visible while the body scrolls.
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            for (int i = 0; i < windows.Count; i++)
                DrawCompactRow(windows[i], i == soonest);
        }
        finally
        {
            // Always close the table, even on an unexpected throw, so the table stack stays
            // balanced for the rest of the overlay.
            ImGui.EndTable();
        }
    }

    private static void DrawCompactRow(TransferWindowInfo w, bool soonest)
    {
        ImGui.TableNextRow();
        if (soonest)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(SoonestRowBg));

        ImGui.TableNextColumn();
        string name = w.IsApproximate ? (w.TargetId + " *") : w.TargetId;
        if (soonest)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, SoonestText);
            ImGui.Text(name);
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.Text(name);
        }
        // Hovering the name cell shows the full labeled detail for the pair (the figures the
        // compact list drops to fit the narrow overlay).
        if (ImGui.IsItemHovered())
            ShowRowTooltip(w);

        ImGui.TableNextColumn();
        ImGui.Text("~" + RoutePanelRenderer.FormatTime(w.TimeToWindowSeconds));

        ImGui.TableNextColumn();
        ImGui.Text(Deg(w.EjectionAngle) + (w.EjectionAhead ? " >" : " <"));
    }

    private static void ShowRowTooltip(TransferWindowInfo w)
    {
        ImGui.BeginTooltip();
        ImGui.Text(w.SourceId + " -> " + w.TargetId);
        ImGui.Separator();
        ImGui.Text("Optimal (lead) phase: " + DegSigned(w.TargetPhaseAngle) + " deg");
        ImGui.Text("Current phase: " + DegSigned(w.CurrentPhaseAngle) + " deg");
        ImGui.Text("Window in: ~" + RoutePanelRenderer.FormatTime(w.TimeToWindowSeconds));
        ImGui.Text("Recurs every: ~" + RoutePanelRenderer.FormatTime(w.SynodicPeriodSeconds));
        ImGui.Text("Transfer time: ~" + RoutePanelRenderer.FormatTime(w.TransferTimeSeconds));
        ImGui.Text("Ejection angle: " + Deg(w.EjectionAngle) + " deg "
            + (w.EjectionAhead ? "ahead of" : "behind") + " prograde");
        ImGui.Text("Relative inclination: " + Deg(w.RelativeInclination) + " deg");
        if (w.Retrograde)
            ImGui.TextDisabled("Retrograde sibling (orbits the hub the opposite way)");
        if (w.IsApproximate)
            ImGui.TextDisabled("* eccentric or open orbit, window less precise");
        ImGui.EndTooltip();
    }

    // The index of the window with the soonest (smallest finite) countdown, or -1 when none.
    private static int SoonestIndex(IReadOnlyList<TransferWindowInfo> windows)
    {
        int best = -1;
        double bestT = double.MaxValue;
        for (int i = 0; i < windows.Count; i++)
        {
            double t = windows[i].TimeToWindowSeconds;
            if (double.IsFinite(t) && t < bestT)
            {
                bestT = t;
                best = i;
            }
        }
        return best;
    }

    private static string CollapsedHeaderLabel(IReadOnlyList<TransferWindowInfo> windows)
    {
        int soonest = SoonestIndex(windows);
        if (soonest < 0)
            return "Transfer windows";
        TransferWindowInfo w = windows[soonest];
        return "Transfer windows - next: " + w.TargetId + " ~" + RoutePanelRenderer.FormatTime(w.TimeToWindowSeconds);
    }

    private static string Deg(double radians)
    {
        return Math.Round(radians * 180.0 / Math.PI).ToString("0", Inv);
    }

    private static string DegSigned(double radians)
    {
        return Math.Round(radians * 180.0 / Math.PI).ToString("+0;-0;0", Inv);
    }
}
