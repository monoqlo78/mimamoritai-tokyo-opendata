# Microsoft Fabric Data Agent セットアップ

このアプリの「生活データ質問」機能（例:「今日最初に活動したのは何時？」）は、`IFabricDataAgentClient` が設定されていれば Microsoft Fabric の Data Agent に問い合わせ、未設定の場合はアプリ内蔵の `LocalDataQuestionService` がDBから直接キーワードマッチで回答します（`AssistantOrchestrator.HandleQueryAsync`）。

## 現状の実装状況

- `src/MimamoriTai.Infrastructure/Fabric/FabricDataAgentMcpClient.cs` が `IFabricDataAgentClient` の実クライアントです。MCP（Model Context Protocol）のJSON-RPC 2.0メッセージ（`initialize` → `notifications/initialized` → `tools/list` → `tools/call`）をHTTP経由で送受信し、レスポンスが `application/json` / `text/event-stream`（SSEの`data:`行）のどちらで返っても解釈します。認証は `EventhouseStreamPublisher` と同じ `Azure.Identity` の `TokenCredential`（`DefaultAzureCredential`）を使い、スコープ `https://api.fabric.microsoft.com/.default` のトークンをキャッシュして再利用します。
- `ServiceCollectionExtensions.cs` は `FabricOptions.IsConfigured` が true の場合のみ `FabricDataAgentMcpClient` を登録し、それ以外は `MockFabricDataAgentClient`（常に `IsConfigured = false`）を登録します。Fabricへの呼び出しが失敗した場合（未接続・容量停止中・不正なレスポンス等）も例外は投げず、`FabricAnswer(Success:false)` を返して `AssistantOrchestrator` が `LocalDataQuestionService` にフォールバックします。
- **実際にデプロイ済みのFabric Data Agent**（ワークスペースID・Data AgentIDはシークレットではないため下記に記載。実際の接続にはApp Serviceのアプリ設定で `Fabric:Enabled=true` にする必要があります）:
  - Workspace ID: `e2a48a60-0b5f-421f-91bb-51a33fe528bc`
  - Data Agent ID: `bd915a90-2bc1-4a4f-bcae-749622366f97`
  - MCP endpoint: `https://api.fabric.microsoft.com/v1/mcp/workspaces/e2a48a60-0b5f-421f-91bb-51a33fe528bc/dataagents/bd915a90-2bc1-4a4f-bcae-749622366f97/agent`
  - データソース: Kusto/KQLデータベース `MimamoriEventhouse` のテーブル `DeviceEvents`（列: EventId, HouseholdId, DeviceId, DeviceName, Room, DeviceType, EventType, State, PowerWatts, Source, OccurredAtUtc）。
  - `appsettings.json` にはこれらのGUIDをハードコードしていません（`WorkspaceId`/`DataAgentId`/`McpUrl` は空文字のまま）。環境ごとにApp Serviceのアプリ設定または `dotnet user-secrets` で注入してください。
- ※未検証：以下の「1. ワークスペースの作成」〜「3. Data Agent の作成」の手順・画面名は、実際のFabricポータルで操作を確認したものではなく一般的な知識をもとに記載しています。Fabricの機能・UIは頻繁に更新されるため、実施前に必ず最新のFabricポータル・公式ドキュメント（`docs/REFERENCES.md`）で確認してください。

## 0. SwitchBotPlugReadings テーブル（Plug Miniの毎周期テレメトリ）

`DeviceEvents`（状態変化イベントのみ）とは別に、Plug Mini（JP）クラスの機器から**ポーリング周期ごとに（状態変化の有無に関わらず）**電力テレメトリを取り込む専用テーブルです。実装は `EventhousePlugMiniReadingStreamPublisher`（`src/MimamoriTai.Infrastructure/Fabric/EventhousePlugMiniReadingStreamPublisher.cs`）、Azure SQL側の正データは `PlugMiniReading` エンティティ（`Core/Domain/Entities.cs`）です。`DeviceEvents` 用のストリーミング取り込みロジックとは意図的にコードを共有せず、片方の障害・設定ミスがもう片方に波及しないようにしています。

