using System;

namespace DeltaVMap.Dv;

// How the interstellar cruise speed is chosen. Preset holds one of the fixed fractions of c,
// Custom the speed the slider was dragged to, and Max the fastest cruise the vessel's delta-v
// pays for, which follows that budget while it changes during a burn.
internal enum SpeedMode
{
    Preset,
    Custom,
    Max
}

// The speed a choice resolves to, and whether MAX could not reach even the cheapest trip.
internal readonly record struct CruiseChoice(double Speed, bool CannotPayCheapest);

// The cruise-speed selection of the interstellar section, free of game types so the unit tests
// cover it. MAX is a stored mode rather than a speed that happens to equal the fastest cruise,
// so it stays selected while the budget moves.
internal sealed class CruiseSpeed
{
    public static readonly double[] PresetFractions = { 0.001, 0.01, 0.1 };

    public const int DefaultPreset = 1;

    public SpeedMode Mode { get; private set; } = SpeedMode.Preset;
    public int Preset { get; private set; } = DefaultPreset;

    // The speed of the Custom mode in m/s, also kept while another mode is active, so a drive
    // vessel that loses MAX falls back to the speed it had.
    public double CustomSpeed { get; private set; } = PresetFractions[DefaultPreset] * InterstellarMath.SpeedOfLight;

    public void SelectPreset(int index)
    {
        Mode = SpeedMode.Preset;
        Preset = Math.Clamp(index, 0, PresetFractions.Length - 1);
    }

    public void SetCustom(double speed)
    {
        Mode = SpeedMode.Custom;
        CustomSpeed = speed;
    }

    public void SelectMax()
    {
        Mode = SpeedMode.Max;
    }

    // MAX exists only for a vessel without an antimatter drive (the drive model plans that one)
    // and with a delta-v reading. Without it, a MAX choice becomes a custom speed at the speed it
    // last resolved to.
    public void DropMaxIfUnavailable(bool maxAvailable, double currentSpeed)
    {
        if (Mode == SpeedMode.Max && !maxAvailable)
            SetCustom(currentSpeed);
    }

    // The cruise speed for the current mode, at most cap. In Max mode it is the fastest cruise
    // maxSpeed when the budget pays for the cheapest trip, and otherwise the cheapest speed, with
    // the flag set so the panel can say why. The cheapest trip can lie at a cruise speed near
    // zero, a coast that never arrives, so that fallback is held at floor, the slowest speed the
    // slider offers.
    public CruiseChoice Resolve(double maxSpeed, double cheapestSpeed, double cap, double floor = 0.0)
    {
        return Mode switch
        {
            SpeedMode.Preset => new CruiseChoice(Math.Min(PresetFractions[Preset] * InterstellarMath.SpeedOfLight, cap), false),
            SpeedMode.Max => maxSpeed > 0.0
                ? new CruiseChoice(Math.Min(maxSpeed, cap), false)
                : new CruiseChoice(Math.Clamp(cheapestSpeed, Math.Min(floor, cap), cap), true),
            _ => new CruiseChoice(Math.Clamp(CustomSpeed, 0.0, cap), false)
        };
    }
}
