using Microsoft.AspNetCore.Http;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Web.Endpoints;

namespace MimamoriTai.Tests;

/// <summary>
/// Regression tests for the device endpoints' household scoping.
///
/// These exist because of a specific defect: <c>GET /api/devices</c> shipped with no
/// household filter and no access check at all, so it returned every device row in the
/// database -- names, rooms and aliases of strangers' homes -- to any caller, including
/// an anonymous one. Every neighbouring endpoint in the same file already went through
/// <see cref="HouseholdAccessService"/>, which is what makes this worth pinning: the
/// rule was established, and this was the one endpoint that silently opted out of it.
///
/// Scoped like SwitchBotConnectionEndpointsTests: the handlers are invoked directly
/// (via InternalsVisibleTo) rather than over HTTP, so route matching and serialization
/// are not covered here -- only the authorization decision and the row filtering.
/// </summary>
public class DeviceEndpointAuthorizationTests
{
    [Fact]
    public async Task ListDevices_Returns_Only_The_Requested_Households_Devices()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var (otherHouseholdId, _) = await AddSampleHouseholdAsync(db, "隣の家", "neighbour-plug");

        var access = Access(db, current: null);

        var result = await ApiEndpoints.ListDevicesAsync(db.HouseholdId, db.Context, access, CancellationToken.None);

        // Both households are Sample here, so both are readable -- the point of this case
        // is the filter, not the guard: asking for A must not also hand back B's devices.
        var devices = ReadOkValue<System.Collections.IEnumerable>(result);
        Assert.NotNull(devices);
        Assert.Single(devices!.Cast<object>());

        var otherResult = await ApiEndpoints.ListDevicesAsync(otherHouseholdId, db.Context, access, CancellationToken.None);
        Assert.Single(ReadOkValue<System.Collections.IEnumerable>(otherResult)!.Cast<object>());
    }

    [Fact]
    public async Task ListDevices_Refuses_A_Production_Household_The_Caller_Is_Not_A_Member_Of()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var (strangerHouseholdId, _) = await AddProductionHouseholdAsync(db, "他人の家", "stranger-plug");

        var access = Access(db, current: null);

        var result = await ApiEndpoints.ListDevicesAsync(strangerHouseholdId, db.Context, access, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, GetStatusCode(result));
    }

    [Fact]
    public async Task ListDevices_Allows_A_Member_Of_The_Production_Household()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var (householdId, _) = await AddProductionHouseholdAsync(db, "自分の家", "my-plug");

        var member = new AppUser { DisplayName = "家族", IdentityProvider = "dev", ExternalSubject = "member-sub" };
        db.Context.AppUsers.Add(member);
        await db.Context.SaveChangesAsync();

        db.Context.HouseholdMembers.Add(new HouseholdMember
        {
            HouseholdId = householdId,
            AppUserId = member.Id,
            Role = HouseholdMemberRole.Owner
        });
        await db.Context.SaveChangesAsync();

        var access = Access(db, FakeCurrentUserAccessor.User(member.Id, member.DisplayName));

        var result = await ApiEndpoints.ListDevicesAsync(householdId, db.Context, access, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, GetStatusCode(result));
        Assert.Single(ReadOkValue<System.Collections.IEnumerable>(result)!.Cast<object>());
    }

    [Fact]
    public async Task GetDevice_Hides_A_Device_In_A_Production_Household_Behind_404()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var (_, strangerDeviceId) = await AddProductionHouseholdAsync(db, "他人の家", "stranger-plug");

        var access = Access(db, current: null);

        var result = await ApiEndpoints.GetDeviceAsync(strangerDeviceId, db.Context, access, CancellationToken.None);

        // 404, not 403: a "forbidden" would confirm that this device id exists.
        Assert.Equal(StatusCodes.Status404NotFound, GetStatusCode(result));
    }

    [Fact]
    public async Task GetDevice_Returns_A_Device_The_Caller_May_Read()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var device = db.Context.Devices.Single();

        var access = Access(db, current: null);

        var result = await ApiEndpoints.GetDeviceAsync(device.Id, db.Context, access, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, GetStatusCode(result));
    }

    private static HouseholdAccessService Access(TestDb db, CurrentUser? current) =>
        new(db.Context, new FakeCurrentUserAccessor(current), new FakeTimeProvider(DateTimeOffset.UtcNow));

    private static Task<(Guid HouseholdId, Guid DeviceId)> AddSampleHouseholdAsync(
        TestDb db, string name, string externalId) =>
        AddHouseholdAsync(db, name, externalId, DataSourceMode.Sample);

    private static Task<(Guid HouseholdId, Guid DeviceId)> AddProductionHouseholdAsync(
        TestDb db, string name, string externalId) =>
        AddHouseholdAsync(db, name, externalId, DataSourceMode.Production);

    private static async Task<(Guid HouseholdId, Guid DeviceId)> AddHouseholdAsync(
        TestDb db, string name, string externalId, DataSourceMode mode)
    {
        var household = new Household { Name = name, DataSourceMode = mode };
        db.Context.Households.Add(household);
        await db.Context.SaveChangesAsync();

        var device = TestDb.Light(alias: externalId, name: $"{name}の家電");
        device.ExternalDeviceId = externalId;
        device.HouseholdId = household.Id;
        db.Context.Devices.Add(device);
        await db.Context.SaveChangesAsync();

        return (household.Id, device.Id);
    }

    private static int? GetStatusCode(IResult result) =>
        result is IStatusCodeHttpResult statusResult ? statusResult.StatusCode : null;

    private static T? ReadOkValue<T>(IResult result) where T : class =>
        result is IValueHttpResult valueResult ? valueResult.Value as T : null;
}
