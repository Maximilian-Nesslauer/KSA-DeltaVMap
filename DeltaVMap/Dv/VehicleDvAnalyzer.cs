using System;
using System.Collections.Generic;
using CommunityToolkit.HighPerformance.Buffers;
using DeltaVMap.Core;
using KSA;

namespace DeltaVMap.Dv;

// Self-contained staged delta-v analyzer for the controlled (flight) or editor vehicle.
// The game's NavBallData.DeltaVInVacuum is a single-stage rocket equation over the whole
// vehicle (one blended exhaust velocity, total wet / total dry mass), so it hides the dV
// a staged vehicle actually has: a high-Isp upper stage gets averaged in with the boosters
// and the staged gain is lost. This walks the part tree's activation sequences instead.
// For each sequence, in firing order: its decouplers jettison their subtrees, then its
// engines burn the fuel reachable in that stage, and Tsiolkovsky gives the stage dV; the
// sum is the vehicle total.
//
// The logic is adapted from the StageInfo mod's analyzer, trimmed to the vacuum total the
// route bar needs: no ambient pressure, no TWR, burn time or per-stage breakdown. Kept in
// this mod so DeltaVMap does not depend on any other mod being installed. Main-thread only
// (the reusable scratch collections are not synchronized).
internal static class VehicleDvAnalyzer
{
    private const float MinMassFlowRate = 1e-6f;

    // Floor on dry mass so the Tsiolkovsky log stays finite if the fuel walk ever sums to
    // (or past) the whole stage mass.
    private const float MinDryMass = 1f;

    private static readonly HashSet<uint> _jettisonedPartIds = new();
    private static readonly HashSet<ulong> _fuelClaimedTankIds = new();
    private static readonly List<EngineController> _engines = new();

    // Total staged vacuum dV of the controlled vehicle, or null when there is no vehicle or
    // the walk fails (surfaced once, never thrown into the draw path).
    public static double? TryControlledVehicleDv()
    {
        try
        {
            Vehicle? vehicle = Program.ControlledVehicle;
            if (vehicle == null)
                return null;
            return ComputeTotalDv(vehicle.Parts, vehicle.TotalMass);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("vehicle-dv", $"[DvMap] Vehicle dV analysis failed: {ex.Message}");
            return null;
        }
    }

    // Total staged vacuum dV of the vehicle under construction in the editor, or null when
    // there is no editor vehicle or the walk fails.
    public static double? TryEditorVehicleDv()
    {
        try
        {
            VehicleEditor? editor = Program.Editor;
            if (editor == null)
                return null;
            PartTree? parts = editor.EditingSpace.Parts;
            if (parts == null || parts.Count == 0 || parts.Moles == null)
                return null;

            float totalMass = (parts.ComputeInertMassPropertiesAsmb()
                + parts.ComputePropellantMassPropertiesAsmb()).Props.Mass;
            if (!(totalMass > 0f))
                return null;

            return ComputeTotalDv(parts, totalMass);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("editor-dv", $"[DvMap] Editor dV analysis failed: {ex.Message}");
            return null;
        }
    }

    private static double ComputeTotalDv(PartTree parts, float totalMass)
    {
        _jettisonedPartIds.Clear();
        _fuelClaimedTankIds.Clear();

        // Sequences are iterated in ascending activation order, on which the running-mass
        // propagation depends. The game keeps SequenceList sorted by Number.
        ReadOnlySpan<Sequence> sequences = parts.SequenceList.Sequences;
        ReadOnlySpan<MoleState> moleStates = parts.Moles.States;

        float currentMass = totalMass;
        double total = 0.0;

        for (int si = 0; si < sequences.Length; si++)
        {
            Sequence sequence = sequences[si];
            if (sequence.Parts.IsEmpty)
                continue;

            // Decouplers in this sequence fire and jettison their subtrees.
            currentMass -= ComputeJettisonedMass(sequence, moleStates);

            // An already-activated sequence counts only its still-active engines (the pilot
            // may have shut some down); a future sequence counts all of them.
            CollectEngines(sequence, sequence.Activated);
            if (_engines.Count == 0)
                continue;

            float totalThrust = 0f;
            float totalFlow = 0f;
            foreach (EngineController engine in _engines)
            {
                totalThrust += engine.VacuumData.ThrustMax.Length();
                totalFlow += engine.VacuumData.MassFlowRateMax;
            }
            if (totalFlow < MinMassFlowRate)
                continue;

            float ve = totalThrust / totalFlow;
            float fuelMass = ComputeSequenceFuel(moleStates);

            float burnable = Math.Min(fuelMass, Math.Max(0f, currentMass - MinDryMass));
            if (burnable <= 0f)
                continue;

            float startMass = currentMass;
            float endMass = currentMass - burnable;
            total += ve * Math.Log(startMass / endMass);
            currentMass = endMass;
        }

        return total;
    }

