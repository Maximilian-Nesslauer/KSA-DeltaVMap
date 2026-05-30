using System;

namespace DeltaVMap.Dv;

// Closed-form delta-v math. Every method here is pure: it takes doubles in SI
// units (meters, seconds, kilograms, m/s) and returns doubles. Nothing in this
// file touches game objects, so the formulas can be reasoned about and tested in
// isolation. The thin layer that pulls mu, radii and atmosphere data off the live
// celestials lives in OrbitalStates, DvCache and the validation dump.
internal static class DeltaVCalculator
{
    // Matches IParentBody.Mu = Mass * 6.6743e-11.
    public const double G = 6.6743e-11;

    // Below this eccentricity an orbit is treated as circular, matching the
    // game's own OrbitalTransfers.HohmannFlight.
    public const double CircularEccentricity = 0.01;

    // Half a degree in radians, below which a plane change is not worth drawing.
    public const double MinPlaneChangeRad = 0.00873;

    // Physical SI reference units (ISA standard atmosphere), NOT data about any body in
    // the loaded system: standard sea-level air density (kg/m^3) and standard surface
    // gravity (m/s^2). The atmospheric ascent/descent heuristics read each body's own
    // density and gravity from the celestial system and normalize them against these
    // absolute references (drag and gravity loss scale with the absolute value, so a body
    // at 1.225 kg/m^3 behaves the same in any system, custom or stock).
    public const double StandardSeaLevelDensity = 1.225;
    public const double StandardSurfaceGravity = 9.81;

    // Sea-level air density (kg/m^3) above which an atmosphere counts as "usable": thick
    // enough to fly a jet in and to aerobrake against. The jet halo, the aerobrake marker,
    // the landing model and the route's aerobrake eligibility all gate on this one floor so
    // they never disagree about which bodies have air.
    public const double UsableAtmosphereDensity = 0.01;

    public static double Mu(double massKg)
    {
        return massKg * G;
    }

    // Effective radius for an orbit, matching OrbitalTransfers.HohmannFlight.
    public static double EffectiveRadius(double eccentricity, double semiMajorAxis, double apoapsis, double periapsis)
    {
        return (eccentricity < CircularEccentricity) ? semiMajorAxis : (apoapsis + periapsis) / 2.0;
    }

    public static double CircularSpeed(double mu, double radius)
    {
        return Math.Sqrt(mu / radius);
    }

    // Vis-viva speed at radius r on an orbit of semi-major axis a.
    public static double VisVivaSpeed(double mu, double radius, double semiMajorAxis)
    {
        return Math.Sqrt(mu * (2.0 / radius - 1.0 / semiMajorAxis));
    }

    // Two-impulse vacuum ascent from rest on the surface to a circular low orbit.
    public static double AscentVacuum(double mu, double rSurface, double rLowOrbit)
    {
        double aTransfer = (rSurface + rLowOrbit) / 2.0;
        double dv1 = VisVivaSpeed(mu, rSurface, aTransfer);
        double dv2 = CircularSpeed(mu, rLowOrbit) - VisVivaSpeed(mu, rLowOrbit, aTransfer);
        return dv1 + dv2;
    }

    // Empirical loss factor applied to the vacuum ascent when the body has an
    // atmosphere, driven by atmospheric density and surface gravity. The result is
    // inherently approximate and clamped to a sane range.
    public static double AtmosphericAscentFactor(double seaLevelDensity, double surfaceGravity, double atmosphereHeight, double rSurface)
    {
        double densityRatio = seaLevelDensity / StandardSeaLevelDensity;
        double gravityRatio = surfaceGravity / StandardSurfaceGravity;
        double atmoDepth = atmosphereHeight / rSurface;
        double factor = 1.0
            + 0.12 * Math.Sqrt(densityRatio) * (1.0 + 2.0 * atmoDepth)
            + 0.05 * Math.Sqrt(gravityRatio);
        return Math.Clamp(factor, 1.0, 2.0);
    }

    // Surface gravity g = mu / r^2, used to derive the gravity ratio in the
    // atmospheric ascent factor.
    public static double SurfaceGravity(double mu, double rSurface)
    {
        return mu / (rSurface * rSurface);
    }

    // Fraction of the vacuum ascent a propulsive landing costs on a body with a usable
    // atmosphere: drag sheds most of the orbital energy, so only a deorbit burn plus a
    // terminal landing burn remain. The cost falls as the atmosphere thickens (a thick
    // atmosphere like Venus brakes nearly to a stop; a thin one like Mars leaves a fast
    // terminal descent). densityRatio is the body's sea-level density over the ISA standard.
    // The floor keeps a final touchdown burn even in a very thick atmosphere; the cap bounds
    // the wispiest atmospheres that barely brake. Checked offline against the stock bodies'
    // own data: Earth landed ~510 m/s, Venus ~460, Mars ~775 (a thin atmosphere reading
    // costlier than the thick ones, as it should). Tune here if an in-game figure reads wrong.
    public static double AtmosphericLandingFraction(double densityRatio)
    {
        return Math.Clamp(0.06 + 0.004 / (densityRatio + 0.01), 0.06, 0.35);
    }

