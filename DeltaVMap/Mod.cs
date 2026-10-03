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

    private const string TestedGameVersion = "v2026.9.22.5482";

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
        try
        {
            _harmony.CreateClassProcessor(typeof(Patch_MenuBar)).Patch();
        }
        catch (Exception ex)
        {
            // A missing menu hook leaves the map unreachable but must not unload the mod.
            LogHelper.ErrorOnce("patch-" + nameof(Patch_MenuBar), $"[DvMap] Failed to apply the menu bar patch: {ex}");
        }

        DefaultCategory.Log.Info("[DvMap] Loaded.");
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
        // Drops the cached part tree and its private analyzer so a reload cannot keep a stale
        // vehicle alive through this mod.
        StagedDv.Reset();
        LogHelper.Reset();
#if DEBUG
        PerfTracker.Reset();
#endif
        DefaultCategory.Log.Info("[DvMap] Unloaded.");
    }
}
