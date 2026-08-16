using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>What may happen to a device, once every condition has been weighed together.</summary>
public enum SafetyDecision
{
    /// <summary>Carry it out now.</summary>
    Allow = 0,

    /// <summary>
    /// Carry it out only once whoever asked has confirmed the area around the appliance
    /// is clear. Never treat this as a refusal: the answer is "yes, after you check".
    /// </summary>
    ConfirmHazard = 1,

    /// <summary>Refuse, and say why.</summary>
    Deny = 2
}

/// <summary>
/// The safety layer's answer, carrying every condition it weighed rather than a bare
/// string.
///
/// <para>
/// It used to return <c>string?</c>, where null meant yes and anything else meant no.
/// That shape cannot express "yes, once you have checked around the heater", so a
/// dangerous appliance could only ever be refused. Callers now branch on
/// <see cref="Decision"/> and can compose the outcome with the other conditions -
/// whether consent was already given, who has to be told afterwards - instead of
/// pattern-matching Japanese prose.
/// </para>
/// </summary>
/// <param name="Decision">What the caller must do.</param>
/// <param name="Reason">Why, in Japanese, ready to show the family. Null when allowed outright.</param>
/// <param name="HazardChecks">
/// What to ask the person before acting. Only populated for
/// <see cref="SafetyDecision.ConfirmHazard"/>.
/// </param>
public sealed record SafetyVerdict(
    SafetyDecision Decision,
    string? Reason = null,
    IReadOnlyList<string>? HazardChecks = null)
{
    public static readonly SafetyVerdict Allowed = new(SafetyDecision.Allow);

    public static SafetyVerdict Denied(string reason) => new(SafetyDecision.Deny, reason);

    public bool IsAllowed => Decision == SafetyDecision.Allow;

    public bool NeedsHazardCheck => Decision == SafetyDecision.ConfirmHazard;
}

/// <summary>
/// Central policy for "may an AI-resolved intent touch this device?".
/// Kept free of I/O so it is trivially unit testable.
/// </summary>
public static class DeviceSafetyPolicy
{
    /// <summary>
    /// The default classification for a device type.
    ///
    /// <para>
    /// Appliances that heat are <see cref="SafetyClass.Guarded"/>, not
    /// <see cref="SafetyClass.Restricted"/>: switching them on remotely is a real need on
    /// a cold day, so it is allowed behind a hazard check instead of being refused. A
    /// plug is classified the same way because we do not know what is plugged into it and
    /// have to assume the worst. Sensors and anything unrecognised stay Restricted -
    /// there is nothing to switch on, so there is nothing to gain by relaxing them.
    /// </para>
    /// </summary>
    public static SafetyClass Classify(DeviceType type) => type switch
    {
        DeviceType.Light or DeviceType.Fan or DeviceType.DemoDevice => SafetyClass.Safe,
        DeviceType.Plug
            or DeviceType.AirConditioner
            or DeviceType.Heater
            or DeviceType.Kettle
            or DeviceType.Microwave
            or DeviceType.CookingDevice => SafetyClass.Guarded,
        _ => SafetyClass.Restricted
    };

    public static readonly DeviceAction[] AllowedAiActions =
    [
        DeviceAction.TurnOn,
        DeviceAction.TurnOff,
        DeviceAction.Toggle,
        DeviceAction.GetStatus
    ];

    /// <summary>
    /// Ceiling on state-changing commands the assistant may execute for one household
    /// inside <see cref="RateLimitWindow"/>. A model that misreads a conversation (or a
    /// prompt-injected one) must not be able to cycle the lights indefinitely; reads
    /// (GetStatus) are exempt because they cannot affect the home.
    /// </summary>
    public const int MaxStateChangesPerWindow = 10;

    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Guard against the same command being repeated back-to-back, which is what a
    /// retry loop or a duplicated LINE webhook delivery looks like.
    /// </summary>
    public const int MaxIdenticalRepeats = 3;

    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(2);

