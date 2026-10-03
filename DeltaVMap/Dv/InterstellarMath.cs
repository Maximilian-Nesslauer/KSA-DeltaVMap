using System;

namespace DeltaVMap.Dv;

// One level of a patched-conic gravity-well chain. The vessel crosses the well of the outer
// body (gravitational parameter OuterMu) at Radius, which is where the inner body it leaves
// (or arrives at) orbits, and that inner body moves at BodySpeed on a near-circular orbit.
// AimSine fixes the direction of the outer excess: it is the sine of the angle between the aim
// (the straight line to the other star system) and the inner body's orbital plane. Null means
// that the direction is free, as on a deeper level of a departure chain, and the burn is taken
// along the body's motion, which is the best case. SharesOuterPlane is true when the inner
// body's orbital plane is the plane of the level above, so a plane correction made for that
// level also holds here.
internal readonly struct WellStep
{
    public readonly double OuterMu;
    public readonly double Radius;
    public readonly double BodySpeed;
    public readonly double? AimSine;
    public readonly bool SharesOuterPlane;

    public WellStep(double outerMu, double radius, double bodySpeed, double? aimSine = null, bool sharesOuterPlane = false)
    {
        OuterMu = outerMu;
        Radius = radius;
        BodySpeed = bodySpeed;
        AimSine = aimSine;
        SharesOuterPlane = sharesOuterPlane;
    }
}

// The orbit at the deep end of a chain: the burn happens at Radius (the periapsis) around a body
// of gravitational parameter Mu, where the vessel moves at Speed before a departure or after a
// capture. AimSine is set only for a vessel that orbits a system root directly on its own orbit,
// because that orbit's plane then limits the burn the same way a WellStep's plane does. A parking
// orbit that can be chosen freely leaves it null. A record struct, so callers can key a cache on
// it without boxing.
internal readonly record struct ParkingOrbit(double Mu, double Radius, double Speed, double? AimSine = null)
{
    public static ParkingOrbit Circular(double mu, double radius)
    {
        return new ParkingOrbit(mu, radius, Math.Sqrt(mu / radius));
    }
}

// Closed-form interstellar leg in the same Newtonian model as the game's own interstellar
// planner: the vessel leaves the source system on an escape hyperbola with excess speed v_inf,
// coasts in a straight line for distance / v_inf, and is captured at the destination. There is
// no relativity, because the game integrates interstellar flight with Newtonian mechanics.
//
// Departure and capture are the same chain run in opposite time directions, so one fold covers
// both: start with v_inf at the system root, and at each level convert the excess into the speed
// needed at the inner body's radius (vis-viva) and subtract the inner body's own velocity. The
// last level ends in the burn at the parking orbit. Pure, so it is unit-tested offline.
internal static class InterstellarMath
{
    public const double SpeedOfLight = 299792458.0;

    // The game's interstellar planner aims at a 1 AU periapsis around the destination root.
    public const double AstronomicalUnit = 149597870700.0;

    // Coarse samples over the departure point, then golden-section steps around the best sample.
    // The function is smooth with one peak, so this resolves it far below a millimeter per second.
    private const int AlongSamples = 32;
    private const int AlongRefineSteps = 40;

    // Golden-section steps for the coast correction before a plane-limited arrival level. The
    // bracket shrinks to 2e-7 of the normal excess, far below a meter per second of cost.
    private const int CorrectionRefineSteps = 32;

    // The along-motion search the coast correction uses to compare candidate corrections: fewer
    // samples and steps, which still place the peak to well under a millionth. Only the choice
    // of the correction uses it; the burn for the chosen correction is evaluated with the full
    // search, so a fast arrival that picks no correction gets the plain chain to the bit.
    private const int CompareAlongSamples = 12;
    private const int CompareAlongRefineSteps = 14;

    private const double InverseGoldenRatio = 0.6180339887498949;

    // Mirrors OrbitalTransfers.DepartureSpeedForExcess: the speed at radius r that leaves a well
    // of gravitational parameter mu with excess speed vInf.
    public static double SpeedForExcess(double mu, double radius, double vInf)
    {
        return Math.Sqrt(vInf * vInf + 2.0 * mu / radius);
    }

