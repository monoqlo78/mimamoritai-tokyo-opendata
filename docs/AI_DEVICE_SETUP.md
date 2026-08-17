# AI・デバイス連携セットアップ（Azure Model Router / SwitchBot / Fabric）

このドキュメントは、見守り隊の AI アシスタント連携を **Mock から実接続へ切り替えるために
ユーザーが手作業で用意する必要があるもの** を一覧化したものです。

アプリは「設定が入っていなければ Mock、入っていれば実接続」という設計です。
つまり **何も設定しなくても動きますが、その場合は全部ダミー応答** になります。
ダッシュボードの「連携状況」カードで、いま実接続か Mock かを確認できます。

---

## 0. 前提：シークレットは絶対にリポジトリへ書かない

`appsettings.json` には **キーの置き場所だけが空文字で用意されています**。
ここに直接キーを書くと git に載ってしまうため、必ず User Secrets か環境変数を使ってください。

```powershell
cd src\MimamoriTai.Web
dotnet user-secrets init   # 初回のみ（csproj に UserSecretsId は設定済み）
```

保存先は `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json` で、
リポジトリの外にあります。

---

## 1. Azure Model Router（LLM）

### 必要なもの

| 設定キー | 必須 | 説明 |
|---|---|---|
| `AzureModelRouter:Endpoint` | **必須** | Azure AI Foundry リソースのエンドポイント。空だと Mock にフォールバックします |
| `AzureModelRouter:ApiKey` | 条件付き必須 | `UseEntraId` が false のとき必須 |
| `AzureModelRouter:UseEntraId` | 任意 | `true` にするとキーの代わりに Microsoft Entra ID で認証します（本番の既定運用） |
| `AzureModelRouter:Deployment` | 任意 | 既定 `model-router`。Foundry 上のデプロイ名 |
| `AzureModelRouter:ApiVersion` | 任意 | 既定 `2024-10-21`。空にすると v1 形式のエンドポイントを使います |
| `AzureModelRouter:TimeoutSeconds` | 任意 | 既定 30。通常の呼び出しの予算 |
| `AzureModelRouter:FastTimeoutSeconds` | 任意 | 既定 10。締切のある経路（`-fast`）の予算（6-4） |

### 取得手順

