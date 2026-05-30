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
