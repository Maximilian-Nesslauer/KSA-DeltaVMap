using System;
using System.Collections.Generic;
using KSA;

namespace DeltaVMap.Dv;

// The kind of state a node represents on a body's ladder, or its role in the
// re-rooted graph. Surface through YouAreHere are physical ladder rungs (built by
// BuildLadder, classified by StateClassifier). Hub is an ancestor body rendered as
// a horizontal bus (the star is the topmost one); Intercept is the arrival node for
// a body too small to hold an orbit (a flyby/landing-only destination). MinorGroup is
// a synthetic aggregate: when a hub carries more minor bodies than the map can show as
// lanes, all of them collapse into one "+N" group node hanging off that hub.
internal enum StateKind
{
    Surface,
    LowOrbit,
    Stationary,
    SoiEdge,
    YouAreHere,
    Hub,
    Intercept,
    MinorGroup
}

// One rung of a body's vertical ladder: a place you can be, expressed as a
// circular radius from the body center. SoiEdge is the exception, it is an
// ellipse whose apoapsis sits at the SOI; Radius then carries that apoapsis.
internal readonly struct LadderRung
{
    public readonly StateKind Kind;
    public readonly double Radius;

    public LadderRung(StateKind kind, double radius)
    {
        Kind = kind;
        Radius = radius;
    }
}

// The full physical ladder for one body. Holds the key radii plus the ordered
// list of rungs from the deepest (Surface) to the highest (SoiEdge). YouAreHere is
// not part of the physical ladder; StateClassifier inserts it.
internal sealed class BodyLadder
{
    public required IParentBody Body { get; init; }
    public required double MeanRadius { get; init; }
    public required double Mu { get; init; }
    public required double LowOrbitRadius { get; init; }
    public double? StationaryRadius { get; init; }
    public double? SoiRadius { get; init; }
    public bool HasSurface { get; init; }
    // False for bodies whose SOI is so tight that no parking orbit fits inside it
    // (tiny moons like Deimos). Such bodies are surface-only / flyby destinations
    // and carry no LowOrbit, Stationary or SoiEdge rungs.
    public bool CanHoldOrbit { get; init; }
    public required IReadOnlyList<LadderRung> Rungs { get; init; }
}

// Surface-to-low-orbit ascent cost. Vacuum is the ideal two-impulse cost;
// Effective additionally applies the empirical atmospheric loss factor when the
// body has an atmosphere, and equals Vacuum when it does not. Effective is
// approximate exactly when an atmosphere is involved, because the loss factor is
// empirical.
internal readonly struct AscentDv
{
    public readonly double Vacuum;
    public readonly double Effective;
    public readonly bool HasAtmosphere;

    public AscentDv(double vacuum, double effective, bool hasAtmosphere)
    {
        Vacuum = vacuum;
        Effective = effective;
        HasAtmosphere = hasAtmosphere;
    }

    public bool IsApproximate => HasAtmosphere;
}

internal static class OrbitalStates
{
    // A body with an atmosphere but a radius larger than this is treated as a gas
    // giant with no solid surface. In the stock system the smallest gas giant
    // (Neptune, ~24,600 km) sits well above this and the largest body with both an
    // atmosphere and a real surface (Earth, ~6,400 km) well below, so the threshold
    // separates them cleanly. Airless bodies always keep their surface rung
    // regardless of size. This is a heuristic, the game exposes no gas-giant flag;
    // revisit if a modded system needs finer control.
    private const double GasGiantRadiusMeters = 15_000_000.0;

    public static bool HasAtmosphere(IParentBody body)
    {
        return body.GetAtmosphereReference() != null;
    }

    // A usable atmosphere: dense enough to fly jets in and to brake against. Reuses the
    // same sea-level density floor the descent and aerobrake models already apply, so the
    // jet halo and the aerobrake marker agree with the routing on which bodies "have air".
    public static bool HasUsableAtmosphere(Astronomical body)
    {
        AtmosphereReference? atmosphere = body.GetAtmosphereReference();
        return atmosphere != null && atmosphere.Physical.SeaLevelDensity > DeltaVCalculator.UsableAtmosphereDensity;
    }

