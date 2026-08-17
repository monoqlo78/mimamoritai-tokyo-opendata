# セキュリティ方針

## 秘密情報の取り扱い

- **すべての秘密情報（APIキー、トークン、接続文字列）は `dotnet user-secrets` またはホスティング環境の環境変数／シークレットストアからのみ供給します。**
- **本番（Azure App Service）では Azure Key Vault を唯一のシークレット供給元とし、App Service のアプリケーション設定に秘密情報を1件も置きません。**（下記「本番のシークレット供給（Key Vault + マネージドID）」参照）
- `src/MimamoriTai.Web/appsettings.json` および `appsettings.Development.json` には、対象キー（`ConnectionStrings:AppDb`, `AzureModelRouter:ApiKey`, `Line:ChannelAccessToken`, `Line:ChannelSecret`, `Line:AlertToId`, `SwitchBot:Token`, `SwitchBot:Secret`, `Fabric:WorkspaceId`, `Fabric:DataAgentId`, `Fabric:McpUrl`）は**空文字列のプレースホルダーとしてのみ**存在し、実際の値をコミットしてはいけません。
- 各オプションクラス（`AzureModelRouterOptions`, `SwitchBotOptions`, `LineOptions`, `FabricOptions`）は `IsConfigured` プロパティを持ち、必須項目が埋まっていない場合は自動的にモック実装へフォールバックします（`ServiceCollectionExtensions.cs`）。これにより、秘密情報が無い状態で誤って実サービスへ接続しようとすることを防いでいます。
- Microsoft Fabricの認証は、コード内に静的なシークレットを置かず `Azure.Identity`（`DefaultAzureCredential`）を利用します（`docs/FABRIC_SETUP.md` 参照）。同じマネージドIDで Key Vault からシークレットを読み出します。
- **重要: ローカル開発の User Secrets（`SwitchBot:Token`/`SwitchBot:Secret` など）は本番環境へ一切転記・移行しません。** 本番では各世帯のオーナーが自分のSwitchBot Token/Secretを「LINE連携設定」画面（`/settings/switchbot`）から個別に入力し、世帯ごとに暗号化して保存します（下記「世帯ごとのSwitchBot認証情報」参照）。User Secretsのグローバル `SwitchBot:Token`/`Secret` はローカル開発のブートストラップ専用の経路として残していますが、`SwitchBot:AllowGlobalFallback=true` を明示しない限り本番相当の設定では使われません。

## 本番のシークレット供給（Key Vault + マネージドID）

本番の App Service には、秘密情報を1件も置きません。アプリは起動時に Azure Key Vault を構成プロバイダーとして追加し、そこからすべての秘密情報を読み込みます。

- **実装**: `AddMimamoriTaiKeyVault()`（`src/MimamoriTai.Web/Services/KeyVaultConfigurationExtensions.cs`）を `Program.cs` の先頭で呼び出します。`KeyVault:Uri` が設定されているときだけ `DefaultAzureCredential` で `SecretClient` を作り、`builder.Configuration.AddAzureKeyVault()` で構成に重ねます。
- **認証はパスワードレス**: App Service のシステム割り当てマネージドIDに Key Vault の `Key Vault Secrets User` ロールを付与しています。**Key Vault へ接続するための資格情報そのものが存在しません。** ローカル開発では同じ `DefaultAzureCredential` が開発者のログイン（Azure CLI / Visual Studio）を拾います。
- **ゼロコンフィグは維持**: `KeyVault:Uri` が空（`appsettings.json` の既定）ならプロバイダーは追加されず、何も起きません。`git clone` して `dotnet run` するだけでモック実装で全機能が動く、という前提は変わりません。
- **シークレット名の変換規則**: Key Vault のシークレット名にはコロンを使えないため、既定の `KeyVaultSecretManager` は **`--` を構成階層の `:` に読み替えます**。つまり `AzureModelRouter:ApiKey` に対応するシークレット名は **`AzureModelRouter--ApiKey`** です（App Service のアプリ設定で使う `__` とは別の記法なので注意）。
- **反映**: `ReloadInterval` は30分です。シークレットをローテーションしても、再デプロイなしで最大30分後に反映されます。
- **監査**: Key Vault 側にアクセスログが残るため、「どのIDがいつどのシークレットを読んだか」を後から追跡できます。アプリ設定に平文で置く方式では得られない性質です。

