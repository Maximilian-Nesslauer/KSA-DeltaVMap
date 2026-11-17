using System;
using DeltaVMap.Core;
using KSA;

namespace DeltaVMap.Mission;

// The probe core power of an uncrewed vessel in flight, for comparison with a mission time.
//
// The state mirrors stock's private Vehicle.ScanControls and Vehicle.ScanProbeCore from the power
// holds stock has already written, without their PowerConsumer.Hold writes. "Unpowered" is the
// first branch of PowerConsumer.HasPower, the consumer's switched-on state, because its second
// branch goes through PowerConsumer.PowerManager, whose getter runs
// PartTree.EnsureResourceManagersBuilt and can rebuild every resource manager of the vessel.
//
// The remaining time mirrors PowerForecast.Compute summed over the whole vessel instead of over the
// probe core's circuit, because PartTree.ElectricalCircuits rebuilds a dirty circuit set, and
// PartTree.RecreateResourceManagers leaves the set dirty for the vehicle solver batch that builds
// it during the draw. For a vessel with one circuit the result is stock's; otherwise every battery
// aboard counts. Re-verify these stock paths on each game update.
internal static class ProbePower
{
    // The balance moves with sunlight and the loads switched on, slowly against mission times.
    private const double RecomputeIntervalSeconds = 1.0;

    private static PartTree? _tree;
    private static ProbeReading? _reading;
    private static double _lastComputeTime = double.NegativeInfinity;

    // The reading of a vessel in flight, null when a read failed.
    internal static ProbeReading? Read(Vehicle vehicle)
    {
        try
        {
            PartTree tree = vehicle.Parts;
            bool sameTree = ReferenceEquals(tree, _tree);
            double now = Program.GetPlayerTime();
            // The backwards test catches a clock reset (new game, save load).
            bool intervalElapsed = now - _lastComputeTime >= RecomputeIntervalSeconds || now < _lastComputeTime;
            if (sameTree && !intervalElapsed)
                return _reading;

            // EnsureHabitatZones below runs PartTree.EnsureDerived. Pending work clears at the next
            // PrepareFrame.
            if (DerivedGate.HasPending(tree, DerivedData.HabitatZones))
                return sameTree ? _reading : null;

            _tree = tree;
            _lastComputeTime = now;
            _reading = Compute(tree);
            return _reading;
        }
        catch (Exception ex)
        {
            // The tree and the timestamp stay, so a read that keeps failing runs again only once
            // per interval.
            _reading = null;
            LogHelper.WarnOnce("probe-power", (Vessel: vehicle.Id, Error: ex),
                static s => $"[DvMap] Probe power read of '{s.Vessel}' failed: {s.Error}");
            return null;
        }
    }

    // The uncached reading, for the harness. It runs PartTree.EnsureHabitatZones without the gate.
    internal static ProbeReading Compute(PartTree tree)
    {
        if (tree.Controls.NumModules == 0)
            return new ProbeReading(ProbeState.None, DrainKind.NotDraining, 0.0);
        tree.EnsureHabitatZones();

        ProbeState state = ProbeState.None;
        // Stock takes the longest remaining time of the powered cores, and a core without a
        // power switch has no end.
        bool unlimited = false;
        Span<Control> controls = tree.Modules.Get<Control>();
        for (int i = 0; i < controls.Length; i++)
        {
            Control control = controls[i];
            // A control inside a habitat zone is a command pod, not a probe core.
            if (HabitatZone.FindZone(tree, control.Parent) != null)
                continue;

            PowerConsumer? consumer = FindProbeConsumer(control.Parent);
            if (consumer == null)
            {
                // A core without a power switch never runs out. Stock also reports such a core
                // failed or rebooting from its avionics; that hold is not mirrored here.
                state = Max(state, ProbeState.Powered);
                unlimited = true;
                continue;
            }

            state = Max(state, StateOf(consumer));
        }

        if (state != ProbeState.Powered)
            return new ProbeReading(state, DrainKind.NotDraining, 0.0);
        if (unlimited)
            return new ProbeReading(state, DrainKind.NotDraining, double.PositiveInfinity);

        (DrainKind drain, double remaining) = Balance(tree);
        return new ProbeReading(state, drain, remaining);
    }

    internal static void Reset()
    {
        _tree = null;
        _reading = null;
        _lastComputeTime = double.NegativeInfinity;
    }

    // The order of Vehicle.ScanProbeCore.
    private static ProbeState StateOf(PowerConsumer consumer)
    {
        if (consumer.Hold == PowerHold.Failed)
            return ProbeState.Failed;
        if (consumer.Hold == PowerHold.StormStandby)
            return ProbeState.StormStandby;
        if (consumer.Mode == PowerSwitchMode.Off)
            return ProbeState.SwitchedOff;
        if (!consumer.IsSwitchedOn())
            return ProbeState.Unpowered;
        if (consumer.Hold == PowerHold.Rebooting)
            return ProbeState.Rebooting;
        return ProbeState.Powered;
    }

    // The sums of PowerForecast.Compute over every circuit of the vessel.
    private static (DrainKind Drain, double RemainingSeconds) Balance(PartTree tree)
    {
        ReadOnlySpan<BatteryState> batteryStates = tree.Batteries.States;
        Span<Battery> batteries = tree.Batteries.Modules;
        double charge = 0.0;
        for (int i = 0; i < batteries.Length; i++)
            charge += batteryStates[batteries[i].StatesIdx].Charge.Value();

        ReadOnlySpan<PowerConsumerState> consumerStates = tree.PowerConsumers.States;
        Span<PowerConsumer> consumers = tree.PowerConsumers.Modules;
        double draw = 0.0;
        for (int i = 0; i < consumers.Length; i++)
        {
            if (consumers[i].IsWanted(consumerStates))
                draw += consumers[i].Consumed.Value();
        }

        double supply = 0.0;
        ReadOnlySpan<SolarPanelState> solarStates = tree.SolarPanels.States;
        Span<SolarPanel> panels = tree.SolarPanels.Modules;
        for (int i = 0; i < panels.Length; i++)
            supply += solarStates[panels[i].StatesIdx].Produced.Value();
        ReadOnlySpan<GeneratorState> generatorStates = tree.Generators.States;
        Span<Generator> generators = tree.Generators.Modules;
        for (int i = 0; i < generators.Length; i++)
            supply += generatorStates[generators[i].StatesIdx].Produced.Value();

        return PowerMath.Forecast(batteries.Length > 0, charge, draw, supply);
    }

    // A mirror of the private Vehicle.FindProbeConsumer: the first consumer with a power switch.
    private static PowerConsumer? FindProbeConsumer(Part part)
    {
        Span<PowerConsumer> consumers = part.Modules.Get<PowerConsumer>();
        for (int i = 0; i < consumers.Length; i++)
        {
            if (consumers[i].PowerSwitch)
                return consumers[i];
        }
        return null;
    }

    private static ProbeState Max(ProbeState a, ProbeState b) => a >= b ? a : b;
}
