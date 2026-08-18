-- =============================================================================
-- 見守り隊 / CareRoute AI ― Power BI セマンティックモデル用のビュー
-- 対象: Rayfin が作った Fabric SQL データベース（運用コンソールの土台）
-- =============================================================================
--
-- なぜビューが要るのか
-- --------------------
-- Rayfin のエンティティ定義は数値も日時も `@text()`、つまり NVARCHAR で持って
-- いる（fabric-app/rayfin/data/*.ts）。運用コンソールは値をそのまま文字として
-- 描くのでこれで困らないが、Power BI から見ると全列が「テキスト」になり、
--
--   * SUM / AVERAGE がそのままでは書けない
--   * 時系列の軸に使えない（日付階層が生えない）
--   * 「未測定」を表す空文字が 0 に化ける
--
-- という三つの問題が出る。
--
-- そこで、TRY_CONVERT で型を付け直したビューをこのスクリプトで作り、
-- セマンティックモデルにはテーブルではなく **ビューだけ** を載せる。
--
-- 【重要】このスクリプトは 2 か所で実行する
-- ----------------------------------------
-- ビューは OneLake にミラーされない。運ばれるのはベーステーブルだけである
-- （分析エンドポイント側の INFORMATION_SCHEMA.TABLES にビューが 1 つも映らない
-- ことを実測で確認済み）。そのため次の両方で実行すること。
--
--   1. Fabric SQL データベース          ...database.fabric.microsoft.com
--      → 運用コンソールや直接の SQL 照会から使うため
--   2. SQL 分析エンドポイント            ...datawarehouse.fabric.microsoft.com
--      → Power BI / セマンティックモデルから見えるようにするため
--
-- 分析エンドポイントはミラーされたテーブルこそ読み取り専用だが、ビューは作れる。
--
-- 空文字の扱い（重要）
-- --------------------
-- 同期側は「測っていない」を空文字で書く。0 ではない（FabricSqlConsoleSync.cs
-- の Measure() 参照。0℃ は真冬のごく普通の観測値なので、欠測と混ぜられない）。
-- TRY_CONVERT は空文字に対して NULL を返すので、この区別はビューでも保たれる。
-- Power BI 側でも SUM は NULL を飛ばすため、欠測が 0 として平均を押し下げる
-- ことはない。
--
-- 使い方
-- ------
--   1. Fabric ポータルで対象の SQL データベースを開く
--   2. 「新しいクエリ」にこのファイルを貼って実行
--   3. モデリング → 新しいセマンティックモデル → v_ で始まるビューを選択
--
-- 冪等。何度実行しても同じ結果になる。
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 世帯ごとの運用状況（ファクト兼ディメンション）
-- 1 世帯 1 行で上書きされる現在値なので、履歴の分析には使えない点に注意。
-- 推移を見たいときは v_ActivityHourly / v_OutdoorHourly を使う。
-- -----------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.v_Household AS
SELECT
    s.id                                            AS HouseholdKey,
    s.householdId                                   AS HouseholdId,
    s.name                                          AS 世帯名,
    s.dataSourceMode                                AS データ種別,          -- Sample / Production
    TRY_CONVERT(int, NULLIF(s.memberCount, ''))     AS 家族人数,
    TRY_CONVERT(int, NULLIF(s.residentCount, ''))   AS 見守り対象人数,
    TRY_CONVERT(int, NULLIF(s.deviceCount, ''))     AS 機器台数,
    TRY_CONVERT(datetime2(0), NULLIF(s.lastEventUtc, '')) AS 最終イベント日時UTC,
    s.switchBotStatus                               AS SwitchBot接続状態,   -- NotConfigured / Connected / Error
    NULLIF(s.switchBotError, '')                    AS SwitchBotエラー,
    TRY_CONVERT(int, NULLIF(s.activeLineRecipients, '')) AS LINE通知先数,
    TRY_CONVERT(int, NULLIF(s.alertsInWindow, ''))  AS 期間内アラート数,
    TRY_CONVERT(int, NULLIF(s.failedAlertsInWindow, '')) AS 期間内通知失敗数,
    NULLIF(s.latestRiskLevel, '')                   AS 直近リスク,          -- Low / Medium / High
    -- 並べ替え用。Power BI の「列で並べ替え」に指定すると凡例が Low→High になる。
    CASE s.latestRiskLevel WHEN 'Low' THEN 1 WHEN 'Medium' THEN 2 WHEN 'High' THEN 3 END
                                                    AS 直近リスク順,
    s.needsAttention                                AS 要対応,
    TRY_CONVERT(decimal(18, 2), NULLIF(s.powerTodayWh, ''))    AS 本日電力量Wh,
    TRY_CONVERT(decimal(18, 2), NULLIF(s.powerBaselineWh, '')) AS 平常時電力量Wh,
    NULLIF(s.powerTrend, '')                        AS 電力傾向,            -- Higher / Lower / Typical / Unknown
    s.capturedAt                                    AS 取得日時
FROM dbo.HouseholdSnapshots AS s;
GO

-- -----------------------------------------------------------------------------
-- 通知の記録（ファクト）
-- -----------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.v_Alert AS
SELECT
    a.id                                          AS AlertKey,
    a.householdId                                 AS HouseholdId,
    a.householdName                               AS 世帯名,
    NULLIF(a.riskLevel, '')                       AS リスク,
    CASE a.riskLevel WHEN 'Low' THEN 1 WHEN 'Medium' THEN 2 WHEN 'High' THEN 3 END
                                                  AS リスク順,
    TRY_CONVERT(int, NULLIF(a.score, ''))         AS スコア,
    a.reason                                      AS 理由,
    a.success                                     AS 送信成功,
    -- 失敗率を測るために 1/0 も持たせる。真偽値のままだと DAX が書きづらい。
    CASE WHEN a.success = 1 THEN 0 ELSE 1 END     AS 失敗フラグ,
    NULLIF(a.error, '')                           AS エラー,
    a.sentAt                                      AS 送信日時,
    CONVERT(date, a.sentAt)                       AS 送信日
FROM dbo.AlertRecords AS a;
GO

-- -----------------------------------------------------------------------------
-- 機器の 1 時間ごとの動き（ファクト・時系列の主役）
-- -----------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.v_ActivityHourly AS
SELECT
    b.id                                          AS ActivityKey,
    b.householdId                                 AS HouseholdId,
    b.householdName                               AS 世帯名,
    b.deviceId                                    AS DeviceId,
    b.deviceName                                  AS 機器名,
    b.deviceType                                  AS 機器種別,
    b.source                                      AS 取得元,
    b.bucketStart                                 AS 時刻,
    CONVERT(date, b.bucketStart)                  AS 日付,
    DATEPART(hour, b.bucketStart)                 AS 時,
    TRY_CONVERT(int, NULLIF(b.eventCount, ''))    AS イベント数,
    TRY_CONVERT(int, NULLIF(b.onCount, ''))       AS ON回数,
    -- 空文字は「電力を測っていない機器」であって 0Wh ではない。NULL のままにする。
    TRY_CONVERT(decimal(18, 3), NULLIF(b.energyWh, '')) AS 電力量Wh
FROM dbo.ActivityBuckets AS b;
GO

-- -----------------------------------------------------------------------------
-- 屋外の 1 時間ごとの観測（ファクト・オープンデータ側）
-- 気象庁アメダスと環境省 WBGT を時間単位に丸めたもの。
-- -----------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.v_OutdoorHourly AS
SELECT
    o.id                                          AS OutdoorKey,
    o.pointCode                                   AS 観測点コード,
    o.areaName                                    AS 地域名,
    o.bucketStart                                 AS 時刻,
    CONVERT(date, o.bucketStart)                  AS 日付,
    DATEPART(hour, o.bucketStart)                 AS 時,
    TRY_CONVERT(decimal(9, 2), NULLIF(o.temperatureC, ''))    AS 気温C,
    TRY_CONVERT(decimal(9, 2), NULLIF(o.minTemperatureC, '')) AS 最低気温C,
    TRY_CONVERT(decimal(9, 2), NULLIF(o.maxTemperatureC, '')) AS 最高気温C,
    TRY_CONVERT(decimal(9, 2), NULLIF(o.humidityPercent, '')) AS 湿度Pct,
    TRY_CONVERT(decimal(9, 2), NULLIF(o.maxWbgt, ''))         AS 暑さ指数WBGT,
    TRY_CONVERT(int, NULLIF(o.heatLevel, ''))                 AS 熱中症警戒度,
    TRY_CONVERT(int, NULLIF(o.coldLevel, ''))                 AS 低体温警戒度,
    TRY_CONVERT(int, NULLIF(o.sampleCount, ''))               AS 観測件数
FROM dbo.OutdoorReadings AS o;
GO

-- -----------------------------------------------------------------------------
-- AI 呼び出しの集計（ファクト・コストと品質の観測用）
-- 審査で指摘された「トークン計測」を足すなら、この行に列が増える形になる。
-- -----------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.v_AiRouterCall AS
SELECT
    c.id                                          AS AiCallKey,
    c.purpose                                     AS 用途,
    c.router                                      AS ルーター,
    c.resolvedModel                               AS 実際のモデル,
    TRY_CONVERT(int, NULLIF(c.callCount, ''))     AS 呼び出し回数,
    TRY_CONVERT(int, NULLIF(c.successCount, ''))  AS 成功回数,
    TRY_CONVERT(int, NULLIF(c.callCount, '')) - TRY_CONVERT(int, NULLIF(c.successCount, ''))
                                                  AS 失敗回数,
    TRY_CONVERT(decimal(12, 1), NULLIF(c.avgDurationMs, '')) AS 平均応答ms,
    c.lastCalledAt                                AS 最終呼び出し日時,
    CONVERT(date, c.lastCalledAt)                 AS 最終呼び出し日
FROM dbo.AiRouterCalls AS c;
GO

-- -----------------------------------------------------------------------------
-- 日付テーブル
--
-- Power BI の自動日付テーブルではなく、これを「日付テーブルとしてマーク」して
-- 使う。屋外の観測（v_OutdoorHourly）と家の中の動き（v_ActivityHourly）を
-- 同じ日付で並べるには、両方から 1 本の日付表に張る必要があるため。
-- これが本作の主題である「気温 × 電力」を Power BI 側で再現する土台になる。
--
-- 範囲は実データの最小・最大から作るので、データが増えれば自動で伸びる。
-- -----------------------------------------------------------------------------
CREATE OR ALTER VIEW dbo.v_Date AS
WITH bounds AS (
    SELECT
        MIN(d) AS MinDate,
        MAX(d) AS MaxDate
    FROM (
        SELECT CONVERT(date, bucketStart) AS d FROM dbo.ActivityBuckets
        UNION ALL
        SELECT CONVERT(date, bucketStart) FROM dbo.OutdoorReadings
        UNION ALL
        SELECT CONVERT(date, sentAt) FROM dbo.AlertRecords
    ) AS all_dates
),
-- 再帰ではなくクロス結合で日付を作る。再帰 CTE は既定の再帰上限 100 に当たり、
-- MAXRECURSION ヒントがビュー定義では使えないため。
-- 4 桁ぶん組むので最長 10,000 日（約 27 年）。実運用でこれを超えることはない。
digits AS (
    SELECT n FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS v(n)
),
numbers AS (
    SELECT (d4.n * 1000 + d3.n * 100 + d2.n * 10 + d1.n) AS n
    FROM digits AS d1
    CROSS JOIN digits AS d2
    CROSS JOIN digits AS d3
    CROSS JOIN digits AS d4
)
SELECT
    DATEADD(day, n.n, b.MinDate)                              AS 日付,
    YEAR(DATEADD(day, n.n, b.MinDate))                        AS 年,
    MONTH(DATEADD(day, n.n, b.MinDate))                       AS 月,
    DAY(DATEADD(day, n.n, b.MinDate))                         AS 日,
    DATEPART(weekday, DATEADD(day, n.n, b.MinDate))           AS 曜日番号,
    CONVERT(char(7), DATEADD(day, n.n, b.MinDate), 126)       AS 年月
FROM bounds AS b
CROSS JOIN numbers AS n
WHERE b.MinDate IS NOT NULL
  AND DATEADD(day, n.n, b.MinDate) <= b.MaxDate;
GO
