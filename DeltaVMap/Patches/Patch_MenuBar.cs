using Brutal.ImGuiApi;
using DeltaVMap.Render;
using HarmonyLib;
using KSA;

namespace DeltaVMap.Patches;

// Adds a top-level "Delta-V Map" menu to the main menu bar in flight and in the vehicle editor.
// Program.DrawProgramMenusHook is an empty hook the game calls inside the menu bar after its own
// menus in both contexts, so the entry does not depend on the layout of the stock menus.
// Accessing MapWindow.Instance here lazily creates the window inside an active ImGui frame,
// which the ImGuiWindow base constructor requires.
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class Patch_MenuBar
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (!ImGui.BeginMenu("Delta-V Map"u8))
            return;

        bool shown = MapWindow.Instance.IsShown;
        if (ImGui.MenuItem("Show Map"u8, default(ImString), shown))
        {
            if (shown)
                MapWindow.Instance.Close();
            else
                MapWindow.Instance.Open();
        }
        ImGui.EndMenu();
    }
}
