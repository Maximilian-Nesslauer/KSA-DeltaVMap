using System;
using System.Collections.Generic;
using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Model;
using DeltaVMap.Route;
using KSA;

namespace DeltaVMap.Render;

// The interstellar leg of the selected route, priced at the cruise speed the player picks. The
// route accumulator folds the trip between two star systems into one InterstellarRoute; this
// section adds the departure out of the ego body's system, the coast and the capture at the
// destination body for that speed, and the route panel shows them inside the route: the
// breakdown lines, the totals and the vehicle bar include the leg, and the section draws the
// speed presets and sliders, its readouts, the fuel estimate and the stock planner button. A
// vessel with an antimatter ram drive gets the game's own interstellar burn model; any other
// vessel gets the rocket equation over its staged sequences.
//
// Nothing here relays out the map or recomputes the route. The leg geometry is cached per
// departure, target and (for a star or barycenter target) capture periapsis. The cheapest trip,
// a search over the cruise speed, is cached per leg and parking orbit, and the fastest cruise
// the staged delta-v pays for per leg, parking orbit and budget. While the periapsis slider is
// dragged, both searches keep their last result and rerun once on release, because every
// periapsis is a new leg. The figures and their strings
// are cached per speed, parking orbit, vessel state and margin, and the drive estimate (the
// game's burn integrator, a few thousand steps) per drive input set, the same caching the stock
// planner uses.
//
// The vessel's own state is latched. During a burn its orbit and its delta-v reading move by
// more than a readout step every few frames, so a new parking orbit, delta-v reading or staged
// revision is taken at most four times a second, the rate StagedDv reads the staged delta-v at.
// The searches always see the exact latched orbit and budget, so each refresh is exact and at
// most a quarter second old. A changed leg, a switch to another vessel, a vessel that enters or
// leaves the orbit, and a delta-v reading that appears or goes are taken at once, and so is every
// user input. The vessel's drive and propellant are re-read at most twice a second.
//
// A vessel that coasts on an open orbit around a system root (InterstellarRoute.InTransit) has
// no departure from a parking orbit. Its live hyperbolic excess, whether it still falls inward,
// and the direction it finally leaves along (InterstellarLegs.TryTransitState) are latched like
// the parking orbit. A capture in the system it coasts through is priced at that excess while
// it falls inward; once it climbs away there is no estimate. A trip on to another system is
// priced as the turn of the excess toward that system at the same speed, then the capture.
internal sealed class InterstellarSection
{
    // About 3 km/s. A chemical vessel cannot reach the stock planner's 0.001 c floor (300 km/s),
    // so the slider starts low enough to show what such a vessel can actually do.
    private const float MinSpeedC = 1e-5f;
    private const float MinPeriapsisAu = 0.01f;
    private const float MaxPeriapsisAu = 1000f;
    private const float DefaultAntimatterPercent = 30f;
    private const double VesselRefreshSeconds = 0.5;
    private const double VesselStateRefreshSeconds = 0.25;

    // The game caps interstellar speed at 0.9 c (OrbitalTransfers.MAX_INTERSTELLAR_SPEED).
    private static readonly double SpeedCap = OrbitalTransfers.MAX_INTERSTELLAR_SPEED;
    private static readonly float MaxSpeedC = (float)(OrbitalTransfers.MAX_INTERSTELLAR_SPEED / InterstellarMath.SpeedOfLight);

    private static readonly string[] SpeedPresets = { "0.001 C", "0.01 C", "0.1 C" };
    private static readonly string[] SpeedPresetsWithMax = { "0.001 C", "0.01 C", "0.1 C", "MAX" };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly CruiseSpeed _speed = new();
    private double _lastSpeed = double.NaN;
    private float _sliderC = (float)CruiseSpeed.PresetFractions[CruiseSpeed.DefaultPreset];
    private float _periapsisAu = 1f;
    private bool _periapsisDragging;
    private float _departureAntimatterPercent = DefaultAntimatterPercent;
    private float _brakeAntimatterPercent = DefaultAntimatterPercent;

    private InterstellarLeg? _leg;
    private LegKey _legKey;

    private ViewKey? _viewKey;
    private readonly List<Row> _rows = new();
    private readonly List<Row> _captureRows = new();
    private readonly List<Row> _statusRows = new();
    private readonly List<Row> _fuelRows = new();
    private string _failureText = GenericFailure;

    private const string GenericFailure = "NO ESTIMATE FOR THIS TRIP";
    private const string LeavingFailure = "NO ESTIMATE: THE VESSEL IS LEAVING THIS SYSTEM";

    // A leg or a view whose computation threw. It stays latched while its inputs stay the same,
    // so a failing case shows its error status instead of throwing again every frame or drawing
    // half-built rows.
    private LegKey? _failedLegKey;
    private ViewKey? _failedViewKey;

