using System;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Core;
using KSA;

namespace DeltaVMap.Render;

/// <summary>
/// Label-and-control rows built on the game's ConsoleWidgets. The map window
/// derives from the stock ImGuiWindow, which draws the console shell and pushes
/// the console widget style around DrawContent, so the panels lay out through
/// the same widgets rather than raw ImGui.
/// </summary>
internal static class ConsoleUi
{
    public static bool CheckboxRow(ReadOnlySpan<char> label, ReadOnlySpan<char> id, ref bool value)
    {
        ConsoleWidgets.BeginRow(label);
        bool changed = ConsoleWidgets.Checkbox(id, ref value, pending: false);
        ConsoleWidgets.EndRow();
        return changed;
    }

    // A slider row. ConsoleWidgets.SliderFloat is linear only, so a value that spans decades
    // (a cruise speed from km/s to a large fraction of c) uses the ImGui slider with its
    // logarithmic flag inside the console row, as the game's own planner does
    // (TransferPlanner.DrawSliderFloatRow). id is the hidden ImGui id without the "##". The
    // value is not rounded to the display format, so a small speed keeps its precision.
    public static bool LogSliderRow(ReadOnlySpan<char> label, ReadOnlySpan<char> id, ref float value, float min, float max, ReadOnlySpan<char> format)
    {
        return LogSliderRow(label, id, ref value, min, max, format, out _);
    }

    // The same row, with active true while the slider is held (ImGui.IsItemActive right after
    // the slider, before the row closes), so a caller can defer costly work to the release.
    public static bool LogSliderRow(ReadOnlySpan<char> label, ReadOnlySpan<char> id, ref float value, float min, float max, ReadOnlySpan<char> format, out bool active)
    {
        ConsoleWidgets.BeginRow(label);
        Span<char> buffer = stackalloc char[64];
        var hidden = new SpanBuilder(buffer);
        hidden.Append("##".AsSpan());
        hidden.Append(id);
        ImGui.SetNextItemWidth(-1f);
        bool changed = ImGui.SliderFloat(hidden, ref value, min, max, format,
            ImGuiSliderFlags.Logarithmic | ImGuiSliderFlags.NoRoundToFormat | ImGuiSliderFlags.AlwaysClamp);
        active = ImGui.IsItemActive();
        ConsoleWidgets.EndRow();
        return changed;
    }

    // A status line in the console value font: muted for information, positive for a good
    // verdict, danger for a problem. It wraps at the panel edge, so a long verdict keeps its
    // figure in view.
    public static void StatusText(string text, StatusTone tone = StatusTone.Muted)
    {
        ConsoleStyle.PushValueFont();
        ImGui.PushTextWrapPos(0f);
        switch (tone)
        {
            case StatusTone.Positive:
                ImGui.TextColored(in ConsoleStyle.Positive, text);
                break;
            case StatusTone.Pending:
                ImGui.TextColored(in ConsoleStyle.Pending, text);
                break;
            case StatusTone.Danger:
                ImGui.TextColored(in ConsoleStyle.Danger, text);
                break;
            default:
                ImGui.TextColored(in ConsoleStyle.TextMuted, text);
                break;
        }
        ImGui.PopTextWrapPos();
        ConsoleStyle.PopFont();
    }
}

internal enum StatusTone
{
    Muted,
    Positive,
    Pending,
    Danger
}
