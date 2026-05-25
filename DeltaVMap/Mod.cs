using DeltaVMap.Core;
using Brutal.Logging;
using HarmonyLib;
using KSA;
using StarMap.API;

namespace DeltaVMap;

[StarMapMod]
public sealed class Mod
{
    private static Harmony? _harmony;

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

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;

        LogHelper.Reset();
#if DEBUG
        PerfTracker.Reset();
#endif
        DefaultCategory.Log.Info("[DvMap] Unloaded.");
    }
}
