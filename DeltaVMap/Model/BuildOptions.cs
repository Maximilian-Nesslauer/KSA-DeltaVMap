using System.Collections.Generic;

namespace DeltaVMap.Model;

// Options that change which nodes the visual tree materializes, so changing any of them
// forces a rebuild (unlike the route toggles, which only re-accumulate a selected path).
// FullLadder promotes every body to its full rung set; the visibility flags drop whole
// bodies from the tree. They never hide the root or a spine ancestor (you must still be
// able to see where you are and route through the hubs), only destinations.
internal readonly struct BuildOptions
{
    // When a hub carries more visible minor bodies than this, all of its minor bodies
    // collapse into a single "+N" group node instead of one lane each (so a dense belt does
    // not blow the map up to hundreds of thousands of pixels), and the "+N" is the full
    // count. It sits comfortably above the busiest stock hub (Sol, with 9 minor children) so
    // the stock map collapses nothing and looks exactly as before, while a dense system's
    // hundreds-to-thousands collapse. Tunable.
    public const int DefaultMinorGroupThreshold = 24;

    public readonly bool FullLadder;
    public readonly bool ShowMinorBodies;
    public readonly bool ShowComets;
    public readonly int MinorGroupThreshold;

    // Isolate mode: show only the major bodies, the spine, and any revealed (searched) body,
    // hiding every other minor body so a single asteroid is reachable out of thousands
    // without rendering the rest. The spine and majors are never destination-filtered, so
    // this only drops un-revealed minor bodies.
    public readonly bool Isolate;

    // Bodies forced visible even when their hub would collapse them (or isolate would hide
    // them): the search/focus picks add to this so a chosen body is materialized as its own
    // lane instead of staying inside the "+N" group. Null means none are revealed.
    public readonly IReadOnlySet<string>? RevealedBodies;

    public BuildOptions(bool fullLadder, bool showMinorBodies, bool showComets,
        bool isolate = false, IReadOnlySet<string>? revealedBodies = null,
        int minorGroupThreshold = DefaultMinorGroupThreshold)
    {
        FullLadder = fullLadder;
        ShowMinorBodies = showMinorBodies;
        ShowComets = showComets;
        Isolate = isolate;
        RevealedBodies = revealedBodies;
        MinorGroupThreshold = minorGroupThreshold;
    }

    // Whether a body has been revealed (searched) and so must always be shown individually.
    public bool IsRevealed(string bodyId)
    {
        return RevealedBodies != null && RevealedBodies.Contains(bodyId);
    }

    // The defaults: full detail context-dependent, everything visible, adaptive collapse.
    public static BuildOptions Default => new(fullLadder: false, showMinorBodies: true, showComets: true);
}
