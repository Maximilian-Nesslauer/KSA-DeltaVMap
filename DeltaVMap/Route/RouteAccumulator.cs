using System;
using System.Collections.Generic;
using DeltaVMap.Dv;
using DeltaVMap.Model;
using KSA;

namespace DeltaVMap.Route;

// One line in the route breakdown: a single burn with its label and cost.
internal sealed class RouteSegment
{
    public required string Label { get; init; }
    public required double Dv { get; init; }
    public required SegmentKind Kind { get; init; }
    public bool IsApproximate { get; init; }
    public bool Aerobraked { get; init; }
}

// The accumulated route: the breakdown, the totals, and the flags the panel needs.
// OutboundDv and ReturnDv already have their own aerobrake applied; the return is not
// simply twice the outbound (it pays the destination ascent and only its own aerobrake).
// PlaneChangeDv is the one-way inclination cost; the panel folds it into the displayed
// total when the toggle is on.
internal sealed class RouteSummary
{
    // Outbound burns total, after the outbound aerobrake. Excludes plane change, which
    // is additive and folded into the displayed total by the panel.
    public double OutboundDv { get; set; }

    // Return burns total, after the return aerobrake. Equal to the outbound baseline by
    // leg symmetry, minus any return-aerobrake saving.
    public double ReturnDv { get; set; }

    // One-way plane change (the interplanetary leg's inclination cost). A round trip
    // incurs it on both legs.
    public double PlaneChangeDv { get; set; }

    public double TransferTimeSeconds { get; set; }

    // Outbound aerobrake (the destination's last atmospheric capture body).
    public bool HasAerobrakeOption { get; set; }
    public string? AerobrakeBodyId { get; set; }
    public bool AerobrakeApplied { get; set; }

    // Return aerobrake (the origin body, where the return arrives).
    public bool HasReturnAerobrakeOption { get; set; }
    public string? ReturnAerobrakeBodyId { get; set; }
    public bool ReturnAerobrakeApplied { get; set; }

    public List<RouteSegment> Segments { get; } = new();

    public bool IsEmpty => Segments.Count == 0;
}

