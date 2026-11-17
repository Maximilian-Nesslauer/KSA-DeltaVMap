namespace DeltaVMap.Mission;

// The member order of KSA.PowerDrain.
internal enum DrainKind : byte { NotDraining, Draining, Empty, NoBattery }

internal static class PowerMath
{
    // The branches of PowerForecast.Compute, in its order: without a battery there is no
    // forecast, a supply that covers the draw never drains, an empty battery has nothing left, and
    // otherwise the charge lasts charge / (draw - supply). Stock takes draw - supply in float; this
    // mirror keeps double.
    public static (DrainKind Drain, double RemainingSeconds) Forecast(bool hasBattery, double chargeJoules, double drawWatts, double supplyWatts)
    {
        if (!hasBattery)
            return (DrainKind.NoBattery, 0.0);
        double net = drawWatts - supplyWatts;
        if (net <= 0.0)
            return (DrainKind.NotDraining, double.PositiveInfinity);
        if (chargeJoules <= 0.0)
            return (DrainKind.Empty, 0.0);
        return (DrainKind.Draining, chargeJoules / net);
    }
}
