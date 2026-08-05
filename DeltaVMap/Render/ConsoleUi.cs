using System;
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
}
