using System;
using System.Collections.Generic;
using Brutal.ImGuiApi;
using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Mission;
using DeltaVMap.Route;
using KSA;

namespace DeltaVMap.Render;

// What the map knows about the mission of the craft it plans for. Goal is the route the mission
// asks for. GoalEndOnMap is the end of the node the map resolved for the goal, null when it has
// none. GoalOnMap is true when the map can select the goal, now or after it reveals the body.
// GoalBodyKnown is true when the loaded universe holds the mission body.
internal readonly record struct MissionContext(
    MissionHost? Host,
    MissionSnapshot? Mission,
    MissionRouteGoal? Goal,
    RouteEnd? GoalEndOnMap,
    bool GoalOnMap,
    bool GoalBodyKnown);

// The mission block of the route panel: the vessel's mission or a button that selects its route,
// the mission time of the selected route through stock's mission timing, the stay at the
// destination, and a button that writes the route into the vessel's mission. Below the vehicle
// bar it adds one line, the supplies aboard against the mission time for a crewed craft, or the
// probe core power of an uncrewed vessel in flight.
//
// Update runs every frame before the panel draws. The timing is computed again only when the
// route, the arrival, the trip, the stay or the cruise speed changes, and the supply and power
// readers cache and throttle their own reads. The strings are built when what they show changes.
internal sealed class MissionSection
{
    private const double SecondsPerDay = 86400.0;
    // The step the panel's cruise speed is rounded to, in fractions of c, for the timing key and
    // for the speed stock times with, so the key always holds the speed that was timed. A finer
    // change does not move a mission time by a shown digit.
    private const double CruiseKeyStep = 1e-6;
    // Two cruise speeds closer than this, relative, are the same plan.
    private const double CruiseMatchTolerance = 1e-6;

    private const string MissionRouteTooltip =
        "Select the route of this vessel's mission: its destination, its arrival and its trip.";
    private const string ReturnTooltip =
        "Counts the trip home in the mission time. The delta-v of an interstellar route stays one way.";
    private const string StayTooltip =
        "How long the vessel stays at the destination. A return waits at least for the next return window.";
    private const string SuppliesTooltip =
        "Air, water, food and CO2 scrubbers aboard now against the whole mission time of this route from where "
        + "the vessel is now, out, stay and back, for the crew planned on each leg (the crew plan of the vehicle "
        + "editor, else the kittens seated). It uses the stock life support forecast. The stock windows time a "
        + "mission in flight from its launch at the home body, so their figures can differ.";
    private const string ProbeTooltip =
        "A snapshot of the power balance now: the charge stored aboard over the draw minus the supply. "
        + "Sunlight, attitude and the loads switched on change it. Every battery aboard counts.";

    private const string WriteTooltipBase =
        "Write this route into the vessel's mission: destination, arrival, trip and stay, and the cruise speed "
        + "of an interstellar route. With the Life Support rule on, life support, the launch check and the stock "
        + "Transfer Planner plan with it.";
    private const string WriteTooltipEditor = " It is one undo step.";
    private const string WriteTooltipFlight =
        " In flight it replaces all of these at once, while the stock UPDATE MISSION button changes only the "
        + "destination. It also clears the arrival the mission has recorded; stock records it again at once if the "
        + "vessel is already there.";
    private const string WriteTooltipHome = " The home body is the stock LOW ORBIT mission.";
    private static readonly string[] WriteTooltips =
    {
        WriteTooltipBase + WriteTooltipFlight,
        WriteTooltipBase + WriteTooltipFlight + WriteTooltipHome,
        WriteTooltipBase + WriteTooltipEditor,
        WriteTooltipBase + WriteTooltipEditor + WriteTooltipHome,
    };

    private static readonly string SlowCruiseText = FormattableString.Invariant(
        $"MISSION TIME AT {MissionRoutes.MinCruiseSpeedC} C, THE SLOWEST CRUISE A STOCK MISSION PLANS");

    private enum FooterKind : byte { None, Supplies, Probe }

