using System;
using DeltaVMap.Core;
using KSA;

namespace DeltaVMap.Mission;

// The mission legs of a route through stock's own mission timing (MissionTiming.ComputeLegs and
// ComputeInterstellarLegs), so the map shows the times the stock mission planner and life support
// plan with.
internal static class MissionTimingBridge
{
    // launch and destination are the bodies of the route's two ends. cruiseSpeedC is read only for
    // a route between two star systems.
    internal static MissionTimes Compute(Astronomical launch, Astronomical destination, RouteArrival arrival, bool returnTrip, double staySeconds, double cruiseSpeedC)
    {
        try
        {
            if (launch is not IParentBody from || destination is not IParentBody to)
                return MissionTimes.Unknown;
            // Stock takes OrbitalTransfers.GetCircularRadius of the orbits on both parent chains,
            // which means nothing for an open orbit.
            if (!ChainIsBound(from) || !ChainIsBound(to))
                return MissionTimes.Unknown;

            double requested = double.IsFinite(staySeconds) ? Math.Max(0.0, staySeconds) : MissionRoutes.DefaultStaySeconds;
            MissionArrival gameArrival = MissionReader.ToGame(arrival);
            MissionTrip trip = returnTrip ? MissionTrip.Return : MissionTrip.OneWay;
            bool interstellar = MissionTiming.IsInterstellar(from, to);
            MissionLegs legs;
            if (interstellar)
            {
                if (double.IsNaN(cruiseSpeedC))
                    return MissionTimes.Unknown;
                legs = MissionTiming.ComputeInterstellarLegs(from, to, gameArrival, trip, requested, cruiseSpeedC);
            }
            else
            {
                legs = MissionTiming.ComputeLegs(from, to, gameArrival, trip, requested);
            }

            MissionTimesStatus status = legs.Status switch
            {
                MissionLegsStatus.Planned => MissionTimesStatus.Planned,
                MissionLegsStatus.FlybyReturnUnavailable => MissionTimesStatus.FlybyReturnUnavailable,
                _ => MissionTimesStatus.Unknown,
            };
            if (status == MissionTimesStatus.Unknown)
                return MissionTimes.Unknown;

            // MissionLegs.FromSeconds saturates an infinite time at UniverseTime.EndOfTime, whose
            // seconds are finite, so a saturated leg counts as no timing.
            if (legs.Out.IsSaturated() || legs.Stay.IsSaturated() || legs.Back.IsSaturated() || legs.ReturnWait.IsSaturated())
                return MissionTimes.Unknown;
            double outSeconds = legs.Out.Seconds();
            double stay = legs.Stay.Seconds();
            double back = legs.Back.Seconds();
            double wait = legs.ReturnWait.Seconds();
            if (!double.IsFinite(outSeconds) || !double.IsFinite(stay) || !double.IsFinite(back) || !double.IsFinite(wait))
                return MissionTimes.Unknown;

            return new MissionTimes(status, outSeconds, stay, back, wait,
                arrival == RouteArrival.Flyby ? 0.0 : requested, interstellar);
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
