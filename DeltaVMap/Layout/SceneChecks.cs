using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeltaVMap.Layout;

// The rules a composed scene with other star systems must keep, checked at fit zoom in screen
// space the way the canvas draws it (through FitPreview), so the offline tests and the harness
// run on the real universe hold the layout to the same rules. Every check returns the broken
// rules as messages, and an empty list when the scene keeps them all.
internal static class SceneChecks
{
    // The screen clearance a connector keeps from a dot, an edge or a label, the empty band
    // between the home map and the strip, and the two canvas overlays (the layout button at the
    // top left, the collapsed transfer-window footer at the bottom left).
    public const double LineClearPx = 6.0;
    public const double MinBandPx = 120.0;
    private const double ButtonZonePx = 76.0;
    private const double FooterW = 480.0;
    private const double FooterH = 56.0;

    // The plate of the route badge at a connector's badge anchor: two text lines about 110 px wide.
    private const double BadgeW = 116.0;
    private const double BadgeH = 2.0 * FitPreview.LinePx + 2.0;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The strip geometry: parts in whole grid steps with their roots flagged as system roots,
    // every part past the channel, the roots of a row on one line, no root on the ego hub's line.
    public static List<string> Strip(LayoutScene scene)
    {
        var fails = new List<string>();
        double g = scene.Config.GridPx;
        bool below = scene.Side == StripSide.Below;
        LayoutResult ego = scene.Ego;
        LayoutNode? hub = scene.EgoHub;
        if (hub == null || !hub.IsSystemRoot)
            fails.Add("the ego hub is no system root");
        var lines = new HashSet<double>();
        foreach (LayoutPart part in scene.Parts)
        {
            if (!IsWhole(part.AppliedDx / g) || !IsWhole(part.AppliedDy / g))
                fails.Add($"part {part.RootId} moved off the grid");
            if (!part.Root.IsSystemRoot)
                fails.Add($"part {part.RootId} has no system root");
            double near = below ? part.MinY : part.MinX;
            double egoEdge = below ? ego.MaxY : ego.MaxX;
            if (!(near > scene.Bus && scene.Bus > egoEdge))
                fails.Add($"part {part.RootId} does not lie past the bus");
            if (hub != null && (below ? part.Root.SnappedY == hub.SnappedY : part.Root.SnappedX == hub.SnappedX))
                fails.Add($"part {part.RootId} has its root on the ego hub's line");
            if (!part.Expanded)
                lines.Add(below ? part.Root.SnappedY : part.Root.SnappedX);
        }
        if (lines.Count > 1 && !Wrapped(scene))
            fails.Add($"the collapsed roots of the strip lie on {lines.Count} lines");
        return fails;
    }

    private static bool Wrapped(LayoutScene scene)
    {
        return scene.Network.Count > 2;
    }

