using System;
using System.Collections.Generic;

namespace DeltaVMap.Layout;

// Places the other star systems as a strip of separate graphs beside the ego system's map, the
// same way in all three layout modes. The ego layout comes from LayoutEngine.Run unchanged and
// is never moved, so the home map looks exactly as it does without other systems.
//
// The strip runs along the ego map's free side, below it when the map is wide (at least 1.5
// times as wide as tall) and to its right otherwise. The side depends only on the ego bounds,
// so opening a system never flips it. Every gap is sized in screen pixels at a nominal fit zoom
// (the zoom that fits the ego map into a fixed 1600x1000 canvas), and every screen-fixed dot,
// label and title is counted at the lowest fit zoom the strip is sized for, so the empty
// channel between the home map and the strip reads the same on screen for a small or a huge
// map, and the live canvas size never moves anything.
//
// The places come from the collapsed shape of every system, which does not depend on what is
// open. The system nearest to the trunk sits next to it, the others follow away from it, and
// all system roots of a row sit on one line. An opened system keeps the place of its collapsed
// root and pushes only the systems on its far side away from the trunk. Only when it would
// reach into its neighbour on the trunk side, or into the channel, does it move off its place.
//
// One dashed connector runs to each destination root. It is made of a trunk that leaves the
// ego hub by the cheapest exit out of the ego box (a crossing-free octilinear exit in the tree
// modes, any of 32 angles in Spring, where crossing an edge or a label costs a long detour and
// crossing a dot is ruled out), a shared bus inside the channel, and a short branch that enters
// the root from its free side. A wrapped row is reached through the gap after the first system of the row
// before it. All of it is a pure function of the laid-out bounds, so a delta-v or cruise-speed
// change can never move it. A "//" mark on the trunk inside the channel says that the line's
// length is not to scale.
internal static class SceneComposer
{
    // The nominal canvas, 1600x1000 logical pixels minus the 32 px fit pad on each side.
    private const double NominalW = 1536.0;
    private const double NominalH = 936.0;

    // The strip goes below the ego map when the map is at least this much wider than tall.
    private const double BelowAspect = 1.5;

    // Screen pixels at the nominal fit zoom for the lane clearance around a graph, the empty
    // channel between the home map and the strip, and the gap between two destination graphs.
    private const double ClearPx = 18.0;
    private const double ChannelPx = 165.0;
    private const double PartGapPx = 64.0;

    // The screen-fixed dots, labels and titles keep their pixel size while the geometry shrinks
    // with the zoom. Counting them a third over keeps the gaps clear down to a fit zoom of
    // about 0.75 of the nominal one, which is a 1300x860 canvas.
    private const double ReachWeight = 1.35;

    // Screen geometry of a system root, shared with the renderer and the fit. These are the
    // collapsed stub's half size, the ring drawn around a system root, the depth of the fan
    // under a stub, the gap between the glyph and its title, the title line height and the
    // title plate padding. The ego root's halo reaches past its dot as well.
    public const double StubHalfPx = 18.0;
    public const double SystemRingPx = 6.0;
    public const double StubFanPx = 14.0;
    public const double TitleGapPx = 8.0;
    public const double TitleLinePx = 17.0;
    public const double TitlePadX = 3.0;
    public const double TitlePadY = 1.0;
    private const double RootHaloPx = 10.0;

    // How far a connector keeps from a dot, an edge or a label, in screen pixels.
    private const double LineClearPx = 6.0;

    // An expanded destination is spread by at most this factor, and never grows across the
    // strip past the first share of the ego map, or along it past the second.
    private const double SpreadTargetZoom = 0.4;
    private const int MaxSpread = 3;
    private const double MaxPartAcross = 0.6;
    private const double MaxPartAlong = 0.5;

    public static double NominalZoom(LayoutResult ego)
    {
        return Math.Min(1.0, Math.Min(NominalW / Math.Max(1.0, ego.Width), NominalH / Math.Max(1.0, ego.Height)));
    }

    // The whole factor a destination graph is spread by in the tree modes, so its nodes stay
    // apart at the zoom that fits the ego map. It is 1 at a nominal zoom of 0.4 or more and
    // grows to 3 for a huge map.
    public static int SpreadFactor(LayoutResult ego)
    {
        int k = (int)Math.Round(SpreadTargetZoom / NominalZoom(ego), MidpointRounding.AwayFromZero);
        return Math.Clamp(k, 1, MaxSpread);
    }

    public static StripSide SideOf(LayoutResult ego)
    {
        return ego.Width >= BelowAspect * ego.Height ? StripSide.Below : StripSide.Right;
    }

