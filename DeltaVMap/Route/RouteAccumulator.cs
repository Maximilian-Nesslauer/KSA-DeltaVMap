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

// What the route accumulator needs to know about the controlled vessel, read once per route
// by the window. FromVessel is true when the vessel parks at the route's departure body
// (InterstellarLegs.VesselParksAt) and the route does not start from the surface, so an
// interstellar departure burns from the vessel's own orbit.
internal readonly record struct RouteContext(bool FromVessel)
{
    public static RouteContext None => default;
}

// The interstellar part of a route, folded into one leg. Its cost depends on the cruise speed
// the player picks, so it is not summed here: the route panel prices the leg at display time
// (InterstellarSection) and adds it to the rest of the route.
internal sealed class InterstellarRoute
{
    // The ego root body the departure climbs out of, and the body the capture ends at.
    public required PhysicalNode DepartBody { get; init; }
    public required PhysicalNode Target { get; init; }

    // The two system roots, for the stock planner preselection. DestinationRoot is null for a
    // capture inside the system the vessel already coasts through on an open orbit.
    public required PhysicalNode SourceRoot { get; init; }
    public PhysicalNode? DestinationRoot { get; init; }

    // The part of the chain capture that the route charges again as the circularize ladder
    // step after an Intercept anchor (DeltaVCalculator.EscapeToSoi), the same split as a
    // transfer into an Intercept. Zero for a low-orbit or parking-orbit anchor.
    public double CaptureSplitDv { get; init; }

    // The target is a star or barycenter, so the capture periapsis can be chosen.
    public bool TargetIsHub { get; init; }

    // The departure burns from the vessel's own orbit (RouteContext.FromVessel); the ladder
    // steps between the vessel and the low orbit are then not charged.
    public bool FromVessel { get; init; }

    // The vessel coasts on an open orbit around the system root it departs from, so there is no
    // departure from a parking orbit: the route panel prices the trip from the vessel's live
    // hyperbolic excess and direction (InterstellarLegs.TryTransitState).
    public bool InTransit { get; init; }

    // Where the interstellar block goes among the route segments.
    public int BreakdownIndex { get; init; }

    // Every segment of the route except the interstellar leg.
    public double RestDv { get; init; }
}

// The accumulated route: the breakdown, the totals, and the flags the panel needs.
// OutboundDv and ReturnDv already have their own aerobrake applied; the return is not
// simply twice the outbound (it pays the destination ascent and only its own aerobrake).
// PlaneChangeDv is the one-way inclination cost; the panel folds it into the displayed
// total when the toggle is on. A route with an interstellar leg carries it in Interstellar
// and has no return, plane change or aerobrake.
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

    public InterstellarRoute? Interstellar { get; set; }

    public bool IsEmpty => Segments.Count == 0 && Interstellar == null;
}

