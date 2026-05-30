namespace DeltaVMap.Layout;

// Which layout strategy the X and Y passes use. CumulativeDown is the as-built pipeline
// (Y is cumulative dV from the root growing downward, a body's rungs are X-spread
// siblings). GravityWell hangs a row of vertical wells off a horizontal low-orbit spine
// (every body's low orbit sits on one line at Y=0, its surface dangles below, its high
// orbits and capture poke above), the structure the canonical KSP subway map uses. Spring
// is a force-directed alternative: nodes repel, edges pull, settled once into an organic
// web with the root pinned at the center. It trades the dV-as-position meaning of the
// other two for a free-form look, so it draws straight edges and skips the tree-shaped
// overlap invariants. The engine carries no game types, so the mode lives here and the
// passes branch on it.
internal enum LayoutMode
{
    CumulativeDown,
    GravityWell,
    Spring
}

// Tunable layout constants, all in pixels at 100% zoom unless noted. These are
// starting values; the interactive layout recomputes widths from real ImGui text
// metrics and tunes the spacing against the rendered map, so treat these numbers as
// indicative rather than final.
internal sealed class LayoutConfig
{
    // The active layout strategy. Defaults to the as-built CumulativeDown so any caller
    // that does not pick a mode (and the existing offline cases) keep their verified
    // output unchanged; the map window picks the mode explicitly (currently CumulativeDown).
    public LayoutMode Mode { get; init; } = LayoutMode.CumulativeDown;
    // Square snap grid. Must be at least the sum of the two largest dot radii so that
    // two dots in orthogonally adjacent cells never overlap; the overlap check relies
    // on it. Validate() enforces this.
    public double GridPx { get; init; } = 35.0;

    // Vertical height of one dV band. A multiple of GridPx so band rows map cleanly
    // onto grid rows. Must be at least MinSegmentPx so every non-structural edge
    // clears the minimum vertical gap. Generous so stacked rungs leave label room.
    public double BandHeightPx { get; init; } = 140.0;

    // Floor on a single edge's vertical length. Validate() requires BandHeightPx to
    // meet it, so every one-band edge clears this minimum gap.
    public double MinSegmentPx { get; init; } = 40.0;

    // dV that maps to one band step, and the clamp on how many band steps a single
    // edge may span. Without the clamp a multi-km/s transfer would stretch the map
    // far taller than it needs to be; the badge text carries the exact value anyway.
    public double BandQuantumDv { get; init; } = 250.0;
    public int MinBandStep { get; init; } = 1;
    public int MaxBandStep { get; init; } = 6;

    // GravityWell vertical spacing. A body's well straddles the spine: surface a few
    // bands below, high orbits / capture a few bands above. The well is deliberately
    // shallow (a smaller band height and a tighter step clamp than the cumulative
    // bands) so a dense planet root reads as a wide, low band centered on the spine
    // rather than a tall thin strip. Must stay a whole multiple of GridPx and at least
    // MinSegmentPx; Validate() enforces both.
    public double WellBandHeightPx { get; init; } = 70.0;
    public int WellMaxBandStep { get; init; } = 3;

    // Spring (force-directed) layout. IdealLength is the rest length of an edge spring
    // (and the scale of the all-pairs repulsion); Iterations is the fixed number of
    // settle steps run once on build, so the result is deterministic and does not animate
    // per frame. Both are deliberately generous so a dense root spreads into a readable web.
    public double SpringIdealLengthPx { get; init; } = 120.0;
    public int SpringIterations { get; init; } = 460;

    // Horizontal breathing room. SiblingGap is added to the half-widths when the
    // tidy tree separates adjacent siblings; BusGap separates whole hub-bus subtrees.
    // Both are wide so neighbouring vertical lanes leave room for their labels.
    public double SiblingGapPx { get; init; } = 100.0;
    public double BusGapPx { get; init; } = 160.0;

    // Extra gap inserted after the root's own subtree in CumulativeDown, on top of BusGap,
    // so the root (and its moons) sit in a visibly detached cluster at the top-left rather
    // than blending into the hub bus. Pure separation, so the root reads as "you start
    // here"; the renderer also draws a halo on it. Zero would fall back to plain BusGap.
    public double RootMarginPx { get; init; } = 110.0;

