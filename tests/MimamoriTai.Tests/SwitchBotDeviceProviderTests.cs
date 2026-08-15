using Microsoft.Extensions.Logging.Abstractions;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Devices;

namespace MimamoriTai.Tests;

/// <summary>Returns canned JSON without ever touching the network, mirroring FakeLineMessagingClient's role.</summary>
public sealed class FakeSwitchBotClient : ISwitchBotClient
{
    public bool IsConfigured { get; init; } = true;

    public string DeviceListResponse { get; set; } = "{}";
    public string DeviceStatusResponse { get; set; } = "{}";
    public string CommandResponse { get; set; } = """{"statusCode":100,"message":"success","body":{}}""";

    public List<(string DeviceId, string Command, string Parameter, string CommandType)> SentCommands { get; } = [];

    /// <summary>
    /// Every deviceId passed to <see cref="GetDeviceStatusRawAsync"/>, in call order.
    /// Lets tests assert the transport-level call count for GET .../status directly
    /// (e.g. that SwitchBotPollingCycleService/SwitchBotDeviceProvider never call the
    /// live status endpoint twice for the same device in one poll cycle).
    /// </summary>
    public List<string> StatusRequests { get; } = [];

    public Task<string> GetDeviceListRawAsync(CancellationToken ct = default) =>
        Task.FromResult(DeviceListResponse);

    public Task<string> GetDeviceStatusRawAsync(string deviceId, CancellationToken ct = default)
    {
        StatusRequests.Add(deviceId);
        return Task.FromResult(DeviceStatusResponse);
    }

    public Task<string> SendCommandRawAsync(string deviceId, string command, string parameter, string commandType, CancellationToken ct = default)
    {
        SentCommands.Add((deviceId, command, parameter, commandType));
        return Task.FromResult(CommandResponse);
    }
}

public class SwitchBotDeviceProviderTests
{
    // Realistic shape taken from the official SwitchBot OpenAPI v1.1 documentation
    // (README.md "Get device list" example), trimmed to the fields this provider reads.
    private const string RealisticDeviceListJson = """
        {
            "statusCode": 100,
            "message": "success",
            "body": {
                "deviceList": [
                    {
                        "deviceId": "AAAAAAAAAAAA",
                        "deviceName": "リビング照明",
                        "deviceType": "Color Bulb",
                        "enableCloudService": true,
                        "hubDeviceId": "000000000000"
                    },
                    {
                        "deviceId": "BBBBBBBBBBBB",
                        "deviceName": "扇風機プラグ",
                        "deviceType": "Plug Mini (JP)",
                        "enableCloudService": true,
                        "hubDeviceId": "CCCCCCCCCCCC"
                    },
                    {
                        "deviceId": "DDDDDDDDDDDD",
                        "deviceName": "謎のセンサー",
                        "deviceType": "Some Future Device",
                        "enableCloudService": true,
                        "hubDeviceId": "000000000000"
                    }
                ],
                "infraredRemoteList": [
                    {
                        "deviceId": "EEEEEEEEEEEE",
                        "deviceName": "エアコン",
                        "remoteType": "Air Conditioner",
                        "hubDeviceId": "CCCCCCCCCCCC"
                    },
                    {
                        "deviceId": "FFFFFFFFFFFF",
                        "deviceName": "テレビ",
                        "remoteType": "TV",
                        "hubDeviceId": "CCCCCCCCCCCC"
                    }
                ]
            }
        }
        """;

    private static SwitchBotDeviceProvider Create(FakeSwitchBotClient client) =>
        new(client, NullLogger<SwitchBotDeviceProvider>.Instance);

