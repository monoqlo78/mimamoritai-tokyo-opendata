namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// One outdoor heat-index observation, shaped for the Fabric Eventhouse HeatReadings
/// table (see docs/FABRIC_SETUP.md for the exact schema).
///
/// Its own record/interface rather than a variant of
/// <see cref="PlugMiniReadingRecord"/> because this stream is not per-household or
/// per-device at all -- it is city-wide open data. Downstream KQL joins it to the
/// plug readings on time, which only works if the two stay separate tables.
/// </summary>
public sealed record HeatReadingRecord(
    Guid ReadingId,
    string PointCode,
    string AreaName,
    double Wbgt,
    int Level,
    string LevelText,
    double? TemperatureC,
    double? HumidityPercent,
    DateTime ObservedAtUtc);

/// <summary>
/// Streams heat-index observations to the Fabric Eventhouse HeatReadings table.
/// Mirrors <see cref="IPlugMiniReadingStreamPublisher"/>'s "never throw, best-effort
/// secondary write" contract exactly, and is a separate interface/table so a heat
/// ingestion outage can never affect device telemetry publishing, and vice versa.
/// </summary>
public interface IHeatReadingStreamPublisher
{
    bool IsConfigured { get; }
    string DisplayName { get; }

    Task<EventStreamPublishResult> PublishAsync(
        IReadOnlyList<HeatReadingRecord> readings,
        CancellationToken ct = default);
}