    // The connectors at fit zoom: the shared network keeps clear of the home map (in Spring it
    // may cross two ego edges and labels, but no dot), each branch keeps clear of its own part,
    // no system title covers a connector, the badge anchors and the break mark lie on the lines
    // with 40 px of line on each side of the mark, each route badge has a spot that covers no
    // title, label or dot, and unless overlays is false nothing of the strip lies under the
    // canvas overlays.
    public static List<string> Connectors(LayoutScene scene, double canvasW, double canvasH, bool overlays = true)
    {
        var fails = new List<string>();
        if (scene.Parts.Count == 0)
            return fails;
        FitPreview fit = FitPreview.Build(scene, canvasW, canvasH);
        string at = string.Create(Inv, $"at {canvasW}x{canvasH}");
        int egoCount = scene.Ego.Tree.Nodes.Count;
        bool spring = scene.Config.Mode == LayoutMode.Spring;
        LayoutNode hub = scene.EgoHub!;

        // The shared network against the ego graph.
        int edgeHits = 0;
        var blocks = new List<string>();
        foreach (IReadOnlyList<LayoutPoint> line in scene.Network)
            ScanLine(fit, line, null, scene.Nodes, 0, egoCount, hub, fit.Texts, blocks, ref edgeHits);
        if (spring)
            blocks.RemoveAll(b => b.StartsWith("the label of ", StringComparison.Ordinal));
        if (blocks.Count > 0)
            fails.Add($"{at}: the trunk or a bus touches {string.Join(", ", blocks)}");
        if (spring ? edgeHits > 2 : edgeHits > 0)
            fails.Add($"{at}: the trunk or a bus crosses {edgeHits} ego edges");

        // Each branch against its own part.
        foreach (LayoutConnector connector in scene.Connectors)
        {
            int partFirst = connector.To.Index;
            int partEnd = partFirst + PartOf(scene, connector.To).Result.Tree.Nodes.Count;
            var branchBlocks = new List<string>();
            int branchEdges = 0;
            ScanLine(fit, connector.Polyline, connector, scene.Nodes, partFirst, partEnd, connector.To, fit.Texts, branchBlocks, ref branchEdges);
            if (branchBlocks.Count > 0 || branchEdges > 0)
                fails.Add($"{at}: the branch to {connector.To.Id} touches its own part ({string.Join(", ", branchBlocks)}, {branchEdges} edges)");
        }

        // Titles against every connector line, and the overlays.
        foreach (PreviewText text in fit.Texts)
        {
            if (!text.Title)
                continue;
            PreviewRect r = text.Rect;
            if (Hits(fit, scene, r))
                fails.Add($"{at}: the title of {text.Node.Id} covers a connector");
            bool isEgo = text.Node.Index < egoCount;
            if (overlays && !isEgo && (r.Intersects(new PreviewRect(0, 0, ButtonZonePx, ButtonZonePx)) || r.Intersects(new PreviewRect(0, canvasH - FooterH, FooterW, canvasH))))
                fails.Add($"{at}: the title of {text.Node.Id} lies under a canvas overlay");
        }
        foreach (LayoutPart part in scene.Parts)
        {
            (double x, double y) = fit.ToScreen(part.Root.SnappedX, part.Root.SnappedY);
            if (overlays && ((x <= ButtonZonePx && y <= ButtonZonePx) || (x <= FooterW && y >= canvasH - FooterH)))
                fails.Add($"{at}: the root of {part.RootId} lies under a canvas overlay");
        }
        if (overlays && Hits(fit, scene, new PreviewRect(0, 0, ButtonZonePx, ButtonZonePx)))
            fails.Add($"{at}: a connector runs under the layout button");

        // Badge anchors and the route badge.
        foreach (LayoutConnector connector in scene.Connectors)
        {
            bool onLine = false;
            for (int i = 0; i + 1 < connector.Polyline.Count; i++)
                onLine |= SceneComposer.SegmentPointDistance(connector.Polyline[i], connector.Polyline[i + 1], connector.BadgeAnchor) < 1e-6;
            if (!onLine)
                fails.Add($"the badge anchor of {connector.To.Id} is off its line");
            (double ax, double ay) = fit.ToScreen(connector.BadgeAnchor.X, connector.BadgeAnchor.Y);
            bool free = false;
            for (int s = 0; s < SceneComposer.BadgeSpotCount && !free; s++)
            {
                (double bx, double by) = SceneComposer.BadgeSpot(s, ax, ay, BadgeW, BadgeH);
                var badge = new PreviewRect(bx, by, bx + BadgeW, by + BadgeH);
                free = badge.X0 >= 0 && badge.Y0 >= 0 && badge.X1 <= canvasW && badge.Y1 <= canvasH;
                foreach (PreviewText text in fit.Texts)
                    free &= !badge.Intersects(text.Rect);
                foreach (LayoutNode node in scene.Nodes)
                    free &= !badge.Intersects(DotRect(fit, node));
            }
            if (!free)
                fails.Add($"{at}: the route badge of {connector.To.Id} has no free spot");
        }

        // The break mark on the trunk, oriented like its segment, with line on both sides along
        // the connector it starts.
        if (!scene.HasBreakMark)
        {
            fails.Add("the trunk has no break mark");
        }
        else
        {
            IReadOnlyList<LayoutPoint> trunk = scene.Connectors[0].Polyline;
            double before = 0.0;
            double total = 0.0;
            bool found = false;
            for (int i = 0; i + 1 < trunk.Count; i++)
            {
                (double ax, double ay) = fit.ToScreen(trunk[i].X, trunk[i].Y);
                (double bx, double by) = fit.ToScreen(trunk[i + 1].X, trunk[i + 1].Y);
                bool vertical = Math.Abs(trunk[i + 1].X - trunk[i].X) < Math.Abs(trunk[i + 1].Y - trunk[i].Y);
                if (!found && vertical == scene.BreakMarkVertical && SceneComposer.SegmentPointDistance(trunk[i], trunk[i + 1], scene.BreakMark) < 1e-6)
                {
                    (double mx, double my) = fit.ToScreen(scene.BreakMark.X, scene.BreakMark.Y);
                    before = total + Math.Sqrt((mx - ax) * (mx - ax) + (my - ay) * (my - ay));
                    found = true;
                }
                total += Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            }
            if (!found)
                fails.Add("the break mark lies on no connector segment of its orientation");
            else if (before < 40.0 || total - before < 40.0)
                fails.Add(string.Create(Inv, $"{at}: the break mark has {before:F0} / {total - before:F0} px of line around it, under 40"));
        }
        return fails;
    }

