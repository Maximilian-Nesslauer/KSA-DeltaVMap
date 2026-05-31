using KSA;

namespace DeltaVMap.Model;

// One departure-window result for an ordered pair: from the map root (Source) to one
// sibling sharing the same hub (Target). Most fields are fixed for a given root because the
// Keplerian orbits do not change, so they are computed once when the root changes; only the
// current phase and the countdown move, and those two are refreshed every frame the section
// is open. The list holds these and the builder mutates the live fields in place, so this is
// a reference type rather than a value type.
internal sealed class TransferWindowInfo
{
    public required Astronomical Source { get; init; }
    public required Astronomical Target { get; init; }

    // Static per root (radians unless noted).
    // The optimal lead angle the target must have over the source at departure.
    public required double TargetPhaseAngle { get; init; }
    // The relative angular rate (rad/s); kept so the countdown can be recomputed without
    // re-reading the periods. Negative when the target is the slower (outer) body.
    public required double SynodicRate { get; init; }
    // How often the window recurs (seconds).
    public required double SynodicPeriodSeconds { get; init; }
    // Hohmann time of flight around the hub (seconds).
    public required double TransferTimeSeconds { get; init; }
    // Ejection angle from the parking-orbit prograde direction (radians, magnitude).
    public required double EjectionAngle { get; init; }
    // True when the burn sits ahead of prograde (outbound, target outer), false when behind.
    public required bool EjectionAhead { get; init; }
    // True when the sibling orbits the hub the opposite way (relative inclination over 90 deg).
    public required bool Retrograde { get; init; }
    // Relative inclination between the two orbits (radians), context only.
    public required double RelativeInclination { get; init; }
    // True when the lead angle is less reliable (an eccentric or open orbit at either end),
    // shown on top of the whole map's standing "~" estimate marker.
    public required bool IsApproximate { get; init; }

    // Live per frame.
    public double CurrentPhaseAngle { get; set; }
    public double TimeToWindowSeconds { get; set; }

    public string SourceId => Source.Id;
    public string TargetId => Target.Id;
}
