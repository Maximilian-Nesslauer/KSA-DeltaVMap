using System;
using System.Collections.Generic;
using Brutal.Numerics;
using DeltaVMap.Dv;
using KSA;

namespace DeltaVMap.Model;

// Builds the game-free InterstellarLeg from the physical forest: the gravity-well chain out of
// the departure body's system, the chain into the destination target, and the distance between
// them. This is the only interstellar file that reads the bodies; the arithmetic is the pure
// InterstellarMath kernel.
//
// The model follows the game's interstellar planner (OrbitalTransfers.SolveInterstellar,
// OrbitalTransfers.TargetInterstellarPeriapsis): the coast runs from the source star (the nearest
// star at or above the departure body, Universe.NearestStar of the vessel in stock) to the
// destination star (the nearest star at or above the target, else the system root), and a star
// or barycenter target is captured around its own mass (Barycenter.Mass for a multiple system)
// at a periapsis that defaults to 1 AU. Unlike stock, the departure climbs through every level
// above the parking orbit, so a moon or a planet of an orbiting star also gets an estimate, and
// the capture runs the whole chain down into a planet's or a moon's own low orbit.
internal static class InterstellarLegs
{
    public static InterstellarLeg Build(PhysicalNode departBody, PhysicalNode target, double capturePeriapsis)
    {
        double3 aim = AimUnit(departBody, target, out double distance);
        return new InterstellarLeg
        {
            DepartSteps = Chain(departBody, aim, arrival: false),
            ArriveSteps = Chain(target, aim, arrival: true),
            ArrivePark = CapturePark(target, capturePeriapsis),
            Distance = distance
        };
    }

    // The leg for a vessel that already coasts on an open orbit toward target. It has no
    // departure, and the arrival direction is the line from the vessel to the destination star.
    public static InterstellarLeg BuildArrival(double3 vesselPositionEcl, PhysicalNode target, double capturePeriapsis)
    {
        double3 aim = DestinationPoint(target).Astro.GetPositionEcl() - vesselPositionEcl;
        double distance = aim.Length();
        double3 unit = distance > 0.0 ? aim / distance : double3.Zero;
        return new InterstellarLeg
        {
            DepartSteps = Array.Empty<WellStep>(),
            ArriveSteps = Chain(target, unit, arrival: true),
            ArrivePark = CapturePark(target, capturePeriapsis),
            Distance = distance
        };
    }

    // The coast distance left for a vessel at vesselPositionEcl toward target.
    public static double RemainingDistance(double3 vesselPositionEcl, PhysicalNode target)
    {
        return (DestinationPoint(target).Astro.GetPositionEcl() - vesselPositionEcl).Length();
    }

    // The capture radius actually used for a target: the requested periapsis around a star or
    // barycenter, kept inside half a finite SOI and outside the photosphere, or a body's own low
    // orbit.
    public static double CaptureRadius(PhysicalNode target, double requestedPeriapsis)
    {
        if (!target.IsHubOnly)
            return target.Ladder.LowOrbitRadius;
        return OrbitalStates.HubParkingRadiusFor(requestedPeriapsis, target.Ladder.MeanRadius, target.Ladder.SoiRadius);
    }

    // True when the vehicle orbits departBody with its periapsis above the surface, so a
    // departure from there burns from the vessel's own orbit rather than from the body's low
    // orbit. An open orbit counts too: during the departure burn the orbit opens while the
    // vessel is still inside the body's SOI, and the plan keeps the energy the burn has added.
    public static bool VesselParksAt(PhysicalNode departBody, Vehicle? vehicle)
    {
        if (vehicle == null || !vehicle.HasMeaningfulOrbit || vehicle.Parent?.Id != departBody.Id)
            return false;
        Orbit orbit = vehicle.Orbit;
        double pe = orbit.Periapsis;
        return pe > departBody.Ladder.MeanRadius && double.IsFinite(pe) && double.IsFinite(orbit.Eccentricity);
    }

