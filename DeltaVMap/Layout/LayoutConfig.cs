namespace DeltaVMap.Layout;

// Tunable layout constants, all in pixels at 100% zoom unless noted. These are
// starting values; the interactive layout recomputes widths from real ImGui text
// metrics and tunes the spacing against the rendered map, so treat these numbers as
// indicative rather than final.
internal sealed class LayoutConfig
{
    // Square snap grid. Must be at least the sum of the two largest dot radii so that
    // two dots in orthogonally adjacent cells never overlap; the overlap check relies
    // on it. Validate() enforces this.
    public double GridPx { get; init; } = 32.0;

    // Vertical height of one dV band. A multiple of GridPx so band rows map cleanly
    // onto grid rows. Must be at least MinSegmentPx so every non-structural edge
    // clears the minimum vertical gap. Generous so stacked rungs leave label room.
    public double BandHeightPx { get; init; } = 128.0;

    // Floor on a single edge's vertical length. Validate() requires BandHeightPx to
    // meet it, so every one-band edge clears this minimum gap.
    public double MinSegmentPx { get; init; } = 40.0;

    // dV that maps to one band step, and the clamp on how many band steps a single
    // edge may span. Without the clamp a multi-km/s transfer would stretch the map
    // far taller than it needs to be; the badge text carries the exact value anyway.
    public double BandQuantumDv { get; init; } = 250.0;
    public int MinBandStep { get; init; } = 1;
    public int MaxBandStep { get; init; } = 6;

    // Horizontal breathing room. SiblingGap is added to the half-widths when the
    // tidy tree separates adjacent siblings; BusGap separates whole hub-bus subtrees.
    // Both are wide so neighbouring vertical lanes leave room for their labels.
    public double SiblingGapPx { get; init; } = 100.0;
    public double BusGapPx { get; init; } = 160.0;

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
    // get their own sizes below.
    public double RootDotRadius { get; init; } = 14.0;
    public double HubDotRadius { get; init; } = 11.0;
    public double YouAreHereDotRadius { get; init; } = 9.0;
    public double PlanetDotRadius { get; init; } = 9.0;
    public double MoonDotRadius { get; init; } = 7.0;
    public double MinorDotRadius { get; init; } = 5.0;

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
    }
}