    // The cheapest trip and the fastest cruise the staged delta-v pays for. Both are searches
    // over the cruise speed, so they are cached apart from the speed itself. The cheapest trip
    // does not depend on the budget, so a new delta-v reading or margin reruns only the second.
    private CheapestKey? _cheapestKey;
    private double _cheapestSpeed;
    private double _cheapestLegDv;
    private CruiseKey? _cruiseKey;
    private double _maxSpeed;
    private int _limitsVersion;

    // The latched vessel state, the exact parking orbit behind it and the vehicle it was read from.
    private VesselState? _vesselState;
    private ParkingOrbit _statePark;
    private double _stateRemaining;
    private TransitState _stateTransit;
    private double _stateCourseCos = 1.0;
    private Vehicle? _stateVehicle;
    private double _vesselStateTime = double.NegativeInfinity;

    private Vehicle? _vesselVehicle;
    private VesselDrive? _vesselDrive;
    private double _vesselReadTime = double.NegativeInfinity;
    private int _vesselVersion;

    private DriveFuelInputs? _fuelInputs;
    private DriveFuelPlan _fuelPlan;

    // The inputs of the last Update, so a control change can re-evaluate in the same frame.
    private SystemGraph? _graph;
    private InterstellarRoute? _route;
    private double? _available;
    private double _dvScale = 1.0;
    private bool _fromSurface;

    private readonly record struct LegKey(SystemGraph? Graph, string? DepartId, string? TargetId, float PeriapsisAu, bool Arrival);

    private readonly record struct ViewKey(
        VesselState State,
        SpeedMode Mode,
        double Speed,
        double DvScale,
        int VesselVersion,
        float DepartureAntimatterPercent,
        float BrakeAntimatterPercent,
        double RestDv,
        double Split,
        int LimitsVersion);

    // KeyPark is the parking orbit rounded for the cache key, RemainingKey the coast left in
    // transit, ExcessKey the measured excess to a meter per second and CourseKey the cosine of the
    // turn toward another system to a millionth. The figures use the exact values.
    private readonly record struct VesselState(
        InterstellarLeg Leg,
        ParkingOrbit KeyPark,
        bool FromVessel,
        double? Available,
        int StagedRevision,
        double RemainingKey,
        double ExcessKey,
        bool Inbound,
        double CourseKey);

    private readonly record struct CheapestKey(InterstellarLeg Leg, ParkingOrbit KeyPark);

    // Budget is the delta-v the leg may spend, or null for a drive vessel, in transit and
    // without a reading.
    private readonly record struct CruiseKey(InterstellarLeg Leg, ParkingOrbit KeyPark, double? Budget);

    private enum RowKind
    {
        Header,
        Readout,
        Status
    }

    private readonly record struct Row(RowKind Kind, string Key, string Value, StatusTone Tone);

    private bool HasDrive => _vesselDrive.HasValue;

    // True when the last Update produced figures for the current route.
    public bool HasView { get; private set; }

    // True when the leg or its figures could not be computed for the current inputs.
    public bool Failed { get; private set; }

    // Bumped whenever the strings and figures below change, so the route panel rebuilds its
    // own lines only then.
    public int ViewVersion { get; private set; }

    public string DepartLine { get; private set; } = "";
    public string CoastLine { get; private set; } = "";
    public string CaptureLine { get; private set; } = "";
    public string CoastTimeLine { get; private set; } = "";

    // The whole route with the leg, margin included, the figure the totals and the vehicle bar
    // show.
    public double RouteTotal { get; private set; }

    public string? Feasibility { get; private set; }
    public StatusTone FeasibilityTone { get; private set; }

    public ConnectorBadge? Badge { get; private set; }

    // Drop every cached figure and the vessel read, for a new system or an unload. The user's
    // speed, periapsis and antimatter split stay, like the other panel settings.
    public void Reset()
    {
        _leg = null;
        _legKey = default;
        _viewKey = null;
        _failedLegKey = null;
        _failedViewKey = null;
        _cheapestKey = null;
        _cruiseKey = null;
        _maxSpeed = 0.0;
        _vesselState = null;
        _stateVehicle = null;
        _vesselStateTime = double.NegativeInfinity;
        _rows.Clear();
        _captureRows.Clear();
        _statusRows.Clear();
        _fuelRows.Clear();
        _failureText = GenericFailure;
        _vesselVehicle = null;
        _vesselDrive = null;
        _vesselReadTime = double.NegativeInfinity;
        _fuelInputs = null;
        _graph = null;
        _route = null;
        HasView = false;
        Failed = false;
        Badge = null;
    }

    // Bring the leg, the limits and the figures up to date for this frame, in that order, so
    // the preset row never offers a stale MAX. Returns true when the vessel parked at or left
    // the departure body since the route was accumulated, so the caller re-accumulates it once.
    public bool Update(SystemGraph graph, InterstellarRoute route, double? availableDv, double dvScale, bool fromSurface)
    {
        _graph = graph;
        _route = route;
        _available = availableDv;
        _dvScale = dvScale;
        _fromSurface = fromSurface;
        return Evaluate();
    }

