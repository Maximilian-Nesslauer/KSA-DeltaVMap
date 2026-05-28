using System;
using System.Collections.Generic;
using Brutal.Logging;
using KSA;

namespace DeltaVMap.Dv;

// A debug-only dump that builds every body's ladder, logs the rung radii and the
// key edge dV values, classifies the controlled vehicle, and spot-checks a few
// well-known routes against published figures. Gated behind
// DebugConfig.ValidationDump and run once per session from the draw hook. No UI,
// no rendering; this only exercises and reports the delta-v engine.
internal static class DvValidationDump
{
    private const string Tag = "[DvMap]";

    internal static void Run()
    {
        CelestialSystem? system = Universe.CurrentSystem;
        if (system == null)
        {
            DefaultCategory.Log.Warning($"{Tag} Validation dump: no current system loaded.");
            return;
        }

        DefaultCategory.Log.Info($"{Tag} === Delta-V engine validation dump ===");

        var cache = new DvCache();

        ReadOnlySpan<Astronomical> all = system.All.AsSpan();
        for (int i = 0; i < all.Length; i++)
        {
            // Planets and moons carry ladders; the star is hub-only and vehicles
            // are not destinations.
            if (all[i] is Celestial body)
                LogBodyLadder(body);
        }

        LogYouAreHere();
        LogSpotChecks(system, cache);

        DefaultCategory.Log.Info(FormattableString.Invariant(
            $"{Tag} Transfer cache holds {cache.Count} body-pair entries."));
        DefaultCategory.Log.Info($"{Tag} === End validation dump ===");
    }

    private static void LogBodyLadder(Celestial body)
    {
        BodyLadder ladder = OrbitalStates.BuildLadder(body);

        var rungText = new List<string>(ladder.Rungs.Count);
        foreach (LadderRung rung in ladder.Rungs)
            rungText.Add(FormattableString.Invariant($"{rung.Kind}={rung.Radius / 1000.0:F1}km"));

        string soi = ladder.SoiRadius.HasValue
            ? FormattableString.Invariant($"{ladder.SoiRadius.Value / 1000.0:F0}km")
            : "n/a";

        DefaultCategory.Log.Info(FormattableString.Invariant(
            $"{Tag} {body.Id} ({body.Class}): R={ladder.MeanRadius / 1000.0:F1}km mu={ladder.Mu:E3} SOI={soi} surface={ladder.HasSurface}"));
        DefaultCategory.Log.Info($"{Tag}   rungs: {string.Join(", ", rungText)}");

        if (!ladder.CanHoldOrbit)
        {
            DefaultCategory.Log.Info($"{Tag}   no stable orbit (SOI too tight); surface-only destination");
            return;
        }

        if (ladder.HasSurface)
        {
            AscentDv ascent = OrbitalStates.ComputeAscent(ladder);
            if (ascent.HasAtmosphere)
                DefaultCategory.Log.Info(FormattableString.Invariant(
                    $"{Tag}   ascent surface->LO: ~{ascent.Effective:F0} m/s (vacuum {ascent.Vacuum:F0})"));
            else
                DefaultCategory.Log.Info(FormattableString.Invariant(
                    $"{Tag}   ascent surface->LO: {ascent.Vacuum:F0} m/s (vacuum)"));
        }

        if (ladder.StationaryRadius.HasValue && ladder.StationaryRadius.Value > ladder.LowOrbitRadius)
        {
            double dv = DeltaVCalculator.CircularToCircular(ladder.Mu, ladder.LowOrbitRadius, ladder.StationaryRadius.Value);
            DefaultCategory.Log.Info(FormattableString.Invariant(
                $"{Tag}   raise LO->stationary: {dv:F0} m/s"));
        }

        if (ladder.SoiRadius.HasValue)
        {
            double dv = DeltaVCalculator.EscapeToSoi(ladder.Mu, ladder.LowOrbitRadius, ladder.SoiRadius.Value);
            DefaultCategory.Log.Info(FormattableString.Invariant(
                $"{Tag}   escape prep LO->SOI edge: {dv:F0} m/s"));
        }
    }

    private static void LogYouAreHere()
    {
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null || vehicle.Parent == null)
        {
            DefaultCategory.Log.Info(
                $"{Tag} You are here: no controlled vehicle; route origin would default to HomeBody surface.");
            return;
        }

