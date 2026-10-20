using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using KSA;

namespace DeltaVMap.Core;

// Opens the stock Transfer Planner on an interstellar plan, in the same state the player's own
// picks in that window produce. The plan type, source and destination are private statics of
// TransferPlanner, so each is bound by name once, here, and these keys are what a game update has
// to re-check (README, game update maintenance).
//
// Every write mirrors stock, in the order of one stock frame. A type change runs what
// TransferPlanner.DrawPlanWindow runs when the PLAN TYPE combo changes, a source change what it
// runs next when the SOURCE combo changes, and a destination pick what
// TransferPlanner.DrawInterstellarPlan runs after both when its DESTINATION combo changes. With
// the type first, the source change sees no destination, so its SetTransferInfo never builds a
// transfer for the plan type that is about to go. The writes happen on the main thread from the
// AfterGui draw, which is after stock drew the window for this frame, so stock never sees half of
// them.
//
// The destination is always a star, because stock offers only stars (TransferPlanner.PopulateWithStars).
// A FixedStar is no IOrbiter, so for it TransferPlanner.Destination is null and SetTransferInfo
// only clears the transfer info. An OrbitingStar (a star of a multiple system) is an IOrbiter, so
// SetTransferInfo builds a Hohmann TransferInfo for it and can pick a new _selectedTimeUnit in
// place of Years. Stock runs SetTransferInfo when the window appears on the next frame and on a
// SOURCE change, and it does so after the player's own pick of such a star too, so the bridge
// still ends in the state that pick ends in.
internal static class StockPlannerBridge
{
    internal const string InterstellarKey = "Interstellar";

    // The plan fields one test can save and put back, so it leaves stock as it found it.
    internal readonly record struct PlannerState(
        TransferType Type,
        TransferObject Source,
        TransferObject Destination,
        OrbitalTransfers.PorkChopEntry? Entry,
        TimeObject TimeUnit,
        bool Calculated,
        bool DepartureFailed);

    // Declared first, because the bindings below add to it while the class initializes.
    private static readonly List<string> Missing = new();

    private static readonly AccessTools.FieldRef<TransferType>? TransferTypeRef = Bind<TransferType>("_transferType");
    private static readonly AccessTools.FieldRef<TransferObject>? SourceBodyRef = Bind<TransferObject>("_sourceBody");
    private static readonly AccessTools.FieldRef<TransferObject>? DestinationBodyRef = Bind<TransferObject>("_destinationBody");
    private static readonly AccessTools.FieldRef<OrbitalTransfers.PorkChopEntry?>? SelectedEntryRef = Bind<OrbitalTransfers.PorkChopEntry?>("_selectedEntry");
    private static readonly AccessTools.FieldRef<TimeObject>? SelectedTimeUnitRef = Bind<TimeObject>("_selectedTimeUnit");
    private static readonly AccessTools.FieldRef<List<TimeObject>>? TimeUnitsRef = Bind<List<TimeObject>>("_timeUnits");
    private static readonly AccessTools.FieldRef<bool>? TransferCalculatedRef = Bind<bool>("_transferCalculated");
    private static readonly AccessTools.FieldRef<bool>? TransferBeingCalculatedRef = Bind<bool>("_transferBeingCalculated");
    private static readonly AccessTools.FieldRef<bool>? InterstellarDepartureFailedRef = Bind<bool>("_interstellarDepartureFailed");
    private static readonly AccessTools.FieldRef<CancellationTokenSource?>? CancellationRef = Bind<CancellationTokenSource?>("cts");
    private static readonly Action? SetTransferInfo = BindSetTransferInfo();

    // Set when a write threw. The planner state is then unknown, so later clicks only open the
    // window.
    private static bool _broken;

    // A cheap check, so the panel can choose its hint on every frame.
    internal static bool IsAvailable =>
        Missing.Count == 0 && !_broken && TryFindInterstellarType(out _) && TryFindYears(out _);

    // Every key this class needs, including the two list entries it looks up by value, that the
    // running game does not have. Empty when the bridge can preselect.
    internal static List<string> MissingKeys()
    {
        var missing = new List<string>(Missing);
        if (Missing.Count == 0)
        {
            if (!TryFindInterstellarType(out _))
                missing.Add($"TransferPlanner.TransferTypes entry '{InterstellarKey}'");
            if (!TryFindYears(out _))
                missing.Add("TransferPlanner._timeUnits entry TimeUnit.Years");
        }
        return missing;
    }

