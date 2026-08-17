-- Mirrors MimamoriTai.Web/Services/AdminConsoleService.LoadAsync, read-only.
-- Window = 7 days, matching AdminConsoleService.DefaultWindowDays.
DECLARE @since DATETIMEOFFSET = DATEADD(day, -7, SYSUTCDATETIME());

SELECT
    h.Id                AS HouseholdId,
    h.Name              AS Name,
    h.DataSourceMode    AS DataSourceMode,
    ISNULL(mem.Cnt, 0)  AS MemberCount,
    ISNULL(res.Cnt, 0)  AS ResidentCount,
    ISNULL(dev.Cnt, 0)  AS DeviceCount,
    CASE
        WHEN ev.LastEventUtc IS NULL THEN pr.LastPlugReadingUtc
        WHEN pr.LastPlugReadingUtc IS NULL THEN ev.LastEventUtc
        WHEN ev.LastEventUtc >= pr.LastPlugReadingUtc THEN ev.LastEventUtc
        ELSE pr.LastPlugReadingUtc
    END                 AS LastEventUtc,
    sb.Status           AS SwitchBotStatus,
    sb.LastErrorMessage AS SwitchBotError,
    ISNULL(lr.Cnt, 0)   AS ActiveLineRecipients,
    ISNULL(al.Total, 0) AS AlertsInWindow,
    ISNULL(al.Failed, 0) AS FailedAlertsInWindow,
    risk.RiskLevel      AS LatestRiskLevel
FROM mimamori.Households h
LEFT JOIN (SELECT HouseholdId, COUNT(*) Cnt FROM mimamori.HouseholdMembers GROUP BY HouseholdId) mem
    ON mem.HouseholdId = h.Id
LEFT JOIN (SELECT HouseholdId, COUNT(*) Cnt FROM mimamori.People WHERE Role = 'Resident' GROUP BY HouseholdId) res
    ON res.HouseholdId = h.Id
LEFT JOIN (SELECT HouseholdId, COUNT(*) Cnt FROM mimamori.Devices GROUP BY HouseholdId) dev
    ON dev.HouseholdId = h.Id
LEFT JOIN (SELECT HouseholdId, MAX(OccurredAtUtc) LastEventUtc FROM mimamori.DeviceEvents GROUP BY HouseholdId) ev
    ON ev.HouseholdId = h.Id
LEFT JOIN (SELECT HouseholdId, MAX(OccurredAtUtc) LastPlugReadingUtc FROM mimamori.PlugMiniReadings GROUP BY HouseholdId) pr
    ON pr.HouseholdId = h.Id
-- Encrypted token/secret columns are deliberately not selected.
LEFT JOIN (SELECT HouseholdId, Status, LastErrorMessage FROM mimamori.SwitchBotConnections) sb
    ON sb.HouseholdId = h.Id
LEFT JOIN (SELECT HouseholdId, COUNT(*) Cnt FROM mimamori.LineRecipients WHERE IsActive = 1 GROUP BY HouseholdId) lr
    ON lr.HouseholdId = h.Id
LEFT JOIN (
    SELECT HouseholdId,
           COUNT(*) Total,
           SUM(CASE WHEN Success = 0 THEN 1 ELSE 0 END) Failed
    FROM mimamori.WatchAlerts WHERE SentAtUtc >= @since GROUP BY HouseholdId
) al ON al.HouseholdId = h.Id
OUTER APPLY (
    SELECT TOP 1 r.RiskLevel FROM mimamori.RiskAssessments r
    WHERE r.HouseholdId = h.Id ORDER BY r.CreatedAtUtc DESC
) risk
ORDER BY h.DataSourceMode, h.CreatedAtUtc;

-- WatchAlert.Message is intentionally excluded: it is family-facing prose that can
-- name the resident. Only the machine-generated Reason is mirrored.
SELECT TOP 50
    a.Id             AS AlertId,
    a.HouseholdId    AS HouseholdId,
    ISNULL(h.Name, N'(deleted)') AS HouseholdName,
    a.RiskLevel      AS RiskLevel,
    a.Score          AS Score,
    a.Reason         AS Reason,
    a.Success        AS Success,
    a.Error          AS Error,
    a.SentAtUtc      AS SentAtUtc
FROM mimamori.WatchAlerts a
LEFT JOIN mimamori.Households h ON h.Id = a.HouseholdId
WHERE a.SentAtUtc >= @since
ORDER BY a.SentAtUtc DESC;

