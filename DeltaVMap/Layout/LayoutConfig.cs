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

// Whether a wide fan of a tree layout continues in rows below its parent. Off keeps one row per
// fan. Auto lets the layout pick the rows from the fit on the nominal canvas. Spring ignores it,
// because its web is already about square.
internal enum WrapMode
{
    Off,
    Auto
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

    // Row wrapping of wide fans. Off by default, so a caller that does not ask for it gets
    // one row per fan. The map window builds its config through ForMap, which sets it.
    public WrapMode Wrap { get => _wrap; init => _wrap = value; }
    private WrapMode _wrap = WrapMode.Off;
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

    // A factor the structure axis is stretched by about the root, after the X pass and before
    // the grid snap (in Spring both axes, since its position has no dV meaning). The map lays
    // out another star system with it, so the system stays readable at the zoom that fits the
    // much larger home map. 1 leaves the layout as it is.
    public double SpreadX { get; init; } = 1.0;

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

    // Row wrapping, used only when Wrap is Auto. A fan wraps when it has more lanes than the
    // minimum (children that are not ladder rungs). Each row holds about Kmax lanes, and a fan
    // has at most MaxRows rows. CumulativeDown and GravityWell have their own values, because a
    // CumulativeDown row adds a drop that is not delta-v while a GravityWell row costs only its
    // local well height. These are tunables for calibration.
    public int WrapMinLanesCd { get; init; } = 12;
    public int WrapKmaxCd { get; init; } = 7;
    public int WrapMaxRowsCd { get; init; } = 4;
    public int WrapMinLanesGw { get; init; } = 8;
    public int WrapKmaxGw { get; init; } = 7;
    public int WrapMaxRowsGw { get; init; } = 6;

    // One more row is taken only when it improves the fit zoom on the nominal canvas by at least
    // this fraction, so a row that gains almost nothing does not cost a layout-only drop.
    public double WrapMinGain { get; init; } = 0.10;

    // The horizontal gutter left of a wrapped block, which holds the trunk that joins its rows.
    // A whole multiple of GridPx, so the trunk lands on a grid column. Validate() enforces it.
    public double WrapGutterPx { get; init; } = 70.0;

    // The clear space in screen pixels a shelf of rows keeps from the shelf above it, at the
    // nominal fit zoom. It is a moon dot (2 * MoonDotRadius), a label line (LineHeightPx) and
    // the line clearance of the scene checks. The layout divides it by the zoom and snaps the
    // result up to GridPx, so this value itself is not on the grid.
    public double ShelfClearScreenPx { get; init; } = 44.0;

    // The share of the nominal fit zoom down to which a Cumulative-down trunk keeps the line
    // clearance of the scene checks from the dots on both sides of its gutter. A 1300x860 canvas
    // with the strip of other star systems below the map and one of them opened draws the map at
    // about this share, so a hub's own rung dot stays clear of its trunk there too.
    public double WrapLineZoomShare { get; init; } = 0.6;

    // Width over height of the nominal canvas the wrap scores against. ForMap sets 1.6, or 2.4
    // when other star systems need the strip below the map, so a wrapped map stays wide enough
    // for the strip to stay below it.
    public double WrapTargetAspect { get; init; } = 1.6;

    // The zoom the screen-pixel gaps and gutters of the wrap are sized for. An opened destination
    // system is drawn at the zoom that fits the ego map beside it, not at its own fit zoom, so
    // SceneComposer.LayOutPart sets the ego map's nominal zoom here. 0 sizes them for the map's
    // own nominal fit zoom.
    public double WrapScreenZoom { get => _wrapScreenZoom; init => _wrapScreenZoom = value; }
    private double _wrapScreenZoom;

    public static LayoutConfig Default => new();

    // The config of the interactive map, which the game and the harness tests both use, so a
    // test checks the same config the player sees. hasSeveralSystems picks the wider target
    // aspect, because the other star systems then take a strip below the map.
    public static LayoutConfig ForMap(LayoutMode mode, bool hasSeveralSystems, WrapMode wrap)
    {
        return new LayoutConfig
        {
            Mode = mode,
            Wrap = wrap,
            WrapTargetAspect = hasSeveralSystems ? SeveralSystemsAspect : OneSystemAspect
        };
    }

    private const double OneSystemAspect = 1.6;
    private const double SeveralSystemsAspect = 2.4;

