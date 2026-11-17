using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using KSA;

namespace DeltaVMap.Core;

// Opens the stock Transfer Planner on an interstellar or a Hohmann plan, in the same state the
// player's own picks in that window produce. The plan type, source and destination are private
// statics of TransferPlanner, so each is bound by name once, here, and these keys are what a game
// update has to re-check (README, game update maintenance): the fields below, including
// _pickMissionDestination and _resetTarget, SetTransferInfo, the list entries looked up by value,
// and the frame order described next.
//
// Every write mirrors stock, in the order of one stock frame. A type change runs what
// TransferPlanner.DrawPlanWindow runs when the PLAN TYPE combo changes, a source change what it
// runs next when the SOURCE combo changes, and a destination pick what
// TransferPlanner.DrawInterstellarPlan runs after both when its DESTINATION combo changes. With
// the type first, the source change sees no destination, so its SetTransferInfo never builds a
// transfer for the plan type that is about to go. The writes happen on the main thread from the
// AfterGui draw. StarMap runs that draw as a postfix of Program.OnDrawUiViewports, and
// Program.OnFrame calls Program.OnDrawUiThreadSafe, which draws the planner window, after it in
// the same frame. So stock never sees half of the writes, and its next draw of the window comes
// later in this same frame.
//
// The destination is always a star, because stock offers only stars (TransferPlanner.PopulateWithStars).
// A FixedStar is no IOrbiter, so for it TransferPlanner.Destination is null and SetTransferInfo
// only clears the transfer info. An OrbitingStar (a star of a multiple system) is an IOrbiter, so
// SetTransferInfo builds a Hohmann TransferInfo for it and can pick a new _selectedTimeUnit in
// place of Years. Stock runs SetTransferInfo when the window appears, later in the same frame, and
// on a SOURCE change, and it does so after the player's own pick of such a star too, so the bridge
// still ends in the state that pick ends in.
//
// When the window appears, or its source is N/A, stock arms the private
// TransferPlanner._pickMissionDestination. Only the Hohmann branch of
// TransferPlanner.DrawPlanWindow consumes it, in the same draw, through
// TransferPlanner.PickMissionDestination, which replaces the destination with the vessel's
// mission destination when stock lists it (MissionTransfer.FindDestinationId: the Life Support
// rule is on and the mission goes to a body and is not custom). The interstellar preselect is not
// affected, because the interstellar branch never reads the flag. The flag stays armed then, so a
// later switch of PLAN TYPE to Hohmann in stock picks the mission destination, as after any
// opening of the window.
//
// The Hohmann preselect has to get past that pick. In a window that is already shown, stock's
// next draw is no appearing one, so the bridge clears the flag and writes the destination the way
// the Hohmann DESTINATION combo does, which ends in the state of a consumed pick followed by the
// player's own pick. A window that is not shown yet appears later in the same frame, and that draw
// sets the source to the controlled vehicle, arms the flag, keeps or picks a destination, and
// then picks the mission destination. So the bridge arms the flag as stock does, writes no
// destination, and keeps the pick pending. Stock clears the flag only in the Hohmann branch, so a
// cleared flag is proof that stock has drawn that branch since, and Tick writes the pick on the
// next frame, before stock draws the window again. The keys only the Hohmann preselect needs are
// kept apart, so their loss leaves the interstellar preselect working.
//
// A TransferObject keeps only an index into the current system, and TransferObject.Body (with
// GetKey over it) throws an ArgumentOutOfRangeException in CelestialSystem.GetIndex once that
// index is past the end, after a vessel was removed or a smaller save was loaded. Stock writes a
// new source on the appearing draw of its window (TransferPlanner.DrawPlanWindow) without reading
// the old one, so a stale selection is no failure here, and KeyOf and BodyOf read it as none.
internal static class StockPlannerBridge
{
    internal const string InterstellarKey = "Interstellar";
    internal const string HohmannKey = "Hohmann";