    // Lay out one destination system for the scene. build must return a fresh tree each call,
    // because a layout run writes its positions into the nodes. In the tree modes the structure
    // axis is spread by SpreadFactor and every step takes at least that many bands, so a parent
    // and its child stay apart at the zoom that fits the ego map. In Spring both axes are spread
    // until the root's neighbours keep clear of the root's ring and title line at that zoom. The
    // spread is lowered while the part would run along the strip past its share of the ego map.
    // While it would grow across the strip past its share, the tree modes lower the cap on a
    // single delta-v step first, then the minimum step. When no tree layout fits, the flattest
    // one tried is used, since it comes nearest. A Spring part that fits with no spread keeps
    // the plain layout.
    public static LayoutResult LayOutPart(Func<LayoutTree> build, LayoutResult ego, Func<string, double>? measureText)
    {
        LayoutConfig cfg = ego.Config;
        Func<string, double>? measure = measureText != null ? Memoize(measureText) : null;
        LayoutResult plain = LayoutEngine.Run(build(), cfg, measure);
        bool spring = cfg.Mode == LayoutMode.Spring;
        double k = spring ? SpringSpread(plain, ego) : SpreadFactor(ego);
        if (k <= 1.0)
            return plain;

        bool below = SideOf(ego) == StripSide.Below;
        double maxAcross = MaxPartAcross * (below ? ego.Height : ego.Width);
        double maxAlong = MaxPartAlong * (below ? ego.Width : ego.Height);
        int maxStep = cfg.Mode == LayoutMode.GravityWell ? cfg.WellMaxBandStep : cfg.MaxBandStep;
        double Along(LayoutResult r) => below ? r.Width : r.Height;
        double Across(LayoutResult r) => below ? r.Height : r.Width;

        if (spring)
        {
            for (double spread = k; spread > 1.0; spread -= 0.25)
            {
                LayoutResult r = LayoutEngine.Run(build(), cfg.WithSpread(spread, cfg.MinBandStep, maxStep), measure);
                if (Along(r) <= maxAlong && Across(r) <= maxAcross)
                    return r;
            }
            return plain;
        }

        LayoutResult flattest = plain;
        for (int spread = (int)k; spread >= 1; spread--)
        {
            for (int minBand = (int)k; minBand >= 1; minBand--)
            {
                // The flattest band range for this minimum. The spread axis does not depend on
                // the bands, so a part too long here is too long for every band range.
                LayoutResult flat = LayoutEngine.Run(build(), cfg.WithSpread(spread, minBand, minBand), measure);
                if (spread > 1 && Along(flat) > maxAlong)
                    break;
                flattest = flat;
                if (Across(flat) > maxAcross)
                    continue;
                // The part grows across the strip with the band cap, so the tallest cap that
                // still fits is found by bisection.
                LayoutResult best = flat;
                int lo = minBand;
                int hi = Math.Max(minBand, maxStep);
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    LayoutResult r = LayoutEngine.Run(build(), cfg.WithSpread(spread, minBand, mid), measure);
                    if (Across(r) <= maxAcross)
                    {
                        lo = mid;
                        best = r;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }
                return best;
            }
        }
        return flattest;
    }

    private static Func<string, double> Memoize(Func<string, double> measure)
    {
        var widths = new Dictionary<string, double>();
        return text =>
        {
            if (!widths.TryGetValue(text, out double w))
            {
                w = measure(text);
                widths[text] = w;
            }
            return w;
        };
    }

