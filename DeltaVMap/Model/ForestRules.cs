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
}
