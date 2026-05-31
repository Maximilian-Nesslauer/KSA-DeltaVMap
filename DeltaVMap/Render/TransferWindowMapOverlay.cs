using System;
using System.Collections.Generic;
using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using DeltaVMap.Core;
using DeltaVMap.Model;
using KSA;

namespace DeltaVMap.Render;

// Map-mode (the game's 3D orbit view) overlay for the transfer windows. Purely additive: a draw
// layer keyed off the existing TransferWindowInfo that, while the game camera is in map mode,
// marks where each sibling destination will be at its next departure window (its position
// projected onto the real orbit) and draws an ejection-angle gizmo at the departure body. It
// changes nothing in the panel, the list, the clock-face or the dV map; it only reads the
// already-built, already-refreshed window list.
//
// It mirrors the proven stock path (TransferPlanner "Show Parent/Target Alignment",
// TransferPlanner.cs:319-331): take an orbit position, transform it to the parent's CCE frame,
// lift it to ECL via the parent body, project with Camera.EclToScreen, and draw on the
// viewport's background draw list. The position projection is the same rendering step the stock
// planner uses, so this recomputes none of the closed-form window math. Everything is wrapped so
// a camera or projection change can never reach the render path.
internal static class TransferWindowMapOverlay
{
    // Amber, matching the clock-face required marker and the on-canvas window badges, so the
    // timing layer reads as one vocabulary across the panel, the metro map and the 3D map.
    private static readonly byte4 MarkerColor = new byte4(240, 200, 90, 255);
    private static readonly byte4 MarkerFaint = new byte4(240, 200, 90, 130);
    // The prograde reference arm: a cool cyan, the usual velocity-vector hue, distinct from amber.
    private static readonly byte4 ProgradeColor = new byte4(120, 200, 235, 235);
    // The hub-to-body angle lines and arc (the planet-star-planet geometry): a neutral steel, dim
    // enough not to fight the amber markers, matching the clock-face's angle-line hue.
    private static readonly byte4 PhaseLineColor = new byte4(176, 190, 210, 210);
    private static readonly byte4 LabelColor = new byte4(236, 224, 196, 255);
    private static readonly byte4 LabelShadow = new byte4(0, 0, 0, 205);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Ejection gizmo geometry, in screen px so it stays a readable size at any map zoom.
    private const float RayPx = 72f;
    private const float ArcPx = 40f;

    // How far along each direction (as a fraction of the source's orbital radius) we project a
    // probe point to read the on-screen direction of that world-space ray. A straight 3D ray
    // projects to a straight screen line, so any non-degenerate probe gives the exact projected
    // direction; this value is just large enough for a numerically stable screen delta and small
    // enough that the probe rarely falls behind the camera.
    private const double DirectionProbeFraction = 0.05;

    // The rotation sense for the ejection arm. With this sign an outbound (target outer) ejection
    // swings the prograde reference toward the outward radial, so it points out toward the target's
    // orbit, and an inbound one swings it toward the hub; confirmed in map mode (an Earth->Mars
    // ejection points out past Earth's orbit rather than sunward). Flip this single constant if a
    // later check shows the arm on the wrong side.
    private const double EjectionSign = -1.0;

    // Draw the map-mode markers for the current window list. No-op unless the game camera is in
    // map mode. emphasisHint is the body to highlight (the hovered sibling, else the selected
    // route's sibling); it falls back to the soonest window here, matching the clock-face.
    public static void Draw(Viewport viewport, IReadOnlyList<TransferWindowInfo> windows, string? emphasisHint)
    {
        if (windows.Count == 0)
            return;
        try
        {
            if (viewport.Mode != CameraMode.Map)
                return;

            // Project with the same viewport whose Mode and Position we use, so the camera is not
            // coupled to which viewport happens to be the frame viewport at this hook.
            Camera camera = viewport.GetCamera();
            float2 vpPos = viewport.Position;
            // Background draw list for the main viewport (index 0), as the stock planner does, so
            // the markers sit on the 3D scene behind the floating windows; a child viewport uses
            // its own window draw list.
            ImDrawListPtr dl = (viewport.Index == 0) ? ImGui.GetBackgroundDrawList() : ImGui.GetWindowDrawList();

            SimTime now = Universe.GetElapsedSimTime();
            double nowSec = now.Seconds();
            string? emphasis = ResolveEmphasis(windows, emphasisHint);
            TransferWindowInfo? focus = Find(windows, emphasis);

            // The live planet-star-planet angle for the emphasized window, drawn first so the
            // markers and the gizmo sit on top of its long hub lines.
            if (focus != null)
                DrawPhaseAngle(dl, camera, vpPos, focus, now);

            // Where each sibling will be when its window opens. The emphasized one is drawn
            // prominently and labeled; the rest are faint rings so a dense root does not smear.
            foreach (TransferWindowInfo w in windows)
                DrawDestinationMarker(dl, camera, vpPos, w, nowSec, w.TargetId == emphasis);

            // The ejection-angle gizmo, only for the emphasized window, at the departure body.
            if (focus != null)
                DrawEjectionGizmo(dl, camera, vpPos, focus, now);
        }
        catch (Exception ex)
        {
            // A camera or projection change must never unwind into the render path.
            LogHelper.ErrorOnce("twindow-map-overlay", $"[DvMap] Transfer window map overlay failed: {ex}");
        }
    }

