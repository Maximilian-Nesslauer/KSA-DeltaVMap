using System.Collections.Generic;
using System.Globalization;
using DeltaVMap.Core;

namespace DeltaVMap.Mission;

internal enum MissionRowKind : byte { Readout, Status }

internal enum MissionTone : byte { Muted, Positive, Pending, Danger }

// A Status row carries its text in Key and leaves Value empty.
internal readonly record struct MissionRow(MissionRowKind Kind, string Key, string Value, MissionTone Tone);

// The member order of KSA.MissionSupply.
internal enum SupplyKind : byte { Air, Scrubber, Water, Food }

// The supply that runs out first in the zone that is shortest, as MissionSupplies.FindShortest
// picks it. Zone counts from 1.
internal readonly record struct SupplyVerdict(bool Short, SupplyKind Supply, double ShortBySeconds, int Zone, int ZoneCount);

// The member order of KSA.ProbeCorePower, with None for NoProbeCore. ProbePower.Max ranks the
// states by this order, as Vehicle.Max ranks ProbeCorePower in Vehicle.ScanProbeCore.
internal enum ProbeState : byte { None, Failed, SwitchedOff, StormStandby, Rebooting, Unpowered, Powered }

internal readonly record struct ProbeReading(ProbeState State, DrainKind Drain, double RemainingSeconds);

// The texts of the mission block. No text holds a percent sign, because the ImGui text calls read
// it as a format.
internal static class MissionText
{
    // The timing rows of one mission, top to bottom. Clears rows first.
    public static void TimeRows(in MissionTimes times, List<MissionRow> rows)
    {
        rows.Clear();
        switch (times.Status)
        {
            case MissionTimesStatus.Planned:
                if (times.OutSeconds > 0.0)
                    rows.Add(Readout("OUT", times.OutSeconds));
                if (times.StaySeconds > 0.0)
                    rows.Add(Readout("STAY", times.StaySeconds));
                if (times.ReturnWaitSeconds > 0.0 && times.BackSeconds > 0.0)
                    rows.Add(new MissionRow(MissionRowKind.Readout, "RETURN WINDOW",
                        Format.Duration(times.ReturnWaitSeconds) + " after arrival", MissionTone.Muted));
                if (times.BackSeconds > 0.0)
                    rows.Add(Readout("BACK", times.BackSeconds));
                rows.Add(Readout("TOTAL", times.TotalSeconds));
                // The one-second tolerance is the one stock's mission breakdown uses.
                if (times.StaySeconds > times.RequestedStaySeconds + 1.0)
                    rows.Add(Status("THE STAY WAITS FOR THE NEXT RETURN WINDOW", MissionTone.Muted));
                if (times.OutSeconds > 0.0)
                    rows.Add(Status(times.Interstellar ? "STRAIGHT LINE AT CRUISE SPEED" : "HOHMANN TRANSFER TIMES", MissionTone.Muted));
                break;
            case MissionTimesStatus.FlybyReturnUnavailable:
                rows.Add(Readout("OUT", times.OutSeconds));
                rows.Add(Status("A FLYBY CANNOT COME STRAIGHT BACK: PICK ORBIT, LAND OR ONE WAY", MissionTone.Pending));
                break;
            default:
                rows.Add(Status("NO MISSION TIME FOR THIS ROUTE", MissionTone.Muted));
                break;
        }
    }

    public static (string Text, MissionTone Tone) SupplyLine(in SupplyVerdict verdict)
    {
        if (!verdict.Short)
            return ("SUPPLIES ENOUGH", MissionTone.Positive);
        string text = SupplyName(verdict.Supply) + " SHORT BY " + Format.Duration(verdict.ShortBySeconds);
        if (verdict.ZoneCount > 1)
            text += ", ZONE " + verdict.Zone.ToString(CultureInfo.InvariantCulture);
        return (text, MissionTone.Danger);
    }

    // missionSeconds is the mission time to compare against, null when there is none.
    public static (string Text, MissionTone Tone) ProbeLine(in ProbeReading reading, double? missionSeconds)
    {
        switch (reading.State)
        {
            case ProbeState.None:
                return ("", MissionTone.Muted);
            case ProbeState.Failed:
                return ("PROBE CORE FAILED", MissionTone.Danger);
            case ProbeState.SwitchedOff:
                return ("PROBE CORE SWITCHED OFF", MissionTone.Pending);
            case ProbeState.StormStandby:
                return ("PROBE CORE IN STORM STANDBY", MissionTone.Pending);
            case ProbeState.Rebooting:
                return ("PROBE CORE REBOOTING", MissionTone.Pending);
            case ProbeState.Unpowered:
                return ("PROBE CORE UNPOWERED", MissionTone.Danger);
        }

        switch (reading.Drain)
        {
            case DrainKind.NoBattery:
                return ("PROBE CORE HAS NO BATTERY, IT LIVES ON ITS POWER SUPPLY", MissionTone.Pending);
            case DrainKind.Empty:
                return ("PROBE BATTERIES EMPTY", MissionTone.Danger);
        }
        if (reading.Drain == DrainKind.NotDraining || double.IsPositiveInfinity(reading.RemainingSeconds))
            return ("PROBE POWER NOT DRAINING", MissionTone.Positive);

        string lasts = "PROBE POWER LASTS " + Format.Duration(reading.RemainingSeconds);
        if (missionSeconds is not double mission)
            return (lasts, MissionTone.Muted);
        if (reading.RemainingSeconds < mission)
            return (lasts + " OF " + Format.Duration(mission), MissionTone.Danger);
        return (lasts, MissionTone.Positive);
    }

    // ASCII names, because stock's ZoneMission.FirstName writes CO2 with a subscript digit.
    public static string SupplyName(SupplyKind kind)
    {
        return kind switch
        {
            SupplyKind.Air => "AIR",
            SupplyKind.Scrubber => "CO2 SCRUBBER",
            SupplyKind.Water => "WATER",
            _ => "FOOD",
        };
    }

    // One line for a mission goal: "LUNA, LAND, RETURN", "BACK TO EARTH" or, for the stock LOW
    // ORBIT mission (lowOrbit), "EARTH LOW ORBIT, RETURN". arrival is the arrival of the plan,
    // which a route that fell back to another node of the body does not show.
    public static string Summary(in MissionRouteGoal goal, RouteArrival arrival, bool lowOrbit)
    {
        string body = (goal.BodyId ?? "").ToUpperInvariant();
        if (goal.WayHome)
            return "BACK TO " + body;
        string trip = goal.Return ? "RETURN" : "ONE WAY";
        if (lowOrbit)
            return body + " LOW ORBIT, " + trip;
        string how = arrival switch
        {
            RouteArrival.Flyby => "FLYBY",
            RouteArrival.Land => "LAND",
            _ => "ORBIT",
        };
        return body + ", " + how + ", " + trip;
    }

    private static MissionRow Readout(string key, double seconds)
    {
        return new MissionRow(MissionRowKind.Readout, key, Format.Duration(seconds), MissionTone.Muted);
    }

    private static MissionRow Status(string text, MissionTone tone)
    {
        return new MissionRow(MissionRowKind.Status, text, "", tone);
    }
}
