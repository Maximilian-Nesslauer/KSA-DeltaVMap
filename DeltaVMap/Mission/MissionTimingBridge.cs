using System;
using DeltaVMap.Core;
using DeltaVMap.Model;
using KSA;

namespace DeltaVMap.Mission;

// The mission legs of a route through stock's own mission timing (MissionTiming.ComputeLegs and
// ComputeInterstellarLegs), so the map times a route with the formulas the stock mission planner
// and life support plan with.
internal static class MissionTimingBridge
{
    // launch and destination are the bodies of the route's two ends. staySeconds goes to stock as
    // it is, as MissionTiming.Compute passes the plan's stay. cruiseSpeedC is read only for a
    // route between two star systems.
    internal static MissionTimes Compute(Astronomical launch, Astronomical destination, RouteArrival arrival, bool returnTrip, double staySeconds, double cruiseSpeedC)
    {
        try
        {
            if (launch is not IParentBody from || destination is not IParentBody to)
                return MissionTimes.Unknown;
            bool interstellar = MissionTiming.IsInterstellar(from, to);
            // A star or barycenter at the start of a route is a vessel in cruise around it. Stock
            // times a mission from a launch body and would start from a parking orbit just above
            // the hub (OrbitalTransfers.SafeOrbitRadius) instead of the vessel's orbit, with no
            // return window, so the times would be far too short. The totals then keep the
            // transfer time from the cruise orbit.
            if (!interstellar && SystemGraph.IsHubOnlyBody(launch))
                return MissionTimes.Unknown;
            // Stock takes OrbitalTransfers.GetCircularRadius of the orbits on both parent chains,
            // which means nothing for an open orbit.
            if (!ChainIsBound(from) || !ChainIsBound(to))
                return MissionTimes.Unknown;

            MissionArrival gameArrival = MissionReader.ToGame(arrival);
            MissionTrip trip = returnTrip ? MissionTrip.Return : MissionTrip.OneWay;
            MissionLegs legs;
            if (interstellar)
            {
                if (double.IsNaN(cruiseSpeedC))
                    return MissionTimes.Unknown;
                legs = MissionTiming.ComputeInterstellarLegs(from, to, gameArrival, trip, staySeconds, cruiseSpeedC);
            }
            else
            {
                legs = MissionTiming.ComputeLegs(from, to, gameArrival, trip, staySeconds);
            }

            MissionTimesStatus status = legs.Status switch
            {
                MissionLegsStatus.Planned => MissionTimesStatus.Planned,
                MissionLegsStatus.FlybyReturnUnavailable => MissionTimesStatus.FlybyReturnUnavailable,
                _ => MissionTimesStatus.Unknown,
            };
            if (status == MissionTimesStatus.Unknown)
                return MissionTimes.Unknown;

            // The UniverseTime(double) constructor saturates an infinite time at the end or the
            // beginning of time, whose seconds are finite, so a saturated leg counts as no timing.
            if (legs.Out.IsSaturated() || legs.Stay.IsSaturated() || legs.Back.IsSaturated() || legs.ReturnWait.IsSaturated())
                return MissionTimes.Unknown;

            return new MissionTimes(status,
                legs.Out.Seconds(), legs.Stay.Seconds(), legs.Back.Seconds(), legs.ReturnWait.Seconds(),
                arrival == RouteArrival.Flyby ? 0.0 : staySeconds, interstellar);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("mission-timing", $"[DvMap] Mission timing failed: {ex}");
            return MissionTimes.Unknown;
        }
    }

    private static bool ChainIsBound(IParentBody body)
    {
        for (IParentBody? b = body; b is IOrbiter orbiter; b = orbiter.Parent)
        {
            if (orbiter.Orbit == null || !orbiter.Orbit.IsBound())
                return false;
        }
        return true;
    }
}
