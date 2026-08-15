using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Infrastructure.Devices;

/// <summary>
/// Real SwitchBot OpenAPI v1.1 provider. Maps the raw JSON returned by
/// <see cref="ISwitchBotClient"/> onto the application's provider abstractions.
///
/// Response shapes verified against the official specification:
/// https://github.com/OpenWonderLabs/SwitchBotAPI (README.md "Devices" section, plus
/// the per-device docs under devices/*.md, e.g. devices/others/bot.md,
/// devices/sensors/motion-sensor.md, devices/sensors/contact-sensor.md,
/// devices/plugs-switches/plug-mini-jp.md, devices/others/virtual-infrared-remote-devices.md).
///
/// Every envelope is `{ "statusCode": int, "message": string, "body": {...} }`.
/// statusCode 100 means success; anything else (including missing/invalid JSON) is
/// treated as a failure and never throws out of this provider.
///
/// Also implements <see cref="IDeviceStatusSnapshotProvider"/>: <see cref="GetStatusSnapshotAsync"/>
/// fetches the status envelope exactly once per call and derives both the on/off
/// state projection and (for Plug Mini devices) the voltage/current/energy
/// telemetry from that single parsed response, so callers that need both (see
/// MimamoriTai.Core.Application.SwitchBotPollingCycleService) never issue two live
/// GET .../status requests for the same device in the same poll. <see cref="GetStatusAsync"/>
/// and <see cref="GetPlugMiniReadingAsync"/> remain available as independent,
/// single-purpose calls for callers that only need one half (e.g. ToggleAsync,
/// device-detail/dashboard reads) -- each of those still does its own single fetch.
/// </summary>
public sealed class SwitchBotDeviceProvider(
    ISwitchBotClient client,
    ILogger<SwitchBotDeviceProvider> logger) : IDeviceProvider, ISwitchBotPlugMiniReader, IDeviceStatusSnapshotProvider
{
    private const int SuccessStatusCode = 100;
    private const string NoHubDeviceId = "000000000000";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DeviceProviderKind Kind => DeviceProviderKind.SwitchBot;

    public bool IsConfigured => client.IsConfigured;

    public async Task<IReadOnlyList<ProviderDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var envelope = await FetchAsync<DeviceListBody>(
            () => client.GetDeviceListRawAsync(ct), "device list");

        if (envelope is null)
        {
            return [];
        }

        var devices = new List<ProviderDevice>();

        foreach (var d in envelope.Body?.DeviceList ?? [])
        {
            if (string.IsNullOrWhiteSpace(d.DeviceId))
            {
                continue;
            }

            devices.Add(new ProviderDevice(
                d.DeviceId,
                d.DeviceName ?? d.DeviceId,
                MapPhysicalDeviceType(d.DeviceType),
                ResolveRoom(d.HubDeviceId, d.DeviceName)));
        }

        foreach (var r in envelope.Body?.InfraredRemoteList ?? [])
        {
            if (string.IsNullOrWhiteSpace(r.DeviceId))
            {
                continue;
            }

            devices.Add(new ProviderDevice(
                r.DeviceId,
                r.DeviceName ?? r.DeviceId,
                MapInfraredRemoteType(r.RemoteType),
                ResolveRoom(r.HubDeviceId, r.DeviceName)));
        }

        return devices;
    }

    public async Task<ProviderDeviceStatus?> GetStatusAsync(string externalDeviceId, CancellationToken ct = default)
    {
        var body = await FetchStatusBodyAsync(externalDeviceId, "device status", ct);
        return body is null ? null : BuildStatus(externalDeviceId, body);
    }

    /// <summary>
    /// Reads the raw voltage/current/daily-energy/usage-minutes fields from a Plug
    /// Mini's status response. Returns null (never throws) when the device is not a
    /// Plug Mini variant (no such fields present) or the request/parse fails --
    /// mirrors GetStatusAsync's "never throw out of this provider" contract.
    /// </summary>
    public async Task<PlugMiniPowerReading?> GetPlugMiniReadingAsync(string externalDeviceId, CancellationToken ct = default)
    {
        var body = await FetchStatusBodyAsync(externalDeviceId, "plug mini status", ct);
        return body is null ? null : BuildPlugMiniReading(externalDeviceId, body);
    }

    /// <summary>
    /// Combined status+Plug Mini projection from exactly ONE status request: fetches
    /// the raw envelope a single time and derives both <see cref="GetStatusAsync"/>'s
    /// and <see cref="GetPlugMiniReadingAsync"/>'s results from that one parsed body,
    /// so a caller polling every device once per cycle (see
    /// MimamoriTai.Core.Application.SwitchBotPollingCycleService) never doubles the
    /// live API call count. Non-Plug-Mini devices simply get a null
    /// <see cref="DeviceStatusSnapshot.PlugMiniReading"/> back -- no extra request is
    /// ever made to find that out, it falls out of the fields already present (or
    /// absent) in the one response.
    /// </summary>
    public async Task<DeviceStatusSnapshot> GetStatusSnapshotAsync(string externalDeviceId, CancellationToken ct = default)
    {
        var body = await FetchStatusBodyAsync(externalDeviceId, "device status", ct);
        if (body is null)
        {
            return new DeviceStatusSnapshot(null, null);
        }

        return new DeviceStatusSnapshot(BuildStatus(externalDeviceId, body), BuildPlugMiniReading(externalDeviceId, body));
    }

    /// <summary>
    /// The one and only place this provider calls GET .../status. Virtual infrared
    /// remote devices have no status endpoint; SwitchBot returns an error for them,
    /// which FetchAsync turns into a null envelope/return here.
    /// </summary>
    private async Task<StatusBody?> FetchStatusBodyAsync(string externalDeviceId, string what, CancellationToken ct)
    {
        var envelope = await FetchAsync<StatusBody>(
            () => client.GetDeviceStatusRawAsync(externalDeviceId, ct), what);
        return envelope?.Body;
    }

    private static ProviderDeviceStatus BuildStatus(string externalDeviceId, StatusBody body)
    {
        var (state, powerWatts) = ResolveState(body);
        return new ProviderDeviceStatus(externalDeviceId, state, powerWatts, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Plug (non-mini) and Bot devices report "power" but never voltage/current, so
    /// this naturally returns null for them (all telemetry fields are absent) --
    /// i.e. no Plug-specific work or request is triggered for non-Plug-Mini devices.
    /// </summary>
    private static PlugMiniPowerReading? BuildPlugMiniReading(string externalDeviceId, StatusBody body)
    {
        if (body.Voltage is null && body.ElectricCurrent is null && body.Weight is null && body.ElectricityOfDay is null)
        {
            return null;
        }

        return new PlugMiniPowerReading(
            externalDeviceId,
            body.Voltage,
            body.ElectricCurrent,
            body.Weight,
            body.ElectricityOfDay,
            DateTimeOffset.UtcNow);
    }

    public Task<ProviderResult> TurnOnAsync(string externalDeviceId, CancellationToken ct = default) =>
        SendCommandAsync(externalDeviceId, "turnOn", ct);

    public Task<ProviderResult> TurnOffAsync(string externalDeviceId, CancellationToken ct = default) =>
        SendCommandAsync(externalDeviceId, "turnOff", ct);

    /// <summary>
    /// Not every SwitchBot device type supports a native "toggle" command (e.g. Bot
    /// only supports turnOn/turnOff/press), so toggling is implemented by reading the
    /// current state first and then sending the opposite explicit command.
    /// </summary>
    public async Task<ProviderResult> ToggleAsync(string externalDeviceId, CancellationToken ct = default)
    {
        var status = await GetStatusAsync(externalDeviceId, ct);
        if (status is null)
        {
            return ProviderResult.Fail("現在の状態を取得できなかったため、切り替えできませんでした。");
        }

        return status.IsOn
            ? await TurnOffAsync(externalDeviceId, ct)
            : await TurnOnAsync(externalDeviceId, ct);
    }

    private async Task<ProviderResult> SendCommandAsync(string externalDeviceId, string command, CancellationToken ct)
    {
        var envelope = await FetchAsync<JsonElement>(
            () => client.SendCommandRawAsync(externalDeviceId, command, "default", "command", ct),
            $"'{command}' command");

        if (envelope is null)
        {
            return ProviderResult.Fail("SwitchBot機器の操作に失敗しました。");
        }

        return ProviderResult.Ok();
    }

    /// <summary>
    /// Calls the transport, parses the envelope, and validates statusCode == 100.
    /// Never throws: transport failures, malformed JSON and non-success status codes
    /// all become a logged warning and a null return. The response body is
    /// deliberately never logged, since it can contain device identifiers.
    /// </summary>
    private async Task<Envelope<TBody>?> FetchAsync<TBody>(Func<Task<string>> call, string what)
    {
        string raw;
        try
        {
            raw = await call();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SwitchBot {What} request failed.", what);
            return null;
        }

        Envelope<TBody>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope<TBody>>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "SwitchBot {What} response could not be parsed.", what);
            return null;
        }

        if (envelope is null)
        {
            logger.LogWarning("SwitchBot {What} response was empty or not a JSON object.", what);
            return null;
        }

        if (envelope.StatusCode != SuccessStatusCode)
        {
            logger.LogWarning(
                "SwitchBot {What} request failed with statusCode {StatusCode}.", what, envelope.StatusCode);
            return null;
        }

        return envelope;
    }

    /// <summary>
    /// Physical device deviceType string -> DeviceType. Anything not explicitly
    /// recognized (hubs, curtains, meters, locks, robot vacuums, cameras, etc.) falls
    /// back to Unknown, which DeviceSafetyPolicy classifies as Restricted -- the
    /// safe, deny-by-default choice for devices this app has no specific handling for.
    /// </summary>
    private static DeviceType MapPhysicalDeviceType(string? deviceType) => deviceType switch
    {
        "Light" or "Color Bulb" or "Strip Light" or "Strip Light 3" or "Ceiling Light" or "Ceiling Light Pro"
            or "Floor Lamp" or "RGBICWW Strip Light" or "RGBICWW Floor Lamp" or "RGBICWW Ceiling Light"
            or "RGBIC Neon Wire Rope Light" or "RGBIC Neon Rope Light" or "Candle Warmer Lamp"
            or "Permanent Outdoor Lights" => DeviceType.Light,

        "Fan" or "Smart Fan" or "Battery Circulator Fan" or "Battery Circulator Fan 2 Pro"
            or "Circulator Fan" or "Standing Circulator Fan" => DeviceType.Fan,

        "Bot" or "Plug" or "Plug Mini (US)" or "Plug Mini (JP)" or "Plug Mini (EU)"
            or "Relay Switch 1" or "Relay Switch 1PM" or "Relay Switch 2PM" or "S20" => DeviceType.Plug,

        "Motion Sensor" or "Presence Sensor" => DeviceType.MotionSensor,

        "Contact Sensor" => DeviceType.ContactSensor,

        _ => DeviceType.Unknown
    };

    /// <summary>
    /// Virtual infrared remote remoteType string -> DeviceType. An IR-controlled
    /// Air Conditioner is treated as a Heater-class appliance (heater-like device
    /// behind a hub remote). Anything else unrecognized falls back to Unknown/Restricted.
    /// </summary>
    private static DeviceType MapInfraredRemoteType(string? remoteType) => remoteType switch
    {
        "Light" => DeviceType.Light,
        "Fan" => DeviceType.Fan,
        "Air Conditioner" => DeviceType.Heater,
        _ => DeviceType.Unknown
    };

    /// <summary>
    /// SwitchBot has no "room" concept in the device list response. Group by the
    /// parent Hub's device id when the device is behind a hub (hubDeviceId is not the
    /// "no hub" sentinel), otherwise fall back to the device's own name.
    /// </summary>
    private static string ResolveRoom(string? hubDeviceId, string? name)
    {
        if (!string.IsNullOrWhiteSpace(hubDeviceId) && !string.Equals(hubDeviceId, NoHubDeviceId, StringComparison.Ordinal))
        {
            return $"Hub {hubDeviceId}";
        }

        return string.IsNullOrWhiteSpace(name) ? "不明" : name;
    }

    /// <summary>
    /// Device-type-specific status bodies vary; this extracts an on/off state and,
    /// when available, a power reading in a best-effort, device-agnostic way.
    /// </summary>
    private static (string State, double? PowerWatts) ResolveState(StatusBody body)
    {
        if (!string.IsNullOrWhiteSpace(body.Power))
        {
            // A Plug Mini reports the relay state and the wattage flowing through it in the
            // same body. Keeping the wattage lets callers tell "switched on and actually
            // drawing power" apart from "switched on but nothing is running" - the family
            // reads 使用中 as a sign of life, so a relay left on at 0 W must not look the
            // same. Devices that report no wattage simply pass null through as before.
            return (string.Equals(body.Power, "on", StringComparison.OrdinalIgnoreCase) ? "on" : "off", body.Weight);
        }

        if (body.MoveDetected is { } moved)
        {
            // Motion Sensor: treat "motion detected" as activity (on).
            return (moved ? "on" : "off", null);
        }

        if (!string.IsNullOrWhiteSpace(body.OpenState))
        {
            // Contact Sensor: treat "open" as activity (on); "close"/"timeOutNotClose" as off.
            return (string.Equals(body.OpenState, "open", StringComparison.OrdinalIgnoreCase) ? "on" : "off", body.Weight);
        }

        if (body.ElectricCurrent is { } current)
        {
            // Plug Mini variants report live current draw but no explicit "power" field;
            // a non-zero current implies the outlet is switched on and drawing load.
            return (current > 0 ? "on" : "off", body.Weight);
        }

        // Unrecognized/telemetry-only body (e.g. Meter humidity/temperature): no
        // on/off concept, so report "off" (no activity signal) rather than guessing.
        return ("off", null);
    }

    private sealed class Envelope<TBody>
    {
        [JsonPropertyName("statusCode")]
        public int StatusCode { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("body")]
        public TBody? Body { get; set; }
    }

    private sealed class DeviceListBody
    {
        public List<PhysicalDeviceDto>? DeviceList { get; set; }
        public List<InfraredRemoteDto>? InfraredRemoteList { get; set; }
    }

    private sealed class PhysicalDeviceDto
    {
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public string? DeviceType { get; set; }
        public string? HubDeviceId { get; set; }
    }

    private sealed class InfraredRemoteDto
    {
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public string? RemoteType { get; set; }
        public string? HubDeviceId { get; set; }
    }

    private sealed class StatusBody
    {
        public string? DeviceId { get; set; }
        public string? DeviceType { get; set; }

        /// <summary>ON/OFF state, present on Bot, Plug, Plug (non-mini), etc.</summary>
        public string? Power { get; set; }

        /// <summary>Motion Sensor / Presence Sensor.</summary>
        public bool? MoveDetected { get; set; }

        /// <summary>Contact Sensor: "open" | "close" | "timeOutNotClose".</summary>
        public string? OpenState { get; set; }

        /// <summary>Plug Mini variants: live current draw in mA.</summary>
        public double? ElectricCurrent { get; set; }

        /// <summary>
        /// Plug Mini variants: SwitchBot's official field name for this is "weight",
        /// despite the name -- per the public SwitchBotAPI docs
        /// (devices/plugs-switches/plug-mini-jp.md) it is the day's accumulated energy
        /// consumption. The API reference does not state the unit unambiguously
        /// (observed values are consistent with Wh, not kWh, for typical household
        /// loads); this project stores it as-is in
        /// <see cref="Domain.PlugMiniReading.DailyEnergyWh"/> assuming Wh and documents
        /// this assumption in docs/FABRIC_SETUP.md as an open ambiguity rather than
        /// silently guessing a scale factor.
        /// </summary>
        public double? Weight { get; set; }

        /// <summary>Plug Mini variants: live instantaneous voltage in V.</summary>
        public double? Voltage { get; set; }

        /// <summary>Plug Mini variants: minutes the outlet has been switched on so far today.</summary>
        public int? ElectricityOfDay { get; set; }
    }
}