    // The size of v - u for a vessel velocity v of size vNeeded and a body velocity u of size
    // bodySpeed, where along is the component of v along u.
    public static double RelativeExcess(double vNeeded, double bodySpeed, double along)
    {
        double sq = vNeeded * vNeeded + bodySpeed * bodySpeed - 2.0 * bodySpeed * along;
        return Math.Sqrt(Math.Max(0.0, sq));
    }

    public static double ExcessAtBody(double outerExcess, in WellStep step)
    {
        return ExcessAtBody(outerExcess, in step, AlongSamples, AlongRefineSteps);
    }

    private static double ExcessAtBody(double outerExcess, in WellStep step, int samples, int refineSteps)
    {
        double needed = SpeedForExcess(step.OuterMu, step.Radius, outerExcess);
        double along = step.AimSine is double aimSine && aimSine != 0.0
            ? BestSpeedAlongMotion(step.OuterMu, step.Radius, outerExcess, aimSine, samples, refineSteps)
            : needed;
        return RelativeExcess(needed, step.BodySpeed, along);
    }

    // The largest component along the body's motion that a velocity at radius can have, when the
    // escape hyperbola from there must leave with excess vInf along a fixed aim that lies out of
    // the body's (circular) orbital plane by an angle with sine aimSine. The best point on the
    // orbit is searched over one revolution, the best time of year.
    //
    // Geometry: the hyperbola's plane holds the central body, the departure point and the aim, so
    // its tilt to the body's plane is at least the aim's angle. For a sweep psi from the departure
    // point to the asymptote, the conic equation with e^2 = 1 + s^2 and p = mu s^2 / vInf^2 gives
    // s^2 - k s sin(psi) - k (1 - cos(psi)) = 0 with k = r vInf^2 / mu. The transverse speed is
    // then h / r = mu s / (vInf r), written so that vInf = 0 (a parabola) needs no special case.
    // At high speed this tends to vInf times the cosine of the aim's angle, and in the plane it
    // tends to the full speed (a tangential burn at periapsis). The radial part of the velocity is
    // perpendicular to a circular orbit's motion, so it does not count.
    public static double BestSpeedAlongMotion(double mu, double radius, double vInf, double aimSine)
    {
        return BestSpeedAlongMotion(mu, radius, vInf, aimSine, AlongSamples, AlongRefineSteps);
    }

    private static double BestSpeedAlongMotion(double mu, double radius, double vInf, double aimSine, int samples, int refineSteps)
    {
        double sinBeta = Math.Min(1.0, Math.Abs(aimSine));
        double cosBeta = Math.Sqrt(1.0 - sinBeta * sinBeta);
        if (!(cosBeta > 0.0))
            return 0.0;
        // With no well the vessel already moves along the aim at vInf.
        if (!(mu > 0.0))
            return vInf * cosBeta;

        double step = Math.PI / samples;
        int best = 0;
        double bestValue = double.NegativeInfinity;
        for (int i = 0; i < samples; i++)
        {
            double value = SpeedAlongMotion(mu, radius, vInf, cosBeta, (i + 0.5) * step);
            if (value > bestValue)
            {
                bestValue = value;
                best = i;
            }
        }

        double a = Math.Max(0.0, (best - 0.5) * step);
        double b = Math.Min(Math.PI, (best + 1.5) * step);
        double c = b - InverseGoldenRatio * (b - a);
        double d = a + InverseGoldenRatio * (b - a);
        double fc = SpeedAlongMotion(mu, radius, vInf, cosBeta, c);
        double fd = SpeedAlongMotion(mu, radius, vInf, cosBeta, d);
        for (int i = 0; i < refineSteps; i++)
        {
            if (fc > fd)
            {
                b = d;
                d = c;
                fd = fc;
                c = b - InverseGoldenRatio * (b - a);
                fc = SpeedAlongMotion(mu, radius, vInf, cosBeta, c);
            }
            else
            {
                a = c;
                c = d;
                fc = fd;
                d = a + InverseGoldenRatio * (b - a);
                fd = SpeedAlongMotion(mu, radius, vInf, cosBeta, d);
            }
        }
        return Math.Max(bestValue, Math.Max(fc, fd));
    }