    private static void CollectEngines(Sequence sequence, bool sequenceActivated)
    {
        _engines.Clear();
        ReadOnlySpan<Part> parts = sequence.Parts;
        for (int pi = 0; pi < parts.Length; pi++)
        {
            Part part = parts[pi];
            if (_jettisonedPartIds.Contains(part.InstanceId))
                continue;

            Span<EngineController> engines = part.Modules.Get<EngineController>();
            for (int ei = 0; ei < engines.Length; ei++)
            {
                EngineController engine = engines[ei];
                if (sequenceActivated && !engine.IsActive)
                    continue;
                _engines.Add(engine);
            }
        }
    }

    // Reachable propellant via each engine core's SameStage tank list. Tanks are claimed
    // once so a later sequence (or a jettison walk) does not count the same fuel twice.
    private static float ComputeSequenceFuel(ReadOnlySpan<MoleState> moleStates)
    {
        float total = 0f;
        foreach (EngineController engine in _engines)
        {
            foreach (RocketCore core in engine.Cores)
            {
                if (core.ResourceManager == null)
                    continue;
                total += WalkSameStage(core.ResourceManager, moleStates);
            }
        }
        return total;
    }

    private static float WalkSameStage(ResourceManager resourceManager, ReadOnlySpan<MoleState> moleStates)
    {
        MemoryOwner<MemoryOwner<Tank>>? nodes = resourceManager.FurtherestToNearestNodeSameStage;
        if (nodes == null || nodes.Length == 0)
            return 0f;

        float current = 0f;
        Span<MemoryOwner<Tank>> nodeSpan = nodes.Span;
        for (int i = 0; i < nodeSpan.Length; i++)
        {
            if (nodeSpan[i] == null || nodeSpan[i].Length == 0)
                continue;

            Span<Tank> tanks = nodeSpan[i].Span;
            for (int j = 0; j < tanks.Length; j++)
            {
                Tank tank = tanks[j];
                if (tank == null)
                    continue;
                if (!_fuelClaimedTankIds.Add(tank.InstanceId))
                    continue;
                current += tank.ComputeSubstanceMass(moleStates);
            }
        }
        return current;
    }

    // Sum of the subtree masses downstream of each decoupler in this sequence. A tank
    // already claimed as fuel by an earlier sequence contributes no propellant here.
    private static float ComputeJettisonedMass(Sequence sequence, ReadOnlySpan<MoleState> moleStates)
    {
        float total = 0f;
        ReadOnlySpan<Part> parts = sequence.Parts;
        for (int pi = 0; pi < parts.Length; pi++)
        {
            Part part = parts[pi];
            if (!part.Modules.HasAny<Decoupler>())
                continue;
            total += CollectSubtreeMass(part, moleStates);
        }
        return total;
    }

    private static float CollectSubtreeMass(Part part, ReadOnlySpan<MoleState> moleStates)
    {
        if (!_jettisonedPartIds.Add(part.InstanceId))
            return 0f;

        float mass = ComputePartMass(part, moleStates);
        List<Part> children = part.TreeChildren;
        for (int i = 0; i < children.Count; i++)
            mass += CollectSubtreeMass(children[i], moleStates);
        return mass;
    }

    private static float ComputePartMass(Part part, ReadOnlySpan<MoleState> moleStates)
    {
        float mass = SumComponentMass(part.Modules, moleStates);
        ReadOnlySpan<Part> subParts = part.SubParts;
        for (int i = 0; i < subParts.Length; i++)
            mass += SumComponentMass(subParts[i].Modules, moleStates);
        return mass;
    }

    private static float SumComponentMass(ModuleList components, ReadOnlySpan<MoleState> moleStates)
    {
        float mass = 0f;
        Span<InertMass> inerts = components.Get<InertMass>();
        for (int i = 0; i < inerts.Length; i++)
            mass += inerts[i].MassPropertiesAsmb.Props.Mass;

        Span<Tank> tanks = components.Get<Tank>();
        for (int i = 0; i < tanks.Length; i++)
        {
            if (!_fuelClaimedTankIds.Contains(tanks[i].InstanceId))
                mass += tanks[i].ComputeSubstanceMass(moleStates);
        }
        return mass;
    }
}