    private bool Evaluate()
    {
#if DEBUG
        using var perf = new PerfTracker.Scope("DvMap.Interstellar.View");
#endif
        SystemGraph graph = _graph!;
        InterstellarRoute route = _route!;
        HasView = false;
        Failed = false;

        _failureText = GenericFailure;

        Vehicle? vehicle = ControlledVehicle();
        RefreshVessel(vehicle);

        bool inTransit = route.InTransit;
        bool crossing = route.DestinationRoot != null;
        if (inTransit && vehicle == null)
        {
            Failed = true;
            return false;
        }

        var legKey = new LegKey(graph, route.DepartBody.Id, route.Target.Id,
            route.TargetIsHub ? _periapsisAu : 0f, inTransit);
        if (legKey == _failedLegKey)
        {
            Failed = true;
            return false;
        }

        bool stale = false;
        try
        {
            EnsureLeg(in legKey, route, vehicle);
            InterstellarLeg leg = _leg!;

            ParkingOrbit park = default;
            bool fromVessel = false;
            double remaining = leg.Distance;
            TransitState transit = default;
            double courseCos = 1.0;
            if (inTransit)
            {
                if (!InterstellarLegs.TryTransitState(vehicle!, out transit))
                {
                    Failed = true;
                    return false;
                }
                double3 position = vehicle!.GetPositionEcl();
                remaining = InterstellarLegs.RemainingDistance(position, route.Target);
                if (crossing)
                    courseCos = double3.Dot(transit.LeaveDirection, InterstellarLegs.AimFrom(position, route.Target));
            }
            else
            {
                bool parks = !_fromSurface && InterstellarLegs.VesselParksAt(route.DepartBody, vehicle);
                stale = parks != route.FromVessel;
                park = InterstellarLegs.DeparturePark(route.DepartBody, route.Target, parks ? vehicle : null, out fromVessel);
            }

            // The vessel's orbit jitters in the last digits even while coasting, so the key holds
            // it to a kilometer, a meter per second and a millionth of the aim's sine; finer
            // changes cannot move a readout.
            ParkingOrbit keyPark = park with
            {
                Radius = Math.Round(park.Radius / 1000.0),
                Speed = Math.Round(park.Speed),
                AimSine = park.AimSine is double aimSine ? Math.Round(aimSine, 6) : null
            };
            double remainingKey = inTransit ? Math.Round(remaining / Format.LightYear, 4) : 0.0;
            var observed = new VesselState(leg, keyPark, fromVessel, _available, StagedDv.Revision, remainingKey,
                Math.Round(transit.Excess), transit.Inbound, Math.Round(courseCos, 6));
            LatchVesselState(in observed, in park, remaining, in transit, courseCos, vehicle);
            VesselState state = _vesselState!.Value;

            // A capture in the system the vessel coasts through needs it to fall inward; one that
            // already climbs away would have to turn back, which this model does not price.
            if (inTransit && !crossing && !state.Inbound)
            {
                _failureText = LeavingFailure;
                Failed = true;
                return false;
            }

            double split = route.CaptureSplitDv;
            if (!inTransit && !(_periapsisDragging && _cheapestKey != null))
                EnsureLimits(leg, in _statePark, in state, route.RestDv - split);

            // MAX exists for a vessel without a drive; a drive vessel is planned by the drive model.
            // A delta-v reading that is missing for a moment (a fresh part tree) keeps MAX and the
            // speed it last resolved to, so a staging event does not drop it.
            double floor = MinSpeedC * InterstellarMath.SpeedOfLight;
            CruiseChoice choice = _speed.Resolve(_maxSpeed, _cheapestSpeed, SpeedCap, floor);
            _speed.DropMaxIfUnavailable(!HasDrive, double.IsNaN(_lastSpeed) ? choice.Speed : _lastSpeed);
            choice = _speed.Resolve(_maxSpeed, _cheapestSpeed, SpeedCap, floor);
            if (_speed.Mode == SpeedMode.Max && !state.Available.HasValue)
                choice = new CruiseChoice(double.IsNaN(_lastSpeed) ? Math.Max(_cheapestSpeed, floor) : _lastSpeed, false);
            double vInf = inTransit ? _stateTransit.Excess : choice.Speed;
            if (!inTransit)
                _lastSpeed = vInf;
            _sliderC = (float)(vInf / InterstellarMath.SpeedOfLight);

            var key = new ViewKey(state, _speed.Mode, vInf, _dvScale, _vesselVersion,
                _departureAntimatterPercent, _brakeAntimatterPercent, route.RestDv, split, _limitsVersion);
            if (key == _failedViewKey)
            {
                Failed = true;
                return stale;
            }
            if (_viewKey != key)
            {
                _viewKey = null;
                _failedViewKey = key;
                Recompute(graph, route, leg, in _statePark, _stateRemaining, vInf, choice.CannotPayCheapest, in key);
                _viewKey = key;
                _failedViewKey = null;
                ViewVersion++;
            }
            HasView = true;
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("interstellar-plan", $"[DvMap] Interstellar plan failed: {ex}");
            Failed = true;
            HasView = false;
        }
        return stale;
    }

