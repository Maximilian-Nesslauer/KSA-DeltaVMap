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
        double r1 = OrbitalStates.TransferRadius(fromOrbit);
        double r2 = OrbitalStates.TransferRadius(toOrbit);

        // An open endpoint (a comet) is matched at its true perihelion speed; a bound pair
        // keeps the exact circular Hohmann, so stock planet-to-planet numbers are unchanged.
        bool fromOpen = fromOrbit.Eccentricity >= 1.0;
        bool toOpen = toOrbit.Eccentricity >= 1.0;
        double departDv;
        double arriveDv;
        if (fromOpen || toOpen)
            DeltaVCalculator.ConicTransfer(muHub, r1, fromOpen, fromOrbit.Eccentricity, r2, toOpen, toOrbit.Eccentricity, out departDv, out arriveDv);
        else
            DeltaVCalculator.Hohmann(muHub, r1, r2, out departDv, out arriveDv);

        double transferTime = DeltaVCalculator.TransferTimeSeconds(muHub, r1, r2);
        bool isApproximate = fromOpen || toOpen;

        return new EdgeDv(departDv, arriveDv, transferTime, isApproximate);
    }
}
