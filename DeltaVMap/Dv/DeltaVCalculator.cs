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
        double densityRatio = seaLevelDensity / 1.225;
        double gravityRatio = surfaceGravity / 9.81;
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
