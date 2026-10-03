using System;

namespace DeltaVMap.Dv;

// One staged sequence as the game's staged model reports it: the delta-v it gives, its mass at
// ignition and the propellant it burns.
internal readonly struct StageMass
{
    public readonly double DeltaV;
    public readonly double WetMass;
    public readonly double BurnedMass;

    public StageMass(double deltaV, double wetMass, double burnedMass)
    {
        DeltaV = deltaV;
        WetMass = wetMass;
        BurnedMass = burnedMass;
    }
}

// The propellant a delta-v costs on a staged vessel. Needed is the propellant burned, Available
// the propellant the staged sequences can burn. When the delta-v exceeds what the stages give,
// ShortfallDv is the missing part and ExtraPropellant the propellant the last burning stage would
// need on top of its load to pay it with its own exhaust velocity. That is a lower bound, because
// the earlier stages would also have to lift the extra load. Log10MassRatio is the base-10
// logarithm of the wet-to-dry mass ratio that last stage would then need. It stays finite where
// ExtraPropellant overflows (an interstellar delta-v on a chemical stage needs a ratio far above
// the range of a double), so the caller can still say how far out of reach the trip is.
internal readonly struct PropellantNeed
{
    public readonly double Needed;
    public readonly double Available;
    public readonly double ShortfallDv;
    public readonly double ExtraPropellant;
    public readonly double Log10MassRatio;

    public PropellantNeed(double needed, double available, double shortfallDv, double extraPropellant, double log10MassRatio)
    {
        Needed = needed;
        Available = available;
        ShortfallDv = shortfallDv;
        ExtraPropellant = extraPropellant;
        Log10MassRatio = log10MassRatio;
    }

    public bool Feasible => !(ShortfallDv > 0.0);
}

// Rocket-equation propellant estimate over the staged sequences, the fallback fuel model for a
// vessel without an interstellar drive. Pure, so it is unit-tested offline.
internal static class RocketPropellant
{
    // The share of a sequence's propellant that delivers dv of its DeltaV, from the rocket
    // equation: with mass ratio q = (wet - burned) / wet over the whole sequence, the burned
    // fraction after dv is (1 - q^(dv / DeltaV)) / (1 - q). The same rule as the game's
    // TransferPlanner.BurnedShare, falling back to a linear share for a degenerate sequence.
    public static double BurnedShare(in StageMass stage, double dv)
    {
        if (dv >= stage.DeltaV)
            return 1.0;
        if (!(dv > 0.0))
            return 0.0;
        double q = (stage.WetMass - stage.BurnedMass) / stage.WetMass;
        if (!(q > 0.0) || !(q < 1.0))
            return dv / stage.DeltaV;
        return (1.0 - Math.Pow(q, dv / stage.DeltaV)) / (1.0 - q);
    }

    // Walk the stages in firing order and sum the propellant that pays dv. Stages that give no
    // delta-v are skipped, as the staged model fires them without thrust.
    public static PropellantNeed Needed(ReadOnlySpan<StageMass> stages, double dv)
    {
        double remaining = Math.Max(0.0, dv);
        double needed = 0.0;
        double available = 0.0;
        int last = -1;
        for (int i = 0; i < stages.Length; i++)
        {
            ref readonly StageMass stage = ref stages[i];
            if (!(stage.DeltaV > 0.0))
                continue;
            available += stage.BurnedMass;
            last = i;
            if (remaining > 0.0)
            {
                needed += stage.BurnedMass * BurnedShare(in stage, Math.Min(remaining, stage.DeltaV));
                remaining -= stage.DeltaV;
            }
        }

        if (!(remaining > 0.0))
            return new PropellantNeed(needed, available, 0.0, 0.0, 0.0);

        if (last < 0)
            return new PropellantNeed(double.PositiveInfinity, available, remaining, double.PositiveInfinity, double.PositiveInfinity);
        double extra = ExtraPropellant(in stages[last], remaining);
        return new PropellantNeed(needed + extra, available, remaining, extra, Log10MassRatio(in stages[last], remaining));
    }

    // The base-10 logarithm of the mass ratio the stage needs to give extraDv more, computed in
    // log space so it does not overflow.
    public static double Log10MassRatio(in StageMass stage, double extraDv)
    {
        double dry = stage.WetMass - stage.BurnedMass;
        if (!(dry > 0.0) || !(stage.BurnedMass > 0.0) || !(stage.DeltaV > 0.0))
            return double.PositiveInfinity;
        double ve = stage.DeltaV / Math.Log(stage.WetMass / dry);
        return (stage.DeltaV + extraDv) / ve / Math.Log(10.0);
    }

    // Extra propellant for the last stage to give extraDv more: with dry mass m_f = wet - burned
    // and exhaust velocity ve = DeltaV / ln(wet / m_f), the stage must start at
    // m_f * exp((DeltaV + extraDv) / ve), so the added load is that minus the current wet mass.
    public static double ExtraPropellant(in StageMass stage, double extraDv)
    {
        double dry = stage.WetMass - stage.BurnedMass;
        if (!(dry > 0.0) || !(stage.BurnedMass > 0.0) || !(stage.DeltaV > 0.0))
            return double.PositiveInfinity;
        double ve = stage.DeltaV / Math.Log(stage.WetMass / dry);
        return dry * Math.Exp((stage.DeltaV + extraDv) / ve) - stage.WetMass;
    }
}