    public static bool IsStateChanging(DeviceAction action) =>
        action is DeviceAction.TurnOn or DeviceAction.TurnOff or DeviceAction.Toggle;

    /// <summary>True when this action could leave the appliance running.</summary>
    public static bool CanEnergise(DeviceAction action) =>
        action is DeviceAction.TurnOn or DeviceAction.Toggle;

    /// <summary>
    /// Weighs every condition — the action, whether the device is enabled, how sure the
    /// model was, whether the owner permitted remote control at all, and how dangerous
    /// the appliance is — into one decision.
    /// </summary>
    public static SafetyVerdict Evaluate(Device device, DeviceAction action, double confidence)
    {
        if (!AllowedAiActions.Contains(action))
        {
            return SafetyVerdict.Denied("許可されていない操作です。");
        }

        if (!device.IsEnabled)
        {
            return SafetyVerdict.Denied($"{device.DisplayName} は現在無効になっています。");
        }

        if (action == DeviceAction.GetStatus)
        {
            return SafetyVerdict.Allowed;
        }

        if (confidence < IntentParser.MinimumConfidence)
        {
            return SafetyVerdict.Denied("指示を確実に理解できませんでした。もう一度、機器の名前を含めて教えてください。");
        }

        if (!device.RemoteControlAllowed)
        {
            return SafetyVerdict.Denied($"{device.DisplayName} は遠隔操作が許可されていません。");
        }

        // Turning something off is always allowed once remote control is permitted. It is
        // the action a worried family reaches for, and it cannot start a fire.
        if (!CanEnergise(action))
        {
            return SafetyVerdict.Allowed;
        }

        return device.SafetyClass switch
        {
            SafetyClass.Safe => SafetyVerdict.Allowed,

            SafetyClass.Guarded => new SafetyVerdict(
                SafetyDecision.ConfirmHazard,
                $"{device.DisplayName} は火や熱をあつかう機器です。周囲の安全を確認してから操作します。",
                HazardChecks(device.DeviceType)),

            // The owner ticked "遠隔でONにしない" for this device, so this is a decision a
            // human already made. Say so plainly, and point at the setting that changes it.
            _ => SafetyVerdict.Denied(
                $"{device.DisplayName} は遠隔でONにしない設定になっています。"
                + "設定画面で変更できます。（OFFにする操作はこのままでも行えます）")
        };
    }

    /// <summary>
    /// What to ask before energising this kind of appliance.
    ///
    /// <para>
    /// Deliberately concrete. "安全ですか?" invites a reflexive yes; "洗濯物やカーテンが
    /// 近くにありませんか?" makes the person picture the room, which is the only thing
    /// standing between a remote switch-on and a fire.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> HazardChecks(DeviceType type) => type switch
    {
        DeviceType.Heater =>
        [
            "洗濯物・新聞・カーテンなど、燃えやすいものが近くに置かれていませんか？",
            "本体が倒れていたり、荷物でふさがれていたりしませんか？",
            "ご本人が在宅で、暑くなったら自分で消せる状態ですか？"
        ],
        DeviceType.Kettle or DeviceType.CookingDevice =>
        [
            "中身は入っていますか？（空だきになりませんか）",
            "上や周りに、燃えやすいものが置かれていませんか？",
            "ご本人が在宅で、使い終わりに気づける状態ですか？"
        ],
        DeviceType.Microwave =>
        [
            "中に何も入っていない、または金属が入っていない状態ですか？",
            "ご本人が在宅で、動いていることに気づける状態ですか？"
        ],
        DeviceType.AirConditioner =>
        [
            "窓が開けっぱなしになっていませんか？（冷えないまま電気代だけがかかります）",
            "運転は冷房になっていますか？（暖房のままだと部屋がさらに暑くなります）",
            "ご本人が在宅で、寒く感じたら自分で止められる状態ですか？"
        ],
        _ =>
        [
            "このコンセントに今つながっているのは何か、把握できていますか？",
            "近くに燃えやすいものや、水がかかるものはありませんか？",
            "ご本人が在宅で、異常に気づける状態ですか？"
        ]
    };
}