    // The parking orbit a departure starts from. The controlled vehicle's own orbit when it
    // parks at the departure body (VesselParksAt; its periapsis is the burn point and
    // DeltaVCalculator.PeriapsisSpeed holds for an open orbit too), else the body's ladder low
    // orbit. fromVessel tells the caller which one it got. A vessel that orbits a system root
    // directly has its plane fixed against the aim to target, the same plane limit a planet has;
    // any other parking orbit can be chosen freely.
    public static ParkingOrbit DeparturePark(PhysicalNode departBody, PhysicalNode target, Vehicle? vehicle, out bool fromVessel)
    {
        fromVessel = VesselParksAt(departBody, vehicle);
        BodyLadder ladder = departBody.Ladder;
        if (fromVessel)
        {
            Orbit orbit = vehicle!.Orbit;
            double pe = orbit.Periapsis;
            double speed = DeltaVCalculator.PeriapsisSpeed(ladder.Mu, pe, orbit.Eccentricity);
            double? aimSine = departBody.IsSystemRoot
                ? AimSine(AimUnit(departBody, target, out _), orbit.GetOrbitNormalCci())
                : null;
            return new ParkingOrbit(ladder.Mu, pe, speed, aimSine);
        }
        return ParkingOrbit.Circular(ladder.Mu, ladder.LowOrbitRadius);
    }

    // The state of a vessel that coasts on an open orbit around a system root: its hyperbolic
    // excess (from Orbit.Mu, Orbit.Periapsis and Orbit.Eccentricity), whether it still falls
    // toward the root, and the direction in ECL axes it finally leaves along. The state vectors
    // come from Orbit.GetPositionCce and Orbit.GetVelocityCce, whose CCE axes are the ECL axes
    // (Vehicle.GetPositionEcl adds the parent's position to the CCE position). False when the
    // orbit is not open.
    public static bool TryTransitState(Vehicle vehicle, out TransitState state)
    {
        state = default;
        Orbit orbit = vehicle.Orbit;
        double vInf = InterstellarMath.HyperbolicExcess(orbit.Mu, orbit.Periapsis, orbit.Eccentricity);
        if (!(vInf > 0.0) || !double.IsFinite(vInf))
            return false;
        double3 r = orbit.GetPositionCce();
        double3 v = orbit.GetVelocityCce();
        if (!InterstellarMath.OutgoingAsymptote(orbit.Mu, (r.X, r.Y, r.Z), (v.X, v.Y, v.Z), out (double X, double Y, double Z) u))
            return false;
        state = new TransitState(vInf, double3.Dot(r, v) < 0.0, new double3(u.X, u.Y, u.Z));
        return true;
    }

    // The unit vector in ECL axes from the vessel at vesselPositionEcl to the point the coast
    // toward target ends at.
    public static double3 AimFrom(double3 vesselPositionEcl, PhysicalNode target)
    {
        double3 aim = DestinationPoint(target).Astro.GetPositionEcl() - vesselPositionEcl;
        double length = aim.Length();
        return length > 0.0 ? aim / length : double3.Zero;
    }

    // The unit vector in ECL axes from the coast's start point to its end point, and that distance.
    private static double3 AimUnit(PhysicalNode departBody, PhysicalNode target, out double distance)
    {
        PhysicalNode sourceStar = SourcePoint(departBody);
        double3 aim = DestinationPoint(target).Astro.GetPositionEcl() - sourceStar.Astro.GetPositionEcl();
        distance = aim.Length();
        return distance > 0.0 ? aim / distance : double3.Zero;
    }

    // The point the coast starts from: the nearest star at or above the departure body (the
    // body itself when it is a star), or the system root for a barycenter with no star above.
    private static PhysicalNode SourcePoint(PhysicalNode departBody)
    {
        return NearestStarAtOrAbove(departBody);
    }

    // The point the coast ends at, by the same rule. A star or root target is that body itself,
    // and every planet of one star shares one distance that does not depend on its orbital phase.
    public static PhysicalNode DestinationPoint(PhysicalNode target)
    {
        return NearestStarAtOrAbove(target);
    }

    private static PhysicalNode NearestStarAtOrAbove(PhysicalNode body)
    {
        for (PhysicalNode? n = body; n != null; n = n.Parent)
        {
            if (n.IsStar)
                return n;
        }
        return SystemGraph.SystemRootOf(body);
    }

