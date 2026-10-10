using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeltaVMap.Model;

// The concrete type of a minor body, for the group noun.
internal enum MinorKind
{
    Other,
    Asteroid,
    Comet
}

// One child of a hub as the fold sees it. Included is the visibility verdict (the toggles and
// isolate), Revealed marks a searched or picked body, and OrbitRadius is the transfer radius
// around the hub in meters (the perihelion for an open orbit). OrbitRadius is read only for the
// minor children of a hub that folds by band.
internal readonly record struct FoldChild(bool Included, bool IsMinor, bool Revealed, MinorKind Kind, double OrbitRadius);

// Where one child of a hub ends up.
internal enum FoldSlot
{
    Hidden,
    Lane,
    Group,
    InnerGroup,
    OuterGroup
}

// Which children of a hub get a lane of their own and which fold into a "+N" group. Pure, so the
// rules are unit-tested offline.
//
// A hub folds only when it carries more included minor children than the threshold, so a
// stock-sized system keeps one lane per body. A folded star or barycenter puts every minor child
// into one group, as a dense asteroid belt needs. A folded planet or moon keeps its inner minor
// moons (orbit radius below InnerBandSoiFraction of its SOI) as lanes, innermost first and at
// most threshold of them, and folds the rest into an inner and an outer group. In a real moon
// system the inner band holds the large regular moons, while the outer swarm of small captured
// moons would make the hub tens of thousands of pixels wide as lanes. A revealed child is always
// a lane and does not count against that budget.
internal static class MinorFold
{
    public const double InnerBandSoiFraction = 0.1;

    // hubSoi is the hub's SOI radius in meters, or null for a star or barycenter hub, which
    // folds into one group without bands. Returns one slot per child, in the children's order.
    public static FoldSlot[] Plan(IReadOnlyList<FoldChild> children, int threshold, double? hubSoi)
    {
        var slots = new FoldSlot[children.Count];
        int minorCount = 0;
        for (int i = 0; i < children.Count; i++)
        {
            FoldChild c = children[i];
            slots[i] = c.Included ? FoldSlot.Lane : FoldSlot.Hidden;
            if (c.Included && c.IsMinor)
                minorCount++;
        }
        if (minorCount <= threshold)
            return slots;

        if (hubSoi is not double soi)
        {
            for (int i = 0; i < children.Count; i++)
            {
                if (IsFoldable(children[i]))
                    slots[i] = FoldSlot.Group;
            }
            return UnfoldSingles(slots);
        }

        double edge = InnerBandSoiFraction * soi;
        var order = new List<int>(children.Count);
        for (int i = 0; i < children.Count; i++)
        {
            if (IsFoldable(children[i]))
                order.Add(i);
        }
        order.Sort((a, b) =>
        {
            int byRadius = children[a].OrbitRadius.CompareTo(children[b].OrbitRadius);
            return byRadius != 0 ? byRadius : a.CompareTo(b);
        });

        int budget = threshold;
        foreach (int i in order)
        {
            if (children[i].OrbitRadius >= edge)
                slots[i] = FoldSlot.OuterGroup;
            else if (budget > 0)
                budget--;
            else
                slots[i] = FoldSlot.InnerGroup;
        }
        return UnfoldSingles(slots);
    }

    // The "+N" label, e.g. "+2892 asteroids" or "+249 outer moons". A group of only asteroids or
    // only comets names them; otherwise a band group names its band and the one group of a star
    // hub says "minor bodies". A group always holds at least two members, so the plural is right.
    public static string Label(FoldSlot slot, IReadOnlyList<MinorKind> members)
    {
        return "+" + members.Count.ToString(CultureInfo.InvariantCulture) + " " + Noun(slot, members);
    }

    // The group node's Id suffix after the hub's body Id, unique per hub.
    public static string IdSuffix(FoldSlot slot)
    {
        return slot switch
        {
            FoldSlot.Group => ".MinorGroup",
            FoldSlot.InnerGroup => ".MinorGroup.Inner",
            FoldSlot.OuterGroup => ".MinorGroup.Outer",
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "not a group slot")
        };
    }

    private static string Noun(FoldSlot slot, IReadOnlyList<MinorKind> members)
    {
        bool allAsteroid = true;
        bool allComet = true;
        foreach (MinorKind kind in members)
        {
            allAsteroid &= kind == MinorKind.Asteroid;
            allComet &= kind == MinorKind.Comet;
        }
        if (allAsteroid)
            return "asteroids";
        if (allComet)
            return "comets";
        return slot switch
        {
            FoldSlot.InnerGroup => "inner moons",
            FoldSlot.OuterGroup => "outer moons",
            _ => "minor bodies"
        };
    }

    private static bool IsFoldable(FoldChild c)
    {
        return c.Included && c.IsMinor && !c.Revealed;
    }

    // A group of one would read "+1", so its single member keeps its lane instead.
    private static FoldSlot[] UnfoldSingles(FoldSlot[] slots)
    {
        foreach (FoldSlot group in new[] { FoldSlot.Group, FoldSlot.InnerGroup, FoldSlot.OuterGroup })
        {
            int last = -1;
            int count = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == group)
                {
                    last = i;
                    count++;
                }
            }
            if (count == 1)
                slots[last] = FoldSlot.Lane;
        }
        return slots;
    }
}