    // Approximate text metrics for the offline pass. Real widths come from
    // ImGui.CalcTextSize inside the draw loop later.
    public double CharWidthPx { get; init; } = 7.0;
    public double LineHeightPx { get; init; } = 16.0;
    public double MinNodeWidthPx { get; init; } = 36.0;

    // Extra width reserved next to a label for its dV badge, so the tidy tree leaves
    // room for the badge the renderer will draw.
    public double BadgePaddingPx { get; init; } = 18.0;

    // Perpendicular spacing between parallel edge lanes leaving one node, so several
    // siblings dropping from the same hub stay visually distinct.
    public double LaneOffsetPx { get; init; } = 6.0;

    // Length (per axis) of the 45-degree diagonal the edge router inserts between the
    // horizontal traverse and the vertical drop into a node. Capped by the available
    // horizontal and vertical room, so it shrinks to nothing for a directly-below child
    // or a same-band hub link. Gives the map a metro-style angled corner instead of a
    // hard right angle. Kept below BandHeightPx so a one-band edge keeps a short
    // vertical lane (here 128 - 96 = 32 px) rather than becoming a pure diagonal.
    public double EdgeDiagonalPx { get; init; } = 96.0;

    // Dot radius by node rank. Rank 0 is the ego root, 1 a planet-level
    // body, 2 a moon-level body, 3 a minor body; hubs and the you-are-here marker
    // get their own sizes below. Sized so the concentric-ring glyphs (ring + dot + ticks)
    // are legible at the zoomed-out auto-fit, not just up close; the largest pair
    // (root + hub) must stay below GridPx so two adjacent dots never touch (Validate()).
    public double RootDotRadius { get; init; } = 20.0;
    public double HubDotRadius { get; init; } = 15.0;
    public double YouAreHereDotRadius { get; init; } = 13.0;
    public double PlanetDotRadius { get; init; } = 14.0;
    public double MoonDotRadius { get; init; } = 11.0;
    public double MinorDotRadius { get; init; } = 9.0;

    public static LayoutConfig Default => new();

    // Enforce the spacing invariants the comments above promise, so a misconfigured
    // instance fails loudly instead of silently producing overlaps. Cheap, called once
    // per layout run; all shipped configs satisfy it.
    public void Validate()
    {
        double largestPair = RootDotRadius + HubDotRadius;
        if (GridPx < largestPair)
            throw new System.ArgumentException(
                $"GridPx ({GridPx}) must be at least the sum of the two largest dot radii ({largestPair}).");
        if (BandHeightPx < MinSegmentPx)
            throw new System.ArgumentException(
                $"BandHeightPx ({BandHeightPx}) must be at least MinSegmentPx ({MinSegmentPx}).");
        double rows = BandHeightPx / GridPx;
        if (System.Math.Abs(rows - System.Math.Round(rows)) > 1e-9)
            throw new System.ArgumentException(
                $"BandHeightPx ({BandHeightPx}) must be a whole multiple of GridPx ({GridPx}).");

        if (WellBandHeightPx < MinSegmentPx)
            throw new System.ArgumentException(
                $"WellBandHeightPx ({WellBandHeightPx}) must be at least MinSegmentPx ({MinSegmentPx}).");
        double wellRows = WellBandHeightPx / GridPx;
        if (System.Math.Abs(wellRows - System.Math.Round(wellRows)) > 1e-9)
            throw new System.ArgumentException(
                $"WellBandHeightPx ({WellBandHeightPx}) must be a whole multiple of GridPx ({GridPx}).");

        if (SpringIdealLengthPx <= 0.0)
            throw new System.ArgumentException(
                $"SpringIdealLengthPx ({SpringIdealLengthPx}) must be positive (it is the spring rest length and force scale).");
        if (SpringIterations < 1)
            throw new System.ArgumentException(
                $"SpringIterations ({SpringIterations}) must be at least 1.");
    }
}