    // The fit-zoom look: the empty band between the home map and the strip, every system title
    // drawn inside the canvas, nothing of one part (dot or label) on another system's glyph or
    // title, and no label on the glyph or title of its own system root.
    public static List<string> Fit(LayoutScene scene, double canvasW, double canvasH, out double bandPx)
    {
        var fails = new List<string>();
        bandPx = double.PositiveInfinity;
        if (scene.Parts.Count == 0)
            return fails;
        FitPreview fit = FitPreview.Build(scene, canvasW, canvasH);
        string at = string.Create(Inv, $"at {canvasW}x{canvasH}");
        bool below = scene.Side == StripSide.Below;
        int egoCount = scene.Ego.Tree.Nodes.Count;
        int[] owner = Owners(scene);

        double egoFar = double.NegativeInfinity;
        double partNear = double.PositiveInfinity;
        foreach (LayoutNode node in scene.Nodes)
        {
            PreviewRect dot = DotRect(fit, node);
            if (node.Index < egoCount)
                egoFar = Math.Max(egoFar, below ? dot.Y1 : dot.X1);
            else
                partNear = Math.Min(partNear, below ? dot.Y0 : dot.X0);
        }
        int titles = 0;
        foreach (PreviewText text in fit.Texts)
        {
            bool isEgo = text.Node.Index < egoCount;
            if (isEgo)
                egoFar = Math.Max(egoFar, below ? text.Rect.Y1 : text.Rect.X1);
            else
                partNear = Math.Min(partNear, below ? text.Rect.Y0 : text.Rect.X0);
            if (!text.Title || isEgo)
                continue;
            titles++;
            PreviewRect r = text.Rect;
            if (r.X0 < 0 || r.Y0 < 0 || r.X1 > canvasW || r.Y1 > canvasH)
                fails.Add($"{at}: the title of {text.Node.Id} is cut off by the canvas edge");
        }
        bandPx = partNear - egoFar;
        if (bandPx < MinBandPx)
            fails.Add(string.Create(Inv, $"{at}: the band between the home map and the strip is {bandPx:F0} px, under {MinBandPx:F0}"));
        if (titles != scene.Parts.Count)
            fails.Add($"{at}: {titles} of {scene.Parts.Count} system titles are drawn");

        // Each system root's glyph and title belong to its system alone.
        foreach (LayoutNode root in scene.Nodes)
        {
            if (!root.IsSystemRoot || root.Index < egoCount)
                continue;
            PreviewRect glyph = DotRect(fit, root);
            PreviewRect? title = null;
            foreach (PreviewText text in fit.Texts)
            {
                if (text.Title && text.Node == root)
                    title = text.Rect;
            }
            foreach (LayoutNode node in scene.Nodes)
            {
                if (owner[node.Index] == owner[root.Index])
                    continue;
                PreviewRect dot = DotRect(fit, node);
                if (dot.Intersects(glyph) || (title is PreviewRect t && dot.Intersects(t)))
                    fails.Add($"{at}: the dot of {node.Id} covers the root or title of {root.Id}");
            }
            foreach (PreviewText text in fit.Texts)
            {
                if (text.Title)
                    continue;
                if (text.Rect.Intersects(glyph))
                    fails.Add($"{at}: the label of {text.Node.Id} covers the root of {root.Id}");
            }
        }
        return fails;
    }

