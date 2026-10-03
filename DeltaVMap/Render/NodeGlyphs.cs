using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace DeltaVMap.Render;

// The KSP-style node-symbol vocabulary, drawn with pure DrawList primitives. Each state
// kind gets a concentric-ring glyph keyed to the canonical KSP delta-v map legend, and the
// body-property markers (the jet/atmosphere halo, the ring ellipse, the aerobrake triangle)
// layer on top. CanvasRenderer draws these on the map and LegendRenderer draws the same
// shapes in the panel legend, so the two can never drift. Nothing here knows about the
// layout tree or game types: a glyph is just a screen radius and a pair of colors, where
// fill is the body's system color and stroke a lightened accent of it.
internal static class NodeGlyphs
{
    // The plane-change number's color, warm so it never reads as one of the grey dV badges
    // sitting near it. Shared here so the map marker and the legend sample stay in lockstep.
    internal static readonly byte4 PlaneChangeColor = new byte4(232, 178, 96, 255);

    // A landed body: a solid filled disc with a thin outline.
    public static void Surface(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircleFilled(in p, r, fill);
        dl.AddCircle(in p, r, stroke, 24, 1.5f);
    }

    // Low orbit: a thin ring with a small filled center dot (KSP "Low Orbit").
    public static void LowOrbit(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircle(in p, r, stroke, 24, 2f);
        dl.AddCircleFilled(in p, CenterDot(r), fill);
    }

    // Synchronous orbit: the low-orbit glyph plus a short accent tick at the top, so the
    // labelled stationary node reads as a notch above low orbit rather than identical to it
    // (KSP draws it as a labelled higher orbit, "Keostationary Orbit").
    public static void Stationary(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircle(in p, r, stroke, 24, 2f);
        dl.AddCircleFilled(in p, CenterDot(r), fill);
        var inner = new float2(p.X, p.Y - r);
        var outer = new float2(p.X, p.Y - r - 4f);
        dl.AddLine(in inner, in outer, stroke, 2f);
        dl.AddCircleFilled(in inner, 2f, stroke);
    }

    // Elliptical orbit to the SOI edge: an outer SOI ring with a small inner ellipse, the
    // loose capture / park ellipse it represents (KSP "Elliptical Orbit to SOI Edge").
    public static void SoiEdge(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircle(in p, r, stroke, 24, 2f);
        Ellipse(dl, p, r * 0.52f, r * 0.34f, fill, 1.6f);
    }

    // Intercept: a flyby target. A thin ring, a center dot, and four short crosshair ticks
    // just outside the ring, so it reads as a reticle and not a plain low-orbit node (KSP
    // "Intercept").
    public static void Intercept(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircle(in p, r, stroke, 24, 1.6f);
        dl.AddCircleFilled(in p, CenterDot(r), fill);
        Tick(dl, p, 0f, -1f, r + 1f, r + 4f, stroke);
        Tick(dl, p, 0f, 1f, r + 1f, r + 4f, stroke);
        Tick(dl, p, -1f, 0f, r + 1f, r + 4f, stroke);
        Tick(dl, p, 1f, 0f, r + 1f, r + 4f, stroke);
    }

    // A hub bus junction: a filled disc with an outline. The KSP map has no equivalent (it
    // never draws the Sun); kept distinct so the spine reads as structure, not a body.
    public static void Hub(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircleFilled(in p, r, fill);
        dl.AddCircle(in p, r, stroke, 20, 1.5f);
    }

    // A star, drawn in a universe with several star systems: a filled disc with four long and
    // four short rays, so a star reads apart from a planet and from a barycenter.
    public static void Star(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        float core = r * 0.62f;
        for (int i = 0; i < 8; i++)
        {
            float angle = i * (MathF.PI / 4f);
            float outer = i % 2 == 0 ? r : r * 0.82f;
            float dx = MathF.Cos(angle);
            float dy = MathF.Sin(angle);
            var a = new float2(p.X + dx * core * 0.9f, p.Y + dy * core * 0.9f);
            var b = new float2(p.X + dx * outer, p.Y + dy * outer);
            dl.AddLine(in a, in b, stroke, i % 2 == 0 ? 2f : 1.4f);
        }
        dl.AddCircleFilled(in p, core, fill);
        dl.AddCircle(in p, core, stroke, 20, 1.5f);
    }