### Key Vault へは Private Endpoint 経由で到達する

ハッカソン環境のテナントには **Key Vault の `publicNetworkAccess` を `Disabled` に書き換えるガバナンスポリシー**（`keyvault_publicnetwork_modify`）が効いています。実際にこれが発動し、起動時の Key Vault 読み込みが `403 Forbidden`（"Public network access is disabled and request is not from a trusted service nor via an approved private link"）で失敗、プロセスが異常終了（exit code 134）して App Service がサイト全体を 503 でブロックする、という事故が起きました。

対処として、公開アクセスを開け直すのではなく **Private Endpoint 方式**に切り替えています。ポリシーが再び公開アクセスを閉じても影響を受けません。

| リソース | 役割 |
| --- | --- |
| `vnet-mimamoritai`（10.20.0.0/16） | 専用の仮想ネットワーク |
| `snet-appsvc`（10.20.1.0/24） | App Service の VNet 統合用（`Microsoft.Web/serverFarms` に委任） |
| `snet-pe`（10.20.2.0/24） | Private Endpoint 用 |
| `pe-kv-mimamoritai` | Key Vault への Private Endpoint |
| `pe-sql-mimamoritai` | Azure SQL への Private Endpoint |
| `privatelink.vaultcore.azure.net` / `privatelink.database.windows.net` | 上記 VNet にリンクした Private DNS ゾーン |

設計上の判断:

- **App Service は Key Vault の "信頼された Microsoft サービス" ではありません。** そのため `bypass=AzureServices` では通らず、IP 許可か Private Endpoint のどちらかが必須です。IP 許可は App Service の送信元 IP が変わると壊れるので採用していません。
- **`vnetRouteAllEnabled` は `false`** にしています。`true` にすると全アウトバウンドが VNet 経由になり送信元 IP が変わるため、共有の SQL サーバーや外部 API 側の許可設定を壊す恐れがあります。`false` なら **RFC1918 宛（＝Private Endpoint 宛）だけ**が VNet を通り、他は従来どおりです。DNS はこの設定に関係なく VNet の設定が使われるので、Private DNS ゾーンをリンクしておけば名前解決は効きます。
- **SQL サーバーは他案件と共有**しているため、サーバー側の `publicNetworkAccess` やファイアウォールは一切変更していません。**Private Endpoint リソースだけを本プロジェクトのリソースグループ側に作成**しており、共有サーバーには承認済みの接続が1件増えるだけです。
- **Key Vault が読めなくても起動は止めません。** 初回ロードは `AddMimamoriTaiKeyVault()` の中で `try/catch` して警告を出し、起動を継続します。秘密情報が必要な機能は個別に無効化されるので、サイト全体を落とすより安全です（上記の 503 はこの穴が原因でした）。

### Microsoft Fabric を Private Link にしなかった理由

Fabric にも Private Link はありますが、**採用していません**。テナントレベルの Private Link はテナント全体の Fabric アクセスに影響し、ワークスペースレベルでも前提としてテナント設定「ワークスペースレベルの受信ネットワーク規則を構成する」を Fabric 管理者が有効化する必要があります。本プロジェクトは**共有テナントの一利用者**であり、他の利用者に影響する設定を変更しない方針のため、ここは意図的に既定（公開アクセス）のままにしています。Fabric 側のデータ保護は、ワークスペースのロール割り当てとサービスプリンシパルの権限で担保しています。


## 世帯ごとのSwitchBot認証情報（Data Protectionによる暗号化）

各世帯のSwitchBot Token/Secretは、`SwitchBotConnection` エンティティ（`Core/Domain/Entities.cs`）に **平文では一切保存されません**。保存される列は `EncryptedToken`/`EncryptedSecret`（保護済みブロブ文字列）のみです。

