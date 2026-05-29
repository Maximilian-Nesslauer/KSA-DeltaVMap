using Brutal.ImGuiApi;
using DeltaVMap.Render;
using HarmonyLib;
using KSA;

namespace DeltaVMap.Patches;

// Adds a "Delta-V Map" toggle to the stock View menu. GaugeCanvas.OnDrawMenuBar is a
// trivial static method the game calls inside the View menu (Program.cs, in the "View"
// BeginMenu block), right where the gauge canvases list themselves. A postfix appends
// our item there, the same hook KSASM uses for its window. Accessing MapWindow.Instance
// here lazily creates the window inside an active ImGui frame, which is required by the
// ImGuiWindow base constructor.
[HarmonyPatch(typeof(GaugeCanvas), nameof(GaugeCanvas.OnDrawMenuBar))]
internal static class Patch_MenuBar
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        bool shown = MapWindow.Instance.IsShown;
        if (ImGui.MenuItem("Delta-V Map"u8, default(ImString), shown))
        {
            if (shown)
                MapWindow.Instance.Close();
            else
                MapWindow.Instance.Open();
        }
    }
}