- **データベース**: `MimamoriEventhouse`（`DeviceEvents` と同じKQLデータベース、`Eventhouse:DatabaseName`）
- **テーブル名**: `SwitchBotPlugReadings`（`Eventhouse:PlugMiniTableName`、既定値）
- **マッピング名**: `SwitchBotPlugReadingsMapping`（`Eventhouse:PlugMiniMappingName`、既定値）
- **列一覧**（`PlugMiniReadingRecord`、`Core/Abstractions/IPlugMiniReadingStreamPublisher.cs` 参照）:

  | 列名 | 型 | 説明 |
  |---|---|---|
  | `readingId` | guid | 一意なレコードID（`PlugMiniReading.Id`） |
  | `householdId` | guid | 世帯ID |
  | `deviceId` | guid | 機器ID |
  | `deviceName` | string | 機器名（取り込み時点のスナップショット） |
  | `room` | string | 部屋名（取り込み時点のスナップショット） |
  | `voltageV` | real (nullable) | 電圧（V）。SwitchBot Plug Mini (JP) ステータスの `voltage` フィールドをそのまま使用 |
  | `currentMa` | real (nullable) | 電流（mA）。ステータスの `electricCurrent` フィールド |
  | `dailyEnergyWh` | real (nullable) | **列名が誤り。実際は「その瞬間の実電力（W）」です。** SwitchBot Plug Mini (JP) ステータスの `weight` フィールドをそのままマッピングしていますが、公式ドキュメントの説明（"the power consumed in a day, measured in Watts"）は単位と期間が矛盾しており、実測で瞬時電力（W）と確定しました。根拠は4つ：(1) SwitchBotアプリの表示（電力0.9W／電流0.3A／電圧104.4V／使用時間9時間59分／消費電力量0.01kWh）に対しAPIのフィールドは4つしかなく、電圧・電流・使用時間で3つ使い切るため残る「電力」が `weight`、(2) 0.9W × 9.98h = 8.98Wh ≒ 0.01kWh とアプリの消費電力量が一致、(3) 本番データで `weight` が時系列で減少する（積算カウンタなら不可能）、(4) `currentMa = 0` のとき必ず `weight = 0`。**使用電力量（Wh）が欲しい場合はこの値を時間で積分してください**（後述のKQL参照）。列名の変更はストリーム定義とスキーマの互換性を壊すため見送っています。 |
  | `usageMinutesToday` | int (nullable) | 当日の稼働時間（分）。ステータスの `electricityOfDay` フィールド（`StatusBody` DTOに今回追加）。SwitchBot公式ドキュメントでは「今日の使用時間（分）」とされているフィールドです。 |
  | `approxWatts` | real (nullable) | **皮相電力（VA）であり実電力ではありません。** `voltageV * currentMa / 1000`（力率1と仮定）で算出しています。本番実測では `currentMa = 314`・`voltageV = 104.1` で `approxWatts = 32.7` に対し実電力（`dailyEnergyWh`）は 0.3W、つまり**力率0.009**でした。`currentMa = 314` なのに実電力が0のサンプルも203件あります。**活動判定や電力量計算には使わないでください**（実電力が取れないときのフォールバック専用）。`voltageV`/`currentMa` の両方が存在する場合のみ計算され、片方でも欠けている場合は null です。 |
  | `occurredAtUtc` | datetime | ポーリング周期の時刻（UTC、ISO 8601） |