    // The plan fields one test can save and put back, so it leaves stock as it found it.
    internal readonly record struct PlannerState(
        TransferType Type,
        TransferObject Source,
        TransferObject Destination,
        OrbitalTransfers.PorkChopEntry? Entry,
        TimeObject TimeUnit,
        bool Calculated,
        bool DepartureFailed,
        bool PickMissionDestination,
        bool ResetTarget);

    // Declared first, because the bindings below add to them while the class initializes.
    private static readonly List<string> Missing = new();
    private static readonly List<string> MissingHohmann = new();

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
    private static readonly AccessTools.FieldRef<bool>? PickMissionDestinationRef = Bind<bool>("_pickMissionDestination", MissingHohmann);
    private static readonly AccessTools.FieldRef<bool>? ResetTargetRef = Bind<bool>("_resetTarget", MissingHohmann);
    private static readonly Action? SetTransferInfo = BindSetTransferInfo();

    // Set when a write threw. The planner state is then unknown, so later clicks only open the
    // window until the mod unloads.
    private static bool _broken;

    // A Hohmann destination that waits for stock's first draw of the Hohmann branch.
    private static PendingPick? _pending;

    // A cheap check, so the panel can choose its hint on every frame.
    internal static bool IsAvailable =>
        Missing.Count == 0 && !_broken && TryFindType(InterstellarKey, out _) && TryFindTimeUnit(TimeUnit.Years, out _);

    internal static bool IsHohmannAvailable =>
        Missing.Count == 0 && MissingHohmann.Count == 0 && !_broken
        && TryFindType(HohmannKey, out _) && TryFindTimeUnit(TimeUnit.Minutes, out _);

    // Every key this class needs, including the list entries it looks up by value, that the
    // running game does not have. Empty when the bridge can preselect both plan types.
    internal static List<string> MissingKeys()
    {
        var missing = new List<string>(Missing);
        missing.AddRange(MissingHohmann);
        if (Missing.Count == 0)
        {
            if (!TryFindType(InterstellarKey, out _))
                missing.Add($"TransferPlanner.TransferTypes entry '{InterstellarKey}'");
            if (!TryFindTimeUnit(TimeUnit.Years, out _))
                missing.Add("TransferPlanner._timeUnits entry TimeUnit.Years");
            if (!TryFindType(HohmannKey, out _))
                missing.Add($"TransferPlanner.TransferTypes entry '{HohmannKey}'");
            if (!TryFindTimeUnit(TimeUnit.Minutes, out _))
                missing.Add("TransferPlanner._timeUnits entry TimeUnit.Minutes");
        }
        return missing;
    }

    // Opens the window, selects the interstellar plan type, makes vehicle the source, and selects
    // destinationStar too when StockDestination accepts it, else clears a destination star that
    // stock does not list. Without the keys it only opens the window.
    internal static void OpenInterstellar(Vehicle vehicle, Astronomical? departureRoot, Astronomical? destinationStar)
    {
        TransferPlanner.ShowPlanWindow = true;
        _pending = null;
        if (_broken)
            return;
        try
        {
            if (!Preselect(vehicle, departureRoot, destinationStar))
                WarnMissingKeys();
        }
        catch (Exception ex)
        {
            MarkBroken(ex);
        }
    }

    // Opens the window on the Hohmann plan with vehicle as the source and destination as the
    // destination when stock lists it. Without the keys it only opens the window.
    internal static void OpenHohmann(Vehicle vehicle, Astronomical destination)
    {
        bool shown = TransferPlanner.ShowPlanWindow;
        TransferPlanner.ShowPlanWindow = true;
        _pending = null;
        if (_broken)
            return;
        try
        {
            if (!PreselectHohmann(vehicle, destination, shown))
                WarnMissingKeys();
        }
        catch (Exception ex)
        {
            MarkBroken(ex);
        }
    }