    private readonly record struct TimingKey(
        RouteSummary Summary,
        RouteArrival Arrival,
        bool ReturnTrip,
        double StaySeconds,
        double CruiseSpeedC,
        bool InTransit);

    // Tree compares by reference, because PartTree does not override Equals.
    private readonly record struct StaySeed(object? Tree, bool HasPlan, double PlanStaySeconds);

    private readonly record struct FooterKey(
        FooterKind Kind,
        int State,
        int Detail,
        long Seconds,
        long MissionSeconds,
        int Zone,
        int ZoneCount);

    private readonly record struct MissionLineKey(MissionRouteGoal Goal, RouteArrival Arrival, bool LowOrbit);

    private RouteOptions? _options;
    private RouteSummary? _summary;
    private MissionContext _context;
    private bool _shown;
    private bool _interstellar;
    private bool _inTransit;
    private RouteEnd _end;
    private double _cruiseC = double.NaN;

    // The STAY input. It starts at the stay of the vessel's plan and keeps the player's value
    // until another craft or another planned stay comes in.
    private double _staySeconds = MissionRoutes.DefaultStaySeconds;
    private StaySeed? _staySeed;

    private TimingKey? _timingKey;
    private MissionTimes _times = MissionTimes.Unknown;
    private readonly List<MissionRow> _rows = new();

    private FooterKind _footerKind;
    private FooterKey? _footerKey;
    private string _footerText = "";
    private MissionTone _footerTone;

    private MissionLineKey? _missionLineKey;
    private string _missionLine = "";
    private (string BodyId, bool Known)? _noteKey;
    private string _noteText = "";

    // A route with a mission time, so the totals leave out their transfer time line.
    public bool HasTiming => _shown && _times.Status != MissionTimesStatus.Unknown;

    // A supplies or probe power line under the vehicle bar.
    public bool HasFooterLine => _shown && _footerKind != FooterKind.None;

    public bool IsMissionRoute { get; private set; }

    // MISSION ROUTE was clicked this frame.
    public bool MissionRouteRequested { get; private set; }

    // The plan SET AS MISSION asked for this frame. MapWindow has WriteRequestedMission write it
    // after the panel draw, as it applies MISSION ROUTE there.
    private (MissionHost Host, MissionWrite Plan)? _writeRequest;

    // The RETURN TRIP row of an interstellar route changed the route options this frame.
    public bool OptionsChanged { get; private set; }

    public void Reset()
    {
        _options = null;
        _context = default;
        _staySeed = null;
        _timingKey = null;
        _times = MissionTimes.Unknown;
        _rows.Clear();
        _missionLineKey = null;
        _noteKey = null;
        MissionRouteRequested = false;
        _writeRequest = null;
        OptionsChanged = false;
        ClearRoute();
    }

    // Writes the plan SET AS MISSION asked for this frame. True when it wrote one.
    public bool WriteRequestedMission()
    {
        if (_writeRequest is not { } request)
            return false;
        _writeRequest = null;
        return MissionWriter.Write(in request.Host, in request.Plan);
    }

    public void Update(RouteSummary? summary, RouteOptions options, InterstellarSection? leg, in MissionContext context)
    {
        MissionRouteRequested = false;
        _writeRequest = null;
        OptionsChanged = false;
        _options = options;
        _context = context;
        try
        {
            SeedStay(in context);
            if (summary == null || summary.IsEmpty)
            {
                ClearRoute();
                return;
            }
            UpdateRoute(summary, options, leg, in context);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("mission-panel", $"[DvMap] Mission panel update failed: {ex}");
            ClearRoute();
        }
    }