- **重複排除キー**: Azure SQL側では `(HouseholdId, DeviceId, OccurredAtUtc)` の組をアプリケーションレベルの重複排除キーとして使用しています（同じポーリング周期内で同じ機器の読み取りを二重挿入しない。詳細は `SwitchBotPollingCycleService` とそのテスト参照）。Eventhouse側にはこの一意性を強制するインデックス/ポリシーは構築していません（KQLの性質上、重複投入されても分析クエリ側で `summarize arg_max(...)` 等で最新値のみを扱うか、`OccurredAtUtc` での重複排除を行うことを推奨します）。
- **公開タイミング**: `PlugMiniReading.PublishedToStreamAtUtc` が null の行のみを対象に、`PlugMiniReadingPublishService`（`Core/Application/PlugMiniReadingPublishService.cs`）が `DeviceEvent`/`EventStreamPublishService` と全く同じ「バッチ公開 → 成功した行だけタイムスタンプを刻む」パターンでバックグラウンド公開します（`PlugMiniReadingPublishBackgroundService`）。Fabricへの送信が失敗しても例外は投げず、次回のバックグラウンド実行で再送されます。
- **Eventhouse側の作成（実施済み）**: テーブル・マッピング・ストリーミング取り込みポリシーは作成済みです。**作らないまま publisher を動かすと、存在しないテーブルへの取り込みが永久に HTTP 400 を返し続けます。**実際にそれが1日半続き、1分ごとの再送がF2容量を飽和させて運用コンソール全体が429で落ちました。同じ手順で再作成できるよう、使用したKQLを残します（Fabricポータルの KQL クエリセット、または後述のRESTで実行）。

  ```kusto
  .create table SwitchBotPlugReadings (
      ReadingId: guid, HouseholdId: guid, DeviceId: guid,
      DeviceName: string, Room: string,
      VoltageV: real, CurrentMa: real, DailyEnergyWh: real,
      UsageMinutesToday: int, ApproxWatts: real, OccurredAtUtc: datetime)

  // path は publisher が送るJSONに合わせて camelCase。列名と綴りが違う点に注意。
  .create-or-alter table SwitchBotPlugReadings ingestion json mapping "SwitchBotPlugReadingsMapping"
  '[{"column":"ReadingId","path":"$.readingId"},{"column":"HouseholdId","path":"$.householdId"},'
  '{"column":"DeviceId","path":"$.deviceId"},{"column":"DeviceName","path":"$.deviceName"},'
  '{"column":"Room","path":"$.room"},{"column":"VoltageV","path":"$.voltageV"},'
  '{"column":"CurrentMa","path":"$.currentMa"},{"column":"DailyEnergyWh","path":"$.dailyEnergyWh"},'
  '{"column":"UsageMinutesToday","path":"$.usageMinutesToday"},{"column":"ApproxWatts","path":"$.approxWatts"},'
  '{"column":"OccurredAtUtc","path":"$.occurredAtUtc"}]'

  .alter table SwitchBotPlugReadings policy streamingingestion enable
  ```

  管理コマンドは Eventhouse のクラスタURIに対して REST でも実行できます（`csl` に上記コマンド、`db` に `MimamoriEventhouse`）:

  ```powershell
  $cl = '<Eventhouse の Query URI>'
  $t  = az account get-access-token --resource $cl --query accessToken -o tsv
  $b  = @{ db = 'MimamoriEventhouse'; csl = '.show tables' } | ConvertTo-Json
  Invoke-RestMethod "$cl/v1/rest/mgmt" -Method Post -Headers @{ Authorization = "Bearer $t" } -ContentType 'application/json' -Body $b
  ```

  取り込みロール（`Eventhouse:PlugMiniTableName`/`PlugMiniMappingName` に書き込むプリンシパル）の権限確認は未実施です。取り込みが動いていることは `SwitchBotPlugReadings | summarize count()` で確認できます。

## 0-2. HeatReadings テーブル（オープンデータの屋外環境）

熱中症ガードと寒さガード（`docs/OPENDATA.md`）が使う**屋外の環境を時系列で残す**テーブルです。世帯にも機器にも紐づかない都市規模のオープンデータなので、`SwitchBotPlugReadings` の派生ではなく独立したテーブルにしています（分析側は時刻で join します）。実装は `EventhouseHeatReadingStreamPublisher`、Azure SQL側の正データは `HeatReading` エンティティ（`Core/Domain/Entities.cs`）です。

環境省のWBGTフィードは4月下旬〜10月下旬しか配信されないため、**11月〜3月は `wbgt` / `level` が null になります**。この5ヶ月間もテーブルが空にならないよう、通年配信される気象庁AMeDASの気温から算出した寒さ区分（`coldLevel`）を同じ行に持たせています。冬のクエリは `wbgt` ではなく `coldLevel` を見てください。

- **テーブル名**: `HeatReadings`（`Eventhouse:HeatTableName`、既定値）
- **マッピング名**: `HeatReadingsMapping`（`Eventhouse:HeatMappingName`、既定値）
- **列一覧**（`HeatReadingRecord`、`Core/Abstractions/IHeatReadingStreamPublisher.cs` 参照）:

  | 列名 | 型 | 説明 |
  |---|---|---|
  | `readingId` | guid | 一意なレコードID（`HeatReading.Id`） |
  | `pointCode` | string | 環境省の観測地点番号（東京は `44132`。`OpenData:PointCode`） |
  | `areaName` | string | 地点名（例: 東京） |
  | `wbgt` | real (nullable) | 暑さ指数（WBGT、℃）。**WBGTフィードが止まる11月〜3月は null** |
  | `level` | int | `HeatAlertLevel`（1=ほぼ安全 … 5=危険）の数値 |
  | `levelText` | string | 同じ区分の日本語ラベル。KQL や Data Agent が環境省の閾値を持たずに「厳重警戒」で集計できるよう非正規化しています |
  | `coldLevel` | int (nullable) | `ColdAlertLevel`（1=穏やか / 2=肌寒い / 3=冷え込み / 4=厳しい冷え込み）。気温が取れた時だけ入ります |
  | `coldLevelText` | string (nullable) | 同じ区分の日本語ラベル |
  | `temperatureC` | real (nullable) | 気象庁AMeDASの気温（℃）。品質フラグが0以外のときは null |
  | `humidityPercent` | real (nullable) | 同・相対湿度（%） |
  | `observedAtUtc` | datetime | 観測・予測時刻（UTC） |

