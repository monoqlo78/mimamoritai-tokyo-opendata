namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// One outdoor observation, shaped for the Fabric Eventhouse HeatReadings table (see
/// docs/FABRIC_SETUP.md for the exact schema).
///
/// Its own record/interface rather than a variant of
/// <see cref="PlugMiniReadingRecord"/> because this stream is not per-household or
/// per-device at all -- it is city-wide open data. Downstream KQL joins it to the
/// plug readings on time, which only works if the two stay separate tables.
///
/// <para>
/// <paramref name="Wbgt"/> is nullable because the 環境省 index is only published from
/// late April to late October. The row still carries the year-round temperature, so a
/// winter query against this table answers "was the house heated on the cold days"
/// just as a summer one answers "was it cooled on the hot ones".
/// </para>
/// </summary>
public sealed record HeatReadingRecord(
    Guid ReadingId,
    string PointCode,
    string AreaName,
    double? Wbgt,
    int Level,
    string LevelText,
    int ColdLevel,
    string ColdLevelText,
    double? TemperatureC,
    double? HumidityPercent,
    DateTime ObservedAtUtc);

/// <summary>
/// Streams outdoor observations to the Fabric Eventhouse HeatReadings table.
/// Mirrors <see cref="IPlugMiniReadingStreamPublisher"/>'s "never throw, best-effort
/// secondary write" contract exactly, and is a separate interface/table so a weather
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