    // A barycenter, the empty center of mass of a multiple star system: a thin ring with a
    // center cross and two small dots on opposite sides, its stars.
    public static void Barycenter(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircle(in p, r, stroke, 24, 1.4f);
        float arm = r * 0.35f;
        var h0 = new float2(p.X - arm, p.Y);
        var h1 = new float2(p.X + arm, p.Y);
        var v0 = new float2(p.X, p.Y - arm);
        var v1 = new float2(p.X, p.Y + arm);
        dl.AddLine(in h0, in h1, stroke, 1.5f);
        dl.AddLine(in v0, in v1, stroke, 1.5f);
        float dot = Math.Max(2f, r * 0.2f);
        var left = new float2(p.X - r * 0.68f, p.Y + r * 0.2f);
        var right = new float2(p.X + r * 0.68f, p.Y - r * 0.2f);
        dl.AddCircleFilled(in left, dot, fill);
        dl.AddCircleFilled(in right, dot, fill);
    }

    // A whole star system drawn as one node: a rounded square of half-size r, the square the
    // stock map view marks another system's root with (IIndependentRoot.DrawSystemLabel), with
    // the star or barycenter glyph inside.
    public static void SystemStub(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke, bool barycenter)
    {
        var min = new float2(p.X - r, p.Y - r);
        var max = new float2(p.X + r, p.Y + r);
        dl.AddRectFilled(in min, in max, StubBackground, 4f);
        dl.AddRect(in min, in max, stroke, 4f);
        if (barycenter)
            Barycenter(dl, p, r * 0.6f, fill, stroke);
        else
            Star(dl, p, r * 0.6f, fill, stroke);
    }

    private static readonly byte4 StubBackground = new byte4(24, 30, 40, 235);

    // A faint fan of five short lines under a collapsed system's square of half-size r, a hint
    // at the tree folded inside it.
    public static void SystemFan(ImDrawListPtr dl, float2 p, float r, byte4 color)
    {
        var top = new float2(p.X, p.Y + r + 2f);
        for (int i = -2; i <= 2; i++)
        {
            var end = new float2(p.X + i * 9f, p.Y + r + 14f);
            dl.AddLine(in top, in end, color, 1.2f);
        }
    }

    // A dashed line from a to b, 7 px on and 5 px off like the stock ConsoleWidgets dash, made
    // of plain AddLine pieces. The segment is clipped to [clipMin, clipMax] first, so a long
    // line at a high zoom draws only the dashes on screen, and the dashes keep their phase from
    // a, so they do not crawl while the view pans.
    public static void DashedSegment(ImDrawListPtr dl, float2 a, float2 b, byte4 color, float thickness, float2 clipMin, float2 clipMax)
    {
        const float on = 7f;
        const float period = 12f;
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (!(length > 0f))
            return;
        if (!Clip(a, dx, dy, clipMin, clipMax, out float t0, out float t1))
            return;
        float ux = dx / length;
        float uy = dy / length;
        float from = t0 * length;
        float to = t1 * length;
        for (float s = MathF.Floor(from / period) * period; s < to; s += period)
        {
            float d0 = MathF.Max(s, from);
            float d1 = MathF.Min(s + on, to);
            if (d1 <= d0)
                continue;
            var p0 = new float2(a.X + ux * d0, a.Y + uy * d0);
            var p1 = new float2(a.X + ux * d1, a.Y + uy * d1);
            dl.AddLine(in p0, in p1, color, thickness);
        }
    }

    // Liang-Barsky clipping of a + t (dx, dy), t in [0, 1], to a rectangle.
    private static bool Clip(float2 a, float dx, float dy, float2 min, float2 max, out float t0, out float t1)
    {
        t0 = 0f;
        t1 = 1f;
        return ClipEdge(-dx, a.X - min.X, ref t0, ref t1)
            && ClipEdge(dx, max.X - a.X, ref t0, ref t1)
            && ClipEdge(-dy, a.Y - min.Y, ref t0, ref t1)
            && ClipEdge(dy, max.Y - a.Y, ref t0, ref t1);
    }

