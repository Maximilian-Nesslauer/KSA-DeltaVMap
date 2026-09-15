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

    // Local dev directory for the layout debug dump (in-game LayoutDump and the test
    // harness); debug-only scaffolding, so a hardcoded source-tree path is fine.
    public const string LayoutDumpDir = @"F:\Coding\KSA Modding\private\temp\deltav-layout";
}
