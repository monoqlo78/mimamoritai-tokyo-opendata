namespace MimamoriTai.Core.Abstractions;

/// <summary>One past observation at one station.</summary>
/// <param name="ObservedAtUtc">When the station recorded it.</param>
/// <param name="TemperatureC">Air temperature in degrees Celsius.</param>
/// <param name="HumidityPercent">Relative humidity, where the station reports one.</param>
public sealed record OutdoorObservation(
    DateTimeOffset ObservedAtUtc,
    double TemperatureC,
    double? HumidityPercent);

/// <summary>
/// Reads a weather station's own past observations.
///
/// <para>
/// The live feed this app polls only ever carries the newest reading nationwide, so
/// until now the outdoor history began the moment the app first ran. That makes the
/// chart that lays the weather over a household's electricity unreadable for the first
/// fortnight -- exactly when a family is deciding whether the app is worth keeping --
/// and it empties again the day someone moves the household to a different station.
/// This fills that gap in from the published record.
/// </para>
/// </summary>
public interface IOutdoorHistoryProvider
{
    /// <summary>
    /// Every observation the station recorded on one local day, oldest first.
    /// Returns an empty list rather than throwing when the day cannot be read: a public
    /// website being slow must never be able to fail a startup.
    /// </summary>
    Task<IReadOnlyList<OutdoorObservation>> GetDayAsync(
        string pointCode, DateOnly localDate, CancellationToken ct = default);
}