    // The velocity component along the body's motion for a departure point at the angle phi
    // behind the aim's projection onto the body's plane. At phi = PI / 2 the body moves straight
    // toward that projection. psi is the sweep from the departure point to the aim.
    private static double SpeedAlongMotion(double mu, double radius, double vInf, double cosBeta, double phi)
    {
        double cosPsi = Math.Cos(phi) * cosBeta;
        double oneMinusCos = 1.0 - cosPsi;
        double sinPsi = Math.Sqrt(Math.Max(0.0, oneMinusCos * (1.0 + cosPsi)));
        double x = radius * vInf / mu;
        double q = 0.5 * (x * sinPsi + Math.Sqrt(x * x * sinPsi * sinPsi + 4.0 * radius * oneMinusCos / mu));
        double transverse = mu / radius * q;
        double toMotion = sinPsi > 0.0 ? Math.Sin(phi) * cosBeta / sinPsi : 1.0;
        return transverse * toMotion;
    }

    // The burn at the parking orbit for a root excess vInf, folding the chain from the root level
    // inward (stepsOuterToInner[0] is the level directly under the system root). An empty chain
    // means the parking orbit is around the root itself. When that orbit is the vessel's own,
    // with a plane fixed by AimSine, the vessel is the inner body of the last level and the burn
    // is its relative excess.
    public static double ChainBurn(double vInf, ReadOnlySpan<WellStep> stepsOuterToInner, in ParkingOrbit park)
    {
        if (stepsOuterToInner.IsEmpty && park.AimSine.HasValue)
            return ExcessAtBody(vInf, new WellStep(park.Mu, park.Radius, park.Speed, park.AimSine));

        double excess = vInf;
        for (int i = 0; i < stepsOuterToInner.Length; i++)
            excess = ExcessAtBody(excess, in stepsOuterToInner[i]);
        return Math.Max(0.0, SpeedForExcess(park.Mu, park.Radius, excess) - park.Speed);
    }

    // The capture burn into the park at the end of an arrival chain, where every level carries
    // the sine of the arrival direction against its own orbital plane. Unlike ChainBurn, the
    // vessel may burn during the coast before a plane-limited level to remove part of the
    // excess component normal to that plane: a burn d out of the normal part w * |sine| leaves
    // the excess sqrt((w cos)^2 + (w |sine| - d)^2) at the smaller sine. A deeper level in the
    // same plane (WellStep.SharesOuterPlane) carries that smaller sine as the direction of its
    // own excess; a level in another plane keeps its own sine, because the correction made for
    // the outer plane does not tell how the excess lies against it. A slow arrival can therefore
    // swing into the plane almost for free, while a fast one keeps d at zero and pays the plain
    // chain.
    //
    // Each level picks its d against the rest of the chain flown without further correction,
    // then the next level does the same with the excess and sine that choice leaves. Every
    // choice is a maneuver the vessel can fly, and d = 0 gives every level its own sine, so the
    // result is not above the plain chain (up to the coarser search that compares the candidate
    // corrections) and the cost grows only linearly with the depth of the chain.
    public static double CaptureChainBurn(double vInf, ReadOnlySpan<WellStep> stepsOuterToInner, in ParkingOrbit park)
    {
        if (stepsOuterToInner.IsEmpty)
            return ChainBurn(vInf, stepsOuterToInner, in park);

        double paid = 0.0;
        double excess = vInf;
        double? carried = null;
        for (int i = 0; i < stepsOuterToInner.Length; i++)
        {
            WellStep step = stepsOuterToInner[i];
            double? sine = LevelSine(in step, carried);
            if (sine is not double s || s == 0.0)
            {
                excess = ExcessAtBody(excess, new WellStep(step.OuterMu, step.Radius, step.BodySpeed));
                // An excess that already lies in this plane carries a zero sine into a deeper
                // level of the same plane.
                if (sine.HasValue)
                    carried = 0.0;
                continue;
            }

            var plane = new PlaneSplit(excess, Math.Min(1.0, Math.Abs(s)));
            double d = BestCoastCorrection(stepsOuterToInner, i, in park, in plane);
            plane.Corrected(d, out double w, out double corrected);
            paid += d;
            excess = ExcessAtBody(w, new WellStep(step.OuterMu, step.Radius, step.BodySpeed, corrected));
            carried = corrected;
        }
        return paid + Math.Max(0.0, SpeedForExcess(park.Mu, park.Radius, excess) - park.Speed);
    }

