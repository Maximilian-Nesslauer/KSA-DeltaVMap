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
        LayoutConfig cfg = result.Config;

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
        DrawEdges(sb, tree, tx, ty);
        DrawNodes(sb, tree, cfg, tx, ty);
        DrawLabels(sb, tree, tx, ty);

        sb.Append("</svg>\n");
        return sb.ToString();
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

    private static void DrawEdges(StringBuilder sb, LayoutTree tree, double tx, double ty)
    {
        foreach (LayoutNode node in tree.Nodes)
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

    private static void DrawNodes(StringBuilder sb, LayoutTree tree, LayoutConfig cfg, double tx, double ty)
    {
        foreach (LayoutNode node in tree.Nodes)
        {
            double cx = node.SnappedX + tx;
            double cy = node.SnappedY + ty;
            (string fill, string stroke) = NodeColor(node);
            sb.Append(Inv, $"<circle cx=\"{Fmt(cx)}\" cy=\"{Fmt(cy)}\" r=\"{Fmt(node.DotRadius)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"2\"/>\n");

            if (node.IsYouAreHere)
                sb.Append(Inv, $"<circle cx=\"{Fmt(cx)}\" cy=\"{Fmt(cy)}\" r=\"{Fmt(node.DotRadius + 4)}\" fill=\"none\" stroke=\"#ffd23f\" stroke-width=\"2\"/>\n");
        }
    }

    private static void DrawLabels(StringBuilder sb, LayoutTree tree, double tx, double ty)
    {
        foreach (LayoutNode node in tree.Nodes)
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