    // The writes of OpenInterstellar without the window, also for a harness test. False when a key
    // is missing, and then nothing is written.
    internal static bool Preselect(Vehicle vehicle, Astronomical? departureRoot, Astronomical? destinationStar)
    {
        if (Missing.Count > 0 || !TryFindType(InterstellarKey, out TransferType interstellar) || !TryFindTimeUnit(TimeUnit.Years, out TimeObject years))
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

    // The writes of OpenHohmann without the window, also for a harness test. windowShown tells
    // whether the window was shown before this click. False when a key is missing, and then
    // nothing is written.
    internal static bool PreselectHohmann(Vehicle vehicle, Astronomical destination, bool windowShown)
    {
        if (!IsHohmannAvailable || !TryFindType(HohmannKey, out TransferType hohmann) || !TryFindTimeUnit(TimeUnit.Minutes, out TimeObject minutes))
            return false;
        SelectType(hohmann, minutes);
        SelectSource(vehicle);
        // Stock then shows its first entry, or the mission destination, as when the player opens
        // it, and the button's hint has said to pick the destination there.
        if (!IsStockTarget(destination))
            return true;
        if (windowShown)
        {
            // Stock draws this window later in this same frame, and that draw is no appearing
            // one, so a flag left armed by an opening on another plan type would be consumed in it
            // and replace the pick.
            PickMissionDestinationRef!() = false;
            SelectHohmannDestination(destination);
        }
        else
        {
            PickMissionDestinationRef!() = true;
            _pending = new PendingPick(vehicle, destination, Universe.CurrentSystem);
        }
        return true;
    }

    // Writes a pending Hohmann destination once stock has drawn the Hohmann branch. Mod.Draw calls
    // it first on every frame, so it runs before the map can make a new pick and before stock
    // draws the window in this frame.
    internal static void Tick()
    {
        if (_pending is not { } pick)
            return;
        try
        {
            if (Universe.CurrentSystem == null || !ReferenceEquals(Universe.CurrentSystem, pick.System)
                || !TransferPlanner.ShowPlanWindow
                || TransferTypeRef!().GetKey() != HohmannKey
                || KeyOf(SourceBodyRef!()) != pick.Vehicle.Id)
            {
                _pending = null;
                return;
            }
            // A flag that is still armed means stock has not drawn the Hohmann branch yet. A
            // collapsed window skips its whole body until the player expands it, so the pick
            // waits with no frame limit, and the checks above drop it when the player closes or
            // changes the window.
            if (PickMissionDestinationRef!())
                return;
            _pending = null;
            if (IsStockTarget(pick.Destination))
                SelectHohmannDestination(pick.Destination);
        }
        catch (Exception ex)
        {
            _pending = null;
            MarkBroken(ex);
        }
    }

    internal static void Reset()
    {
        _pending = null;
        _broken = false;
    }

    internal static bool HasPendingPick => _pending != null;

    // The plan type and destination writes without the source and the window, for a harness test.
    // A null destination selects the type only.
    internal static bool TrySelectInterstellar(Astronomical? destination)
    {
        if (!IsAvailable || !TryFindType(InterstellarKey, out TransferType interstellar) || !TryFindTimeUnit(TimeUnit.Years, out TimeObject years))
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
            InterstellarDepartureFailedRef!(),
            PickMissionDestinationRef != null && PickMissionDestinationRef(),
            ResetTargetRef != null && ResetTargetRef());
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
        if (PickMissionDestinationRef != null)
            PickMissionDestinationRef() = state.PickMissionDestination;
        if (ResetTargetRef != null)
            ResetTargetRef() = state.ResetTarget;
    }

    private static void SelectSource(Vehicle vehicle)
    {
        if (KeyOf(SourceBodyRef!()) == vehicle.Id)
            return;
        SourceBodyRef!() = new TransferObject(vehicle);
        TransferCalculatedRef!() = false;
        CancelCalculation();
        SetTransferInfo!();
    }

