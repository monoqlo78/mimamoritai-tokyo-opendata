namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Microsoft Fabric Eventhouse (KQL database) streaming ingestion settings.
/// No secrets here: authentication uses Azure.Identity (DefaultAzureCredential /
/// Azure CLI login locally, Managed Identity in Azure).
/// </summary>
public sealed class EventhouseOptions
{
    public const string SectionName = "Eventhouse";

    public bool Enabled { get; set; }

    /// <summary>Engine query/ingest URI, e.g. https://&lt;cluster&gt;.z2.kusto.fabric.microsoft.com
    /// (NOT the ingest-&lt;cluster&gt; host: streaming ingestion goes to the engine host).</summary>
    public string ClusterUri { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = "MimamoriEventhouse";

    public string TableName { get; set; } = "DeviceEvents";

    public string MappingName { get; set; } = "DeviceEventsMapping";

    /// <summary>Table for the per-poll-cycle Plug Mini telemetry stream (see docs/FABRIC_SETUP.md).</summary>
    public string PlugMiniTableName { get; set; } = "SwitchBotPlugReadings";

    public string PlugMiniMappingName { get; set; } = "SwitchBotPlugReadingsMapping";

    /// <summary>Table for the outdoor heat index captured from open data (see docs/FABRIC_SETUP.md).</summary>
    public string HeatTableName { get; set; } = "HeatReadings";

    public string HeatMappingName { get; set; } = "HeatReadingsMapping";

    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ClusterUri)
        && !string.IsNullOrWhiteSpace(DatabaseName)
        && !string.IsNullOrWhiteSpace(TableName);
}