    [Fact]
    public async Task GetDevicesAsync_Maps_Both_Physical_And_Infrared_Devices()
    {
        var client = new FakeSwitchBotClient { DeviceListResponse = RealisticDeviceListJson };
        var provider = Create(client);

        var devices = await provider.GetDevicesAsync();

        Assert.Equal(5, devices.Count);

        var light = devices.Single(d => d.ExternalDeviceId == "AAAAAAAAAAAA");
        Assert.Equal("リビング照明", light.Name);
        Assert.Equal(DeviceType.Light, light.DeviceType);

        var plugMini = devices.Single(d => d.ExternalDeviceId == "BBBBBBBBBBBB");
        Assert.Equal(DeviceType.Plug, plugMini.DeviceType);
        Assert.Contains("CCCCCCCCCCCC", plugMini.Room);

        var aircon = devices.Single(d => d.ExternalDeviceId == "EEEEEEEEEEEE");
        Assert.Equal(DeviceType.Heater, aircon.DeviceType);

        var tv = devices.Single(d => d.ExternalDeviceId == "FFFFFFFFFFFF");
        Assert.Equal(DeviceType.Unknown, tv.DeviceType);
    }

    [Fact]
    public async Task GetDevicesAsync_Maps_Unknown_Device_Type_To_Restricted_Safety_Class()
    {
        var client = new FakeSwitchBotClient { DeviceListResponse = RealisticDeviceListJson };
        var provider = Create(client);

        var devices = await provider.GetDevicesAsync();
        var unknown = devices.Single(d => d.ExternalDeviceId == "DDDDDDDDDDDD");

        Assert.Equal(DeviceType.Unknown, unknown.DeviceType);
        Assert.Equal(SafetyClass.Restricted, DeviceSafetyPolicy.Classify(unknown.DeviceType));
    }