1. [Azure AI Foundry](https://ai.azure.com/) でプロジェクトを開く。
2. **モデル カタログ**から `model-router` を選び、デプロイする。基盤モデルを個別に
   デプロイする必要はありません（ルーターが裏で選びます）。
3. デプロイのエンドポイントとキーをコピーし、以下を実行する。

```powershell
cd src\MimamoriTai.Web
dotnet user-secrets set "AzureModelRouter:Endpoint" "<endpoint>"
dotnet user-secrets set "AzureModelRouter:ApiKey" "<key>"
```

4. アプリを起動し、ダッシュボードの連携状況チップが `AI 接続済み` になることを確認する。

キーを使わず Microsoft Entra ID で認証する場合は、`AzureModelRouter:UseEntraId` を `true` に
し、実行 ID に Foundry リソースの **Cognitive Services User** ロールを付与してください。

### 動作確認（設定投入後）

```powershell
$body = @{ message = "今日のお母さんの様子を教えて" } | ConvertTo-Json -Compress
$bytes = [Text.Encoding]::UTF8.GetBytes($body)
$r = Invoke-WebRequest -Uri "http://localhost:5302/api/assistant/message" `
    -Method Post -Body $bytes -ContentType "application/json; charset=utf-8" -UseBasicParsing
[Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray())
```

応答 JSON の `router` が `Azure Model Router`、`resolvedModel` が**ルーターが実際に選んだ
基盤モデル名**になっていれば実接続です。
Mock のときは `router: "MockAiRouter"` / `resolvedModel: "mock/local-rules"` になります。

### モデルを固定していない理由

以前は用途ごとにモデルをピン留めしていました（JSON を要求する呼び出しは JSON 対応モデルへ、
締切のある呼び出しは速いモデルへ）。Model Router はリクエストごとに適切なモデルを
自分で選ぶため、**アプリ側でのピン留めは廃止**し、こちらは「待てる時間」だけを
宣言する形にしました（6-4）。

モデル選択がルーター任せになっても、**どのモデルが応答したかはレスポンスの `model`
フィールドで分かる**ため、`AiRequestLog` への記録とダッシュボード表示はそのままです。

### 障害時の挙動

- 429（レート制限）と 5xx は自動リトライします。`Retry-After` ヘッダーがあれば従います
  （`AzureModelRouter:MaxRetryDelaySeconds` で上限）。
- 4xx（こちらの投げ方の問題）と呼び出し側のキャンセルは再試行しません。
- 最終的に失敗しても **アプリは落ちず、日本語のメッセージを返します**。
  この場合モデル名は記録せず、コンソールでは「未応答（失敗）」として可視化します。
---

## 2. SwitchBot（家電操作）

### 必要なもの

| 設定キー | 必須 | 説明 |
|---|---|---|
| `SwitchBot:Token` | **必須** | 開発者トークン |
| `SwitchBot:Secret` | **必須** | HMAC-SHA256 署名用シークレット |

### 取得手順

1. スマホの SwitchBot アプリを開く。
2. 「プロフィール」→「設定」を開く。
3. **「アプリバージョン」を10回連続でタップ** する（開発者オプションが出現）。
4. 「開発者オプション」を開き、**トークン** と **クライアントシークレット** をコピーする。

```powershell
cd src\MimamoriTai.Web
dotnet user-secrets set "SwitchBot:Token" "<token>"
dotnet user-secrets set "SwitchBot:Secret" "<secret>"
```

より詳しい手順と、実機なしで送信内容を確認する方法は
[`SWITCHBOT_SETUP.md`](SWITCHBOT_SETUP.md) を参照してください。

### 世帯ごとの資格情報

SwitchBot は **世帯単位** でも設定できます（`SwitchBotConnectionService`）。
この場合は DataProtection で暗号化して DB に保存され、
`IDeviceProviderFactory` が世帯ごとに実接続と Mock を実行時に切り替えます。
つまり「A家は実機、B家はデモ」という混在が可能です。

---

## 3. Fabric Data Agent（任意）

使わない場合は設定不要です。未設定のときはローカル DB の
`ILocalDataQuestionService` が同じ質問に答えるので、機能は落ちません。

必要な設定は [`FABRIC_SETUP.md`](FABRIC_SETUP.md) を参照してください。
概略は以下の通りです。

| 設定キー | 説明 |
|---|---|
| `Fabric:WorkspaceId` | Fabric ワークスペース ID |
| `Fabric:DataAgentId` | Data Agent の ID |
| `Fabric:McpUrl` | MCP エンドポイント |
| `Fabric:TenantId` | Entra テナント ID |
| `Fabric:ClientId` | アプリ登録のクライアント ID |
| `Fabric:ClientSecret` | クライアントシークレット |

---

## 4. 安全設計：AI が家電を勝手に連発しないための仕組み

LLM に家電操作をさせる以上、暴走したときの被害を構造的に抑える必要があります。
以下は **設定不要で常に有効** です。

### 4-1. 実行前の確認（ヒューマン・イン・ザ・ループ）

状態を変える操作（ON / OFF / トグル）は **即座に実行されません**。
まず「〜します。よろしいですか？」と聞き返し、
ユーザーが「はい」と答えて初めて実行します。

実測例（ポート5302、Mock プロバイダ）:

```
> リビングの電気を消して
  reply: リビング照明 を消します。よろしいですか？（「はい」で実行、「いいえ」で中止）
  awaitingConfirmation: true
  deviceChanged: false        ← まだ何もしていない

> はい
  reply: リビング照明 を消しました。
  deviceChanged: true
```

拒否した場合:

```
> リビングの電気をつけて
  reply: リビング照明 をつけます。よろしいですか？…
> いいえ
  reply: リビング照明 の操作を中止しました。
  deviceChanged: false        ← 機器の状態は off のまま
```

補足:

- **状態を読むだけの操作（「ついてる？」）は確認不要** です。すぐ答えます。
- 確認は **3分で失効** します。放置した指示が後から実行されることはありません。
- 確認は **一度きり** です。続けて「はい」と言っても再実行されません。
- 「はい」「いいえ」以外を言った場合は **新しい指示として解釈** され、
  保留中の操作は承認されません。
- 「はい、やめて」のような曖昧な返事は **安全側に倒して拒否** と解釈します。

### 4-2. ホワイトリストと機器クラス

`DeviceSafetyPolicy` が以下を判定します（従来からの仕組み）。

- 遠隔操作が許可された機器か（`RemoteControlAllowed`）
- 機器の安全クラス（`Safe` / `Guarded` / `Restricted`）
- 意図解析の信頼度が閾値（0.85）以上か

暖房器具のような `Guarded` 機器は、**周囲の安全を確認する質問に「はい」と答えるまで通電しません**。
確認が取れて実際に ON になった場合は、世帯の全員に LINE で通知が飛びます。
機器ごとの設定で「遠隔でONにすることを禁止する」を選ぶと `Restricted` になり、
質問すら出さずに拒否されます（OFF は常に可能）。

### 4-3. 連発防止のレート上限

| 上限 | 値 | 目的 |
|---|---|---|
| 世帯あたりの状態変更 | **10分間に10回まで** | 家全体の暴走を止める |
| 同一機器・同一操作の反復 | **2分間に3回まで** | 同じスイッチの連打を止める |
| 状態を読む操作 | **無制限** | 監視機能を阻害しない |

重要な設計判断として、**カウントするのは実際に成功した状態変更だけ** です。
拒否された試行はカウントしません。そうしないと、AI が誤った指示を繰り返しただけで
家族が自宅から締め出されてしまいます。

上限に達したときの応答:

```
安全のため、10分間に操作できる回数の上限（10回）に達しました。少し時間をおいてから試してください。
同じ操作が短時間に繰り返されています。安全のため一度お休みします。
```

---

## 5. 異常検知と家族への通知

異常かどうかの判定は **LLM に任せていません**。`RiskAssessmentService` が決定論的な
ルールでスコアリングし、LLM は結果の言い回しを整えるだけです。
モデルの機嫌で「異常なし」と言われては見守りになりません。

### 検知するもの

| 検知内容 | 条件 | 加点 |
|---|---|---|
| 家電の利用がない | 普段の起床＋2時間（最遅10時）を過ぎても0回 | +60 |
| 活動開始が遅い | 初回利用が10時以降 | +35 |
| 深夜の活動 | 0〜5時に2回以上 | +30 |
| 活動量の低下 | 直近平均の40%以下 | +25 |
| **電気つけっぱなし（暖房系）** | ストーブ/ケトル/調理器が**2時間**以上ON | **+60** |
| **電気つけっぱなし（照明等）** | 深夜は**4時間**、日中は**12時間**以上ON | +20 |

重要度は合計スコアで決まります（60以上=High、25以上=Medium、それ未満=Low）。

つけっぱなし検知の設計判断:

- **暖房系は単独で High** になります。火災の恐れがあるため、他の兆候を待ちません。
- 照明が何個ついていても **加点は最悪の1件だけ** です。
  部屋数の多い家で誤って High にならないようにするためです。
- 暖房系と照明が同時に該当した場合は **暖房系を優先して報告** します。

### 通知

`WatchAlertService` がリスクを評価し、閾値（既定 Medium）以上なら LINE で家族に通知します。

- 同一人物・同一リスクレベルの通知は **6時間クールダウン** され、連投しません。
- 通知文は Azure Model Router が設定済みなら LLM が自然な日本語に整えます。
  **失敗しても定型文で必ず送信** されるため、通知が LLM に依存することはありません。

---

## 6. 現在の検証状況（実キー投入後・実測）

実測した項目は「実測」、ドキュメントで確認しただけの項目は「確認」と書き分けています。推測は含みません。

| 項目 | 状態 |
|---|---|
| Azure Model Router のエンドポイント形式 / 認証方式 | 公式ドキュメントで **確認済み**（`docs/REFERENCES.md`） |
| モデル選択のピン留め廃止（`JsonModel` / `FastModel`） | **移行済み**（ルーターに委譲。6-4） |
| 429 / `Retry-After` / 再試行方針 | **実装・テスト済み**（`AzureModelRouterClientTests` 19件） |
| Azure Model Router の実デプロイによる応答 | **実測済み**（Azure AI Foundry に `model-router` をデプロイし、APIキー経路・Entra ID 経路の両方で応答を確認。下記 6-6） |
| ルーターによるモデル自動選択 | **実測済み**（同一エンドポイントへの呼び出しで `gpt-5-mini` / `gpt-5.4-mini` / `gpt-oss-120b` が選ばれ、レスポンスの `model` に実モデル名が返ることを確認） |
| `response_format: json_object`（意図解析経路） | **実測済み**（ルーター経由で JSON が返ることを確認） |
| SwitchBot の署名生成・送信内容 | **テストで実証済み**（秘匿値はマスク） |
| SwitchBot 実機のデバイス一覧・状態取得 | **実測済み**（Plug Mini 1台を検出） |
| SwitchBot 実機の電源 OFF → 復元 | **実測済み**（`power on → off → on`） |
| 確認フロー / レート上限 | **実装・テスト・実機実測すべて完了** |
| つけっぱなし検知 | **実装・テスト済み**（7件） |
| LLM による状況要約経路 | **経路として実測済み**（旧ルーター構成での計測。下記参照） |
| Fabric Data Agent の疎通 | **HTTP 200 まで到達。ただしデータソース未到達**（下記 6-3） |

### 6-1. 実測ログ（抜粋）

```
[LIVE] router=auto resolvedModel=qwen3.7-plus 11682ms
[LIVE] jsonModel=gpt-4.1-mini-2025-04-14 raw={"intent":"control_device","action":"turn_off"}
[LIVE] device id=8CFD49F79C92 name=プラグミニ 92 type=Plug
[LIVE] awaitingConfirmation=True deviceChanged=False
[LIVE] propose=プラグミニ 92 を消します。よろしいですか？（「はい」で実行、「いいえ」で中止）
[LIVE] execute=プラグミニ 92 を消しました。
[LIVE] power before=True after=False
[LIVE] summary=お母様は今朝7時頃から活動を始められ、家電も2回ご利用になっています。
              普段と変わらないリズムで過ごされているようで、安心しました。
```

### 6-2. 実接続テストの動かし方

実 API を叩くテストは既定では動きません。環境変数で明示的に有効化します。

```powershell
# 読み取りのみ（LLM 応答・デバイス一覧・状態取得・要約）。実機は操作しない
$env:MIMAMORI_LIVE = "1"
dotnet test --filter "FullyQualifiedName~LiveIntegrationTests"

# 実機の電源を実際に落とす検証まで行う場合（テスト終了時に元の状態へ復元します）
$env:MIMAMORI_LIVE_CONTROL = "1"
```

環境変数が無いときは各テストが即座に return するため、オフラインでも CI でも
テストは緑のままです。

### 6-3. Fabric Data Agent の既知の制約（要対処）

MCP エンドポイントへの認証・疎通は成功しており **HTTP 200 が返ります**。
しかし Data Agent 自身が自分のデータソースに到達できておらず、200 のまま
日本語の謝罪文を返してきます。実測した生の応答は以下です。

```
家電の利用状況をお調べしようとしましたが、技術的な問題で最新のデータを
取得できませんでした。ご不便おかけし申し訳ありません。
```

アプリ側はこれを失敗として検出し、**ローカル DB（`ILocalDataQuestionService`）へ
自動フォールバック**します。したがって家族には正常な回答が返り、機能は落ちません。

これは Fabric 側の構成の問題で、アプリのコードでは直せません。以下を確認してください。

1. Data Agent に紐づくデータソース（Lakehouse / Warehouse / KQL DB）が、
   **サービスプリンシパル認証に対応した種類** になっているか。
   セマンティックモデル経由などサービスプリンシパル非対応の構成だと、
   認証は通るのにクエリだけ失敗します。
2. `Fabric:ClientId` のサービスプリンシパルに、ワークスペースと
   データソース両方の読み取り権限が付いているか。
3. Fabric テナント設定で「サービス プリンシパルによる Fabric API の使用を許可する」
   が有効か。

失敗時は生の応答先頭300文字が警告ログに出るので、原因の切り分けに使えます
（この文言は家族の画面には出ません）。

### 6-4. 応答速度と LINE の 8 秒制限（重要）

LINE の webhook 処理は **1 イベントあたり 8 秒**でキャンセルされます
（`WebhookEndpoints.EventProcessingTimeout`）。これを超えると、内部で正しい要約が
できていても家族には定型のタイムアウト文しか届きません。したがって応答速度は
「体感の好み」ではなく **LINE で機能が成立するかどうかの分かれ目**です。

自動ルーティングを使う以上、**遅いモデルを引く可能性は消せません**。実測でも、同一
プロンプトの状況要約が速いときは数秒、推論（thinking）系に解決されると数十秒かかる
という分散を観測しています。平均ではなく分散が問題です。

そこで**モデルではなく「待てる時間」を経路ごとに宣言します**。

| 経路 | `purpose` | 予算 | 理由 |
|---|---|---|---|
| LINE webhook | `summary-fast` | `FastTimeoutSeconds`（既定 10 秒） | 8 秒の締切がある |
| Web UI / API | `summary` | `TimeoutSeconds`（既定 30 秒） | 締切が無く、自動ルーティングの利点を活かす |
| 意図分類（JSON） | `intent` | `TimeoutSeconds` | `response_format: json_object` を要求 |

```json
"AzureModelRouter": { "TimeoutSeconds": 30, "FastTimeoutSeconds": 10 }
```

なお `-fast` は「締切がある呼び出し」を表す接尾辞で、チャネル名ではありません。
他の用途にも同じ規則で展開できます（例 `intent-fast`）。

予算はリクエストごとに `CancellationTokenSource` で切っており、超過したぶんの再試行を
含めても呼び出し側の締切を食い潰しません。予算内に返らなかった場合はローカル DB から
作った定型の答えにフォールバックするため、**要約が空のまま返ることはありません**。

#### Fabric の待ち時間予算（`Fabric:QueryTimeoutSeconds`、既定 2 秒）

要約・データ質問の経路は、**先にローカル DB から完全な答えを作ってから**
Fabric Data Agent に問い合わせ、成功すればそれを併記します。つまり Fabric は
「あれば嬉しい追加情報」であって前提ではありません。

以前はこの Fabric 呼び出しに上限が無く（HTTP 側の 120 秒まで待つ）、Fabric 単独で
19〜20 秒かかっていたため、**手元に正しい答えがあるのに呼び出し側の 8 秒を
使い切って答えごと失われる**という不具合がありました。現在は予算を超えると
Fabric を諦めてローカルの答えを返します。

#### Fabric を呼ぶ質問の限定（`scope`）

予算を切っても、Fabric に聞く価値の無い質問で 2 秒を捨てるのは無駄です。
そこで意図分類の JSON に `scope` を追加し、**分析・集計・期間をまたぐ質問
（`analysis`）のときだけ** Fabric を呼びます。「今どう?」「今日の様子は?」の
ような直近状態の質問（`recent`）はローカル DB だけで即答します。

- 判定は**既存の意図分類 1 往復の中で**行うため、LLM の呼び出しは増えません。
- `scope` が欠落・不正値のときは `recent` に倒します。取りこぼしても失うのは
  追加情報だけですが、逆に倒すと全質問が予算を消費するためです。

エンドツーエンドの実測（`POST /api/assistant/message`、要約）:

| 構成 | 実測 | 8 秒予算 |
|---|---|---|
| `auto` + Fabric 無制限（修正前） | 19〜51 秒 | 超過 |
| `mini` + Fabric 4 秒 | 6.9〜8.1 秒 | ぎりぎり／たまに超過 |
| **`mini` + Fabric 2 秒（現在の LINE 既定）** | **4.8〜6.9 秒** | 収まる |

Fabric がデータソースに到達できるようになり、待つ価値が出たら
`Fabric:QueryTimeoutSeconds` を上げてください。ただし
「要約全体 ≦ 8 秒」を守れる範囲に留める必要があります。

### 6-5. SwitchBot 実機の構成に関する注意

検証に使ったアカウントに登録されている操作可能な機器は
**Plug Mini 1台（`プラグミニ 92`）のみ** です。照明やエアコンは登録されていません。

「家の電気を消して」というデモを行う場合は、**照明のプラグを Plug Mini に挿して**
Plug Mini の名前を「リビングの電気」等に変更してください。アプリは機器の別名
（`Device.Alias`）で照合するため、名前を合わせれば自然文で操作できます。

なお `Plug` は安全区分 `Guarded` に分類されており、**電源 OFF は常に許可、
ON と Toggle は「周囲に燃えやすいものや、動いていると危ないものはありませんか？」
という確認に答えてから**実行されます。何が挿さっているか分からない以上、
最悪のケース（ヒーター等）を想定する設計です。ONになった場合は世帯の全員に通知されます。
特定のプラグを絶対に遠隔ONさせたくない場合は、機器の設定画面で
「遠隔でONにすることを禁止する」にチェックを入れてください。

### 6-6. Azure Model Router の実デプロイ実測

Azure AI Foundry（`AIServices`, japaneast）に `model-router` をデプロイし、実際に応答することを確認しました。
`model-router` は japaneast では提供されていますが **japanwest では提供されていません**（`az cognitiveservices model list` で確認）。

確認できたこと。

| 確認項目 | 結果 |
|---|---|
| APIキー（`api-key` ヘッダ）での呼び出し | 応答あり |
| Entra ID（`Authorization: Bearer`、スコープ `https://cognitiveservices.azure.com/.default`）での呼び出し | 応答あり |
| レスポンス `model` に実モデル名が返るか | 返る。呼び出しごとに `gpt-5-mini` / `gpt-5.4-mini` / `gpt-oss-120b` と変化した |
| `temperature` の指定 | 400 にならず受理された |
| `response_format: json_object` | JSON が返った（意図解析経路がそのまま使える） |

**注意点（実測で判明）。** ルーターは推論モデル（`gpt-5-mini` など）を選ぶことがあります。
推論モデルは `max_tokens` を推論トークンでも消費するため、`max_tokens` を小さく指定すると
本文が空文字で返ります。本実装は `max_tokens` を送らずモデル既定に任せているため影響を受けませんが、
将来 `max_tokens` を足す場合はこの挙動に注意してください。

本番（App Service）側は、キーを Key Vault に置かず **Entra ID 認証**にしています。
Key Vault が Private Endpoint 専用構成でネットワーク規則を変更したくなかったためです。
アプリ設定は `AzureModelRouter__Endpoint` / `AzureModelRouter__Deployment` / `AzureModelRouter__UseEntraId=true` の 3 つで、
App Service のシステム割り当てマネージド ID と Fabric 用サービスプリンシパルの双方に、
この AI リソース**だけ**をスコープとした `Cognitive Services User` ロールを付与しています
（アプリは Fabric が有効なとき Fabric 側の資格情報を共有するため、両方に必要です）。

### ユーザー側に残っている手作業

1. Fabric Data Agent のデータソースをサービスプリンシパル対応の構成に直す（6-3）。
   直さない場合もローカル DB へフォールバックするため、デモは実施可能です。
2. デモで照明を操作したい場合、照明を Plug Mini に挿して別名を合わせる（6-5）。
3. （対応済み）本番（App Service）の Azure Model Router 接続設定。Entra ID 認証で構成済みです（6-6）。