- **重複排除キー**: Azure SQL側は `(PointCode, ObservedAtUtc)` に一意インデックスを張っています。観測時刻キーは**通年配信されるAMeDASの10分刻みを優先**し、AMeDASが取れなかった時だけWBGTの予測時刻にフォールバックします。プロバイダは `OpenData:CacheMinutes` の間ずっと同じ予測列を返すため、これがないと同じ観測が何度も積み上がります。
- **公開タイミング**: `PlugMiniReading` と同じく `PublishedToStreamAtUtc` が null の行だけを `HeatReadingService.PublishUnpublishedBatchAsync` がバッチ送信し、**成功した行にだけ**タイムスタンプを刻みます。取得と送信は `HeatReadingCaptureBackgroundService` が同じ周期で回しますが呼び出しは別々なので、Fabric が落ちていてもDBへの記録は残ります。
- **Eventhouse側の作成**:

  ```kusto
  .create-merge table HeatReadings (
      ReadingId: guid, PointCode: string, AreaName: string,
      Wbgt: real, Level: int, LevelText: string,
      TemperatureC: real, HumidityPercent: real, ObservedAtUtc: datetime,
      ColdLevel: int, ColdLevelText: string)

  // 既存テーブルに寒さ2列を後から足す場合はこちら。
  .alter-merge table HeatReadings (ColdLevel: int, ColdLevelText: string)

  // path は publisher が送るJSONに合わせて camelCase。
  .create-or-alter table HeatReadings ingestion json mapping "HeatReadingsMapping"
  '[{"column":"ReadingId","path":"$.readingId"},{"column":"PointCode","path":"$.pointCode"},'
  '{"column":"AreaName","path":"$.areaName"},{"column":"Wbgt","path":"$.wbgt"},'
  '{"column":"Level","path":"$.level"},{"column":"LevelText","path":"$.levelText"},'
  '{"column":"TemperatureC","path":"$.temperatureC"},{"column":"HumidityPercent","path":"$.humidityPercent"},'
  '{"column":"ObservedAtUtc","path":"$.observedAtUtc"},'
  '{"column":"ColdLevel","path":"$.coldLevel"},{"column":"ColdLevelText","path":"$.coldLevelText"}]'

  .alter table HeatReadings policy streamingingestion enable
  ```

- **分析例**（暑さ指数が高い時間帯に冷房が使われていたか）:

  ```kql
  let heat = HeatReadings
  | where ObservedAtUtc > ago(7d)
  | summarize Wbgt = max(Wbgt), LevelText = take_any(LevelText) by Hour = bin(ObservedAtUtc, 1h);
  SwitchBotPlugReadings
  | where OccurredAtUtc > ago(7d)
  | summarize Watts = avg(DailyEnergyWh) by DeviceName, Hour = bin(OccurredAtUtc, 1h)
  | join kind=inner heat on Hour
  | where Wbgt >= 28
  | project Hour, DeviceName, Watts, Wbgt, LevelText
  | order by Hour desc
  ```

- **分析例**（冷え込んだ時間帯に暖房が使われていたか＝暖房の我慢を探す）:

  ```kql
  let cold = HeatReadings
  | where ObservedAtUtc > ago(30d) and ColdLevel >= 3   // 冷え込み以上
  | summarize TemperatureC = min(TemperatureC), ColdLevelText = take_any(ColdLevelText)
      by Hour = bin(ObservedAtUtc, 1h);
  SwitchBotPlugReadings
  | where OccurredAtUtc > ago(30d)
  | summarize Watts = avg(DailyEnergyWh) by DeviceName, Hour = bin(OccurredAtUtc, 1h)
  | join kind=inner cold on Hour
  | summarize HeatingWatts = sum(Watts) by Hour, TemperatureC, ColdLevelText
  | where HeatingWatts < 1.0        // 冷え込んでいるのに何も動いていない時間帯
  | order by Hour desc
  ```