    // The places that must hold when one system opens, taken before it opens: the parts are
    // moved in place, so the positions have to be copied.
    public sealed class Places
    {
        public required StripSide Side { get; init; }
        public required double Bus { get; init; }
        public required string Trunk { get; init; }
        public required Dictionary<string, (double X, double Y)> Roots { get; init; }
    }

    public static Places Take(LayoutScene scene)
    {
        var roots = new Dictionary<string, (double X, double Y)>();
        foreach (LayoutPart part in scene.Parts)
            roots[part.RootId] = (part.Root.SnappedX, part.Root.SnappedY);
        return new Places { Side = scene.Side, Bus = scene.Bus, Trunk = Points(scene.Trunk), Roots = roots };
    }

    // Opening one system keeps the side, the trunk and the bus, keeps every part nearer the
    // trunk where it was, and keeps every collapsed root on its line. shiftPx reports how far
    // the opened root moved along and across the strip, in layout units.
    public static List<string> Stability(Places before, LayoutScene opened, string openedRootId, out double alongShift, out double acrossShift)
    {
        var fails = new List<string>();
        alongShift = 0.0;
        acrossShift = 0.0;
        if (opened.Side != before.Side || opened.Bus != before.Bus || Points(opened.Trunk) != before.Trunk)
            fails.Add("opening a system moves the side, the bus or the trunk");
        bool below = opened.Side == StripSide.Below;
        bool nearer = true;
        foreach (LayoutPart part in opened.Parts)
        {
            if (!before.Roots.TryGetValue(part.RootId, out (double X, double Y) was))
                continue;
            double dAlong = below ? part.Root.SnappedX - was.X : part.Root.SnappedY - was.Y;
            double dAcross = below ? part.Root.SnappedY - was.Y : part.Root.SnappedX - was.X;
            if (part.RootId == openedRootId)
            {
                alongShift = dAlong;
                acrossShift = dAcross;
                nearer = false;
                continue;
            }
            if (nearer && (dAlong != 0.0 || dAcross != 0.0))
                fails.Add($"opening {openedRootId} moves {part.RootId}, which lies nearer the trunk");
            if (dAcross != 0.0)
                fails.Add($"opening {openedRootId} moves {part.RootId} off the root line");
        }
        return fails;
    }

