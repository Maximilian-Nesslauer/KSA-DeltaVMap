using System;

namespace DeltaVMap.Dv;

// Low-orbit altitude heuristic (r_lo), the radius of the circular parking orbit a
// body's ladder starts at. The convention, borrowed from the KSP delta-v map, is
// "about 10 km above the atmosphere or the highest terrain". Pure: takes
// primitives so it can be reasoned about without a live body; OrbitalStates pulls
// the atmosphere and terrain values off the celestial and calls in here.
internal static class LowOrbitHeuristic
{
    private const double ClearanceMeters = 10000.0;
    private const double AbsoluteFloorMeters = 5000.0;

    public static double Compute(double meanRadius, bool hasAtmosphere, double atmosphereHeight, double nearSurfaceRadius)
    {
        double rLo;
        if (hasAtmosphere)
        {
            // About 10 km above the atmosphere boundary (or 10% of its height,
            // whichever is larger, for very tall atmospheres).
            rLo = meanRadius + atmosphereHeight + Math.Max(ClearanceMeters, 0.1 * atmosphereHeight);
        }
        else
        {
            // About 10 km above the highest terrain, but never below 5% of the
            // body radius. The 5% term is a sane minimum parking altitude that
            // wins for larger airless bodies (e.g. Luna lands at ~87 km, not the
            // ~20 km the bare 10 km clearance would give), leaving margin over
            // unmodelled terrain.
            rLo = Math.Max(nearSurfaceRadius + ClearanceMeters, meanRadius * 1.05);
        }

        return Math.Max(rLo, meanRadius + AbsoluteFloorMeters);
    }
}