// Sums the per-leg delta-v along a route path, applying the toggles. Every transfer's
// burns are derived here from its v_inf legs via the shared TransferBurns rule (Oberth
// at a child or sibling, a plain Hohmann leg at a hub); ladder edges carry their own
// exact dV. The result reproduces the DvValidationDump totals (Earth->Luna, Earth->Mars)
// leg for leg.
//
// A route that crosses an Interstellar edge, or leaves an open cruise through an Approach
// edge, folds everything from that edge to the last Approach edge into one InterstellarRoute:
// the departure out of the ego root body and the patched-conic capture into the body the
// Approach run ends at. The steps before it (an ascent, the hub links) and after it (the
// circularize from an Intercept anchor, a landing) are charged as usual.
internal static class RouteAccumulator
{
    public static RouteSummary Accumulate(RoutePath path, SystemGraph graph, RouteOptions options, in RouteContext context)
    {
        int legStart = InterstellarLegStart(path);
        if (legStart >= 0)
            return AccumulateInterstellar(path, graph, legStart, in context);

        Func<string, BodyLadder?> ladderFor = graph.LadderFor;
        var summary = new RouteSummary();

        // When rooted at a star or barycenter the vehicle is in cruise around it: the hub
        // links to its children carry no dV in the tree, so derive a real transfer around the
        // hub from the "you are here" radius instead of reporting zero. A vehicle on an open
        // orbit has no orbit to transfer from, so it gets no cruise legs.
        double cruiseRadius = 0.0;
        if (path.Origin.Kind == StateKind.YouAreHere && !path.Origin.IsOpenCruise && SystemGraph.IsHubOnlyBody(path.Origin.Body))
            cruiseRadius = path.Origin.RadiusFromBody;
        Astronomical origin = path.Origin.Body;

        // Pass 1: find the last atmospheric body the route captures into from outside.
        // Aerobraking applies there (the destination, or the last atmospheric body on
        // the way, e.g. Mars for an Earth->Phobos trip).
        string? aeroId = null;
        foreach (RouteStep step in path.Steps)
        {
            TransferLegs? legs = LegsFor(step, origin, cruiseRadius, ladderFor);
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
            TransferLegs? legsOpt = LegsFor(step, origin, cruiseRadius, ladderFor);

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

    // The first step of the interstellar leg: the Interstellar edge, or for an origin on an open
    // cruise the first Approach edge out of its root hub. -1 for a route inside one system.
    private static int InterstellarLegStart(RoutePath path)
    {
        for (int i = 0; i < path.Steps.Count; i++)
        {
            SegmentKind kind = path.Steps[i].Edge.Kind;
            if (kind == SegmentKind.Interstellar || kind == SegmentKind.Approach)
                return i;
        }
        return -1;
    }

    private static RouteSummary AccumulateInterstellar(RoutePath path, SystemGraph graph, int legStart, in RouteContext context)
    {
        var summary = new RouteSummary();
        Func<string, BodyLadder?> ladderFor = graph.LadderFor;

        // The Approach run ends at the last Approach step: the node after it is the anchor the
        // chain captures into, and every step in between is part of the folded leg.
        int legEnd = legStart;
        for (int i = legStart; i < path.Steps.Count; i++)
        {
            if (path.Steps[i].Edge.Kind == SegmentKind.Approach)
                legEnd = i;
        }
        StateNode anchor = path.Nodes[legEnd + 1];

        PhysicalNode departBody = graph.Find(path.Origin.Body.Id)
            ?? throw new InvalidOperationException($"Route origin body '{path.Origin.Body.Id}' is not in the graph.");
        PhysicalNode target = graph.Find(anchor.Body.Id)
            ?? throw new InvalidOperationException($"Route target body '{anchor.Body.Id}' is not in the graph.");
        bool crossesSystems = path.Steps[legStart].Edge.Kind == SegmentKind.Interstellar;
        // Only an open orbit around the system root itself is a coast between systems. Around
        // an orbiting star the vessel's excess is measured against that star, so it departs
        // from its own orbit there like from any other body.
        bool inTransit = path.Origin.IsOpenCruise && departBody.IsSystemRoot;

        double outbound = 0.0;
        double unused = 0.0;
        for (int i = 0; i < legStart; i++)
        {
            RouteStep step = path.Steps[i];
            Edge e = step.Edge;
            // The departure from the vessel's own orbit replaces the ladder steps between the
            // vessel and the low orbit.
            if (e.IsStructural || (context.FromVessel && !inTransit && !e.IsTransfer))
                continue;
            AddLadderOrTransfer(summary, step, ladderFor, ref outbound, ref unused);
        }
        int breakdownIndex = summary.Segments.Count;

        for (int i = legEnd + 1; i < path.Steps.Count; i++)
        {
            RouteStep step = path.Steps[i];
            if (step.Edge.IsStructural)
                continue;
            AddLadderOrTransfer(summary, step, ladderFor, ref outbound, ref unused);
        }

        double split = 0.0;
        if (anchor.Kind == StateKind.Intercept && target.Ladder is { CanHoldOrbit: true, SoiRadius: double soi })
            split = DeltaVCalculator.EscapeToSoi(target.Ladder.Mu, target.Ladder.LowOrbitRadius, soi);

        summary.OutboundDv = outbound;
        summary.Interstellar = new InterstellarRoute
        {
            DepartBody = departBody,
            Target = target,
            SourceRoot = SystemGraph.SystemRootOf(departBody),
            DestinationRoot = crossesSystems ? SystemGraph.SystemRootOf(target) : null,
            CaptureSplitDv = split,
            TargetIsHub = target.IsHubOnly,
            FromVessel = context.FromVessel && !inTransit,
            InTransit = inTransit,
            BreakdownIndex = breakdownIndex,
            RestDv = outbound
        };
        return summary;
    }

    // One step outside the interstellar leg. Only ladder steps and the hub links reach here in
    // practice; a transfer is still priced by the shared rule so nothing is ever charged as zero.
    private static void AddLadderOrTransfer(RouteSummary summary, in RouteStep step, Func<string, BodyLadder?> ladderFor, ref double outbound, ref double baseSum)
    {
        Edge e = step.Edge;
        if (e.IsTransfer)
        {
            TransferLegs legs = TransferBurns.ComputeLegs(e, ladderFor);
            AddSegment(summary, ref outbound, ref baseSum, false, DepartLabel(legs), legs.DepartBurn, SegmentKind.Transfer, legs.IsApproximate, canAero: false);
            AddSegment(summary, ref outbound, ref baseSum, false, ArriveLabel(legs), legs.ArriveBurn, SegmentKind.Transfer, legs.IsApproximate, canAero: false);
            return;
        }
        double dv = e.Kind == SegmentKind.Ascent ? (step.Forward ? e.DescentDv : e.LadderDv) : e.LadderDv;
        AddSegment(summary, ref outbound, ref baseSum, false, LadderLabel(e, step.Forward), dv, e.Kind, e.IsApproximate, canAero: false);
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
    private static TransferLegs? LegsFor(RouteStep step, Astronomical origin, double cruiseRadius, Func<string, BodyLadder?> ladderFor)
    {
        Edge e = step.Edge;
        if (e.IsTransfer)
        {
            TransferLegs legs = TransferBurns.ComputeLegs(e, ladderFor);
            return cruiseRadius > 0.0 && !legs.DepartIsHub && legs.DepartBody.Id == origin.Id
                ? FromCruiseOrbit(legs, cruiseRadius, ladderFor)
                : legs;
        }
        if (IsCruiseLink(e, cruiseRadius))
            return CruiseLegs(e, cruiseRadius, ladderFor);
        return null;
    }

    // A vehicle that orbits a hub-only root directly (a star of a multiple system) leaves for a
    // sibling star from its own orbit, not from the parking orbit of the star's ladder, which it
    // never reached. The ejection is the Oberth burn at the vehicle's radius, taken as circular
    // like the other cruise legs.
    private static TransferLegs FromCruiseOrbit(in TransferLegs legs, double cruiseRadius, Func<string, BodyLadder?> ladderFor)
    {
        BodyLadder? ladder = ladderFor(legs.DepartBody.Id);
        double depart = ladder != null
            ? DeltaVCalculator.OberthBurn(ladder.Mu, cruiseRadius, legs.DepartVinf)
            : legs.DepartVinf;
        return new TransferLegs(depart, legs.DepartBody, legs.DepartIsHub,
            legs.ArriveBurn, legs.ArriveBody, legs.ArriveIsHub, legs.ArriveSplit,
            legs.DepartVinf, legs.ArriveVinf, legs.TransferTimeSeconds, legs.IsApproximate);
    }

    // A cruise hop: the star (or barycenter) hub links straight to a child's anchor and we
    // know the vehicle's radius around the hub. The down-leg only (the up-leg lands on the
    // you-are-here node and stays a free structural link).
    private static bool IsCruiseLink(Edge e, double cruiseRadius)
    {
        return e.IsStructural
            && cruiseRadius > 0.0
            && e.From.Kind == StateKind.Hub
            && SystemGraph.IsHubOnlyBody(e.From.Body)
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

        // The vehicle is already in a (bound) orbit around the hub, so the cruise origin is the
        // bound end. A comet target is matched at its true perihelion speed; a star of a
        // multiple system moves at the speed of its own central mass; a planet keeps the plain
        // circular Hohmann arrive leg.
        bool toOpen = orbiter != null && orbiter.Orbit.Eccentricity >= 1.0;
        bool ownCentralMass = orbiter != null && OrbitalStates.HasOwnCentralMass(orbiter.Orbit, starMu);
        double depart;
        double arriveVinf;
        if (toOpen)
        {
            DeltaVCalculator.ConicTransfer(starMu, cruiseRadius, false, 0.0, r2, true, orbiter!.Orbit.Eccentricity, out depart, out arriveVinf);
        }
        else if (ownCentralMass)
        {
            DeltaVCalculator.TransferBetweenSpeeds(starMu, cruiseRadius, DeltaVCalculator.CircularSpeed(starMu, cruiseRadius),
                r2, DeltaVCalculator.CircularSpeed(orbiter!.Orbit.Mu, r2), out depart, out arriveVinf);
        }
        else
        {
            DeltaVCalculator.Hohmann(starMu, cruiseRadius, r2, out depart, out arriveVinf);
        }
        double transferTime = DeltaVCalculator.TransferTimeSeconds(starMu, cruiseRadius, r2);
        bool approx = orbiter == null || toOpen || ownCentralMass;

        // Departure is a plain burn around the hub: the vehicle already orbits it, there is no
        // body SOI to climb out of, so the depart leg stands as is.
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
    // planets of a star, or two moons of a planet). A hub-own-ladder transfer (one end
    // is the hub) and a cruise leg (one end is the star or barycenter hub) are skipped.
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
        return atmo != null && atmo.Physical.SeaLevelDensity > DeltaVCalculator.UsableAtmosphereDensity;
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
            SegmentKind.Interstellar => $"Interstellar to {e.To.Body.Id}",
            SegmentKind.Approach => $"Approach {e.To.Body.Id}",
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
