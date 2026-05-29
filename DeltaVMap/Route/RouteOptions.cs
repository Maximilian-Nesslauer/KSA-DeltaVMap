namespace DeltaVMap.Route;

// The route toggles. These modify how a selected route is accumulated;
// changing any of them recomputes the route live. Defaults match the design: only
// land-at-destination is on. "Full ladder everywhere" is a detail/layout toggle and
// lives on the window (it rebuilds the tree), not here.
internal sealed class RouteOptions
{
    // Start the route at the root body's surface (add the ascent) instead of the
    // "you are here" state. Off by default; the route then starts from where the
    // vehicle actually is.
    public bool FromSurface;

    // Extend the route down to the destination body's surface (the final descent /
    // landing). On by default, matching the KSP-map convention that a body's headline
    // figure is "to its surface".
    public bool LandAtDestination = true;

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