    // The sine a plane-limited level is priced with: the sine carried from the level above when
    // both share one plane, else the level's own. Null for a level with a free direction.
    private static double? LevelSine(in WellStep step, double? carried)
    {
        if (!step.AimSine.HasValue)
            return null;
        return step.SharesOuterPlane && carried.HasValue ? carried : step.AimSine;
    }

    // The coast correction d in [0, normal] before level index that minimizes d plus the rest of
    // the chain without further correction. The ends are evaluated exactly, so a fast arrival
    // gets d = 0 and the plain chain to the bit.
    private static double BestCoastCorrection(ReadOnlySpan<WellStep> steps, int index, in ParkingOrbit park, in PlaneSplit plane)
    {
        double normal = plane.Normal;
        if (!(normal > 0.0))
            return 0.0;
        double atZero = CorrectedCost(steps, index, in park, in plane, 0.0);

        double bestD = 0.0;
        double best = atZero;
        double atFull = CorrectedCost(steps, index, in park, in plane, normal);
        if (atFull < best)
        {
            best = atFull;
            bestD = normal;
        }

        double a = 0.0;
        double b = normal;
        double c = b - InverseGoldenRatio * (b - a);
        double dd = a + InverseGoldenRatio * (b - a);
        double fc = CorrectedCost(steps, index, in park, in plane, c);
        double fd = CorrectedCost(steps, index, in park, in plane, dd);
        for (int k = 0; k < CorrectionRefineSteps; k++)
        {
            if (fc < fd)
            {
                b = dd;
                dd = c;
                fd = fc;
                c = b - InverseGoldenRatio * (b - a);
                fc = CorrectedCost(steps, index, in park, in plane, c);
            }
            else
            {
                a = c;
                c = dd;
                fc = fd;
                dd = a + InverseGoldenRatio * (b - a);
                fd = CorrectedCost(steps, index, in park, in plane, dd);
            }
        }
        if (fc < best)
        {
            best = fc;
            bestD = c;
        }
        if (fd < best)
            bestD = dd;
        return bestD;
    }

    // d plus the burn of the chain from level index on, with the corrected excess at that level.
    // A deeper level takes the corrected sine by the same rule as CaptureChainBurn.
    private static double CorrectedCost(ReadOnlySpan<WellStep> steps, int index, in ParkingOrbit park, in PlaneSplit plane, double d)
    {
        plane.Corrected(d, out double w, out double sine);
        double? carried = sine;
        for (int i = index; i < steps.Length; i++)
        {
            WellStep step = steps[i];
            double? levelSine = i == index ? sine : LevelSine(in step, carried);
            w = ExcessAtBody(w, new WellStep(step.OuterMu, step.Radius, step.BodySpeed, levelSine), CompareAlongSamples, CompareAlongRefineSteps);
            if (levelSine.HasValue)
                carried = levelSine;
        }
        return d + Math.Max(0.0, SpeedForExcess(park.Mu, park.Radius, w) - park.Speed);
    }

    // An excess split into its parts in and normal to a level's plane. Without a correction it
    // returns the excess and sine it was built from, so d = 0 is the plain chain to the bit.
    private readonly struct PlaneSplit
    {
        private readonly double _excess;
        private readonly double _sine;
        private readonly double _inPlane;
        public readonly double Normal;

        // An arrival at zero excess comes in on a parabola from any direction, so it has no plane
        // to keep.
        public PlaneSplit(double excess, double absSine)
        {
            _excess = excess;
            _sine = excess > 0.0 ? absSine : 0.0;
            Normal = excess * _sine;
            _inPlane = Math.Sqrt(Math.Max(0.0, excess * excess - Normal * Normal));
        }

        public void Corrected(double d, out double excess, out double sine)
        {
            if (!(d > 0.0))
            {
                excess = _excess;
                sine = _sine;
                return;
            }
            double rest = Math.Max(0.0, Normal - d);
            excess = Math.Sqrt(_inPlane * _inPlane + rest * rest);
            sine = excess > 0.0 ? rest / excess : 0.0;
        }
    }

    // The hyperbolic excess speed of an open orbit, from vis-viva at periapsis: the semi-major
    // axis is pe / (1 - e), so v_inf^2 = mu (e - 1) / pe. Zero for a bound or parabolic orbit.
    public static double HyperbolicExcess(double mu, double periapsis, double eccentricity)
    {
        if (!(eccentricity > 1.0) || !(periapsis > 0.0) || !(mu > 0.0))
            return 0.0;
        return Math.Sqrt(mu * (eccentricity - 1.0) / periapsis);
    }

