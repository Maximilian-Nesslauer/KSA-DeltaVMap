using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;
using DeltaVMap.Render;
using Brutal.Logging;
using KSA;
using StarMap.API;
using StarMap.API.Services;

namespace DeltaVMap;

[StarMapMod]
public sealed class Mod
{
    private static bool _validationDumped;

    private const string TestedGameVersion = "v2026.11.10.5801";

    private readonly IModMenuService _modMenu;
    private IDisposable? _modMenuItem;

    public Mod(IModMenuService modMenu)
    {
        _modMenu = modMenu;
    }

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        DefaultCategory.Log.Info($"[DvMap] Game version: {gameVersion}");
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning(
                $"[DvMap] Tested against {TestedGameVersion}, current is {gameVersion}. " +
                "Some features may not work correctly.");

        // StarMap calls the item from inside the game's menu bar draw, an active ImGui frame, so
        // MapWindow.Instance can be created there. The label must be unique across mods, and a
        // duplicate throws. Without the item the map is unreachable, but the mod stays loaded.
        try
        {
            _modMenuItem = _modMenu.AddModMenuItem("Delta-V Map", () => MapWindow.Instance.ToggleShown());
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("mod-menu", $"[DvMap] Failed to add the Mods menu item: {ex}");
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
        _modMenuItem?.Dispose();
        _modMenuItem = null;
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