// Sums the per-leg delta-v along a route path, applying the toggles. Every transfer's
// burns are derived here from its v_inf legs via the shared TransferBurns rule (Oberth
// at a child or sibling, a plain Hohmann leg at a hub); ladder edges carry their own
// exact dV. The result reproduces the DvValidationDump totals (Earth->Luna, Earth->Mars)
// leg for leg.
internal static class RouteAccumulator
{
    public static RouteSummary Accumulate(RoutePath path, SystemGraph graph, RouteOptions options)
    {
        Func<string, BodyLadder?> ladderFor = graph.LadderFor;
        var summary = new RouteSummary();

        // When rooted at the star the vehicle is in heliocentric cruise: the star->planet
        // links carry no dV in the tree, so derive a real heliocentric transfer from the
        // "you are here" radius instead of reporting zero.
        double cruiseRadius = 0.0;
        if (path.Origin.Kind == StateKind.YouAreHere && path.Origin.Body.IsStar())
            cruiseRadius = path.Origin.RadiusFromBody;

        // Pass 1: find the last atmospheric body the route captures into from outside.
        // Aerobraking applies there (the destination, or the last atmospheric body on
        // the way, e.g. Mars for an Earth->Phobos trip).
        string? aeroId = null;
        foreach (RouteStep step in path.Steps)
        {
            TransferLegs? legs = LegsFor(step, cruiseRadius, ladderFor);
            if (legs is { ArriveIsHub: false } l && HasUsableAtmosphere(l.ArriveBody, ladderFor))
                aeroId = l.ArriveBody.Id;
        }
        summary.HasAerobrakeOption = aeroId != null;
        summary.AerobrakeBodyId = aeroId;
        bool aero = options.Aerobraking && aeroId != null;
        summary.AerobrakeApplied = aero;

        double outbound = 0.0;
        double baseSum = 0.0;
        double planeChange = 0.0;
        double time = 0.0;

        // Sum of (return-direction - outbound-direction) over the asymmetric Ascent edges,
        // so the return baseline can account for taking off where the outbound landed and
        // landing where it took off.
        double returnAdjustment = 0.0;

        // The first transfer departs the origin body. On the return that same burn is the
        // final capture back at the origin (Oberth eject and capture are equal), so it is
        // exactly what a return aerobrake would save.
        double originDepartBurn = 0.0;
        bool originDepartSeen = false;

        // Pass 2: walk the steps in order, appending segments.
        foreach (RouteStep step in path.Steps)
        {
            Edge e = step.Edge;
            TransferLegs? legsOpt = LegsFor(step, cruiseRadius, ladderFor);

            if (legsOpt is TransferLegs legs)
            {
                time += legs.TransferTimeSeconds;

                if (!originDepartSeen)
                {
                    originDepartBurn = legs.DepartBurn;
                    originDepartSeen = true;
                }

                AddSegment(summary, ref outbound, ref baseSum, aero,
                    DepartLabel(legs), legs.DepartBurn, SegmentKind.Transfer, legs.IsApproximate, canAero: false);

                AddSegment(summary, ref outbound, ref baseSum, aero,
                    ArriveLabel(legs), legs.ArriveBurn, SegmentKind.Transfer, legs.IsApproximate,
                    canAero: legs.ArriveBody.Id == aeroId);

                if (options.IncludePlaneChange)
                    planeChange += PlaneChangeFor(legs);
            }
            else if (e.IsStructural)
            {
                // A non-cruise hub link is a pure structural connector with no dV.
            }
            else
            {
                // A within-SOI ladder edge carries its own exact, direction-symmetric dV,
                // except an Ascent edge: landing is cheaper than ascending on a body with an
                // atmosphere, so pick by direction. IsApproximate is set at build for an
                // atmospheric body (empirical ascent loss / landing model).
                bool canAero = e.Kind == SegmentKind.Capture && e.To.Body.Id == aeroId;
                double dv = e.LadderDv;
                if (e.Kind == SegmentKind.Ascent)
                {
                    // Ascent runs LowOrbit -> Surface: a forward step lands (descent), a
                    // backward step launches (ascent). The return reverses each leg and so
                    // pays the opposite direction; record that delta for the round trip.
                    dv = step.Forward ? e.DescentDv : e.LadderDv;
                    returnAdjustment += step.Forward ? (e.LadderDv - e.DescentDv) : (e.DescentDv - e.LadderDv);
                }
                AddSegment(summary, ref outbound, ref baseSum, aero,
                    LadderLabel(e, step.Forward), dv, e.Kind, e.IsApproximate, canAero);
            }
        }

        summary.OutboundDv = outbound;
        summary.PlaneChangeDv = planeChange;
        summary.TransferTimeSeconds = time;

        // The return aerobrakes at the origin body, where it arrives back. Eligible when
        // that body has a usable atmosphere and the route actually left it on a transfer.
        bool returnAeroEligible = originDepartSeen && HasUsableAtmosphere(path.Origin.Body, ladderFor);
        summary.HasReturnAerobrakeOption = returnAeroEligible;
        summary.ReturnAerobrakeBodyId = returnAeroEligible ? path.Origin.Body.Id : null;
        bool returnAero = options.AerobrakingReturn && returnAeroEligible;
        summary.ReturnAerobrakeApplied = returnAero;

        // The return reverses the path: the transfer legs cost the same, but the Ascent
        // legs swap (take off where the outbound landed, land where it took off), which is
        // returnAdjustment. The return aerobrake then zeroes the origin capture.
        if (options.ShowReturnTrip)
            summary.ReturnDv = baseSum + returnAdjustment - (returnAero ? originDepartBurn : 0.0);

        return summary;
    }

    private static void AddSegment(
        RouteSummary summary, ref double outbound, ref double baseSum, bool aeroActive,
        string label, double baseDv, SegmentKind kind, bool approx, bool canAero)
    {
        bool zeroed = aeroActive && canAero;
        double finalDv = zeroed ? 0.0 : baseDv;
        outbound += finalDv;
        baseSum += baseDv;
        summary.Segments.Add(new RouteSegment
        {
            Label = label,
            Dv = finalDv,
            Kind = kind,
            IsApproximate = approx,
            Aerobraked = zeroed
        });
    }

    // The coupled legs of a transfer step, or of a cruise star->planet link; null for a
    // ladder or a plain structural hub link.
    private static TransferLegs? LegsFor(RouteStep step, double cruiseRadius, Func<string, BodyLadder?> ladderFor)
    {
        Edge e = step.Edge;
        if (e.IsTransfer)
            return TransferBurns.ComputeLegs(e, ladderFor);
        if (IsCruiseLink(e, cruiseRadius))
            return CruiseLegs(e, cruiseRadius, ladderFor);
        return null;
    }

    // A heliocentric cruise hop: the star hub links straight to a planet anchor and we
    // know the vehicle's heliocentric radius. The down-leg only (the up-leg lands on the
    // you-are-here node and stays a free structural link).
    private static bool IsCruiseLink(Edge e, double cruiseRadius)
    {
        return e.IsStructural
            && cruiseRadius > 0.0
            && e.From.Kind == StateKind.Hub
            && e.From.Body.IsStar()
            && e.To.Kind != StateKind.Hub
            && e.To.Kind != StateKind.YouAreHere;
    }

