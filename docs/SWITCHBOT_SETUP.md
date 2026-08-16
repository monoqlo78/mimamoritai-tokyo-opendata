# SwitchBot API セットアップ ＆ 実装状況

## 0. 本番運用: 世帯ごとにWeb UIから接続する（推奨）

**本番環境では、Token/SecretはApp Serviceの設定や `dotnet user-secrets` には入れません。** 各世帯のオーナーが、ログイン後に **「LINE連携設定」画面（`/settings/switchbot`）** で自分のSwitchBot Token/Secretを直接入力します。入力された値は保存前に `GET /v1.1/devices` で疎通確認され、成功した場合のみ ASP.NET Core Data Protection で暗号化された状態で世帯ごとに（`SwitchBotConnection` テーブルへ）保存されます。平文はデータベースにもログにも一切残りません。詳細は `docs/SECURITY.md`「世帯ごとのSwitchBot認証情報」を参照してください。

以下の「1. SwitchBotアプリでToken/Secretを取得する」の手順自体は、本番でもローカル開発でも同じです（取得したToken/Secretの**入力先**が異なるだけです）。

## 1. SwitchBotアプリでToken/Secretを取得する

1. スマートフォンの **SwitchBot アプリ** を開きます。
2. 「プロフィール」タブ →「設定」（Preferences）を開きます。
3. 「App Version」の項目を **10回前後連続でタップ** します（開発者向け画面が有効化されます。タップ回数は実機・アプリバージョンによって多少前後することがあります）。
4. 表示された「Developer Options（開発者向けオプション）」を開き、以下を取得します。
   - **Token**
   - **Secret**

**本番（各世帯のオーナー）**: 上記で取得したToken/Secretを、ログイン後に開く「LINE連携設定」画面（`/settings/switchbot`）にそのまま貼り付けて保存してください。

**ローカル開発（グローバル/ブートストラップ用の唯一の経路）**: 開発時に世帯ごとの設定UIを経由せず素早く動作確認したい場合のみ、以下のUser Secretsを使えます。

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "SwitchBot:Token" "<your-switchbot-token>"
dotnet user-secrets set "SwitchBot:Secret" "<your-switchbot-secret>"
```

`SwitchBot:Enabled` を `true` にすることも忘れないでください（`appsettings.Development.json` または環境変数 `SwitchBot__Enabled=true`）。ポーリング間隔（既定5分）を変えたい場合は `SwitchBot:PollIntervalMinutes`（環境変数なら `SwitchBot__PollIntervalMinutes`）を設定してください。

このグローバル設定は、**`SwitchBot:AllowGlobalFallback=true` のときのみ**、かつ対象世帯に世帯ごとの `SwitchBotConnection` が1件も保存されていないときのみフォールバックとして使われます（`HouseholdSwitchBotClientFactory` の優先順位: ① 世帯ごとの暗号化済み接続 → ②（`AllowGlobalFallback=true` の場合のみ）グローバル `SwitchBotOptions` → ③ 未設定）。既定値は `false` で、本番向けの設定（`appsettings.json` 等）では明示的に有効化しない限りこのフォールバックは効きません。`appsettings.Development.json` でのみ `true` にすることを想定しています。

## 2. v1.1 の署名方式

SwitchBot OpenAPI v1.1 は、リクエストごとに以下の値を計算してヘッダーに付与する方式で認証します（`src/MimamoriTai.Infrastructure/Devices/SwitchBotClient.cs` の `ApplyAuthHeaders` に実装済み）。

1. `t`: 現在時刻のUnixミリ秒（文字列）
2. `nonce`: ランダムなGUID（ハイフン無し文字列）
3. `payload = token + t + nonce` を作成
4. `sign = Base64( HMACSHA256(secret, payload) )` を計算
5. リクエストヘッダーに以下を設定:
   - `Authorization: <token>`
   - `sign: <sign>`
   - `t: <t>`
   - `nonce: <nonce>`

エンドポイントのベースURLは `https://api.switch-bot.com`（`SwitchBotOptions.BaseUrl`）です。

## 3. 実装済みの範囲

`ISwitchBotClient`（`SwitchBotClient`）は、認証ヘッダーの付与とHTTP送受信のみを実装しており、以下の3メソッドは **生のJSON文字列をそのまま返します**（レスポンスDTOへのマッピングは `SwitchBotDeviceProvider` が行います）。

- `GetDeviceListRawAsync()` — `GET /v1.1/devices`
- `GetDeviceStatusRawAsync(deviceId)` — `GET /v1.1/devices/{deviceId}/status`
- `SendCommandRawAsync(deviceId, command, parameter, commandType)` — `POST /v1.1/devices/{deviceId}/commands`