    // Hohmann transfer between two circular radii around the same body.
    public static double CircularToCircular(double mu, double r1, double r2)
    {
        double aTransfer = (r1 + r2) / 2.0;
        double dvDepart = Math.Abs(CircularSpeed(mu, r1) - VisVivaSpeed(mu, r1, aTransfer));
        double dvArrive = Math.Abs(VisVivaSpeed(mu, r2, aTransfer) - CircularSpeed(mu, r2));
        return dvDepart + dvArrive;
    }

    // Raise apoapsis from a circular rung at r to the SOI boundary.
    public static double EscapeToSoi(double mu, double r, double rSoi)
    {
        double aEscape = (r + rSoi) / 2.0;
        return VisVivaSpeed(mu, r, aEscape) - CircularSpeed(mu, r);
    }

    // Hohmann transfer around a hub body between two orbital radii. Returns the
    // departure and arrival burn magnitudes separately; the arrival magnitude
    // doubles as the hyperbolic excess speed (v_inf) for an Oberth capture.
    public static void Hohmann(double muHub, double r1, double r2, out double dvDepart, out double dvArrive)
    {
        double sum = r1 + r2;
        dvDepart = Math.Abs(CircularSpeed(muHub, r1) * (Math.Sqrt(2.0 * r2 / sum) - 1.0));
        dvArrive = Math.Abs(CircularSpeed(muHub, r2) * (1.0 - Math.Sqrt(2.0 * r1 / sum)));
    }

    // Two-impulse transfer around a hub between two endpoints, one or both of which may be on
    // an open (parabolic / hyperbolic) conic. This generalizes Hohmann: a bound endpoint moves
    // at circular speed, but an open endpoint (a comet at its perihelion) moves far faster, at
    // or above escape speed, so matching its velocity there costs much more than a circular
    // Hohmann leg implies. rA / rB are the rendezvous radii (a bound body's effective radius,
    // an open body's perihelion); openA / openB pick a velocity match against the true
    // perihelion speed instead of the circular speed. With both ends bound this reduces
    // exactly to Hohmann (verified in the offline tests), so the common path is unchanged and
    // only comet transfers see the difference. Named apart from the TransferLegs struct (the
    // route-level result of a transfer edge) to keep the two distinct where both appear.
    public static void ConicTransfer(
        double muHub,
        double rA, bool openA, double eccentricityA,
        double rB, bool openB, double eccentricityB,
        out double dvA, out double dvB)
    {
        double aTransfer = (rA + rB) / 2.0;
        double vBodyA = openA ? PeriapsisSpeed(muHub, rA, eccentricityA) : CircularSpeed(muHub, rA);
        double vBodyB = openB ? PeriapsisSpeed(muHub, rB, eccentricityB) : CircularSpeed(muHub, rB);
        dvA = Math.Abs(vBodyA - VisVivaSpeed(muHub, rA, aTransfer));
        dvB = Math.Abs(vBodyB - VisVivaSpeed(muHub, rB, aTransfer));
    }

    // Speed at periapsis on any conic of eccentricity e: v = sqrt(mu * (1 + e) / r_peri). It
    // reduces to the circular speed at e = 0, the escape speed at e = 1, and exceeds escape
    // for a hyperbola, so it gives the true (fast) speed of a comet at its closest approach.
    public static double PeriapsisSpeed(double mu, double rPeriapsis, double eccentricity)
    {
        return Math.Sqrt(mu * (1.0 + eccentricity) / rPeriapsis);
    }

    // Analytical patched-conic ejection (or capture, reversed) from a circular low
    // orbit deep in the well, given the hyperbolic excess speed v_inf from the hub
    // Hohmann. As mu -> 0 the Oberth benefit vanishes and the burn tends to v_inf,
    // which is physically correct and needs no special case.
    public static double OberthBurn(double muBody, double rLowOrbit, double vInf)
    {
        double vPark = CircularSpeed(muBody, rLowOrbit);
        double vBurn = Math.Sqrt(vInf * vInf + 2.0 * muBody / rLowOrbit);
        return vBurn - vPark;
    }

    // Plane change at a node, given the circular speed at the relevant radius.
    // Returns 0 below the half-degree threshold.
    public static double PlaneChange(double circularSpeed, double relativeInclinationRad)
    {
        if (relativeInclinationRad < MinPlaneChangeRad)
            return 0.0;
        return 2.0 * circularSpeed * Math.Sin(relativeInclinationRad / 2.0);
    }

    // Hohmann transfer time (half the transfer ellipse period).
    public static double TransferTimeSeconds(double muHub, double r1, double r2)
    {
        double aTransfer = (r1 + r2) / 2.0;
        return Math.PI * Math.Sqrt(aTransfer * aTransfer * aTransfer / muHub);
    }

    // Synchronous (stationary) orbit radius for a body rotating at angular
    // velocity omega. Caller is responsible for checking omega != 0 and that the
    // result lies inside the SOI.
    public static double SynchronousRadius(double mu, double omega)
    {
        double rotationPeriod = 2.0 * Math.PI / Math.Abs(omega);
        return Math.Cbrt(mu * rotationPeriod * rotationPeriod / (4.0 * Math.PI * Math.PI));
    }
}