    private static bool ClipEdge(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0f)
            return q >= 0f;
        float t = q / p;
        if (p < 0f)
        {
            if (t > t1)
                return false;
            if (t > t0)
                t0 = t;
        }
        else
        {
            if (t < t0)
                return false;
            if (t < t1)
                t1 = t;
        }
        return true;
    }

    // The scale-break mark on the interstellar trunk: two short parallel slashes across the
    // line, so it reads as not to scale. vertical is the direction of the line it sits on.
    public static void ScaleBreak(ImDrawListPtr dl, float2 p, byte4 color, byte4 background, bool vertical)
    {
        var bgMin = vertical ? new float2(p.X - 7f, p.Y - 6f) : new float2(p.X - 6f, p.Y - 7f);
        var bgMax = vertical ? new float2(p.X + 7f, p.Y + 6f) : new float2(p.X + 6f, p.Y + 7f);
        dl.AddRectFilled(in bgMin, in bgMax, background);
        for (int i = -1; i <= 1; i += 2)
        {
            var a = vertical ? new float2(p.X - 7f, p.Y + i * 3f + 3f) : new float2(p.X + i * 3f - 3f, p.Y + 7f);
            var b = vertical ? new float2(p.X + 7f, p.Y + i * 3f - 3f) : new float2(p.X + i * 3f + 3f, p.Y - 7f);
            dl.AddLine(in a, in b, color, 2f);
        }
    }

    // A solid disc, used for the "you are here" anchor (CanvasRenderer rings it in yellow).
    public static void Solid(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircleFilled(in p, r, fill);
        dl.AddCircle(in p, r, stroke, 20, 1.5f);
    }

    // A minor-body group ("+N more"): a little cluster of small filled dots inside a thin
    // ring, so it reads as "many small bodies aggregated here" rather than one body. The
    // count rides the node label, not the glyph.
    public static void MinorGroup(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircle(in p, r, stroke, 20, 1.2f);
        float d = Math.Max(1.5f, r * 0.26f);
        float o = r * 0.42f;
        var a = new float2(p.X - o, p.Y - o * 0.5f);
        var b = new float2(p.X + o, p.Y - o * 0.2f);
        var c = new float2(p.X, p.Y + o * 0.75f);
        dl.AddCircleFilled(in a, d, fill);
        dl.AddCircleFilled(in b, d, fill);
        dl.AddCircleFilled(in c, d, fill);
    }

    // A heavy bold halo just outside the base glyph, marking a body with a usable
    // atmosphere (KSP "Jet Engine Operation Possible").
    public static void AtmosphereHalo(ImDrawListPtr dl, float2 p, float r, byte4 color)
    {
        dl.AddCircle(in p, r + 4f, color, 28, 3.5f);
    }

    // A thin flattened ring ellipse around a ringed body (Saturn in the stock system).
    public static void RingEllipse(ImDrawListPtr dl, float2 p, float r, byte4 color)
    {
        Ellipse(dl, p, r * 2.3f, r * 0.78f, color, 1.6f);
    }

    // A filled directional triangle marking an aerobraking-possible capture. (dirX, dirY)
    // is a unit vector pointing the way the capture runs (toward the body's low orbit), so
    // the arrowhead reads as "brake inward here" (KSP "Aerobraking Possible").
    public static void AerobrakeTriangle(ImDrawListPtr dl, float2 p, float dirX, float dirY, float size, byte4 color)
    {
        float px = -dirY;
        float py = dirX;
        var tip = new float2(p.X + dirX * size, p.Y + dirY * size);
        var baseL = new float2(
            p.X - dirX * size * 0.5f + px * size * 0.7f,
            p.Y - dirY * size * 0.5f + py * size * 0.7f);
        var baseR = new float2(
            p.X - dirX * size * 0.5f - px * size * 0.7f,
            p.Y - dirY * size * 0.5f - py * size * 0.7f);
        dl.AddTriangleFilled(in tip, in baseL, in baseR, color);
    }

    private static float CenterDot(float r)
    {
        return Math.Max(2f, r * 0.34f);
    }

    // A short radial tick from r0 to r1 along the unit direction (dx, dy).
    private static void Tick(ImDrawListPtr dl, float2 p, float dx, float dy, float r0, float r1, byte4 col)
    {
        var a = new float2(p.X + dx * r0, p.Y + dy * r0);
        var b = new float2(p.X + dx * r1, p.Y + dy * r1);
        dl.AddLine(in a, in b, col, 1.5f);
    }

    // A closed ellipse outline built from straight segments. The DrawList exposes an
    // AddEllipse, but stitching AddLine keeps the call surface to the byte4 primitives the
    // rest of the renderer already uses and gives direct thickness control.
    private static void Ellipse(ImDrawListPtr dl, float2 c, float rx, float ry, byte4 col, float thickness)
    {
        const int segments = 24;
        var prev = new float2(c.X + rx, c.Y);
        for (int i = 1; i <= segments; i++)
        {
            double a = i * (2.0 * Math.PI / segments);
            var next = new float2(c.X + rx * (float)Math.Cos(a), c.Y + ry * (float)Math.Sin(a));
            dl.AddLine(in prev, in next, col, thickness);
            prev = next;
        }
    }
}