        BodyLadder ladder = OrbitalStates.BuildLadder(vehicle.Parent);
        ClassifiedState state = StateClassifier.Classify(vehicle, ladder);

        DefaultCategory.Log.Info(FormattableString.Invariant(
            $"{Tag} You are here: {vehicle.Id} around {ladder.Body.Id} -> {state.Kind} at r={state.Radius / 1000.0:F1}km, available dV={vehicle.NavBallData.DeltaVInVacuum:F0} m/s"));
    }

    private static void LogSpotChecks(CelestialSystem system, DvCache cache)
    {
        DefaultCategory.Log.Info($"{Tag} --- Spot checks (approximate, compare to known values) ---");

        var earth = system.Get("Earth") as Celestial;
        var luna = system.Get("Luna") as Celestial;
        var mars = system.Get("Mars") as Celestial;

        BodyLadder? earthLadder = (earth != null) ? OrbitalStates.BuildLadder(earth) : null;

        // Earth surface -> low Earth orbit (real-world figure is ~9300-9400 m/s).
        if (earth != null && earthLadder != null)
        {
            AscentDv ascent = OrbitalStates.ComputeAscent(earthLadder);
            DefaultCategory.Log.Info(FormattableString.Invariant(
                $"{Tag} Earth surface->LEO: ~{ascent.Effective:F0} m/s (target ~9400; vacuum {ascent.Vacuum:F0})"));
        }
        else
        {
            DefaultCategory.Log.Info($"{Tag} Earth surface->LEO: Earth not found in this system.");
        }

        // Earth LEO -> low Luna orbit. Luna orbits Earth, so this stays inside
        // Earth's SOI: a Hohmann raise to Luna's orbit (trans-lunar injection)
        // followed by an Oberth capture into low Luna orbit.
        if (earth != null && earthLadder != null && luna != null)
        {
            BodyLadder lunaLadder = OrbitalStates.BuildLadder(luna);
            double muEarth = earthLadder.Mu;
            double earthRLo = earthLadder.LowOrbitRadius;
            double lunaOrbitRadius = DeltaVCalculator.EffectiveRadius(
                luna.Orbit.Eccentricity, luna.Orbit.SemiMajorAxis, luna.Orbit.Apoapsis, luna.Orbit.Periapsis);

            DeltaVCalculator.Hohmann(muEarth, earthRLo, lunaOrbitRadius, out double tli, out double vInfLuna);
            double capture = DeltaVCalculator.OberthBurn(lunaLadder.Mu, lunaLadder.LowOrbitRadius, vInfLuna);
            double total = tli + capture;
            double transitHours = DeltaVCalculator.TransferTimeSeconds(muEarth, earthRLo, lunaOrbitRadius) / 3600.0;

            DefaultCategory.Log.Info(FormattableString.Invariant(
                $"{Tag} Earth LEO->low Luna orbit: ~{total:F0} m/s (TLI {tli:F0} + capture {capture:F0}); transit ~{transitHours:F1} h"));
        }
        else
        {
            DefaultCategory.Log.Info($"{Tag} Earth LEO->Luna: Earth or Luna not found in this system.");
        }

        // Earth -> Mars: a heliocentric Hohmann between sibling planets, with an
        // Oberth ejection from low Earth orbit and capture into low Mars orbit.
        if (earth != null && earthLadder != null && mars != null)
        {
            BodyLadder marsLadder = OrbitalStates.BuildLadder(mars);
            EdgeDv edge = cache.GetTransfer(earth, mars);
            double ejection = DeltaVCalculator.OberthBurn(earthLadder.Mu, earthLadder.LowOrbitRadius, edge.DepartDv);
            double capture = DeltaVCalculator.OberthBurn(marsLadder.Mu, marsLadder.LowOrbitRadius, edge.ArriveDv);
            double total = ejection + capture;
            double transitDays = edge.TransferTimeSeconds / 86400.0;

            DefaultCategory.Log.Info(FormattableString.Invariant(
                $"{Tag} Earth LEO->Mars LO: ~{total:F0} m/s (ejection {ejection:F0} + capture {capture:F0}); v_inf dep {edge.DepartDv:F0}/arr {edge.ArriveDv:F0}; transit ~{transitDays:F0} d"));
        }
        else
        {
            DefaultCategory.Log.Info($"{Tag} Earth->Mars: Earth or Mars not found in this system.");
        }
    }
}
