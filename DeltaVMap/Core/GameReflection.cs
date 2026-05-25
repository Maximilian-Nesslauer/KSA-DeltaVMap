using System.Reflection;
using Brutal.Logging;
using HarmonyLib;

namespace DeltaVMap.Core;

internal static class GameReflection
{
    private static bool ValidateTargets(string feature, (string name, object? target)[] targets)
    {
        bool allOk = true;
        foreach (var (name, target) in targets)
        {
            if (target == null)
            {
                DefaultCategory.Log.Error(
                    $"[DvMap] {feature}: {name} not found - game version may have changed.");
                allOk = false;
            }
        }
        return allOk;
    }
}
