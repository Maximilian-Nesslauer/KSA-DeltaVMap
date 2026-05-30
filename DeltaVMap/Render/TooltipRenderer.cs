using System;
using System.Globalization;
using Brutal.ImGuiApi;
using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;
using KSA;

namespace DeltaVMap.Render;

// Rich hover tooltips for the map. A node tooltip reports the body's physical and orbital
// properties; an edge tooltip reports the segment, its dV, transfer time and the formula
// behind it. Both open and close their own ImGui tooltip block. All figures carry the same
// leading "~" as the rest of the map (closed-form estimates), and the dV values honour the
// piloting margin so a tooltip agrees with the badge it sits over.
internal static class TooltipRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Body and orbit facts for a hovered node. ladder is the graph's cached ladder for the
    // body (mu, radii, SOI); it may be null for a node whose body is not in the graph, in
    // which case only the always-available facts are shown.
    public static void Node(StateNode node, BodyLadder? ladder)
    {
        Astronomical body = node.Body;

        ImGui.BeginTooltip();
        ImGui.Text(node.Label);
        ImGui.Separator();

        ImGui.TextDisabled("Class: " + body.Class);
        ImGui.Text("Mean radius: " + Km(body.MeanRadius));

        if (ladder != null)
        {
            ImGui.Text("Mass: " + Mass(ladder.Body.Mass));
            // The star has an infinite SOI (no finite radius and no parent to escape to);
            // a real finite SOI shows its size; anything else has no bound SOI.
            string soiText = ladder.SoiRadius.HasValue ? Km(ladder.SoiRadius.Value)
                : double.IsInfinity(ladder.Body.SphereOfInfluence) ? "entire system"
                : "n/a";
            ImGui.Text("SOI: " + soiText);
        }

        // Orbital facts only exist for a body that orbits something (not the star).
        if (body is IOrbiter orbiter && orbiter.Orbit != null)
        {
            // An open orbit (a comet, e >= 1) has no period (the game returns NaN), so report
            // that rather than formatting the NaN; the eccentricity line shows it is open.
            double period = orbiter.Orbit.Period;
            ImGui.Text("Orbital period: " + (double.IsFinite(period) ? Duration(period) : "n/a (open orbit)"));
            ImGui.Text("Eccentricity: " + orbiter.Orbit.Eccentricity.ToString("0.000", Inv));
        }

        AtmosphereReference? atmosphere = body.GetAtmosphereReference();
        if (atmosphere == null)
        {
            ImGui.TextDisabled("No atmosphere");
        }
        else
        {
            double density = atmosphere.Physical.SeaLevelDensity;
            ImGui.Text("Atmosphere: " + string.Format(Inv, "{0:0.###} kg/m3 at surface", density));
            // Use the density just read rather than re-fetching the atmosphere; the floor is
            // the one shared constant the jet/aerobrake/descent paths all gate on.
            bool usable = density > DeltaVCalculator.UsableAtmosphereDensity;
            ImGui.TextDisabled(usable ? "Aerobraking / jet possible" : "Too thin to aerobrake");
        }

        ImGui.EndTooltip();
    }

    // Segment, dV, transfer time and formula for a hovered edge. game carries the fine
    // segment kind and the transfer time; layout carries the display dV (the coupled
    // Oberth burn for a transfer, the exact ladder cost otherwise) and the descent dV.
    public static void Edge(Edge game, LayoutEdge layout, double dvScale, bool showTime)
    {
        ImGui.BeginTooltip();
        ImGui.Text(SegmentTitle(game.Kind));
        ImGui.Separator();

        // An Ascent edge on an atmospheric body is cheaper to descend than to climb, so show
        // both directions; everything else is one figure.
        bool dual = game.Kind == SegmentKind.Ascent && layout.DescentDv > 1.0
            && Math.Abs(layout.DescentDv - layout.RouteDv) > 1.0;
        if (dual)
        {
            ImGui.Text("Ascent: " + Dv(layout.RouteDv * dvScale));
            ImGui.Text("Descent: " + Dv(layout.DescentDv * dvScale));
        }
        else
        {
            ImGui.Text("Delta-v: " + Dv(layout.RouteDv * dvScale));
        }

        if (showTime && game.TransferTimeSeconds > 0.0)
            ImGui.Text("Transfer time: " + Duration(game.TransferTimeSeconds));

        if (layout.PlaneChangeDv > 1.0)
            ImGui.TextDisabled("Plane change available: " + Dv(layout.PlaneChangeDv * dvScale));

        ImGui.TextDisabled("Formula: " + Formula(game.Kind, game.IsApproximate));
        ImGui.EndTooltip();
    }

    private static string SegmentTitle(SegmentKind kind)
    {
        return kind switch
        {
            SegmentKind.Ascent => "Ascent / descent",
            SegmentKind.Raise => "Orbit change",
            SegmentKind.Land => "Landing",
            SegmentKind.Capture => "Capture (circularize)",
            SegmentKind.Transfer => "Transfer",
            SegmentKind.HubLink => "Structural link",
            _ => kind.ToString()
        };
    }

    private static string Formula(SegmentKind kind, bool approximate)
    {
        return kind switch
        {
            SegmentKind.Ascent => "two-burn Hohmann ascent (with atmospheric loss); drag-braked descent",
            SegmentKind.Raise => "Hohmann between circular orbits",
            SegmentKind.Land => "kill near-surface circular speed",
            SegmentKind.Capture => "circularize from the SOI-edge ellipse",
            // A transfer to a comet matches its open-orbit speed at perihelion, not a Hohmann.
            SegmentKind.Transfer => approximate
                ? "perihelion velocity match with Oberth capture (approximate)"
                : "Hohmann transfer with Oberth ejection and capture",
            SegmentKind.HubLink => "structural connector, no delta-v",
            _ => "closed-form patched-conic estimate"
        };
    }

    private static string Dv(double dv)
    {
        return "~" + Math.Round(dv).ToString("#,##0", Inv) + " m/s";
    }

    private static string Km(double meters)
    {
        return (meters / 1000.0).ToString("#,##0", Inv) + " km";
    }

    private static string Mass(double kg)
    {
        return kg.ToString("0.###e0", Inv) + " kg";
    }

    // Auto-scaled duration: minutes, hours, days, then years.
    private static string Duration(double seconds)
    {
        if (seconds < 3600.0)
            return string.Format(Inv, "{0:0} min", seconds / 60.0);
        if (seconds < 86400.0)
            return string.Format(Inv, "{0:0.0} h", seconds / 3600.0);
        double days = seconds / 86400.0;
        if (days < 365.25)
            return string.Format(Inv, "{0:0.0} d", days);
        return string.Format(Inv, "{0:0.00} yr", days / 365.25);
    }
}
