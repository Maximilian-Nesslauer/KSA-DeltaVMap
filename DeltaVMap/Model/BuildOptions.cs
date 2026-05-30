namespace DeltaVMap.Model;

// Options that change which nodes the visual tree materializes, so changing any of them
// forces a rebuild (unlike the route toggles, which only re-accumulate a selected path).
// FullLadder promotes every body to its full rung set; the visibility flags drop whole
// bodies from the tree. They never hide the root or a spine ancestor (you must still be
// able to see where you are and route through the hubs), only destinations.
internal readonly struct BuildOptions
{
    public readonly bool FullLadder;
    public readonly bool ShowMinorBodies;
    public readonly bool ShowComets;

    public BuildOptions(bool fullLadder, bool showMinorBodies, bool showComets)
    {
        FullLadder = fullLadder;
        ShowMinorBodies = showMinorBodies;
        ShowComets = showComets;
    }

    // The defaults: full detail context-dependent, everything visible.
    public static BuildOptions Default => new(fullLadder: false, showMinorBodies: true, showComets: true);
}
