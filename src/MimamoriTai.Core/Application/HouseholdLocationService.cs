using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>
/// A place a family can pick, and the station its weather will actually come from.
/// </summary>
/// <param name="Station">Null when the station table could not be read.</param>
/// <param name="DistanceKm">
/// How far that station is from the municipal office. Shown rather than hidden, so a
/// household on an island is not led to believe the reading is from their doorstep.
/// </param>
public sealed record HouseholdArea(
    string Name,
    string Group,
    AmedasStation? Station,
    double DistanceKm);

/// <summary>
/// Where a household lives, expressed as the 気象庁AMeDAS station its weather is read
/// from.
///
/// <para>
/// The family never types a station number, and no longer picks one either. They choose
/// their 区市町村 -- or tap "現在地から設定" -- and this turns that into the nearest station
/// that actually measures temperature. Only a handful of stations in Tokyo do, and none is
/// named after a ward, so asking someone in 葛飾区 to recognise "江戸川臨海" was asking them
/// to know the observation network.
/// </para>
/// </summary>
public sealed class HouseholdLocationService(IAppDbContext db, IAmedasStationCatalog catalog)
{
    /// <summary>
    /// Stations available for the dropdown. "44" is 東京都, which is the area this
    /// service watches; the prefix is a parameter so a family outside Tokyo is a
    /// configuration change rather than a code change.
    /// </summary>
    public Task<IReadOnlyList<AmedasStation>> ListAsync(string codePrefix = "44", CancellationToken ct = default) =>
        catalog.ListAsync(codePrefix, ct);

    /// <summary>
    /// Every 区市町村 in 東京都, each already resolved to the station it would use.
    ///
    /// <para>
    /// Resolution runs against the whole national table rather than the 東京都 prefix: for a
    /// household in 町田市 the nearest thermometer may well stand in 神奈川県, and insisting
    /// on a tidy prefecture code would hand them a reading from further away.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<HouseholdArea>> ListAreasAsync(CancellationToken ct = default)
    {
        var stations = await catalog.ListAsync(string.Empty, ct);

        return
        [
            .. TokyoMunicipalities.All.Select(m =>
            {
                var nearest = stations
                    .Select(s => (Station: (AmedasStation?)s, Km: s.DistanceKmTo(m.Latitude, m.Longitude)))
                    .OrderBy(x => x.Km)
                    .FirstOrDefault();

                return new HouseholdArea(m.Name, m.Group, nearest.Station, nearest.Km);
            })
        ];
    }

    /// <summary>The place the family chose and the station currently in use.</summary>
    public async Task<(string? AreaName, AmedasStation? Station)> GetAsync(
        Guid householdId, CancellationToken ct = default)
    {
        var current = await db.Households
            .Where(h => h.Id == householdId)
            .Select(h => new { h.AmedasStationCode, h.AreaName })
            .FirstOrDefaultAsync(ct);

        if (current is null)
        {
            return (null, null);
        }

        var station = current.AmedasStationCode is { Length: > 0 }
            ? await catalog.FindAsync(current.AmedasStationCode, ct)
            : null;

        return (current.AreaName, station);
    }

    /// <summary>
    /// Resolves a coordinate to the nearest station and stores it. Returns null when
    /// the station table could not be read, so the caller can say so rather than
    /// silently leaving the household pointed somewhere else.
    /// </summary>
    public async Task<AmedasStation?> SetFromCoordinateAsync(
        Guid householdId, double latitude, double longitude, CancellationToken ct = default)
    {
        var station = await catalog.FindNearestAsync(latitude, longitude, ct);
        if (station is null)
        {
            return null;
        }

        // The fix names the station, but the screen should still show a place the family
        // recognises, so the nearest municipality is recorded next to it.
        var area = TokyoMunicipalities.All
            .OrderBy(m => Distance(latitude, longitude, m.Latitude, m.Longitude))
            .FirstOrDefault();

        return await SetAsync(householdId, station, area?.Name, ct);
    }

    /// <summary>Applies a 区市町村 the family picked, resolving it to a station on their behalf.</summary>
    public async Task<AmedasStation?> SetByAreaAsync(
        Guid householdId, string areaName, CancellationToken ct = default)
    {
        if (TokyoMunicipalities.Find(areaName) is not { } area)
        {
            return null;
        }

        var station = await catalog.FindNearestAsync(area.Latitude, area.Longitude, ct);

        return station is null ? null : await SetAsync(householdId, station, area.Name, ct);
    }

    public async Task<AmedasStation?> SetByCodeAsync(Guid householdId, string code, CancellationToken ct = default)
    {
        var station = await catalog.FindAsync(code, ct);

        return station is null ? null : await SetAsync(householdId, station, areaName: null, ct);
    }

    private async Task<AmedasStation?> SetAsync(
        Guid householdId, AmedasStation station, string? areaName, CancellationToken ct)
    {
        var household = await db.Households.FirstOrDefaultAsync(h => h.Id == householdId, ct);
        if (household is null)
        {
            return null;
        }

        household.AmedasStationCode = station.Code;
        household.AmedasStationName = station.Name;

        if (areaName is { Length: > 0 })
        {
            household.AreaName = areaName;
        }

        await db.SaveChangesAsync(ct);

        return station;
    }

    /// <summary>
    /// Flat-earth distance, good enough to tell one ward office from another and far
    /// cheaper than haversine across 62 candidates on every GPS fix.
    /// </summary>
    private static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = lat1 - lat2;
        var dLon = (lon1 - lon2) * Math.Cos(lat1 * Math.PI / 180);

        return Math.Sqrt((dLat * dLat) + (dLon * dLon));
    }
}
