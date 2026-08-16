using System.Collections.Concurrent;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Infrastructure.Devices;

/// <summary>
/// DEMO ONLY. In-memory smart-home backend used until the physical SwitchBot
/// devices arrive. It holds no credentials and talks to no external service.
/// </summary>
public sealed class MockDeviceProvider : IDeviceProvider
{
    public const string DemoPrefix = "demo-";

    /// <summary>Fixed demo devices. The `demo-` prefix makes it obvious in the DB that this is not real hardware.</summary>
    /// <remarks>
    /// The heater is intentionally included: it classifies as <see cref="DeviceSafetyClass.Restricted"/>,
    /// which lets the safety guard-rail (refuse ON, allow OFF) be demonstrated end to end.
    /// The air conditioner is here for the other half of that story: on a hot day it is
    /// the appliance the heat rule expects to see running, so a demo without one can only
    /// ever show the warning, never the suggestion that answers it.
    /// </remarks>
    public static readonly IReadOnlyList<ProviderDevice> SeedDevices =
    [
        new($"{DemoPrefix}living-light", "リビング照明", DeviceType.Light, "リビング"),
        new($"{DemoPrefix}bedroom-light", "寝室照明", DeviceType.Light, "寝室"),
        new($"{DemoPrefix}living-ac", "エアコン", DeviceType.AirConditioner, "リビング"),
        new($"{DemoPrefix}living-fan", "扇風機", DeviceType.Fan, "リビング"),
        new($"{DemoPrefix}living-heater", "電気ストーブ", DeviceType.Heater, "リビング")
    ];

    public static readonly IReadOnlyDictionary<string, string> SeedAliases = new Dictionary<string, string>
    {
        [$"{DemoPrefix}living-light"] = "living-light",
        [$"{DemoPrefix}bedroom-light"] = "bedroom-light",
        [$"{DemoPrefix}living-ac"] = "living-ac",
        [$"{DemoPrefix}living-fan"] = "living-fan",
        [$"{DemoPrefix}living-heater"] = "living-heater"
    };

    private readonly ConcurrentDictionary<string, bool> _state = new();
    private readonly TimeProvider _clock;

    public MockDeviceProvider(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        foreach (var device in SeedDevices)
        {
            _state[device.ExternalDeviceId] = false;
        }
    }

    public DeviceProviderKind Kind => DeviceProviderKind.Mock;

    public bool IsConfigured => true;

    public Task<IReadOnlyList<ProviderDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult(SeedDevices);

    public Task<ProviderDeviceStatus?> GetStatusAsync(string externalDeviceId, CancellationToken ct = default)
    {
        if (!_state.TryGetValue(externalDeviceId, out var isOn))
        {
            return Task.FromResult<ProviderDeviceStatus?>(null);
        }

        var status = new ProviderDeviceStatus(
            externalDeviceId,
            isOn ? "on" : "off",
            isOn ? 32.0 : 0.0,
            _clock.GetUtcNow());

        return Task.FromResult<ProviderDeviceStatus?>(status);
    }

    public Task<ProviderResult> TurnOnAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(SetState(externalDeviceId, true));

    public Task<ProviderResult> TurnOffAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(SetState(externalDeviceId, false));

    public Task<ProviderResult> ToggleAsync(string externalDeviceId, CancellationToken ct = default)
    {
        if (!_state.TryGetValue(externalDeviceId, out var current))
        {
            return Task.FromResult(ProviderResult.Fail("未登録の機器です。"));
        }

        return Task.FromResult(SetState(externalDeviceId, !current));
    }

    private ProviderResult SetState(string externalDeviceId, bool isOn)
    {
        if (!_state.ContainsKey(externalDeviceId))
        {
            return ProviderResult.Fail("未登録の機器です。");
        }

        _state[externalDeviceId] = isOn;
        return ProviderResult.Ok();
    }
}
