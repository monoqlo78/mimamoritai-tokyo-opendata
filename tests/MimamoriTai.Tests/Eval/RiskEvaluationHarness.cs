using System.Text.Json;
using System.Text.Json.Serialization;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests.Eval;

/// <summary>One day described the way a family would describe it, not the way the rules read it.</summary>
public sealed record DayShape(
    [property: JsonPropertyName("first")] string? First,
    [property: JsonPropertyName("last")] string? Last,
    [property: JsonPropertyName("uses")] int Uses,
    [property: JsonPropertyName("activeMinutes")] int ActiveMinutes,
    [property: JsonPropertyName("night")] int Night,
    [property: JsonPropertyName("energyWh")] double EnergyWh);

public sealed record LeftOnShape(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("hours")] double Hours);

public sealed record FlatShape(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("hours")] double Hours,
    [property: JsonPropertyName("thresholdHours")] int ThresholdHours);

/// <summary>An air conditioner or a heater, and whether it is actually running.</summary>
public sealed record ClimateShape(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isOn")] bool IsOn,
    [property: JsonPropertyName("watts")] double? Watts);

/// <summary>
/// One labelled day. <see cref="Kind"/> is the whole point of the set: "normal" days are
/// ones where a notification would be an intrusion, "incident" days are ones where silence
/// would be a failure. <see cref="Rationale"/> records why a family would say so, written
/// before the scenario was ever run.
/// </summary>
public sealed record RiskScenario(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("now")] string Now,
    [property: JsonPropertyName("rationale")] string Rationale,
    [property: JsonPropertyName("baseline")] DayShape Baseline,
    [property: JsonPropertyName("today")] DayShape Today,
    [property: JsonPropertyName("wbgt")] double? Wbgt,
    [property: JsonPropertyName("temperatureC")] double? TemperatureC,
    [property: JsonPropertyName("leftOn")] IReadOnlyList<LeftOnShape>? LeftOn,
    [property: JsonPropertyName("flatPower")] IReadOnlyList<FlatShape>? FlatPower,
    [property: JsonPropertyName("cooling")] IReadOnlyList<ClimateShape>? Cooling,
    [property: JsonPropertyName("heating")] IReadOnlyList<ClimateShape>? Heating);

/// <summary>The outcome of one scenario, in the terms the family experiences.</summary>
public sealed record RiskOutcome(RiskScenario Scenario, RiskResult Result, bool Notified)
{
    /// <summary>A quiet day that pushed a notification. This is the number that decides
    /// whether the product survives contact with a real household.</summary>
    public bool IsFalseAlarm => Scenario.Kind == "normal" && Notified;

    /// <summary>A day that needed someone to look, and nobody was told.</summary>
    public bool IsMiss => Scenario.Kind == "incident" && !Notified;
}

/// <summary>
/// Replays labelled days through the shipped risk rules.
///
/// A monitoring product is not judged on how much it detects. It is judged on whether the
/// family still reads the notifications after a month, and that is decided by how often it
/// interrupts them over nothing. <see cref="RiskAssessmentService.Evaluate"/> is a pure
/// function of its arguments, so every scenario here reproduces exactly and the false-alarm
/// rate can be a build gate rather than a claim on a slide.
///
/// The honest limits of this set are recorded in risk-scenarios.json and repeated in the
/// generated report: these are hand-written days, not labelled recordings of real
/// households, so they measure the rules against stated intent -- not against reality.
/// </summary>
public static class RiskEvaluationHarness
{
    /// <summary>Fixed so the report is reproducible; the rules read no calendar season.</summary>
    public static readonly DateOnly Today = new(2026, 8, 5);

    /// <summary>Days of history a household is assumed to have. Matches the demo window.</summary>
    public const int BaselineDays = 14;

    /// <summary>
    /// The level at which the product actually pushes to LINE, read from the shipped
    /// default rather than repeated here, so lowering the threshold moves this measurement
    /// instead of quietly invalidating it.
    /// </summary>
    public static readonly RiskLevel NotifyThreshold = new WatchAlertSettings().Threshold;

