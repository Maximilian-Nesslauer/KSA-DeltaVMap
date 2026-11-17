namespace DeltaVMap.Mission;

internal enum MissionTimesStatus : byte { Unknown, FlybyReturnUnavailable, Planned }

// The legs of a mission in seconds, as KSA.MissionLegs gives them. StaySeconds is the stay rounded
// up to the next return window, RequestedStaySeconds the stay that was asked for (0 for a flyby).
internal readonly record struct MissionTimes(
    MissionTimesStatus Status,
    double OutSeconds,
    double StaySeconds,
    double BackSeconds,
    double ReturnWaitSeconds,
    double RequestedStaySeconds,
    bool Interstellar)
{
    public double TotalSeconds => OutSeconds + StaySeconds + BackSeconds;

    public static MissionTimes Unknown => new(MissionTimesStatus.Unknown, 0.0, 0.0, 0.0, 0.0, 0.0, false);
}
