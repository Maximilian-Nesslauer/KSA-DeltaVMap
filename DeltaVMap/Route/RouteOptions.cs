namespace DeltaVMap.Route;

// The route toggles. These modify how a selected route is accumulated;
// changing any of them recomputes the route live. All default off, so a route shows the
// plain cost to reach the destination (its low orbit / intercept) until the user opts into
// landing, return trips, etc. "Full ladder everywhere" is a detail/layout toggle and lives
// on the window (it rebuilds the tree), not here.
internal sealed class RouteOptions
{
    // Start the route at the root body's surface (add the ascent) instead of the
    // "you are here" state. Off by default; the route then starts from where the
    // vehicle actually is.
    public bool FromSurface;

    // Extend the route down to the destination body's surface (the final descent /
    // landing). Off by default, so a route stops at the destination's low orbit; clicking
    // a body's surface still turns it on automatically (MapWindow), so an explicit
    // surface target reads correctly.
    public bool LandAtDestination;

    // Aerobrake the capture at the last atmospheric body the route enters from outside,
    // zeroing that capture burn. Outbound only. Has no effect unless the route actually
    // captures into a body with a usable atmosphere.
    public bool Aerobraking;

    // Aerobrake the capture on the way back, at the origin body (where the return trip
    // arrives). Only meaningful with ShowReturnTrip on and an atmospheric origin; kept
    // separate from the outbound aerobrake because the two legs are independent.
    public bool AerobrakingReturn;

    // Add the inclination-change dV as a separate figure, excluded from the baseline
    // total (KSP-map convention). Only the interplanetary (sibling) legs contribute.
    public bool IncludePlaneChange;

    // Show the round trip (outbound + return). The return pays full captures with no
    // aerobrake benefit, so it is not simply double the outbound.
    public bool ShowReturnTrip;
}
