using System.ComponentModel.DataAnnotations.Schema;

namespace MimamoriTai.Core.Domain;


public class Household
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether this household is the shared demo dataset (visible to every user) or
    /// a real user's production data (visible only to its <see cref="HouseholdMember"/>s).
    /// </summary>
    public DataSourceMode DataSourceMode { get; set; } = DataSourceMode.Sample;

    public List<Person> People { get; set; } = [];
    public List<Device> Devices { get; set; } = [];
}

public class Person
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public PersonRole Role { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Household? Household { get; set; }
}

public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }

    /// <summary>Identifier used by the upstream provider (SwitchBot deviceId, or a mock id).</summary>
    public string ExternalDeviceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Stable, human friendly key used to resolve natural language references.</summary>
    public string Alias { get; set; } = string.Empty;

    public DeviceType DeviceType { get; set; }
    public string Room { get; set; } = string.Empty;

    /// <summary>
    /// Name the resident's family typed on screen, or null while they have never renamed
    /// this device. Kept separate from <see cref="Name"/> because <see cref="Name"/> is the
    /// provider's own label: SwitchBot re-reports it on every sync, so storing the human
    /// correction there would be silently reverted by the next poll. The raw label is also
    /// what an operator needs in order to recognise the device in the SwitchBot app, so it
    /// is never overwritten or discarded.
    /// </summary>
    public string? DisplayNameOverride { get; set; }

    /// <summary>
    /// Room the family typed on screen, or null while they have never set one. SwitchBot has
    /// no concept of a room, so <see cref="Room"/> only ever holds a synthesised placeholder
    /// (the hub id, or the device's own name) - this is the only field that can hold a room
    /// a human would recognise.
    /// </summary>
    public string? RoomOverride { get; set; }

    /// <summary>What the family should see everywhere: their own name if they set one.</summary>
    [NotMapped]
    public string DisplayName =>
        string.IsNullOrWhiteSpace(DisplayNameOverride) ? Name : DisplayNameOverride;

    /// <summary>What the family should see everywhere: their own room if they set one.</summary>
    [NotMapped]
    public string DisplayRoom =>
        string.IsNullOrWhiteSpace(RoomOverride) ? Room : RoomOverride;

    public DeviceProviderKind Provider { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool RemoteControlAllowed { get; set; }
    public SafetyClass SafetyClass { get; set; }

    /// <summary>
    /// False when this device was previously synced from a provider (e.g. SwitchBot)
    /// but no longer appears there. Deactivated devices are kept (never deleted) so
    /// their historical DeviceEvent/DeviceCommand rows remain valid, but they are
    /// excluded from the dashboard and from natural language resolution.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Hours of unchanging draw after which the family wants to be told, or null while
    /// they have not asked to be told about this appliance.
    ///
    /// Opt-in on purpose. A fridge or a router draws the same watts forever and would
    /// raise an alert every single day; the appliances worth watching are the ones
    /// whose stillness is unusual -- the kettle normally used by mid-morning, the
    /// heater that normally comes on in the evening.
    /// </summary>
    public int? FlatPowerAlertHours { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Household? Household { get; set; }
}

public class DeviceEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid DeviceId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public double? PowerWatts { get; set; }
    public double? NumericValue { get; set; }
    public string? Unit { get; set; }
    public EventSource Source { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? RawPayloadJson { get; set; }

    /// <summary>
    /// When this row was last streamed to the Fabric Eventhouse, or null if it has
    /// never been published. Drives the incremental publish background service:
    /// only null rows are candidates, and stamping this on success (never on
    /// failure) makes republishing idempotent and safely retryable.
    /// </summary>
    public DateTimeOffset? PublishedToStreamAtUtc { get; set; }

    public Device? Device { get; set; }
}

public class DeviceCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid? RequestedByPersonId { get; set; }
    public CommandSource Source { get; set; }
    public string OriginalText { get; set; } = string.Empty;
    public DeviceAction Action { get; set; }
    public CommandStatus Status { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExecutedAtUtc { get; set; }
    public string? AiResolvedModel { get; set; }

    public Device? Device { get; set; }
}

public class FamilyMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid? PersonId { get; set; }
    public CommandSource Source { get; set; }
    public MessageType MessageType { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Person? Person { get; set; }
}

public class RiskAssessment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid PersonId { get; set; }
    public RiskLevel RiskLevel { get; set; }
    public int Score { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class DailyActivitySummary
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid PersonId { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly? FirstActivityTime { get; set; }
    public TimeOnly? LastActivityTime { get; set; }
    public int DeviceUsageCount { get; set; }
    public int ActiveMinutes { get; set; }
    public int NightActivityCount { get; set; }
    public int RiskScore { get; set; }
    public RiskLevel RiskLevel { get; set; }
}

/// <summary>
/// Records a LINE push notification sent (or attempted) because a watch/risk anomaly
/// was detected. Used to deduplicate repeat alerts for the same person + risk level
/// within a cooldown window.
/// </summary>
public class WatchAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid PersonId { get; set; }
    public RiskLevel RiskLevel { get; set; }
    public int Score { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset SentAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class AiRequestLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? HouseholdId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string Router { get; set; } = string.Empty;
    public string ResolvedModel { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public bool Success { get; set; }

    /// <summary>
    /// Why the call failed, null when it succeeded.
    /// </summary>
    /// <remarks>
    /// Recording only <see cref="Success"/> made failures unexplainable after the
    /// fact: a call logged as failed with <see cref="ResolvedModel"/> still "auto"
    /// says a model never answered but not why, and the app log that did say why
    /// had already rotated away by the time anyone asked. This is the router's
    /// short reason ("OrcaRouter returned 401.", an exception type name), never a
    /// response body, which can echo the prompt.
    /// </remarks>
    public string? Error { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A signed-in (or, today, the single dev/demo) user. Identity is keyed by
/// (<see cref="IdentityProvider"/>, <see cref="ExternalSubject"/>) so a later switch to
/// real Entra External ID / LINE OIDC authentication only needs to upsert this row,
/// never change its shape. <see cref="ICurrentUserAccessor"/> is the only coupling
/// point between the auth layer and the rest of the app.
/// </summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>e.g. "dev", "entra-external", "line".</summary>
    public string IdentityProvider { get; set; } = string.Empty;

    /// <summary>The stable subject/oid claim from the IdP.</summary>
    public string ExternalSubject { get; set; } = string.Empty;

    /// <summary>LINE `sub` / userId when known.</summary>
    public string? LineUserId { get; set; }

    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastLoginAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Membership of an <see cref="AppUser"/> in a <see cref="Household"/>, with a role.</summary>
public class HouseholdMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid AppUserId { get; set; }
    public HouseholdMemberRole Role { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Household? Household { get; set; }
    public AppUser? AppUser { get; set; }
}

/// <summary>
/// A LINE user (or group) that has added the bot as a friend / sent it a message, captured
/// via the LINE webhook's `follow`/`message` events. Alert pushes target every active
/// recipient of a household instead of a single hard-coded id, so onboarding a new family
/// member is just "add the bot as a friend" with no manual copy/paste of LINE user ids.
/// </summary>
public class LineRecipient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }

    /// <summary>LINE `userId` (1:1 chat) or `groupId` (group chat) — both are valid `to` values for push.</summary>
    public string LineUserId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>False once the corresponding `unfollow` webhook event is received.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public Household? Household { get; set; }
}

/// <summary>
/// Per-household SwitchBot API credentials. Exactly one row per household (unique
/// index on <see cref="HouseholdId"/>). <see cref="EncryptedToken"/> and
/// <see cref="EncryptedSecret"/> are opaque blobs produced by
/// <see cref="MimamoriTai.Core.Abstractions.ICredentialProtector"/> (ASP.NET Core Data
/// Protection) -- the plaintext Token/Secret is never stored, logged or returned to
/// the UI once saved. Only <see cref="Status"/>, <see cref="LastValidatedAtUtc"/>,
/// <see cref="LastSyncAtUtc"/> and <see cref="LastErrorMessage"/> are safe to surface.
/// </summary>
public class SwitchBotConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }

    /// <summary>Data-Protection-protected SwitchBot open token. Never plaintext.</summary>
    public string EncryptedToken { get; set; } = string.Empty;

    /// <summary>Data-Protection-protected SwitchBot secret (HMAC signing key). Never plaintext.</summary>
    public string EncryptedSecret { get; set; } = string.Empty;

    public SwitchBotConnectionStatus Status { get; set; } = SwitchBotConnectionStatus.NotConfigured;

    public DateTimeOffset? LastValidatedAtUtc { get; set; }
    public DateTimeOffset? LastSyncAtUtc { get; set; }

    /// <summary>Human-readable failure summary only. Must never include the token/secret value.</summary>
    public string? LastErrorMessage { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Household? Household { get; set; }
}

/// <summary>
/// One polled SwitchBot Plug Mini reading, recorded on every poll cycle (unlike
/// <see cref="DeviceEvent"/>, which is only written on an observed state change) so
/// voltage/current/energy usage can be graphed as a time series in Fabric.
///
/// Field semantics, per the SwitchBot OpenAPI v1.1 Plug Mini (JP) status response:
///   - <see cref="VoltageV"/> &lt;- `voltage`: instantaneous line voltage, Volts.
///   - <see cref="CurrentMa"/> &lt;- `electricCurrent`: instantaneous current draw, mA.
///   - <see cref="DailyEnergyWh"/> &lt;- `weight`: SwitchBot documents this as "the
///     power consumed in a day, measured in Watts", which cannot be right for a daily
///     total. Checked against a live plug it is instantaneous real power in Watts:
///     a plug drawing 314mA at 104V (32.7VA apparent) reported 0.3 here, which is the
///     real power a near-idle load actually draws. Stored raw for fidelity, but the
///     "Wh" in the property name is a misnomer kept only to avoid a migration, and it
///     must never be read as an energy figure -- <c>PowerUsageService</c> integrates
///     these watts over time to get energy. The existing
///     <c>SwitchBotDeviceProvider.ResolveState</c> mapping of the same field to
///     <c>ProviderDeviceStatus.PowerWatts</c> is correct and unchanged.
///   - <see cref="UsageMinutesToday"/> &lt;- `electricityOfDay`: minutes the outlet has
///     been switched on today; reset by the device at local midnight.
///   - <see cref="ApproxWatts"/>: voltage * current / 1000, i.e. apparent power (VA).
///     Kept for the record but never charted or integrated: on the reading above it
///     overstates real power by two orders of magnitude.
/// </summary>
public class PlugMiniReading
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid DeviceId { get; set; }

    public double? VoltageV { get; set; }
    public double? CurrentMa { get; set; }
    public double? DailyEnergyWh { get; set; }
    public int? UsageMinutesToday { get; set; }

    /// <summary>Approximation only: voltage * current / 1000W, assumes power factor 1.</summary>
    public double? ApproxWatts { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Stamped only on a successful Fabric Eventhouse publish, mirroring
    /// <see cref="DeviceEvent.PublishedToStreamAtUtc"/>: null rows are retried, never
    /// silently dropped.
    /// </summary>
    public DateTimeOffset? PublishedToStreamAtUtc { get; set; }

    public Device? Device { get; set; }
}

/// <summary>
/// One observation of the outdoor heat index for the watched area, captured from
/// public open data (環境省 WBGT + 気象庁 AMeDAS). Stored per observation time rather
/// than per household because the figure is city-wide: every household watching the
/// same area shares the same row.
///
/// Persisting it at all is deliberate. The provider only ever knows "now", but the
/// question a family actually asks is "was it this hot yesterday too, and was the
/// air conditioner running then?" -- which needs history sitting next to the plug
/// readings, in the app database and in the Fabric Eventhouse.
/// </summary>
public class HeatReading
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Observation point, e.g. 44132 (東京).</summary>
    public string PointCode { get; set; } = string.Empty;

    public string AreaName { get; set; } = string.Empty;

    /// <summary>暑さ指数 (WBGT) in degrees Celsius.</summary>
    public double Wbgt { get; set; }

    /// <summary>The 環境省 five-band classification of <see cref="Wbgt"/>, stored as its int value.</summary>
    public int Level { get; set; }

    public double? TemperatureC { get; set; }
    public double? HumidityPercent { get; set; }

    /// <summary>When the source data says the observation applies to, not when we fetched it.</summary>
    public DateTimeOffset ObservedAtUtc { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Stamped only on a successful Fabric Eventhouse publish, mirroring
    /// <see cref="PlugMiniReading.PublishedToStreamAtUtc"/>: null rows are retried,
    /// never silently dropped.
    /// </summary>
    public DateTimeOffset? PublishedToStreamAtUtc { get; set; }
}

/// <summary>
/// A short-lived, single-use pairing code that lets a signed-in household owner link
/// a LINE Messaging API source (userId/groupId) to their household by sending
/// "連携 123456" to the bot, without ever displaying or storing the LINE userId in
/// the Settings UI, and without the webhook ever guessing which household an
/// unrecognized LINE source belongs to (see WebhookEndpoints and
/// docs/LINE_SETUP.md for the full flow this replaces).
///
/// The plaintext 6-digit code is shown to the household owner exactly once (at
/// generation time) and is never persisted: only <see cref="CodeHash"/> (a keyed
/// hash, see LineLinkCodeService) is stored, so a leaked database row cannot be
/// replayed to redeem the code. Only one code may be active (unused and unexpired)
/// per household at a time -- generating a new one invalidates any prior one.
/// </summary>
public class LineLinkCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }

    /// <summary>Keyed hash of the plaintext code. Never the plaintext code itself.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>Set once this code is successfully redeemed; makes redemption single-use.</summary>
    public DateTimeOffset? UsedAtUtc { get; set; }

    /// <summary>
    /// Failed-redemption attempts observed while this code was still active. Every
    /// currently-active code's counter is incremented on any failed redemption
    /// attempt system-wide (not only attempts targeting this exact code), and a code
    /// is force-expired once its counter reaches the configured limit. This is a
    /// deliberately simple, no-new-infrastructure brute-force guard: see
    /// LineLinkCodeService for the exact policy and rationale.
    /// </summary>
    public int AttemptCount { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Household? Household { get; set; }
}