`SwitchBotDeviceProvider`（`src/MimamoriTai.Infrastructure/Devices/SwitchBotDeviceProvider.cs`）は上記の生JSONを、公式仕様（[OpenWonderLabs/SwitchBotAPI](https://github.com/OpenWonderLabs/SwitchBotAPI)、README.mdおよび `devices/*.md`）で確認した以下のレスポンス形状に基づいてマッピングします。

- 共通エンベロープ: `{ "statusCode": 100, "message": "success", "body": {...} }`。`statusCode` が100以外（`401 Unauthorized`、`190 System error` 等）は失敗として扱われ、例外は投げません。
- `GET /v1.1/devices` の `body` には物理デバイス `deviceList`（`deviceId`, `deviceName`, `deviceType`, `hubDeviceId` 等）と、Hub経由の赤外線リモコン `infraredRemoteList`（`deviceId`, `deviceName`, `remoteType`, `hubDeviceId`）の2つの配列があり、両方をマッピングします（高齢者宅では照明や扇風機がHub経由の赤外線リモコンであることが多いため）。
- `GET /v1.1/devices/{id}/status` の `body` は機種ごとにフィールドが異なります（例: Botの `power`（ON/OFF文字列）、Motion Sensorの `moveDetected`（真偽値）、Contact Sensorの `openState`（`open`/`close`/`timeOutNotClose`）、Plug Mini (JP) は `power` フィールドが**存在せず** `electricCurrent`（mA）と `weight`（1日の消費電力量、W）から状態を推定）。
- コマンド送信は `{"command":"turnOn"/"turnOff","parameter":"default","commandType":"command"}`。`toggle` は機種（例: Bot）によっては対応コマンドが無いため、`ToggleAsync` は現在の状態を取得してから逆の明示コマンドを送信します。

デバイス種別（SwitchBotの `deviceType`/`remoteType` 文字列）は `MimamoriTai.Core.Domain.DeviceType` にマッピングされます。マッピング表に無い機種（Hub、Curtain、Meter、Lock、Robot Vacuumなど）は `DeviceType.Unknown` にフォールバックし、`DeviceSafetyPolicy` により自動的に `Restricted`（安全側）として扱われます。

## 4. 実機データをアプリへ反映する
1. `SwitchBot:Enabled=true` とToken/Secretを設定して起動すると、`IDeviceProvider` が `SwitchBotDeviceProvider` に切り替わります。
2. ダッシュボードの「実機を同期」ボタン（または `POST /api/devices/sync`）を押すと、`DeviceSyncService` が実機の機器一覧を取得し、`Devices` テーブルへ反映します（新規は追加、既存は名前/種別/部屋を更新、実機側から消えた機器は削除せず無効化）。同期は冪等で、変化が無ければ2回目の実行は何も変更しません。
3. 同期後は `SwitchBotPollingBackgroundService` が既定5分間隔（`SwitchBot:PollIntervalMinutes`）で各機器のステータスをポーリングし、ON/OFF・人感・開閉の変化を検知したときだけ `DeviceEvent`（`Source=SwitchBotPoll`）を記録します。状態が変わらない限り重複イベントは作成されません。加えて、既定60分間隔（`SwitchBot:DeviceDiscoveryIntervalMinutes`）で機器一覧（`GET /v1.1/devices`）も自動で再取得し、SwitchBot側で追加された新しい機器（例: プラグミニの2台目）を自動でDevicesテーブルへ追加します。手動の「今すぐ同期する」と同じ`DeviceSyncService`を再利用しますが、消えた機器を無効化（`IsActive=false`）する処理は行いません（一時的なAPI障害で機器が一覧から欠落しただけかもしれないため）。実機側で本当に機器を削除した場合は、引き続き手動同期（「今すぐ同期する」）で反映してください。
4. 同期は機器を発見するだけで、遠隔操作の許可（`RemoteControlAllowed`）は自動では付与されません。安全のため、AIチャット/LINEからの操作を許可する機器は運用者が個別に設定してください。
5. `/webhooks/switchbot`（`src/MimamoriTai.Web/Endpoints/WebhookEndpoints.cs`）はSwitchBot Webhookのコールバックを受信し、`SwitchBotWebhookIngestService` が `DeviceEvent`（`Source=SwitchBotWebhook`）と `PlugMiniReading` を記録します。ポーリングは**そのまま併存**します（後述の理由）。登録は下記「6.」を参照してください。

## 5. Webhook（プッシュ受信）を登録する

**なぜ必要か。** SwitchBotクラウドの `status` APIは、**デバイスから最後に受け取った値をそのまま返します**。プラグが報告を止めてもポーリングは成功し続け、同じ値が返り続けます。実際に本番環境で、電圧 103.4V・電流 140mA・通電時間 120分という**まったく同じ値が10時間・123回にわたって保存されました**。ポーリングだけでは「変化のない家」と「黙ったプラグ」を区別できません。Webhookはデバイスが実際に報告したときにだけ届くため、沈黙は沈黙のまま（＝データの空白として）現れます。

登録はSwitchBot APIへ1回POSTするだけです（`Token`/`Secret` の署名は `status` API と同じ手順）。

```http
POST https://api.switch-bot.com/v1.1/webhook/setupWebhook
{
  "action": "setupWebhook",
  "url": "https://<あなたのホスト>/webhooks/switchbot",
  "deviceList": "ALL"
}
```

確認は `queryWebhook`（`{"action":"queryUrl"}`）、変更は `updateWebhook`、解除は `deleteWebhook` です。

**ポーリングを残す理由。**

- Webhookは**変化があったときだけ**送られます。負荷が一定の家電は何時間も何も送らないため、定期的な生存確認にはなりません。
- Webhookの登録URLはアカウント全体で1つです。他システムに向け替えられると無言で止まります。
- 公式仕様が本文の実例を載せているのはBotとCurtainだけで、機種によっては `powerState` のみが届きます。その場合は状態のみを記録し、**計測値を捏造しません**。

両者は同じ `(世帯, 機器, 時刻)` の一意性で重複排除されるため、先に届いたほうが採用され、もう一方は無視されます。フィールド名と単位は `status` API と同一として扱います（`voltage`=V、`electricCurrent`=mA、`weight`=**その瞬間の実電力W**、`electricityOfDay`=使用分数）。**`voltage × electricCurrent` から電力を計算してはいけません**——それは皮相電力(VA)で、力率の低い負荷では2桁ずれます。

## 6. 実機なしで「送信内容」を確認する

実機やアカウントが無い段階でも、アプリがSwitchBotへ**実際に何を送るのか**を確認できます。`SwitchBotClient` は送信直前に、URL・ヘッダー・ボディを `Information` レベルでログ出力します。**Token と sign（署名）は必ず `***(len=N)` にマスク**されるため、ログを共有しても資格情報は漏れません。

以下のテストを実行すると、実際のログ行が出力されます（`tests/MimamoriTai.Tests/SwitchBotClientTests.cs`）。

```powershell
dotnet test --filter "FullyQualifiedName~Outgoing_log_shows" --logger "console;verbosity=detailed"
```

実際の出力（2026-08-11 実測、ダミー資格情報を使用）:

```text
SwitchBot -> POST https://api.switch-bot.com/v1.1/devices/01-202410-12345678/commands | headers: Authorization: ***(len=27), sign: ***(len=44), t: 1786413337412, nonce: 6208875eb1b047a2be646fe29d2e04d7, Accept: application/json | body: {"command":"turnOff","parameter":"default","commandType":"command"}
SwitchBot <- 200 POST /v1.1/devices/01-202410-12345678/commands (48 bytes)
```

このテスト一式では、以下も自動で検証しています。

- 署名が `base64(HMACSHA256(secret, token + t + nonce))`（32バイト＝Base64で44文字）であること
- `t` がUnixミリ秒であり、実時刻とのずれが1分未満であること
- `nonce` がハイフン無し32文字のGUIDであり、**リクエストごとに署名が変わる**こと
- 失敗レスポンス時の例外メッセージにTokenもSecretも含まれないこと
- 未設定時はHTTP送信そのものが行われないこと

実機を接続したあと、同じ形式のログが `dotnet run` のコンソールに出ます。「リビングの電気を消して」が実機まで届いたかは、`SwitchBot -> POST .../commands ... "command":"turnOff"` と、直後の `SwitchBot <- 200` で確認してください。

## 7. デモ環境での代替

実機が無い間は `MockDeviceProvider`（`src/MimamoriTai.Infrastructure/Devices/MockDeviceProvider.cs`）がインメモリで5台の擬似デバイス（リビング照明・寝室照明・扇風機・電気ストーブ・エアコン）を提供し、認証情報を一切必要としません。電気ストーブとエアコンは `SafetyClass.Guarded` に分類される機器で、AIからのON操作に周囲の安全確認がはさまることを実演するために含まれています。ダッシュボードの表示・自然言語操作・安全ガードレールのデモはすべてこのモックで完結します。