    // Opens the window, selects the interstellar plan type, makes vehicle the source, and selects
    // destinationStar too when StockDestination accepts it, else clears a destination star that
    // stock does not list. Without the keys it only opens the window.
    internal static void OpenInterstellar(Vehicle vehicle, Astronomical? departureRoot, Astronomical? destinationStar)
    {
        TransferPlanner.ShowPlanWindow = true;
        if (_broken)
            return;
        try
        {
            if (!Preselect(vehicle, departureRoot, destinationStar))
            {
                LogHelper.WarnOnce("stock-planner-keys",
                    $"[DvMap] The stock Transfer Planner opens without a preselected plan. Missing: {string.Join(", ", MissingKeys())}.");
            }
        }
        catch (Exception ex)
        {
            _broken = true;
            LogHelper.WarnOnce("stock-planner-write", $"[DvMap] Could not preselect the stock Transfer Planner, the button only opens it from now on: {ex}");
        }
    }

    // The writes of OpenInterstellar without the window, also for a harness test. False when a key
    // is missing, and then nothing is written.
    internal static bool Preselect(Vehicle vehicle, Astronomical? departureRoot, Astronomical? destinationStar)
    {
        if (Missing.Count > 0 || !TryFindInterstellarType(out TransferType interstellar) || !TryFindYears(out TimeObject years))
            return false;
        SelectType(interstellar, years);
        SelectSource(vehicle);
        Astronomical? destination = StockDestination(vehicle, departureRoot, destinationStar);
        if (destination != null)
            SelectDestination(destination);
        else
            ClearUnlistedStar(vehicle);
        return true;
    }

    // The plan type and destination writes without the source and the window, for a harness test.
    // A null destination selects the type only.
    internal static bool TrySelectInterstellar(Astronomical? destination)
    {
        if (!IsAvailable || !TryFindInterstellarType(out TransferType interstellar) || !TryFindYears(out TimeObject years))
            return false;
        SelectType(interstellar, years);
        SelectDestination(destination);
        return true;
    }

    internal static PlannerState Capture()
    {
        return new PlannerState(
            TransferTypeRef!(),
            SourceBodyRef!(),
            DestinationBodyRef!(),
            SelectedEntryRef!(),
            SelectedTimeUnitRef!(),
            TransferCalculatedRef!(),
            InterstellarDepartureFailedRef!());
    }

    internal static void Restore(in PlannerState state)
    {
        TransferTypeRef!() = state.Type;
        SourceBodyRef!() = state.Source;
        DestinationBodyRef!() = state.Destination;
        SelectedEntryRef!() = state.Entry;
        SelectedTimeUnitRef!() = state.TimeUnit;
        TransferCalculatedRef!() = state.Calculated;
        InterstellarDepartureFailedRef!() = state.DepartureFailed;
    }

    private static void SelectSource(Vehicle vehicle)
    {
        if (SourceBodyRef!().GetKey() == vehicle.Id)
            return;
        SourceBodyRef!() = new TransferObject(vehicle);
        TransferCalculatedRef!() = false;
        CancelCalculation();
        SetTransferInfo!();
    }

    private static void SelectType(TransferType interstellar, TimeObject years)
    {
        if (TransferTypeRef!().GetKey() == InterstellarKey)
            return;
        TransferTypeRef!() = interstellar;
        TransferCalculatedRef!() = false;
        CancelCalculation();
        DestinationBodyRef!() = new TransferObject(-1);
        SelectedEntryRef!() = null;
        SelectedTimeUnitRef!() = years;
        SetTransferInfo!();
    }

    private static void SelectDestination(Astronomical? destination)
    {
        if (destination == null || DestinationBodyRef!().GetKey() == destination.Id)
            return;
        DestinationBodyRef!() = new TransferObject(destination);
        TransferCalculatedRef!() = false;
        InterstellarDepartureFailedRef!() = false;
    }