    // The section header and the cruise-speed presets and slider. A change re-evaluates the
    // figures at once, so the totals below follow in the same frame; nothing here touches the
    // route or the layout.
    public void DrawControls()
    {
        if (_route == null)
            return;
        ConsoleWidgets.RegionHeader("INTERSTELLAR LEG".AsSpan());
        bool changed = false;
        if (!_route.InTransit)
        {
            // MAX is the fastest cruise the staged delta-v pays for. A drive vessel is planned by
            // the drive model instead, and without a delta-v reading there is no such speed.
            bool showMax = !HasDrive && (_available.HasValue || _speed.Mode == SpeedMode.Max);
            int preset = ConsoleWidgets.Segmented("DvmIsSpeed".AsSpan(), showMax ? SpeedPresetsWithMax : SpeedPresets, ActivePreset(showMax));
            if (preset >= 0 && preset < CruiseSpeed.PresetFractions.Length)
            {
                if (_speed.Mode != SpeedMode.Preset || _speed.Preset != preset)
                {
                    _speed.SelectPreset(preset);
                    changed = true;
                }
            }
            else if (showMax && preset == CruiseSpeed.PresetFractions.Length && _speed.Mode != SpeedMode.Max)
            {
                _speed.SelectMax();
                changed = true;
            }
            if (ConsoleUi.LogSliderRow("V INF".AsSpan(), "dvisvinf".AsSpan(), ref _sliderC, MinSpeedC, MaxSpeedC, "%.5f c".AsSpan()))
            {
                _speed.SetCustom(_sliderC * InterstellarMath.SpeedOfLight);
                changed = true;
            }
        }

        if (changed)
            Evaluate();
    }

    // The leg's readouts, then for a star or barycenter target the capture periapsis slider
    // with its readouts, then the model note. The slider shows even for a failed estimate, so
    // the player can move off a periapsis that cannot be computed.
    public void DrawReadouts()
    {
        if (_route == null)
            return;
        if (!Failed)
            DrawRows(_rows);

        if (_route.TargetIsHub)
        {
            ConsoleWidgets.RegionHeader("CAPTURE".AsSpan());
            float before = _periapsisAu;
            ConsoleUi.LogSliderRow("PERIAPSIS".AsSpan(), "dvisperi".AsSpan(), ref _periapsisAu, MinPeriapsisAu, MaxPeriapsisAu,
                "%.2f AU".AsSpan(), out bool dragging);
            bool released = _periapsisDragging && !dragging;
            _periapsisDragging = dragging;
            if (_periapsisAu != before || released)
                Evaluate();
            if (!Failed)
                DrawRows(_captureRows);
        }
        else
        {
            _periapsisDragging = false;
        }

        if (Failed)
            ConsoleUi.StatusText(_failureText, StatusTone.Danger);
        else
            DrawRows(_statusRows);
    }

    public void DrawFuel()
    {
        if (Failed || !HasView)
            return;
        if (HasDrive && _route is { InTransit: false })
        {
            ConsoleWidgets.RegionHeader("ANTIMATTER DRIVE".AsSpan());
            bool changed = ConsoleUi.LogSliderRow("SPEED UP ANTIMATTER".AsSpan(), "dvisamdep".AsSpan(), ref _departureAntimatterPercent, 0.1f, 100f, "%.1f %%".AsSpan());
            changed |= ConsoleUi.LogSliderRow("SLOW DOWN ANTIMATTER".AsSpan(), "dvisambrk".AsSpan(), ref _brakeAntimatterPercent, 0.1f, 100f, "%.1f %%".AsSpan());
            if (changed)
                Evaluate();
        }
        DrawRows(_fuelRows);
    }

    // Opens the stock planner on an interstellar plan from this vessel to the route's system.
    // Stock departs only from the vessel's own system (StockPlannerBridge.StockDestination), so
    // for a map rooted elsewhere, or a vessel in transit, the button selects the plan type alone
    // and the player picks the destination there.
    public void DrawStockPlannerButton()
    {
        if (Program.Editor != null || _route == null)
            return;
        Vehicle? vehicle = ControlledVehicle();
        if (vehicle == null)
            return;
        ImGui.Separator();
        Astronomical? departure = _route.InTransit ? null : _route.SourceRoot.Astro;
        Astronomical? destination = _route.DestinationRoot?.Astro;
        if (ConsoleWidgets.Button("OPEN STOCK TRANSFER PLANNER".AsSpan()))
            StockPlannerBridge.OpenInterstellar(vehicle, departure, destination);
        if (!StockPlannerBridge.IsAvailable)
            ImGui.TextWrapped("Set PLAN TYPE to Interstellar there.");
        else if (StockPlannerBridge.StockDestination(vehicle, departure, destination) != null)
            ImGui.TextWrapped("Opens on the Interstellar plan to this system.");
        else
            ImGui.TextWrapped("Opens on the Interstellar plan. Pick the destination there.");
    }

