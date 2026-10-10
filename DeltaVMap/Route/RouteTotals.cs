using System.Collections.Generic;
using DeltaVMap.Core;

namespace DeltaVMap.Route;

// The figures behind a route's totals, already multiplied by the piloting margin. PlaneChangeDv
// is zero when the player has not opted in. For a route with an interstellar leg OutboundDv is
// the whole trip and only CoastTimeLine is read besides it, because that leg has no return, no
// separate plane change and no aerobrake. An aerobrake body is null when no aerobrake applies.
internal readonly record struct TotalsInput(
    double OutboundDv,
    double ReturnDv,
    double PlaneChangeDv,
    double TransferTimeSeconds,
    string? AerobrakeBodyId,
    string? ReturnAerobrakeBodyId,
    bool ShowReturnTrip,
    int MarginPercent,
    bool Interstellar,
    string? CoastTimeLine);

// The text of a route's totals, split into the headline the panel pins in view (the figure the
// vehicle bar compares against) and the detail lines that scroll with the breakdown. The
// headline figure is never repeated in the details.
internal readonly record struct RouteTotalsText(string Headline, string MarginLine, double Needed);

internal static class RouteTotals
{
    // Fills details with the detail lines (Disabled marks the secondary ones) and returns the
    // headline. Plane change is incurred per interplanetary leg, so a round trip pays it on the
    // outbound and on the return.
    public static RouteTotalsText Build(in TotalsInput input, List<(string Text, bool Disabled)> details)
    {
        details.Clear();

        // A non-zero piloting margin silently inflates every figure, which reads as "the
        // numbers are wrong" if you forget it is on, so it is called out under the headline.
        string marginLine = input.MarginPercent > 0
            ? "incl. +" + Format.Percent(input.MarginPercent) + "% piloting margin"
            : "";

        if (input.Interstellar)
        {
            if (input.CoastTimeLine != null)
                details.Add((input.CoastTimeLine, true));
            return new RouteTotalsText("Total: " + Format.Dv(input.OutboundDv), marginLine, input.OutboundDv);
        }

        double planeChange = input.PlaneChangeDv;
        double outboundTotal = input.OutboundDv + planeChange;
        double returnTotal = input.ReturnDv + planeChange;
        double roundTrip = outboundTotal + returnTotal;

        if (planeChange > 0.0)
            details.Add(("+ plane change: " + Format.Dv(planeChange), true));
        if (input.ShowReturnTrip)
        {
            details.Add(("Outbound: " + Format.Dv(outboundTotal), true));
            details.Add(("Return: " + Format.Dv(returnTotal), true));
        }
        if (input.TransferTimeSeconds > 0.0)
        {
            // The summed transfer time is the outbound coast; a round trip is roughly twice
            // it, so it is labelled one-way when the return is shown.
            string suffix = input.ShowReturnTrip ? " (one way)" : "";
            details.Add(("Transfer time: " + Format.Duration(input.TransferTimeSeconds) + suffix, true));
        }
        if (input.AerobrakeBodyId != null)
            details.Add(("Aerobrake at " + input.AerobrakeBodyId, true));
        if (input.ReturnAerobrakeBodyId != null)
            details.Add(("Aerobrake on return at " + input.ReturnAerobrakeBodyId, true));

        return input.ShowReturnTrip
            ? new RouteTotalsText("Round trip: " + Format.Dv(roundTrip), marginLine, roundTrip)
            : new RouteTotalsText("Total: " + Format.Dv(outboundTotal), marginLine, outboundTotal);
    }
}