    // The delta-v the interstellar leg may spend: the vessel's budget without the piloting
    // margin, minus the rest of the route. Never negative.
    public static double LegBudget(double available, double dvScale, double restDv)
    {
        return Math.Max(0.0, available / dvScale - restDv);
    }

    // The whole route with the leg at one cruise speed, margin included. split is the part of
    // the capture that the route already charges as a circularize ladder step.
    public static double RouteTotal(double restDv, double departBurn, double captureBurn, double split, double dvScale)
    {
        return (restDv + departBurn + captureBurn - split) * dvScale;
    }

    // The burn that turns an excess of size vInf through the angle with cosine cosAngle while it
    // keeps its size: |vInf a - vInf b| = vInf sqrt(2 - 2 cos) for unit vectors a and b.
    public static double CourseChangeBurn(double vInf, double cosAngle)
    {
        return vInf * Math.Sqrt(Math.Max(0.0, 2.0 - 2.0 * Math.Clamp(cosAngle, -1.0, 1.0)));
    }

    // The unit vector a vessel on an open orbit finally leaves along, from its position and
    // velocity relative to the central body, both in one frame. With the angular momentum
    // h = r x v and the eccentricity vector e = (v x h) / mu - r / |r|, the outgoing asymptote
    // lies at the true anomaly with cosine -1 / e, which gives (-P + sqrt(e^2 - 1) Q) / e for
    // P = e / |e| and Q = (h / |h|) x P. False for a bound or degenerate orbit.
    public static bool OutgoingAsymptote(double mu, (double X, double Y, double Z) r, (double X, double Y, double Z) v,
        out (double X, double Y, double Z) direction)
    {
        direction = default;
        double rLength = Length(r);
        if (!(mu > 0.0) || !(rLength > 0.0))
            return false;
        (double X, double Y, double Z) h = Cross(r, v);
        double hLength = Length(h);
        if (!(hLength > 0.0))
            return false;
        (double X, double Y, double Z) vxh = Cross(v, h);
        (double X, double Y, double Z) ev = (vxh.X / mu - r.X / rLength, vxh.Y / mu - r.Y / rLength, vxh.Z / mu - r.Z / rLength);
        double e = Length(ev);
        if (!(e > 1.0) || !double.IsFinite(e))
            return false;
        (double X, double Y, double Z) p = (ev.X / e, ev.Y / e, ev.Z / e);
        (double X, double Y, double Z) q = Cross((h.X / hLength, h.Y / hLength, h.Z / hLength), p);
        double k = Math.Sqrt(e * e - 1.0);
        direction = ((k * q.X - p.X) / e, (k * q.Y - p.Y) / e, (k * q.Z - p.Z) / e);
        return true;
    }

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        return (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    private static double Length((double X, double Y, double Z) a)
    {
        return Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
    }

    public static double CoastSeconds(double distance, double vInf)
    {
        return vInf > 0.0 ? distance / vInf : double.PositiveInfinity;
    }

    // Lower bound used to bracket the inverse problem: every level can remove at most its body
    // speed and the park removes at most its own speed, so the burn is at least
    // vInf - (sum of those speeds).
    public static double SpeedAllowance(ReadOnlySpan<WellStep> steps, in ParkingOrbit park)
    {
        double sum = park.Speed;
        for (int i = 0; i < steps.Length; i++)
            sum += steps[i].BodySpeed;
        return sum;
    }
}

// One interstellar leg between two systems. The chains and the distance are fixed for a pair of
// bodies, so the leg is built once and then evaluated for any cruise speed and any departure
// parking orbit (the vessel's own orbit changes during a burn, the geometry does not).
//
// The total is not monotonic in the cruise speed. When the aim lies out of a body's plane, the
// plane-limited departure (InterstellarMath.ChainBurn) makes a slow escape bend too much to line
// up with the body's motion, so a few km/s of cruise speed can make the departure cheaper; a body
// on an open orbit already moves faster than the escape. The cheapest trip is therefore searched
// for, and the inverse problem works on the rising branch above it.
//
// The departure and the capture are priced differently on purpose. The capture may correct the
// plane during the coast (InterstellarMath.CaptureChainBurn); the departure takes no such
// correction after the escape, so for a slow trip out of a tilted plane it is an upper bound.
//
// A capture through plane-limited arrival levels costs a search per evaluation, and the speed
// searches evaluate it about a hundred times while the departure park changes during every
// burn. The capture does not depend on that park, so the searches read it from a table over the
// cruise speed. A node is evaluated the first time a search needs it and then kept for the leg,
// and the fastest cruise is then polished against the exact total so the vessel always reads as
// able to pay for it. Every figure shown is evaluated exactly. A leg without such a capture
// searches on the exact total, as the table would only copy it.
internal sealed class InterstellarLeg
{
    // Table nodes per doubling of the cruise speed, from 1 m/s up, then TableTopSpeed itself as
    // the last node, plus zero.
    private const int TableNodesPerOctave = 6;
    private const double TableTopSpeed = 0.9 * InterstellarMath.SpeedOfLight;

