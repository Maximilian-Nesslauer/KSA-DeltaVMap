namespace DeltaVMap.Core;

internal static class DebugConfig
{
#if DEBUG
    public static bool Performance = true;
#else
    public static bool Performance = false;
#endif

    public static bool Any => Performance;
}