    private static void SelectType(TransferType type, TimeObject unit)
    {
        if (TransferTypeRef!().GetKey() == type.GetKey())
            return;
        TransferTypeRef!() = type;
        TransferCalculatedRef!() = false;
        CancelCalculation();
        DestinationBodyRef!() = new TransferObject(-1);
        SelectedEntryRef!() = null;
        SelectedTimeUnitRef!() = unit;
        SetTransferInfo!();
    }

    private static void SelectDestination(Astronomical? destination)
    {
        if (destination == null || KeyOf(DestinationBodyRef!()) == destination.Id)
            return;
        DestinationBodyRef!() = new TransferObject(destination);
        TransferCalculatedRef!() = false;
        InterstellarDepartureFailedRef!() = false;
    }

    // The writes of a change of the Hohmann DESTINATION combo, in stock order. _resetTarget makes
    // stock send the vessel's target on its next draw, the destination with SET TARGET on and none
    // with it off, exactly as after the player's own pick.
    private static void SelectHohmannDestination(Astronomical destination)
    {
        if (KeyOf(DestinationBodyRef!()) == destination.Id)
            return;
        DestinationBodyRef!() = new TransferObject(destination);
        TransferCalculatedRef!() = false;
        CancelCalculation();
        ResetTargetRef!() = true;
        SetTransferInfo!();
    }

    // Stock replaces only a destination that is no star with its first entry, on its next draw
    // (TransferPlanner.DrawInterstellarPlan). It keeps a star that it does not list for the source
    // and plans to that star, while its DESTINATION combo shows the first entry, because
    // TransferPlanner.DrawComboControl writes the value only when a key matches. A star left from
    // an earlier pick can be such a star after the vessel changed systems, so it is cleared here,
    // and stock then shows and plans the same first entry. The writes are those of that stock reset.
    private static void ClearUnlistedStar(Vehicle vehicle)
    {
        if (BodyOf(DestinationBodyRef!()) is not StellarBody star || IsListed(vehicle, star))
            return;
        DestinationBodyRef!() = new TransferObject(-1);
        TransferCalculatedRef!() = false;
    }

    // The key of a stock selection as TransferObject.GetKey gives it, "N/A" for a stale index too.
    private static string KeyOf(TransferObject selection) => BodyOf(selection)?.Id ?? "N/A";