### 使用電力量を KQL で見る

**`DailyEnergyWh` は「その日の積算電力量」ではありません。** 列名に反して、SwitchBot が返す `weight` は**その瞬間の実電力（W）**です（実機での検証結果は後述）。したがって使用電力量（Wh）を出すには、**サンプルの値をそのサンプルが代表する時間で積分**します。アプリの `PowerUsageService` とまったく同じ考え方です。

1サンプルが代表できる時間には **10分の上限**を設けます。ポーリングは5分間隔ですが、本番で491分の欠測が観測されており、上限がないと停電中もその電力で動き続けたことになってしまうためです。

```kql
// 日別の使用電力量（世帯合計・JST基準）
SwitchBotPlugReadings
| where isnotnull(DailyEnergyWh)
| order by DeviceId asc, OccurredAtUtc asc
| extend NextAt = next(OccurredAtUtc), NextDevice = next(DeviceId)
| extend SpanH = iff(NextDevice == DeviceId,
    min_of(datetime_diff('second', NextAt, OccurredAtUtc) / 3600.0, 10.0 / 60), 10.0 / 60)
| extend Wh = DailyEnergyWh * SpanH   // DailyEnergyWh は瞬時の実電力(W)
| extend Day = bin(datetime_add('hour', 9, OccurredAtUtc), 1d)
| summarize TotalWh = sum(Wh) by Day
| order by Day asc
```

```kql
// 昨日 / 過去7日 / 過去30日
SwitchBotPlugReadings
| where isnotnull(DailyEnergyWh)
| order by DeviceId asc, OccurredAtUtc asc
| extend NextAt = next(OccurredAtUtc), NextDevice = next(DeviceId)
| extend SpanH = iff(NextDevice == DeviceId,
    min_of(datetime_diff('second', NextAt, OccurredAtUtc) / 3600.0, 10.0 / 60), 10.0 / 60)
| extend Wh = DailyEnergyWh * SpanH
| extend Day = bin(datetime_add('hour', 9, OccurredAtUtc), 1d)
| summarize TotalWh = sum(Wh) by Day
| extend DaysAgo = datetime_diff('day', bin(datetime_add('hour', 9, now()), 1d), Day)
| summarize
    Yesterday = sumif(TotalWh, DaysAgo == 1),
    Last7Days = sumif(TotalWh, DaysAgo between (0 .. 6)),
    Last30Days = sumif(TotalWh, DaysAgo between (0 .. 29))
```

```kql
// 「いつもと比べて今日はどうか」（遷移）
// 直近14日の中央値を「いつも」とし、今日と比べます。アプリの表示と同じ判定です。
let daily = SwitchBotPlugReadings
| where isnotnull(DailyEnergyWh)
| order by DeviceId asc, OccurredAtUtc asc
| extend NextAt = next(OccurredAtUtc), NextDevice = next(DeviceId)
| extend SpanH = iff(NextDevice == DeviceId,
    min_of(datetime_diff('second', NextAt, OccurredAtUtc) / 3600.0, 10.0 / 60), 10.0 / 60)
| extend Wh = DailyEnergyWh * SpanH
| extend Day = bin(datetime_add('hour', 9, OccurredAtUtc), 1d)
| summarize TotalWh = sum(Wh) by Day
| extend DaysAgo = datetime_diff('day', bin(datetime_add('hour', 9, now()), 1d), Day);
let today = toscalar(daily | where DaysAgo == 0 | summarize sum(TotalWh));
// 中央値。1日の異常値が「いつも」を書き換えないようにするため平均ではなく中央値を使います。
let baseline = toscalar(daily | where DaysAgo between (1 .. 14) | summarize percentile(TotalWh, 50));
print TodayWh = today, Baseline = baseline, Ratio = today / baseline
| extend Trend = case(isnull(Ratio), "不明", Ratio >= 1.4, "いつもより多め",
    Ratio <= 0.6, "いつもより少なめ", "ほぼいつもどおり")
```

> 閾値 1.4 / 0.6 と中央値14日はアプリの `PowerUsageService` と揃えてあります。下振れ側を甘く（0.6）しているのは、「誰も起きていない」を見逃すほうが誤報より高くつくためです。

機器ごとの内訳を見たいときは、`summarize ... by Day` を `by Day, DeviceId` に変えてください。

