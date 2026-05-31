using System;
using System.Collections.Generic;
using DeltaVMap.Core;
using DeltaVMap.Dv;
using KSA;

namespace DeltaVMap.Model;

// Builds the transfer-window list for the current map root and refreshes its live fields.
// From the root it walks the re-rooter to the nearest hub (the root's parent) and treats the
// other children of that hub as sibling destinations: for a planet root the other planets,
// for a moon root the other moons of the same planet. The siblings are filtered to the bodies
// the map currently shows as their own lane, so the table mirrors the canvas (the minor-body
// swarm aggregated into a "+N" group never floods it, and a searched / revealed body appears).
// For each kept sibling it reads the live orbital elements off the bodies and the cached
// transfer time and hyperbolic excess speed off the dV cache, then produces a
// TransferWindowInfo via the closed-form TransferWindow math.
//
// This is the only transfer-window file that touches game types; the timing arithmetic itself
// is the pure Dv/TransferWindow kernel. The phase / synodic branch structure mirrors the
// game's OrbitalTransfers.AlignmentTime, so if a future build changes that formula these
// figures will silently diverge from the stock alignment planner; re-verify against
// AlignmentTime when the game updates.
internal static class TransferWindows
{
    // Above this relative inclination the sibling orbits the hub the opposite way, and the
    // window math switches to its retrograde branch. Half a turn, matching the game's
    // OrbitalTransfers.AlignmentTime.
    private const double RetrogradeThreshold = Math.PI / 2.0;

    // A bound orbit eccentric beyond this is flagged approximate: the lead angle uses the
    // semi-major axis, so a noticeably elliptical sibling's true window drifts from the
    // closed-form estimate. Matches the circular threshold the rest of the map uses.
    private const double EccentricThreshold = DeltaVCalculator.CircularEccentricity;

    // Build a window per shown sibling of the root's nearest hub. visibleBodyIds is the set of
    // bodies the map draws as their own lane, so the table never lists the aggregated minor-body
    // swarm. Returns an empty list when the root has no hub above it (a star or parentless root
    // has no siblings to phase against).
    public static List<TransferWindowInfo> Build(
        DvCache cache, PhysicalNode root, IReadOnlySet<string> visibleBodyIds, SimTime now)
    {
        var list = new List<TransferWindowInfo>();

        ReRootResult reroot = ReRooter.ReRoot(root);
        if (reroot.Spine.Count == 0)
            return list;
        if (root.Astro is not IOrbiter sourceOrbiter)
            return list;

        HubLevel hub = reroot.Spine[0];
        Orbit sourceOrbit = sourceOrbiter.Orbit;

        foreach (PhysicalNode sibling in hub.OtherChildren)
        {
            // Only siblings the map shows as their own lane; the aggregated minor-body swarm is
            // not a destination here, exactly as on the canvas.
            if (!visibleBodyIds.Contains(sibling.Id))
                continue;

            TransferWindowInfo? info = TryBuildPair(cache, root, sourceOrbiter, sourceOrbit, sibling, now);
            if (info != null)
                list.Add(info);
        }

        // Soonest window first, the actionable order for the table and the highlight; any
        // non-finite countdown sorts last rather than to the top.
        list.Sort(static (a, b) => CompareCountdown(a.TimeToWindowSeconds, b.TimeToWindowSeconds));
        return list;
    }