    // Project the destination body's position at its next window time onto its real orbit and
    // mark it. Mirrors the stock alignment path; the window time is the already-computed countdown
    // added to now, not a re-derived alignment.
    private static void DrawDestinationMarker(
        ImDrawListPtr dl, Camera camera, float2 vpPos, TransferWindowInfo w, double nowSec, bool emphasized)
    {
        if (!double.IsFinite(w.TimeToWindowSeconds) || w.Target is not IOrbiter orbiter)
            return;

        Orbit orbit = orbiter.Orbit;
        var windowTime = new SimTime(nowSec + w.TimeToWindowSeconds);
        double3 posOrb = orbit.GetPositionOrb(orbit.GetTimeSincePeriapsisThisOrbit(windowTime));
        double3 cce = posOrb.Transform(orbit.GetOrb2ParentCce());
        float2 s = CceToScreen(orbit, cce, camera, vpPos);
        if (!Valid(s))
            return;

        if (emphasized)
        {
            dl.AddCircleFilled(in s, 5f, MarkerColor);
            dl.AddCircle(in s, 9f, MarkerColor, 20, 2f);
            string label = w.TargetId + " ~" + FormatWindowTime(w.TimeToWindowSeconds);
            var lp = new float2(s.X + 12f, s.Y - 8f);
            var sh = new float2(lp.X + 1f, lp.Y + 1f);
            dl.AddText(in sh, LabelShadow, label);
            dl.AddText(in lp, LabelColor, label);
        }
        else
        {
            dl.AddCircle(in s, 5f, MarkerFaint, 16, 1.6f);
        }
    }