    // Exact evaluations the fastest-cruise polish may spend. The bracket from the table search
    // is within the table's error, so a few secant steps reach the budget to 1e-9.
    private const int PolishIterations = 40;

    // Bisection steps for the inverse problem. The bracket is at most a few times the budget, so
    // this resolves it far below a meter per second.
    private const int MaxSpeedIterations = 200;

    // The cheapest-speed search: a doubling scan from FirstScanSpeed to the cap, then
    // golden-section steps between the neighbours of the best scan point.
    private const double FirstScanSpeed = 1.0;
    private const int CheapestRefineSteps = 60;

    private const double InverseGoldenRatio = 0.6180339887498949;

    public required WellStep[] DepartSteps { get; init; }
    public required WellStep[] ArriveSteps { get; init; }
    public required ParkingOrbit ArrivePark { get; init; }
    public required double Distance { get; init; }

    public double DepartBurn(double vInf, in ParkingOrbit departPark)
    {
        return InterstellarMath.ChainBurn(vInf, DepartSteps, in departPark);
    }

    public double CaptureBurn(double vInf)
    {
        return InterstellarMath.CaptureChainBurn(vInf, ArriveSteps, ArrivePark);
    }

    public double TotalDv(double vInf, in ParkingOrbit departPark)
    {
        return DepartBurn(vInf, in departPark) + CaptureBurn(vInf);
    }

    // Captures and slopes hold NaN until a search needs them.
    private double[]? _tableSpeeds;
    private double[]? _tableCaptures;
    private double[]? _tableSlopes;
    private int _usesTable = -1;

    // True when the capture runs a correction search, so the speed searches use the table.
    private bool UsesTable
    {
        get
        {
            if (_usesTable < 0)
            {
                _usesTable = 0;
                for (int i = 0; i < ArriveSteps.Length; i++)
                {
                    if (ArriveSteps[i].AimSine is double sine && sine != 0.0)
                        _usesTable = 1;
                }
            }
            return _usesTable == 1;
        }
    }

    // The capture as the speed searches see it: the table for a leg with a corrected capture,
    // else the exact burn.
    internal double SearchCapture(double vInf)
    {
        if (!UsesTable || !(vInf >= 0.0) || vInf > TableTopSpeed)
            return CaptureBurn(vInf);
        EnsureTable();
        return Interpolate(vInf);
    }

    private double SearchTotal(double vInf, in ParkingOrbit departPark)
    {
        return DepartBurn(vInf, in departPark) + SearchCapture(vInf);
    }

    // The node speeds: zero, then log-spaced speeds from 1 m/s while they stay below
    // TableTopSpeed, then TableTopSpeed once, so no two nodes share a speed. The captures and
    // slopes are filled on demand.
    private void EnsureTable()
    {
        if (_tableSpeeds != null)
            return;
        int logNodes = 0;
        while (Math.Pow(2.0, logNodes / (double)TableNodesPerOctave) < TableTopSpeed)
            logNodes++;
        int count = logNodes + 2;
        var speeds = new double[count];
        speeds[0] = 0.0;
        for (int i = 1; i <= logNodes; i++)
            speeds[i] = Math.Pow(2.0, (i - 1) / (double)TableNodesPerOctave);
        speeds[count - 1] = TableTopSpeed;
        var captures = new double[count];
        var slopes = new double[count];
        Array.Fill(captures, double.NaN);
        Array.Fill(slopes, double.NaN);
        _tableSpeeds = speeds;
        _tableCaptures = captures;
        _tableSlopes = slopes;
    }