    // The well chain from the system root down to body, outermost level first. Each level is the
    // body one step below the previous, crossing its parent's well at its own orbit radius.
    //
    // A departure chain is plane-limited only on the level directly under the root, because the
    // excess there must point along the aim; deeper levels take the best case of a burn along the
    // body's motion, so the result is a lower bound there. The root's frame shares the ECL axes
    // (a star's and a barycenter's CCI-to-CCE rotations are the identity), so the orbit normal
    // and the ECL aim compare directly.
    //
    // An arrival chain carries a sine on every level, measured against that level's own orbital
    // plane (the normal lifted to ECL axes through the parent's Cci2Cce), because the capture
    // kernel (InterstellarMath.CaptureChainBurn) corrects the plane level by level during the
    // coast instead of assuming the best direction. An arrival level whose plane is the plane
    // of the level above (normals parallel to 1e-9) is marked WellStep.SharesOuterPlane, so a
    // correction for the outer plane carries into it.
    private static WellStep[] Chain(PhysicalNode body, double3 aimUnit, bool arrival)
    {
        var levels = new List<PhysicalNode>();
        for (PhysicalNode? n = body; n != null && n.Parent != null; n = n.Parent)
            levels.Add(n);

        var steps = new WellStep[levels.Count];
        double3 outerNormal = double3.Zero;
        for (int i = 0; i < levels.Count; i++)
        {
            PhysicalNode level = levels[levels.Count - 1 - i];
            Orbit orbit = ((IOrbiter)level.Astro).Orbit;
            double radius = OrbitalStates.TransferRadius(orbit);
            double? aimSine = null;
            bool sharesOuterPlane = false;
            if (level.Parent!.IsSystemRoot || arrival)
            {
                double3 normal = level.Parent!.IsSystemRoot
                    ? orbit.GetOrbitNormalCci()
                    : Rotate(orbit.GetOrbitNormalCci(), orbit.Parent.GetCci2Cce());
                aimSine = AimSine(aimUnit, normal);
                if (arrival)
                {
                    sharesOuterPlane = i > 0 && SamePlane(normal, outerNormal);
                    outerNormal = normal;
                }
            }
            steps[i] = new WellStep(level.Parent!.Body.Mu, radius, BodySpeed(orbit, radius), aimSine, sharesOuterPlane);
        }
        return steps;
    }

    private static bool SamePlane(double3 a, double3 b)
    {
        double la = a.Length();
        double lb = b.Length();
        if (!(la > 0.0) || !(lb > 0.0))
            return false;
        return Math.Abs(double3.Dot(a, b)) / (la * lb) >= 1.0 - 1e-9;
    }

    // The vector v rotated by the unit quaternion q, the same rotation as Double3Ex.Transform.
    // The extension is not called because its overload set needs an assembly the mod does not
    // reference.
    private static double3 Rotate(double3 v, doubleQuat q)
    {
        double x2 = q.X + q.X;
        double y2 = q.Y + q.Y;
        double z2 = q.Z + q.Z;
        double wx = q.W * x2;
        double wy = q.W * y2;
        double wz = q.W * z2;
        double xx = q.X * x2;
        double xy = q.X * y2;
        double xz = q.X * z2;
        double yy = q.Y * y2;
        double yz = q.Y * z2;
        double zz = q.Z * z2;
        return new double3(
            v.X * (1.0 - yy - zz) + v.Y * (xy - wz) + v.Z * (xz + wy),
            v.X * (xy + wz) + v.Y * (1.0 - xx - zz) + v.Z * (yz - wx),
            v.X * (xz - wy) + v.Y * (yz + wx) + v.Z * (1.0 - xx - yy));
    }

    // The sine of the aim's angle out of the plane with this normal, or null without an aim or a
    // plane.
    private static double? AimSine(double3 aimUnit, double3 normal)
    {
        double length = normal.Length();
        if (!(length > 0.0) || !(aimUnit.LengthSquared() > 0.0))
            return null;
        return Math.Clamp(double3.Dot(aimUnit, normal / length), -1.0, 1.0);
    }

    // A body's own speed at its transfer radius, from its own central mass (Orbit.Mu), which for
    // the stars of a multiple system differs from the mass of the barycenter the vessel feels.
    private static double BodySpeed(Orbit orbit, double radius)
    {
        return orbit.Eccentricity >= 1.0
            ? DeltaVCalculator.PeriapsisSpeed(orbit.Mu, radius, orbit.Eccentricity)
            : DeltaVCalculator.CircularSpeed(orbit.Mu, radius);
    }

    private static ParkingOrbit CapturePark(PhysicalNode target, double capturePeriapsis)
    {
        return ParkingOrbit.Circular(target.Ladder.Mu, CaptureRadius(target, capturePeriapsis));
    }
}

// A vessel on an open orbit around a system root, read by InterstellarLegs.TryTransitState.
// LeaveDirection is a unit vector in ECL axes.
internal readonly record struct TransitState(double Excess, bool Inbound, double3 LeaveDirection);
