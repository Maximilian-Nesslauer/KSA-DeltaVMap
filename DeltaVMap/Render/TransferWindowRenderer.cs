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

// The collapsible "Transfer windows" section in the right-hand panel: a mode selector and a
// per-sibling table of the optimal lead angle, the live current phase, the countdown to the
// next window, the synodic period, the ejection angle and the transfer time. The soonest
// window is highlighted; the full labeled detail (including relative inclination) is one
// hover away on each row. It reuses the panel's time formatting and color vocabulary; the
// clock-face diagram and the on-map markers are separate later steps.
internal static class TransferWindowRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // A subtle wash on the soonest window's row and a brighter name, reusing the panel green.
    private static readonly byte4 SoonestRowBg = new byte4(70, 168, 104, 60);
    private static readonly byte4 SoonestText = new byte4(150, 226, 178, 255);

    private const ImGuiTableFlags TableFlags =
        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersV | ImGuiTableFlags.SizingStretchProp
        | ImGuiTableFlags.ScrollY;

    // Rows shown before the table scrolls; after the visibility filter a stock root has only a
    // handful of siblings, so this only bites on a dense modded hub.
    private const int MaxVisibleRows = 10;

    // Draw the section. Collapsed by default; the header shows the next window so it stays
    // useful while closed.
    public static void Draw(IReadOnlyList<TransferWindowInfo> windows, ref TransferWindowMode mode)
    {
        // The text before "###" updates with the soonest window so the collapsed header still
        // names the next one; the id after "###" is stable, so toggling state survives the
        // changing label.
        string header = CollapsedHeaderLabel(windows) + "###dvtransferwindows";
        if (!ImGui.CollapsingHeader(header))
            return;

        DrawModeSelector(ref mode);

        if (mode == TransferWindowMode.SelectedRoute)
            ImGui.TextDisabled("Selected-route timing is coming in a later step. Showing Overview for now.");

        if (windows.Count == 0)
        {
            ImGui.TextDisabled("No sibling destinations from this root.");
            return;
        }

        // Every window shares the same source (the root); name it once for context.
        ImGui.TextDisabled("From " + windows[0].SourceId + " to its siblings:");
        ImGui.TextDisabled("Lead/Now/Eject in degrees; * = eccentric orbit; hover a row for detail.");

        DrawTable(windows);
    }

    private static void DrawModeSelector(ref TransferWindowMode mode)
    {
        if (ImGui.RadioButton("Overview"u8, mode == TransferWindowMode.Overview))
            mode = TransferWindowMode.Overview;
        ImGui.SameLine();
        if (ImGui.RadioButton("Selected route"u8, mode == TransferWindowMode.SelectedRoute))
            mode = TransferWindowMode.SelectedRoute;
    }

    private static void DrawTable(IReadOnlyList<TransferWindowInfo> windows)
    {
        int soonest = SoonestIndex(windows);

        // Bound the table height so a dense hub scrolls instead of pushing the panel off-screen:
        // a header row plus up to MaxVisibleRows data rows.
        int rows = Math.Min(windows.Count, MaxVisibleRows);
        var outer = new float2(0f, (rows + 1) * ImGui.GetFrameHeight() + 4f);

        if (!ImGui.BeginTable("##dvtwtable"u8, 7, TableFlags, (float2?)outer))
            return;
        try
        {
            ImGui.TableSetupColumn("Target"u8, ImGuiTableColumnFlags.WidthStretch, 1.6f, 0);
            ImGui.TableSetupColumn("Lead"u8, ImGuiTableColumnFlags.WidthStretch, 1.0f, 1);
            ImGui.TableSetupColumn("Now"u8, ImGuiTableColumnFlags.WidthStretch, 1.0f, 2);
            ImGui.TableSetupColumn("In"u8, ImGuiTableColumnFlags.WidthStretch, 1.2f, 3);
            ImGui.TableSetupColumn("Every"u8, ImGuiTableColumnFlags.WidthStretch, 1.2f, 4);
            ImGui.TableSetupColumn("Eject"u8, ImGuiTableColumnFlags.WidthStretch, 1.1f, 5);
            ImGui.TableSetupColumn("ToF"u8, ImGuiTableColumnFlags.WidthStretch, 1.2f, 6);
            // Keep the header visible while the body scrolls.
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            for (int i = 0; i < windows.Count; i++)
                DrawRow(windows[i], i == soonest);
        }
        finally
        {
            // Always close the table, even on an unexpected throw, so the table stack stays
            // balanced for the rest of the panel.
            ImGui.EndTable();
        }
    }

    private static void DrawRow(TransferWindowInfo w, bool soonest)
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
        // Hovering the name cell shows the full labeled detail for the pair.
        if (ImGui.IsItemHovered())
            ShowRowTooltip(w);

        ImGui.TableNextColumn();
        ImGui.Text(DegSigned(w.TargetPhaseAngle));

        ImGui.TableNextColumn();
        ImGui.Text(DegSigned(w.CurrentPhaseAngle));

        ImGui.TableNextColumn();
        ImGui.Text("~" + RoutePanelRenderer.FormatTime(w.TimeToWindowSeconds));

        ImGui.TableNextColumn();
        ImGui.Text("~" + RoutePanelRenderer.FormatTime(w.SynodicPeriodSeconds));

        ImGui.TableNextColumn();
        ImGui.Text(Deg(w.EjectionAngle) + (w.EjectionAhead ? " >" : " <"));

        ImGui.TableNextColumn();
        ImGui.Text("~" + RoutePanelRenderer.FormatTime(w.TransferTimeSeconds));
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
