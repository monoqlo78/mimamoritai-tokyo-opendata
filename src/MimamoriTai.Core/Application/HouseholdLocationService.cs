using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>
/// Where a household lives, expressed as the 気象庁AMeDAS station its weather is read
/// from.
///
/// <para>
/// The family never types a station number. They either tap "現在地から設定" -- which
/// turns a GPS fix into the nearest station that actually measures temperature -- or
/// pick a place name from the list for their prefecture. Both routes end here.
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

    public async Task<AmedasStation?> GetAsync(Guid householdId, CancellationToken ct = default)
    {
        var code = await db.Households
            .Where(h => h.Id == householdId)
            .Select(h => h.AmedasStationCode)
            .FirstOrDefaultAsync(ct);

        return code is { Length: > 0 } ? await catalog.FindAsync(code, ct) : null;
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

        return station is null ? null : await SetAsync(householdId, station, ct);
    }

    public async Task<AmedasStation?> SetByCodeAsync(Guid householdId, string code, CancellationToken ct = default)
    {
        var station = await catalog.FindAsync(code, ct);

        return station is null ? null : await SetAsync(householdId, station, ct);
    }

    private async Task<AmedasStation?> SetAsync(Guid householdId, AmedasStation station, CancellationToken ct)
    {
        var household = await db.Households.FirstOrDefaultAsync(h => h.Id == householdId, ct);
        if (household is null)
        {
            return null;
        }

        household.AmedasStationCode = station.Code;
        household.AmedasStationName = station.Name;
        await db.SaveChangesAsync(ct);

        return station;
    }
}
