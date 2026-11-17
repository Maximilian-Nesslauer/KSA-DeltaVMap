using System;
using System.Collections.Generic;
using DeltaVMap.Core;
using KSA;

namespace DeltaVMap.Mission;

// Air, water, food and CO2 scrubbers aboard against the legs of a mission, through the same stock
// calls the transfer planner's mission line (MissionTransfer.DrawSupplies) and the life support
// window (LifeSupportWindow.DrawContent) make. The habitat zones come from
// PartTree.EnsureHabitatZones, which runs PartTree.EnsureDerived, so a flight read waits behind
// DerivedGate as the staged delta-v readout does. The supply states (PartTree.Moles and the habitat
// states) are read during the vehicle solver batch, as the staged delta-v readout and stock's own
// windows read them.
internal static class MissionSupplyCheck
{
    // The supplies drain slowly against mission times of days and more, so a second between
    // recomputes cannot move the verdict noticeably.
    private const double RecomputeIntervalSeconds = 1.0;

    private readonly record struct Key(PartTree Tree, bool InEditor, double Out, double Stay, double Back, double Wait);

    private static readonly List<LifeSupportForecast> _forecasts = new();
    private static readonly List<ZoneMission> _missions = new();

    private static Key? _key;
    private static SupplyVerdict? _verdict;
    private static double _lastComputeTime = double.NegativeInfinity;

    // The verdict of the supplies aboard against times, or null when there is nothing to show
    // (life support rule off, no timing, no habitat, or no kitten on any leg).
    internal static SupplyVerdict? Read(in MissionHost host, in MissionTimes times)
    {
        try
        {
            var key = new Key(host.Tree, host.InEditor, times.OutSeconds, times.StaySeconds, times.BackSeconds, times.ReturnWaitSeconds);
            // PartTree does not override Equals, so the key compares the tree by reference.
            bool sameKey = _key == key;
            double now = Program.GetPlayerTime();
            // The backwards test catches a clock reset (new game, save load).
            bool intervalElapsed = now - _lastComputeTime >= RecomputeIntervalSeconds || now < _lastComputeTime;
            if (sameKey && !intervalElapsed)
                return _verdict;

            // Pending work clears at the next PrepareFrame. The editor does not wait, because no
            // job touches the editor tree during the draw. Until then the craft keeps its last
            // verdict, also for new times, so the line under the vehicle bar does not flicker; the
            // key stays old, so the next read computes the new times.
            if (!host.InEditor && DerivedGate.HasPending(host.Tree, DerivedData.HabitatZones))
            {
                bool sameCraft = _key is { } last && ReferenceEquals(last.Tree, host.Tree) && !last.InEditor;
                return sameCraft && times.Status == MissionTimesStatus.Planned ? _verdict : null;
            }

            _key = key;
            _lastComputeTime = now;
            _verdict = Compute(host.Tree, host.InEditor, in times);
            return _verdict;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("mission-supplies", $"[DvMap] Mission supply check failed: {ex}");
            Reset();
            return null;
        }
    }

    // The uncached check, for the harness. It runs PartTree.EnsureHabitatZones without the gate.
    internal static SupplyVerdict? Compute(PartTree tree, bool inEditor, in MissionTimes times)
    {
        if (!Universe.Rules.LifeSupport)
            return null;
        if (times.Status != MissionTimesStatus.Planned || !(times.TotalSeconds > 0.0))
            return null;
        if (tree.Moles == null)
            return null;

        ReadOnlySpan<HabitatZone> zones = tree.EnsureHabitatZones();
        if (zones.Length == 0)
            return null;

        MissionLegs legs = MissionLegs.FromSeconds(MissionLegsStatus.Planned,
            times.OutSeconds, times.StaySeconds, times.BackSeconds, times.ReturnWaitSeconds);
        // LifeSupport.ComputeZoneForecasts appends to the list, and MissionSupplies.Compute reads
        // its entries by zone index, so a list that is not cleared first gives the forecasts of
        // the call before.
        _forecasts.Clear();
        // Flight as MissionTransfer.DrawSupplies, the editor as LifeSupportWindow, which counts
        // the seats when no kitten is assigned yet.
        LifeSupport.ComputeZoneForecasts(tree, tree.Moles.States, null, seatsWhenUnassigned: inEditor,
            LifeSupport.GetFlightAir(tree), _forecasts);
        MissionSupplies.Compute(tree, zones, _forecasts, in legs, _missions);
        int shortest = MissionSupplies.FindShortest(_missions);
        // No kitten on any leg: the craft is not crewed for this route. Stock would print ENOUGH;
        // the map shows nothing instead.
        if (shortest < 0)
            return null;

        ZoneMission zone = _missions[shortest];
        SupplyKind supply = zone.FirstSupply switch
        {
            MissionSupply.Air => SupplyKind.Air,
            MissionSupply.Scrubber => SupplyKind.Scrubber,
            MissionSupply.Water => SupplyKind.Water,
            _ => SupplyKind.Food,
        };
        return new SupplyVerdict(zone.Verdict == MissionSupplyVerdict.Short, supply, zone.ShortBy.Seconds(), shortest + 1, zones.Length);
    }

    internal static void Reset()
    {
        _key = null;
        _verdict = null;
        _lastComputeTime = double.NegativeInfinity;
        _forecasts.Clear();
        _missions.Clear();
    }
}