    private static TransferLegs CruiseLegs(Edge e, double cruiseRadius, Func<string, BodyLadder?> ladderFor)
    {
        Astronomical star = e.From.Body;
        double starMu = ((IParentBody)star).Mu;
        Astronomical planet = e.To.Body;
        var orbiter = planet as IOrbiter;
        double r2 = orbiter != null ? OrbitalStates.TransferRadius(orbiter.Orbit) : cruiseRadius;

        DeltaVCalculator.Hohmann(starMu, cruiseRadius, r2, out double depart, out double arriveVinf);
        double transferTime = DeltaVCalculator.TransferTimeSeconds(starMu, cruiseRadius, r2);
        bool approx = orbiter == null || orbiter.Orbit.Eccentricity >= 1.0;

        // Departure is a plain heliocentric burn: the vehicle is already in solar orbit,
        // there is no body SOI to climb out of, so the depart leg stands as is.
        BodyLadder? pl = ladderFor(planet.Id);
        double arriveFull = (pl != null && pl.LowOrbitRadius > 0.0)
            ? DeltaVCalculator.OberthBurn(pl.Mu, pl.LowOrbitRadius, arriveVinf)
            : arriveVinf;

        double circularize = 0.0;
        bool split = false;
        double arriveBurn = arriveFull;
        if (e.To.Kind == StateKind.Intercept && pl is { CanHoldOrbit: true } p && p.SoiRadius.HasValue)
        {
            circularize = DeltaVCalculator.EscapeToSoi(p.Mu, p.LowOrbitRadius, p.SoiRadius.Value);
            arriveBurn = Math.Max(0.0, arriveFull - circularize);
            split = true;
        }

        return new TransferLegs(depart, star, departIsHub: true, arriveBurn, planet,
            arriveIsHub: false, split, depart, arriveVinf, transferTime, approx);
    }

    // Plane change for a sibling leg only: both endpoints must orbit the same hub (two
    // planets of the star, or two moons of a planet). A hub-own-ladder transfer (one end
    // is the hub) and a cruise leg (one end is the star) are skipped.
    //
    // The inclination is turned against the hyperbolic excess (v_inf), not the much
    // larger hub-orbital speed: you tilt the departure (or arrival) asymptote, which is
    // the realistic marginal cost over a coplanar transfer. Using the orbital speed here
    // was the bug behind the wildly inflated figure - it overstated the cost by the ratio
    // of orbital speed to v_inf (roughly 10x for an Earth-Mars transfer). Charge it at the
    // cheaper (smaller v_inf) end.
    private static double PlaneChangeFor(TransferLegs legs)
    {
        if (legs.DepartBody is not IOrbiter a || legs.ArriveBody is not IOrbiter b)
            return 0.0;
        if (a.Orbit?.Parent == null || b.Orbit?.Parent == null)
            return 0.0;
        if (!ReferenceEquals(a.Orbit.Parent, b.Orbit.Parent))
            return 0.0;

        double di = a.Orbit.GetRelativeInclination(b.Orbit).Value();
        double vInf = Math.Min(legs.DepartVinf, legs.ArriveVinf);
        return DeltaVCalculator.PlaneChange(vInf, di);
    }

    private static bool HasUsableAtmosphere(Astronomical body, Func<string, BodyLadder?> ladderFor)
    {
        AtmosphereReference? atmo = ladderFor(body.Id)?.Body.GetAtmosphereReference();
        return atmo != null && atmo.Physical.SeaLevelDensity > 0.01;
    }

    private static string DepartLabel(TransferLegs legs)
    {
        return legs.DepartIsHub
            ? $"Depart {legs.DepartBody.Id} orbit"
            : $"Eject from {legs.DepartBody.Id}";
    }

    private static string ArriveLabel(TransferLegs legs)
    {
        if (legs.ArriveIsHub)
            return $"Arrive {legs.ArriveBody.Id} orbit";
        return legs.ArriveSplit
            ? $"Capture at {legs.ArriveBody.Id} (ellipse)"
            : $"Capture at {legs.ArriveBody.Id}";
    }

    private static string LadderLabel(Edge e, bool forward)
    {
        string body = e.From.Body.Id;
        return e.Kind switch
        {
            // Ascent runs LowOrbit -> Surface, so forward is the descent.
            SegmentKind.Ascent => forward ? $"Land on {body}" : $"Ascend from {body}",
            SegmentKind.Raise => forward ? $"Raise to {KindWord(e.To.Kind)}" : "Lower to low orbit",
            SegmentKind.Land => forward ? $"Land on {body}" : $"Lift off from {body}",
            SegmentKind.Capture => forward ? $"Circularize at {body}" : "Raise to capture ellipse",
            _ => e.Kind.ToString()
        };
    }

    private static string KindWord(StateKind kind)
    {
        return kind switch
        {
            StateKind.Stationary => "stationary orbit",
            StateKind.SoiEdge => "SOI edge",
            StateKind.YouAreHere => "current orbit",
            StateKind.LowOrbit => "low orbit",
            StateKind.Surface => "surface",
            _ => kind.ToString()
        };
    }
}