    // The ejection-angle gizmo at the departure body: a prograde reference arm (the body's
    // heliocentric velocity direction) and an ejection arm rotated from it by the ejection angle,
    // ahead or behind, in the body's orbital plane. Drawn at a fixed screen length with the angle
    // labeled. The arms' directions are the true projected orbit-plane directions in the (possibly
    // tilted) map view; the angle drawn between them is therefore the projected angle, so the
    // numeric label carries the exact value.
    private static void DrawEjectionGizmo(
        ImDrawListPtr dl, Camera camera, float2 vpPos, TransferWindowInfo w, SimTime now)
    {
        if (w.Source is not IOrbiter src)
            return;

        Orbit orbit = src.Orbit;
        double3 pCce = orbit.GetPositionOrb(orbit.GetTimeSincePeriapsisThisOrbit(now)).Transform(orbit.GetOrb2ParentCce());
        double3 vCce = src.GetVelocityCce();
        double rLen = pCce.Length();
        double vLen = vCce.Length();
        if (!(rLen > 0.0) || !(vLen > 0.0))
            return;

        double3 vHat = vCce * (1.0 / vLen);
        double3 rHat = pCce * (1.0 / rLen);
        // NormalizeOrZero (not Normalized) so a degenerate radial state cannot inject NaN; real
        // orbits never hit it (open orbits are filtered upstream), matching the stock guard.
        double3 nHat = double3.Cross(rHat, vHat).NormalizeOrZero();
        double angle = EjectionSign * (w.EjectionAhead ? 1.0 : -1.0) * w.EjectionAngle;
        double3 ejDir = Rotate(vHat, nHat, angle);

        double probe = rLen * DirectionProbeFraction;
        float2 s0 = CceToScreen(orbit, pCce, camera, vpPos);
        float2 sPro = CceToScreen(orbit, pCce + vHat * probe, camera, vpPos);
        float2 sEj = CceToScreen(orbit, pCce + ejDir * probe, camera, vpPos);
        if (!Valid(s0) || !Valid(sPro) || !Valid(sEj))
            return;

        float2 uPro = Unit(sPro - s0);
        float2 uEj = Unit(sEj - s0);
        if (IsZero(uPro) || IsZero(uEj))
            return;

        var proEnd = new float2(s0.X + uPro.X * RayPx, s0.Y + uPro.Y * RayPx);
        var ejEnd = new float2(s0.X + uEj.X * RayPx, s0.Y + uEj.Y * RayPx);
        dl.AddLine(in s0, in proEnd, ProgradeColor, 1.8f);
        dl.AddLine(in s0, in ejEnd, MarkerColor, 2.2f);
        dl.AddCircleFilled(in s0, 3f, MarkerColor);

        ScreenArc(dl, s0, ArcPx, uPro, uEj, MarkerColor, 1.6f);

        var proTag = new float2(proEnd.X + 3f, proEnd.Y - 6f);
        dl.AddText(in proTag, ProgradeColor, "prograde");

        string deg = Math.Round(w.EjectionAngle * 180.0 / Math.PI).ToString("0", Inv);
        string label = "eject " + deg + " deg " + (w.EjectionAhead ? "ahead" : "behind");
        float2 mid = Unit(new float2(uPro.X + uEj.X, uPro.Y + uEj.Y));
        if (IsZero(mid))
            mid = uEj;
        var lp = new float2(s0.X + mid.X * (ArcPx + 10f), s0.Y + mid.Y * (ArcPx + 10f));
        var lsh = new float2(lp.X + 1f, lp.Y + 1f);
        dl.AddText(in lsh, LabelShadow, label);
        dl.AddText(in lp, LabelColor, label);
    }

    // The live planet-star-planet angle: lines from the hub (at the CCE origin) to the departure
    // body and to the destination at their current positions, with an arc at the hub spanning the
    // current phase angle and the value labeled. The lines reach the bodies' real positions, so
    // the destination line ends at the game's own body marker; the amber optimal-departure marker
    // drawn elsewhere shows where that body moves to by the window. As with the gizmo, the drawn
    // span is the projected angle in a tilted view, so the numeric label carries the exact value.
    private static void DrawPhaseAngle(ImDrawListPtr dl, Camera camera, float2 vpPos, TransferWindowInfo w, SimTime now)
    {
        if (w.Source is not IOrbiter src || w.Target is not IOrbiter tgt)
            return;

        Orbit so = src.Orbit;
        Orbit to = tgt.Orbit;
        double3 srcCce = so.GetPositionOrb(so.GetTimeSincePeriapsisThisOrbit(now)).Transform(so.GetOrb2ParentCce());
        double3 tgtCce = to.GetPositionOrb(to.GetTimeSincePeriapsisThisOrbit(now)).Transform(to.GetOrb2ParentCce());

        // The hub sits at the CCE origin for both orbits (the shared parent), so project that.
        float2 hub = CceToScreen(so, double3.Zero, camera, vpPos);
        float2 sScr = CceToScreen(so, srcCce, camera, vpPos);
        float2 tScr = CceToScreen(to, tgtCce, camera, vpPos);
        if (!Valid(hub) || !Valid(sScr) || !Valid(tScr))
            return;

        dl.AddLine(in hub, in sScr, PhaseLineColor, 1.4f);
        dl.AddLine(in hub, in tScr, PhaseLineColor, 1.4f);

        float2 uS = Unit(sScr - hub);
        float2 uT = Unit(tScr - hub);
        if (IsZero(uS) || IsZero(uT))
            return;
        ScreenArc(dl, hub, ArcPx, uS, uT, PhaseLineColor, 1.4f);

        double deg = Math.Abs(w.CurrentPhaseAngle) * 180.0 / Math.PI;
        string label = "phase " + Math.Round(deg).ToString("0", Inv) + " deg";
        // Place the label along the angular bisector of the arc (robust even when the bodies are
        // nearly opposite, where summing the two unit directions would cancel).
        double a0 = Math.Atan2(uS.Y, uS.X);
        double a1 = Math.Atan2(uT.Y, uT.X);
        double d = a1 - a0;
        while (d > Math.PI) d -= 2.0 * Math.PI;
        while (d < -Math.PI) d += 2.0 * Math.PI;
        double am = a0 + d * 0.5;
        var lp = new float2(hub.X + (float)Math.Cos(am) * (ArcPx + 10f), hub.Y + (float)Math.Sin(am) * (ArcPx + 10f));
        var sh = new float2(lp.X + 1f, lp.Y + 1f);
        dl.AddText(in sh, LabelShadow, label);
        dl.AddText(in lp, LabelColor, label);
    }

