using System;
using System.Collections.Generic;

namespace DeltaVMap.Model;

// What a body is in the map's structure. A Star (any StellarBody) and a Barycenter (the empty
// root of a multiple star system) are hub-only: the map shows them as a bus and never gives them
// a surface. Every other body (planets, moons, minor bodies) is a Body.
internal enum BodyRole
{
    Body,
    Star,
    Barycenter
}

// The structural decisions of the physical forest, free of game types so they are unit-tested
// offline. SystemGraph reads the bodies and asks these rules.
internal static class ForestRules
{
    // A StellarBody is a star. An independent root that is no star is a barycenter, which the
    // game's own Astronomical.IsStar() does not count as a star (Barycenter.IsStar() is false).
    public static BodyRole RoleOf(bool isStellarBody, bool isIndependentRoot)
    {
        if (isStellarBody)
            return BodyRole.Star;
        return isIndependentRoot ? BodyRole.Barycenter : BodyRole.Body;
    }

    // The minor bodies directly under a star or barycenter that has no major planet are that
    // system's planets, because stock authors every exoplanet as a MinorBody. Under a body, or
    // next to a major planet (Ceres beside the planets of Sol), they stay minor.
    public static bool MinorChildrenArePlanets(BodyRole parentRole, bool hasMajorPlanet)
    {
        return parentRole != BodyRole.Body && !hasMajorPlanet;
    }

    public static bool IsMinor(bool isMinorBody, bool minorChildrenArePlanets)
    {
        return isMinorBody && !minorChildrenArePlanets;
    }

    // The order of the system roots: the home system first, then the others nearest to it, with
    // the Id as a stable tie-breaker.
    public static int CompareRoots(bool aIsHome, double aDistanceSquared, string aId, bool bIsHome, double bDistanceSquared, string bId)
    {
        if (aIsHome != bIsHome)
            return aIsHome ? -1 : 1;
        int byDistance = aDistanceSquared.CompareTo(bDistanceSquared);
        return byDistance != 0 ? byDistance : string.CompareOrdinal(aId, bId);
    }

    // The index of the star with the largest positive finite mass, or -1 when there is none.
    // Equal masses go to the ordinal-smaller Id, so the order of the list never changes the pick.
    public static int MostMassive(IReadOnlyList<(string Id, double Mass)> stars)
    {
        int best = -1;
        for (int i = 0; i < stars.Count; i++)
        {
            (string id, double mass) = stars[i];
            if (!(mass > 0.0) || !double.IsFinite(mass))
                continue;
            if (best < 0)
            {
                best = i;
                continue;
            }
            double bestMass = stars[best].Mass;
            if (mass > bestMass || (mass == bestMass && string.CompareOrdinal(id, stars[best].Id) < 0))
                best = i;
        }
        return best;
    }

    // The star the stock interstellar planner should get for a target, because stock lists only
    // stars as destinations. The nearest star at or above the target comes first, so a star hub
    // or its parking orbit is exactly that star and a planet or moon is the star it orbits. A
    // target with no star above it (a barycenter, or a body that orbits one directly) gets the
    // most massive star anywhere under its root. Null when the root holds no star at all.
    public static T? DestinationStar<T>(T target, Func<T, T?> parentOf, Func<T, bool> isStar, Func<T, IReadOnlyList<T>> childrenOf, Func<T, (string Id, double Mass)> keyOf)
        where T : class
    {
        T root = target;
        for (T? n = target; n != null; n = parentOf(n))
        {
            if (isStar(n))
                return n;
            root = n;
        }

        var stars = new List<T>();
        var pending = new Stack<T>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            T node = pending.Pop();
            if (isStar(node))
                stars.Add(node);
            IReadOnlyList<T> children = childrenOf(node);
            for (int i = 0; i < children.Count; i++)
                pending.Push(children[i]);
        }

        var keys = new List<(string Id, double Mass)>(stars.Count);
        foreach (T star in stars)
            keys.Add(keyOf(star));
        int best = MostMassive(keys);
        return best >= 0 ? stars[best] : null;
    }
}
