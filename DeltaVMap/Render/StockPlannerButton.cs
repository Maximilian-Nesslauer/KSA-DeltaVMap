using System;
using Brutal.ImGuiApi;
using DeltaVMap.Core;
using DeltaVMap.Route;
using KSA;

namespace DeltaVMap.Render;

// The stock planner button of an in-system route: it opens the stock Transfer Planner on the
// Hohmann plan from the controlled vessel to the route's target body. Flight only, because stock
// plans from a vessel in flight. An interstellar route has its own button in InterstellarSection.
internal sealed class StockPlannerButton
{
    // The hint is cached on these references, so the mirror of stock's list runs only when one
    // of them changes.
    private Vehicle? _hintVehicle;
    private IParentBody? _hintParent;
    private Astronomical? _hintTarget;
    private string _hintText = "";

    public void Draw(RouteSummary summary)
    {
        try
        {
            if (Program.Editor != null || summary.IsEmpty || summary.Interstellar != null)
                return;
            Vehicle? vehicle = ControlledCraft.VehicleWithParent();
            if (vehicle == null)
                return;
            Astronomical target = summary.Target.Body;
            // Stock never lists the body the vessel orbits, and a landing there has no Hohmann
            // plan.
            if (target is Vehicle || target is not IOrbiter || target.Id == vehicle.Parent.Id)
                return;

            ImGui.Separator();
            if (ConsoleWidgets.Button("OPEN STOCK TRANSFER PLANNER".AsSpan()))
                StockPlannerBridge.OpenHohmann(vehicle, target);
            if (!StockPlannerBridge.IsHohmannAvailable)
            {
                ImGui.TextWrapped("Set PLAN TYPE to Hohmann there.");
                return;
            }
            IParentBody parent = vehicle.Parent;
            if (!ReferenceEquals(vehicle, _hintVehicle) || !ReferenceEquals(parent, _hintParent) || !ReferenceEquals(target, _hintTarget))
            {
                _hintVehicle = vehicle;
                _hintParent = parent;
                _hintTarget = target;
                _hintText = StockPlannerBridge.IsHohmannTarget(vehicle, target)
                    ? "Opens on the Hohmann plan to " + target.Id + "."
                    : "Opens on the Hohmann plan. Stock does not list " + target.Id + " from this orbit, pick the destination there.";
            }
            ImGui.TextWrapped(_hintText);
        }
        catch (Exception ex)
        {
            LogHelper.WarnOnce("stock-planner-button", $"[DvMap] Could not draw the stock planner button: {ex.Message}");
        }
    }
}