    private void UpdateRoute(RouteSummary summary, RouteOptions options, InterstellarSection? leg, in MissionContext context)
    {
        _summary = summary;
        _shown = true;
        _interstellar = summary.Interstellar != null;
        _inTransit = summary.Interstellar is { InTransit: true };
        _end = MissionRouteResolver.EndOf(summary.Target.Kind);
        _cruiseC = _interstellar && leg != null ? leg.CurrentCruiseSpeed / InterstellarMath.SpeedOfLight : double.NaN;

        bool wayHome = context.Goal is { WayHome: true };
        IsMissionRoute = MissionRoutes.IsMissionRoute(context.Goal, context.GoalEndOnMap, summary.Target.Body.Id, _end, options.ShowReturnTrip)
            && CruiseMatches(context.Mission);

        // On the mission's route the timing takes the plan's arrival and stay, even where the map
        // fell back to another node of the body. The legs still start where the vessel is now. The
        // way home has no stay, as stock's Home leg has none.
        RouteArrival arrival = MissionRoutes.ArrivalOf(_end);
        double stay = _staySeconds;
        if (IsMissionRoute && wayHome)
        {
            stay = 0.0;
        }
        else if (IsMissionRoute && context.Mission is { } mission)
        {
            arrival = mission.Arrival;
            stay = mission.StaySeconds;
        }

        double cruiseKey = double.IsNaN(_cruiseC) ? double.NaN : Math.Round(_cruiseC / CruiseKeyStep) * CruiseKeyStep;
        var key = new TimingKey(summary, arrival, options.ShowReturnTrip, stay, cruiseKey, _inTransit);
        if (_timingKey != key)
        {
            _timingKey = key;
            // Stock's interstellar timing is a straight line from star to star from the start and
            // does not know the coast left to a vessel in transit, which the coast time line of
            // the totals gives.
            _times = _inTransit
                ? MissionTimes.Unknown
                : MissionTimingBridge.Compute(summary.Origin.Body, summary.Target.Body, arrival, options.ShowReturnTrip, stay, cruiseKey);
            MissionText.TimeRows(in _times, _rows);
        }

        UpdateFooter(in context);
    }

    // The panel's cruise speed and the plan's are the same plan, or there is nothing to compare.
    private bool CruiseMatches(MissionSnapshot? mission)
    {
        if (!_interstellar || _inTransit || mission is not { } m || double.IsNaN(_cruiseC) || double.IsNaN(m.CruiseSpeedC))
            return true;
        double ours = Math.Clamp(_cruiseC, MissionRoutes.MinCruiseSpeedC, MissionRoutes.MaxCruiseSpeedC);
        double plan = Math.Clamp(m.CruiseSpeedC, MissionRoutes.MinCruiseSpeedC, MissionRoutes.MaxCruiseSpeedC);
        return Math.Abs(ours - plan) <= CruiseMatchTolerance * plan;
    }

    private void SeedStay(in MissionContext context)
    {
        var seed = new StaySeed(context.Host?.Tree, context.Mission.HasValue, context.Mission?.StaySeconds ?? 0.0);
        if (_staySeed == seed)
            return;
        _staySeed = seed;
        _staySeconds = MissionRoutes.NormalizeStay(context.Mission?.StaySeconds ?? double.NaN);
    }

    private void ClearRoute()
    {
        _shown = false;
        _summary = null;
        IsMissionRoute = false;
        _footerKind = FooterKind.None;
        _footerKey = null;
    }

    // The supplies of a crewed craft, else the probe core power of an uncrewed vessel in flight.
    // A crewed vessel with the life support rule off shows neither.
    private void UpdateFooter(in MissionContext context)
    {
        if (context.Host is not { } host)
        {
            SetFooter(FooterKind.None, default);
            return;
        }

        if (MissionSupplyCheck.Read(in host, in _times) is { } verdict)
        {
            var key = new FooterKey(FooterKind.Supplies, verdict.Short ? 1 : 0, (int)verdict.Supply,
                Format.DurationKey(verdict.ShortBySeconds), 0, verdict.Zone, verdict.ZoneCount);
            if (_footerKey != key)
            {
                (_footerText, _footerTone) = MissionText.SupplyLine(in verdict);
                SetFooter(FooterKind.Supplies, key);
            }
            return;
        }

        if (!host.InEditor && host.Vehicle is { } vehicle && IsUncrewed(vehicle)
            && ProbePower.Read(vehicle) is { State: not ProbeState.None } reading)
        {
            double? missionSeconds = _times.Status == MissionTimesStatus.Planned ? _times.TotalSeconds : null;
            var key = new FooterKey(FooterKind.Probe, (int)reading.State, (int)reading.Drain,
                Format.DurationKey(reading.RemainingSeconds),
                missionSeconds is double s ? Format.DurationKey(s) : long.MaxValue, 0, 0);
            if (_footerKey != key)
            {
                (_footerText, _footerTone) = MissionText.ProbeLine(in reading, missionSeconds);
                SetFooter(FooterKind.Probe, key);
            }
            return;
        }

        SetFooter(FooterKind.None, default);
    }