    private static void DrawRows(List<Row> rows)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            Row row = rows[i];
            switch (row.Kind)
            {
                case RowKind.Header:
                    ConsoleWidgets.RegionHeader(row.Key.AsSpan());
                    break;
                case RowKind.Readout:
                    ConsoleWidgets.Readout(row.Key.AsSpan(), row.Value.AsSpan());
                    break;
                case RowKind.Status:
                    ConsoleUi.StatusText(row.Key, row.Tone);
                    break;
            }
        }
    }

    // The highlighted preset comes from the stored mode, never from comparing speeds, so MAX
    // stays lit while the budget behind it moves.
    private int ActivePreset(bool showMax)
    {
        return _speed.Mode switch
        {
            SpeedMode.Preset => _speed.Preset,
            SpeedMode.Max when showMax => CruiseSpeed.PresetFractions.Length,
            _ => -1
        };
    }

    // Build the leg for a new departure, target or periapsis. The failure latch is armed before
    // the build and cleared after it, so a build that throws stays latched for its key.
    private void EnsureLeg(in LegKey key, InterstellarRoute route, Vehicle? vehicle)
    {
        if (_leg != null && key == _legKey)
            return;
#if DEBUG
        using var perf = new PerfTracker.Scope("DvMap.Interstellar.Leg");
#endif
        _leg = null;
        _failedLegKey = key;
        double periapsis = _periapsisAu * InterstellarMath.AstronomicalUnit;
        _leg = key.Arrival
            ? InterstellarLegs.BuildArrival(vehicle!.GetPositionEcl(), route.Target, periapsis)
            : InterstellarLegs.Build(route.DepartBody, route.Target, periapsis);
        _legKey = key;
        _failedLegKey = null;
    }

    // Re-read the vessel's drive and propellant when the vehicle changes or the refresh interval
    // has passed. Bumps the version only when the read differs, so an idle vessel recomputes
    // nothing.
    private void RefreshVessel(Vehicle? vehicle)
    {
        double now = Program.GetPlayerTime();
        bool due = !ReferenceEquals(vehicle, _vesselVehicle)
            || now - _vesselReadTime >= VesselRefreshSeconds
            || now < _vesselReadTime;
        if (!due)
            return;

        _vesselVehicle = vehicle;
        _vesselReadTime = now;
        VesselDrive? read = null;
        try
        {
            if (vehicle != null && InterstellarFuel.TryReadVessel(vehicle, out VesselDrive drive))
                read = drive;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("interstellar-drive", $"[DvMap] Could not read the vessel's antimatter drive: {ex.Message}");
        }

        if (!Nullable.Equals(read, _vesselDrive))
        {
            _vesselDrive = read;
            _vesselVersion++;
        }
    }

    // Takes a new vessel state at once when its shape changed (a new leg, another vessel, the
    // vessel entering or leaving the orbit, a delta-v reading appearing or going), and otherwise at
    // most every VesselStateRefreshSeconds. Update runs on every frame, so the last change of a
    // burn still lands once the interval has passed. A vessel in transit that turns from falling
    // inward to climbing away is a change of shape too.
    private void LatchVesselState(in VesselState state, in ParkingOrbit park, double remaining, in TransitState transit, double courseCos, Vehicle? vehicle)
    {
        if (_vesselState is VesselState latched)
        {
            bool sameVehicle = ReferenceEquals(vehicle, _stateVehicle);
            if (sameVehicle && latched == state)
                return;
            bool sameShape = sameVehicle
                && ReferenceEquals(latched.Leg, state.Leg)
                && latched.FromVessel == state.FromVessel
                && latched.Inbound == state.Inbound
                && latched.Available.HasValue == state.Available.HasValue;
            double now = Program.GetPlayerTime();
            // The backwards test catches a clock reset, as in StagedDv.
            if (sameShape && now - _vesselStateTime < VesselStateRefreshSeconds && now >= _vesselStateTime)
                return;
        }
        _vesselState = state;
        _statePark = park;
        _stateRemaining = remaining;
        _stateTransit = transit;
        _stateCourseCos = courseCos;
        _stateVehicle = vehicle;
        _vesselStateTime = Program.GetPlayerTime();
    }

    // The cheapest trip and, for a vessel without a drive, the fastest cruise its staged delta-v
    // pays for. Both search over the cruise speed, so they rerun only when the leg, the parking
    // orbit or the budget change, not when the speed slider moves. restDv is the rest of the
    // route the budget pays first.
    private void EnsureLimits(InterstellarLeg leg, in ParkingOrbit park, in VesselState state, double restDv)
    {
#if DEBUG
        using var perf = new PerfTracker.Scope("DvMap.Interstellar.Limits");
#endif
        var cheapestKey = new CheapestKey(state.Leg, state.KeyPark);
        if (_cheapestKey != cheapestKey)
        {
            _cheapestKey = null;
            _cruiseKey = null;
            _cheapestSpeed = leg.CheapestSpeed(in park, SpeedCap);
            _cheapestLegDv = leg.TotalDv(_cheapestSpeed, in park);
            _cheapestKey = cheapestKey;
            _limitsVersion++;
        }

        double? budget = !HasDrive && state.Available is double available
            ? InterstellarMath.LegBudget(available, _dvScale, restDv)
            : null;
        var cruiseKey = new CruiseKey(state.Leg, state.KeyPark, budget);
        if (_cruiseKey == cruiseKey)
            return;
        _cruiseKey = null;
        _maxSpeed = budget is double b ? leg.MaxCruiseSpeed(b, in park, SpeedCap, _cheapestSpeed) : 0.0;
        _cruiseKey = cruiseKey;
        _limitsVersion++;
    }

    private void Recompute(SystemGraph graph, InterstellarRoute route, InterstellarLeg leg, in ParkingOrbit park, double remaining,
        double vInf, bool cannotPayCheapest, in ViewKey key)
    {
        double scale = key.DvScale;
        double? availableDv = key.State.Available;
        bool hasDrive = HasDrive;
        bool inTransit = route.InTransit;
        double split = route.CaptureSplitDv;
        double rest = route.RestDv;

        bool crossing = route.DestinationRoot != null;

        // In transit there is no departure from a parking orbit; a trip on to another system
        // turns the excess toward it at the same speed.
        double departBurn = !inTransit ? leg.DepartBurn(vInf, in park)
            : crossing ? InterstellarMath.CourseChangeBurn(vInf, _stateCourseCos)
            : 0.0;
        double captureBurn = leg.CaptureBurn(vInf);
        double total = InterstellarMath.RouteTotal(rest, departBurn, captureBurn, split, scale);
        double distance = inTransit ? remaining : leg.Distance;
        double coast = InterstellarMath.CoastSeconds(distance, vInf);
        double cheapestRoute = InterstellarMath.RouteTotal(rest, _cheapestLegDv, 0.0, split, scale);
        // The coast ends at the target's star (InterstellarLegs.DestinationPoint), which for a
        // planet of an orbiting star is not the system root the stub names.
        string coastEnd = InterstellarLegs.DestinationPoint(route.Target).Id;
        RouteTotal = total;

        if (!inTransit)
            DepartLine = "Depart " + route.DepartBody.Id + " (" + FromWord(key.State.FromVessel, route.DepartBody) + "): " + Format.Dv(departBurn * scale);
        else if (crossing)
            DepartLine = "Turn toward " + coastEnd + ": " + Format.Dv(departBurn * scale);
        else
            DepartLine = "Coasting at " + Format.Speed(vInf);
        CoastLine = "Coast " + Format.Distance(distance) + " to " + coastEnd + ": " + Format.Duration(coast);
        CaptureLine = "Capture at " + route.Target.Id + (route.TargetIsHub ? " parking orbit" : "")
            + (split > 0.0 ? " (ellipse)" : "") + ": " + Format.Dv((captureBurn - split) * scale);
        CoastTimeLine = "Coast time: " + Format.Duration(coast);

        if (route.DestinationRoot is PhysicalNode destinationRoot)
        {
            string dvText = Format.Dv((departBurn + captureBurn) * scale);
            string timeText = Format.Duration(coast);
            Badge = new ConnectorBadge(destinationRoot.Id + "." + StateKind.Hub, dvText, ImGui.CalcTextSize(dvText).X,
                timeText, ImGui.CalcTextSize(timeText).X);
        }
        else
        {
            Badge = null;
        }

        // The distance and the coast time are on the breakdown's coast line, so the readouts
        // carry only what it does not.
        _rows.Clear();
        if (cannotPayCheapest && _speed.Mode == SpeedMode.Max)
            _rows.Add(Status("MAX: THIS VESSEL CANNOT PAY FOR THE CHEAPEST TRIP", StatusTone.Danger));
        _rows.Add(Readout("FROM", inTransit
            ? "in transit, open orbit"
            : route.DepartBody.Id + ", " + FromWord(key.State.FromVessel, route.DepartBody)));
        _rows.Add(Readout("TO", route.Target.Id + (route.TargetIsHub ? ", parking orbit" : ", low orbit")));
        _rows.Add(Readout("SPEED", Format.Speed(vInf)));
        if (!inTransit)
            _rows.Add(Readout("CHEAPEST TRIP, ANY SPEED", Format.Dv(cheapestRoute)));
        if (!hasDrive && !inTransit && availableDv.HasValue && _maxSpeed > 0.0)
        {
            _rows.Add(Readout("FASTEST CRUISE", Format.Speed(_maxSpeed)));
            _rows.Add(Readout("FASTEST COAST", Format.Duration(InterstellarMath.CoastSeconds(leg.Distance, _maxSpeed))));
        }

        _captureRows.Clear();
        if (route.TargetIsHub)
        {
            _captureRows.Add(Readout("CIRCULAR AT PERIAPSIS", Format.Speed(leg.ArrivePark.Speed)));
            if (Math.Abs(leg.ArrivePark.Radius - _periapsisAu * InterstellarMath.AstronomicalUnit) > 1.0)
                _captureRows.Add(Readout("PERIAPSIS USED", Format.Distance(leg.ArrivePark.Radius)));
        }

        _statusRows.Clear();
        _statusRows.Add(Status(hasDrive ? "NEWTONIAN, IMPULSIVE BURNS, BEST TIME OF YEAR" : "NEWTONIAN, BEST TIME OF YEAR", StatusTone.Muted));

        // The staged delta-v is the chemical answer. A drive vessel gets its feasibility from the
        // drive model below.
        Feasibility = null;
        if (!hasDrive && availableDv is double available)
        {
            if (total <= available)
            {
                Feasibility = inTransit ? "REACHABLE" : "REACHABLE AT THIS SPEED";
                FeasibilityTone = StatusTone.Positive;
            }
            else if (inTransit)
            {
                Feasibility = "SHORT BY " + Format.Dv(total - available);
                FeasibilityTone = StatusTone.Danger;
            }
            else if (_maxSpeed > 0.0)
            {
                Feasibility = "SHORT BY " + Format.Dv(total - available) + ", CHOOSE A LOWER SPEED";
                FeasibilityTone = StatusTone.Danger;
            }
            else
            {
                Feasibility = "SHORT OF THE CHEAPEST TRIP BY " + Format.Dv(cheapestRoute - available);
                FeasibilityTone = StatusTone.Danger;
            }
        }

        _fuelRows.Clear();
        if (_vesselDrive is VesselDrive drive)
        {
            if (inTransit)
                _fuelRows.Add(Status("PLAN THE BRAKE IN THE STOCK TRANSFER PLANNER.", StatusTone.Muted));
            else
                BuildDriveRows(graph, route, leg, drive, vInf, scale);
        }
        else
        {
            BuildRocketRows(total);
        }
    }

    private static string FromWord(bool fromVessel, PhysicalNode departBody)
    {
        if (fromVessel)
            return "current orbit";
        return departBody.IsHubOnly ? "parking orbit" : "low orbit";
    }

    // The game's own burn model for an antimatter ram drive, fed the same inputs as the stock
    // planner: the chosen cruise speed as the speed-up target, a share of the antimatter aboard
    // for each burn, and the final speed. A star or barycenter target brakes to the circular
    // speed at the capture periapsis, as stock does. A planet or moon target brakes to rest, and
    // the chain capture from there is listed as an impulsive burn after it.
    private void BuildDriveRows(SystemGraph graph, InterstellarRoute route, InterstellarLeg leg, VesselDrive drive, double vInf, double scale)
    {
        double finalSpeed = route.TargetIsHub ? leg.ArrivePark.Speed : 0.0;
        var inputs = new DriveFuelInputs(
            drive.Drive,
            drive.Mass,
            drive.HydrogenAboard,
            vInf,
            drive.AntimatterAboard * _departureAntimatterPercent / 100.0,
            drive.AntimatterAboard * _brakeAntimatterPercent / 100.0,
            finalSpeed,
            graph.System.InterstellarGasDensity,
            leg.Distance,
            drive.HydrogenSpace);
        if (_fuelInputs != inputs)
        {
            _fuelInputs = null;
            _fuelPlan = InterstellarFuel.Plan(in inputs);
            _fuelInputs = inputs;
        }
        DriveFuelPlan plan = _fuelPlan;

        if (!plan.Departs)
        {
            _fuelRows.Add(Status("THE SPEED UP BURN GOES NOWHERE", StatusTone.Danger));
            return;
        }

        AddBurnRows("SPEED UP BURN", plan.Departure);
        if (plan.Departure.EndSpeed < 0.999 * vInf)
            _fuelRows.Add(Status("SPEED UP BURN ONLY REACHES " + Format.Speed(plan.Departure.EndSpeed), StatusTone.Danger));
        _fuelRows.Add(Header("COAST"));
        _fuelRows.Add(Readout("COAST TIME", Format.Duration(plan.CoastSeconds)));
        _fuelRows.Add(Readout("HYDROGEN SCOOPED", Format.Mass(plan.Scooped)));
        AddBurnRows("SLOW DOWN BURN", plan.Brake);
        if (plan.BrakeFallsShort(finalSpeed))
        {
            string reason = plan.Brake.End == InterstellarBurnEnd.Antimatter
                ? "SLOW DOWN BURN RUNS OUT OF ANTIMATTER"
                : "NOT ENOUGH ROOM LEFT TO SLOW DOWN";
            _fuelRows.Add(Status(reason, StatusTone.Danger));
        }
        if (!route.TargetIsHub)
            _fuelRows.Add(Readout("CAPTURE AFTER THE BRAKE", Format.Dv((leg.CaptureBurn(0.0) - route.CaptureSplitDv) * scale) + " (impulsive)"));

        _fuelRows.Add(Header("PROPELLANT"));
        _fuelRows.Add(Readout("TIME OF FLIGHT", Format.Duration(plan.TimeOfFlightSeconds)));
        _fuelRows.Add(Readout("ANTIMATTER", Format.Mass(plan.AntimatterNeeded) + " / " + Format.Mass(drive.AntimatterAboard)));
        _fuelRows.Add(Readout("HYDROGEN", Format.Mass(Math.Max(0.0, plan.HydrogenNeeded)) + " / " + Format.Mass(drive.HydrogenAboard)));
        if (plan.AntimatterNeeded > drive.AntimatterAboard)
            _fuelRows.Add(Status("NOT ENOUGH ANTIMATTER: " + Format.Mass(plan.AntimatterNeeded - drive.AntimatterAboard) + " SHORT", StatusTone.Danger));
        if (plan.HydrogenNeeded > drive.HydrogenAboard)
            _fuelRows.Add(Status("NOT ENOUGH HYDROGEN: " + Format.Mass(plan.HydrogenNeeded - drive.HydrogenAboard) + " SHORT", StatusTone.Danger));
    }

    private void AddBurnRows(string header, in InterstellarBurnEstimate burn)
    {
        _fuelRows.Add(Header(header));
        _fuelRows.Add(Readout("BURN TIME", Format.Duration(burn.Seconds)));
        _fuelRows.Add(Readout("ENDS AT", BurnEndText(burn.End)));
        _fuelRows.Add(Readout("SPEED AFTER", Format.Speed(burn.EndSpeed)));
        _fuelRows.Add(Readout("ANTIMATTER", Format.Mass(burn.Antimatter)));
        _fuelRows.Add(Readout("HYDROGEN", Format.Mass(burn.CarriedHydrogen)));
    }

    // Rocket equation over the staged sequences, for a vessel without an antimatter drive (or in
    // the editor). The needed figure covers the whole route. Past the stages, the extra propellant
    // is only what the last stage alone would need, so it is a lower bound; when even that
    // overflows, the mass ratio it would take tells how far out of reach the trip is.
    private void BuildRocketRows(double totalDv)
    {
        ReadOnlySpan<StageMass> stages = StagedDv.Stages;
        _fuelRows.Add(Header("PROPELLANT (ROCKET EQUATION)"));
        if (stages.IsEmpty)
        {
            _fuelRows.Add(Status("NO STAGED VESSEL", StatusTone.Muted));
            return;
        }

        PropellantNeed need = RocketPropellant.Needed(stages, totalDv);
        if (need.Feasible)
        {
            _fuelRows.Add(Readout("NEEDED", Format.Mass(need.Needed) + " / " + Format.Mass(need.Available)));
            _fuelRows.Add(Status("ENOUGH PROPELLANT ABOARD", StatusTone.Positive));
        }
        else if (double.IsFinite(need.ExtraPropellant))
        {
            _fuelRows.Add(Readout("NEEDED", "at least " + Format.Mass(need.Needed) + " / " + Format.Mass(need.Available)));
            _fuelRows.Add(Status("LAST STAGE NEEDS AT LEAST " + Format.Mass(need.ExtraPropellant) + " MORE", StatusTone.Danger));
        }
        else if (double.IsFinite(need.Log10MassRatio))
        {
            _fuelRows.Add(Readout("LAST STAGE MASS RATIO", "1E+" + Math.Floor(need.Log10MassRatio).ToString("#,##0", Inv)));
            _fuelRows.Add(Status("NOT POSSIBLE WITH THIS PROPULSION", StatusTone.Danger));
        }
        else
        {
            _fuelRows.Add(Status("NO STAGE GIVES DELTA-V", StatusTone.Danger));
        }
    }

    private static Row Header(string text) => new(RowKind.Header, text, "", StatusTone.Muted);
    private static Row Readout(string key, string value) => new(RowKind.Readout, key, value, StatusTone.Muted);
    private static Row Status(string text, StatusTone tone) => new(RowKind.Status, text, "", tone);

    private static string BurnEndText(InterstellarBurnEnd end)
    {
        return end switch
        {
            InterstellarBurnEnd.Speed => "TARGET SPEED",
            InterstellarBurnEnd.Antimatter => "ANTIMATTER USED",
            InterstellarBurnEnd.Hydrogen => "HYDROGEN USED",
            InterstellarBurnEnd.Distance => "HALFWAY",
            InterstellarBurnEnd.Vessel => "VESSEL BURNED AWAY",
            _ => end.ToString().ToUpperInvariant()
        };
    }

    // Reading the controlled vehicle can throw for a vehicle mid transition; treat that as none.
    private static Vehicle? ControlledVehicle()
    {
        try
        {
            Vehicle? vehicle = Program.ControlledVehicle;
            return vehicle?.Parent != null ? vehicle : null;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("interstellar-vehicle", $"[DvMap] Could not read the controlled vehicle: {ex.Message}");
            return null;
        }
    }
}
