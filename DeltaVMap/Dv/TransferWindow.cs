using System;

namespace DeltaVMap.Dv;

// Closed-form transfer-window math: the timing companion to DeltaVCalculator. Like
// that file every method here is pure, taking doubles in SI units (meters, seconds,
// kilograms) and radians, and returning doubles. Nothing touches game objects, so the
// formulas can be reasoned about and tested offline. The thin layer that pulls the
// semi-major axes, periods, phase angles and v_inf off the live celestials lives in the
// game-typed builder.
//
// The phase-angle, synodic-rate and countdown formulas mirror the game's
// OrbitalTransfers.AlignmentTime so those figures agree with the stock "Show Alignment"
// planner. The ejection geometry is a separate patched-conic approximation (Bate-Mueller-
// White / Curtis); AlignmentTime computes no ejection angle, and the game's own ejection
// value is a numerical minimization that is not reproduced here. The angle wrapping
// reproduces MathEx.ToOrbitAngle / ToDeviationAngle locally to keep the file free of any
// game dependency.
internal static class TransferWindow
{
    public const double TwoPi = 2.0 * Math.PI;

    // The lead angle the target must have over the source at departure so a Hohmann arc
    // arrives where the target will be. Source inner (aSource < aTarget) yields a positive
    // angle (target should lead), source outer yields a negative one, and the single wrap
    // handles both. The retrograde branch (relative inclination over 90 deg) mirrors the
    // 2*PI minus form the game uses; that form is deliberately left un-rewrapped, matching
    // AlignmentTime so the two agree.
    public static double TargetPhaseAngle(double aSource, double aTarget, bool retrograde)
    {
        double lead = Math.PI * (1.0 - Math.Pow((aSource + aTarget) / (2.0 * aTarget), 1.5));
        return retrograde ? (TwoPi - ToOrbitAngle(lead)) : ToOrbitAngle(lead);
    }

    // The relative angular rate between the two orbits. Prograde subtracts the source rate
    // from the target rate; retrograde adds them, because the bodies sweep opposite ways and
    // the window recurs faster. The source term is dropped when its period is zero, guarding a
    // parking-orbit source with no defined period. The game applies that guard on its prograde
    // and same-SOI paths; it is kept here on the retrograde path too, which never differs in
    // practice because a retrograde sibling always has a non-zero period.
    public static double SynodicRate(double periodSource, double periodTarget, bool retrograde)
    {
        double rateSource = (periodSource != 0.0) ? (TwoPi / periodSource) : 0.0;
        double rateTarget = TwoPi / periodTarget;
        return retrograde ? (rateTarget + rateSource) : (rateTarget - rateSource);
    }

    // How often the window recurs: the time for the relative angle to sweep a full turn.
    public static double SynodicPeriod(double periodSource, double periodTarget, bool retrograde)
    {
        return TwoPi / Math.Abs(SynodicRate(periodSource, periodTarget, retrograde));
    }

    // Seconds until the next departure window, given the current and target phase angles and
    // the synodic rate. The gap is the angle the configuration still has to close; the wrap
    // folds it into a single relative revolution so the result is the soonest window, never a
    // past one. The wrap flag is true for the prograde and same-SOI cases (the game normalizes
    // the gap there) and false for retrograde, where the game divides the raw gap directly
    // without normalizing; passing it through faithfully keeps the result equal to the stock
    // planner. The optional offset shifts the target phase, exposed by the game as a degrees
    // offset and kept at zero by default.
    public static double TimeToWindowSeconds(double currentPhase, double targetPhase, double synodicRate, double offset = 0.0, bool wrap = true)
    {
        double gap = currentPhase - (targetPhase + offset);
        if (wrap)
        {
            if (gap > 0.0 && synodicRate > 0.0)
                gap -= TwoPi;
            if (gap < 0.0)
                gap += TwoPi;
        }
        return Math.Abs(gap / synodicRate);
    }

    // Eccentricity of the departure hyperbola for a burn from a circular parking orbit of
    // radius rPark around a body of gravitational parameter muBody, reaching hyperbolic
    // excess speed vInf. Greater than 1 for any positive vInf, as an escape trajectory must be.
    public static double EjectionEccentricity(double vInf, double rPark, double muBody)
    {
        return 1.0 + rPark * vInf * vInf / muBody;
    }

    // The angle from the parking-orbit prograde direction at which the ejection burn's
    // periapsis must sit so the outgoing hyperbolic asymptote ends up parallel to the body's
    // velocity around the hub. acos(-1/e) is the true anomaly of the outgoing asymptote, in
    // (PI/2, PI); the burn point sits PI minus that ahead of (or behind) prograde, in
    // (0, PI/2), and rises toward 90 deg as vInf grows. This is the analytical patched-conic
    // value (Bate-Mueller-White / Curtis); the exact game value additionally accounts for the
    // finite SOI, which is not needed here.
    public static double EjectionAngle(double vInf, double rPark, double muBody)
    {
        double e = EjectionEccentricity(vInf, rPark, muBody);
        return Math.PI - Math.Acos(-1.0 / e);
    }

    // Local reimplementation of MathEx.ToOrbitAngle, which delegates to ToDeviationAngle.
    // Kept private so the file carries no game reference.
    private static double ToOrbitAngle(double inRadians)
    {
        return ToDeviationAngle(inRadians);
    }

    // Wraps an angle into (-PI, PI], matching MathEx.ToDeviationAngle exactly.
    private static double ToDeviationAngle(double inRadians)
    {
        double num = inRadians % TwoPi;
        if (num < -Math.PI)
            return num + TwoPi;
        if (num > Math.PI)
            return num - TwoPi;
        return num;
    }
}
