using Brutal.ImGuiApi;
using DeltaVMap.Render;
using HarmonyLib;
using KSA;

namespace DeltaVMap.Patches;

// Adds a top-level "Delta-V Map" menu in the editor. The editor has no stock "View"
// menu (where the flight hook in Patch_MenuBar lives), so mirror what StageInfo does:
// postfix Program.DrawProgramMenusHook (an empty hook the game calls inside the main
// menu bar, in both flight and editor) and guard on Program.Editor != null so the tab
// shows only in the editor and does not duplicate the flight View entry.
//
// The map is useful in the editor for planning a mission's budget; it roots at the home
// body there (no controlled vehicle, so no "you are here"), and degrades to a clean
// "unavailable" message if no system is loaded.
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class Patch_EditorMenuBar
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (Program.Editor == null)
            return;

        if (ImGui.BeginMenu("Delta-V Map"u8))
        {
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
}
