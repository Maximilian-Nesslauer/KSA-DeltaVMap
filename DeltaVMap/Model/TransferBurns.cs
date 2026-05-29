using System;
using DeltaVMap.Dv;
using KSA;

namespace DeltaVMap.Model;

// The two real burns of a single cross-hub transfer edge, derived from its stored
// v_inf legs (EdgeDv) plus each end body's low-orbit radius. This is the one place that
// turns those two legs into actual burns (an Oberth ejection/capture at a child or
// sibling, a plain Hohmann leg at the hub), used by the route accumulator (which sums it
// along a path and applies toggles) and by the badge renderer (which shows the per-edge
// total). Computing it the same way for both guarantees a transfer's badge and its
// breakdown line always agree.
internal readonly struct TransferLegs
{
    // Departure burn at the From-side body's low orbit: a plain Hohmann leg when that
    // body IS the hub (a parking orbit around it, e.g. Earth LO -> Luna), an Oberth
    // ejection when it is a child/sibling that must climb out of its own SOI.
    public readonly double DepartBurn;
    public readonly Astronomical DepartBody;
    public readonly bool DepartIsHub;

    // Arrival burn at the To-side body. When the transfer lands on an Intercept anchor
    // (a destination entered from outside its SOI, full detail) this is only the loose
    // Oberth capture into the high ellipse; the circularize down to low orbit is the
    // separate Capture ladder edge below the Intercept, so the two are not double
    // counted. Otherwise it is the full burn: a plain Hohmann arrive leg when the To
    // body IS the hub (descend within its SOI), or the full Oberth capture straight to
    // low orbit at core detail.
    public readonly double ArriveBurn;
    public readonly Astronomical ArriveBody;
    public readonly bool ArriveIsHub;

    // True when ArriveBurn is only the loose capture into the Intercept ellipse and a
    // separate Capture ladder edge carries the remaining circularize down to low orbit
    // (To.Kind == Intercept on a body that holds an orbit). Drives the "(ellipse)" label;
    // the circularize itself lives on, and is zeroed for aerobraking via, that Capture edge.
    public readonly bool ArriveSplit;

    // The raw hyperbolic-excess speeds (v_inf) at each end, before the Oberth wrap. A
    // plane change is done against v_inf, not the much faster hub-orbital speed, so the
    // accumulator reads these for the inclination cost (using the cheaper, smaller end).
    public readonly double DepartVinf;
    public readonly double ArriveVinf;

    public readonly double TransferTimeSeconds;
    public readonly bool IsApproximate;

    public TransferLegs(
        double departBurn, Astronomical departBody, bool departIsHub,
        double arriveBurn, Astronomical arriveBody, bool arriveIsHub,
        bool arriveSplit,
        double departVinf, double arriveVinf,
        double transferTimeSeconds, bool isApproximate)
    {
        DepartBurn = departBurn;
        DepartBody = departBody;
        DepartIsHub = departIsHub;
        ArriveBurn = arriveBurn;
        ArriveBody = arriveBody;
        ArriveIsHub = arriveIsHub;
        ArriveSplit = arriveSplit;
        DepartVinf = departVinf;
        ArriveVinf = arriveVinf;
        TransferTimeSeconds = transferTimeSeconds;
        IsApproximate = isApproximate;
    }

    // The full coupled cost of the transfer leg (depart + the displayed arrival). When
    // the arrival is split this excludes the circularize, which the Capture edge adds,
    // so summing edge totals along a path stays correct.
    public double Total => DepartBurn + ArriveBurn;
}

internal static class TransferBurns
{
    // Resolve the two coupled burns of a Transfer edge. ladderFor maps a body Id to its
    // cached ladder (mu, r_lo, r_soi, CanHoldOrbit); it returns null only for an unknown
    // body, in which case that end falls back to the raw v_inf leg (the Oberth benefit
    // vanishes, which is the correct mu -> 0 limit). The edge MUST be a Transfer with a
    // value; callers gate on Edge.IsTransfer.
    public static TransferLegs ComputeLegs(Edge edge, Func<string, BodyLadder?> ladderFor)
    {
        EdgeDv dv = edge.Transfer!.Value;

        // From.Body is always the hub of the transfer, whether From is the hub's own
        // low orbit (a child/sibling spoke off the local hub) or the abstract hub bus
        // (a sibling spoke or the hub's-own-ladder spoke). The departure body differs:
        // off the hub bus the real departure is the spine child one level below it.
        Astronomical hub = edge.From.Body;
        bool departIsHub = edge.From.Kind != StateKind.Hub;
        Astronomical departBody = departIsHub
            ? edge.From.Body
            : (edge.From.Parent?.Body ?? edge.From.Body);
        Astronomical arriveBody = edge.To.Body;
        bool arriveIsHub = ReferenceEquals(arriveBody, hub) || arriveBody.Id == hub.Id;

        BodyLadder? departLadder = ladderFor(departBody.Id);
        BodyLadder? arriveLadder = ladderFor(arriveBody.Id);

        double departBurn = departIsHub
            ? dv.DepartDv
            : OberthOrRaw(departLadder, dv.DepartDv);

        double arriveFull = arriveIsHub
            ? dv.ArriveDv
            : OberthOrRaw(arriveLadder, dv.ArriveDv);

        // The capture into a destination's Intercept ellipse is the full capture minus
        // the circularize the Capture ladder edge carries, so the route never charges
        // the circularize twice.
        double circularize = 0.0;
        bool split = false;
        double arriveBurn = arriveFull;
        if (!arriveIsHub && edge.To.Kind == StateKind.Intercept
            && arriveLadder is { CanHoldOrbit: true } al && al.SoiRadius.HasValue)
        {
            circularize = DeltaVCalculator.EscapeToSoi(al.Mu, al.LowOrbitRadius, al.SoiRadius.Value);
            arriveBurn = Math.Max(0.0, arriveFull - circularize);
            split = true;
        }

        return new TransferLegs(
            departBurn, departBody, departIsHub,
            arriveBurn, arriveBody, arriveIsHub,
            split,
            dv.DepartDv, dv.ArriveDv,
            dv.TransferTimeSeconds, dv.IsApproximate);
    }

    private static double OberthOrRaw(BodyLadder? ladder, double vInf)
    {
        if (ladder == null || !(ladder.LowOrbitRadius > 0.0))
            return vInf;
        return DeltaVCalculator.OberthBurn(ladder.Mu, ladder.LowOrbitRadius, vInf);
    }
}
