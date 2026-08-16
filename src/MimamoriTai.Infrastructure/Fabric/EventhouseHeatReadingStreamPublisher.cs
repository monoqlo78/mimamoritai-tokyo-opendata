using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Streams outdoor heat-index observations into a Fabric Eventhouse (KQL database)
/// table (HeatReadings) using the same raw streaming ingestion REST endpoint as
/// <see cref="EventhousePlugMiniReadingStreamPublisher"/>, authenticated
/// passwordlessly via Azure.Identity.
///
/// Deliberately mirrors that publisher's token-caching/NDJSON/exception handling
/// exactly (rather than sharing code) so a heat ingestion outage or misconfiguration
/// can never affect device telemetry publishing, and vice versa. Must never throw:
/// this is a best-effort secondary write path.
/// </summary>
public sealed class EventhouseHeatReadingStreamPublisher(
    HttpClient http,
    IOptions<EventhouseOptions> options,
    TokenCredential credential,
    ILogger<EventhouseHeatReadingStreamPublisher> logger) : IHeatReadingStreamPublisher
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly EventhouseOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AccessToken? _cachedToken;

    public bool IsConfigured => _options.IsConfigured;

    public string DisplayName => "EventhouseHeatReading";

    public async Task<EventStreamPublishResult> PublishAsync(
        IReadOnlyList<HeatReadingRecord> readings, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (!IsConfigured)
        {
            return new EventStreamPublishResult(false, 0, 0, "Eventhouse is not configured.");
        }

        if (readings.Count == 0)
        {
            return new EventStreamPublishResult(true, 0, sw.ElapsedMilliseconds);
        }

        try
        {
            var token = await GetTokenAsync(ct);
            var body = BuildNewlineDelimitedJson(readings);

            var url = $"v1/rest/ingest/{_options.DatabaseName}/{_options.HeatTableName}" +
                      $"?streamFormat=json&mappingName={_options.HeatMappingName}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await http.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Deliberately does not include the response body, which may echo request data.
                logger.LogWarning("Eventhouse heat ingest failed with {Status}.", (int)response.StatusCode);
                return new EventStreamPublishResult(false, 0, sw.ElapsedMilliseconds,
                    $"Eventhouse returned {(int)response.StatusCode}.");
            }

            return new EventStreamPublishResult(true, readings.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or CredentialUnavailableException
            or Azure.RequestFailedException)
        {
            logger.LogWarning("Eventhouse heat ingest failed: {Type}.", ex.GetType().Name);
            return new EventStreamPublishResult(false, 0, sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    private async Task<AccessToken> GetTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is { } cached && cached.ExpiresOn > DateTimeOffset.UtcNow + RefreshMargin)
        {
            return cached;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is { } stillCached && stillCached.ExpiresOn > DateTimeOffset.UtcNow + RefreshMargin)
            {
                return stillCached;
            }

            var scope = _options.ClusterUri.TrimEnd('/') + "/.default";
            var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), ct);
            _cachedToken = token;
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string BuildNewlineDelimitedJson(IReadOnlyList<HeatReadingRecord> readings)
    {
        var sb = new StringBuilder();
        foreach (var r in readings)
        {
            var line = JsonSerializer.Serialize(new
            {
                readingId = r.ReadingId,
                pointCode = r.PointCode,
                areaName = r.AreaName,
                wbgt = r.Wbgt,
                level = r.Level,
                levelText = r.LevelText,
                temperatureC = r.TemperatureC,
                humidityPercent = r.HumidityPercent,
                observedAtUtc = r.ObservedAtUtc.ToString("o")
            }, JsonOptions);
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }
}