    // True when the body carries a ring system in its template (Saturn in the stock
    // system). The map draws a thin ring ellipse for these; a body without the data simply
    // gets none, so this never fabricates rings where the game does not model them.
    public static bool HasRings(Astronomical body)
    {
        return body.BodyTemplate.RingsReference != null;
    }

    // True if the body has a solid surface you can land on.
    public static bool HasSolidSurface(IParentBody body)
    {
        if (!HasAtmosphere(body))
            return true;
        return body.MeanRadius < GasGiantRadiusMeters;
    }

    public static double ComputeLowOrbitRadius(IParentBody body)
    {
        AtmosphereReference? atmosphere = body.GetAtmosphereReference();
        bool hasAtmosphere = atmosphere != null;
        double atmosphereHeight = hasAtmosphere ? atmosphere!.Physical.Height.InMeters() : 0.0;
        return LowOrbitHeuristic.Compute(body.MeanRadius, hasAtmosphere, atmosphereHeight, body.GetNearSurfaceRadius());
    }

    // Synchronous orbit radius, or null if the body does not rotate or the
    // synchronous altitude lies outside the SOI (so it would not be a real place
    // you could park).
    public static double? ComputeStationaryRadius(IParentBody body)
    {
        double omega = Math.Abs(body.GetAngularVelocity());
        if (omega == 0.0)
            return null;

        double rSync = DeltaVCalculator.SynchronousRadius(body.Mu, omega);
        double soi = body.SphereOfInfluence;
        if (double.IsFinite(soi) && rSync >= soi)
            return null;

        return rSync;
    }

    public static double? FiniteSoiRadius(IParentBody body)
    {
        double soi = body.SphereOfInfluence;
        return (double.IsFinite(soi) && soi > 0.0) ? soi : null;
    }

    // The rendezvous radius to use for a transfer. For closed orbits this matches the
    // EffectiveRadius the game itself uses. Open orbits (comets, e >= 1) have no apoapsis,
    // so (Apoapsis + Periapsis) / 2 would be garbage (a hyperbolic apoapsis is negative);
    // their natural rendezvous point is the perihelion (closest approach to the hub). The
    // periapsis radius alone used to badly understate the cost because the downstream
    // Hohmann then treated the comet as a slow circular body there; DeltaVCalculator.
    // ConicTransfer now matches the comet's real (fast) perihelion speed instead, so the
    // perihelion is the correct rendezvous radius rather than a coarse stand-in. The caller
    // still flags such transfers as approximate.
    public static double TransferRadius(Orbit orbit)
    {
        if (orbit.Eccentricity >= 1.0)
            return orbit.Periapsis;
        return DeltaVCalculator.EffectiveRadius(orbit.Eccentricity, orbit.SemiMajorAxis, orbit.Apoapsis, orbit.Periapsis);
    }

