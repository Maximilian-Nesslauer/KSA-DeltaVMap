using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;
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

    private const string TestedGameVersion = "v2026.5.11.4462";

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

        DefaultCategory.Log.Info("[DvMap] Loaded.");
    }

    // Once a system is loaded, run the delta-v engine validation dump a single
    // time. This is debug-only scaffolding; the real map will build its graph
    // lazily on first window open.
    [StarMapAfterGui]
    public void Draw(double dt)
    {
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
            // This runs inside the game's ImGui draw via StarMap's postfix; never
            // let a dump failure unwind into the render path.
            LogHelper.ErrorOnce("validation-dump", $"[DvMap] Validation dump failed: {ex}");
        }
    }

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        _validationDumped = false;

        LogHelper.Reset();
#if DEBUG
        PerfTracker.Reset();
#endif
        DefaultCategory.Log.Info("[DvMap] Unloaded.");
    }
}
