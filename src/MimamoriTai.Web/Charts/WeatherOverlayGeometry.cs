using System.Globalization;

namespace MimamoriTai.Web.Charts;

/// <summary>One day of the overlay: the household's electricity next to the weather outside.</summary>
/// <param name="Label">Short axis label, e.g. "8/11".</param>
/// <param name="EnergyWh">Electricity used that day, in watt-hours.</param>
/// <param name="LowC">Coldest outdoor reading recorded that day, or null if none was.</param>
/// <param name="HighC">Warmest outdoor reading recorded that day, or null if none was.</param>
/// <param name="IsToday">Marks today so it stands out.</param>
public sealed record WeatherOverlayPoint(
    string Label,
    double EnergyWh,
    double? LowC,
    double? HighC,
    bool IsToday = false);

/// <summary>
/// Geometry for the chart that lays the weather over the household's electricity.
///
/// <para>
/// Two quantities with nothing in common share one plot here -- watt-hours and degrees --
/// so each gets its own scale and the picture is only ever read as "do these move
/// together?". That is the whole point: a fortnight of bars says nothing about whether a
/// grandmother is coping, but a fortnight of bars that stay flat while the line outside
/// dives says she is going without heating.
/// </para>
/// <para>
/// The temperature scale is padded and never collapses onto a single line, because a
/// stable week would otherwise draw a jagged line out of half a degree of noise and
/// invent a story that is not there.
/// </para>
/// </summary>
public static class WeatherOverlayGeometry
{
    public const double ViewWidth = 100;
    public const double ViewHeight = 40;

    public const double PlotBottom = ViewHeight - 4;
    public const double PlotTop = 3;

    /// <summary>Days with no recorded use still occupy their slot; an empty gap reads as missing data.</summary>
    public const double MinBarHeight = 0.5;

    /// <summary>Smallest temperature span the scale will show, in degrees.</summary>
    public const double MinDegreeSpan = 6;

    public static double Slot(int count) => ViewWidth / Math.Max(count, 1);

    public static double BarWidth(int count) => Math.Max(Slot(count) * 0.5, 0.5);

    public static double BarX(int index, int count)
    {
        var slot = Slot(count);
        return (slot * index) + ((slot - BarWidth(count)) / 2);
    }

    public static double CenterX(int index, int count) => BarX(index, count) + (BarWidth(count) / 2);

    public static double MaxEnergy(IReadOnlyList<WeatherOverlayPoint> points) =>
        points.Count == 0 ? 0 : points.Max(p => p.EnergyWh);

    public static double BarHeight(double wh, double max)
    {
        if (max <= 0 || wh <= 0 || double.IsNaN(wh) || double.IsNaN(max))
        {
            return MinBarHeight;
        }

        return Math.Clamp(wh / max * (PlotBottom - PlotTop), MinBarHeight, PlotBottom - PlotTop);
    }

    public static double BarTop(double wh, double max) => PlotBottom - BarHeight(wh, max);

    /// <summary>
    /// The temperature axis, widened to <see cref="MinDegreeSpan"/> around its own middle
    /// and then given a degree of headroom so the warm line never rides the frame.
    /// </summary>
    public static (double Low, double High) DegreeScale(IReadOnlyList<WeatherOverlayPoint> points)
    {
        var lows = points.Where(p => p.LowC is not null).Select(p => p.LowC!.Value).ToList();
        var highs = points.Where(p => p.HighC is not null).Select(p => p.HighC!.Value).ToList();

        if (lows.Count == 0 && highs.Count == 0)
        {
            return (0, MinDegreeSpan);
        }

        var low = lows.Count == 0 ? highs.Min() : lows.Min();
        var high = highs.Count == 0 ? lows.Max() : highs.Max();

        low -= 1;
        high += 1;

        var span = high - low;
        if (span < MinDegreeSpan)
        {
            var middle = (high + low) / 2;
            low = middle - (MinDegreeSpan / 2);
            high = middle + (MinDegreeSpan / 2);
        }

        return (low, high);
    }

    public static double DegreeY(double celsius, (double Low, double High) scale)
    {
        var span = scale.High - scale.Low;
        if (span <= 0)
        {
            return (PlotTop + PlotBottom) / 2;
        }

        var ratio = (celsius - scale.Low) / span;

        return Math.Clamp(PlotBottom - (ratio * (PlotBottom - PlotTop)), PlotTop, PlotBottom);
    }

    /// <summary>
    /// Points for one temperature line. Days without a reading are skipped rather than
    /// drawn as zero -- an outage should leave a gap, not a plunge to freezing.
    /// </summary>
    public static string Line(
        IReadOnlyList<WeatherOverlayPoint> points,
        bool high,
        (double Low, double High) scale)
    {
        var parts = new List<string>(points.Count);

        for (var i = 0; i < points.Count; i++)
        {
            var value = high ? points[i].HighC : points[i].LowC;
            if (value is null)
            {
                continue;
            }

            parts.Add($"{F(CenterX(i, points.Count))},{F(DegreeY(value.Value, scale))}");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// The band between the day's low and high, closed into a shape so the swing between
    /// morning and afternoon is visible at a glance -- that swing is what a body actually
    /// has to cope with.
    /// </summary>
    public static string Band(IReadOnlyList<WeatherOverlayPoint> points, (double Low, double High) scale)
    {
        var tops = new List<string>();
        var bottoms = new List<string>();

        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].LowC is not { } low || points[i].HighC is not { } high)
            {
                continue;
            }

            var x = F(CenterX(i, points.Count));
            tops.Add($"{x},{F(DegreeY(high, scale))}");
            bottoms.Add($"{x},{F(DegreeY(low, scale))}");
        }

        if (tops.Count < 2)
        {
            return string.Empty;
        }

        bottoms.Reverse();

        return $"M{tops[0]} L{string.Join(" L", tops.Skip(1))} L{string.Join(" L", bottoms)} Z";
    }

    public static string BarClass(WeatherOverlayPoint point) =>
        point.IsToday ? "overlay-bar is-today" : "overlay-bar";

    /// <summary>Spoken description of one day, used for the tooltip and the accessible table.</summary>
    public static string Describe(WeatherOverlayPoint point)
    {
        var energy = $"{F(Math.Round(point.EnergyWh))}Wh";

        if (point.LowC is not { } low || point.HighC is not { } high)
        {
            return $"{point.Label} 電気 {energy}／気温の記録なし";
        }

        return $"{point.Label} 電気 {energy}／気温 {F(low)}〜{F(high)}℃";
    }

    public static string F(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