    private static readonly JsonSerializerOptions CaseJson = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<RiskScenario> LoadScenarios()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Eval", "risk-scenarios.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        var scenarios = document.RootElement.GetProperty("scenarios")
            .Deserialize<List<RiskScenario>>(CaseJson);

        return scenarios ?? throw new InvalidOperationException($"シナリオを読み込めませんでした: {path}");
    }

    public static RiskOutcome Run(RiskScenario scenario)
    {
        var result = RiskAssessmentService.Evaluate(
            today: ToActivity(scenario.Today, Today),
            baseline: Baseline(scenario.Baseline),
            nowLocal: TimeOnly.Parse(scenario.Now),
            leftOn: scenario.LeftOn?.Select(ToLeftOn).ToList(),
            flatPower: scenario.FlatPower?.Select(ToFlat).ToList(),
            heat: ToHeat(scenario.Wbgt),
            cooling: scenario.Cooling?.Select(c => new CoolingDevice(c.Name, c.IsOn, c.Watts)).ToList(),
            cold: ToCold(scenario.TemperatureC),
            heating: scenario.Heating?.Select(h => new HeatingDevice(h.Name, h.IsOn, h.Watts)).ToList());

        return new RiskOutcome(scenario, result, result.Level >= NotifyThreshold);
    }

    /// <summary>
    /// Expands the described typical day into history. Real days are never identical, and
    /// a baseline of fourteen copies of one number would make "today differs from usual"
    /// far easier to satisfy than it is in a real home, so each day is nudged by a small
    /// deterministic amount that averages out to the shape as written.
    /// </summary>
    private static List<DailyActivity> Baseline(DayShape shape)
    {
        var days = new List<DailyActivity>(BaselineDays);

        for (var i = BaselineDays; i >= 1; i--)
        {
            var date = Today.AddDays(-i);
            var drift = (i % 5) - 2;

            days.Add(ToActivity(
                shape with
                {
                    Uses = Math.Max(1, shape.Uses + drift),
                    ActiveMinutes = Math.Max(0, shape.ActiveMinutes + (drift * 10)),
                    EnergyWh = Math.Max(0, shape.EnergyWh + (drift * 15))
                },
                date,
                minuteOffset: drift * 6));
        }

        return days;
    }

    private static DailyActivity ToActivity(DayShape shape, DateOnly date, int minuteOffset = 0)
    {
        var first = Shift(shape.First, minuteOffset);

        return new DailyActivity(
            Date: date,
            FirstActivityTime: first,
            LastActivityTime: Shift(shape.Last, 0),
            DeviceUsageCount: shape.Uses,
            ActiveMinutes: shape.ActiveMinutes,
            NightActivityCount: shape.Night,
            EnergyWh: shape.EnergyWh,
            FirstPowerMoveTime: first,
            SettledTime: Shift(shape.Last, 0));
    }

    private static TimeOnly? Shift(string? value, int minutes) =>
        value is null ? null : TimeOnly.Parse(value).AddMinutes(minutes);

    private static LeftOnDevice ToLeftOn(LeftOnShape shape) => new(
        shape.Name,
        Enum.Parse<DeviceType>(shape.Type, ignoreCase: true),
        TimeSpan.FromHours(shape.Hours));

    private static FlatPowerDevice ToFlat(FlatShape shape) =>
        new(shape.Name, TimeSpan.FromHours(shape.Hours), shape.ThresholdHours);

    private static HeatAdvisory? ToHeat(double? wbgt) => wbgt is not { } value
        ? null
        : new HeatAdvisory(
            value,
            HeatAdvisory.Classify(value),
            TemperatureC: null,
            HumidityPercent: null,
            ObservedAtUtc: new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            AreaName: "東京",
            Attribution: "環境省熱中症予防情報サイト");

    private static ColdAdvisory? ToCold(double? temperatureC) => temperatureC is not { } value
        ? null
        : new ColdAdvisory(
            value,
            ColdAdvisory.Classify(value),
            HumidityPercent: null,
            ObservedAtUtc: new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            AreaName: "東京",
            Attribution: "気象庁");
}