- **暗号化の実装**: `ICredentialProtector`（`Core/Abstractions/ICredentialProtector.cs`）の唯一の本番実装 `DataProtectionCredentialProtector`（`Infrastructure/Security/DataProtectionCredentialProtector.cs`）が、ASP.NET Core Data Protection（`IDataProtectionProvider.CreateProtector(purpose)`）をラップします。
- **purpose文字列**: `"MimamoriTai.SwitchBotCredentials.v1"` に固定しています。**この文字列は将来にわたって変更してはいけません** — 変更すると、既存の全 `SwitchBotConnection` 行が復号不能になります。将来キーローテーションが必要な場合は、新しいpurposeで再暗号化しつつ旧purposeでも読めるようにする移行手順を別途設計してください（単純なリネームでは対応できません）。
- **独自の可逆暗号（XOR/Base64等）は一切使用しません。** Data Protectionのみを使う方針です。
- **キーリングの永続化**:
  - ローカル開発（`IsDevelopment()`）: ASP.NET Coreの既定のローカルキーリング（ユーザープロファイル配下）をそのまま使います。追加設定は不要です。
  - **本番（非Development環境）は、`DataProtection:KeyDirectory` に永続化された（アプリの再起動・再デプロイをまたいで消えない）ディレクトリパスを必ず設定する必要があります**（例: Azure App Serviceにマウントした Azure Files 共有、または永続ボリューム）。`PersistKeysToFileSystem` でこのパスに保存されます（`ServiceCollectionExtensions.cs`）。
  - **フェイルファスト**: `Program.cs` は、非Development環境で `DataProtection:KeyDirectory` が未設定の場合、**起動時に例外を投げてプロセスを終了します**（一時的なキーリングのまま黙って起動し、再起動のたびに全世帯の暗号化済み認証情報が読めなくなる、という事故を防ぐため）。エラーメッセージにはトークン等の秘密情報は一切含みません。
- **`IHouseholdSwitchBotClientFactory`**（`Infrastructure/Devices/HouseholdSwitchBotClientFactory.cs`）が、世帯IDを受け取り、短命なスコープ内でのみ復号したToken/Secretを使って `ISwitchBotClient` を生成します。復号済みの値がスコープを超えてキャッシュされることはなく、ある世帯の復号済み認証情報が別の世帯の処理に混入することもありません（`HouseholdSwitchBotClientFactoryTests.GetClientAsync_NeverLeaksOneHouseholdsCredentialsIntoAnothers` で回帰テスト済み）。
- **解決の優先順位**: ① 世帯ごとに保存された `SwitchBotConnection`（あれば必ずこれを使う） → ②（`SwitchBot:AllowGlobalFallback=true` の場合のみ）グローバルな `SwitchBotOptions`（User Secrets由来、ローカル開発のブートストラップ専用） → ③ どちらも無ければ未設定として扱う（例外を投げない）。
- **接続の検証**: 「LINE連携設定」画面でToken/Secretを保存する前に、必ず実際に `GET /v1.1/devices` を呼び出して疎通確認します（`SwitchBotConnectionService.ValidateAndSaveAsync`）。失敗した場合は保存されず、`LastErrorMessage` にはSwitchBotのAPIから返ったエラーの種類だけを記録し、**Token/Secretの値そのものは決して記録しません**。
- **画面表示**: 保存済みのTokenやSecretがUIに再表示されることは一切ありません。画面には「未設定/接続済み/エラー」のステータスと、最終検証・最終同期日時のみを表示します（`SwitchBotConnection.EncryptedToken`/`EncryptedSecret` はUIのレスポンスにも含まれません）。

## `.gitignore` による除外

リポジトリの `.gitignore`（.NET標準テンプレート相当）は、ビルド成果物（`bin/`, `obj/`）に加え、秘密情報が誤って混入しやすい以下のようなファイル／パターンを除外対象とすべきです。

