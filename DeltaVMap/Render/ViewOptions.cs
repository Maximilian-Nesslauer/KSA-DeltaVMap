namespace DeltaVMap.Render;

// Display and visibility settings, separate from the route toggles (RouteOptions). These
// fall into two groups by how they take effect. ShowMinorBodies / ShowComets change which
// bodies the visual tree contains, so the panel triggers a rebuild when they change.
// ShowTransferTimes, ShowBodyMarkers and PilotingMarginPercent are display-only: the
// renderer and panel read them every frame, so changing them needs no rebuild and no
// re-accumulate. All session-only (not persisted yet).
internal sealed class ViewOptions
{
    // Visibility (rebuild on change). Minor bodies are asteroids, comets and minor bodies;
    // comets are the subset of those. Off hides them as destinations (never the root/spine).
    public bool ShowMinorBodies = true;
    public bool ShowComets = true;

    // Display-only. Transfer times ride under transfer dV badges; body markers are the ring
    // ellipse, the atmosphere/jet halo and the aerobrake arrow (named for what they draw, not
    // "feasibility", since a ring is a body feature, not a capability).
    public bool ShowTransferTimes = true;
    public bool ShowBodyMarkers = true;

    // Piloting margin / "fudge factor": inflates every displayed route dV (breakdown, totals,
    // plane change, the vehicle bar's needed, and the on-map badges) by (1 + percent/100), to
    // budget for non-optimal piloting. Applied at display time only, so the route path and the
    // layout are unchanged; 0 leaves the map at its canonical reference values. Clamped 0-50.
    public int PilotingMarginPercent;

    // The multiplier the margin applies to a displayed dV. 1.0 at 0%.
    public double DvScale => 1.0 + PilotingMarginPercent / 100.0;
}