    // Walk a polyline in screen space and collect what it passes within LineClearPx of: the dots
    // and drawn labels of the nodes [first, end) (except near the node the line starts or ends
    // at), and their edges as edge hits. A connector is walked from its badge anchor on.
    private static void ScanLine(FitPreview fit, IReadOnlyList<LayoutPoint> line, LayoutConnector? branchOf, IReadOnlyList<LayoutNode> nodes,
        int first, int end, LayoutNode skip, List<PreviewText> texts, List<string> blocks, ref int edgeHits)
    {
        var pts = new List<(double X, double Y)>();
        if (branchOf != null)
        {
            pts.Add(fit.ToScreen(branchOf.BadgeAnchor.X, branchOf.BadgeAnchor.Y));
            for (int i = branchOf.BranchStart; i < line.Count; i++)
                pts.Add(fit.ToScreen(line[i].X, line[i].Y));
        }
        else
        {
            foreach (LayoutPoint p in line)
                pts.Add(fit.ToScreen(p.X, p.Y));
        }
        (double sx, double sy) = fit.ToScreen(skip.SnappedX, skip.SnappedY);
        double skipR = SceneComposer.GlyphRadiusPx(skip) + SceneComposer.SystemRingPx + (skip.IsRoot ? 10.0 : 0.0) + 2.0 * LineClearPx;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            LayoutPoint a = new(pts[i].X, pts[i].Y);
            LayoutPoint b = new(pts[i + 1].X, pts[i + 1].Y);
            if (!Trim(ref a, ref b, new LayoutPoint(sx, sy), skipR))
                continue;
            for (int n = first; n < end; n++)
            {
                LayoutNode node = nodes[n];
                (double x, double y) = fit.ToScreen(node.SnappedX, node.SnappedY);
                double r = SceneComposer.GlyphRadiusPx(node) + (node.IsSystemRoot ? SceneComposer.SystemRingPx : 0.0);
                if (node != skip && SceneComposer.SegmentPointDistance(a, b, new LayoutPoint(x, y)) < r + LineClearPx)
                    blocks.Add("the dot of " + node.Id);
                foreach (LayoutEdge edge in node.Out)
                {
                    for (int p = 0; p + 1 < edge.Polyline.Count; p++)
                    {
                        (double ex0, double ey0) = fit.ToScreen(edge.Polyline[p].X, edge.Polyline[p].Y);
                        (double ex1, double ey1) = fit.ToScreen(edge.Polyline[p + 1].X, edge.Polyline[p + 1].Y);
                        if (SceneComposer.SegmentSegmentDistance(a, b, new LayoutPoint(ex0, ey0), new LayoutPoint(ex1, ey1)) < LineClearPx)
                            edgeHits++;
                    }
                }
            }
            foreach (PreviewText text in texts)
            {
                int idx = text.Node.Index;
                if (text.Title || idx < first || idx >= end)
                    continue;
                PreviewRect r = text.Rect;
                if (SceneComposer.SegmentHitsRect(a, b, r.X0 - LineClearPx, r.Y0 - LineClearPx, r.X1 + LineClearPx, r.Y1 + LineClearPx))
                    blocks.Add("the label of " + text.Node.Id);
            }
        }
    }

    private static bool Hits(FitPreview fit, LayoutScene scene, PreviewRect r)
    {
        double originX = fit.PanX - fit.MinX * fit.Zoom;
        double originY = fit.PanY - fit.MinY * fit.Zoom;
        return SceneComposer.ConnectorsHitScreenRect(scene, fit.Zoom, originX, originY, r.X0, r.Y0, r.X1, r.Y1);
    }

    public static PreviewRect DotRect(FitPreview fit, LayoutNode node)
    {
        (double x, double y) = fit.ToScreen(node.SnappedX, node.SnappedY);
        double r = SceneComposer.GlyphRadiusPx(node);
        return new PreviewRect(x - r, y - r, x + r, y + r);
    }

    // Which system each scene node belongs to: 0 for the ego map, i + 1 for part i.
    private static int[] Owners(LayoutScene scene)
    {
        var owner = new int[scene.Nodes.Count];
        for (int p = 0; p < scene.Parts.Count; p++)
        {
            foreach (LayoutNode node in scene.Parts[p].Result.Tree.Nodes)
                owner[node.Index] = p + 1;
        }
        return owner;
    }

    private static LayoutPart PartOf(LayoutScene scene, LayoutNode root)
    {
        foreach (LayoutPart part in scene.Parts)
        {
            if (part.Root == root)
                return part;
        }
        throw new InvalidOperationException($"No part has the root {root.Id}.");
    }

    // Cut the part of a segment that lies within r of c off its ends; false when nothing is left.
    private static bool Trim(ref LayoutPoint a, ref LayoutPoint b, LayoutPoint c, double r)
    {
        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        if (len < 1e-9)
            return false;
        double ux = (b.X - a.X) / len;
        double uy = (b.Y - a.Y) / len;
        double da = Math.Sqrt((a.X - c.X) * (a.X - c.X) + (a.Y - c.Y) * (a.Y - c.Y));
        double db = Math.Sqrt((b.X - c.X) * (b.X - c.X) + (b.Y - c.Y) * (b.Y - c.Y));
        if (da < r)
            a = new LayoutPoint(a.X + ux * (r - da), a.Y + uy * (r - da));
        if (db < r)
            b = new LayoutPoint(b.X - ux * (r - db), b.Y - uy * (r - db));
        return (b.X - a.X) * ux + (b.Y - a.Y) * uy > 1e-6;
    }

    private static bool IsWhole(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 1e-9;
    }

    private static string Points(IReadOnlyList<LayoutPoint> line)
    {
        var sb = new System.Text.StringBuilder();
        foreach (LayoutPoint p in line)
            sb.Append(Inv, $"{p.X},{p.Y} ");
        return sb.ToString();
    }
}
