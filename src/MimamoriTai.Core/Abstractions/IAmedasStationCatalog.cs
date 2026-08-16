namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// One 気象庁AMeDAS observation station: where it is, and what it is called.
/// </summary>
/// <param name="Code">Five-digit station number, e.g. "44132" (東京).</param>
/// <param name="Name">Japanese station name, which for city stations is the ward or city itself.</param>
/// <param name="Latitude">Decimal degrees, converted from the table's degrees/minutes pair.</param>
/// <param name="Longitude">Decimal degrees.</param>
public sealed record AmedasStation(string Code, string Name, double Latitude, double Longitude)
{
    /// <summary>
    /// Great-circle distance in kilometres, used to pick the station nearest a phone.
    ///
    /// <para>
    /// The haversine formula is overkill for a city and exact for the whole country;
    /// since the catalogue is nationwide, "nearest" has to keep meaning nearest for a
    /// family who moved out of Tokyo.
    /// </para>
    /// </summary>
    public double DistanceKmTo(double latitude, double longitude)
    {
        const double earthRadiusKm = 6371.0;

        var dLat = ToRadians(latitude - Latitude);
        var dLon = ToRadians(longitude - Longitude);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(ToRadians(Latitude)) * Math.Cos(ToRadians(latitude))
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));

        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}

/// <summary>
/// The list of AMeDAS stations that actually report a temperature.
///
/// <para>
/// This exists so a household can say where it lives once and never think about it
/// again. Asking an older resident's family for a station number would be absurd, so
/// the two ways in are a tap on "現在地から設定" and a list of nearby place names --
/// and both are answered from 気象庁's own published station table rather than a
/// hand-maintained list of wards that would rot the first time a station moved.
/// </para>
/// </summary>
public interface IAmedasStationCatalog
{
    /// <summary>
    /// Station closest to a coordinate, or null if the table could not be read.
    /// Never throws: a failed lookup means the household keeps the station it had.
    /// </summary>
    Task<AmedasStation?> FindNearestAsync(double latitude, double longitude, CancellationToken ct = default);

    /// <summary>
    /// Stations whose code starts with <paramref name="codePrefix"/> ("44" is 東京都),
    /// ordered by name for a dropdown. Empty when the table is unavailable.
    /// </summary>
    Task<IReadOnlyList<AmedasStation>> ListAsync(string codePrefix, CancellationToken ct = default);

    Task<AmedasStation?> FindAsync(string code, CancellationToken ct = default);
}
