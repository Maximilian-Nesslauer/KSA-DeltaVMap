using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;
using DeltaVMap.Patches;
using DeltaVMap.Render;
using Brutal.Logging;
using HarmonyLib;
using KSA;
using StarMap.API;

namespace DeltaVMap;

[StarMapMod]
public sealed class Mod
{
    private static Harmony? _harmony;
    private static bool _validationDumped;

    private const string TestedGameVersion = "v2026.6.3.4568";

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        DefaultCategory.Log.Info($"[DvMap] Game version: {gameVersion}");
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning(
                $"[DvMap] Tested against {TestedGameVersion}, current is {gameVersion}. " +
                "Some features may not work correctly.");

        _harmony = new Harmony("com.maxi.deltavmap");
        // Apply each menu hook on its own so a future game change to one target does not
        // stop the other from being patched. Patch_MenuBar adds the flight View-menu
        // item; Patch_EditorMenuBar adds the editor's top-level tab.
        ApplyPatch(typeof(Patch_MenuBar), "flight View menu");
        ApplyPatch(typeof(Patch_EditorMenuBar), "editor menu bar");

        DefaultCategory.Log.Info("[DvMap] Loaded.");
    }

    private static void ApplyPatch(Type patchClass, string description)
    {
        try
        {
            _harmony!.CreateClassProcessor(patchClass).Patch();
        }
        catch (Exception ex)
        {
            // A missing hook should not unload the mod or block the other patch.
            LogHelper.ErrorOnce("patch-" + patchClass.Name, $"[DvMap] Failed to apply {description} patch: {ex}");
        }
    }

    // Runs every frame after KSA's own ImGui, while the frame is still active. Draws
    // the map window when the user has it open, and on the first loaded system runs the
    // debug dumps once (behind DebugConfig) so the engine and layout can be verified
    // in-game; the dumps write SVG/text to DebugConfig.LayoutDumpDir.
    [StarMapAfterGui]
    public void Draw(double dt)
    {
        MapWindow.DrawActive(Program.MainViewport);

        if (_validationDumped || !DebugConfig.ValidationDump)
            return;
        if (Universe.CurrentSystem == null)
            return;

        _validationDumped = true;
        try
        {
            DvValidationDump.Run();
            VisualTreeDump.Run();
            LayoutDump.Run();
        }
        catch (Exception ex)
        {
            // Never let a dump failure unwind into the render path.
            LogHelper.ErrorOnce("validation-dump", $"[DvMap] Validation dump failed: {ex}");
        }
    }

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        _validationDumped = false;

        MapWindow.ResetStatic();
        LogHelper.Reset();
#if DEBUG
        PerfTracker.Reset();
#endif
        DefaultCategory.Log.Info("[DvMap] Unloaded.");
    }
}