## 1. ワークスペースの作成
2. 左下の「ワークスペース」→「新しいワークスペースの作成」から、見守り隊専用のワークスペースを作成します（Fabric容量が必要）。

## 2. SQLデータのミラーリング／取り込み

本アプリのデータは SQL Server（またはSQLite）に保存されています。Fabricから参照可能にする方法は主に2通りです。

- **Azure SQL Database のミラーリング**: SQL Server が Azure SQL Database の場合、Fabricの「Mirrored Azure SQL Database」機能でほぼリアルタイムにOneLakeへ複製できます。
- **Data Pipeline / Dataflow による定期取り込み**: オンプレミスSQL Server や SQLite ファイルの場合は、Fabric Data Pipeline や Dataflow Gen2 でLakehouse/Warehouseへ定期コピーします。

対象テーブルの例: `DeviceEvents`, `DailyActivitySummaries`（未実装のためビューで代用可）, `Devices`, `People`, `RiskAssessments`。

### デモ／LINEアカウント別の表示切替

移行 `AddAnalyticsProfileViews` は、Power BIまたはFabric Real-Time Intelligenceで同じプルダウンを作れるように次のSQLビューを用意します。

- `mimamori.vw_AnalyticsProfiles`: 表示対象ディメンション。`AnalyticsProfileName` は `デモデータ`、`まさあき（LINE）`、`わが家（LINE未連携）` のような表示名です。
- `mimamori.vw_CurrentDeviceStatus`
- `mimamori.vw_DailyActivity`
- `mimamori.vw_RecentDeviceActivity`
- `mimamori.vw_PlugMiniReadings`

各ファクトビューには `AnalyticsProfileId`、`AnalyticsProfileName`、`DataScope`（`Demo` / `LineAccount`）が含まれます。Power BIでは `vw_AnalyticsProfiles[AnalyticsProfileId]` から各ファクトビューの同名列へ1対多のリレーションを作り、`AnalyticsProfileName` をスライサーに配置します。`DataScope` を使えば「デモ／実データ」だけの切替もできます。

LINEの生の `userId` は分析ビューに公開しません。1世帯に複数のLINE受信者がいる場合は、最後に利用した有効な受信者をその世帯の表示プロフィールとして採用します。センサーデータ自体はLINE受信者ではなく世帯に属するため、選択後の全ビジュアルは同じ `HouseholdId` のデータへ絞り込まれます。Webダッシュボード上部の「表示データ」プルダウンも同じ規則を使用します。

## 3. Data Agent の作成

1. Fabricワークスペース内で「+ 新規」→「Data Agent（AI skill）」を作成します。
2. データソースとして、上記でミラーリング／取り込みしたLakehouseまたはWarehouseを追加します。
3. Data Agent の詳細画面から、MCP（Model Context Protocol）エンドポイントURLを取得し、`Fabric:McpUrl` に設定します。

## 4. Data Agent のプロンプト設定

Data Agent はセットアップ画面に 4 つの入力欄（エージェントの指示 / データ ソースの説明 / データ ソースの手順 / クエリの例）があり、そのすべてを埋めないと誤答する。

貼り付ける本文は **[`FABRIC_DATA_AGENT_PROMPTS.md`](FABRIC_DATA_AGENT_PROMPTS.md)** に切り出してある。実際に起きた誤答（UTC を JST と読み違えて「最新は 8/8 まで」と答えた、デモ世帯の照明を実機として説明した）と、その対策としてどの文が必要かを対応づけて書いてあるので、文面を変えるときはそちらを参照すること。

要点だけ挙げると次の 3 つで、どれも欠かすと実際に誤答が再現する。

- **全時刻列は UTC**。表示前に必ず `DATEADD(hour, 9, ...)` で JST に直す。`2026-08-14T22:23:59Z` は JST では翌日の `08-15 07:23:59` になる。
- **最新データの時刻は必ず `MAX(occurredAtUtc)` を実行して求める**。別の質問で拾った行や会話履歴の日付を最新と断定しない。
- **デモと本番は `source` 列で切り分ける**。`Seed` はデモ世帯、`SwitchBotPoll` / `SwitchBotWebhook` / `AppCommand` が本番。世帯名で判別しない。

列名は Lakehouse 側では camelCase（`occurredAtUtc`, `householdId`, …）である点にも注意。アプリが Eventstream へ送る JSON のプロパティ名がそのまま列になるため（`EventHubEventStreamPublisher.cs` L135-149）。