    // Stock replaces only a destination that is no star with its first entry, on its next draw
    // (TransferPlanner.DrawInterstellarPlan). It keeps a star that it does not list for the source
    // and plans to that star, while its DESTINATION combo shows the first entry, because
    // TransferPlanner.DrawComboControl writes the value only when a key matches. A star left from
    // an earlier pick can be such a star after the vessel changed systems, so it is cleared here,
    // and stock then shows and plans the same first entry. The writes are those of that stock reset.
    private static void ClearUnlistedStar(Vehicle vehicle)
    {
        if (DestinationBodyRef!().Body is not StellarBody star || IsListed(vehicle, star))
            return;
        DestinationBodyRef!() = new TransferObject(-1);
        TransferCalculatedRef!() = false;
    }

    // Stock cancels only while a calculation runs, and disposes the source only after it cleared
    // that flag. A source that was disposed all the same has nothing left to cancel.
    private static void CancelCalculation()
    {
        if (!TransferBeingCalculatedRef!())
            return;
        try
        {
            CancellationRef!()?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // destinationStar when stock offers it as a destination for this vehicle, else null. Stock
    // lists every star of Universe.Stars whose root is not the root the vehicle is nearest to
    // (TransferPlanner.PopulateWithStars), and departs from that root, so departureRoot must be it
    // too, or the panel would show one route and stock would plan another. A barycenter is never
    // in that list, and neither is a star of the vehicle's own multiple system. Stock keeps a star
    // outside the list as its destination but shows the first entry in its DESTINATION combo, so
    // a pick outside the list would show one star while stock plans for another.
    internal static Astronomical? StockDestination(Vehicle vehicle, Astronomical? departureRoot, Astronomical? destinationStar)
    {
        if (departureRoot == null || destinationStar is not StellarBody star)
            return null;
        IIndependentRoot? own = Universe.NearestRoot(vehicle.GetPositionEcl());
        if (own == null || !ReferenceEquals(own, departureRoot))
            return null;
        return IsListed(vehicle, star) ? star : null;
    }

    // The test of TransferPlanner.PopulateWithStars with vehicle as the source.
    private static bool IsListed(Vehicle vehicle, StellarBody star)
    {
        IIndependentRoot? own = Universe.NearestRoot(vehicle.GetPositionEcl());
        if (ReferenceEquals(IIndependentRoot.RootOf(star), own))
            return false;
        ReadOnlySpan<StellarBody> stars = Universe.Stars;
        for (int i = 0; i < stars.Length; i++)
        {
            if (ReferenceEquals(stars[i], star))
                return true;
        }
        return false;
    }

    // Searched by key, not by index, because other mods add their own plan types to the list.
    private static bool TryFindInterstellarType(out TransferType type)
    {
        foreach (TransferType candidate in TransferPlanner.TransferTypes)
        {
            if (candidate.GetKey() == InterstellarKey)
            {
                type = candidate;
                return true;
            }
        }
        type = default;
        return false;
    }

    private static bool TryFindYears(out TimeObject years)
    {
        if (TimeUnitsRef != null && TimeUnitsRef() is List<TimeObject> units)
        {
            foreach (TimeObject candidate in units)
            {
                if (candidate.Unit == TimeUnit.Years)
                {
                    years = candidate;
                    return true;
                }
            }
        }
        years = default;
        return false;
    }

    private static AccessTools.FieldRef<T>? Bind<T>(string name)
    {
        try
        {
            FieldInfo? field = AccessTools.DeclaredField(typeof(TransferPlanner), name);
            if (field != null && field.IsStatic && field.FieldType == typeof(T))
                return AccessTools.StaticFieldRefAccess<T>(field);
        }
        catch (Exception)
        {
        }
        Missing.Add($"TransferPlanner.{name} ({typeof(T).Name})");
        return null;
    }

    private static Action? BindSetTransferInfo()
    {
        try
        {
            MethodInfo? method = AccessTools.DeclaredMethod(typeof(TransferPlanner), "SetTransferInfo", Type.EmptyTypes);
            if (method != null && method.IsStatic && method.ReturnType == typeof(void))
                return method.CreateDelegate<Action>();
        }
        catch (Exception)
        {
        }
        Missing.Add("TransferPlanner.SetTransferInfo()");
        return null;
    }
}