    // Lift a parent-frame CCE point to ECL and project it to viewport screen coordinates, exactly
    // as the stock alignment marker does.
    private static float2 CceToScreen(Orbit orbit, double3 cce, Camera camera, float2 vpPos)
    {
        double3 ecl = orbit.Parent.GetPositionEclFromCce(cce);
        return vpPos + camera.EclToScreen(ecl);
    }

    // Rotate a vector about a unit axis by an angle (Rodrigues' rotation), used to swing the
    // prograde direction into the ejection direction within the orbital plane.
    private static double3 Rotate(double3 v, double3 axis, double angle)
    {
        double c = Math.Cos(angle);
        double s = Math.Sin(angle);
        return v * c + double3.Cross(axis, v) * s + axis * (double3.Dot(axis, v) * (1.0 - c));
    }

    // A thin arc between two screen-space unit directions around a center, the short way, as a few
    // line segments (the binding exposes no path-arc helper, matching the clock-face's DrawArc).
    private static void ScreenArc(ImDrawListPtr dl, float2 center, float r, float2 uFrom, float2 uTo, byte4 color, float thickness)
    {
        double a0 = Math.Atan2(uFrom.Y, uFrom.X);
        double a1 = Math.Atan2(uTo.Y, uTo.X);
        double delta = a1 - a0;
        while (delta > Math.PI) delta -= 2.0 * Math.PI;
        while (delta < -Math.PI) delta += 2.0 * Math.PI;

        const int segments = 20;
        float2 prev = new float2(center.X + r * (float)Math.Cos(a0), center.Y + r * (float)Math.Sin(a0));
        for (int i = 1; i <= segments; i++)
        {
            double a = a0 + delta * (i / (double)segments);
            float2 cur = new float2(center.X + r * (float)Math.Cos(a), center.Y + r * (float)Math.Sin(a));
            dl.AddLine(in prev, in cur, color, thickness);
            prev = cur;
        }
    }

    // The body to emphasize: the hint when it is a current sibling, else the soonest window,
    // matching the clock-face's fallback so the 3D layer highlights the same destination.
    private static string? ResolveEmphasis(IReadOnlyList<TransferWindowInfo> windows, string? hint)
    {
        if (hint != null)
        {
            foreach (TransferWindowInfo w in windows)
                if (w.TargetId == hint)
                    return hint;
        }

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
        return best >= 0 ? windows[best].TargetId : null;
    }

    private static TransferWindowInfo? Find(IReadOnlyList<TransferWindowInfo> windows, string? id)
    {
        if (id == null)
            return null;
        foreach (TransferWindowInfo w in windows)
            if (w.TargetId == id)
                return w;
        return null;
    }

    private static bool Valid(float2 s)
    {
        return !float.IsNaN(s.X) && !float.IsNaN(s.Y) && !float.IsInfinity(s.X) && !float.IsInfinity(s.Y);
    }

    private static bool IsZero(float2 v)
    {
        return v.X == 0f && v.Y == 0f;
    }

    private static float2 Unit(float2 v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        if (len < 1e-4f)
            return new float2(0f, 0f);
        return new float2(v.X / len, v.Y / len);
    }

    // Compact countdown for the marker label, matching the on-canvas window badge: minutes,
    // hours, days, then years, no spaces, invariant so it stays ASCII regardless of locale.
    private static string FormatWindowTime(double seconds)
    {
        if (seconds < 3600.0)
            return string.Format(Inv, "{0:0}m", seconds / 60.0);
        if (seconds < 86400.0)
            return string.Format(Inv, "{0:0}h", seconds / 3600.0);
        double days = seconds / 86400.0;
        if (days < 365.25)
            return string.Format(Inv, "{0:0}d", days);
        return string.Format(Inv, "{0:0.0}yr", days / 365.25);
    }
}