    // The spread a Spring part needs at the lowest fit zoom, so the root's neighbours keep the
    // root's ring, their own dot and one text line apart from it, and any two other nodes keep
    // their two dots and a small gap apart.
    private static double SpringSpread(LayoutResult part, LayoutResult ego)
    {
        double zFit = NominalZoom(ego) / ReachWeight;
        IReadOnlyList<LayoutNode> nodes = part.Tree.Nodes;
        LayoutNode root = part.Tree.Root;
        double need = 1.0;
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                LayoutNode a = nodes[i];
                LayoutNode b = nodes[j];
                double d = Math.Sqrt((a.SnappedX - b.SnappedX) * (a.SnappedX - b.SnappedX) + (a.SnappedY - b.SnappedY) * (a.SnappedY - b.SnappedY));
                if (d <= 0.0)
                    continue;
                double gapPx = a == root || b == root
                    ? GlyphRadiusPx(root) + SystemRingPx + (a == root ? b : a).DotRadius + TitleLinePx
                    : a.DotRadius + b.DotRadius + 4.0;
                need = Math.Max(need, gapPx / (d * zFit));
            }
        }
        return Math.Min(MaxSpread, Math.Ceiling(need * 4.0 - 1e-9) / 4.0);
    }

    // A collapsed system shows its summary as a second title line.
    public static bool ShowsSummary(LayoutNode node)
    {
        return node.IsSystemStub && node.Summary.Length > 0;
    }

    // The screen width of a system root's title block, which is the title line (drawn fake-bold,
    // one pixel wider) or the summary line under it, whichever is wider.
    public static double TitleTextW(LayoutNode node)
    {
        return ShowsSummary(node) ? StubTitleTextW(node) : node.ShortLabelTextW + 1.0;
    }

    // The title block width the root would have collapsed, whether it is open or not.
    private static double StubTitleTextW(LayoutNode node)
    {
        return Math.Max(node.ShortLabelTextW + 1.0, node.SummaryTextW);
    }

    public static double TitleHeightPx(LayoutNode node)
    {
        return ShowsSummary(node) ? 2.0 * TitleLinePx : TitleLinePx;
    }

    public const int TitleSpotCount = 5;

    // The screen spots a system title tries, as the top-left of its plate of size w x h, for a
    // root drawn at (x, y). The first is right of the root with its first line centered on it,
    // which is the room the strip keeps for it. The others stay clear of the root's ring and of a
    // line that enters the root straight from above or from the side. They lie above and right
    // of it, left of it, below and right of it (under a stub's fan), and above and left of it.
    public static void SystemTitleSpots(LayoutNode node, double x, double y, double w, double h, double linePx, Span<(double X, double Y)> spots)
    {
        double r = GlyphRadiusPx(node);
        double ring = r + SystemRingPx;
        double top = y - 0.5 * linePx - TitlePadY;
        double rightX = x + r + TitleGapPx - TitlePadX;
        double leftX = x - r - TitleGapPx - w + TitlePadX;
        double aboveY = y - ring - 2.0 - h;
        double belowY = y + (node.IsSystemStub ? r + StubFanPx : ring) + 2.0;
        spots[0] = (rightX, top);
        spots[1] = (rightX, aboveY);
        spots[2] = (leftX, top);
        spots[3] = (rightX, belowY);
        spots[4] = (leftX, aboveY);
    }

    public const int BadgeSpotCount = 5;

    // The screen spots the route badge of a connector tries, as the top-left of its plate of
    // size w x h, beside its badge anchor at (x, y) on the bus. It goes right of the branch below
    // the bus, right above it, right and a stub's height lower (clear of a root the branch
    // reaches soon), then left below and left above.
    public static (double X, double Y) BadgeSpot(int i, double x, double y, double w, double h)
    {
        double right = x + 3.0;
        double left = x - 3.0 - w;
        return i switch
        {
            0 => (right, y + 3.0),
            1 => (right, y - 3.0 - h),
            2 => (right, y + StubHalfPx + SystemRingPx + 2.0),
            3 => (left, y + 3.0),
            _ => (left, y - 3.0 - h)
        };
    }

    // The screen radius a node's glyph is drawn at. A collapsed system is a larger square.
    public static double GlyphRadiusPx(LayoutNode node)
    {
        return node.IsSystemStub ? StubHalfPx : node.DotRadius;
    }

    // Whether a screen rectangle touches a connector line, for a view that draws a layout point
    // p at (originX + p.X * zoom, originY + p.Y * zoom). The test runs in layout space.
    public static bool ConnectorsHitScreenRect(LayoutScene scene, double zoom, double originX, double originY, double x0, double y0, double x1, double y1)
    {
        if (scene.Connectors.Count == 0 || zoom <= 0.0)
            return false;
        double lx0 = (x0 - originX) / zoom;
        double ly0 = (y0 - originY) / zoom;
        double lx1 = (x1 - originX) / zoom;
        double ly1 = (y1 - originY) / zoom;
        IReadOnlyList<IReadOnlyList<LayoutPoint>> network = scene.Network;
        for (int i = 0; i < network.Count; i++)
        {
            IReadOnlyList<LayoutPoint> line = network[i];
            for (int p = 0; p + 1 < line.Count; p++)
            {
                if (SegmentHitsRect(line[p], line[p + 1], lx0, ly0, lx1, ly1))
                    return true;
            }
        }
        IReadOnlyList<LayoutConnector> connectors = scene.Connectors;
        for (int i = 0; i < connectors.Count; i++)
        {
            LayoutConnector connector = connectors[i];
            LayoutPoint a = connector.BadgeAnchor;
            for (int p = connector.BranchStart; p < connector.Polyline.Count; p++)
            {
                LayoutPoint b = connector.Polyline[p];
                if (SegmentHitsRect(a, b, lx0, ly0, lx1, ly1))
                    return true;
                a = b;
            }
        }
        return false;
    }

    // A part's reach around its unmoved root, in layout units, along and across the strip.
    private readonly record struct Extent(double AlongLo, double AlongHi, double AcrossLo, double AcrossHi);

    public static LayoutScene Compose(LayoutResult ego, LayoutNode? egoHub, IReadOnlyList<LayoutPart> parts, LayoutConfig cfg)
    {
        var nodes = new List<LayoutNode>(ego.Tree.Nodes.Count);
        AddNodes(nodes, ego.Tree.Nodes);

        if (parts.Count == 0 || egoHub == null)
        {
            if (egoHub != null)
                egoHub.IsSystemRoot = false;
            return new LayoutScene
            {
                Ego = ego,
                Parts = Array.Empty<LayoutPart>(),
                Nodes = nodes,
                Connectors = Array.Empty<LayoutConnector>(),
                MinX = ego.MinX,
                MinY = ego.MinY,
                MaxX = ego.MaxX,
                MaxY = ego.MaxY
            };
        }

        egoHub.IsSystemRoot = true;
        double g = cfg.GridPx;
        double zNom = NominalZoom(ego);
        double s = ReachWeight / zNom;
        double zFit = zNom / ReachWeight;
        bool below = SideOf(ego) == StripSide.Below;
        bool spring = cfg.Mode == LayoutMode.Spring;
        double clear = Math.Max(g, SnapUp(ClearPx / zNom, g));
        double partGap = Math.Max(4.0 * g, SnapUp(PartGapPx / zNom, g));
        double channel = Math.Max(6.0 * g, SnapUp((ChannelPx + ReachWeight * EgoReachPx(ego, below, zNom)) / zNom, g));
        double egoEdge = below ? ego.MaxY : ego.MaxX;
        // The bus runs in the middle of the channel. Beside a strip to the right it sits nearer
        // the ego map, so each branch is long enough to carry the route badge across the
        // channel before it reaches its root.
        double bus = Snap(egoEdge + (below ? 0.5 : 0.3) * channel, g);
        Box egoBox = EgoBox(ego, below, s, zFit, clear, g);
        List<LayoutPoint> trunk = Trunk(ego.Tree.Nodes, egoHub, egoBox, below, bus, g, spring, s, zFit);
        LayoutPoint join = trunk[trunk.Count - 1];

        int n = parts.Count;
        var stub = new Extent[n];
        var actual = new Extent[n];
        for (int i = 0; i < n; i++)
        {
            parts[i].Root.IsSystemRoot = true;
            stub[i] = StubExtent(parts[i].Root, below, s, g);
            actual[i] = PartExtent(parts[i], below, s, zFit, g, stub[i]);
        }

        // Positions along the strip run in u = dir * along, so the junction side is always the
        // low end, and a row runs from the trunk toward the end of the ego map with more room. A
        // row the trunk reaches past the end of the ego map starts at that end. Otherwise its
        // first root sits right under the trunk.
        double egoLo = below ? ego.MinX : ego.MinY;
        double egoHi = below ? ego.MaxX : ego.MaxY;
        double joinAlong = below ? join.X : join.Y;
        bool back = joinAlong > 0.5 * (egoLo + egoHi);
        bool beyond = back ? joinAlong >= egoHi : joinAlong <= egoLo;
        double dir = back ? -1.0 : 1.0;
        double uLo = back ? -egoHi : egoLo;
        double uHi = back ? -egoLo : egoHi;
        double LoU(in Extent e) => back ? -e.AlongHi : e.AlongLo;
        double HiU(in Extent e) => back ? -e.AlongLo : e.AlongHi;

        // Rows and the places of the roots come from the collapsed shapes. Parts flow nearest
        // first and wrap where they would not fit the length of the ego map.
        var rowFirst = new List<int> { 0 };
        var rowLen = new List<double>();
        double used = 0.0;
        for (int i = 0; i < n; i++)
        {
            double w = HiU(stub[i]) - LoU(stub[i]);
            int lead = rowFirst[rowFirst.Count - 1];
            if (i > lead && used + partGap + w > uHi - uLo)
            {
                rowFirst.Add(i);
                rowLen.Add(used);
                used = w;
            }
            else
            {
                used += (i > lead ? partGap : 0.0) + w;
            }
        }
        rowLen.Add(used);
        int rows = rowFirst.Count;
        var rowOf = new int[n];
        var stubRoot = new double[n];
        for (int row = 0; row < rows; row++)
        {
            int first = rowFirst[row];
            int end = row + 1 < rows ? rowFirst[row + 1] : n;
            double start = beyond ? uLo : Snap(dir * joinAlong, g) + LoU(stub[first]);
            if (uLo - start > 0.1 * (uHi - uLo))
                start = uLo;
            if (start + rowLen[row] > uHi)
                start = Math.Max(uLo, SnapDown(uHi - rowLen[row], g));
            double cursor = start;
            for (int i = first; i < end; i++)
            {
                rowOf[i] = row;
                stubRoot[i] = cursor - LoU(stub[i]);
                cursor += HiU(stub[i]) - LoU(stub[i]) + partGap;
            }
        }

        // A part keeps its collapsed place unless the part before it, nearer the trunk, reaches
        // into it, and it keeps out of the channel. The root line and the near
        // edge of each row come from the collapsed shapes too, and a wrapped row starts past the
        // reach of the row before it.
        var rootU = new double[n];
        var rowBus = new double[rows];
        double rowStart = egoEdge + channel;
        for (int row = 0; row < rows; row++)
        {
            int first = rowFirst[row];
            int end = row + 1 < rows ? rowFirst[row + 1] : n;
            double depth = 0.0;
            for (int i = first; i < end; i++)
                depth = Math.Max(depth, -stub[i].AcrossLo);
            double rootLine = SnapUp(rowStart + depth, g);
            double prevHi = double.NegativeInfinity;
            double rowEnd = double.NegativeInfinity;
            for (int i = first; i < end; i++)
            {
                LayoutPart part = parts[i];
                double u = stubRoot[i];
                if (!double.IsNegativeInfinity(prevHi))
                    u = Math.Max(u, prevHi + partGap - LoU(actual[i]));
                u = Snap(u, g);
                rootU[i] = u;
                prevHi = u + HiU(actual[i]);
                double across = Math.Max(rootLine, SnapUp(rowStart - actual[i].AcrossLo, g));
                double unmovedRootX = part.Root.SnappedX - part.AppliedDx;
                double unmovedRootY = part.Root.SnappedY - part.AppliedDy;
                double along = dir * u;
                double dx = (below ? along : across) - unmovedRootX;
                double dy = (below ? across : along) - unmovedRootY;
                Translate(part, dx - part.AppliedDx, dy - part.AppliedDy, g);
                part.AppliedDx = dx;
                part.AppliedDy = dy;
                rowEnd = Math.Max(rowEnd, across + actual[i].AcrossHi);
            }
            rowBus[row] = row == 0 ? bus : Snap(rowStart - 0.5 * partGap, g);
            rowStart = SnapUp(rowEnd + partGap, g);
        }

        // A wrapped row is reached across the row before it, through the gap after that row's
        // first part, or past its only part.
        var link = new double[rows];
        for (int row = 1; row < rows; row++)
        {
            int first = rowFirst[row - 1];
            double gapU = first + 1 < rowFirst[row]
                ? 0.5 * (rootU[first] + HiU(actual[first]) + rootU[first + 1] + LoU(actual[first + 1]))
                : rootU[first] + HiU(actual[first]) + 0.5 * partGap;
            link[row] = Snap(dir * gapU, g);
        }

        double inflate = Math.Max(g, Math.Min(clear, SnapUp(partGap / 3.0, g)));
        var connectors = new LayoutConnector[n];
        var busLo = new double[rows];
        var busHi = new double[rows];
        for (int row = 0; row < rows; row++)
        {
            double at = row == 0 ? joinAlong : link[row];
            busLo[row] = at;
            busHi[row] = at;
            if (row + 1 < rows)
            {
                busLo[row] = Math.Min(busLo[row], link[row + 1]);
                busHi[row] = Math.Max(busHi[row], link[row + 1]);
            }
        }
        for (int i = 0; i < n; i++)
        {
            LayoutPart part = parts[i];
            int row = rowOf[i];
            var line = new List<LayoutPoint>(trunk.Count + 2 * row + 3);
            line.AddRange(trunk);
            for (int r = 1; r <= row; r++)
            {
                line.Add(OnBus(below, link[r], rowBus[r - 1]));
                line.Add(OnBus(below, link[r], rowBus[r]));
            }
            LayoutPoint entry = Entry(part, below, inflate, s, zFit, g);
            double entryAlong = below ? entry.X : entry.Y;
            LayoutPoint onBus = OnBus(below, entryAlong, rowBus[row]);
            busLo[row] = Math.Min(busLo[row], entryAlong);
            busHi[row] = Math.Max(busHi[row], entryAlong);
            line.Add(onBus);
            line.Add(entry);
            line.Add(new LayoutPoint(part.Root.SnappedX, part.Root.SnappedY));
            List<LayoutPoint> simple = Simplify(line);
            connectors[i] = new LayoutConnector
            {
                From = egoHub,
                To = part.Root,
                Polyline = simple,
                BadgeAnchor = onBus,
                BranchStart = BranchStart(simple, onBus)
            };
        }

        var network = new List<IReadOnlyList<LayoutPoint>>(2 * rows) { trunk };
        for (int row = 0; row < rows; row++)
        {
            if (row > 0)
                network.Add(new[] { OnBus(below, link[row], rowBus[row - 1]), OnBus(below, link[row], rowBus[row]) });
            if (busHi[row] > busLo[row])
                network.Add(new[] { OnBus(below, busLo[row], rowBus[row]), OnBus(below, busHi[row], rowBus[row]) });
        }

        // The scale-break mark sits on the trunk where it crosses the middle of the channel
        // half nearer the ego map.
        double boxEdge = below ? egoBox.Y1 : egoBox.X1;
        double markLine = 0.5 * (boxEdge + bus);
        bool hasMark = false;
        bool markVertical = false;
        LayoutPoint mark = default;
        for (int i = 0; i + 1 < trunk.Count && !hasMark; i++)
        {
            LayoutPoint a = trunk[i];
            LayoutPoint b = trunk[i + 1];
            double a0 = below ? a.Y : a.X;
            double b0 = below ? b.Y : b.X;
            if (Math.Min(a0, b0) >= markLine || Math.Max(a0, b0) <= markLine)
                continue;
            double t = (markLine - a0) / (b0 - a0);
            mark = new LayoutPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            markVertical = Math.Abs(b.X - a.X) < Math.Abs(b.Y - a.Y);
            hasMark = true;
        }

        double minX = ego.MinX;
        double minY = ego.MinY;
        double maxX = ego.MaxX;
        double maxY = ego.MaxY;
        for (int i = 0; i < n; i++)
        {
            LayoutPart part = parts[i];
            AddNodes(nodes, part.Result.Tree.Nodes);
            minX = Math.Min(minX, part.MinX);
            minY = Math.Min(minY, part.MinY);
            maxX = Math.Max(maxX, part.MaxX);
            maxY = Math.Max(maxY, part.MaxY);
        }
        for (int i = 0; i < network.Count; i++)
        {
            IReadOnlyList<LayoutPoint> line = network[i];
            for (int p = 0; p < line.Count; p++)
            {
                minX = Math.Min(minX, line[p].X - 0.5 * g);
                minY = Math.Min(minY, line[p].Y - 0.5 * g);
                maxX = Math.Max(maxX, line[p].X + 0.5 * g);
                maxY = Math.Max(maxY, line[p].Y + 0.5 * g);
            }
        }

        return new LayoutScene
        {
            Ego = ego,
            Parts = new List<LayoutPart>(parts),
            Nodes = nodes,
            Connectors = connectors,
            EgoHub = egoHub,
            Side = below ? StripSide.Below : StripSide.Right,
            Bus = bus,
            Trunk = trunk,
            Network = network,
            HasBreakMark = hasMark,
            BreakMark = mark,
            BreakMarkVertical = markVertical,
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY
        };
    }

    private static LayoutPoint OnBus(bool below, double along, double busLine)
    {
        return below ? new LayoutPoint(along, busLine) : new LayoutPoint(busLine, along);
    }

    // The index of the first polyline point after the badge anchor, so the anchor lies on the
    // segment that ends there. The branch is the end of the line, so the search runs backwards.
    private static int BranchStart(List<LayoutPoint> line, LayoutPoint anchor)
    {
        for (int i = line.Count - 2; i >= 0; i--)
        {
            if (SegmentPointDistance(line[i], line[i + 1], anchor) < 1e-6)
                return i + 1;
        }
        return line.Count - 1;
    }

    // The collapsed shape of a destination around its root, in screen pixels times s, which is
    // the stub with its ring, its fan and its title block on the right. It depends only on the root's
    // title, so it is the same whether the system is open or not. Below the home map it also
    // keeps one title line above the ring, where an opened root's title goes when its first
    // child takes the room on the right, so that title never reaches into the channel.
    private static Extent StubExtent(LayoutNode root, bool below, double s, double g)
    {
        RootBoxPx(root, true, out double x0, out double y0, out double x1, out double y1);
        if (below)
            y0 = Math.Min(y0, -(StubHalfPx + SystemRingPx + 2.0 + TitleLinePx + 2.0 * TitlePadY));
        return ToExtent(x0 * s, y0 * s, x1 * s, y1 * s, below, g);
    }

    // What a part covers around its unmoved root. That is its laid-out bounds, every dot at its
    // screen size and every label the fit shows at its screen width (both at the lowest fit
    // zoom), and its root's glyph and title. A collapsed part never covers less than its collapsed shape.
    private static Extent PartExtent(LayoutPart part, bool below, double s, double zFit, double g, in Extent stub)
    {
        LayoutNode root = part.Root;
        double rx = root.SnappedX - part.AppliedDx;
        double ry = root.SnappedY - part.AppliedDy;
        double x0 = part.Result.MinX - rx;
        double y0 = part.Result.MinY - ry;
        double x1 = part.Result.MaxX - rx;
        double y1 = part.Result.MaxY - ry;
        IReadOnlyList<LayoutNode> nodes = part.Result.Tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode node = nodes[i];
            double nx = node.SnappedX - part.AppliedDx - rx;
            double ny = node.SnappedY - part.AppliedDy - ry;
            if (node.IsSystemRoot)
            {
                RootBoxPx(node, node.IsSystemStub, out double bx0, out double by0, out double bx1, out double by1);
                Grow(ref x0, ref y0, ref x1, ref y1, nx + bx0 * s, ny + by0 * s, nx + bx1 * s, ny + by1 * s);
                continue;
            }
            double r = GlyphRadiusPx(node) * s;
            Grow(ref x0, ref y0, ref x1, ref y1, nx - r, ny - r, nx + r, ny + r);
            if (node.LabelPlaced && LabelLod.RankShows(node, zFit))
            {
                double lx = node.LabelX - part.AppliedDx - rx;
                double ly = node.LabelY - part.AppliedDy - ry;
                Grow(ref x0, ref y0, ref x1, ref y1, lx, ly, lx + node.ShortLabelTextW * s, ly + TitleLinePx * s);
            }
        }
        Extent e = ToExtent(x0, y0, x1, y1, below, g);
        if (!part.Expanded)
        {
            e = new Extent(Math.Min(e.AlongLo, stub.AlongLo), Math.Max(e.AlongHi, stub.AlongHi),
                Math.Min(e.AcrossLo, stub.AcrossLo), Math.Max(e.AcrossHi, stub.AcrossHi));
        }
        return e;
    }

    // The box a system root's glyph, ring and right-hand title plate cover, in screen pixels
    // around the root (y down). As a stub it includes the fan and the summary line.
    private static void RootBoxPx(LayoutNode root, bool asStub, out double x0, out double y0, out double x1, out double y1)
    {
        double r = asStub ? StubHalfPx : GlyphRadiusPx(root);
        double ring = r + SystemRingPx;
        double titleW = asStub ? StubTitleTextW(root) : TitleTextW(root);
        double titleH = (asStub ? 2.0 * TitleLinePx : TitleHeightPx(root)) + 2.0 * TitlePadY;
        double titleTop = -0.5 * TitleLinePx - TitlePadY;
        x0 = -ring;
        y0 = Math.Min(-ring, titleTop);
        x1 = Math.Max(ring, r + TitleGapPx - TitlePadX + titleW + 2.0 * TitlePadX);
        y1 = Math.Max(asStub ? r + StubFanPx : ring, titleTop + titleH);
    }

    private static Extent ToExtent(double x0, double y0, double x1, double y1, bool below, double g)
    {
        x0 = SnapDown(x0, g);
        y0 = SnapDown(y0, g);
        x1 = SnapUp(x1, g);
        y1 = SnapUp(y1, g);
        return below ? new Extent(x0, x1, y0, y1) : new Extent(y0, y1, x0, x1);
    }

    private static void Grow(ref double x0, ref double y0, ref double x1, ref double y1, double ax, double ay, double bx, double by)
    {
        x0 = Math.Min(x0, ax);
        y0 = Math.Min(y0, ay);
        x1 = Math.Max(x1, bx);
        y1 = Math.Max(y1, by);
    }

    // How far the ego map's screen-fixed dots and labels reach past its far edge toward the
    // strip at the nominal zoom. For Below that is under the bottom row, for Right it is right
    // of the right column, where an overview label runs out to the right of its dot.
    private static double EgoReachPx(LayoutResult ego, bool below, double zNom)
    {
        double edge = (below ? ego.MaxY : ego.MaxX) * zNom;
        double far = edge;
        IReadOnlyList<LayoutNode> nodes = ego.Tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode node = nodes[i];
            if (below)
            {
                double bottom = node.SnappedY * zNom + node.DotRadius;
                if (node.LabelPlaced)
                    bottom = Math.Max(bottom, node.LabelY * zNom + TitleLinePx + 1.0);
                far = Math.Max(far, bottom);
            }
            else
            {
                far = Math.Max(far, node.SnappedX * zNom + node.DotRadius + 8.0 + (node.LabelPlaced ? node.ShortLabelTextW : 0.0));
            }
        }
        return far - edge;
    }

    private readonly record struct Box(double X0, double Y0, double X1, double Y1);

    // The box the trunk runs around is the ego map with its screen-fixed dots and fit labels at
    // the lowest fit zoom, grown by the lane clearance. On the strip side it ends at the ego
    // bounds plus the clearance, since the channel already keeps the labels' room there.
    private static Box EgoBox(LayoutResult ego, bool below, double s, double zFit, double clear, double g)
    {
        double x0 = ego.MinX;
        double y0 = ego.MinY;
        double x1 = ego.MaxX;
        double y1 = ego.MaxY;
        IReadOnlyList<LayoutNode> nodes = ego.Tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode node = nodes[i];
            double r = DotReachPx(node) * s;
            Grow(ref x0, ref y0, ref x1, ref y1, node.SnappedX - r, node.SnappedY - r, node.SnappedX + r, node.SnappedY + r);
            if (node.LabelPlaced && !node.IsSystemRoot && LabelLod.RankShows(node, zFit))
                Grow(ref x0, ref y0, ref x1, ref y1, node.LabelX, node.LabelY, node.LabelX + node.ShortLabelTextW * s, node.LabelY + TitleLinePx * s);
        }
        return new Box(
            Math.Floor((x0 - clear) / g) * g,
            Math.Floor((y0 - clear) / g) * g,
            Math.Ceiling(((below ? x1 : ego.MaxX) + clear) / g) * g,
            Math.Ceiling(((below ? ego.MaxY : y1) + clear) / g) * g);
    }

    // The screen radius around a node that a line keeps out of, which is the glyph plus the
    // ring of a system root and the halo of the ego root.
    private static double DotReachPx(LayoutNode node)
    {
        return GlyphRadiusPx(node) + (node.IsSystemRoot ? SystemRingPx : 0.0) + (node.IsRoot ? RootHaloPx : 0.0);
    }

    // The cheapest route from the hub out of the ego box onto the bus. It is one ray to the box,
    // then along the box to the side that faces the strip. Every leg is tested. Octilinear rays keep
    // the metro look in the tree modes, where any crossing rules a route out. Spring draws
    // straight edges at any angle round a dense core, so it tries 32 angles and pays an edge or
    // a label crossing with a long detour, while a dot stays ruled out. There the route may also
    // leave the core along one angle for part of the way and then run straight to the strip, so
    // it can take the free gap between two spokes without crossing the rest of the map.
    private static List<LayoutPoint> Trunk(IReadOnlyList<LayoutNode> nodes, LayoutNode hub, Box box, bool below, double bus, double g, bool anyAngle, double s, double zFit)
    {
        var start = new LayoutPoint(hub.SnappedX, hub.SnappedY);
        List<LayoutPoint>? best = null;
        double bestCost = double.PositiveInfinity;
        double crossing = anyAngle ? 12.0 * g : 1e6;
        void Consider(List<LayoutPoint> route)
        {
            int dots = 0;
            int others = 0;
            for (int leg = 0; leg + 1 < route.Count; leg++)
            {
                Crossings c = Hits(nodes, leg == 0 ? hub : null, route[leg], route[leg + 1], null, s, zFit);
                dots += c.Dots;
                others += c.Labels + c.Edges;
            }
            double cost = dots * 1e6 + others * crossing + Length(route) + (route.Count - 2) * 4.0 * g;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = route;
            }
        }

        int count = anyAngle ? 32 : 8;
        for (int d = 0; d < count; d++)
        {
            double angle = 2.0 * Math.PI * d / count;
            double dx = Math.Round(Math.Cos(angle), 9);
            double dy = Math.Round(Math.Sin(angle), 9);
            if (!anyAngle)
            {
                dx = Math.Sign(dx);
                dy = Math.Sign(dy);
            }
            LayoutPoint exit = RayToBox(start, dx, dy, box);
            foreach (List<LayoutPoint> route in Around(start, exit, box, below, bus))
                Consider(route);
            if (!anyAngle)
                continue;
            for (int k = 1; k <= SpringElbows; k++)
            {
                double t = k / (SpringElbows + 1.0);
                var elbow = new LayoutPoint(start.X + (exit.X - start.X) * t, start.Y + (exit.Y - start.Y) * t);
                Consider(below
                    ? new List<LayoutPoint> { start, elbow, new(elbow.X, box.Y1), new(elbow.X, bus) }
                    : new List<LayoutPoint> { start, elbow, new(box.X1, elbow.Y), new(bus, elbow.Y) });
            }
        }
        return Simplify(best!);
    }

    // How many places along each Spring ray the trunk may bend toward the strip.
    private const int SpringElbows = 4;

    private static IEnumerable<List<LayoutPoint>> Around(LayoutPoint s, LayoutPoint p, Box b, bool below, double bus)
    {
        if (below)
        {
            if (p.Y >= b.Y1)
            {
                yield return new List<LayoutPoint> { s, p, new(p.X, bus) };
                yield break;
            }
            if (p.X <= b.X0)
            {
                yield return new List<LayoutPoint> { s, p, new(b.X0, bus) };
                yield break;
            }
            if (p.X >= b.X1)
            {
                yield return new List<LayoutPoint> { s, p, new(b.X1, bus) };
                yield break;
            }
            yield return new List<LayoutPoint> { s, p, new(b.X1, b.Y0), new(b.X1, bus) };
            yield return new List<LayoutPoint> { s, p, new(b.X0, b.Y0), new(b.X0, bus) };
        }
        else
        {
            if (p.X >= b.X1)
            {
                yield return new List<LayoutPoint> { s, p, new(bus, p.Y) };
                yield break;
            }
            if (p.Y <= b.Y0)
            {
                yield return new List<LayoutPoint> { s, p, new(bus, b.Y0) };
                yield break;
            }
            if (p.Y >= b.Y1)
            {
                yield return new List<LayoutPoint> { s, p, new(bus, b.Y1) };
                yield break;
            }
            yield return new List<LayoutPoint> { s, p, new(b.X0, b.Y0), new(bus, b.Y0) };
            yield return new List<LayoutPoint> { s, p, new(b.X0, b.Y1), new(bus, b.Y1) };
        }
    }

    // Where a part's branch enters it, which is the end of the first ray from the root (facing
    // the bus first) that crosses none of the part's own dots, edges, labels or its title.
    private static LayoutPoint Entry(LayoutPart part, bool below, double inflate, double s, double zFit, double g)
    {
        var box = new Box(
            Math.Floor((part.MinX - inflate) / g) * g,
            Math.Floor((part.MinY - inflate) / g) * g,
            Math.Ceiling((part.MaxX + inflate) / g) * g,
            Math.Ceiling((part.MaxY + inflate) / g) * g);
        LayoutNode root = part.Root;
        var start = new LayoutPoint(root.SnappedX, root.SnappedY);
        RootBoxPx(root, root.IsSystemStub, out _, out double ty0, out double tx1, out double ty1);
        double titleX = root.SnappedX + (GlyphRadiusPx(root) + TitleGapPx - TitlePadX) * s;
        var title = new Box(titleX, root.SnappedY + ty0 * s, root.SnappedX + tx1 * s, root.SnappedY + ty1 * s);
        (int X, int Y)[] order = below ? BelowEntries : RightEntries;
        for (int i = 0; i < order.Length; i++)
        {
            LayoutPoint end = RayToBox(start, order[i].X, order[i].Y, box);
            if (Hits(part.Result.Tree.Nodes, root, start, end, title, s, zFit).Total == 0)
                return end;
        }
        return RayToBox(start, order[0].X, order[0].Y, box);
    }

    private static readonly (int X, int Y)[] BelowEntries = { (0, -1), (-1, -1), (1, -1), (-1, 0), (1, 0) };
    private static readonly (int X, int Y)[] RightEntries = { (-1, 0), (-1, -1), (-1, 1), (0, -1), (0, 1) };

    private static LayoutPoint RayToBox(LayoutPoint s, double dx, double dy, Box b)
    {
        double tx = dx > 0 ? (b.X1 - s.X) / dx : dx < 0 ? (b.X0 - s.X) / dx : double.PositiveInfinity;
        double ty = dy > 0 ? (b.Y1 - s.Y) / dy : dy < 0 ? (b.Y0 - s.Y) / dy : double.PositiveInfinity;
        double t = Math.Min(tx, ty);
        return new LayoutPoint(s.X + dx * t, s.Y + dy * t);
    }

    private readonly record struct Crossings(int Dots, int Labels, int Edges)
    {
        public int Total => Dots + Labels + Edges;
    }

    // What the segment from a to e passes closer than LineClearPx screen pixels to, at the
    // lowest fit zoom (s layout units per pixel). It counts the dots (with the extra box), the
    // label boxes as the fit shows them (and as zoom 1 shows them), and the edge segments. When the
    // segment leaves the dot of from, the stretch inside that dot and its clearance is skipped,
    // so the edges that leave that node do not count.
    private static Crossings Hits(IReadOnlyList<LayoutNode> nodes, LayoutNode? from, LayoutPoint a, LayoutPoint e, Box? extra, double s, double zFit)
    {
        if (from != null)
        {
            double len = Math.Sqrt((e.X - a.X) * (e.X - a.X) + (e.Y - a.Y) * (e.Y - a.Y));
            double skip = (DotReachPx(from) + 2.0 * LineClearPx) * s;
            if (len <= skip)
                return default;
            a = new LayoutPoint(a.X + (e.X - a.X) / len * skip, a.Y + (e.Y - a.Y) / len * skip);
        }
        double clearance = LineClearPx * s;
        int dots = 0;
        int labels = 0;
        int edges = 0;
        if (extra is Box box && SegmentHitsRect(a, e, box.X0, box.Y0, box.X1, box.Y1))
            dots++;
        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode node = nodes[i];
            if (node != from && SegmentPointDistance(a, e, new LayoutPoint(node.SnappedX, node.SnappedY)) < DotReachPx(node) * s + clearance)
                dots++;
            if (node.LabelPlaced && !node.IsSystemRoot)
            {
                bool fit = LabelLod.RankShows(node, zFit);
                double w = Math.Max(node.Width, fit ? node.ShortLabelTextW * s : 0.0);
                double h = Math.Max(node.Height, fit ? TitleLinePx * s : 0.0);
                if (SegmentHitsRect(a, e, node.LabelX - clearance, node.LabelY - clearance, node.LabelX + w + clearance, node.LabelY + h + clearance))
                    labels++;
            }
            for (int k = 0; k < node.Out.Count; k++)
            {
                IReadOnlyList<LayoutPoint> line = node.Out[k].Polyline;
                for (int p = 0; p + 1 < line.Count; p++)
                {
                    if (SegmentSegmentDistance(a, e, line[p], line[p + 1]) < clearance)
                        edges++;
                }
            }
        }
        return new Crossings(dots, labels, edges);
    }

    public static double SegmentPointDistance(LayoutPoint a, LayoutPoint b, LayoutPoint p)
    {
        double vx = b.X - a.X;
        double vy = b.Y - a.Y;
        double l2 = vx * vx + vy * vy;
        double t = l2 > 0.0 ? Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / l2, 0.0, 1.0) : 0.0;
        double qx = a.X + t * vx - p.X;
        double qy = a.Y + t * vy - p.Y;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    public static double SegmentSegmentDistance(LayoutPoint a, LayoutPoint b, LayoutPoint c, LayoutPoint d)
    {
        double d1 = Cross(a, b, c);
        double d2 = Cross(a, b, d);
        double d3 = Cross(c, d, a);
        double d4 = Cross(c, d, b);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return 0.0;
        return Math.Min(
            Math.Min(SegmentPointDistance(a, b, c), SegmentPointDistance(a, b, d)),
            Math.Min(SegmentPointDistance(c, d, a), SegmentPointDistance(c, d, b)));
    }

    // Whether the segment a-b touches the rectangle, with an end inside it or a crossing of a
    // side.
    public static bool SegmentHitsRect(LayoutPoint a, LayoutPoint b, double x0, double y0, double x1, double y1)
    {
        if (Inside(a, x0, y0, x1, y1) || Inside(b, x0, y0, x1, y1))
            return true;
        var p00 = new LayoutPoint(x0, y0);
        var p10 = new LayoutPoint(x1, y0);
        var p11 = new LayoutPoint(x1, y1);
        var p01 = new LayoutPoint(x0, y1);
        return SegmentSegmentDistance(a, b, p00, p10) == 0.0
            || SegmentSegmentDistance(a, b, p10, p11) == 0.0
            || SegmentSegmentDistance(a, b, p11, p01) == 0.0
            || SegmentSegmentDistance(a, b, p01, p00) == 0.0;
    }

    private static bool Inside(LayoutPoint p, double x0, double y0, double x1, double y1)
    {
        return p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;
    }

    private static double Cross(LayoutPoint o, LayoutPoint p, LayoutPoint q)
    {
        return (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
    }

    private static double Length(List<LayoutPoint> line)
    {
        double sum = 0.0;
        for (int i = 0; i + 1 < line.Count; i++)
            sum += Math.Abs(line[i + 1].X - line[i].X) + Math.Abs(line[i + 1].Y - line[i].Y);
        return sum;
    }

    // Drop repeated points and merge collinear runs, so the dashes of one straight stretch keep
    // one phase.
    private static List<LayoutPoint> Simplify(List<LayoutPoint> line)
    {
        var result = new List<LayoutPoint>(line.Count);
        foreach (LayoutPoint p in line)
        {
            if (result.Count > 0 && Math.Abs(result[result.Count - 1].X - p.X) < 1e-6 && Math.Abs(result[result.Count - 1].Y - p.Y) < 1e-6)
                continue;
            if (result.Count >= 2)
            {
                LayoutPoint a = result[result.Count - 2];
                LayoutPoint b = result[result.Count - 1];
                double cross = (b.X - a.X) * (p.Y - b.Y) - (b.Y - a.Y) * (p.X - b.X);
                double dot = (b.X - a.X) * (p.X - b.X) + (b.Y - a.Y) * (p.Y - b.Y);
                if (Math.Abs(cross) < 1e-6 && dot > 0)
                {
                    result[result.Count - 1] = p;
                    continue;
                }
            }
            result.Add(p);
        }
        return result;
    }

    private static double Snap(double v, double g) => Math.Round(v / g) * g;

    private static double SnapUp(double v, double g) => Math.Ceiling(v / g - 1e-9) * g;

    private static double SnapDown(double v, double g) => Math.Floor(v / g + 1e-9) * g;

    private static void AddNodes(List<LayoutNode> into, IReadOnlyList<LayoutNode> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            nodes[i].Index = into.Count;
            into.Add(nodes[i]);
        }
    }

    // Move every node of a part, its label box and its edge polylines by (dx, dy). The grid cell
    // moves with it, so the per-cell overlap checks still see the right neighbours.
    private static void Translate(LayoutPart part, double dx, double dy, double g)
    {
        if (dx == 0.0 && dy == 0.0)
            return;
        int dCol = (int)Math.Round(dx / g);
        int dRow = (int)Math.Round(dy / g);
        IReadOnlyList<LayoutNode> nodes = part.Result.Tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            LayoutNode node = nodes[i];
            node.X += dx;
            node.Y += dy;
            node.SnappedX += dx;
            node.SnappedY += dy;
            node.Col += dCol;
            node.Row += dRow;
            node.LabelX += dx;
            node.LabelY += dy;
            for (int e = 0; e < node.Out.Count; e++)
            {
                LayoutEdge edge = node.Out[e];
                IReadOnlyList<LayoutPoint> line = edge.Polyline;
                var moved = new LayoutPoint[line.Count];
                for (int p = 0; p < line.Count; p++)
                    moved[p] = new LayoutPoint(line[p].X + dx, line[p].Y + dy);
                edge.Polyline = moved;
            }
        }
    }
}