- `appsettings.*.local.json` のようなローカル専用設定ファイル
- `*.db`, `*.db-shm`, `*.db-wal`（SQLiteのデモDBファイル。`mimamoritai-demo.db` はデモデータのみを含みますが、実運用相当のデータを含めた場合は特にコミットしない）
- `secrets.json` を直接プロジェクト内に置く運用は行わない（`dotnet user-secrets` はユーザープロファイル配下の別ディレクトリに保存されるため、そもそもリポジトリに含まれない）

## LINE Webhookの署名検証

`ILineMessagingClient.VerifySignature` （実装: `LineSignature.Verify`, `src/MimamoriTai.Infrastructure/Line/MockLineMessagingClient.cs`）は、以下の手順で検証します。

1. リクエストの生ボディ（rawBody）に対して、チャネルシークレットを鍵とした HMAC-SHA256 を計算する。
2. `X-Line-Signature` ヘッダーの値をBase64デコードする。
3. 計算結果とヘッダー値を `CryptographicOperations.FixedTimeEquals`（**タイミング攻撃に耐性のある定数時間比較**）で比較する。
4. チャネルシークレットが未設定、またはヘッダーが欠落・不正な場合は **必ず `false`** を返す（＝安全側に倒す）。

署名検証に失敗したリクエストは `WebhookEndpoints.MapWebhookEndpoints` で **401 Unauthorized** を返し、`AssistantOrchestrator` には一切渡されません。

## LINE送信元と世帯の紐付け（連携コード）のセキュリティ

以前は、署名検証さえ通れば**すべての**LINE Webhookイベントが無条件に「デフォルト世帯」に結びつけられていました。これは単一世帯のデモでは問題になりませんが、複数世帯が本番運用された場合、ある世帯宛のはずのイベントが別世帯として処理される・すべての新規友だち追加が同じ世帯に混入するといった重大なリスクになります。

- **既定の安全側動作（`Line:AllowDefaultHouseholdFallback=false`）**: 送信元（userId/groupId）に対応する有効な `LineRecipient` 行が無い限り、**いかなる世帯にも自動的に結びつけません**。未リンクの送信元には、6桁の連携コード（`連携 123456`）を使うよう案内する返信のみを送ります。
- **連携コードの発行はログイン済みの世帯オーナーのみ可能**（`LineLinkCodeService.IsOwnerAsync` によるチェック、匿名・非オーナーは拒否）。
- **コードは平文で保存されません**（`LineLinkCode.CodeHash`、詳細は `docs/LINE_SETUP.md` 「6. 世帯とLINEを紐付ける」参照）。有効期限10分・使い捨て・試行回数制限（既定5回）により、コードの総当たりや漏洩後の悪用を防ぎます。
- **ローカルデモ限定のフォールバック**: `appsettings.Development.json` でのみ `Line:AllowDefaultHouseholdFallback=true` にしており、これは既存の単一世帯デモ体験を壊さないための限定的な例外です。本番相当の設定ファイルではこのフラグを `true` にしないでください。

## AI家電操作のガードレール

自然言語による家電操作は `DeviceSafetyPolicy.Evaluate` によって多層的に制限されています（詳細は `docs/ARCHITECTURE.md` の「安全ガードレールのフロー」参照）。判定結果は「許可／拒否」の2値ではなく `SafetyVerdict`（`Allow` / `ConfirmHazard` / `Deny`）という3値の型で返され、呼び出し側が日本語メッセージを解釈する必要がないようになっています。要点:

- **火や熱をあつかう家電（Heater/Kettle/Microwave/CookingDevice/Plug = `SafetyClass.Guarded`）のTurnOn/Toggleは、機器種別に応じたハザード確認（「周りに燃えやすいものはありませんか？」等）に「はい」と答えた場合にのみ実行される。** この関門は `DeviceControlService.ExecuteAsync` に置かれており、会話フローを迂回してAPIを直接呼んでも回避できない。
- **`Guarded` 機器が遠隔でONになった場合、世帯の全員にLINEで通知される**（`IGuardedActionNotifier`）。通知はガードレールの一部であり、操作した本人だけが知っている状態を作らない。通知の失敗は操作を失敗させない（家電はすでにONになっているため）。
- **オーナーは機器ごとに「遠隔でONにすることを禁止する」を設定でき、その機器は `SafetyClass.Restricted` となりハザード確認すら提示せず拒否される。** この設定は機器種別の既定分類より優先される（最も慎重な設定が勝つ）。
- **センサー類・未知の機器（MotionSensor/ContactSensor/Unknown）は既定で `Restricted`。** 許可リスト方式なので、SwitchBotから同期された未知の機種が自動的に操作対象にならない。
- **TurnOff（消す操作）はどの安全クラスでも常に許可される非対称なガードレール。** 心配した家族が最初に手を伸ばす操作であり、火事の原因にはならないため。
- 機器ごとの `RemoteControlAllowed` フラグ（既定 `false`）で個別に遠隔操作を無効化できる。同期処理はこのフラグを自動で立てない。
- LLMが返す確信度 (`confidence`) が `IntentParser.MinimumConfidence`（0.85）未満なら操作を実行しない。
- 機器名（エイリアス）が一意に特定できない場合は、機器を推測せずに確認を求める。
- LLMの出力（JSON）が不正な場合、`IntentParser.TryParse` は `null` を返し、`AssistantOrchestrator` は1回だけ修復を試みてそれでも失敗すれば何も実行しない。
- **ハザード確認の「はい」はLLMの申告では代替できない。** モデルが「利用者は確認済みと言っています」と主張しても、実際の確認ターンを経ていなければ実行されない（プロンプトインジェクション対策）。
- LLMが返す確信度 (`confidence`) が `IntentParser.MinimumConfidence`（0.85）未満なら操作を実行しない。
- 機器名（エイリアス）が一意に特定できない場合は、機器を推測せずに確認を求める。
- LLMの出力（JSON）が不正な場合、`IntentParser.TryParse` は `null` を返し、`AssistantOrchestrator` は1回だけ修復を試みてそれでも失敗すれば何も実行しない。

## HTTP APIの認可と世帯スコープ

`/api/*` の読み取り系エンドポイントは、**呼び出し側が指定した世帯IDを信用しません**。`ApiEndpoints` の各ハンドラは以下の順で処理します。

1. `householdId` が省略された場合は `HouseholdAccessService.ResolveDefaultAsync` で**サインイン中の利用者がアクセスできる世帯**を決定する（匿名デモモードでは `DataSourceMode=Sample` の世帯のみ）。
2. `householdId` が明示された場合は `HouseholdAccessService.CanAccessAsync` を必ず通し、権限が無ければ **404 Not Found** を返す（403 ではなく 404 を返すのは、他世帯のIDの存在有無を推測させないため）。
3. 一覧系（`GET /api/devices` など）のクエリは、確定した世帯IDで**必ずフィルタ**する。

- **`GET /api/devices` は当初この3段をすべて欠いており、全世帯の機器を無条件に返していました**（隣接する `GET /api/devices/{id}` には `CanAccessAsync` があったため、一覧だけが抜けている状態でした）。現在は上記のとおり修正済みで、`DeviceEndpointAuthorizationTests` が「他世帯の機器が一覧に出ない」「他世帯の機器IDを直接叩くと 404」の両方を回帰テストしています。
- 単一世帯のデモでは表面化しない種類の欠落であり、**世帯フィルタは「動くかどうか」ではなくテストでしか守れない**という教訓から、認可はエンドポイント単位でテストを持つ方針にしています。

## SwitchBot Webhookの認証

`POST /webhooks/switchbot` は、SwitchBotクラウドからの機器状態変化コールバックを受け取ります。