    private double NodeCapture(int i)
    {
        double[] captures = _tableCaptures!;
        if (double.IsNaN(captures[i]))
            captures[i] = CaptureBurn(_tableSpeeds![i]);
        return captures[i];
    }

    // The secant per unit of log2(speed) between node i and node i + 1 (index 1 on).
    private double Secant(int i)
    {
        double[] speeds = _tableSpeeds!;
        return (NodeCapture(i + 1) - NodeCapture(i)) / (Math.Log2(speeds[i + 1]) - Math.Log2(speeds[i]));
    }

    // The monotone cubic Hermite slope in log speed at node i (Fritsch and Carlson 1980), so the
    // interpolation never overshoots between nodes.
    private double NodeSlope(int i)
    {
        double[] slopes = _tableSlopes!;
        if (!double.IsNaN(slopes[i]))
            return slopes[i];
        int last = slopes.Length - 1;
        double slope;
        if (i <= 1)
        {
            slope = Secant(1);
        }
        else if (i >= last)
        {
            slope = Secant(last - 1);
        }
        else
        {
            double left = Secant(i - 1);
            double right = Secant(i);
            slope = left * right <= 0.0 ? 0.0 : 2.0 / (1.0 / left + 1.0 / right);
        }
        slopes[i] = slope;
        return slope;
    }

    private double Interpolate(double vInf)
    {
        double[] speeds = _tableSpeeds!;
        if (vInf <= speeds[1])
        {
            // Below 1 m/s the capture is flat to well under a millimeter per second.
            double t = speeds[1] > 0.0 ? vInf / speeds[1] : 0.0;
            double atZero = NodeCapture(0);
            return atZero + (NodeCapture(1) - atZero) * t;
        }
        double x = Math.Log2(vInf);
        int i = Math.Clamp(1 + (int)Math.Floor(x * TableNodesPerOctave), 1, speeds.Length - 2);
        while (i > 1 && vInf < speeds[i])
            i--;
        while (i < speeds.Length - 2 && vInf > speeds[i + 1])
            i++;
        // A speed on a node (every power of two the cheapest-speed scan visits) needs only that
        // node.
        if (vInf == speeds[i])
            return NodeCapture(i);
        double x0 = Math.Log2(speeds[i]);
        double h = Math.Log2(speeds[i + 1]) - x0;
        double s = (x - x0) / h;
        double s2 = s * s;
        double s3 = s2 * s;
        return (2.0 * s3 - 3.0 * s2 + 1.0) * NodeCapture(i)
            + (s3 - 2.0 * s2 + s) * h * NodeSlope(i)
            + (-2.0 * s3 + 3.0 * s2) * NodeCapture(i + 1)
            + (s3 - s2) * h * NodeSlope(i + 1);
    }

    // The cruise speed of the cheapest trip, at most speedCap.
    public double CheapestSpeed(in ParkingOrbit departPark, double speedCap)
    {
        double bestSpeed = 0.0;
        double bestTotal = SearchTotal(0.0, in departPark);
        double below = 0.0;
        double above = Math.Min(FirstScanSpeed, speedCap);
        double previous = 0.0;
        for (double v = Math.Min(FirstScanSpeed, speedCap); ; v = Math.Min(2.0 * v, speedCap))
        {
            double total = SearchTotal(v, in departPark);
            if (total < bestTotal)
            {
                bestTotal = total;
                bestSpeed = v;
                below = previous;
                above = Math.Min(2.0 * v, speedCap);
            }
            previous = v;
            if (v >= speedCap)
                break;
        }

        double a = below;
        double b = above;
        double c = b - InverseGoldenRatio * (b - a);
        double d = a + InverseGoldenRatio * (b - a);
        double fc = SearchTotal(c, in departPark);
        double fd = SearchTotal(d, in departPark);
        for (int i = 0; i < CheapestRefineSteps; i++)
        {
            if (fc < fd)
            {
                b = d;
                d = c;
                fd = fc;
                c = b - InverseGoldenRatio * (b - a);
                fc = SearchTotal(c, in departPark);
            }
            else
            {
                a = c;
                c = d;
                fc = fd;
                d = a + InverseGoldenRatio * (b - a);
                fd = SearchTotal(d, in departPark);
            }
        }
        if (fc < bestTotal)
        {
            bestTotal = fc;
            bestSpeed = c;
        }
        if (fd < bestTotal)
            bestSpeed = d;
        return bestSpeed;
    }