    private void SetFooter(FooterKind kind, FooterKey key)
    {
        _footerKind = kind;
        _footerKey = kind == FooterKind.None ? null : key;
    }

    private static bool IsUncrewed(Vehicle vehicle)
    {
        ReadOnlySpan<IVASeat> seats = vehicle.Crew;
        for (int i = 0; i < seats.Length; i++)
        {
            if (seats[i].AssignedKittenHash != KeyHash.Zero)
                return false;
        }
        return true;
    }

    // Under the "Click a body" text when nothing is selected.
    public void DrawNoRoute()
    {
        try
        {
            if (_context.Goal is not { } goal)
                return;
            if (_context.GoalOnMap)
                DrawMissionRouteButton();
            else
                ConsoleUi.StatusText(NoteText(goal));
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("mission-panel-draw-none", $"[DvMap] Mission panel draw without a route failed: {ex}");
        }
    }

    public void DrawBody()
    {
        if (!_shown || _summary == null || _options == null)
            return;
        try
        {
            DrawBodyRows(_summary, _options);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("mission-panel-draw", $"[DvMap] Mission panel draw failed: {ex}");
        }
    }

    private void DrawBodyRows(RouteSummary summary, RouteOptions options)
    {
        ImGui.Separator();
        ConsoleWidgets.RegionHeader("MISSION".AsSpan());

        if (_context.Goal is { } goal)
        {
            if (IsMissionRoute)
                ConsoleUi.StatusText(MissionLine(goal), StatusTone.Positive);
            else if (_context.GoalOnMap)
                DrawMissionRouteButton();
            else
                ConsoleUi.StatusText(NoteText(goal));
        }

        // The route options of an interstellar route hide the return toggle, because its delta-v
        // stays one way, so the mission time gets its own row here.
        if (_interstellar)
        {
            if (ConsoleUi.CheckboxRow("RETURN TRIP".AsSpan(), "DvmMissionReturn".AsSpan(), ref options.ShowReturnTrip))
                OptionsChanged = true;
            ConsoleWidgets.ItemTooltip(ReturnTooltip.AsSpan());
        }

        // Stock's mission picker shows the stay whenever the arrival is not a flyby, one way too,
        // and its timing counts it.
        if (!IsMissionRoute && _end != RouteEnd.Flyby)
            DrawStayRow();

        if (_interstellar && !_inTransit && _cruiseC < MissionRoutes.MinCruiseSpeedC)
            ConsoleUi.StatusText(SlowCruiseText);

        DrawRows();
        DrawWriteRow(summary, options);
    }

    private void DrawMissionRouteButton()
    {
        if (ConsoleWidgets.Button("MISSION ROUTE".AsSpan()))
            MissionRouteRequested = true;
        ConsoleWidgets.ItemTooltip(MissionRouteTooltip.AsSpan());
    }

    private void DrawStayRow()
    {
        ConsoleWidgets.BeginRow("STAY AT DESTINATION".AsSpan());
        double days = _staySeconds / SecondsPerDay;
        ImGui.SetNextItemWidth(-1f);
        bool changed = ImGui.InputDouble("##DvmStay"u8, ref days, 0.0, 0.0, "%.1f d"u8);
        ConsoleWidgets.ItemTooltip(StayTooltip.AsSpan());
        ConsoleWidgets.EndRow();
        if (changed && double.IsFinite(days))
            _staySeconds = Math.Max(0.0, days) * SecondsPerDay;
    }

