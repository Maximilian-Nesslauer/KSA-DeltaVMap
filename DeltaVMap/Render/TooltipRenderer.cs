using System;
using System.Collections.Generic;
using System.Globalization;
using Brutal.ImGuiApi;
using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Layout;
using DeltaVMap.Model;
using KSA;

namespace DeltaVMap.Render;

// Rich hover tooltips for the map. A node tooltip reports the body's physical and orbital
// properties; an edge tooltip reports the segment, its dV, transfer time and the formula
// behind it; a connector tooltip reports the distance to another star system. All figures
// carry the same leading "~" as the rest of the map (closed-form estimates), and the dV values
// honour the piloting margin so a tooltip agrees with the badge it sits over.
//
// The lines are built once when the hovered item or a display setting changes, and drawn from
// that cache while the cursor stays, so a held hover allocates nothing.
internal sealed class TooltipRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private enum LineStyle
    {
        Text,
        Disabled,
        Separator
    }

    private readonly List<(string Text, LineStyle Style)> _lines = new();
    private object? _key;
    private double _keyScale;
    private bool _keyTime;

    public void Reset()
    {
        _lines.Clear();
        _key = null;
    }

    // A minor-body group ("+N more") or the group of folded star systems: the label, and a
    // short preview of the collapsed bodies so the user can see what is inside without
    // expanding it. Search / isolate is the way to pull a specific one out.
    public void MinorGroup(StateNode node)
    {
        if (!IsCached(node, 1.0, false))
        {
            IReadOnlyList<PhysicalNode> members = node.GroupMembers ?? Array.Empty<PhysicalNode>();
            // The label already carries the count and the specific noun ("+2892 asteroids"),
            // so the body is just a preview of what is inside.
            Add(node.Label);
            Separator();
            AddDisabled(members.Count > 0 && members[0].IsSystemRoot ? "Star systems, nearest first:" : "Aggregated bodies:");
            const int preview = 6;
            int shown = Math.Min(preview, members.Count);
            for (int i = 0; i < shown; i++)
                AddDisabled("  " + members[i].Astro.Id);
            if (members.Count > shown)
                AddDisabled("  ... and " + (members.Count - shown).ToString(Inv) + " more");
        }
        Show();
    }

    // Body and orbit facts for a hovered node. ladder is the graph's cached ladder for the
    // body (mu, radii, SOI); it may be null for a node whose body is not in the graph, in
    // which case only the always-available facts are shown. showParking adds the parking
    // orbit of a star or barycenter, which a route can end at in a universe with several star
    // systems, so a single-star map leaves it out.
    public void Node(StateNode node, BodyLadder? ladder, bool showParking)
    {
        if (!IsCached(node, 1.0, showParking))
            BuildNode(node, ladder, showParking);
        Show();
    }

    private void BuildNode(StateNode node, BodyLadder? ladder, bool showParking)
    {
        Astronomical body = node.Body;
        Add(node.Label);
        Separator();

        AddDisabled("Class: " + body.Class);
        // A barycenter is an empty point: it has mass (its stars') but no radius at all.
        if (body is Barycenter)
            Add("No surface (center of mass of its stars)");
        else
            Add("Mean radius: " + Format.Km(body.MeanRadius));

        if (ladder != null)
        {
            Add("Mass: " + Format.BodyMass(ladder.Body.Mass));
            // A system root has an infinite SOI: it holds everything up to the midpoint with the
            // next star system. A real finite SOI shows its size; anything else has no bound SOI.
            string soiText = ladder.SoiRadius.HasValue ? Format.Km(ladder.SoiRadius.Value)
                : double.IsInfinity(ladder.Body.SphereOfInfluence) ? "whole star system"
                : "n/a";
            Add("SOI: " + soiText);
            if (showParking && ladder.IsHubOnly)
                Add("Parking orbit: " + Format.Distance(ladder.LowOrbitRadius));
        }

        // Orbital facts only exist for a body that orbits something (not a system root).
        if (body is IOrbiter orbiter && orbiter.Orbit != null)
        {
            // An open orbit (a comet, e >= 1) has no period (the game returns NaN), so report
            // that rather than formatting the NaN; the eccentricity line shows it is open.
            double period = orbiter.Orbit.Period;
            Add("Orbital period: " + (double.IsFinite(period) ? Format.Duration(period) : "n/a (open orbit)"));
            Add("Eccentricity: " + orbiter.Orbit.Eccentricity.ToString("0.000", Inv));
        }

        AtmosphereReference? atmosphere = body.GetAtmosphereReference();
        if (atmosphere == null)
        {
            AddDisabled("No atmosphere");
        }
        else
        {
            double density = atmosphere.Physical.SeaLevelDensity;
            Add("Atmosphere: " + density.ToString("0.###", Inv) + " kg/m3 at surface");
            // Use the density just read rather than re-fetching the atmosphere; the floor is
            // the one shared constant the jet/aerobrake/descent paths all gate on.
            bool usable = density > DeltaVCalculator.UsableAtmosphereDensity;
            AddDisabled(usable ? "Aerobraking / jet possible" : "Too thin to aerobrake");
        }
    }

    // Segment, dV, transfer time and formula for a hovered edge. game carries the fine
    // segment kind and the transfer time; layout carries the display dV (the coupled
    // Oberth burn for a transfer, the exact ladder cost otherwise) and the descent dV.
    public void Edge(Edge game, LayoutEdge layout, double dvScale, bool showTime)
    {
        if (!IsCached(game, dvScale, showTime))
            BuildEdge(game, layout, dvScale, showTime);
        Show();
    }

    private void BuildEdge(Edge game, LayoutEdge layout, double dvScale, bool showTime)
    {
        Add(SegmentTitle(game.Kind));
        Separator();

        if (game.Kind == SegmentKind.Approach)
        {
            Add("Part of the arrival at " + game.To.Body.Id);
            AddDisabled("Its cost is in the interstellar leg of a route.");
            AddDisabled("Formula: " + Formula(game.Kind, game.IsApproximate));
            return;
        }

        // An Ascent edge on an atmospheric body is cheaper to descend than to climb, so show
        // both directions; everything else is one figure.
        bool dual = game.Kind == SegmentKind.Ascent && layout.DescentDv > 1.0
            && Math.Abs(layout.DescentDv - layout.RouteDv) > 1.0;
        if (dual)
        {
            Add("Ascent: " + Format.Dv(layout.RouteDv * dvScale));
            Add("Descent: " + Format.Dv(layout.DescentDv * dvScale));
        }
        else
        {
            Add("Delta-v: " + Format.Dv(layout.RouteDv * dvScale));
        }

        if (showTime && game.TransferTimeSeconds > 0.0)
            Add("Transfer time: " + Format.Duration(game.TransferTimeSeconds));

        if (layout.PlaneChangeDv > 1.0)
            AddDisabled("Plane change available: " + Format.Dv(layout.PlaneChangeDv * dvScale));

        AddDisabled("Formula: " + Formula(game.Kind, game.IsApproximate));
    }

    // The interstellar connector to another star system: the distance between the two roots,
    // and where its cost is shown, since that depends on the cruise speed.
    public void Connector(Edge interstellar)
    {
        if (!IsCached(interstellar, 1.0, false))
        {
            Add(SegmentTitle(SegmentKind.Interstellar) + " to " + interstellar.To.Body.Id);
            Separator();
            Add("Distance: " + Format.Distance(interstellar.InterstellarDistance));
            AddDisabled("The cost depends on the cruise speed, see the route panel.");
            AddDisabled("The line length is not to scale.");
            AddDisabled("Formula: " + Formula(SegmentKind.Interstellar, false));
        }
        Show();
    }

    private bool IsCached(object key, double dvScale, bool flag)
    {
        if (ReferenceEquals(key, _key) && dvScale == _keyScale && flag == _keyTime)
            return true;
        _lines.Clear();
        _key = key;
        _keyScale = dvScale;
        _keyTime = flag;
        return false;
    }

    private void Show()
    {
        ImGui.BeginTooltip();
        for (int i = 0; i < _lines.Count; i++)
        {
            (string text, LineStyle style) = _lines[i];
            switch (style)
            {
                case LineStyle.Separator:
                    ImGui.Separator();
                    break;
                case LineStyle.Disabled:
                    ImGui.TextDisabled(text);
                    break;
                default:
                    ImGui.Text(text);
                    break;
            }
        }
        ImGui.EndTooltip();
    }

    private void Add(string text) => _lines.Add((text, LineStyle.Text));
    private void AddDisabled(string text) => _lines.Add((text, LineStyle.Disabled));
    private void Separator() => _lines.Add(("", LineStyle.Separator));

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
            SegmentKind.GroupLink => "Minor bodies",
            SegmentKind.Interstellar => "Interstellar trip",
            SegmentKind.Approach => "Approach",
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
            SegmentKind.GroupLink => "aggregated bodies, no delta-v",
            SegmentKind.Interstellar => "Newtonian coast; departure and capture through every gravity well",
            SegmentKind.Approach => "patched-conic capture chain into the route's last body",
            _ => "closed-form patched-conic estimate"
        };
    }
}
