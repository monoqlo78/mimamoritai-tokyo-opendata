using Microsoft.Extensions.Logging;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// DEMO ONLY. Stands in for the Fabric Eventhouse heat reading stream while it is not
/// configured, so the capture background service stays fully functional (and
/// demoable) with zero secrets and no network calls. Open data still reaches the app
/// database in this mode; only the analytics hop is skipped.
/// </summary>
public sealed class MockHeatReadingStreamPublisher(ILogger<MockHeatReadingStreamPublisher> logger)
    : IHeatReadingStreamPublisher
{
    public bool IsConfigured => false;

    public string DisplayName => "MockHeatReadingStream";

    public Task<EventStreamPublishResult> PublishAsync(
        IReadOnlyList<HeatReadingRecord> readings, CancellationToken ct = default)
    {
        logger.LogDebug("MockHeatReadingStream: pretending to publish {Count} reading(s).", readings.Count);
        return Task.FromResult(new EventStreamPublishResult(true, readings.Count, 0));
    }
}