    // The cheapest possible trip at any cruise speed up to speedCap. A budget below it cannot
    // reach the destination at all.
    public double MinimumDv(in ParkingOrbit departPark, double speedCap)
    {
        return TotalDv(CheapestSpeed(in departPark, speedCap), in departPark);
    }

    // The fastest cruise speed a delta-v budget pays for, departure and capture included,
    // capped at speedCap. Returns 0 when the budget is below MinimumDv. Above the cheapest speed
    // the total rises with the cruise speed, so bisection on that branch is exact.
    public double MaxCruiseSpeed(double budget, in ParkingOrbit departPark, double speedCap)
    {
        if (!(budget > 0.0))
            return 0.0;
        return MaxCruiseSpeed(budget, in departPark, speedCap, CheapestSpeed(in departPark, speedCap));
    }

    // The same search for a caller that already holds CheapestSpeed for this parking orbit and
    // cap, which saves the second cheapest-speed search.
    public double MaxCruiseSpeed(double budget, in ParkingOrbit departPark, double speedCap, double cheapest)
    {
        if (!(budget > 0.0))
            return 0.0;
        if (budget < TotalDv(cheapest, in departPark))
            return 0.0;
        if (TotalDv(speedCap, in departPark) <= budget)
            return speedCap;

        double allowance = InterstellarMath.SpeedAllowance(DepartSteps, in departPark)
            + InterstellarMath.SpeedAllowance(ArriveSteps, ArrivePark);
        double lo = cheapest;
        double hi = Math.Max(cheapest, Math.Min(speedCap, 0.5 * (budget + allowance) + 1.0));
        for (int i = 0; i < MaxSpeedIterations && hi - lo > 1e-9 * hi; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (SearchTotal(mid, in departPark) <= budget)
                lo = mid;
            else
                hi = mid;
        }
        return UsesTable ? Polish(budget, in departPark, lo, cheapest, speedCap) : lo;
    }

    // Move the table's answer onto the exact total: a bracket that steps out from it until the
    // exact total crosses the budget, then regula falsi with the Illinois weighting on the exact
    // total. Returns the fastest speed of the final bracket whose exact total fits the budget.
    private double Polish(double budget, in ParkingOrbit departPark, double guess, double cheapest, double speedCap)
    {
        double a = guess;
        double fa = TotalDv(a, in departPark) - budget;
        // The total rises about one for one with the cruise speed, so the miss is a first step.
        double step = Math.Max(1.5 * Math.Abs(fa), 1e-3);
        double b = a;
        double fb = fa;
        int evaluations = 1;
        while (Math.Sign(fb) == Math.Sign(fa) && fa != 0.0 && evaluations < PolishIterations)
        {
            b = fa > 0.0 ? Math.Max(cheapest, a - step) : Math.Min(speedCap, a + step);
            fb = TotalDv(b, in departPark) - budget;
            evaluations++;
            if (Math.Sign(fb) == Math.Sign(fa))
            {
                a = b;
                fa = fb;
                step *= 4.0;
            }
            if (b == cheapest || b == speedCap)
                break;
        }
        if (fa == 0.0)
            return a;
        if (Math.Sign(fb) == Math.Sign(fa))
            return fa <= 0.0 ? a : cheapest;

        // Keep a as the feasible end (exact total at or below the budget).
        if (fa > 0.0)
        {
            (a, b) = (b, a);
            (fa, fb) = (fb, fa);
        }
        int side = 0;
        while (evaluations < PolishIterations && Math.Abs(b - a) > 1e-9 * Math.Max(a, b))
        {
            double m = (a * fb - b * fa) / (fb - fa);
            if (!(m > Math.Min(a, b) && m < Math.Max(a, b)))
                m = 0.5 * (a + b);
            double fm = TotalDv(m, in departPark) - budget;
            evaluations++;
            if (fm <= 0.0)
            {
                a = m;
                fa = fm;
                if (side == -1)
                    fb *= 0.5;
                side = -1;
            }
            else
            {
                b = m;
                fb = fm;
                if (side == 1)
                    fa *= 0.5;
                side = 1;
            }
        }
        return a;
    }
}
