using System;
using System.Reflection;
using DeltaVMap.Core;
using HarmonyLib;
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
// The analyzer's scratch is its own, but Recompute calls PartTree.EnsureDerived for substance
// stores, solid motor stacks and sequences, and that rebuilds shared tree state whenever the tree
// has pending derived work.
//
// Called from the draw path, which is NOT ordered after the vehicle solver: Program.PrepareFrame
// joins JobSystems.VehicleSolver at its start, then flushes pending derived work
// (PartTree.FlushDirtyDerived) and queues the next batch through Universe.ExecuteNextVehicleSolvers
// before Program.OnFrame draws. Stock marks a flight tree dirty only on the main thread before that
// flush, so a tree that is clean at the draw stays clean while the batch runs and EnsureDerived only
// reads it. A flight recompute therefore waits while the tree has pending derived work, instead of
// racing the solver's own EnsureDerived in PhysicsBubble.RunVehiclePostWorkInner. Re-verify on each
// game update the flush order, the main-thread-only dirtying, the private PartTree._derivedDirty
// field, and that no SequencePerformanceList scratch field has moved to a static.
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

    private const DerivedData RecomputeInputs =
        DerivedData.SubstanceStores | DerivedData.SolidMotorStacks | DerivedData.Sequences;

    private static readonly AccessTools.FieldRef<PartTree, DerivedData>? DerivedDirty = BindDerivedDirty();

    private static PartTree? _tree;
    private static SequencePerformanceList? _analyzer;
    private static double? _totalDv;
    private static double _lastComputeTime = double.NegativeInfinity;
    private static double _lastPropellantMass = double.NaN;
    private static int _lastPartCount = -1;
    private static int _lastSequenceCount = -1;

    // Owned copy of the per-sequence masses from the last recompute, because stock rewrites its
    // published array in place two recomputes later.
    private static StageMass[] _stages = Array.Empty<StageMass>();
    private static int _stageCount;

    // Bumped on every recompute and when a reading is dropped, so a consumer can key its own
    // cache on the vessel's staged state without probing the part tree again.
    private static int _revision;

    internal static int Revision => _revision;

    // The staged sequences behind the last total, in firing order. Valid after TryTotalDv
    // returned a value; empty otherwise.
    internal static ReadOnlySpan<StageMass> Stages => _totalDv.HasValue ? _stages.AsSpan(0, _stageCount) : ReadOnlySpan<StageMass>.Empty;

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
                // Pending work clears at the next PrepareFrame, so the readout keeps its last
                // value, or shows n/a for a frame on a fresh tree.
                if (inFlight && HasPendingDerivedWork(tree))
                    return _totalDv;

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
        // Runs every frame while there is no vehicle, so only dropping a reading counts as a
        // change.
        if (_totalDv.HasValue)
            _revision++;
        _tree = null;
        _analyzer = null;
        _totalDv = null;
        _lastComputeTime = double.NegativeInfinity;
        _lastPropellantMass = double.NaN;
        _lastPartCount = -1;
        _lastSequenceCount = -1;
        _stageCount = 0;
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

    private static bool HasPendingDerivedWork(PartTree tree)
    {
        if (DerivedDirty == null)
        {
            LogHelper.WarnOnce("staged-dv-derived-dirty",
                "[DvMap] PartTree._derivedDirty not found, staged dV no longer waits for the derived-data flush.");
            return false;
        }
        return (DerivedDirty(tree) & RecomputeInputs) != DerivedData.None;
    }

    private static AccessTools.FieldRef<PartTree, DerivedData>? BindDerivedDirty()
    {
        FieldInfo? field = AccessTools.DeclaredField(typeof(PartTree), "_derivedDirty");
        return field != null && field.FieldType == typeof(DerivedData) && !field.IsStatic
            ? AccessTools.FieldRefAccess<PartTree, DerivedData>(field)
            : null;
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
        // unsaved. The editor recompute does not wait for the flush, because no job touches the
        // editor tree during the draw (stock queues its editor recompute after the UI draw), so any
        // derived rebuild it triggers is the one the next flush would run anyway.
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

        if (_stages.Length < count)
            _stages = new StageMass[count];
        double total = 0.0;
        for (int i = 0; i < count; i++)
        {
            total += perf[i].DeltaV;
            _stages[i] = new StageMass(perf[i].DeltaV, perf[i].WetMass, perf[i].BurnedFuelMass);
        }
        _stageCount = count;
        _revision++;
        return total;
    }
}
