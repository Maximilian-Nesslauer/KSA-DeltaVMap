using DeltaVMap.Core;
using DeltaVMap.Mission;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

// Locks the mission adapters against stock. The mission enums are mirrored by member order and the
// plan limits by value, so a reordered or retuned game type fails here first. The route timing
// must equal stock's own mission timing (MissionTiming.Compute of a plan) for every body stock's
// mission picker lists. With a vessel save it also checks the supply verdict against the stock
// call sequence, that a flight supply read waits for pending habitat zones instead of building
// them, the flight mission write, and the probe power against stock's PowerForecast.
public sealed class MissionCoreTest : IHarnessTest
{
    private const string Tag = "[dvmap-mission-core]";
    private const double Day = 86400.0;
    private const double TimeTolerance = 1e-6;

    public string Name => "dvmap-mission-core";

    public int Run(HeadlessSession session)
    {
        bool ok = true;
        CelestialSystem system = session.System;
        try
        {
            ok &= CheckEnumsAndConstants();
            if (system.HomeBody is not Astronomical home)
            {
                HarnessLog.Line($"{Tag} FAIL: the loaded system has no home body.");
                return 1;
            }
            ok &= CheckTiming(home);
            ok &= RunWithVehicle(system, home);
        }
        catch (Exception ex)
        {
            HarnessLog.Line($"{Tag} FAIL: {ex}");
            ok = false;
        }

        HarnessLog.Line($"{Tag} {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool CheckEnumsAndConstants()
    {
        bool ok = true;
        ok &= Check("MissionGoal follows MissionDestination", SameOrder<MissionDestination, MissionGoal>(null));
        ok &= Check("RouteArrival follows MissionArrival", SameOrder<MissionArrival, RouteArrival>(null));
        ok &= Check("SupplyKind follows MissionSupply", SameOrder<MissionSupply, SupplyKind>(null));
        ok &= Check("ProbeState follows ProbeCorePower", SameOrder<ProbeCorePower, ProbeState>(("NoProbeCore", "None")));
        ok &= Check("DrainKind follows PowerDrain", SameOrder<PowerDrain, DrainKind>(null));
        ok &= Check("the plan limits equal MissionPlan's",
            MissionRoutes.MinCruiseSpeedC == MissionPlan.MIN_CRUISE_SPEED_C
            && MissionRoutes.MaxCruiseSpeedC == MissionPlan.MAX_CRUISE_SPEED_C
            && MissionRoutes.DefaultCruiseSpeedC == MissionPlan.DEFAULT_CRUISE_SPEED_C
            && MissionRoutes.DefaultStaySeconds == MissionPlan.DEFAULT_STAY_SECONDS);
        return ok;
    }

    // Both enums have the same names (one rename allowed) at the same values.
    private static bool SameOrder<TStock, TOurs>((string Stock, string Ours)? rename)
        where TStock : struct, Enum
        where TOurs : struct, Enum
    {
        string[] stock = Enum.GetNames<TStock>();
        string[] ours = Enum.GetNames<TOurs>();
        if (stock.Length != ours.Length)
            return false;
        foreach (string name in stock)
        {
            string mapped = rename is { } r && r.Stock == name ? r.Ours : name;
            if (!Enum.TryParse(mapped, out TOurs value))
                return false;
            if (Convert.ToInt64(value) != Convert.ToInt64(Enum.Parse<TStock>(name)))
                return false;
        }
        return true;
    }

    // For every body the stock picker lists, every arrival and both trips, the route timing equals
    // MissionTiming.Compute of the same plan launched from the home body; low orbit too.
    private static bool CheckTiming(Astronomical home)
    {
        bool ok = true;
        int compared = 0;
        int skipped = 0;
        var arrivals = new[] { RouteArrival.Flyby, RouteArrival.Orbit, RouteArrival.Land };
        foreach (Astronomical body in PickerBodies((IParentBody)home))
        {
            if (!ChainIsBound((IParentBody)body))
            {
                HarnessLog.Line($"{Tag} skip timing for '{body.Id}': an orbit on its parent chain is open.");
                skipped++;
                continue;
            }
            foreach (RouteArrival arrival in arrivals)
            {
                foreach (bool ret in new[] { true, false })
                {
                    var plan = new MissionPlan
                    {
                        Destination = MissionDestination.Body,
                        BodyId = body.Id,
                        Arrival = MissionReader.ToGame(arrival),
                        Trip = ret ? MissionTrip.Return : MissionTrip.OneWay,
                        Stay = new UniverseTime(3.0 * Day),
                        CruiseSpeedC = 0.1,
                    };
                    ok &= CompareTiming(home, body, plan, arrival, ret);
                    compared++;
                }
            }
        }

        foreach (bool ret in new[] { true, false })
        {
            var lowOrbit = new MissionPlan
            {
                Destination = MissionDestination.LowOrbit,
                Trip = ret ? MissionTrip.Return : MissionTrip.OneWay,
                Stay = new UniverseTime(3.0 * Day),
            };
            ok &= CompareTiming(home, home, lowOrbit, RouteArrival.Orbit, ret);
            compared++;
        }

        HarnessLog.Line($"{Tag} timing compared for {compared} plans, {skipped} bodies skipped.");
        return ok & Check("the route timing was compared for at least one body", compared > 2);
    }

    private static bool CompareTiming(Astronomical home, Astronomical body, MissionPlan plan, RouteArrival arrival, bool ret)
    {
        MissionLegs stock = MissionTiming.Compute(plan, (IParentBody)home);
        MissionTimes ours = MissionTimingBridge.Compute(home, body, arrival, ret, plan.Stay.Seconds(), plan.CruiseSpeedC);
        MissionTimesStatus expected = stock.Status switch
        {
            MissionLegsStatus.Planned => MissionTimesStatus.Planned,
            MissionLegsStatus.FlybyReturnUnavailable => MissionTimesStatus.FlybyReturnUnavailable,
            _ => MissionTimesStatus.Unknown,
        };
        bool same = ours.Status == expected;
        if (same && expected != MissionTimesStatus.Unknown)
        {
            same = Near(ours.OutSeconds, stock.Out.Seconds())
                && Near(ours.StaySeconds, stock.Stay.Seconds())
                && Near(ours.BackSeconds, stock.Back.Seconds())
                && Near(ours.ReturnWaitSeconds, stock.ReturnWait.Seconds())
                && ours.Interstellar == MissionTiming.IsInterstellar((IParentBody)home, (IParentBody)body);
        }
        if (!same)
        {
            HarnessLog.Line(FormattableString.Invariant(
                $"{Tag} TEST timing '{body.Id}' {arrival} {(ret ? "return" : "one way")}: ours {ours.Status} {ours.OutSeconds:F1}/{ours.StaySeconds:F1}/{ours.BackSeconds:F1}/{ours.ReturnWaitSeconds:F1}, stock {stock.Status} {stock.Out.Seconds():F1}/{stock.Stay.Seconds():F1}/{stock.Back.Seconds():F1}/{stock.ReturnWait.Seconds():F1} => FAIL"));
        }
        return same;
    }

    private static bool RunWithVehicle(CelestialSystem system, Astronomical home)
    {
        string? saveId = Environment.GetEnvironmentVariable(TestSupport.VehicleEnvVar);
        if (string.IsNullOrEmpty(saveId))
        {
            HarnessLog.Line($"{Tag} SKIP vehicle checks: {TestSupport.VehicleEnvVar} not set.");
            return true;
        }

        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        bool lifeSupport = Universe.Rules.LifeSupport;
        bool ok = true;
        try
        {
            IParentBody parent = (IParentBody)home;
            Orbit orbit = VehicleSpawner.CircularCci(parent, home.MeanRadius + 500_000.0, Universe.GetElapsedTime());
            Vehicle vehicle = VehicleSpawner.SpawnFromSave(saveId, system, parent, "DvMapMissionCoreTest", orbit);
            Program.ControlledVehicle = vehicle;
            // Stands in for Program.PrepareFrame, which this headless run has no frame loop for.
            PartTree.FlushDirtyDerived();
            MissionSupplyCheck.Reset();
            ProbePower.Reset();

            ok &= CheckSupplies(vehicle, home);
            ok &= CheckWrite(vehicle, home);
            ok &= CheckProbePower(vehicle);
        }
        finally
        {
            Universe.Rules.LifeSupport = lifeSupport;
            Program.ControlledVehicle = null;
            MissionSupplyCheck.Reset();
            ProbePower.Reset();
            TestSupport.DespawnNewVehicles(system, preexisting);
        }
        return ok;
    }

    // The verdict equals a direct run of the stock sequence (cleared list, the flight variant of
    // LifeSupport.ComputeZoneForecasts, MissionSupplies.Compute, FindShortest) for short and long
    // missions.
    private static bool CheckSupplies(Vehicle vehicle, Astronomical home)
    {
        Universe.Rules.LifeSupport = true;
        PartTree tree = vehicle.Parts;
        if (tree.EnsureHabitatZones().Length == 0)
        {
            HarnessLog.Line($"{Tag} SKIP supplies: the vessel has no habitat zone.");
            return true;
        }
        if (Probe(tree, Day) == null)
        {
            int seated = VehicleSpawner.SeatRandomCrew(vehicle);
            PartTree.FlushDirtyDerived();
            HarnessLog.Line($"{Tag} seated {seated} kitten(s) for the supply checks.");
        }
        if (Probe(tree, Day) == null)
        {
            HarnessLog.Line($"{Tag} SKIP supplies: no kitten on any leg.");
            return true;
        }

        bool ok = true;
        foreach (double seconds in new[] { Day, 365.25 * Day, 100.0 * 365.25 * Day })
        {
            MissionTimes times = Legs(seconds);
            SupplyVerdict? ours = MissionSupplyCheck.Compute(tree, inEditor: false, in times);
            SupplyVerdict? stock = StockVerdict(tree, in times);
            HarnessLog.Line(FormattableString.Invariant(
                $"{Tag} supplies over {seconds / Day:F1} d legs: ours {Describe(ours)}, stock {Describe(stock)}"));
            ok &= Check(FormattableString.Invariant($"supplies over {seconds / Day:F1} d legs equal stock's"),
                ours.HasValue == stock.HasValue
                && (ours is not { } o || stock is not { } s
                    || (o.Short == s.Short && o.Supply == s.Supply && o.Zone == s.Zone && o.ZoneCount == s.ZoneCount
                        && Near(o.ShortBySeconds, s.ShortBySeconds))));
        }

        // A flight read on a fresh cache must not build pending habitat zones.
        tree.MarkDerivedDirty(DerivedData.HabitatZones);
        MissionSupplyCheck.Reset();
        var host = new MissionHost(tree, vehicle, false);
        MissionTimes legs = Legs(Day);
        bool waited = MissionSupplyCheck.Read(in host, in legs) == null;
        bool stillPending = DerivedGate.HasPending(tree, DerivedData.HabitatZones);
        PartTree.FlushDirtyDerived();
        ok &= Check("a flight supply read waits for the derived-data flush", waited && stillPending);
        ok &= Check("a flight supply read after the flush gives a verdict", MissionSupplyCheck.Read(in host, in legs) != null);
        return ok;
    }

    private static SupplyVerdict? Probe(PartTree tree, double seconds)
    {
        MissionTimes times = Legs(seconds);
        return MissionSupplyCheck.Compute(tree, inEditor: false, in times);
    }

    private static MissionTimes Legs(double seconds)
    {
        return new MissionTimes(MissionTimesStatus.Planned, seconds, seconds, seconds, 0.0, seconds, false);
    }

    private static SupplyVerdict? StockVerdict(PartTree tree, in MissionTimes times)
    {
        ReadOnlySpan<HabitatZone> zones = tree.EnsureHabitatZones();
        var forecasts = new List<LifeSupportForecast>();
        var missions = new List<ZoneMission>();
        MissionLegs legs = MissionLegs.FromSeconds(MissionLegsStatus.Planned,
            times.OutSeconds, times.StaySeconds, times.BackSeconds, times.ReturnWaitSeconds);
        LifeSupport.ComputeZoneForecasts(tree, tree.Moles.States, null, seatsWhenUnassigned: false, LifeSupport.GetFlightAir(tree), forecasts);
        MissionSupplies.Compute(tree, zones, forecasts, in legs, missions);
        int i = MissionSupplies.FindShortest(missions);
        if (i < 0)
            return null;
        return new SupplyVerdict(missions[i].Verdict == MissionSupplyVerdict.Short, (SupplyKind)(byte)missions[i].FirstSupply,
            missions[i].ShortBy.Seconds(), i + 1, zones.Length);
    }

    private static string Describe(SupplyVerdict? verdict)
    {
        if (verdict is not { } v)
            return "none";
        return FormattableString.Invariant($"{(v.Short ? "short" : "enough")} {v.Supply} by {v.ShortBySeconds:F0} s, zone {v.Zone}/{v.ZoneCount}");
    }

    // The flight write round trip: a body with Orbit Return 3 d over a mission with a recorded
    // arrival, low orbit, and a body onto a vessel without a mission. The original plan is put back.
    private static bool CheckWrite(Vehicle vehicle, Astronomical home)
    {
        PartTree tree = vehicle.Parts;
        MissionPlan? original = tree.Mission?.Clone();
        Astronomical? target = null;
        foreach (Astronomical body in PickerBodies((IParentBody)home))
        {
            if (!MissionTiming.IsInterstellar((IParentBody)home, (IParentBody)body) && ChainIsBound((IParentBody)body))
            {
                target = body;
                break;
            }
        }
        if (target == null)
        {
            HarnessLog.Line($"{Tag} SKIP mission write: stock lists no body in the home system.");
            return true;
        }

        bool ok = true;
        try
        {
            MissionHost host = MissionReader.ResolveHost() ?? new MissionHost(tree, vehicle, false);
            ok &= Check("the controlled vessel is the flight host", !host.InEditor && ReferenceEquals(host.Tree, tree));
            string? homeId = MissionReader.HomeBodyId();
            ok &= Check("a stock mission accepts the target and the home body",
                MissionWriter.RefusalFor(target, (IParentBody)home) == null && MissionWriter.RefusalFor(home, (IParentBody)home) == null);

            tree.Mission = new MissionPlan
            {
                Destination = MissionDestination.Body,
                BodyId = home.Id,
                ArrivedAt = new UniverseTime(1.0),
                LeftAt = new UniverseTime(2.0),
            };
            MissionWrite body = MissionRoutes.PlanFor(target.Id, homeId, RouteEnd.Orbit, true, false, 3.0 * Day, 0.1);
            ok &= CheckOneWrite("a body with orbit and return", host, body,
                new MissionSnapshot(MissionGoal.Body, target.Id, RouteArrival.Orbit, true, 3.0 * Day, 0.1, false, false));

            MissionWrite lowOrbit = MissionRoutes.PlanFor(home.Id, homeId, RouteEnd.Orbit, false, false, Day, 0.1);
            ok &= CheckOneWrite("low orbit", host, lowOrbit,
                new MissionSnapshot(MissionGoal.LowOrbit, "", RouteArrival.Orbit, false, Day, 0.1, false, false));

            tree.Mission = null;
            MissionWrite landing = MissionRoutes.PlanFor(target.Id, homeId, RouteEnd.Surface, true, false, 2.0 * Day, 0.1);
            ok &= CheckOneWrite("a body onto a vessel without a mission", host, landing,
                new MissionSnapshot(MissionGoal.Body, target.Id, RouteArrival.Land, true, 2.0 * Day, 0.1, false, false));
        }
        finally
        {
            tree.Mission = original;
        }
        return ok;
    }

    private static bool CheckOneWrite(string what, MissionHost host, MissionWrite plan, MissionSnapshot expected)
    {
        bool written = MissionWriter.Write(in host, in plan);
        MissionPlan? mission = host.Tree.Mission;
        MissionSnapshot? read = MissionReader.Read(host.Tree);
        bool ok = written && mission != null
            && read is { } r
            && r.Destination == expected.Destination
            && r.BodyId == expected.BodyId
            && r.Arrival == expected.Arrival
            && r.Return == expected.Return
            && Near(r.StaySeconds, expected.StaySeconds)
            && Near(r.CruiseSpeedC, expected.CruiseSpeedC)
            && !r.Custom && !r.Arrived
            && !mission.ArrivedAt.HasValue && !mission.LeftAt.HasValue
            && MissionPicker.ComputeLegs(mission).Status == MissionLegsStatus.Planned;
        return Check($"the flight write of {what} reads back as written and plans in stock", ok);
    }

    // With one circuit the whole-vessel balance is stock's per-circuit forecast of the probe core.
    private static bool CheckProbePower(Vehicle vehicle)
    {
        PartTree tree = vehicle.Parts;
        Part? core = FindProbeCorePart(tree);
        if (core == null)
        {
            HarnessLog.Line($"{Tag} SKIP probe power: the vessel has no probe core with a power switch.");
            return true;
        }
        if (tree.ElectricalCircuits.CircuitCount != 1)
        {
            HarnessLog.Line($"{Tag} SKIP probe power: the vessel has {tree.ElectricalCircuits.CircuitCount} circuits.");
            return true;
        }

        ProbeReading ours = ProbePower.Compute(tree);
        if (ours.State != ProbeState.Powered)
        {
            HarnessLog.Line($"{Tag} SKIP probe power: the probe core is {ours.State}.");
            return true;
        }

        PowerForecast stock = PowerForecast.Compute(tree, core);
        double stockRemaining = stock.Remaining.IsEndOfTime() ? double.PositiveInfinity : stock.Remaining.Seconds();
        HarnessLog.Line(FormattableString.Invariant(
            $"{Tag} probe power: ours {ours.Drain} {ours.RemainingSeconds:F1} s, stock {stock.Drain} {stockRemaining:F1} s"));
        bool sameRemaining = double.IsPositiveInfinity(ours.RemainingSeconds) && double.IsPositiveInfinity(stockRemaining)
            || Math.Abs(ours.RemainingSeconds - stockRemaining) <= 1e-3 * Math.Max(1.0, Math.Abs(stockRemaining));
        bool ok = Check("probe power drains as stock forecasts", (byte)ours.Drain == (byte)stock.Drain && sameRemaining);

        ProbePower.Reset();
        ok &= Check("a probe power read gives the uncached reading", ProbePower.Read(vehicle) == ours);
        return ok;
    }

    private static Part? FindProbeCorePart(PartTree tree)
    {
        tree.EnsureHabitatZones();
        foreach (Control control in tree.Modules.Get<Control>())
        {
            if (HabitatZone.FindZone(tree, control.Parent) != null)
                continue;
            foreach (PowerConsumer consumer in control.Parent.Modules.Get<PowerConsumer>())
            {
                if (consumer.PowerSwitch)
                    return consumer.Parent.FullPart;
            }
        }
        return null;
    }

    // The bodies MissionPicker.DrawDestinationOptions lists: under each parent of the home root,
    // every star and every body on a bound orbit other than the home body, then the root star of
    // every other system with its children (or the children of a root that is no star).
    private static List<Astronomical> PickerBodies(IParentBody home)
    {
        var list = new List<Astronomical>();
        IParentBody homeRoot = home;
        while (homeRoot is IOrbiter orbiter && orbiter.Parent != null)
            homeRoot = orbiter.Parent;
        AddChildren(list, homeRoot, home);
        foreach (IIndependentRoot root in Universe.Roots)
        {
            if (root == homeRoot)
                continue;
            if (root.IsStar() && root is Astronomical star)
                list.Add(star);
            AddChildren(list, root, home);
        }
        return list;
    }

    private static void AddChildren(List<Astronomical> list, IParentBody parent, IParentBody home)
    {
        foreach (IOrbiter child in parent.Children)
        {
            if (child is not IParentBody body || child is Vehicle)
                continue;
            if (child is StellarBody || (child is Celestial celestial && !ReferenceEquals(celestial, home) && celestial.Orbit.IsBound()))
                list.Add((Astronomical)child);
            if (body.HasChildren())
                AddChildren(list, body, home);
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

    private static bool Near(double actual, double expected)
    {
        return Math.Abs(actual - expected) <= TimeTolerance * Math.Max(1.0, Math.Abs(expected));
    }

    private static bool Check(string what, bool pass)
    {
        HarnessLog.Line($"{Tag} TEST {what} => {TestSupport.Verdict(pass)}");
        return pass;
    }
}
