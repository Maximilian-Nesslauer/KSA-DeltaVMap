using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeltaVMap.Layout;

// Renders a laid-out tree to an SVG image and to a compact text tree. Both are debug
// aids while no in-game rendering exists yet: the SVG lets the layout be
// eyeballed in any browser, the text tree pins down exact positions for diffing. Pure
// string building, no game or ImGui dependency, so it runs offline against synthetic
// trees as well as in-game against the real system.
internal static class LayoutDumpFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string ToSvg(LayoutResult result)
    {
        LayoutTree tree = result.Tree;

        const double pad = 40.0;
        double tx = pad - result.MinX;
        double ty = pad - result.MinY;
        double w = result.Width + 2 * pad;
        double h = result.Height + 2 * pad;

        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Fmt(w)}\" height=\"{Fmt(h)}\" viewBox=\"0 0 {Fmt(w)} {Fmt(h)}\" font-family=\"monospace\" font-size=\"11\">\n");
        sb.Append(Inv, $"<rect x=\"0\" y=\"0\" width=\"{Fmt(w)}\" height=\"{Fmt(h)}\" fill=\"#11151c\"/>\n");
        sb.Append(Inv, $"<text x=\"8\" y=\"18\" fill=\"#8aa0b4\">{Escape(tree.Name)}  ({tree.Nodes.Count} nodes, {result.Labels.Placed}/{result.Labels.Total} labels)</text>\n");

        DrawBandGuides(sb, result, tx, ty, w);
        DrawEdges(sb, tree.Nodes, tx, ty);
        DrawNodes(sb, tree.Nodes, tx, ty);
        DrawLabels(sb, tree.Nodes, tx, ty);

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    // The whole scene: the ego map, every destination part where SceneComposer put it, and the
    // dashed interstellar connectors (trunk, bus and branches).
    public static string ToSvg(LayoutScene scene)
    {
        const double pad = 40.0;
        double tx = pad - scene.MinX;
        double ty = pad - scene.MinY;
        double w = scene.Width + 2 * pad;
        double h = scene.Height + 2 * pad;

        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Fmt(w)}\" height=\"{Fmt(h)}\" viewBox=\"0 0 {Fmt(w)} {Fmt(h)}\" font-family=\"monospace\" font-size=\"11\">\n");
        sb.Append(Inv, $"<rect x=\"0\" y=\"0\" width=\"{Fmt(w)}\" height=\"{Fmt(h)}\" fill=\"#11151c\"/>\n");
        sb.Append(Inv, $"<text x=\"8\" y=\"18\" fill=\"#8aa0b4\">{Escape(scene.Ego.Tree.Name)}  ({scene.Nodes.Count} nodes, {scene.Parts.Count} other systems)</text>\n");

        foreach (LayoutConnector connector in scene.Connectors)
        {
            var pts = new StringBuilder();
            foreach (LayoutPoint p in connector.Polyline)
                pts.Append(Inv, $"{Fmt(p.X + tx)},{Fmt(p.Y + ty)} ");
            sb.Append(Inv, $"<polyline points=\"{pts.ToString().Trim()}\" fill=\"none\" stroke=\"#be96ff\" stroke-width=\"2.2\" stroke-dasharray=\"7 5\"/>\n");
        }
        DrawEdges(sb, scene.Nodes, tx, ty);
        DrawNodes(sb, scene.Nodes, tx, ty);
        DrawLabels(sb, scene.Nodes, tx, ty);

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    // The ego tree, each destination part and the connector polylines, as text.
    public static string ToText(LayoutScene scene)
    {
        var sb = new StringBuilder();
        sb.Append(ToText(scene.Ego));
        if (scene.Parts.Count > 0)
        {
            sb.Append(Inv, $"Strip {scene.Side}, bus at {Fmt0(scene.Bus)}, trunk");
            foreach (LayoutPoint p in scene.Trunk)
                sb.Append(Inv, $" ({Fmt0(p.X)},{Fmt0(p.Y)})");
            if (scene.HasBreakMark)
                sb.Append(Inv, $", break mark ({Fmt0(scene.BreakMark.X)},{Fmt0(scene.BreakMark.Y)}) {(scene.BreakMarkVertical ? "vertical" : "horizontal")}");
            sb.Append('\n');
        }
        foreach (LayoutPart part in scene.Parts)
        {
            sb.Append(Inv, $"Part '{part.RootId}' ({(part.Expanded ? "expanded" : "collapsed")}) moved by ({Fmt0(part.AppliedDx)},{Fmt0(part.AppliedDy)}):\n");
            AppendTextNode(sb, part.Root, null, 1);
        }
        foreach (LayoutConnector connector in scene.Connectors)
        {
            sb.Append(Inv, $"Connector {connector.From.Id} -> {connector.To.Id}, badge at ({Fmt0(connector.BadgeAnchor.X)},{Fmt0(connector.BadgeAnchor.Y)}):");
            foreach (LayoutPoint p in connector.Polyline)
                sb.Append(Inv, $" ({Fmt0(p.X)},{Fmt0(p.Y)})");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // The scene as the canvas shows it at fit zoom on a canvas of the given size: positions
    // scaled by the fit zoom, dots and text at a fixed screen size, the system titles and the
    // culled labels of FitPreview, and outlines of the canvas overlays (the layout button top
    // left, the collapsed transfer-window footer bottom left). A zoom-1 dump hides how the map
    // reads at the fit, which is where the systems must read as separate graphs.
    public static string ToFitSvg(LayoutScene scene, double canvasW, double canvasH)
    {
        FitPreview fit = FitPreview.Build(scene, canvasW, canvasH);
        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Fmt(canvasW)}\" height=\"{Fmt(canvasH)}\" viewBox=\"0 0 {Fmt(canvasW)} {Fmt(canvasH)}\" font-family=\"Consolas, monospace\" font-size=\"12.5\">\n");
        sb.Append(Inv, $"<rect width=\"{Fmt(canvasW)}\" height=\"{Fmt(canvasH)}\" fill=\"#11151c\"/>\n");

        foreach (IReadOnlyList<LayoutPoint> line in scene.Network)
            sb.Append(Inv, $"<polyline points=\"{ScreenPoints(fit, line)}\" fill=\"none\" stroke=\"#be96ff\" stroke-width=\"2.2\" stroke-dasharray=\"7 5\"/>\n");
        foreach (LayoutConnector connector in scene.Connectors)
        {
            var branch = new List<LayoutPoint> { connector.BadgeAnchor };
            for (int i = connector.BranchStart; i < connector.Polyline.Count; i++)
                branch.Add(connector.Polyline[i]);
            sb.Append(Inv, $"<polyline points=\"{ScreenPoints(fit, branch)}\" fill=\"none\" stroke=\"#be96ff\" stroke-width=\"2.2\" stroke-dasharray=\"7 5\"/>\n");
        }
        foreach (LayoutNode node in scene.Nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.Polyline.Count < 2)
                    continue;
                string color = edge.IsHubLink ? "#7a8a98" : Hue(edge.To);
                double width = edge.IsHubLink ? 3.5 : edge.IsApproach ? 1.4 : edge.Class == EdgeClass.Transfer ? 2.2 : 1.6;
                sb.Append(Inv, $"<polyline points=\"{ScreenPoints(fit, edge.Polyline)}\" fill=\"none\" stroke=\"{color}\" stroke-opacity=\"0.85\" stroke-width=\"{Fmt(width)}\"/>\n");
            }
        }
        if (scene.HasBreakMark)
        {
            (double mx, double my) = fit.ToScreen(scene.BreakMark.X, scene.BreakMark.Y);
            sb.Append(Inv, $"<rect x=\"{Fmt(mx - 7)}\" y=\"{Fmt(my - 7)}\" width=\"14\" height=\"14\" fill=\"#11151c\"/>\n");
            for (int i = -1; i <= 1; i += 2)
            {
                if (scene.BreakMarkVertical)
                    sb.Append(Inv, $"<line x1=\"{Fmt(mx - 7)}\" y1=\"{Fmt(my + i * 3 + 3)}\" x2=\"{Fmt(mx + 7)}\" y2=\"{Fmt(my + i * 3 - 3)}\" stroke=\"#be96ff\" stroke-width=\"2\"/>\n");
                else
                    sb.Append(Inv, $"<line x1=\"{Fmt(mx + i * 3 - 3)}\" y1=\"{Fmt(my + 7)}\" x2=\"{Fmt(mx + i * 3 + 3)}\" y2=\"{Fmt(my - 7)}\" stroke=\"#be96ff\" stroke-width=\"2\"/>\n");
            }
        }

        foreach (LayoutNode node in scene.Nodes)
        {
            (double x, double y) = fit.ToScreen(node.SnappedX, node.SnappedY);
            double r = node.DotRadius;
            string c = Hue(node);
            if (node.IsRoot)
                sb.Append(Inv, $"<circle cx=\"{Fmt(x)}\" cy=\"{Fmt(y)}\" r=\"{Fmt(r + 10)}\" fill=\"#ffaa78\" fill-opacity=\"0.24\"/>\n");
            if (node.IsSystemRoot && !node.IsRoot)
                sb.Append(Inv, $"<circle cx=\"{Fmt(x)}\" cy=\"{Fmt(y)}\" r=\"{Fmt(SceneComposer.GlyphRadiusPx(node) + SceneComposer.SystemRingPx)}\" fill=\"none\" stroke=\"{c}\" stroke-opacity=\"0.75\" stroke-width=\"1.5\"/>\n");
            if (node.IsSystemStub)
            {
                double h = SceneComposer.StubHalfPx;
                sb.Append(Inv, $"<rect x=\"{Fmt(x - h)}\" y=\"{Fmt(y - h)}\" width=\"{Fmt(2 * h)}\" height=\"{Fmt(2 * h)}\" rx=\"4\" fill=\"#181e28\" stroke=\"{c}\" stroke-width=\"1.5\"/>\n");
                sb.Append(Inv, $"<circle cx=\"{Fmt(x)}\" cy=\"{Fmt(y)}\" r=\"{Fmt(h * 0.45)}\" fill=\"{c}\"/>\n");
                for (int k = -2; k <= 2; k++)
                    sb.Append(Inv, $"<line x1=\"{Fmt(x)}\" y1=\"{Fmt(y + h + 2)}\" x2=\"{Fmt(x + k * 9)}\" y2=\"{Fmt(y + h + 14)}\" stroke=\"{c}\" stroke-opacity=\"0.35\" stroke-width=\"1.2\"/>\n");
                continue;
            }
            if (node.Kind == LayoutKind.Hub || node.Kind == LayoutKind.Surface)
            {
                string fill = node.Kind == LayoutKind.Hub ? "#1c232e" : c;
                sb.Append(Inv, $"<circle cx=\"{Fmt(x)}\" cy=\"{Fmt(y)}\" r=\"{Fmt(r)}\" fill=\"{fill}\" stroke=\"{c}\" stroke-width=\"1.5\"/>\n");
                if (node.Kind == LayoutKind.Hub)
                    sb.Append(Inv, $"<circle cx=\"{Fmt(x)}\" cy=\"{Fmt(y)}\" r=\"{Fmt(r * 0.5)}\" fill=\"{c}\"/>\n");
            }
            else
            {
                sb.Append(Inv, $"<circle cx=\"{Fmt(x)}\" cy=\"{Fmt(y)}\" r=\"{Fmt(r)}\" fill=\"none\" stroke=\"{c}\" stroke-width=\"2\"/>\n");
                sb.Append(Inv, $"<circle cx=\"{Fmt(x)}\" cy=\"{Fmt(y)}\" r=\"{Fmt(Math.Max(2.5, r * 0.3))}\" fill=\"{c}\"/>\n");
            }
        }

        foreach (PreviewText text in fit.Texts)
        {
            PreviewRect r = text.Rect;
            LayoutNode node = text.Node;
            sb.Append(Inv, $"<rect x=\"{Fmt(r.X0)}\" y=\"{Fmt(r.Y0)}\" width=\"{Fmt(r.X1 - r.X0)}\" height=\"{Fmt(r.Y1 - r.Y0)}\" rx=\"2\" fill=\"#10141c\" fill-opacity=\"0.8\"/>\n");
            double tx = r.X0 + 3;
            double ty = r.Y0 + 13.5;
            if (!text.Title)
            {
                string label = text.Short ? FitPreview.ShortText(node) : node.Label;
                sb.Append(Inv, $"<text x=\"{Fmt(tx)}\" y=\"{Fmt(ty)}\" fill=\"#cdd6e0\">{Escape(label)}</text>\n");
                continue;
            }
            string title = node.ShortLabel.Length > 0 ? node.ShortLabel : node.Label;
            int cut = title.IndexOf("  ", StringComparison.Ordinal);
            string name = cut > 0 ? title.Substring(0, cut) : title;
            sb.Append(Inv, $"<text x=\"{Fmt(tx)}\" y=\"{Fmt(ty)}\" xml:space=\"preserve\"><tspan fill=\"#ffffff\" font-weight=\"700\">{Escape(name)}</tspan>");
            if (cut > 0)
                sb.Append(Inv, $"<tspan fill=\"#be96ff\">{Escape(title.Substring(cut))}</tspan>");
            sb.Append("</text>\n");
            if (SceneComposer.ShowsSummary(node))
                sb.Append(Inv, $"<text x=\"{Fmt(tx)}\" y=\"{Fmt(ty + FitPreview.LinePx)}\" fill=\"#8593a3\">{Escape(node.Summary)}</text>\n");
        }

        sb.Append("<rect x=\"0\" y=\"0\" width=\"76\" height=\"76\" fill=\"none\" stroke=\"#3a4654\" stroke-dasharray=\"3 3\"/>\n");
        sb.Append(Inv, $"<rect x=\"0\" y=\"{Fmt(canvasH - 56)}\" width=\"480\" height=\"56\" fill=\"none\" stroke=\"#3a4654\" stroke-dasharray=\"3 3\"/>\n");
        sb.Append(Inv, $"<text x=\"{Fmt(canvasW - 8)}\" y=\"16\" fill=\"#5d6b78\" text-anchor=\"end\">{Escape(scene.Ego.Tree.Name)}  fit zoom {fit.Zoom.ToString("0.000", Inv)}</text>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string ScreenPoints(FitPreview fit, IReadOnlyList<LayoutPoint> line)
    {
        var pts = new StringBuilder();
        foreach (LayoutPoint p in line)
        {
            (double x, double y) = fit.ToScreen(p.X, p.Y);
            pts.Append(Inv, $"{Fmt(x)},{Fmt(y)} ");
        }
        return pts.ToString().TrimEnd();
    }

    // One color per planetary system of the ego map (the subtree right under a hub) and per
    // other star system, in place of the per-body colors of the canvas.
    private static readonly string[] Hues =
    {
        "#4aa3ff", "#e2a23b", "#e0533d", "#7fb069", "#e8a35c", "#e9c46a", "#5ccfd6", "#5b7cfa",
        "#c7a3e0", "#d58fb3", "#9bd0a0", "#d0b48f", "#ff7ac8", "#ff8a5c", "#ffd166", "#c98a5a"
    };

    private static string Hue(LayoutNode node)
    {
        LayoutNode top = node;
        while (top.Parent != null)
            top = top.Parent;
        LayoutNode key = top;
        if (top.IsRoot)
        {
            key = node;
            while (key.Parent != null && key.Parent.Kind != LayoutKind.Hub)
                key = key.Parent;
        }
        uint h = 2166136261;
        foreach (char ch in key.Id)
            h = (h ^ ch) * 16777619;
        return Hues[(int)(h % (uint)Hues.Length)];
    }

    private static void DrawBandGuides(StringBuilder sb, LayoutResult result, double tx, double ty, double w)
    {
        var rows = new SortedSet<int>();
        foreach (LayoutNode node in result.Tree.Nodes)
            rows.Add(node.Row);

        foreach (int row in rows)
        {
            double y = row * result.Config.GridPx + ty;
            sb.Append(Inv, $"<line x1=\"0\" y1=\"{Fmt(y)}\" x2=\"{Fmt(w)}\" y2=\"{Fmt(y)}\" stroke=\"#1d2530\" stroke-width=\"1\"/>\n");
        }
    }

    private static void DrawEdges(StringBuilder sb, IReadOnlyList<LayoutNode> nodes, double tx, double ty)
    {
        foreach (LayoutNode node in nodes)
        {
            foreach (LayoutEdge edge in node.Out)
            {
                if (edge.Polyline.Count < 2)
                    continue;

                string color = edge.IsHubLink ? "#6f7e8c" : edge.Class == EdgeClass.Transfer ? "#4f9fe0" : "#7a8a5a";
                double width = edge.IsHubLink ? 3.5 : edge.Class == EdgeClass.Transfer ? 2.0 : 1.5;

                var pts = new StringBuilder();
                foreach (LayoutPoint p in edge.Polyline)
                    pts.Append(Inv, $"{Fmt(p.X + tx)},{Fmt(p.Y + ty)} ");
                sb.Append(Inv, $"<polyline points=\"{pts.ToString().Trim()}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{Fmt(width)}\"/>\n");

                AppendEdgeBadge(sb, edge, tx, ty);
            }
        }
    }

    // Put the dV value near the middle of the edge's first (metric) segment so the
    // band spacing can be sanity-checked against the numbers.
    private static void AppendEdgeBadge(StringBuilder sb, LayoutEdge edge, double tx, double ty)
    {
        if (edge.IsHubLink || edge.Polyline.Count < 2)
            return;

        LayoutPoint a = edge.Polyline[0];
        LayoutPoint b = edge.Polyline[1];
        double mx = (a.X + b.X) / 2 + tx + 3;
        double my = (a.Y + b.Y) / 2 + ty;
        string prefix = edge.IsApproximate ? "~" : "";
        sb.Append(Inv, $"<text x=\"{Fmt(mx)}\" y=\"{Fmt(my)}\" fill=\"#5d6b78\" font-size=\"9\">{prefix}{Fmt0(edge.Dv)}</text>\n");
    }

    private static void DrawNodes(StringBuilder sb, IReadOnlyList<LayoutNode> nodes, double tx, double ty)
    {
        foreach (LayoutNode node in nodes)
        {
            double cx = node.SnappedX + tx;
            double cy = node.SnappedY + ty;
            (string fill, string stroke) = NodeColor(node);
            sb.Append(Inv, $"<circle cx=\"{Fmt(cx)}\" cy=\"{Fmt(cy)}\" r=\"{Fmt(node.DotRadius)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"2\"/>\n");

            if (node.IsYouAreHere)
                sb.Append(Inv, $"<circle cx=\"{Fmt(cx)}\" cy=\"{Fmt(cy)}\" r=\"{Fmt(node.DotRadius + 4)}\" fill=\"none\" stroke=\"#ffd23f\" stroke-width=\"2\"/>\n");
        }
    }

    private static void DrawLabels(StringBuilder sb, IReadOnlyList<LayoutNode> nodes, double tx, double ty)
    {
        foreach (LayoutNode node in nodes)
        {
            if (!node.LabelPlaced)
                continue;
            double lx = node.LabelX + tx;
            double ly = node.LabelY + ty + node.Height - 4;
            sb.Append(Inv, $"<text x=\"{Fmt(lx)}\" y=\"{Fmt(ly)}\" fill=\"#c8d4de\">{Escape(node.Label)}</text>\n");
        }
    }

    private static (string Fill, string Stroke) NodeColor(LayoutNode node)
    {
        if (node.IsRoot)
            return ("#ff7043", "#ffd2c2");
        if (node.IsYouAreHere)
            return ("#ffd23f", "#fff2c0");
        return node.Kind switch
        {
            LayoutKind.Hub => ("#2b3946", "#9fb2c2"),
            LayoutKind.Surface => ("#8d6e63", "#c9b3aa"),
            LayoutKind.LowOrbit => ("#42a5f5", "#bfe0fb"),
            LayoutKind.Stationary => ("#7e57c2", "#cdbce8"),
            LayoutKind.SoiEdge => ("#26a69a", "#a7ded8"),
            LayoutKind.Intercept => ("#ec407a", "#f8bcd4"),
            _ => ("#90a4ae", "#d6dee3")
        };
    }

    public static string ToText(LayoutResult result)
    {
        var sb = new StringBuilder();
        LayoutTree tree = result.Tree;
        sb.Append(Inv, $"Layout '{tree.Name}': {tree.Nodes.Count} nodes, bounds {Fmt0(result.Width)}x{Fmt0(result.Height)} px, labels {result.Labels.Placed}/{result.Labels.Total} placed\n");
        AppendTextNode(sb, tree.Root, null, 0);
        return sb.ToString();
    }

    private static void AppendTextNode(StringBuilder sb, LayoutNode node, LayoutEdge? incoming, int depth)
    {
        string indent = new string(' ', depth * 2);
        string edge = incoming == null ? "(root)" : DescribeEdge(incoming);
        string here = node.IsYouAreHere ? " <YOU ARE HERE>" : "";
        sb.Append(Inv, $"{indent}{edge} {node.Label} [{node.Kind} b{node.Band} cell({node.Col},{node.Row}) xy({Fmt0(node.SnappedX)},{Fmt0(node.SnappedY)})]{(node.LabelPlaced ? "" : " label:dropped")}{here}\n");
        foreach (LayoutEdge child in node.Out)
            AppendTextNode(sb, child.To, child, depth + 1);
    }

    private static string DescribeEdge(LayoutEdge edge)
    {
        if (edge.IsHubLink)
            return "-[hub]->";
        string prefix = edge.IsApproximate ? "~" : "";
        return string.Create(Inv, $"-[{edge.Class} {prefix}{Fmt0(edge.Dv)}]->");
    }

    private static string Fmt(double v) => v.ToString("0.0", Inv);

    private static string Fmt0(double v) => v.ToString("0", Inv);

    private static string Escape(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