-- Hourly activity rollup. Counts only: no raw payload, no resident identifier.
-- PlugMiniReadings.DailyEnergyWh carries SwitchBot's instantaneous real watts
-- despite the legacy column name, so it is integrated into hourly Wh.
-- 30 days so the console has a usable time series even when alerting is quiet.
WITH DeviceEventBuckets AS (
    SELECT
        e.HouseholdId                                  AS HouseholdId,
        e.DeviceId                                     AS DeviceId,
        DATEADD(hour, DATEDIFF(hour, 0, CAST(e.OccurredAtUtc AS DATETIME2)), 0) AS BucketStart,
        COUNT(*)                                       AS EventCount,
        SUM(CASE WHEN e.State IN ('on', 'active') THEN 1 ELSE 0 END) AS OnCount,
        MAX(e.Source)                                  AS Source,
        CAST(NULL AS FLOAT)                            AS EnergyWh
    FROM mimamori.DeviceEvents e
    WHERE e.OccurredAtUtc >= DATEADD(day, -30, SYSUTCDATETIME())
    GROUP BY
        e.HouseholdId,
        e.DeviceId,
        DATEADD(hour, DATEDIFF(hour, 0, CAST(e.OccurredAtUtc AS DATETIME2)), 0)
),
PlugOrdered AS (
    SELECT
        r.HouseholdId,
        r.DeviceId,
        CAST(r.OccurredAtUtc AS DATETIME2) AS StartAt,
        CAST(
            CASE
                WHEN LEAD(r.OccurredAtUtc) OVER (PARTITION BY r.HouseholdId, r.DeviceId ORDER BY r.OccurredAtUtc) IS NULL
                    THEN NULL
                WHEN DATEDIFF(minute, r.OccurredAtUtc, LEAD(r.OccurredAtUtc) OVER (PARTITION BY r.HouseholdId, r.DeviceId ORDER BY r.OccurredAtUtc)) > 10
                    THEN DATEADD(minute, 10, r.OccurredAtUtc)
                ELSE LEAD(r.OccurredAtUtc) OVER (PARTITION BY r.HouseholdId, r.DeviceId ORDER BY r.OccurredAtUtc)
            END AS DATETIME2) AS EndAt,
        r.DailyEnergyWh AS Watts
    FROM mimamori.PlugMiniReadings r
    WHERE r.OccurredAtUtc >= DATEADD(day, -30, SYSUTCDATETIME())
        AND r.DailyEnergyWh IS NOT NULL
        AND r.DailyEnergyWh > 0
),
PlugSlices AS (
    SELECT
        p.HouseholdId,
        p.DeviceId,
        DATEADD(hour, DATEDIFF(hour, 0, p.StartAt), 0) AS BucketStart,
        p.Watts * DATEDIFF(second, p.StartAt, CASE WHEN p.EndAt < b.NextHour THEN p.EndAt ELSE b.NextHour END) / 3600.0 AS EnergyWh
    FROM PlugOrdered p
    CROSS APPLY (SELECT DATEADD(hour, DATEDIFF(hour, 0, p.StartAt) + 1, 0) AS NextHour) b
    WHERE p.EndAt IS NOT NULL AND p.EndAt > p.StartAt

    UNION ALL

    SELECT
        p.HouseholdId,
        p.DeviceId,
        DATEADD(hour, DATEDIFF(hour, 0, p.EndAt), 0) AS BucketStart,
        p.Watts * DATEDIFF(second, b.NextHour, p.EndAt) / 3600.0 AS EnergyWh
    FROM PlugOrdered p
    CROSS APPLY (SELECT DATEADD(hour, DATEDIFF(hour, 0, p.StartAt) + 1, 0) AS NextHour) b
    WHERE p.EndAt IS NOT NULL AND p.EndAt > b.NextHour
),
PlugBuckets AS (
    SELECT
        HouseholdId,
        DeviceId,
        BucketStart,
        0 AS EventCount,
        0 AS OnCount,
        N'SwitchBotPoll' AS Source,
        SUM(EnergyWh) AS EnergyWh
    FROM PlugSlices
    GROUP BY HouseholdId, DeviceId, BucketStart
),
Activity AS (
    SELECT * FROM DeviceEventBuckets
    UNION ALL
    SELECT * FROM PlugBuckets
)
SELECT
    a.HouseholdId                                  AS HouseholdId,
    ISNULL(h.Name, N'(deleted)')                   AS HouseholdName,
    a.DeviceId                                     AS DeviceId,
    ISNULL(d.Name, N'(unknown)')                   AS DeviceName,
    ISNULL(d.DeviceType, N'')                      AS DeviceType,
    a.BucketStart                                  AS BucketStart,
    SUM(a.EventCount)                              AS EventCount,
    SUM(a.OnCount)                                 AS OnCount,
    MAX(a.Source)                                  AS Source,
    SUM(a.EnergyWh)                                AS EnergyWh
FROM Activity a
LEFT JOIN mimamori.Households h ON h.Id = a.HouseholdId
LEFT JOIN mimamori.Devices d ON d.Id = a.DeviceId
GROUP BY
    a.HouseholdId,
    a.DeviceId,
    h.Name,
    d.Name,
    d.DeviceType,
    a.BucketStart
ORDER BY BucketStart;

-- AI router rollup. Counts only: AiRequestLogs stores no prompt or completion
-- text, and the household id is deliberately dropped here as well.
--
-- The grain is what makes the routing visible: `Router` names the client that
-- served the call ("Azure Model Router" or "MockAiRouter"), and `ResolvedModel`
-- is the model the router actually picked, taken from the response `model`
-- field. Calls that failed before reaching a model carry an empty
-- ResolvedModel, which is what the console renders as the trailing "unanswered"
-- bar instead of quietly dropping them.
SELECT
    l.Purpose        AS Purpose,
    l.Router         AS Router,
    l.ResolvedModel  AS ResolvedModel,
    COUNT(*)         AS CallCount,
    SUM(CASE WHEN l.Success = 1 THEN 1 ELSE 0 END) AS SuccessCount,
    CAST(ROUND(AVG(CAST(l.DurationMs AS FLOAT)), 0) AS BIGINT) AS AvgDurationMs,
    MAX(l.CreatedAtUtc) AS LastCalledAt
FROM mimamori.AiRequestLogs l
GROUP BY l.Purpose, l.Router, l.ResolvedModel
ORDER BY CallCount DESC;