    // Build the per-body ladder. Rungs are ordered bottom to top: Surface (if
    // solid), LowOrbit, Stationary (if applicable), SoiEdge (if finite).
    public static BodyLadder BuildLadder(IParentBody body)
    {
        double meanRadius = body.MeanRadius;
        double rLo = ComputeLowOrbitRadius(body);
        bool hasSurface = HasSolidSurface(body);
        double? rSoi = FiniteSoiRadius(body);

        // Keep the ladder physically ordered when the SOI is tight. The standard
        // "10 km above terrain" low orbit can land above a tiny body's SOI; when
        // that happens there is either no room for a parking orbit at all (SOI at
        // or below the surface) or only a grazing one (clamp it inside the SOI).
        bool canHoldOrbit = true;
        if (rSoi.HasValue)
        {
            if (rSoi.Value <= meanRadius)
                canHoldOrbit = false;
            else if (rLo >= rSoi.Value)
                rLo = (meanRadius + rSoi.Value) / 2.0;
        }

        double? rSync = canHoldOrbit ? ComputeStationaryRadius(body) : null;

        var rungs = new List<LadderRung>(4);
        if (hasSurface)
            rungs.Add(new LadderRung(StateKind.Surface, meanRadius));

        if (canHoldOrbit)
        {
            rungs.Add(new LadderRung(StateKind.LowOrbit, rLo));
            // A stationary rung only makes sense above the low orbit; below it the
            // body rotates faster than a low parking orbit and it is not useful.
            if (rSync.HasValue && rSync.Value > rLo)
                rungs.Add(new LadderRung(StateKind.Stationary, rSync.Value));
            if (rSoi.HasValue)
                rungs.Add(new LadderRung(StateKind.SoiEdge, rSoi.Value));
        }

        return new BodyLadder
        {
            Body = body,
            MeanRadius = meanRadius,
            Mu = body.Mu,
            LowOrbitRadius = rLo,
            StationaryRadius = rSync,
            // The body's real finite SOI, even when it is too tight to orbit, so
            // consumers can still report it. Rung inclusion keys off CanHoldOrbit.
            SoiRadius = rSoi,
            HasSurface = hasSurface,
            CanHoldOrbit = canHoldOrbit,
            Rungs = rungs
        };
    }

    // Surface-to-low-orbit ascent dV for a body, composing the vacuum two-impulse
    // cost with the atmospheric loss factor. Meaningful only for bodies with a
    // solid surface and a stable low orbit; callers gate on BodyLadder.HasSurface
    // and CanHoldOrbit.
    public static AscentDv ComputeAscent(BodyLadder ladder)
    {
        double rSurface = ladder.MeanRadius;
        double vacuum = DeltaVCalculator.AscentVacuum(ladder.Mu, rSurface, ladder.LowOrbitRadius);

        AtmosphereReference? atmosphere = ladder.Body.GetAtmosphereReference();
        if (atmosphere == null)
            return new AscentDv(vacuum, vacuum, hasAtmosphere: false);

        double seaLevelDensity = atmosphere.Physical.SeaLevelDensity;
        double atmosphereHeight = atmosphere.Physical.Height.InMeters();
        double surfaceGravity = DeltaVCalculator.SurfaceGravity(ladder.Mu, rSurface);
        double factor = DeltaVCalculator.AtmosphericAscentFactor(seaLevelDensity, surfaceGravity, atmosphereHeight, rSurface);
        return new AscentDv(vacuum, vacuum * factor, hasAtmosphere: true);
    }

    // Landing (low orbit -> surface) dV. On an airless body it equals the vacuum ascent:
    // there is no atmosphere to brake you, so a propulsive descent costs what a propulsive
    // ascent does. On a body with a usable atmosphere drag sheds most of the orbital
    // energy, so the propulsive cost is only a deorbit burn plus a terminal landing burn -
    // a fraction of the vacuum ascent that shrinks as the atmosphere thickens. The fraction
    // is an empirical heuristic in the same spirit as the atmospheric ascent factor (a thin
    // atmosphere like Mars leaves a fast terminal descent, a thick one like Venus brakes
    // almost to a stop); tune it against in-game figures, mark the result approximate.
    public static double ComputeDescent(BodyLadder ladder)
    {
        double vacuumAscent = DeltaVCalculator.AscentVacuum(ladder.Mu, ladder.MeanRadius, ladder.LowOrbitRadius);

        AtmosphereReference? atmosphere = ladder.Body.GetAtmosphereReference();
        if (atmosphere == null || atmosphere.Physical.SeaLevelDensity <= DeltaVCalculator.UsableAtmosphereDensity)
            return vacuumAscent;

        double densityRatio = atmosphere.Physical.SeaLevelDensity / DeltaVCalculator.StandardSeaLevelDensity;
        return vacuumAscent * DeltaVCalculator.AtmosphericLandingFraction(densityRatio);
    }
}