    private void DrawRows()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            MissionRow row = _rows[i];
            if (row.Kind == MissionRowKind.Readout)
                ConsoleWidgets.Readout(row.Key.AsSpan(), row.Value.AsSpan());
            else
                ConsoleUi.StatusText(row.Key, ToneOf(row.Tone));
        }
    }

    // The button shows only for a route a stock mission can name, and says why otherwise.
    private void DrawWriteRow(RouteSummary summary, RouteOptions options)
    {
        if (IsMissionRoute || _context.Host is not { } host)
            return;
        Astronomical target = summary.Target.Body;
        IParentBody? home = MissionReader.HomeBody();
        string? refusal = MissionWriter.RefusalFor(target, _end, home);
        if (refusal != null)
        {
            ConsoleUi.StatusText(refusal);
            return;
        }

        if (ConsoleWidgets.Button("SET AS MISSION".AsSpan()))
            _writeRequest = (host, MissionRoutes.PlanFor(target.Id, home?.Id, _end, options.ShowReturnTrip, _interstellar, _staySeconds, _cruiseC));
        ConsoleWidgets.ItemTooltip(WriteTooltip(host.InEditor, ReferenceEquals(target, home)).AsSpan());
    }

    private static string WriteTooltip(bool inEditor, bool isHome) => WriteTooltips[(inEditor ? 2 : 0) + (isHome ? 1 : 0)];

    // The supplies or probe power line under the vehicle bar, one line in the value font.
    public void DrawFooterLine()
    {
        if (!HasFooterLine)
            return;
        ConsoleStyle.PushValueFont();
        switch (_footerTone)
        {
            case MissionTone.Positive:
                ImGui.TextColored(in ConsoleStyle.Positive, _footerText);
                break;
            case MissionTone.Pending:
                ImGui.TextColored(in ConsoleStyle.Pending, _footerText);
                break;
            case MissionTone.Danger:
                ImGui.TextColored(in ConsoleStyle.Danger, _footerText);
                break;
            default:
                ImGui.TextColored(in ConsoleStyle.TextMuted, _footerText);
                break;
        }
        ConsoleStyle.PopFont();
        ConsoleWidgets.ItemTooltip((_footerKind == FooterKind.Supplies ? SuppliesTooltip : ProbeTooltip).AsSpan());
    }

    private string MissionLine(in MissionRouteGoal goal)
    {
        MissionSnapshot? mission = _context.Mission;
        RouteArrival arrival = mission?.Arrival ?? MissionRoutes.ArrivalOf(goal.End);
        var key = new MissionLineKey(goal, arrival, mission is { Destination: MissionGoal.LowOrbit });
        if (_missionLineKey != key)
        {
            _missionLineKey = key;
            _missionLine = "THE VESSEL'S MISSION: " + MissionText.Summary(in goal, arrival, key.LowOrbit);
        }
        return _missionLine;
    }

    private string NoteText(in MissionRouteGoal goal)
    {
        (string BodyId, bool Known) key = (goal.BodyId ?? "", _context.GoalBodyKnown);
        if (_noteKey != key)
        {
            _noteKey = key;
            string id = key.BodyId.ToUpperInvariant();
            _noteText = key.Known
                ? "THE MAP HAS NO ROUTE TO THE MISSION BODY " + id
                : "THE MISSION GOES TO " + id + ", WHICH IS NOT IN THIS SYSTEM";
        }
        return _noteText;
    }

    private static StatusTone ToneOf(MissionTone tone)
    {
        return tone switch
        {
            MissionTone.Positive => StatusTone.Positive,
            MissionTone.Pending => StatusTone.Pending,
            MissionTone.Danger => StatusTone.Danger,
            _ => StatusTone.Muted,
        };
    }
}