- **SwitchBotのWebhookは署名ヘッダーを送りません。** LINE のような HMAC 署名検証が使えないため、**共有シークレット方式**を採っています。`SwitchBot:WebhookSecret` に十分長いランダム文字列を設定し、同じ値を `X-Webhook-Token` ヘッダー、または**クエリ文字列 `?token=`** で渡します（SwitchBot のコンソールはコールバックURLしか設定できずカスタムヘッダーを送れないため、後者を用意しています）。比較は両辺を SHA-256 でハッシュしたうえで `CryptographicOperations.FixedTimeEquals` で行い、**定数時間かつシークレットの長さも漏らしません**。
- **既定は fail-closed です。** `SwitchBot:WebhookSecret` が未設定の場合、リクエストは **401 Unauthorized** で拒否されます。コールバックを取りこぼしても、`SwitchBotSyncService` のポーリングが状態を回収するため実害は「反映が遅れる」だけであり、無認証で受け付けるより安全です。
- **明示的に無認証を許す場合のみ** `SwitchBot:AllowUnauthenticatedWebhook=true`（既定 `false`）を設定します。ローカル開発でトンネル越しに疎通確認する用途に限定してください。
- **修正前は無認証でした。** 攻撃者がエンドポイントを知っていれば、任意のMACアドレスを詐称した偽の状態変化イベントを注入でき、「一定時間まったく機器が動いていない」ことを根拠にした**無活動アラートを握り潰す**ことが可能でした。見守りアプリにおいて「アラートが鳴らない」は最も危険な故障モードであるため、優先して塞いでいます。

## 監査ログ (`DeviceCommand`)

すべての家電操作の**試行**（成功・失敗・拒否のいずれも）は `DeviceCommand` エンティティとして永続化されます（`DeviceControlService.ExecuteAsync` / `RejectAsync`）。記録される情報:

- `OriginalText`: ユーザーの元の発言
- `Action`: 要求された操作（TurnOn/TurnOff/Toggle/GetStatus）
- `Status`: `Pending`/`Succeeded`/`Failed`/`Rejected`
- `FailureReason`: 拒否・失敗の理由（日本語の説明文）
- `AiResolvedModel`: 判定に使われたAIモデル名（`AiRequestLog` とも紐付く）
- `RequestedAtUtc` / `ExecutedAtUtc`: リクエスト時刻・実行時刻（UTC）
- `Source`: `Web`/`Line`/`System` のいずれの経路から来たか
- `RequestedByPersonId`: 要求した人物（判明する場合）

これにより、「誰が・いつ・何を・どのような理由で」操作しようとしたか（拒否も含む）を後から追跡できます。

## プライバシー上の考慮事項（高齢者見守りデータ）

- 本アプリが収集するのは**家電のON/OFFイベントとタイムスタンプのみ**であり、映像・音声・位置情報等のより機微なデータは扱いません。
- それでも、生活リズム（起床・就寝・外出の推定等）は個人の生活パターンを推測できる情報であるため、以下を推奨します。
  - データベースへのアクセスは、世帯の家族と本人のみに限定する。認証・認可の土台自体はOIDC（`Auth:Enabled`、`docs/ARCHITECTURE.md`「実認証の実装」参照）と `HouseholdAccessService` により実装済みですが、既定では `Auth:Enabled=false`（匿名デモモード）です。**SwitchBot認証情報・LINE連携コードを扱う「LINE連携設定」画面（`/settings/switchbot`）は、世帯オーナーとしてサインインしていることを必須とします**（`SwitchBotConnectionEndpoints.RequireOwnerAsync`）が、本番運用としてダッシュボード全体を保護するには `Auth:Enabled=true` とし、本番相当の認証プロバイダー（Entra External ID等）を設定する必要があります（要確認: 実運用前に必ず有効化すること）。
  - デモデータ（`DemoDataSeeder` が生成する `EventSource.Seed` 由来のデータ、`demo-` プレフィックス付きの `ExternalDeviceId`）と実データを明確に区別し、デモ環境と本番環境のデータベースを分離する。
  - LINEなど外部サービスに送信するメッセージには、必要以上の医療的・断定的な表現を含めない（`LocalDataQuestionService` の応答文言もこの方針に沿っている）。
  - Fabric Data Agentの指示文（`docs/FABRIC_SETUP.md`）にも、断定的な診断をしないよう明記している。
