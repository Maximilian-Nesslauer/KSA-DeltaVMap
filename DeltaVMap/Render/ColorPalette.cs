using System;
using System.Collections.Generic;
using Brutal.Numerics;
using DeltaVMap.Model;
using KSA;

namespace DeltaVMap.Render;

// Per-planetary-system colors for the map. Every body is mapped to the hue of its
// "system anchor" (the planet it belongs to, or itself if it is a planet or the
// star), so a planet and all its moons share one family of colors and read as one
// metro line. A small stock hue table pins the well-known bodies; everything else
// hashes its anchor name to a stable hue. Moons are lightened and minor bodies
// desaturated so a body's role still reads at a glance within its system color.
internal sealed class ColorPalette
{
    private readonly Dictionary<string, byte4> _byBodyId;
    private static readonly byte4 Fallback = new byte4(144, 164, 174, 255);

    private ColorPalette(Dictionary<string, byte4> byBodyId)
    {
        _byBodyId = byBodyId;
    }

    public byte4 ColorFor(string bodyId)
    {
        return _byBodyId.TryGetValue(bodyId, out byte4 c) ? c : Fallback;
    }

    public byte4 ColorFor(Astronomical body)
    {
        return ColorFor(body.Id);
    }

    // Precompute a color for every body in the graph. Done once per system build, so
    // the per-frame renderer only does dictionary lookups.
    public static ColorPalette Build(SystemGraph graph)
    {
        var map = new Dictionary<string, byte4>();
        foreach (PhysicalNode node in graph.AllNodes)
        {
            PhysicalNode anchor = SystemAnchor(node);
            double hue = HueFor(anchor.Id);

            double saturation = 0.70;
            double lightness = 0.55;

            // A moon sits brighter than its planet's base color; a minor body is
            // washed out. The star keeps the base values.
            if (node.Astro.IsMoon())
                lightness = 0.70;
            if (node.Astro is MinorBody)
                saturation = 0.50;

            map[node.Id] = HslToByte4(hue, saturation, lightness);
        }
        return new ColorPalette(map);
    }

    // Climb to the planet directly under the star, which defines the planetary system
    // this body belongs to. A planet returns itself; the star returns itself.
    private static PhysicalNode SystemAnchor(PhysicalNode node)
    {
        if (node.IsStar)
            return node;
        PhysicalNode current = node;
        while (current.Parent != null && !current.Parent.IsStar)
            current = current.Parent;
        return current;
    }

    private static double HueFor(string anchorId)
    {
        if (StockHues.TryGetValue(anchorId, out double hue))
            return hue;
        return Fnv1a(anchorId) % 360u;
    }

    // A handful of stock bodies get hand-picked hues so the common system reads in
    // familiar colors. Anything not listed (and every modded body) falls back to the
    // name hash, which is stable across runs.
    private static readonly Dictionary<string, double> StockHues = new()
    {
        ["Sol"] = 48,
        ["Sun"] = 48,
        ["Mercury"] = 28,
        ["Venus"] = 40,
        ["Earth"] = 210,
        ["Mars"] = 12,
        ["Jupiter"] = 30,
        ["Saturn"] = 45,
        ["Uranus"] = 180,
        ["Neptune"] = 222,
    };

    private static uint Fnv1a(string s)
    {
        uint hash = 2166136261u;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }

    // Standard HSL to RGB. h in degrees [0,360), s and l in [0,1]. Alpha is opaque.
    private static byte4 HslToByte4(double h, double s, double l)
    {
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double hp = (h % 360.0) / 60.0;
        double x = c * (1.0 - Math.Abs(hp % 2.0 - 1.0));
        double r = 0.0, g = 0.0, b = 0.0;

        if (hp < 1.0) { r = c; g = x; }
        else if (hp < 2.0) { r = x; g = c; }
        else if (hp < 3.0) { g = c; b = x; }
        else if (hp < 4.0) { g = x; b = c; }
        else if (hp < 5.0) { r = x; b = c; }
        else { r = c; b = x; }

        double m = l - c / 2.0;
        return new byte4(To255(r + m), To255(g + m), To255(b + m), byte.MaxValue);
    }

    private static byte To255(double v)
    {
        return (byte)Math.Clamp((int)Math.Round(v * 255.0), 0, 255);
    }
}