    private static Astronomical? BodyOf(TransferObject selection)
    {
        try
        {
            return selection.Body;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void MarkBroken(Exception ex)
    {
        _broken = true;
        LogHelper.WarnOnce("stock-planner-write", $"[DvMap] Could not preselect the stock Transfer Planner, the button only opens it from now on: {ex}");
    }

    private static void WarnMissingKeys()
    {
        LogHelper.WarnOnce("stock-planner-keys",
            $"[DvMap] The stock Transfer Planner opens without a preselected plan. Missing: {string.Join(", ", MissingKeys())}.");
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

    // Whether stock's Hohmann DESTINATION list holds destination for its current source, from
    // TransferPlanner.PopulateWithTargets with the span size stock uses. Called on a click and
    // while a pick is pending, never on every frame.
    private static bool IsStockTarget(Astronomical destination)
    {
        int length = Universe.CurrentSystem!.All.AsSpan().Length;
        Span<TransferObject> list = length <= 512 ? stackalloc TransferObject[length] : new TransferObject[length];
        int count = 0;
        TransferPlanner.PopulateWithTargets(list, ref count);
        for (int i = 0; i < count; i++)
        {
            if (list[i].GetKey() == destination.Id)
                return true;
        }
        return false;
    }

    // Whether stock lists destination as a Hohmann destination with vehicle as the source. Stock's
    // own list follows its current source, so it cannot answer for vehicle before the button has
    // made vehicle the source. This is the test of TransferPlanner.PopulateWithPlanets with
    // getAll false, which is the source of truth, line for line; a harness test locks the two
    // together.
    internal static bool IsHohmannTarget(Vehicle vehicle, Astronomical destination)
    {
        if (destination is Vehicle || destination is FixedStar || destination is not IOrbiter orbiter)
            return false;
        if (destination.System.Id != vehicle.System.Id || orbiter.Id == vehicle.Id || orbiter.Id == vehicle.Parent?.Id)
            return false;
        IParentBody? parent = vehicle.Parent;
        if (parent == null)
            return false;
        Celestial? celestial = parent as Celestial;
        StellarBody? stellarBody = parent as StellarBody;
        if (parent is OrbitingStar orbitingStar)
        {
            if (!ReferenceEquals(orbiter.Parent, orbitingStar) && !ReferenceEquals(orbiter.Parent, orbitingStar.Parent))
                return false;
        }
        else if (parent.IsMoon())
        {
            if (orbiter.Parent?.Id != celestial?.Parent.Id)
                return false;
        }
        else if ((!destination.IsMoon() && orbiter.Parent?.Id != (celestial != null ? celestial.Parent.Id : stellarBody?.Id))
            || (destination.IsMoon() && orbiter.Parent?.Id != parent.Id))
        {
            return false;
        }
        if ((orbiter.Orbit != null && !orbiter.Orbit.IsBound()) || double.IsNaN(orbiter.SphereOfInfluence) || orbiter.SphereOfInfluence <= 0.0)
            return false;
        return true;
    }

    // Searched by key, not by index, because other mods add their own plan types to the list.
    private static bool TryFindType(string key, out TransferType type)
    {
        foreach (TransferType candidate in TransferPlanner.TransferTypes)
        {
            if (candidate.GetKey() == key)
            {
                type = candidate;
                return true;
            }
        }
        type = default;
        return false;
    }

    private static bool TryFindTimeUnit(TimeUnit unit, out TimeObject found)
    {
        if (TimeUnitsRef != null && TimeUnitsRef() is List<TimeObject> units)
        {
            foreach (TimeObject candidate in units)
            {
                if (candidate.Unit == unit)
                {
                    found = candidate;
                    return true;
                }
            }
        }
        found = default;
        return false;
    }

    private static AccessTools.FieldRef<T>? Bind<T>(string name) => Bind<T>(name, Missing);

    // A binding that threw names its exception in the missing entry.
    private static AccessTools.FieldRef<T>? Bind<T>(string name, List<string> missing)
    {
        string error = "";
        try
        {
            FieldInfo? field = AccessTools.DeclaredField(typeof(TransferPlanner), name);
            if (field != null && field.IsStatic && field.FieldType == typeof(T))
                return AccessTools.StaticFieldRefAccess<T>(field);
        }
        catch (Exception ex)
        {
            error = ": " + ex.Message;
        }
        missing.Add($"TransferPlanner.{name} ({typeof(T).Name}){error}");
        return null;
    }

    private static Action? BindSetTransferInfo()
    {
        string error = "";
        try
        {
            MethodInfo? method = AccessTools.DeclaredMethod(typeof(TransferPlanner), "SetTransferInfo", Type.EmptyTypes);
            if (method != null && method.IsStatic && method.ReturnType == typeof(void))
                return method.CreateDelegate<Action>();
        }
        catch (Exception ex)
        {
            error = ": " + ex.Message;
        }
        Missing.Add($"TransferPlanner.SetTransferInfo(){error}");
        return null;
    }

    private sealed class PendingPick
    {
        public PendingPick(Vehicle vehicle, Astronomical destination, CelestialSystem? system)
        {
            Vehicle = vehicle;
            Destination = destination;
            System = system;
        }

        public Vehicle Vehicle { get; }
        public Astronomical Destination { get; }
        public CelestialSystem? System { get; }
    }
}
