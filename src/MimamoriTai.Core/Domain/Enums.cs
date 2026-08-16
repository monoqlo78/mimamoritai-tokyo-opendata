namespace MimamoriTai.Core.Domain;

public enum PersonRole
{
    Resident = 0,
    Family = 1,
    Admin = 2
}

public enum DeviceType
{
    Unknown = 0,
    Light = 1,
    Fan = 2,
    Plug = 3,
    MotionSensor = 4,
    ContactSensor = 5,
    Heater = 6,
    Kettle = 7,
    Microwave = 8,
    CookingDevice = 9,
    DemoDevice = 10,
    /// <summary>
    /// Cooling appliance. Broken out from <see cref="Plug"/> because the heatstroke rule
    /// has to know which socket is the one that is supposed to be running on a hot day.
    /// </summary>
    AirConditioner = 11
}

/// <summary>
/// Safety classification used by the natural language control guard rails. It decides
/// what happens when something asks to switch a device <em>on</em>; switching off is
/// never gated by it, because turning an appliance off cannot start a fire.
/// </summary>
public enum SafetyClass
{
    /// <summary>Turning on is harmless. A lamp or a fan.</summary>
    Safe = 0,

    /// <summary>
    /// Turning on remotely is refused outright. This is what the owner selects when they
    /// want the appliance to be off-only, and it is also where anything we cannot
    /// classify lands.
    /// </summary>
    Restricted = 1,

    /// <summary>
    /// Turning on is permitted, but only after whoever asked has confirmed the area
    /// around the appliance is clear, and every success is announced to the whole
    /// family.
    ///
    /// <para>
    /// This exists because refusing outright was the wrong answer for the person we are
    /// actually building for. A relative with early dementia who cannot work a heater on
    /// a cold night is in more danger from the cold than from the heater, and a family
    /// that cannot help remotely is left with no option but to drive over. The risk is
    /// real, so it is answered with a hazard check and an audit trail the whole family
    /// sees - not by pretending the need does not exist.
    /// </para>
    /// </summary>
    Guarded = 2
}

public enum DeviceProviderKind
{
    Mock = 0,
    SwitchBot = 1
}

public enum CommandSource
{
    Web = 0,
    Line = 1,
    System = 2
}

public enum DeviceAction
{
    TurnOn = 0,
    TurnOff = 1,
    Toggle = 2,
    GetStatus = 3
}

public enum CommandStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Rejected = 3
}

public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2
}

public enum MessageType
{
    Text = 0,
    AiReply = 1,
    Notice = 2
}

public enum AssistantIntent
{
    Conversation = 0,
    ControlDevice = 1,
    DeviceStatus = 2,
    QueryData = 3
}

public enum EventSource
{
    Mock = 0,
    SwitchBotWebhook = 1,
    SwitchBotPoll = 2,
    AppCommand = 3,
    Simulator = 4,
    Seed = 5
}

/// <summary>
/// Whether a household's data is the shared demo dataset or a real user's
/// production data. Drives both device-provider selection and per-user access
/// control (Sample households are visible to everyone; Production households are
/// only visible to their <see cref="HouseholdMember"/>s).
/// </summary>
public enum DataSourceMode
{
    Sample = 0,
    Production = 1
}

/// <summary>Role of an <see cref="AppUser"/> within a <see cref="Household"/>.</summary>
public enum HouseholdMemberRole
{
    Owner = 0,
    Member = 1,
    Viewer = 2
}

/// <summary>
/// Connection status of a household's per-household SwitchBot credentials
/// (<see cref="SwitchBotConnection"/>). Drives the Settings UI badge and whether the
/// household-scoped polling loop attempts to poll this household at all.
/// </summary>
public enum SwitchBotConnectionStatus
{
    /// <summary>No Token/Secret has been saved for this household yet.</summary>
    NotConfigured = 0,

    /// <summary>Credentials were saved and the most recent validation/sync succeeded.</summary>
    Connected = 1,

    /// <summary>Credentials were saved but the most recent validation/sync failed (e.g. revoked token).</summary>
    Error = 2
}
