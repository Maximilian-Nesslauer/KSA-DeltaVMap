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

// What the overlay reports back per frame: whether the footer toggle was clicked (so the
// caller flips the expanded state), and which sibling a clock dot or list row is hovering (so
// the caller can highlight that body on the map).
internal readonly struct OverlayResult
{
    public readonly bool Toggled;
    public readonly string? HoverBodyId;

    public OverlayResult(bool toggled, string? hoverBodyId)
    {
        Toggled = toggled;
        HoverBodyId = hoverBodyId;
    }
}

// The content of the "Transfer windows" overlay (the floating frames are positioned by
// MapWindow, bottom-left in the canvas). The list panel holds a footer toggle (a real arrow)
// pinned at the bottom so it does not move when the overlay expands upward, a mode selector,
// and a narrow per-sibling list of the target, the countdown to the next window and the
// ejection angle; its soonest window is highlighted and the rest of the figures are one hover
// away on each row. The separate clock panel beside it holds the live polar clock-face: a top-
// down schematic of the hub and its bodies at their current phase, where the soonest (or
// hovered) window gets a required-position marker, an arc to the body's current position and
// the countdown. It reuses the panel's time formatting and the per-system color palette; the
// on-map markers are a separate later step.
internal static class TransferWindowRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // A subtle wash on the soonest window's row and a brighter name, reusing the panel green.
    private static readonly byte4 SoonestRowBg = new byte4(70, 168, 104, 60);
    private static readonly byte4 SoonestText = new byte4(150, 226, 178, 255);
    // A faint wash on the hovered row, distinct from the soonest green.
    private static readonly byte4 HoverRowBg = new byte4(96, 116, 156, 70);

    private const ImGuiTableFlags TableFlags =
        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersV | ImGuiTableFlags.SizingStretchProp
        | ImGuiTableFlags.ScrollY;

    private static readonly byte4 ArrowColor = new byte4(210, 220, 235, 255);

    // Clock-face palette.
    private static readonly byte4 HubColor = new byte4(200, 210, 225, 255);
    private static readonly byte4 FaintRing = new byte4(92, 102, 118, 120);
    private static readonly byte4 DefaultDot = new byte4(150, 160, 175, 255);
    private static readonly byte4 RequiredColor = new byte4(240, 200, 90, 255);
    private static readonly byte4 AngleLineColor = new byte4(176, 190, 210, 225);
    private static readonly byte4 LabelColor = new byte4(222, 230, 242, 255);
    private static readonly byte4 LabelShadow = new byte4(0, 0, 0, 205);
    private static readonly byte4 TitleColor = new byte4(150, 166, 190, 255);

    // The clock's usable radial band as a fraction of the panel side: bodies sit between the
    // inner and outer ring, log-scaled by semi-major axis.
    private const float RingInner = 0.16f;
    private const float RingOuter = 0.44f;

    // Hover pick radius (screen px) for a clock dot.
    private const float DotPickRadius = 8f;

    // Draw the list panel's content inside the frame MapWindow positions. When expanded it draws
    // the mode selector and the compact per-sibling list from the top; the toggle is drawn last
    // and pinned to the bottom of the frame, so collapsing / expanding never moves it (the clock-
    // face lives in its own panel beside this one). highlightBodyId is the body to emphasize
    // (last frame's hover, so a hovered row also lights its clock dot); clockHidden is true when
    // the clock panel could not fit beside this one, to note it. The return reports this frame's
    // click and hovered body.
    public static OverlayResult DrawOverlay(
        IReadOnlyList<TransferWindowInfo> windows, ref TransferWindowMode mode, bool expanded,
        string? highlightBodyId, bool clockHidden)
    {
        string? hover = null;

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
                // explained in the per-row hover.
                ImGui.TextDisabled("From " + windows[0].SourceId + " - hover a row for detail");
                if (clockHidden)
                    ImGui.TextDisabled("(widen the window to show the phase clock)");

                float footerH = ImGui.GetFrameHeight() + 4f;
                float listH = ImGui.GetContentRegionAvail().Y - footerH;
                hover = DrawCompactList(windows, highlightBodyId, listH) ?? hover;
            }

            // Push the toggle to the bottom edge so it stays put across collapse / expand.
            float bottomY = ImGui.GetWindowHeight() - ImGui.GetFrameHeight() - 4f;
            if (bottomY > ImGui.GetCursorPosY())
                ImGui.SetCursorPosY(bottomY);
        }

        bool toggled = DrawToggle(windows, expanded);
        return new OverlayResult(toggled, hover);
    }

    // Draw the clock-face filling its own panel (a second overlay frame MapWindow places beside
    // the list, big enough to read). Draws a small title in the top-left corner (outside the
    // circle), centers the square clock, and returns the hovered body, or null.
    public static string? DrawClockPanel(IReadOnlyList<TransferWindowInfo> windows, ColorPalette? palette, string? highlightBodyId)
    {
        if (windows.Count == 0)
            return null;

        float2 panelOrigin = ImGui.GetCursorScreenPos();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        var titlePos = new float2(panelOrigin.X + 4f, panelOrigin.Y + 3f);
        dl.AddText(in titlePos, TitleColor, "Phase clock - " + windows[0].SourceId);

        float2 avail = ImGui.GetContentRegionAvail();
        float side = Math.Min(avail.X, avail.Y);
        if (side < 60f)
            return null;

        if (avail.X > side)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail.X - side) * 0.5f);
        if (avail.Y > side)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (avail.Y - side) * 0.5f);

        return DrawClockFace(windows, palette, highlightBodyId, side);
    }

    // The footer toggle: a full-width clickable row (stable id via "###" so the live label can
    // change between press and release without dropping the click) with a real triangle arrow
    // drawn at its left.
    private static bool DrawToggle(IReadOnlyList<TransferWindowInfo> windows, bool expanded)
    {
        // Expanded, the list and clock carry the detail, so the footer just identifies the
        // section; collapsed, it names the next window. The "###" id keeps the toggle stable
        // across the changing label.
        string text = expanded ? "Transfer windows" : CollapsedHeaderLabel(windows);
        string label = "   " + text + "###dvtwtoggle";
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

    // The polar clock-face: a top-down schematic of the hub (center) and its bodies, each a dot
    // on a log-SMA ring at its current phase angle. The source (root) is drawn prominently in its
    // system color. For the emphasized sibling (hovered, else soonest) it draws lines from the
    // hub to the source and the target (the current configuration), then the WANTED departure
    // geometry in amber: a wedge from the source spanning the optimal lead angle, ending in a
    // line and hollow marker where the target must be, labeled with that lead and the countdown.
    // A line at the bottom decodes it ("<target>: lead +X, now +Y, in ~T"). Reserves the square
    // via Dummy. Returns the body id whose dot the mouse is over, or null.
    private static string? DrawClockFace(
        IReadOnlyList<TransferWindowInfo> windows, ColorPalette? palette, string? highlightBodyId, float side)
    {
        float2 region = ImGui.GetCursorScreenPos();
        var center = new float2(region.X + side * 0.5f, region.Y + side * 0.5f);
        float maxR = side * RingOuter;
        float minR = side * RingInner;
        float dot = MathF.Max(3.5f, side * 0.017f);
        float bigDot = dot * 1.6f;

        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        // The shared source fields live on any window (same root); use the first.
        TransferWindowInfo sourceRef = windows[0];

        // SMA range across the source and every sibling, for the log radius scale.
        double minSma = sourceRef.SourceSemiMajorAxis;
        double maxSma = sourceRef.SourceSemiMajorAxis;
        foreach (TransferWindowInfo w in windows)
        {
            if (w.TargetSemiMajorAxis < minSma) minSma = w.TargetSemiMajorAxis;
            if (w.TargetSemiMajorAxis > maxSma) maxSma = w.TargetSemiMajorAxis;
        }

        // The body to emphasize: the hovered one if given, else the soonest window.
        string? emphasis = highlightBodyId;
        if (emphasis == null)
        {
            int soonest = SoonestIndex(windows);
            if (soonest >= 0)
                emphasis = windows[soonest].TargetId;
        }

        float rSrc = RingRadius(sourceRef.SourceSemiMajorAxis, minSma, maxSma, minR, maxR);
        float2 srcPos = Polar(center, rSrc, sourceRef.SourcePhaseAngleNow);
        byte4 srcCol = palette?.ColorFor(sourceRef.SourceId) ?? DefaultDot;

        // Background: the source ring.
        dl.AddCircle(in center, rSrc, FaintRing, 64, 1f);

        TransferWindowInfo? emphasisInfo = null;
        foreach (TransferWindowInfo w in windows)
        {
            if (w.TargetId == emphasis)
            {
                emphasisInfo = w;
                break;
            }
        }

        // The configuration and the wanted departure geometry for the emphasized sibling, drawn
        // under the dots.
        if (emphasisInfo != null)
        {
            float r = RingRadius(emphasisInfo.TargetSemiMajorAxis, minSma, maxSma, minR, maxR);
            dl.AddCircle(in center, r, FaintRing, 64, 1f);
            float2 tgtPos = Polar(center, r, emphasisInfo.TargetPhaseAngleNow);

            // Lines from the hub to the source and to the target (where the bodies are now).
            dl.AddLine(in center, in srcPos, AngleLineColor, 1.5f);
            dl.AddLine(in center, in tgtPos, AngleLineColor, 1.5f);

            // The WANTED lead (amber): a wedge from the source spanning the optimal lead, a line
            // and hollow marker where the target must be, the lead and the countdown labeled.
            // (Retrograde measures the lead the other way round.)
            double lead = emphasisInfo.Retrograde
                ? (2.0 * Math.PI - emphasisInfo.TargetPhaseAngle)
                : emphasisInfo.TargetPhaseAngle;
            double requiredAngle = sourceRef.SourcePhaseAngleNow + lead;
            float2 reqPos = Polar(center, r, requiredAngle);

            DrawArc(dl, center, minR * 0.72f, sourceRef.SourcePhaseAngleNow, requiredAngle, RequiredColor, 2.2f);
            dl.AddLine(in center, in reqPos, RequiredColor, 1.5f);
            dl.AddCircle(in reqPos, bigDot * 1.1f, RequiredColor, 18, 2f);

            // The numbers go on one line at the bottom, not on the crowded center where labels
            // would collide; shortened to a "lead + countdown" form if it would not fit the width.
            string time = RoutePanelRenderer.FormatTime(emphasisInfo.TimeToWindowSeconds);
            string decode = "lead " + DegSigned(emphasisInfo.TargetPhaseAngle) + " deg, now "
                + DegSigned(emphasisInfo.CurrentPhaseAngle) + ", in ~" + time;
            if (ImGui.CalcTextSize(decode).X > side - 8f)
                decode = "lead " + DegSigned(emphasisInfo.TargetPhaseAngle) + " deg, in ~" + time;
            var decodePos = new float2(region.X + 4f, region.Y + side - 17f);
            var decodeShadow = new float2(decodePos.X + 1f, decodePos.Y + 1f);
            dl.AddText(in decodeShadow, LabelShadow, decode);
            dl.AddText(in decodePos, LabelColor, decode);
        }

        // Dots on top of the geometry; the emphasized / hovered dot is enlarged and named.
        float2 mouse = ImGui.GetMousePos();
        string? hovered = null;
        foreach (TransferWindowInfo w in windows)
        {
            float r = RingRadius(w.TargetSemiMajorAxis, minSma, maxSma, minR, maxR);
            float2 p = Polar(center, r, w.TargetPhaseAngleNow);
            byte4 col = palette?.ColorFor(w.TargetId) ?? DefaultDot;
            bool over = Dist(mouse, p) <= DotPickRadius;
            if (over)
                hovered = w.TargetId;
            bool emph = w.TargetId == emphasis;
            dl.AddCircleFilled(in p, (emph || over) ? bigDot : dot, col);
            if (emph)
                ClockLabel(dl, p, w.TargetId);
        }

        // Hub and source on top, with the source named.
        dl.AddCircleFilled(in center, dot, HubColor);
        dl.AddCircleFilled(in srcPos, bigDot, srcCol);
        ClockLabel(dl, srcPos, sourceRef.SourceId);

        ImGui.Dummy(new float2(side, side));
        return hovered;
    }

    private static float2 Polar(float2 center, float r, double angle)
    {
        return new float2(center.X + r * (float)Math.Cos(angle), center.Y - r * (float)Math.Sin(angle));
    }

    // Wrap an angle delta to the short way, (-PI, PI].
    private static double ShortDelta(double d)
    {
        d %= 2.0 * Math.PI;
        if (d < -Math.PI)
            d += 2.0 * Math.PI;
        if (d > Math.PI)
            d -= 2.0 * Math.PI;
        return d;
    }

    // Log-scaled ring radius for a semi-major axis, so inner and outer bodies separate cleanly;
    // falls back to a middle ring when the range is degenerate (a single body or equal SMAs).
    private static float RingRadius(double sma, double minSma, double maxSma, float minR, float maxR)
    {
        if (!(sma > 0.0) || !(minSma > 0.0) || maxSma <= minSma)
            return (minR + maxR) * 0.5f;
        double t = Math.Clamp((Math.Log(sma) - Math.Log(minSma)) / (Math.Log(maxSma) - Math.Log(minSma)), 0.0, 1.0);
        return minR + (float)t * (maxR - minR);
    }

    private static float Dist(float2 a, float2 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // A thin arc on a ring from one angle to another, the short way, as a few line segments (the
    // binding exposes no path-arc helper).
    private static void DrawArc(ImDrawListPtr dl, float2 center, float r, double a0, double a1, byte4 color, float thickness)
    {
        double delta = ShortDelta(a1 - a0);
        const int segments = 24;
        float2 prev = Polar(center, r, a0);
        for (int i = 1; i <= segments; i++)
        {
            float2 cur = Polar(center, r, a0 + delta * (i / (double)segments));
            dl.AddLine(in prev, in cur, color, thickness);
            prev = cur;
        }
    }

    // A short label with a drop shadow, offset from a point. Kept tiny; the list carries the full
    // names, so the clock labels only the source, the emphasized sibling and the wanted angle.
    private static void ClockLabel(ImDrawListPtr dl, float2 at, string text)
    {
        ClockLabel(dl, at, text, LabelColor);
    }

    private static void ClockLabel(ImDrawListPtr dl, float2 at, string text, byte4 color)
    {
        var pos = new float2(at.X + 6f, at.Y - 6f);
        var shadow = new float2(pos.X + 1f, pos.Y + 1f);
        dl.AddText(in shadow, LabelShadow, text);
        dl.AddText(in pos, color, text);
    }

    // The compact per-sibling list below the clock-face. Highlights the soonest (green) and the
    // emphasized (hovered) row, and returns the body id of the row the mouse is over, or null.
    private static string? DrawCompactList(IReadOnlyList<TransferWindowInfo> windows, string? highlightBodyId, float height)
    {
        int soonest = SoonestIndex(windows);
        string? hovered = null;

        // Fill the space between the clock-face and the footer toggle; the table scrolls
        // internally when a dense hub has more siblings than fit.
        var outer = new float2(0f, Math.Max(height, ImGui.GetFrameHeight() * 2f));

        if (!ImGui.BeginTable("##dvtwlist"u8, 3, TableFlags, (float2?)outer))
            return null;
        try
        {
            ImGui.TableSetupColumn("Target"u8, ImGuiTableColumnFlags.WidthStretch, 1.7f, 0);
            ImGui.TableSetupColumn("In"u8, ImGuiTableColumnFlags.WidthStretch, 1.1f, 1);
            ImGui.TableSetupColumn("Eject"u8, ImGuiTableColumnFlags.WidthStretch, 0.9f, 2);
            // Keep the header visible while the body scrolls.
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            for (int i = 0; i < windows.Count; i++)
            {
                string? h = DrawCompactRow(windows[i], i == soonest, windows[i].TargetId == highlightBodyId);
                if (h != null)
                    hovered = h;
            }
        }
        finally
        {
            // Always close the table, even on an unexpected throw, so the table stack stays
            // balanced for the rest of the overlay.
            ImGui.EndTable();
        }
        return hovered;
    }

    private static string? DrawCompactRow(TransferWindowInfo w, bool soonest, bool highlight)
    {
        ImGui.TableNextRow();
        if (soonest)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(SoonestRowBg));
        else if (highlight)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(HoverRowBg));

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
        bool isHovered = ImGui.IsItemHovered();
        if (isHovered)
            ShowRowTooltip(w);

        ImGui.TableNextColumn();
        ImGui.Text("~" + RoutePanelRenderer.FormatTime(w.TimeToWindowSeconds));

        ImGui.TableNextColumn();
        ImGui.Text(Deg(w.EjectionAngle) + (w.EjectionAhead ? " >" : " <"));

        return isHovered ? w.TargetId : null;
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
        return "next: " + w.TargetId + " ~" + RoutePanelRenderer.FormatTime(w.TimeToWindowSeconds);
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
