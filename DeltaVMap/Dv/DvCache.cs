using System;
using System.Collections.Generic;
using KSA;

namespace DeltaVMap.Dv;

// The Hohmann transfer cost between two bodies sharing a hub. Depart and Arrive
// are the burn magnitudes in the hub frame; Arrive doubles as the hyperbolic
// excess speed (v_inf) used for the Oberth ejection/capture at the destination
// body. The struct is direction-bearing: Depart is the burn at the "from" body,
// Arrive at the "to" body.
internal readonly struct EdgeDv
{
    public readonly double DepartDv;
    public readonly double ArriveDv;
    public readonly double TransferTimeSeconds;
    public readonly bool IsApproximate;

    public EdgeDv(double departDv, double arriveDv, double transferTimeSeconds, bool isApproximate)
    {
        DepartDv = departDv;
        ArriveDv = arriveDv;
        TransferTimeSeconds = transferTimeSeconds;
        IsApproximate = isApproximate;
    }

    public double TotalDv => DepartDv + ArriveDv;

    public EdgeDv Reversed()
    {
        return new EdgeDv(ArriveDv, DepartDv, TransferTimeSeconds, IsApproximate);
    }
}

// Transfer dV cached by unordered body pair. Keplerian orbits are fixed, so values
// are computed once and never invalidated. The dictionary key is the
// lexicographically ordered pair of body Ids; lookups for the reverse direction
// reuse the stored value with the two burn legs swapped.
internal sealed class DvCache
{
    private readonly Dictionary<(string, string), EdgeDv> _cache = new();

    public int Count => _cache.Count;

    public void Clear()
    {
        _cache.Clear();
    }

    // Transfer cost from one body to another, both orbiting the same hub. Caller
    // is responsible for passing siblings (e.g. two planets of the star, or two
    // moons of a planet).
    public EdgeDv GetTransfer(IOrbiter from, IOrbiter to)
    {
        bool swapped = string.CompareOrdinal(from.Id, to.Id) > 0;
        var key = swapped ? (to.Id, from.Id) : (from.Id, to.Id);

        if (!_cache.TryGetValue(key, out EdgeDv edge))
        {
            IOrbiter first = swapped ? to : from;
            IOrbiter second = swapped ? from : to;
            edge = Compute(first, second);
            _cache[key] = edge;
        }

        return swapped ? edge.Reversed() : edge;
    }

    private static EdgeDv Compute(IOrbiter from, IOrbiter to)
    {
        Orbit fromOrbit = from.Orbit;
        Orbit toOrbit = to.Orbit;

        // A Hohmann transfer only makes sense between bodies sharing one hub. This
        // is a documented precondition; surface a violation loudly rather than
        // returning a number computed in mismatched frames. The dump wraps the
        // call in a catch, so this cannot reach the render path.
        if (fromOrbit.Parent != toOrbit.Parent)
            throw new ArgumentException(
                $"Transfer requires two bodies sharing one hub, got '{from.Id}' (parent '{fromOrbit.Parent?.Id}') and '{to.Id}' (parent '{toOrbit.Parent?.Id}').");

        double muHub = fromOrbit.Parent.Mu;
        double r1 = TransferRadius(fromOrbit);
        double r2 = TransferRadius(toOrbit);

        DeltaVCalculator.Hohmann(muHub, r1, r2, out double departDv, out double arriveDv);
        double transferTime = DeltaVCalculator.TransferTimeSeconds(muHub, r1, r2);
        bool isApproximate = fromOrbit.Eccentricity >= 1.0 || toOrbit.Eccentricity >= 1.0;

        return new EdgeDv(departDv, arriveDv, transferTime, isApproximate);
    }

    // The orbital radius used for a Hohmann-style transfer. For closed orbits this
    // matches the EffectiveRadius the game itself uses. Open orbits (comets, e >= 1)
    // have no apoapsis, so (Apoapsis + Periapsis) / 2 would be garbage; fall back to
    // the periapsis (perihelion) distance instead. This is a coarse approximation
    // for open orbits that just keeps the number finite, and the caller always
    // flags such transfers as approximate.
    private static double TransferRadius(Orbit orbit)
    {
        if (orbit.Eccentricity >= 1.0)
            return orbit.Periapsis;
        return DeltaVCalculator.EffectiveRadius(orbit.Eccentricity, orbit.SemiMajorAxis, orbit.Apoapsis, orbit.Periapsis);
    }
}