    private static TransferWindowInfo? TryBuildPair(
        DvCache cache, PhysicalNode root, IOrbiter sourceOrbiter, Orbit sourceOrbit, PhysicalNode sibling, SimTime now)
    {
        try
        {
            if (sibling.Astro is not IOrbiter targetOrbiter)
                return null;
            Orbit targetOrbit = targetOrbiter.Orbit;

            double aSource = sourceOrbit.SemiMajorAxis;
            double aTarget = targetOrbit.SemiMajorAxis;
            double periodSource = sourceOrbit.Period;
            double periodTarget = targetOrbit.Period;

            // The closed-form lead angle and synodic period are only defined for two bound
            // orbits, so skip a sibling (or a source) whose semi-major axis or period is open
            // or otherwise non-finite. Open-orbit comets fall out here.
            if (!IsFinitePositive(aSource) || !IsFinitePositive(aTarget))
                return null;
            if (!IsFinitePositive(periodSource) || !IsFinitePositive(periodTarget))
                return null;

            double relInc = sourceOrbit.GetRelativeInclination(targetOrbit).Value();
            bool retrograde = relInc > RetrogradeThreshold;

            double synodicRate = TransferWindow.SynodicRate(periodSource, periodTarget, retrograde);
            // Two co-orbital siblings (equal periods) never realign: the synodic rate is zero, so
            // there is no recurring window. Skip rather than emit an infinite countdown.
            if (!double.IsFinite(synodicRate) || synodicRate == 0.0)
                return null;

            double targetPhase = TransferWindow.TargetPhaseAngle(aSource, aTarget, retrograde);
            double synodicPeriod = TransferWindow.SynodicPeriod(periodSource, periodTarget, retrograde);

            // The dV cache already holds this pair's Hohmann time of flight and its hyperbolic
            // excess speed, so reuse them instead of recomputing: the depart leg from source to
            // sibling IS the v_inf at the source, and the cached time of flight is symmetric in
            // the two rendezvous radii.
            EdgeDv edge = cache.GetTransfer(sourceOrbiter, targetOrbiter);
            double vInf = edge.DepartDv;
            double transferTime = edge.TransferTimeSeconds;
            double muSource = root.Body.Mu;
            double rPark = root.Ladder.LowOrbitRadius;
            double ejectionAngle = TransferWindow.EjectionAngle(vInf, rPark, muSource);
            bool ejectionAhead = aTarget > aSource;

            bool approximate = edge.IsApproximate
                || sourceOrbit.Eccentricity > EccentricThreshold
                || targetOrbit.Eccentricity > EccentricThreshold;

            var info = new TransferWindowInfo
            {
                Source = root.Astro,
                Target = sibling.Astro,
                TargetPhaseAngle = targetPhase,
                SynodicRate = synodicRate,
                SynodicPeriodSeconds = synodicPeriod,
                TransferTimeSeconds = transferTime,
                EjectionAngle = ejectionAngle,
                EjectionAhead = ejectionAhead,
                Retrograde = retrograde,
                RelativeInclination = relInc,
                IsApproximate = approximate
            };

            Refresh(info, now);
            return info;
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("twindow-" + sibling.Id, $"[DvMap] Transfer window for '{sibling.Id}' failed: {ex.Message}");
            return null;
        }
    }

    // Recompute the two live fields for every window at a new time.
    public static void RefreshAll(IReadOnlyList<TransferWindowInfo> windows, SimTime now)
    {
        for (int i = 0; i < windows.Count; i++)
            Refresh(windows[i], now);
    }

    // Recompute the current phase angle and the countdown for one window at a new time. The
    // current phase is the target's phase angle minus the source's, wrapped with the game's
    // own MathEx.ToOrbitAngle so the figure agrees with the stock alignment planner; the
    // retrograde branch measures it the other way round. The countdown then folds that gap
    // through the synodic rate (without the wrap on the retrograde branch, matching the game).
    public static void Refresh(TransferWindowInfo info, SimTime now)
    {
        try
        {
            if (info.Source is not IOrbiter source || info.Target is not IOrbiter target)
                return;

            double raw = MathEx.ToOrbitAngle(
                target.Orbit.GetPhaseAngle(now).Value() - source.Orbit.GetPhaseAngle(now).Value());
            double currentPhase = info.Retrograde ? (TransferWindow.TwoPi - raw) : raw;

            info.CurrentPhaseAngle = currentPhase;
            info.TimeToWindowSeconds = TransferWindow.TimeToWindowSeconds(
                currentPhase, info.TargetPhaseAngle, info.SynodicRate, offset: 0.0, wrap: !info.Retrograde);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("twindow-refresh-" + info.TargetId,
                $"[DvMap] Transfer window refresh for '{info.TargetId}' failed: {ex.Message}");
        }
    }

    private static bool IsFinitePositive(double value)
    {
        return double.IsFinite(value) && value > 0.0;
    }

    // Order finite countdowns ascending; push any non-finite value (a degenerate pair that
    // slipped through) to the end instead of letting NaN sort to the front.
    private static int CompareCountdown(double a, double b)
    {
        bool af = double.IsFinite(a);
        bool bf = double.IsFinite(b);
        if (af && bf)
            return a.CompareTo(b);
        if (af)
            return -1;
        if (bf)
            return 1;
        return 0;
    }
}
