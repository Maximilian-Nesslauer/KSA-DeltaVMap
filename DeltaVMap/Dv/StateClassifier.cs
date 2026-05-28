using System;
using KSA;

namespace DeltaVMap.Dv;

// The classified "you are here" state: which rung the vehicle sits on (or
// between), plus the radius used to place it.
internal readonly struct ClassifiedState
{
    public readonly StateKind Kind;
    public readonly double Radius;

    public ClassifiedState(StateKind kind, double radius)
    {
        Kind = kind;
        Radius = radius;
    }
}

// Classify the controlled vehicle's current state on its parent body's ladder, so
// routes start from the right place. Surface detection uses the game's own flag
// (Vehicle.HasMeaningfulOrbit) rather than a heuristic.
internal static class StateClassifier
{
    // An orbit this close to circular is treated as circular for rung snapping.
    private const double NearCircularEccentricity = 0.05;

    // A near-circular orbit whose SMA is within this fraction of a rung radius
    // snaps onto that rung instead of becoming its own YouAreHere node.
    private const double RungSnapFraction = 0.05;

    // Above this fraction of the SOI radius the vehicle is treated as being on the
    // elliptical-to-SOI-edge rung rather than in a tidy parking orbit.
    private const double SoiEdgeFraction = 0.5;

    public static ClassifiedState Classify(Vehicle vehicle, BodyLadder ladder)
    {
        if (!vehicle.HasMeaningfulOrbit)
            return new ClassifiedState(StateKind.Surface, ladder.MeanRadius);

        Orbit orbit = vehicle.Orbit;
        double eccentricity = orbit.Eccentricity;
        double semiMajorAxis = orbit.SemiMajorAxis;
        double apoapsis = orbit.Apoapsis;

        // An escape trajectory is leaving through the SOI boundary.
        if (eccentricity >= 1.0)
            return new ClassifiedState(StateKind.SoiEdge, ladder.SoiRadius ?? apoapsis);

        // Snap a near-circular orbit onto a specific rung first. This has to come
        // before the fractional SOI-edge test below, because on a tight-SOI body
        // the low orbit can sit above half the SOI, and a genuine parking orbit
        // there must still read as LowOrbit, not SoiEdge.
        if (eccentricity < NearCircularEccentricity)
        {
            if (IsNear(semiMajorAxis, ladder.LowOrbitRadius))
                return new ClassifiedState(StateKind.LowOrbit, ladder.LowOrbitRadius);

            if (ladder.StationaryRadius.HasValue && IsNear(semiMajorAxis, ladder.StationaryRadius.Value))
                return new ClassifiedState(StateKind.Stationary, ladder.StationaryRadius.Value);
        }

        // Otherwise, an orbit reaching well out toward the SOI is on the
        // elliptical-to-SOI-edge rung.
        if (ladder.SoiRadius.HasValue && apoapsis > SoiEdgeFraction * ladder.SoiRadius.Value)
            return new ClassifiedState(StateKind.SoiEdge, ladder.SoiRadius.Value);

        // Between rungs: the "medium orbit" case, placed at its actual SMA.
        return new ClassifiedState(StateKind.YouAreHere, semiMajorAxis);
    }

    private static bool IsNear(double value, double target)
    {
        return Math.Abs(value - target) <= RungSnapFraction * target;
    }
}
