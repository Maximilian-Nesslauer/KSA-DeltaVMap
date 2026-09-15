using System;
using DeltaVMap.Core;
using KSA;

namespace DeltaVMap.Dv;

// Remaining staged delta-v of the controlled (flight) or editor vehicle, taken from the game's own
// SequencePerformanceList instead of a re-derived walk. Stock runs an event-driven per-reactant
// drain simulation with cutoff groups, cross-stage fuel attribution and solid residue, and
// integrates Tsiolkovsky piecewise across the drain phases as engines cut out. A hand-rolled walk
// cannot match that, and cannot keep matching it across game updates.
//
// The list is always a private instance, never the vehicle's shared one:
//   - in flight stock refreshes the shared list only while the staging window or the engine
//     control gauge is open (PhysicsBubble.RunVehiclePostWorkInner), so reading it would freeze
//     this readout;
//   - stock double-buffers the published SequencePerformance array, so the per-sequence entries
//     read here are rewritten in place two recomputes later.
// A private instance touches nothing outside itself. The only write SequencePerformanceList makes
// to the part tree is PartTree.HasUnsavedChanges inside SetDirty, which RecomputeForFlight does not
// call; everything else it mutates is its own scratch. Re-verify that on a game update, because a
// scratch field moving to a static would make this silently racy.
//
// Called from the draw path, which is NOT ordered after the vehicle solver: Program.PrepareFrame
// joins JobSystems.VehicleSolver and publishes results at its start, then queues the next batch
// through Universe.ExecuteNextVehicleSolvers before Program.OnFrame draws. So this reads the part
// tree with a solver batch in flight, exactly as stock's own draw-thread reads of
// PartTree.PerformanceSequences do; what keeps it safe is the private analyzer above, not an
// ordering guarantee.
internal static class StagedDv
{
    // A stock recompute costs single-digit milliseconds, far too much to run per frame on the draw
    // thread. It only has to run when the answer can have moved, which is why the propellant and
    // part-count signature below gates it; this interval is the floor between recomputes while that
    // signature keeps changing, as it does throughout a burn.
    private const double RecomputeIntervalSeconds = 0.25;

    // Propellant mass wanders by rounding alone while nothing is burning, so ignore changes that
    // cannot move a delta-v readout.
    private const double PropellantEpsilonKg = 0.05;

    private static PartTree? _tree;
    private static SequencePerformanceList? _analyzer;
    private static double? _totalDv;
    private static double _lastComputeTime = double.NegativeInfinity;
    private static double _lastPropellantMass = double.NaN;
    private static int _lastPartCount = -1;
    private static int _lastSequenceCount = -1;

    internal static double? TryTotalDv()
    {
        try
        {
            PartTree? tree = ResolveTree(out bool inFlight);
            if (tree == null || tree.Count == 0)
            {
                Forget();
                return null;
            }

            if (!ReferenceEquals(tree, _tree))
            {
                Forget();
                _tree = tree;
                _analyzer = new SequencePerformanceList(tree);
            }

            double now = Program.GetPlayerTime();
            // The backwards test catches a clock reset (new game, save load) so the readout cannot
            // latch until the old timestamp is reached again.
            bool intervalElapsed = now - _lastComputeTime >= RecomputeIntervalSeconds || now < _lastComputeTime;

            double propellant = SumPropellantMass(tree);
            int partCount = tree.Count;
            int sequenceCount = tree.SequenceList.Sequences.Length;
            bool changed = partCount != _lastPartCount
                || sequenceCount != _lastSequenceCount
                || double.IsNaN(_lastPropellantMass)
                || Math.Abs(propellant - _lastPropellantMass) >= PropellantEpsilonKg;

            if (_totalDv == null || (intervalElapsed && changed))
            {
                // The baseline is the state the cached total was computed from, so it moves only
                // here. Advancing it on a frame that skipped the recompute would let a slow drain
                // stay under the epsilon forever and freeze the readout.
                _lastComputeTime = now;
                _lastPropellantMass = propellant;
                _lastPartCount = partCount;
                _lastSequenceCount = sequenceCount;
                _totalDv = Compute(_analyzer!, tree, inFlight);
            }
            return _totalDv;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("staged-dv", $"[DvMap] Staged dV read failed: {ex}");
            Forget();
            return null;
        }
    }

    internal static void Reset() => Forget();

    private static void Forget()
    {
        _tree = null;
        _analyzer = null;
        _totalDv = null;
        _lastComputeTime = double.NegativeInfinity;
        _lastPropellantMass = double.NaN;
        _lastPartCount = -1;
        _lastSequenceCount = -1;
    }

    // Half of the change probe: propellant drains during a burn and is consumed or added by an
    // edit. A walk over a few dozen floats, cheap enough to run on every frame, which is what keeps
    // a coasting vehicle or an untouched editor from recomputing at all.
    private static double SumPropellantMass(PartTree tree)
    {
        var moles = tree.Moles;
        if (moles == null)
            return 0.0;

        double mass = 0.0;
        ReadOnlySpan<MoleState> states = moles.States;
        for (int i = 0; i < states.Length; i++)
            mass += states[i].Mass;
        return mass;
    }

    private static PartTree? ResolveTree(out bool inFlight)
    {
        Vehicle? vehicle = Program.ControlledVehicle;
        inFlight = vehicle != null;
        if (vehicle != null)
            return vehicle.Parts;
        return Program.Editor?.EditingSpace.Parts;
    }

    private static double Compute(SequencePerformanceList analyzer, PartTree tree, bool inFlight)
    {
        // Each sequence drains at its own Environment toggle in both modes. Flight mode matches
        // stock's flight readout, where the ambient pressure pins the display thrust of the active
        // sequence and a sequence numbered below SequenceList.ActiveSequence registers no engines.
        // Stock's editor readout runs the plain recompute, and inserting a sequence in the editor
        // raises ActiveSequence, so flight mode there would drop whole stages. The editor gets a
        // fresh list instead, which starts dirty, because SetDirty would also mark the craft as
        // unsaved.
        if (inFlight)
        {
            analyzer.RecomputeForFlight(0f);
        }
        else
        {
            analyzer = new SequencePerformanceList(tree);
            analyzer.RecomputeIfDirty();
        }

        ReadOnlySpan<Sequence> sequences = tree.SequenceList.Sequences;
        ReadOnlySpan<SequencePerformance> perf = analyzer.PerformanceSequences;
        int count = Math.Min(sequences.Length, perf.Length);

        double total = 0.0;
        for (int i = 0; i < count; i++)
            total += perf[i].DeltaV;
        return total;
    }
}