    // A copy with another structure spread and band step range, for laying out another star
    // system beside a map laid out with this config. maxBandStep caps the cumulative and the
    // gravity-well step alike. The copy keeps the wrap tunables but always has Wrap Off, because
    // a destination system wraps only where SceneComposer.LayOutPart turns it back on.
    public LayoutConfig WithSpread(double spreadX, int minBandStep, int maxBandStep)
    {
        return new LayoutConfig
        {
            Mode = Mode,
            GridPx = GridPx,
            BandHeightPx = BandHeightPx,
            MinSegmentPx = MinSegmentPx,
            BandQuantumDv = BandQuantumDv,
            MinBandStep = minBandStep,
            MaxBandStep = System.Math.Min(MaxBandStep, maxBandStep),
            WellBandHeightPx = WellBandHeightPx,
            WellMaxBandStep = System.Math.Min(WellMaxBandStep, maxBandStep),
            SpringIdealLengthPx = SpringIdealLengthPx,
            SpringIterations = SpringIterations,
            SpreadX = spreadX,
            SiblingGapPx = SiblingGapPx,
            BusGapPx = BusGapPx,
            RootMarginPx = RootMarginPx,
            CharWidthPx = CharWidthPx,
            LineHeightPx = LineHeightPx,
            MinNodeWidthPx = MinNodeWidthPx,
            BadgePaddingPx = BadgePaddingPx,
            LaneOffsetPx = LaneOffsetPx,
            EdgeDiagonalPx = EdgeDiagonalPx,
            RootDotRadius = RootDotRadius,
            HubDotRadius = HubDotRadius,
            YouAreHereDotRadius = YouAreHereDotRadius,
            PlanetDotRadius = PlanetDotRadius,
            MoonDotRadius = MoonDotRadius,
            MinorDotRadius = MinorDotRadius,
            WrapMinLanesCd = WrapMinLanesCd,
            WrapKmaxCd = WrapKmaxCd,
            WrapMaxRowsCd = WrapMaxRowsCd,
            WrapMinLanesGw = WrapMinLanesGw,
            WrapKmaxGw = WrapKmaxGw,
            WrapMaxRowsGw = WrapMaxRowsGw,
            WrapMinGain = WrapMinGain,
            WrapGutterPx = WrapGutterPx,
            ShelfClearScreenPx = ShelfClearScreenPx,
            WrapLineZoomShare = WrapLineZoomShare,
            WrapTargetAspect = WrapTargetAspect,
            WrapScreenZoom = WrapScreenZoom
        };
    }

    // An exact copy with Wrap Off, for laying out a destination system from the ego config.
    public LayoutConfig WithoutWrap()
    {
        return WithSpread(SpreadX, MinBandStep, System.Math.Max(MaxBandStep, WellMaxBandStep));
    }

    // An exact copy with another wrap mode and every wrap tunable kept, so one config can be laid
    // out with and without its rows.
    public LayoutConfig WithWrap(WrapMode wrap)
    {
        var copy = (LayoutConfig)MemberwiseClone();
        copy._wrap = wrap;
        return copy;
    }

    // An exact copy whose wrap sizes its screen gaps for the given zoom, see WrapScreenZoom.
    public LayoutConfig WithWrapScreenZoom(double zoom)
    {
        var copy = (LayoutConfig)MemberwiseClone();
        copy._wrapScreenZoom = zoom;
        return copy;
    }

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
        if (SpreadX < 1.0 || SpreadX > 4.0)
            throw new System.ArgumentException(
                $"SpreadX ({SpreadX}) must be between 1 and 4.");

        if (WrapMinLanesCd < 1 || WrapMinLanesGw < 1)
            throw new System.ArgumentException(
                $"WrapMinLanesCd ({WrapMinLanesCd}) and WrapMinLanesGw ({WrapMinLanesGw}) must be at least 1.");
        if (WrapKmaxCd < 2 || WrapKmaxGw < 2)
            throw new System.ArgumentException(
                $"WrapKmaxCd ({WrapKmaxCd}) and WrapKmaxGw ({WrapKmaxGw}) must be at least 2, because no row holds a single lane.");
        if (WrapMaxRowsCd < 1 || WrapMaxRowsGw < 1)
            throw new System.ArgumentException(
                $"WrapMaxRowsCd ({WrapMaxRowsCd}) and WrapMaxRowsGw ({WrapMaxRowsGw}) must be at least 1.");
        if (WrapMinGain < 0.0)
            throw new System.ArgumentException(
                $"WrapMinGain ({WrapMinGain}) must not be negative.");
        if (WrapGutterPx <= 0.0)
            throw new System.ArgumentException(
                $"WrapGutterPx ({WrapGutterPx}) must be positive.");
        double gutterCols = WrapGutterPx / GridPx;
        if (System.Math.Abs(gutterCols - System.Math.Round(gutterCols)) > 1e-9)
            throw new System.ArgumentException(
                $"WrapGutterPx ({WrapGutterPx}) must be a whole multiple of GridPx ({GridPx}).");
        if (ShelfClearScreenPx <= 0.0)
            throw new System.ArgumentException(
                $"ShelfClearScreenPx ({ShelfClearScreenPx}) must be positive.");
        if (WrapLineZoomShare <= 0.0 || WrapLineZoomShare > 1.0)
            throw new System.ArgumentException(
                $"WrapLineZoomShare ({WrapLineZoomShare}) must be above 0 and at most 1.");
        if (WrapTargetAspect <= 0.0)
            throw new System.ArgumentException(
                $"WrapTargetAspect ({WrapTargetAspect}) must be positive.");
        if (WrapScreenZoom < 0.0)
            throw new System.ArgumentException(
                $"WrapScreenZoom ({WrapScreenZoom}) must not be negative.");
    }
}