## 5. 想定質問例（動作確認用）

- 「今日最初に活動したのは何時？」
- 「先週と比べて活動量はどう変わった？」
- 「深夜に家電を使った日はありましたか？」
- 「直近2週間で一番活動が少なかった日は？」
- 「今日のお母さんの様子を教えて」

これらは現在 `LocalDataQuestionService`（`src/MimamoriTai.Core/Application/LocalDataQuestionService.cs`）がキーワードマッチで簡易的にカバーしている質問と同じ種類のものです。Fabric Data Agentを実装・接続した後も、`AssistantOrchestrator.HandleQueryAsync` は Fabric が失敗した場合に自動的にこのローカル回答へフォールバックするため、Fabricの回答精度が不十分な場合でもデモが破綻しない設計になっています。

## 実クライアントの接続先

実装が完了したら、次の設定を行ってください（プレースホルダーのみ・実際の値は絶対にコミットしないこと。ただしワークスペースID/Data AgentIDはシークレットではないため上記「現状の実装状況」に実値を記載済みです）。

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "Fabric:WorkspaceId" "<your-fabric-workspace-id>"
dotnet user-secrets set "Fabric:DataAgentId" "<your-fabric-data-agent-id>"
dotnet user-secrets set "Fabric:McpUrl" "<your-fabric-data-agent-mcp-url>"
```

`Fabric:Enabled` を `true` にするのを忘れないでください（`appsettings.Development.json` または環境変数 `Fabric__Enabled=true`）。

## 6. Power BI で可視化する（セマンティックモデル）

運用コンソール（Rayfin）が読み書きしている Fabric SQL データベースは、そのまま
Power BI から使えます。Fabric SQL データベースは OneLake へ自動でミラーされ、
読み取り専用の **SQL 分析エンドポイント**が付いてくるためです。新しくデータの箱を
用意する必要はありません。

ただしセマンティックモデルは自動では作られません。ワークスペースを API で確認した
ところ 0 件でした（`GET /v1/workspaces/{id}/semanticModels`）。自分で作ります。

作るときも、テーブルを直接指すと困ります。Rayfin のエンティティ定義
（`fabric-app/rayfin/data/*.ts`）は数値も日時も `@text()`、つまり NVARCHAR で
持っているからです。コンソールは値をそのまま文字として描くのでこれで足りて
いるのですが、Power BI から見ると全列がテキストになり、

- `SUM` / `AVERAGE` がそのままでは書けない
- 時系列の軸に使えない（日付階層が生えない）
- 「未測定」を表す空文字が 0 に化ける

という三つの問題が出ます。三番目が特に厄介で、電力を測っていない機器の 0 と
本当に 0Wh だった機器を混ぜてしまいます。

そこで、型を戻したビューを挟みます。

```
Fabric SQL データベース（dbo.HouseholdSnapshots ほか / 全部テキスト）
        ↓  OneLake へ自動ミラー（テーブルのみ。ビューは運ばれない）
SQL 分析エンドポイント
        ↓  semantic-model-views.sql  ← TRY_CONVERT で型を復元
ビュー（dbo.v_Household / v_ActivityHourly / v_OutdoorHourly / v_Alert /
        v_AiRouterCall / v_Date）
        ↓  scripts/gen-semantic-model.py → scripts/deploy-semantic-model.ps1
セマンティックモデル（DirectQuery）→ Power BI レポート
```

### ビューは 2 か所に作る

ここが一番はまりやすいところです。**ビューは OneLake にミラーされません。**
ミラーされるのはベーステーブルだけです。実際に分析エンドポイント側の
`INFORMATION_SCHEMA.TABLES` を見ると、SQL データベースに作ったビューは
1 つも映っていませんでした。

したがって同じ `semantic-model-views.sql` を 2 つのエンドポイントに流します。

| 流す先 | 何のため |
| --- | --- |
| SQL データベース | 運用コンソールや直接の SQL 照会から使うため |
| SQL 分析エンドポイント | Power BI / セマンティックモデルから見えるようにするため |

分析エンドポイントは書き込み不可に見えますが、それはミラーされたテーブルの話で、
ビューは作れます（実測済み）。

分析エンドポイントのホスト名は、ポータルの Lakehouse → 設定 → SQL 分析
エンドポイントの接続文字列から取れます。`....datawarehouse.fabric.microsoft.com`
のようにドメインが `datawarehouse` になっているのが分析エンドポイント側、
`database` になっているのが SQL データベース側です。

### 手順

1. Fabric ポータルで対象の SQL データベースを開き、「新しいクエリ」に
   `fabric-app/scripts/semantic-model-views.sql` を貼って実行
   （`CREATE OR ALTER` なので何度流しても同じ結果になります）
2. 同じスクリプトを **SQL 分析エンドポイント**でも実行する
3. セマンティックモデルを配置する

   ```powershell
   python scripts/gen-semantic-model.py       # TMDL を生成
   pwsh ./scripts/deploy-semantic-model.ps1   # Fabric へ配置
   ```

   TMDL は手で書かずに生成します。ビューの列を足したり型を変えたりしたときに、
   モデル側の定義とずれるのを防ぐためです。

4. Power BI から「MimamoriTai」セマンティックモデルを選んでレポートを作る

ポータルの GUI で作りたい場合は、3 の代わりに 分析エンドポイント →
「新しいセマンティックモデル」で `v_` で始まる 6 つのビューを選び、`v_Date` を
「日付テーブルとしてマーク」してから、`v_Date[日付]` → `v_ActivityHourly[日付]`
`v_OutdoorHourly[日付]` `v_Alert[送信日]` に一対多のリレーションを張ります。
スクリプトはこれと同じ構成を TMDL で組み立てているだけです。

### DirectQuery にした理由

取り込み（Import）ではなく DirectQuery にしています。コンソールが数分おきに
データを書き換えるので、見るたびに再取り込みの完了を待ちたくないためです。
ビューの上にモデルを載せている以上、Direct Lake は使えません（Direct Lake は
Delta テーブルを直接読む方式で、ビューには効きません）。

### 日付テーブルを必ず作る理由

この作品の主題は「屋外の気温」と「家の中の電力」を重ね合わせることです。
`v_OutdoorHourly` と `v_ActivityHourly` は別のテーブルなので、共通の日付表を
経由しないと同じグラフに並びません。Power BI の自動日付テーブルはテーブルごとに
別々に作られるため、この用途には使えません。

`v_Date` は実データの最小日〜最大日から動的に作るので、データが増えれば自動で
伸びます。

### 空文字と NULL

同期側（`FabricSqlConsoleSync.Measure()`）は「測っていない」を空文字で書きます。
0 ではありません。0℃ は真冬のごく普通の観測値なので、欠測と混ぜられないためです。
`TRY_CONVERT` は空文字に対して NULL を返すので、この区別はビューでも保たれます。
Power BI の `SUM` / `AVERAGE` は NULL を無視するため、欠測が 0 として平均を
押し下げることもありません。

| Fabric SQL の値 | ビューでの値 | Power BI での扱い |
| --- | --- | --- |
| `'1450.5'` | `1450.50` | 集計対象 |
| `'0'` | `0.00` | 集計対象（真の 0） |
| `''`（欠測） | `NULL` | 集計から除外 |

### 作られるビュー

| ビュー | 元テーブル | 用途 |
| --- | --- | --- |
| `v_Household` | `HouseholdSnapshots` | 世帯ごとの現在の運用状況。1 世帯 1 行の上書きなので履歴分析には使えない |
| `v_Alert` | `AlertRecords` | 通知の記録。`失敗フラグ` で送信失敗率を出せる |
| `v_ActivityHourly` | `ActivityBuckets` | 機器の 1 時間ごとの動きと電力量。時系列の主役 |
| `v_OutdoorHourly` | `OutdoorReadings` | 気象庁アメダスと環境省 WBGT を時間単位に丸めたもの |
| `v_AiRouterCall` | `AiRouterCalls` | AI 呼び出しの回数・成功率・応答時間 |
| `v_Date` | 上記から生成 | 日付ディメンション |

### 注意

- Fabric のキャパシティが停止していると SQL 分析エンドポイントに接続できません。
  先にキャパシティを再開してください。
- ビューを直したら、**2 つのエンドポイントの両方**に流し直してください。片方だけ
  直すと、コンソールと Power BI で違う数字が出ます。
- 分析エンドポイントはミラーなので、当日ぶんの行数が SQL データベースより
  わずかに少ないことがあります（実測で 318 行 → 298 行）。異常ではなく
  反映待ちです。過去日の集計値は一致します。
- `v_Household` は現在値の上書きなので、世帯ごとの推移を追いたい場合は
  `v_ActivityHourly` を集計してください。

