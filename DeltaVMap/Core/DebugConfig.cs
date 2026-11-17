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

    // The folder of the layout debug dump (the in-game LayoutDump and the test harness).
    // DELTAVMAP_LAYOUT_DIR names it on a developer machine, and without it the dump goes
    // to the temp folder, so the source names no local path.
    public static string LayoutDumpDir =>
        Environment.GetEnvironmentVariable("DELTAVMAP_LAYOUT_DIR") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Path.GetTempPath(), "DeltaVMap", "layout");
}
