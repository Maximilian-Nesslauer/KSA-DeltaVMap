using System;
using System.Globalization;
using DeltaVMap.Dv;

namespace DeltaVMap.Core;

// The number formats every panel, badge, tooltip and dump of the map uses, in one place so they
// read alike. Free of game types, so the unit tests lock the strings. Every format uses the
// invariant culture, so a number never shows a locale comma and stays ASCII.
internal static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The light-year the stock readouts print (DistanceReference.ToNearest divides astronomical
    // units by 63241.077084), so a distance here reads the same as in the stock planner.
    public const double LightYear = 63241.077084 * InterstellarMath.AstronomicalUnit;

    private const double JulianYearDays = 365.25;

    // A delta-v figure for display. The leading "~" marks every figure on the map as an estimate.
    public static string Dv(double dv)
    {
        return "~" + DvNumber(dv) + " m/s";
    }

    // A rounded delta-v magnitude with thousands separators, without unit and "~".
    public static string DvNumber(double dv)
    {
        return Math.Round(dv).ToString("#,##0", Inv);
    }

    // A duration in minutes, hours, days, then years, with thousands grouped from 1,000 years
    // (interstellar coasts run to many thousands). "-" for a non-finite duration, such as a
    // window that never recurs.
    public static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds))
            return "-";
        if (seconds < 3600.0)
            return (seconds / 60.0).ToString("0", Inv) + " min";
        if (seconds < 86400.0)
            return (seconds / 3600.0).ToString("0.0", Inv) + " h";
        double days = seconds / 86400.0;
        if (days < JulianYearDays)
            return days.ToString("0.0", Inv) + " d";
        double years = days / JulianYearDays;
        if (years < 1000.0)
            return years.ToString("0.00", Inv) + " yr";
        return years.ToString("#,##0", Inv) + " yr";
    }

    // The compact transfer time on a map badge: like Duration, but years always with one
    // decimal and no grouping, so a badge stays narrow.
    public static string TransferTime(double seconds)
    {
        if (!double.IsFinite(seconds))
            return "-";
        if (seconds < 86400.0 * JulianYearDays)
            return Duration(seconds);
        return (seconds / 86400.0 / JulianYearDays).ToString("0.0", Inv) + " yr";
    }

    // A key that changes exactly when Duration shows another string.
    public static long DurationKey(double seconds)
    {
        if (!double.IsFinite(seconds))
            return long.MinValue;
        if (seconds < 3600.0)
            return Rounded(seconds / 60.0);
        if (seconds < 86400.0)
            return 1L << 56 | Rounded(seconds / 3600.0 * 10.0);
        double days = seconds / 86400.0;
        if (days < JulianYearDays)
            return 2L << 56 | Rounded(days * 10.0);
        double years = days / JulianYearDays;
        if (years < 1000.0)
            return 3L << 56 | Rounded(years * 100.0);
        return 4L << 56 | Rounded(years);
    }

    private static long Rounded(double value)
    {
        return (long)Math.Round(Math.Min(value, 1e15), MidpointRounding.AwayFromZero);
    }

    // The compact countdown of a transfer-window marker, without spaces so the badge stays small.
    public static string WindowTime(double seconds)
    {
        if (!double.IsFinite(seconds))
            return "-";
        if (seconds < 3600.0)
            return (seconds / 60.0).ToString("0", Inv) + "m";
        if (seconds < 86400.0)
            return (seconds / 3600.0).ToString("0", Inv) + "h";
        double days = seconds / 86400.0;
        if (days < JulianYearDays)
            return days.ToString("0", Inv) + "d";
        return (days / JulianYearDays).ToString("0.0", Inv) + "yr";
    }

    // A key that changes exactly when WindowTime shows another string, so a caller can keep the
    // string until the shown value moves.
    public static long WindowTimeKey(double seconds)
    {
        if (!double.IsFinite(seconds))
            return long.MinValue;
        if (seconds < 3600.0)
            return Rounded(seconds / 60.0);
        if (seconds < 86400.0)
            return 1L << 56 | Rounded(seconds / 3600.0);
        double days = seconds / 86400.0;
        if (days < JulianYearDays)
            return 2L << 56 | Rounded(days);
        return 3L << 56 | Rounded(days / JulianYearDays * 10.0);
    }

    // A speed as a fraction of c from 0.001 c up, as the stock planner shows it, with km/s beside
    // it; km/s alone below.
    public static string Speed(double speed)
    {
        double c = InterstellarMath.SpeedOfLight;
        if (speed >= 0.001 * c)
            return SpeedC(speed) + " (" + (speed / 1000.0).ToString("#,##0", Inv) + " km/s)";
        return (speed / 1000.0).ToString("#,##0.0", Inv) + " km/s";
    }

    // A speed as a fraction of c alone.
    public static string SpeedC(double speed)
    {
        return (speed / InterstellarMath.SpeedOfLight).ToString("0.0000", Inv) + " c";
    }

    // Light-years between star systems, astronomical units inside one, kilometers close in.
    public static string Distance(double meters)
    {
        if (meters >= 0.01 * LightYear)
            return (meters / LightYear).ToString("0.000", Inv) + " ly";
        if (meters >= 0.01 * InterstellarMath.AstronomicalUnit)
            return (meters / InterstellarMath.AstronomicalUnit).ToString("#,##0.00", Inv) + " AU";
        return Km(meters);
    }

    // A propellant mass in kilograms below a tonne, in tonnes above. "-" when not finite.
    public static string Mass(double kg)
    {
        if (!double.IsFinite(kg))
            return "-";
        if (Math.Abs(kg) < 1000.0)
            return kg.ToString("#,##0.0", Inv) + " kg";
        return (kg / 1000.0).ToString("#,##0.00", Inv) + " t";
    }

    // The mass of a body, which spans many decades, in scientific notation.
    public static string BodyMass(double kg)
    {
        return kg.ToString("0.###e0", Inv) + " kg";
    }

    public static string Km(double meters)
    {
        return (meters / 1000.0).ToString("#,##0", Inv) + " km";
    }

    // An angle given in radians, as whole degrees.
    public static string Degrees(double radians)
    {
        return Math.Round(radians * 180.0 / Math.PI).ToString("0", Inv);
    }

    // An angle given in radians, as whole degrees with an explicit sign.
    public static string DegreesSigned(double radians)
    {
        return Math.Round(radians * 180.0 / Math.PI).ToString("+0;-0;0", Inv);
    }

    // A whole percentage, for the piloting-margin note.
    public static string Percent(double percent)
    {
        return percent.ToString("0", Inv);
    }
}
