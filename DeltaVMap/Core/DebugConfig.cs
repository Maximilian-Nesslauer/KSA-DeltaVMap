namespace DeltaVMap.Core;

internal static class DebugConfig
{
#if DEBUG
    public static bool Performance = true;
    public static bool ValidationDump = true;
#else
    public static bool Performance = false;
    public static bool ValidationDump = false;
#endif

    public static bool Any => Performance || ValidationDump;
}