    [Fact]
    public async Task GetDevicesAsync_Returns_Empty_When_StatusCode_Is_Not_100()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceListResponse = """{"statusCode":190,"message":"System error","body":{}}"""
        };
        var provider = Create(client);

        var devices = await provider.GetDevicesAsync();

        Assert.Empty(devices);
    }

    [Fact]
    public async Task GetDevicesAsync_Returns_Empty_On_Malformed_Json()
    {
        var client = new FakeSwitchBotClient { DeviceListResponse = "{not valid json!!" };
        var provider = Create(client);

        var devices = await provider.GetDevicesAsync();

        Assert.Empty(devices);
    }

    [Fact]
    public async Task GetStatusAsync_Maps_Power_Field_For_Bot()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """
                {"statusCode":100,"message":"success","body":{"deviceId":"AAAAAAAAAAAA","deviceType":"Bot","power":"on","battery":100}}
                """
        };
        var provider = Create(client);

        var status = await provider.GetStatusAsync("AAAAAAAAAAAA");

        Assert.NotNull(status);
        Assert.True(status!.IsOn);
    }

    [Fact]
    public async Task GetStatusAsync_Infers_State_From_ElectricCurrent_For_Plug_Mini()
    {
        // Plug Mini (JP) status has no "power" field per the official spec -- only
        // voltage/weight/electricityOfDay/electricCurrent.
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """
                {"statusCode":100,"message":"success","body":{"deviceId":"BBBBBBBBBBBB","deviceType":"Plug Mini (JP)","voltage":100.5,"weight":12.3,"electricityOfDay":30,"electricCurrent":0.5}}
                """
        };
        var provider = Create(client);

        var status = await provider.GetStatusAsync("BBBBBBBBBBBB");

        Assert.NotNull(status);
        Assert.True(status!.IsOn);
        Assert.Equal(12.3, status.PowerWatts);
    }

    [Fact]
    public async Task GetStatusAsync_Keeps_Wattage_When_Plug_Mini_Also_Reports_Power()
    {
        // Real Plug Minis report BOTH a relay state and the wattage. Dropping the wattage
        // here left the dashboard unable to tell "on and running" from "on at 0 W", so a
        // plug whose appliance had been off for hours still read 使用中.
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """
                {"statusCode":100,"message":"success","body":{"deviceId":"CCCCCCCCCCCC","deviceType":"Plug Mini (JP)","power":"on","voltage":102.9,"weight":0,"electricityOfDay":0,"electricCurrent":0}}
                """
        };
        var provider = Create(client);

        var status = await provider.GetStatusAsync("CCCCCCCCCCCC");

        Assert.NotNull(status);
        Assert.True(status!.IsOn);
        Assert.Equal(0, status.PowerWatts);
    }

    [Fact]
    public async Task GetStatusAsync_Returns_Null_When_StatusCode_Is_Not_100()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """{"statusCode":401,"message":"Unauthorized","body":{}}"""
        };
        var provider = Create(client);

        var status = await provider.GetStatusAsync("AAAAAAAAAAAA");

        Assert.Null(status);
    }

    [Fact]
    public async Task GetStatusAsync_Returns_Null_On_Malformed_Json()
    {
        var client = new FakeSwitchBotClient { DeviceStatusResponse = "not json at all" };
        var provider = Create(client);

        var status = await provider.GetStatusAsync("AAAAAAAAAAAA");

        Assert.Null(status);
    }

    [Fact]
    public async Task TurnOnAsync_Sends_TurnOn_Command_And_Succeeds()
    {
        var client = new FakeSwitchBotClient();
        var provider = Create(client);

        var result = await provider.TurnOnAsync("AAAAAAAAAAAA");

        Assert.True(result.Success);
        Assert.Single(client.SentCommands);
        Assert.Equal("turnOn", client.SentCommands[0].Command);
    }

    [Fact]
    public async Task TurnOnAsync_Fails_Without_Throwing_When_StatusCode_Is_Not_100()
    {
        var client = new FakeSwitchBotClient
        {
            CommandResponse = """{"statusCode":151,"message":"Device internal error","body":{}}"""
        };
        var provider = Create(client);

        var result = await provider.TurnOnAsync("AAAAAAAAAAAA");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ToggleAsync_Turns_Off_When_Currently_On()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """{"statusCode":100,"message":"success","body":{"deviceId":"AAAAAAAAAAAA","deviceType":"Bot","power":"on"}}"""
        };
        var provider = Create(client);

        var result = await provider.ToggleAsync("AAAAAAAAAAAA");

        Assert.True(result.Success);
        Assert.Single(client.SentCommands);
        Assert.Equal("turnOff", client.SentCommands[0].Command);
    }

    [Fact]
    public async Task ToggleAsync_Fails_Without_Throwing_When_Status_Cannot_Be_Determined()
    {
        // Infrared remotes have no status endpoint; SwitchBot returns an error for them.
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """{"statusCode":190,"message":"System error","body":{}}"""
        };
        var provider = Create(client);

        var result = await provider.ToggleAsync("EEEEEEEEEEEE");

        Assert.False(result.Success);
        Assert.Empty(client.SentCommands);
    }

    [Fact]
    public async Task GetPlugMiniReadingAsync_Maps_Voltage_Current_Weight_And_ElectricityOfDay()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """
                {"statusCode":100,"message":"success","body":{"deviceId":"BBBBBBBBBBBB","deviceType":"Plug Mini (JP)","voltage":100.5,"weight":12.3,"electricityOfDay":30,"electricCurrent":0.5}}
                """
        };
        var provider = Create(client);

        var reading = await provider.GetPlugMiniReadingAsync("BBBBBBBBBBBB");

        Assert.NotNull(reading);
        Assert.Equal("BBBBBBBBBBBB", reading!.ExternalDeviceId);
        Assert.Equal(100.5, reading.VoltageV);
        Assert.Equal(0.5, reading.CurrentMa);
        Assert.Equal(12.3, reading.DailyEnergyWh);
        Assert.Equal(30, reading.UsageMinutesToday);
        // ApproxWatts = voltage * current / 1000 (power-factor-1 approximation).
        Assert.Equal(100.5 * 0.5 / 1000, reading.ApproxWatts);
    }

    [Fact]
    public async Task GetPlugMiniReadingAsync_Returns_Null_For_A_Non_PlugMini_Device()
    {
        // A plain Bot reports only "power"; none of the Plug Mini telemetry fields are present.
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """{"statusCode":100,"message":"success","body":{"deviceId":"AAAAAAAAAAAA","deviceType":"Bot","power":"on"}}"""
        };
        var provider = Create(client);

        var reading = await provider.GetPlugMiniReadingAsync("AAAAAAAAAAAA");

        Assert.Null(reading);
    }

    [Fact]
    public async Task GetPlugMiniReadingAsync_Returns_Null_When_StatusCode_Is_Not_100()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """{"statusCode":401,"message":"Unauthorized","body":{}}"""
        };
        var provider = Create(client);

        var reading = await provider.GetPlugMiniReadingAsync("BBBBBBBBBBBB");

        Assert.Null(reading);
    }

    [Fact]
    public async Task GetPlugMiniReadingAsync_Returns_Null_And_Does_Not_Throw_On_Malformed_Json()
    {
        var client = new FakeSwitchBotClient { DeviceStatusResponse = "not json at all" };
        var provider = Create(client);

        var reading = await provider.GetPlugMiniReadingAsync("BBBBBBBBBBBB");

        Assert.Null(reading);
    }

    [Fact]
    public async Task GetPlugMiniReadingAsync_Handles_Partial_Telemetry_Gracefully()
    {
        // Only electricCurrent present (e.g. a transient/incomplete poll): still
        // recognized as Plug Mini telemetry, remaining fields surface as null rather
        // than the whole reading being discarded.
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """{"statusCode":100,"message":"success","body":{"deviceId":"BBBBBBBBBBBB","deviceType":"Plug Mini (JP)","electricCurrent":0.2}}"""
        };
        var provider = Create(client);

        var reading = await provider.GetPlugMiniReadingAsync("BBBBBBBBBBBB");

        Assert.NotNull(reading);
        Assert.Null(reading!.VoltageV);
        Assert.Equal(0.2, reading.CurrentMa);
        Assert.Null(reading.DailyEnergyWh);
        Assert.Null(reading.UsageMinutesToday);
        Assert.Null(reading.ApproxWatts); // needs both voltage AND current to compute
    }

    [Fact]
    public async Task GetStatusSnapshotAsync_Issues_Exactly_One_Status_Request_For_A_Plug_Mini_Device()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """
                {"statusCode":100,"message":"success","body":{"deviceId":"BBBBBBBBBBBB","deviceType":"Plug Mini (JP)","voltage":100.5,"weight":12.3,"electricityOfDay":30,"electricCurrent":0.5}}
                """
        };
        var provider = Create(client);

        var snapshot = await provider.GetStatusSnapshotAsync("BBBBBBBBBBBB");

        // Exactly one call to the underlying transport for this one snapshot call --
        // the whole point of GetStatusSnapshotAsync is to avoid the previous
        // double-fetch (one for GetStatusAsync, one for GetPlugMiniReadingAsync).
        Assert.Single(client.StatusRequests);
        Assert.Equal("BBBBBBBBBBBB", client.StatusRequests[0]);

        // Both projections must come out of that single request.
        Assert.NotNull(snapshot.Status);
        Assert.Equal("on", snapshot.Status!.State); // electricCurrent > 0 => on
        Assert.NotNull(snapshot.PlugMiniReading);
        Assert.Equal(100.5, snapshot.PlugMiniReading!.VoltageV);
        Assert.Equal(0.5, snapshot.PlugMiniReading.CurrentMa);
        Assert.Equal(12.3, snapshot.PlugMiniReading.DailyEnergyWh);
        Assert.Equal(30, snapshot.PlugMiniReading.UsageMinutesToday);
    }

    [Fact]
    public async Task GetStatusSnapshotAsync_Returns_Null_PlugMiniReading_And_One_Request_For_A_Non_PlugMini_Device()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """{"statusCode":100,"message":"success","body":{"deviceId":"AAAAAAAAAAAA","deviceType":"Bot","power":"on"}}"""
        };
        var provider = Create(client);

        var snapshot = await provider.GetStatusSnapshotAsync("AAAAAAAAAAAA");

        Assert.Single(client.StatusRequests); // one request, not a second one to "check" for Plug Mini fields
        Assert.NotNull(snapshot.Status);
        Assert.Equal("on", snapshot.Status!.State);
        Assert.Null(snapshot.PlugMiniReading); // no Plug-specific work/data for a non-Plug-Mini device
    }

    [Fact]
    public async Task GetStatusSnapshotAsync_Returns_All_Null_When_StatusCode_Is_Not_100()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """{"statusCode":401,"message":"Unauthorized","body":{}}"""
        };
        var provider = Create(client);

        var snapshot = await provider.GetStatusSnapshotAsync("BBBBBBBBBBBB");

        Assert.Single(client.StatusRequests);
        Assert.Null(snapshot.Status);
        Assert.Null(snapshot.PlugMiniReading);
    }
}

