using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace DeltaVMap.Render;

// The map legend, drawn in the route panel's spare vertical space below the summary. It
// renders the very same NodeGlyphs the canvas draws, next to one-line labels, so the symbol
// set is self-documenting and can never drift from the map. Static and compact; no
// interaction. Colors are a fixed representative system color rather than any one body's, so
// the legend reads the same whatever is on screen.
internal static class LegendRenderer
{
    private static readonly byte4 Fill = new byte4(120, 170, 210, 255);
    private static readonly byte4 Stroke = new byte4(186, 210, 230, 255);
    private static readonly byte4 Marker = new byte4(212, 226, 240, 255);

    private const float CellW = 28f;
    private const float CellH = 22f;
    private const float R = 7f;

    public static void Draw()
    {
        // Controls first, so the map's interactions are discoverable without a manual.
        ImGui.SeparatorText("Controls"u8);
        ImGui.TextDisabled("Click a body: plan a route to it");
        ImGui.TextDisabled("Click it again: clear the route");
        ImGui.TextDisabled("Shift+click: re-root the map here");
        ImGui.TextDisabled("Drag: pan,  mouse wheel: zoom");
        ImGui.TextDisabled("Top-left icon: switch layout");

        ImGui.SeparatorText("Legend"u8);

        Row("Low orbit", static (dl, c) => NodeGlyphs.LowOrbit(dl, c, R, Fill, Stroke));
        Row("Stationary orbit", static (dl, c) => NodeGlyphs.Stationary(dl, c, R, Fill, Stroke));
        Row("Elliptical to SOI edge", static (dl, c) => NodeGlyphs.SoiEdge(dl, c, R, Fill, Stroke));
        Row("Intercept (flyby)", static (dl, c) => NodeGlyphs.Intercept(dl, c, R, Fill, Stroke));
        Row("Surface (landed)", static (dl, c) => NodeGlyphs.Surface(dl, c, R, Fill, Stroke));
        Row("Minor-body group (+N)", static (dl, c) => NodeGlyphs.MinorGroup(dl, c, R, Fill, Stroke));
        Row("Jet engine possible", static (dl, c) =>
        {
            NodeGlyphs.LowOrbit(dl, c, 5f, Fill, Stroke);
            NodeGlyphs.AtmosphereHalo(dl, c, 5f, Marker);
        });
        Row("Aerobraking possible", static (dl, c) => NodeGlyphs.AerobrakeTriangle(dl, c, 1f, 0f, 8f, Marker));
        Row("Ringed planet", static (dl, c) =>
        {
            NodeGlyphs.Solid(dl, c, 5f, Fill, Stroke);
            NodeGlyphs.RingEllipse(dl, c, 5f, Stroke);
        });
        PlaneChangeRow("Max plane change (when on)");

        ImGui.TextDisabled("Color = planetary system");
        ImGui.TextDisabled("Transfer dV: injection + capture, then total");
        ImGui.TextDisabled("With a margin: base (left) | amber +margin (right)");
        ImGui.TextDisabled("~  approximate (closed-form estimate)");
    }

    // Draw a glyph centered in a fixed cell, then its label vertically centered beside it.
    private static void Row(string label, Action<ImDrawListPtr, float2> drawGlyph)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float2 cur = ImGui.GetCursorScreenPos();
        var center = new float2(cur.X + CellW * 0.5f, cur.Y + CellH * 0.5f);
        drawGlyph(dl, center);
        ImGui.Dummy(new float2(CellW, CellH));
        ImGui.SameLine(0f, 6f);
        LabelCentered(cur.Y, label);
    }

    // The plane-change marker is a number, not a glyph, so its legend cell shows a sample
    // number in the same warm color the map uses. The sample is wider than a glyph cell, so
    // the reserved width grows to fit it; otherwise the label would overlap the number.
    private static void PlaneChangeRow(string label)
    {
        const string sample = "i ~340";
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float2 cur = ImGui.GetCursorScreenPos();
        var pos = new float2(cur.X + 4f, cur.Y + (CellH - ImGui.GetTextLineHeight()) * 0.5f);
        dl.AddText(in pos, NodeGlyphs.PlaneChangeColor, sample);
        float cellW = Math.Max(CellW, ImGui.CalcTextSize(sample).X + 8f);
        ImGui.Dummy(new float2(cellW, CellH));
        ImGui.SameLine(0f, 6f);
        LabelCentered(cur.Y, label);
    }

    private static void LabelCentered(float rowTop, string label)
    {
        float2 cursor = ImGui.GetCursorScreenPos();
        float y = rowTop + (CellH - ImGui.GetTextLineHeight()) * 0.5f;
        ImGui.SetCursorScreenPos(new float2(cursor.X, y));
        ImGui.Text(label);
    }
}