- 本リポジトリ・ドキュメントの範囲では、認証・認可・データ保持期間・削除ポリシー等の詳細な運用ポリシーは策定されていません。実運用移行時には別途整備が必要です（要確認）。

## 既知の未対応事項と対応計画

「対応していないこと」を暗黙にしないため、現時点で把握している制約・未対応事項をここに集約します。**この一覧に無いものは「検討していない」ではなく「把握していない」を意味します。** 新たに判明した項目は、直すかどうかに関わらずまずここに1行追加する運用とします。

| # | 事項 | 現状 | 影響 | 対応計画 |
| --- | --- | --- | --- | --- |
| 1 | ダッシュボード全体の認証 | 既定 `Auth:Enabled=false`（匿名デモモード）。OIDC の土台と `HouseholdAccessService` は実装済み | 匿名モードでは `DataSourceMode=Sample` の世帯しか見えないため実データは露出しないが、**本番運用としては不足** | 実運用前に `Auth:Enabled=true` とし、Entra External ID 等の本番プロバイダーを設定する。**着手条件**：実データを1世帯でも投入する時点 |
| 2 | 書き込み系APIのCSRF対策 | Blazor Server のフォームは既定の Antiforgery で保護。`/api/*` の書き込み系は現状 API キー等を持たない | 匿名デモモードでは第三者が機器操作APIを叩ける可能性がある（ただし `DeviceSafetyPolicy` により `Guarded` 機器のONは確認ターン無しでは実行されない） | #1 と同時に対応。認証が入れば Cookie + Antiforgery で塞がる |
| 3 | レート制限 | LINE Webhook / SwitchBot Webhook / `/api/*` にレート制限なし | 大量リクエストによるコスト増（LLM 呼び出しを伴う経路）とサービス低下 | ASP.NET Core のレート制限ミドルウェアを Webhook 経路に適用する。**着手条件**：公開URLを常時稼働させる時点 |
| 4 | データ保持期間と削除 | `PowerReading` / `DeviceCommand` / `AiRequestLog` は無期限に保持。削除APIなし | 生活リズムを推測できるデータが蓄積し続ける | 保持期間（例: 生データ90日、日次集計は無期限）と世帯単位の削除手順を定義する。**着手条件**：実運用移行時 |
| 5 | LLM 応答の内容監査 | `InventsNumbers` 検査で「ソースに無い数値を主張した要約」は破棄するが、それ以外の内容監査は無い | 断定的な医療表現などがすり抜ける余地 | プロンプト側の禁止事項に加え、出力側の禁止語チェックを追加する |
| 6 | 意図分類の継続的な精度計測 | 100件のラベル付き評価セットと計測ハーネスを実装済み（`docs/eval/intent-accuracy.md`）。ただし**CIでは毎push実行していない** | モデル更新時の精度劣化に気付くのが遅れる | API 費用が発生するため手動実行としている。モデル／プロンプト変更時に手動で回し、結果を `docs/eval/intent-accuracy.md` にコミットする運用 |

### 対応済み（履歴として残す）

過去に穴があり、現在は修正済みの項目です。同じ種類の見落としを繰り返さないため、削除せずに残します。

| 事項 | 何が問題だったか | 対応 |
| --- | --- | --- |
| `GET /api/devices` の認可・世帯フィルタ欠落 | 隣接エンドポイントには `CanAccessAsync` があったのに、一覧だけ認可も世帯フィルタも無く**全世帯の機器を返していた** | 上記「HTTP APIの認可と世帯スコープ」のとおり修正。エンドポイント単位の回帰テストを追加 |
| `POST /webhooks/switchbot` の無認証 | MACアドレスを詐称した偽イベントを注入でき、**無活動アラートを握り潰せた** | 共有シークレット認証を追加し、既定 fail-closed 化。上記「SwitchBot Webhookの認証」参照 |
| LLM のトークン使用量が計測できない | `ChatCompletionResponse` が `usage` をデシリアライズしておらず、削減施策の効果を金額で言えなかった | ルーター → `AiRequestLog` → 管理画面まで `PromptTokens`/`CompletionTokens`/`TotalTokens` を通した |

