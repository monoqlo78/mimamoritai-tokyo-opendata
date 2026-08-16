# LINE ログイン設定手順（Entra External ID 連携 / LINE 直結）

このドキュメントは、見守り隊で **実際に LINE ログインを動かす**ための設定手順を、
Entra External ID (CIAM) 側と LINE Developers 側の **両方** について記載します。

> **重要:** これは **LINE Login**（ユーザー認証）の手順です。家族への通知に使う
> **LINE Messaging API** とは別物です。Messaging API 側は `docs/LINE_SETUP.md` を参照してください。
> 両者はチャネルが別で、Channel ID / Channel secret も別々に発行されます。

---

## 0. 「ログインできない」の原因（実測済み）

このリポジトリの認証コードは正しく配線されています。実測で確認された失敗要因は次のとおりです。

| # | 症状 | 実測結果 | 対処 |
| --- | --- | --- | --- |
| 1 | `/auth/login` がログイン画面に飛ばず「ログイン機能は現在設定されていません」と表示される | HTTP 200 + プレーンテキスト（302 リダイレクトではない） | `Auth:*` の4項目が未設定。**下記「手順 3」で投入する**（これが第一原因） |
| 2 | `dotnet user-secrets` が実行できない | `Could not find the global property 'UserSecretsId'` | `MimamoriTai.Web.csproj` に `UserSecretsId` を追加済み（修正済み） |
| 3 | Entra のサインイン画面に **LINE ボタンが出ない** | サインイン画面はメールアドレス入力のみ | Entra 側に LINE が外部 IdP として未登録。**手順 2 の `setup-line-entra-idp.ps1` が未実行**（要対応） |
| 4 | LINE 直結時に `/auth/logout` が HTTP 500 | `InvalidOperationException: Cannot redirect to the end session endpoint` | LINE の discovery に `end_session_endpoint` が無い。Cookie のみサインアウトするよう修正済み |
| 5 | LINE 直結時に認可 URL へ `response_mode=form_post` が付く | LINE は `response_mode` を仕様に持たない | LINE 直結時は `query` に固定するよう修正済み |
| 6 | Entra 経由でログインすると `AADSTS50011`（redirect_uri 不一致） | アプリ登録の登録値は `http://localhost:5199/signin-oidc` / `https://localhost:7199/signin-oidc` だったのに対し、`launchSettings.json` は `5234` / `7215` を使う | Graph API でローカル用リダイレクト URI（`5234` / `7215` / `5301`）を追加済み（下記「2-1」参照） |
| 7 | User Secrets を設定したのに `/auth/login` が依然として案内文を返す | `dotnet run --no-launch-profile` は `ASPNETCORE_ENVIRONMENT` を設定しないため **Production 扱いになり User Secrets が読み込まれない** | 起動時に `ASPNETCORE_ENVIRONMENT=Development` を明示するか、`--launch-profile https` を使う |

#### Graph API で確認した Entra テナントの実際の状態（2026-08-11 時点）

`az account get-access-token --resource https://graph.microsoft.com --tenant <tenantId>` で取得したトークンで実測しました。

| 対象 | 実測値 |
| --- | --- |
| ユーザーフロー一覧 | `SAMLTEST` / `SAMLTEST2` / **`MimamoriTai SignUpSignIn`**（id: `d06ea237-ed42-4f1c-8526-9d766b66d8f4`。スクリプト既定値と一致） |
| `MimamoriTai SignUpSignIn` に紐づく IdP | **`Email One Time Passcode` のみ。LINE は未登録** ← 原因 #3 の確証 |
| アプリ登録 | `MimamoriTai Web`（objectId: `707a5eb9-3a28-40a5-a533-0db0f90bdb3b`） |
| リダイレクト URI（修正前） | `http://localhost:5199/signin-oidc` / `https://localhost:7199/signin-oidc` / `https://<your-app>.azurewebsites.net/signin-oidc` ← 原因 #6 の確証 |

> `identityProviders` の一覧取得は `IdentityProvider.Read.All` が必要で、Azure CLI の既定トークンでは **HTTP 403**（`AADB2C: The application does not have any of the required delegated permissions`）になります。`setup-line-entra-idp.ps1` 実行時に同じエラーが出た場合は「トラブルシューティング › Graph API 呼び出しが 401/403 で失敗する」を参照してください。

### 実測した LINE の OIDC 仕様（`https://access.line.me/.well-known/openid-configuration`）

```
issuer                            : https://access.line.me
authorization_endpoint            : https://access.line.me/oauth2/v2.1/authorize
token_endpoint                    : https://api.line.me/oauth2/v2.1/token
userinfo_endpoint                 : https://api.line.me/oauth2/v2.1/userinfo
jwks_uri                          : https://api.line.me/oauth2/v2.1/certs
scopes_supported                  : openid, profile, email     ← offline_access は無い
response_types_supported          : code
id_token_signing_alg_values_supported : ES256
code_challenge_methods_supported  : S256                        ← PKCE 必須
end_session_endpoint              : （存在しない）              ← ログアウトは Cookie 削除のみ
```

---

## 1. どちらの構成を採るか

| 構成 | Authority | 長所 | 短所 |
| --- | --- | --- | --- |
| A. Entra External ID 経由 | `https://<tenantId>.ciamlogin.com/<tenantId>/v2.0` | LINE 以外（メール・Google 等）も同じ配線で追加できる。トークン発行・ユーザー管理を Entra に一元化できる。`offline_access` が使える。ログアウトが正しく機能する。 | **現状 Entra 側が LINE の登録を拒否するため使用不可**（下記参照） |
| **B. LINE Login 直結（実測で動作を確認済み・現在の既定）** | `https://access.line.me` | 設定が LINE Developers だけで完結し、最短で動く。**実ログイン完走を実測済み** | LINE ユーザー限定。リフレッシュトークン（`offline_access`）が使えない。リモートログアウト不可 |

**このリポジトリの現在の既定は B（LINE 直結）です。**
コードは両方に対応しており、`Auth:Authority` に `access.line.me` を設定すると自動的に B の分岐
（`offline_access` を要求しない／`response_mode=query`／Cookie のみサインアウト／HS256 対称鍵での
署名検証）に切り替わります。`Auth:ProviderName` の既定値 `entra-external` は互換のため残していますが、
プロバイダー名は Authority から自動判定されます（`AuthOptions.ResolveIdentityProvider`）。

### 構成 A が現在使えない理由（Graph API での実測）

Microsoft Graph に LINE を OIDC ID プロバイダーとして登録しようとすると、**HTTP 400** で拒否されます。

```
POST https://graph.microsoft.com/beta/identity/identityProviders
→ 400 Bad Request
   "Custom OIDC well-known endpoint validation error: Error when deserializing response.
    Required property 'token_endpoint_auth_methods_supported' not found in JSON."
```

LINE の discovery ドキュメント（`https://access.line.me/.well-known/openid-configuration`）には
`token_endpoint_auth_methods_supported` が**含まれていません**。OIDC Discovery 仕様上このプロパティは
OPTIONAL ですが、**Entra External ID は必須として扱う**ため、検証段階で弾かれます。

B2C 系の型（`microsoft.graph.openIdConnectIdentityProvider`）でも試しましたが、
`The IdentityProvider type 'OpenIdConnect' is invalid` となり External ID テナントでは利用できません。

**回避するには**、不足プロパティを補った discovery ドキュメントを自前でホストし
（例: App Service に `/.well-known/line-openid-configuration` を追加）、それを `wellKnownEndpoint` に
指定する必要があります。ただし Entra が「discovery のホストと `issuer` のホストの一致」を検証するか
どうかは未検証のため、実現可能性は未確定です。LINE 側が discovery を修正すれば構成 A は即座に使えます。

#### 構成 A を将来復活させるには（記録・現時点では実装しない）

この案件では **構成 A の復活は保留**とし、構成 B を正式採用しました。将来もう一度検討する場合の
選択肢を記録として残します。

| 案 | 内容 | リスク・未検証点 |
| --- | --- | --- |
| **A-1. LINE 側の修正を待つ**（推奨） | LINE が discovery に `token_endpoint_auth_methods_supported` を追加すれば、**コード変更なしで構成 A に戻せます**（`scripts/setup-line-entra-idp.ps1` は修正済みの JSON 形式で動くようにしてあります）。まず discovery を再取得して当該プロパティの有無を確認してください。 | LINE 側の対応時期は不明 |
| A-2. discovery を自前でホストする | 不足プロパティ（`"token_endpoint_auth_methods_supported": ["client_secret_post"]`）を補ったコピーを App Service に配置し（例: `/.well-known/line-openid-configuration`）、IdP の `wellKnownEndpoint` にそのURLを指定する。`issuer` は `https://access.line.me` のまま。 | **Entra が「discovery のホストと `issuer` のホストの一致」を検証する可能性があり、成功保証なし。** LINE が discovery を変更したときの追随も必要 |
| A-3. Entra のカスタム認証拡張 | Entra の拡張ポイントで LINE との連携を実装する | 実装コストが大きく、External ID の対応状況も要調査 |

**復活時の手順は本書の「手順 2」がそのまま使えます。** `setup-line-entra-idp.ps1` は既に
実測に基づいて修正済みです（`clientSecret` を `clientAuthentication` 配下に置く／クレームキーを
`sub`・`name`・`email` にする／`supportedTenantTypes: "externalId"` を付ける／`-AccessToken` で
デバイスコードフローのトークンを直接渡せる）。復活させる際は `Auth:Authority` を
ciamlogin 形式に戻し、`Auth:CallbackPath` を `/signin-oidc` に戻すだけです。

---

## 2. 相互に貼り付け合う値の対応表

**ここが「相互設定」の核心です。** どの値をどちらからどちらへコピーするかを示します。

### 構成 A（Entra External ID 経由）— 貼り付けは3往復

| # | コピー元 | 値 | コピー先 |
| --- | --- | --- | --- |
| 1 | **LINE Developers** › LINE Login チャネル › チャネル基本設定 | **チャネル ID**（数字10桁） | **Entra**（`setup-line-entra-idp.ps1` の `-LineChannelId`）＝ OIDC IdP の `clientId` |
| 2 | **LINE Developers** › 同上 | **チャネルシークレット** | **Entra**（`-LineChannelSecret`）＝ OIDC IdP の `clientSecret` |
| 3 | **Entra** › 外部 ID › すべての ID プロバイダー › LINE | **コールバック URL**（例 `https://<tenantId>.ciamlogin.com/<tenantId>/federation/oauth2`） | **LINE Developers** › LINE Login 設定 › **コールバック URL** |
| 4 | **Entra** › アプリ登録 › 概要 | **アプリケーション (クライアント) ID** | アプリの `Auth:ClientId` |
| 5 | **Entra** › アプリ登録 › 証明書とシークレット | **クライアントシークレットの値** | アプリの `Auth:ClientSecret` |
| 6 | **アプリ** | `https://<ホスト>/signin-oidc` | **Entra** › アプリ登録 › 認証 › **リダイレクト URI (Web)** |
| 7 | **アプリ** | `https://<ホスト>/signout-callback-oidc` | **Entra** › アプリ登録 › 認証 › **フロントチャネルログアウト URL** |

> **注意:** LINE 側のコールバック URL には**アプリの URL を書きません**。構成 A では
> LINE から見た「アプリ」は Entra なので、**Entra のコールバック URL** を登録します。
> ここを間違えて `https://<ホスト>/signin-oidc` を入れるのが典型的な失敗です。

### 構成 B（LINE 直結）— 貼り付けは2往復

| # | コピー元 | 値 | コピー先 |
| --- | --- | --- | --- |
| 1 | **LINE Developers** › チャネル基本設定 | **チャネル ID** | アプリの `Auth:ClientId` |
| 2 | **LINE Developers** › 同上 | **チャネルシークレット** | アプリの `Auth:ClientSecret` |
| 3 | **アプリ** | `https://<ホスト>/signin-line`（ローカルは `http://localhost:5301/signin-line`） | **LINE Developers** › LINE Login 設定 › **コールバック URL** |

> コールバックのパスは `Auth:CallbackPath` で決まります。**構成 B では `/signin-line` を推奨**します
> （既定値は `/signin-oidc`）。LINE 由来のコールバックだと URL だけで判別でき、将来 Entra と併用
> するときにパスが衝突しません。`Auth:CallbackPath` に設定した値と、LINE Developers のコールバック
> URL の**パス部分は完全一致**させてください。

---

## 手順 1: LINE Developers Console で LINE Login チャネルを作成する（手動・両構成共通）

1. https://developers.line.biz/console/ にログインします。
2. プロバイダーを選択（このリポジトリの既定 Provider ID: `1581660279`）、または新規作成します。
3. 「新規チャネル作成」から **LINE Login** チャネルを作成します。
   - チャネルの種類: **LINE Login**
   - アプリタイプ: **ウェブアプリ (Web app)** にチェック（**必須**。これが無いとブラウザから
     ログインできません）
   - チャネル名・チャネル説明・業種などの必須項目を入力して作成します。
4. **「チャネル基本設定」タブ**で以下を控えます。
   - **チャネル ID**（数字10桁）
   - **チャネルシークレット**（「発行」ボタンで発行）
   - ⚠️ **シークレットはコミット・チャット貼り付け・ログ出力をしないでください。**
5. **「LINEログイン設定」タブ**で **コールバック URL** を登録します。
   - 構成 A: **手順 2 実行後に Entra 管理センターに表示される値**（上の対応表 #3）
   - 構成 B: `https://<ホスト>/signin-line`（ローカル開発は `http://localhost:5301/signin-line`）
     — `Auth:CallbackPath` に設定した値と一致させます
   - 複数行に分けて複数登録できます。本番とローカルの両方を登録しておくと便利です。
6. **「OpenID Connect」タブ**で以下を設定します。
   - **OpenID Connect を有効化**します（これが無効だと `id_token` が返らず、
     ASP.NET Core 側の OIDC ハンドラが必ず失敗します）。
   - **`email` 権限を申請**します。`openid` と `profile` は申請不要ですが、
     **`email` は審査承認まで使えません**（数営業日かかることがあります）。
   - 承認前は `email` クレームが返りません。動作確認だけなら `openid profile` で進められます。

---

## 手順 2: Entra External ID 側の設定（構成 A のみ）

### 2-1. アプリ登録（アプリ自身が Entra にサインインするための設定）

1. Microsoft Entra 管理センター › **アプリの登録** › 対象アプリを開きます
   （このリポジトリの既定 App ID: `dcc221af-ceb0-47fe-baac-837e8853423c`）。
2. **認証** › **プラットフォームを追加** › **Web** で、**リダイレクト URI** に以下を登録します。
   - 本番: `https://<your-app>.azurewebsites.net/signin-oidc`
   - ローカル: `https://localhost:7215/signin-oidc`（および必要なら `http://localhost:5234/signin-oidc`）
   - ⚠️ **末尾スラッシュ・大文字小文字を含めて完全一致**でなければ `AADSTS50011` になります。
   - ⚠️ **`launchSettings.json` のポートと必ず一致させること。** 実際にこのテナントでは `5199`/`7199` しか登録されておらず、`launchSettings.json` の `5234`/`7215` と食い違っていました（原因 #6）。

   ポータルを開かずに Graph API で追加することもできます（**既存の URI を消さないよう、必ず現在値を読んでから結合すること**）。

   ```powershell
   $t   = '5ff64b34-cc0e-4813-9911-92968b7ff975'
   $obj = '707a5eb9-3a28-40a5-a533-0db0f90bdb3b'   # アプリ登録の objectId
   $tok = (az account get-access-token --resource https://graph.microsoft.com --tenant $t -o tsv --query accessToken).Trim()
   $h   = @{ Authorization = "Bearer $tok" }

   # 1) 現在値を読む
   $cur = (Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/applications/$obj" -Headers $h).web.redirectUris

   # 2) 追加したいものと結合して PATCH
   $all = ($cur + @(
       'http://localhost:5234/signin-oidc',
       'https://localhost:7215/signin-oidc'
   )) | Select-Object -Unique
   $body = @{ web = @{ redirectUris = $all } } | ConvertTo-Json -Depth 6
   Invoke-RestMethod -Method PATCH -Uri "https://graph.microsoft.com/v1.0/applications/$obj" -Headers $h -Body $body -ContentType 'application/json'

   # 3) 反映を確認
   (Invoke-RestMethod -Uri "https://graph.microsoft.com/v1.0/applications/$obj" -Headers $h).web.redirectUris
   ```
3. 同じ画面の **フロントチャネルログアウト URL** に
   `https://<ホスト>/signout-callback-oidc` を登録します。
4. **証明書とシークレット** › **新しいクライアントシークレット** を作成し、**値**（ID ではない）を控えます。
   - この値は作成直後にしか表示されません。
   - Graph API で発行することもできます（`secretText` が**この応答でしか取得できない**点はポータルと同じ）。

     ```powershell
     $body = @{ passwordCredential = @{
         displayName = 'mimamoritai-local-dev'
         endDateTime = (Get-Date).AddMonths(6).ToString('o')
     } } | ConvertTo-Json -Depth 5
     $sec = Invoke-RestMethod -Method POST `
         -Uri "https://graph.microsoft.com/v1.0/applications/$obj/addPassword" `
         -Headers $h -Body $body -ContentType 'application/json'
     # 画面に出さず、そのまま User Secrets へ流し込む
     Push-Location src/MimamoriTai.Web
     dotnet user-secrets set "Auth:ClientSecret" $sec.secretText | Out-Null
     Pop-Location
     ```
5. **概要** タブの **アプリケーション (クライアント) ID** を控えます。

### 2-2. LINE を外部 ID プロバイダーとして登録（スクリプトで自動化）

手順 1 で取得した Channel ID / Channel secret を使って、リポジトリルートで実行します。

```powershell
az login --tenant 5ff64b34-cc0e-4813-9911-92968b7ff975 --allow-no-subscriptions

./scripts/setup-line-entra-idp.ps1 `
    -LineChannelId "1234567890" `
    -LineChannelSecret "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
```

スクリプトが行うこと:

1. `az account get-access-token` で CIAM テナント向けの Graph トークンを取得します。
2. 既存の `LINE` という表示名の OIDC 識別プロバイダーを確認し、あれば **更新 (PATCH)**、
   なければ **作成 (POST)** します（`issuer` 付きで自動リトライします）。
3. 識別プロバイダーをユーザーフロー（既定 ID: `d06ea237-ed42-4f1c-8526-9d766b66d8f4`）に
   **リンク**します。
4. ユーザーフローを再読込し、有効な識別プロバイダー一覧を表示して検証します。
5. LINE Login チャネルに登録すべき **コールバック URL の候補** を表示します。

テナント ID・アプリ ID・ユーザーフロー ID を明示指定する場合:

```powershell
./scripts/setup-line-entra-idp.ps1 `
    -LineChannelId "1234567890" `
    -LineChannelSecret "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" `
    -TenantId "5ff64b34-cc0e-4813-9911-92968b7ff975" `
    -AppId "dcc221af-ceb0-47fe-baac-837e8853423c" `
    -UserFlowId "d06ea237-ed42-4f1c-8526-9d766b66d8f4"
```

### 2-3. ユーザーフローにアプリを紐付ける

Entra 管理センター › **外部 ID** › **ユーザーフロー** › 対象フロー › **アプリケーション** で、
2-1 のアプリが追加されていることを確認します。ここが未設定だと、サインイン画面は出るものの
LINE ボタンが表示されません。

### 2-4. LINE ボタンが出ることを目視で確認する

ブラウザで `/auth/login` を開き、Entra のサインイン画面に **「LINE」ボタンが表示されること**を
確認します。**メールアドレス入力欄しか無い場合は、2-2 または 2-3 が未完了です。**

### 2-5. Entra 側のコールバック URL を LINE に貼り戻す

Entra 管理センター › **外部 ID** › **すべての ID プロバイダー** › **LINE** を開き、
表示されている **コールバック URL** をコピーして、LINE Developers Console の
「LINEログイン設定」› コールバック URL に登録します（対応表 #3）。

Microsoft はテナントによって表示形式を変えるため、**推測せず必ず管理センターの表示値を
コピーしてください。** よく見られる形式:

- `https://<tenantId>.ciamlogin.com/<tenantId>/federation/oauth2`
- `https://<tenant-subdomain>.ciamlogin.com/<tenantId>/federation/oidc/access.line.me`
- `https://<tenant>.ciamlogin.com/<tenantId>/oauth2/authresp`
  （**Azure AD B2C / 従来のワークフォーステナント**でよく使われる形式。External ID の
  カスタム OIDC プロバイダーでは `federation` 系が表示されることが多く、混同しやすいので注意）

**どれが正しいかは推測できません。** 2-2 の IdP 作成が完了すると管理センターに実際の値が
表示されるので、必ずそれをコピーしてください。管理センターを開けない場合は Graph API でも
確認できます（`IdentityProvider.Read.All` が必要）。

```powershell
$t   = '5ff64b34-cc0e-4813-9911-92968b7ff975'
$tok = (az account get-access-token --resource https://graph.microsoft.com --tenant $t -o tsv --query accessToken).Trim()
Invoke-RestMethod -Uri 'https://graph.microsoft.com/beta/identity/identityProviders' `
    -Headers @{ Authorization = "Bearer $tok" } |
    Select-Object -ExpandProperty value |
    Where-Object displayName -eq 'LINE' |
    Format-List displayName, id, clientId, scope, wellKnownEndpoint
```

> LINE 側のコールバック URL 欄は**複数登録できる**ため、候補が絞れないうちは上記を
> すべて登録しておいても実害はありません（一致したものだけが使われます）。ただし
> **最終的には管理センターの表示値と完全一致していること**を必ず確認してください。

---

## 手順 3: アプリ側に設定を投入する（User Secrets）

`ClientSecret` は **絶対に `appsettings.json` / `appsettings.Development.json` に書かないでください。**
`MimamoriTai.Web.csproj` に `UserSecretsId` を追加済みなので、次のコマンドがそのまま動きます。

### 構成 A（Entra External ID 経由・既定）

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "Auth:Enabled"      "true"
dotnet user-secrets set "Auth:Authority"    "https://5ff64b34-cc0e-4813-9911-92968b7ff975.ciamlogin.com/5ff64b34-cc0e-4813-9911-92968b7ff975/v2.0"
dotnet user-secrets set "Auth:ClientId"     "dcc221af-ceb0-47fe-baac-837e8853423c"
dotnet user-secrets set "Auth:ClientSecret" "<アプリ登録のクライアントシークレットの値>"
dotnet user-secrets set "Auth:ProviderName" "entra-external"
```

> Authority の形式は実測で確認済みです。
> `https://5ff64b34-cc0e-4813-9911-92968b7ff975.ciamlogin.com/5ff64b34-cc0e-4813-9911-92968b7ff975/v2.0/.well-known/openid-configuration`
> が HTTP 200 を返し、`issuer` が同一文字列であることを確認しました。

### 構成 B（LINE 直結）

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "Auth:Enabled"      "true"
dotnet user-secrets set "Auth:Authority"    "https://access.line.me"
dotnet user-secrets set "Auth:ClientId"     "<LINE のチャネル ID>"
dotnet user-secrets set "Auth:ClientSecret" "<LINE のチャネルシークレット>"
dotnet user-secrets set "Auth:CallbackPath" "/signin-line"
dotnet user-secrets set "Auth:ProviderName" "line"
```

環境変数で渡す場合（CI・コンテナ・一時検証用）。`:` は `__` に置き換えます。

```powershell
$env:Auth__Enabled="true"; $env:Auth__Authority="https://access.line.me"
$env:Auth__ClientId="<チャネル ID>"; $env:Auth__ClientSecret="<チャネルシークレット>"
$env:Auth__CallbackPath="/signin-line"; $env:Auth__ProviderName="line"
```

> ⚠️ User Secrets は **`ASPNETCORE_ENVIRONMENT=Development` のときしか読まれません**。
> `dotnet run --no-launch-profile` はこの変数を設定しないため Production 扱いになり、
> 設定が読まれず匿名モードに戻ります（原因 #7）。環境変数を明示してください。

#### 構成 B の配線実測（このリポジトリで確認済み）

上記の設定でポート 5301 に起動し、`/auth/login` を叩いたときの実際の 302 応答:

```
HTTP 302
Location: https://access.line.me/oauth2/v2.1/authorize
          ?client_id=<チャネル ID>
          &redirect_uri=http%3A%2F%2Flocalhost%3A5301%2Fsignin-line
          &response_type=code
          &scope=openid profile email          ← offline_access なし（原因 #5 の修正が効いている）
          &code_challenge=...&code_challenge_method=S256   ← PKCE 有効
          &nonce=...&state=...
                                               ← response_mode パラメータなし（LINE 仕様どおり）
```

同時に確認した挙動:

| 確認項目 | 結果 |
| --- | --- |
| `GET /` | HTTP 200 |
| `GET /auth/logout` | HTTP 302 → `/`（LINE には `end_session_endpoint` が無いので 500 にならない＝原因 #4 の修正） |
| `GET /signin-line`（state 無しの生 GET） | HTTP 500 `OpenIdConnectAuthenticationHandler: message.State is null or empty` ＝ **404 ではなくハンドラが登録されている**証拠 |

### 設定を確認・削除する

```powershell
dotnet user-secrets list                 # ClientSecret も表示されるので画面共有中は注意
dotnet user-secrets remove "Auth:Enabled"
dotnet user-secrets clear                # 全削除（＝匿名デモモードに戻す）
```

### Azure App Service で運用する場合

同じ5項目をアプリケーション設定として登録します（`:` を `__` に置換）。

```
Auth__Enabled / Auth__Authority / Auth__ClientId / Auth__ClientSecret / Auth__ProviderName
```

---

## 手順 3-B: 本番を構成 B（LINE 直結）へ切り替える

> ⚠️ **実行順序を守ってください。設定の切り替えは「最後」です。**
>
> 1. 実装をコミット → 2. **デプロイ** → 3. **デプロイ後の生存確認** → 4. 設定切替 → 5. **明示的な再起動** → 6. 本番検証
>
> デプロイ前に `Auth__Authority` を `access.line.me` に変えると、**LINE へリダイレクトはするが
> コールバックを受ける `/signin-line` が存在しない**状態になり、ユーザーが確実に 404 に落ちます。

### 3-B-1. 事前確認（デプロイ済みであることの証明）

> ⚠️ **【重要な訂正】`/signin-line` の 404 はデプロイ成否の判定に使えません。**
>
> 当初この手順書では「`GET /signin-line` が 404 でなければデプロイ済み」と書いていましたが、
> **これは誤りでした**（2026-08-11 に実測で判明）。`/signin-line` は OIDC ハンドラの
> `CallbackPath` に依存して登録されるルートです。切替前は `Auth__CallbackPath` が未設定＝既定の
> `/signin-oidc` なので、**新しいコードがデプロイされていても `/signin-line` は 404 のまま**です。
> このパスが 404 でなくなるのは **3-B-4 の切替が完了した後**です。

デプロイ成否は「**そのデプロイでしか存在しないページ**」で判定してください。

```powershell
$app = "https://<your-app>.azurewebsites.net"

# 1) まずアプリが生きているか（最優先。503 なら 3-B-1b へ）
curl.exe -s -o NUL -w "GET /          -> %{http_code}`n" "$app/"

# 2) 新しいコードにしか無いページで判定する（例）
curl.exe -s -o NUL -w "GET /one-touch -> %{http_code}`n" "$app/one-touch"
curl.exe -s -o NUL -w "GET /admin     -> %{http_code}`n" "$app/admin"
```

- ✅ `/` が **200** かつ新規ページが **200** → デプロイ済み。次へ進める
- ❌ `/` が **503** → 起動に失敗しています。**3-B-1b を実施してください**

### 3-B-1b. デプロイ後に 503 になったとき（実際に発生した障害）

`az webapp deploy` が次のエラーで終わることがあります。**これは「ファイル展開は成功したが、
アプリが起動できなかった」という意味**で、コードは既に本番に届いています。

```
ERROR: Deployment failed because the site failed to start within 10 mins.
InprogressInstances: 0, SuccessfulInstances: 0, FailedInstances: 1
```

**必ずコンテナの起動ログを読んでください。推測で対処しないこと。**

```powershell
# 方法1: 直近の起動ログを JSON で取得（早い）
az webapp log startup show -n <your-app> -g rg-mimamoritai-hackathon > $env:TEMP\startup.json
# JSON の content フィールドに \n 区切りでログが入っている。Unhandled / exit code で検索する

# 方法2: ログ一式をダウンロードして展開（確実。スタックトレースが読める）
az webapp log download -n <your-app> -g rg-mimamoritai-hackathon --log-file $env:TEMP\logs.zip
Expand-Archive $env:TEMP\logs.zip -DestinationPath $env:TEMP\applogs -Force
Get-ChildItem $env:TEMP\applogs\LogFiles\StartupLogs\*_failure.log | Get-Content -Tail 80
```

#### 実際に踏んだ障害: `DataProtection__KeyDirectory` 未設定で起動不能（exit code 134）

```
ContainerStream: Unhandled exception.
  System.InvalidOperationException: DataProtection:KeyDirectory must be configured with a durable,
  persistent path in non-Development environments (see docs/SECURITY.md).
     at Program.<Main>$(String[] args) in ...\src\MimamoriTai.Web\Program.cs:line 39
/opt/startup/startup.sh: line 20: 1872 Aborted (core dumped) dotnet "MimamoriTai.Web.dll"
Container has finished running with exit code: 134
ContainerStatus: Site is blocked due to multiple, consecutive cold start failures
ContainerStatus: Site: <your-app> stopped.
```

`Program.cs` の fail-fast ガード（非 Development 環境で `DataProtection:KeyDirectory` が
未設定なら起動時に throw する）が原因です。SwitchBot の世帯別資格情報がこの鍵リングで
暗号化されるため、揮発する鍵で起動させない設計になっています。**認証とは無関係です。**

対処:

```powershell
# Linux App Service で永続化されるのは /home 配下のみ。ここ以外だと再起動で鍵が消える
az webapp config appsettings set -g rg-mimamoritai-hackathon -n <your-app> `
  --settings 'DataProtection__KeyDirectory=/home/data/dataprotection-keys'

# 連続起動失敗でサイトごと停止していた場合、設定投入だけでは復旧しない
az webapp restart -g rg-mimamoritai-hackathon -n <your-app>
# （State: Stopped まで行っていたら az webapp start が必要）
```

投入後 20 秒間隔でポーリングし、`GET /` が 200 に戻ることを確認します（実測では2回目で復旧）。

> ⚠️ **`DataProtection__KeyDirectory` は絶対に削除しないでください。**消すと本番が起動不能になります。

### 3-B-2. 現在の Entra 設定を退避する（ロールバック可能にする）

`Auth:*` を直接上書きすると構成 A の値が失われます。**先に `AuthEntra:*` へ複製**してください。
（`AuthEntra:*` はアプリが読まない純粋な退避領域です。バインドされないので副作用はありません。）

App Service 側:

```powershell
$rg  = "rg-mimamoritai-hackathon"
$app = "<your-app>"

# 1) 現在値を JSON でローカルにバックアップ（画面に出さない）
az webapp config appsettings list -g $rg -n $app `
  | ConvertFrom-Json `
  | Where-Object { $_.name -like "Auth__*" } `
  | ConvertTo-Json | Out-File "$env:USERPROFILE\auth-backup-entra.json" -Encoding utf8

# 2) AuthEntra__* へ複製（退避）
$cur = az webapp config appsettings list -g $rg -n $app | ConvertFrom-Json
$pairs = @()
foreach ($k in "Enabled","Authority","ClientId","ClientSecret","CallbackPath","ProviderName") {
    $v = ($cur | Where-Object name -eq "Auth__$k").value
    if ($null -ne $v) { $pairs += "AuthEntra__$k=$v" }
}
az webapp config appsettings set -g $rg -n $app --settings $pairs
```

ローカル（User Secrets）側:

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets list | Out-File "$env:USERPROFILE\usersecrets-backup.txt" -Encoding utf8
# 上記ファイルから Auth:* の値を読み、AuthEntra:* として再投入する
```

### 3-B-3. 投入する `Auth__*` の一覧（構成 B）

| キー（App Service） | キー（User Secrets） | 入れる値 |
| --- | --- | --- |
| `Auth__Enabled` | `Auth:Enabled` | `true` |
| `Auth__Authority` | `Auth:Authority` | `https://access.line.me` |
| `Auth__ClientId` | `Auth:ClientId` | **LINE Login チャネルのチャネル ID**（数字10桁）。`LineLogin__ChannelId` と同じ値 |
| `Auth__ClientSecret` | `Auth:ClientSecret` | **LINE Login チャネルのチャネルシークレット**。`LineLogin__ChannelSecret` と同じ値 |
| `Auth__CallbackPath` | `Auth:CallbackPath` | `/signin-line` |
| `Auth__ProviderName` | `Auth:ProviderName` | （任意）Authority から自動判定されるため**設定不要** |

> `Line__*`（Messaging API 用）と `LineLogin__*`（ログイン用チャネルの控え）は**そのまま残してください**。
> アプリが実際にログインで読むのは `Auth__*` だけです。`LineLogin__*` は復旧用の保管です。

### 3-B-4. 切替コマンド

```powershell
# 値は変数経由で渡し、コンソールに出さない
$id  = "<LINE Channel ID>"
$sec = "<LINE Channel Secret>"
az webapp config appsettings set -g $rg -n $app --settings `
  "Auth__Enabled=true" `
  "Auth__Authority=https://access.line.me" `
  "Auth__ClientId=$id" `
  "Auth__ClientSecret=$sec" `
  "Auth__CallbackPath=/signin-line"

# ⚠️ 設定変更だけでは切り替わりません。必ず明示的に再起動してください（下記参照）
az webapp restart -g $rg -n $app
```

> ⚠️ **【実測で判明】`az webapp config appsettings set` だけでは切り替わりません。**
>
> 設定変更で App Service は再起動しますが、実測では変更直後に `GET /` が 200 に戻っても
> **`/auth/login` はまだ旧設定（Entra）へ 302 していました**（旧ワーカーが応答し続ける）。
> `az webapp restart` を明示的に打って初めて `access.line.me` へ変わりました。
> **切替後に期待どおりにならない場合、まず再起動を疑ってください。**設定を疑って値を
> いじり直すと事態が悪化します。

再起動後、`/auth/login` の Location が `access.line.me` になるまでポーリングします。

```powershell
for ($i=1; $i -le 20; $i++) {
    $r = Invoke-WebRequest -Uri "$appUrl/auth/login" -MaximumRedirection 0 -SkipHttpErrorCheck -TimeoutSec 90
    $h = if ($r.Headers.Location) { ([uri](($r.Headers.Location) -join '')).Host } else { '(none)' }
    "try{0,2} status={1} host={2}" -f $i, $r.StatusCode, $h
    if ($h -eq 'access.line.me') { "SWITCHED-TO-LINE"; break }
    Start-Sleep -Seconds 15
}
```

なお `Auth__ProviderName` は Authority から自動判定されるため本来不要ですが、明示したい場合は
`Auth__ProviderName=line` を追加しても構いません（実測環境では明示しています）。

LINE Developers 側のコールバック URL に
`https://<your-app>.azurewebsites.net/signin-line` が登録済みであることを確認してください
（本手順書作成時点で登録済み）。

### 3-B-5. 本番検証コマンド

```powershell
$app = "https://<your-app>.azurewebsites.net"

# 1) 302 になり、Location のホストが access.line.me であること
curl.exe -s -i -o - "$app/auth/login?returnUrl=/" | Select-String "^HTTP/|^location:"

# 2) redirect_uri が https で本番ホストであること（ここが ForwardedHeaders の検証点）
curl.exe -s -i "$app/auth/login?returnUrl=/" `
  | Select-String "location:" `
  | ForEach-Object { [uri]::UnescapeDataString($_.ToString()) } `
  | Select-String "redirect_uri=[^&]+"
```

- ✅ 期待 1: `HTTP/2 302` ＋ `location: https://access.line.me/oauth2/v2.1/authorize?...`
- ✅ 期待 2: `redirect_uri=https://<your-app>.azurewebsites.net/signin-line`
  （**`http://` になっていたら ForwardedHeaders が効いていません**。`UseMimamoriTaiForwardedHeaders()` が
  `UseAuthentication()` より**前**で呼ばれているか確認してください）
- 続けてブラウザで `$app/auth/login` を開き、LINE ログイン → `$app/auth/me` が
  `{"authenticated":true,...,"provider":"line",...}` を返すこと

#### 3-B-5 の実測結果（2026-08-11・本番で成功した記録）

以下は実際に本番 `<your-app>` で構成 B へ切り替えた直後の実測値です。
**新しく切り替えたときは、この値と一致することを確認してください。**

```
GET /              -> 200
GET /signin-line   -> 500   ← 404 から変化した = ルート登録済みの証拠
                              （code/state 無しの生 GET なので 500 が正常）
GET /auth/me       -> 200   {"authenticated":false,"displayName":null,"provider":null,"appUserId":null}
GET /one-touch     -> 200
GET /admin         -> 200

GET /auth/login    -> 302
   host          : access.line.me
   path          : /oauth2/v2.1/authorize
   redirect_uri  : https://<your-app>.azurewebsites.net/signin-line
   scope         : openid profile email      ← offline_access が付かない（LINE 分岐が効いている）
   response_type : code
   pkce          : S256
   client_id len : 10                        ← LINE チャネル ID の桁数

--- アサーション ---
host is access.line.me         : True
redirect_uri is HTTPS          : True   ← ForwardedHeaders が本番で効いている証拠
redirect_uri path /signin-line : True
no offline_access              : True
```

**Messaging API（見守り機能）に影響が無いことも必ず確認してください。**
`Line__*` と `Auth__*` は別系統なので理屈の上では無関係ですが、本番を触った以上は実測します。

```powershell
cd src/MimamoriTai.Web
$tok = ((dotnet user-secrets list | Select-String '^Line:ChannelAccessToken = ') -replace '^Line:ChannelAccessToken = ','').Trim()
$h = @{ Authorization = "Bearer $tok" }
Invoke-RestMethod -Uri 'https://api.line.me/v2/bot/channel/webhook/endpoint' -Headers $h
1..3 | ForEach-Object {
    Invoke-RestMethod -Uri 'https://api.line.me/v2/bot/channel/webhook/test' `
      -Headers $h -Method Post -ContentType 'application/json' -Body '{}'
}
```

実測結果:

```
webhook endpoint: https://<your-app>.azurewebsites.net/webhooks/line  active=True
test1: success=True statusCode=200 reason=OK
test2: success=True statusCode=200 reason=OK
test3: success=True statusCode=200 reason=OK
```

#### 切替前後の App Service 設定（キー名と長さのみ・値は記録しない）

| キー | 切替前 | 切替後 |
| --- | --- | --- |
| `Auth__Enabled` | 4 | 4 |
| `Auth__Authority` | 78（Entra） | **22**（`https://access.line.me`） |
| `Auth__ClientId` | 36（GUID） | **10**（LINE チャネル ID） |
| `Auth__ClientSecret` | 40 | **32**（LINE チャネルシークレット） |
| `Auth__CallbackPath` | （未設定） | **12**（`/signin-line`） |
| `Auth__ProviderName` | （未設定） | 4（`line`） |
| `AuthEntra__Authority` | （無し） | **78**（退避） |
| `AuthEntra__ClientId` | （無し） | **36**（退避） |
| `AuthEntra__ClientSecret` | （無し） | **40**（退避） |
| `AuthEntra__Enabled` | （無し） | **4**（退避） |
| `DataProtection__KeyDirectory` | （未設定＝起動不能） | **30**（`/home/data/dataprotection-keys`） |

退避が正しく取れたかは、長さだけでなく**値の厳密一致**で検証してください。

```powershell
$k = az webapp config appsettings list -g $rg -n $app -o json | ConvertFrom-Json
$m = @{}; foreach ($s in $k) { $m[$s.name] = $s.value }
foreach ($p in 'Enabled','Authority','ClientId','ClientSecret') {
    "{0,-14} identical={1}" -f $p, ($m["Auth__$p"] -ceq $m["AuthEntra__$p"])
}
```

切替スクリプトには「**退避が存在しなければ中断する**」ガードを入れておくと安全です。

```powershell
if (-not $m['AuthEntra__ClientSecret']) { throw "BACKUP MISSING - abort" }
```

### 3-B-6. ロールバック手順（構成 A へ戻す）

```powershell
# 退避しておいた AuthEntra__* を Auth__* へ書き戻す
$cur = az webapp config appsettings list -g $rg -n $app | ConvertFrom-Json
$pairs = @()
foreach ($k in "Enabled","Authority","ClientId","ClientSecret","CallbackPath","ProviderName") {
    $v = ($cur | Where-Object name -eq "AuthEntra__$k").value
    if ($null -ne $v) { $pairs += "Auth__$k=$v" }
}
az webapp config appsettings set -g $rg -n $app --settings $pairs
az webapp config appsettings set -g $rg -n $app --settings "Auth__CallbackPath=/signin-oidc"
az webapp restart -g $rg -n $app   # ⚠️ 3-B-4 と同じ理由で明示的な再起動が必須
```

> 退避 `AuthEntra__*` が揃っている限り、**構成 A（Entra ログイン）へ完全復帰できます**。
> 匿名モードよりこちらの方が望ましい復帰先です（本番のログイン機能が生きたまま戻せる）。

**緊急停止（ログイン機能をまるごと無効化して匿名デモモードへ戻す）:**

```powershell
az webapp config appsettings set -g $rg -n $app --settings "Auth__Enabled=false"
```

`Auth__Enabled=false` にすると認証パイプラインが一切登録されず、アプリは匿名のデモモードで
正常に動作します（`/auth/login` は 404 ではなく **HTTP 200 ＋ 日本語の案内文**、`/auth/me` は
`provider:"dev"`）。**これが最も安全なロールバックです。**

---

## 手順 4: ローカル開発で HTTPS を使う

LINE Developers も Entra も、**`localhost` 以外のコールバック URL は HTTPS 必須**です。
既定の `http` プロファイル（`http://localhost:5234`）は HTTP なので、以下のいずれかを使ってください。

### 方法 1: dotnet dev-certs ＋ https プロファイル（推奨・最短）

```powershell
dotnet dev-certs https --trust     # 初回のみ。証明書の信頼を求めるダイアログで「はい」を選ぶ
cd src/MimamoriTai.Web
dotnet run --launch-profile https  # https://localhost:7215 と http://localhost:5234 で待ち受ける
```

登録するリダイレクト URI: `https://localhost:7215/signin-oidc`

証明書の状態確認 / 作り直し:

```powershell
dotnet dev-certs https --check --trust
dotnet dev-certs https --clean
dotnet dev-certs https --trust
```

### 方法 2: devtunnel（実機の LINE アプリから検証したい場合）

スマートフォンの LINE アプリから検証する場合は `localhost` に到達できないため、
公開 HTTPS URL が必要です。

```powershell
devtunnel user login
devtunnel host -p 5234 --allow-anonymous
```

表示された `https://<id>.devtunnels.ms` を、リダイレクト URI / コールバック URL として
Entra と LINE の両方に登録します。パスは `/signin-oidc` です。

> アプリは `UseMimamoriTaiForwardedHeaders()` で `X-Forwarded-Proto` を解釈するため、
> トンネル経由でも `redirect_uri` は正しく `https` として組み立てられます。

---

## 手順 5: 動作確認

```powershell
cd src/MimamoriTai.Web
dotnet run --launch-profile https
```

1. **設定が入ったことを確認**
   ```powershell
   curl.exe -i "https://localhost:7215/auth/login?returnUrl=/"
   ```
   - ✅ 期待: **HTTP 302** ＋ `Location:` が IdP の `authorize` エンドポイント
   - ❌ `HTTP 200` ＋「ログイン機能は現在設定されていません」→ `Auth:*` が未投入（手順 3 に戻る）

2. **ブラウザでログイン**: `https://localhost:7215/auth/login?returnUrl=/` を開き、
   LINE でログインします。構成 A では Entra のサインイン画面の **LINE ボタン**から入ります。

3. **サインイン状態を確認**
   ```powershell
   curl.exe "https://localhost:7215/auth/me"
   ```
   - ✅ 期待: `{"authenticated":true,"displayName":"<LINE の表示名>","provider":"line","appUserId":"<GUID>"}`

4. **DB に AppUser が作られたことを確認**（SQLite デモ DB）
   ```powershell
   dotnet tool install --global dotnet-suggest 2>$null
   # sqlite3 が使える場合
   sqlite3 src/MimamoriTai.Web/mimamoritai.db "SELECT Id, DisplayName, IdentityProvider, ExternalSubject, LineUserId, Email FROM AppUsers;"
   ```
   - ✅ 期待: `IdentityProvider = 'line'` の行があり、**`LineUserId` に LINE の `sub`（`U` で始まる ID）が入っている**
   - 書き込みは `CurrentUserAccessorFactory.ResolveAndProvisionAsync`（`AuthenticationExtensions.cs`）が
     OIDC の `OnTokenValidated` イベントで一度だけ実行します。

5. **ログアウト**: `https://localhost:7215/auth/logout`
   - ✅ 構成 A: Entra の `end_session_endpoint` へ 302
   - ✅ 構成 B: `/` へ 302（LINE には `end_session_endpoint` が無いため Cookie 削除のみ）

### 実測ログ（構成 B・実際の LINE アカウントで完走）

以下は本番のチャネル `2011065310`（プロバイダー「見守り隊」）を使い、
`http://localhost:5301` で実際にログインを完走させたときの実測値です。

| # | 検証項目 | 実測結果 |
| --- | --- | --- |
| 1 | `dotnet build` | 0 エラー |
| 2 | `GET /` | **HTTP 200** |
| 3 | `GET /auth/login` | **HTTP 302** → `https://access.line.me/oauth2/v2.1/authorize?client_id=2011065310&redirect_uri=http%3A%2F%2Flocalhost%3A5301%2Fsignin-line&response_type=code&scope=openid%20profile%20email&code_challenge=...&code_challenge_method=S256&nonce=...&state=...` |
| 4 | LINE ログイン画面・同意画面 | 通過（同意項目は「メインプロフィール情報」「あなたの内部識別子」の2つ。email は未申請のため非表示） |
| 5 | `GET /signin-line?code=...` | **HTTP 302** → `/`（`IssuerSigningKeyResolver` 追加前は HTTP 500 / IDX10517） |
| 6 | `GET /auth/me` | **HTTP 200** `{"authenticated":true,"displayName":"<表示名>","provider":"line","appUserId":"<GUID>"}` |
| 7 | `AppUsers` テーブル | `IdentityProvider='line'` / `ExternalSubject='U...'` / **`LineUserId` に33文字の LINE ユーザー ID** / `DisplayName` に LINE の表示名 |
| 8 | `dotnet test` | **300 件合格 / 0 失敗** |

> **注意:** ローカル開発では `http://localhost:5301/signin-line`（**HTTP**）を LINE Developers の
> コールバック URL に登録しても**受理されます**。LINE Login は localhost に限り HTTPS を強制しません。
> 本番ホストでは HTTPS が必須です。

> **DB の読み方:** SQLite は WAL モードで動くため、`.db` 本体だけをコピーしても中身は空に見えます。
> `mimamoritai-demo.db` / `-wal` / `-shm` の**3ファイルをまとめて**コピーしてから読んでください。
> 実体は `src/MimamoriTai.Web/bin/Debug/net10.0/mimamoritai-demo.db`（`AppContext.BaseDirectory` 配下）です。

---

## トラブルシューティング

### 本番が **HTTP 503** になる（デプロイ直後）

**認証設定を疑う前に、まず起動ログを読んでください。** 実測では原因が認証と無関係でした
（`DataProtection:KeyDirectory` 未設定による起動時 fail-fast / exit code 134）。詳細と復旧手順は
[3-B-1b](#3-b-1b-デプロイ後に-503-になったとき実際に発生した障害) を参照してください。

要点だけ再掲します。

| 症状 | 確認コマンド | 対処 |
| --- | --- | --- |
| `az webapp deploy` が「site failed to start within 10 mins」で exit 1 | `az webapp log startup show` / `az webapp log download` | ファイル展開は成功している。起動失敗の原因をログで特定する |
| `Container has finished running with exit code: 134` | 起動ログに `Unhandled exception` が出ている | 例外メッセージのとおりに設定を投入する |
| `Site is blocked due to multiple, consecutive cold start failures` | `az webapp show --query state` | 設定投入だけでは復旧しない。`az webapp restart`（Stopped なら `az webapp start`）が必要 |
| 設定を変えたのに挙動が変わらない | `/auth/login` の Location ホスト | 旧ワーカーが応答している。`az webapp restart` を明示的に打つ |

### `/auth/login` が 302 にならず、日本語の案内文が返る

`Auth:Enabled` / `Authority` / `ClientId` / `ClientSecret` の **4つすべて**が揃わないと
`AuthOptions.IsConfigured` が `false` のままで、認証パイプラインが一切登録されません
（未設定でも匿名で動く設計のため、エラーにはなりません）。`dotnet user-secrets list` で確認してください。

**これは仕様であり、正常な動作です（実測で維持を確認済み）。**

| エンドポイント | `Auth:Enabled=false` 時の実測結果 |
| --- | --- |
| `GET /` | **HTTP 200**（ダッシュボードが通常どおり表示される） |
| `GET /auth/login?returnUrl=/` | **HTTP 200** ＋ `ログイン機能は現在設定されていません（デモモードで動作中です）。` |
| `GET /auth/me` | **HTTP 200** `{"authenticated":false,"displayName":null,"provider":"dev","appUserId":null}` |
| `GET /auth/logout` | **HTTP 200** ＋ 同じ案内文 |

**404 にも 500 にもならない**のが重要です。404 が返る場合はアプリが古いビルドか、デプロイされて
いません。

### `redirect_uri` が `http://` で生成される（App Service などプロキシ配下）

App Service は TLS を終端し、アプリには HTTP でリクエストを転送します。素の状態では
`redirect_uri` が `http://<host>/signin-line` として生成され、LINE / Entra 側の登録値（HTTPS）と
**不一致になって弾かれます。**

対処は実装済みです。`Program.cs` が `app.UseMimamoriTaiForwardedHeaders()` を
**`app.UseAuthentication()` より前**で呼び、`X-Forwarded-Proto` / `X-Forwarded-For` を反映します
（`KnownIPNetworks` / `KnownProxies` をクリアしてあるため App Service の可変プロキシIPでも動作します）。
検証方法は「手順 3-B-5」の 2 番目のコマンドを参照してください。

### `AADSTS50011` / `redirect_uri does not match` / `400 Bad Request`

リダイレクト URI の完全一致に失敗しています。次を確認してください。

- スキーム（`http` / `https`）とポート番号が一致しているか
- 末尾スラッシュの有無、大文字小文字
- 構成 A で、LINE 側に**アプリの URL を登録していないか**（正しくは **Entra のコールバック URL**）
- リバースプロキシ配下の場合、`X-Forwarded-Proto` が転送されているか

### `IDX10205: Issuer validation failed`

Entra External ID は discovery の `issuer` ホストと Authority のホストが異なる場合があります。
`AuthenticationExtensions.BuildValidIssuers` が `<tenantId>.ciamlogin.com` 形式と設定値の両方を
許可済みです。それでも失敗する場合は、実際の issuer を確認して Authority を合わせてください。

```powershell
(Invoke-RestMethod "https://<tenantId>.ciamlogin.com/<tenantId>/v2.0/.well-known/openid-configuration").issuer
```

### Entra のサインイン画面に LINE ボタンが出ない

**最頻出の原因です。** 次の順で確認してください。

1. `scripts/setup-line-entra-idp.ps1` を実行したか
2. Entra 管理センター › 外部 ID › すべての ID プロバイダー に **LINE** が存在するか
3. 対象の**ユーザーフロー**に LINE がリンクされているか
4. そのユーザーフローに**アプリが紐付いている**か

### `IDX10517: Signature validation failed. The token's kid is missing`（HTTP 500・構成 B）

**構成 B で最初に必ず踏むエラーです。** LINE の `id_token` は **チャネルシークレットを鍵とする
HS256（対称署名）** で発行され、JWT ヘッダーに `kid` を持ちません。一方 LINE の discovery は
`id_token_signing_alg_values_supported: ["ES256"]` としか宣言しておらず、`OpenIdConnectHandler` は
JWKS（`https://api.line.me/oauth2/v2.1/certs`）の ES256 公開鍵だけを試して失敗します。

対処は実装済みです。`AuthenticationExtensions.cs` で `IsLineAuthority` のときだけ
`TokenValidationParameters.IssuerSigningKeyResolver` を差し込み、`kid` が無いトークンには
チャネルシークレットから作った `SymmetricSecurityKey` を返します（`kid` があるトークンは
従来どおり JWKS の鍵で検証）。したがって `Auth:ClientSecret` に**正しいチャネルシークレット**が
入っていることが必須です。値が誤っていると同じ場所で `IDX10503: Signature validation failed` に
変わります。

### `Cannot redirect to the end session endpoint`（HTTP 500）

LINE の discovery には `end_session_endpoint` がありません。現在は
`AuthOptions.SupportsRemoteSignOut` が `false` になり、Cookie のみのサインアウトへ
自動的にフォールバックします。この例外が再発する場合は `Auth:Authority` が
`access.line.me` を含んでいるか確認してください。

### `offline_access` スコープが使えない

LINE は `offline_access` をサポートしません（`scopes_supported` は `openid profile email` のみ）。
`Auth:Authority` が `access.line.me` の場合、コード側で自動的にこのスコープを除外します。
Entra 側の IdP 設定でも `openid profile email` のみを指定してください
（`setup-line-entra-idp.ps1` はそうなっています）。

### `email` クレームが返らない

LINE Developers の「OpenID Connect」タブで `email` 権限の申請が承認されているか確認してください。
未承認の間は `email` クレームが返らず、`AppUser.Email` は空のままになります
（ログイン自体は成功します）。**未申請でも `scope` に `email` を含めてエラーにはなりません**
（実測：同意画面には「メインプロフィール情報」「あなたの内部識別子」のみが表示され、
email は黙って無視される）。そのため急ぐ場合は申請せずに進めて構いません。

### Graph API 呼び出しが 401/403 で失敗する

`az login --tenant <TenantId> --allow-no-subscriptions` を再実行し、CIAM テナントに対して
十分な権限（外部 ID プロバイダー管理者などのロール）を持つアカウントでサインインしてください。

---

## 補足: LIFF は必要か

**現時点では不要です。** LIFF（LINE Front-end Framework）は、**LINE アプリ内蔵ブラウザで動く
ミニアプリ**を作るための仕組みです。見守り隊は通常のブラウザで動く Blazor Web App なので、
ユーザー認証には LINE Login（本ドキュメント）だけで足ります。

LIFF が必要になるのは次のようなケースです。

- LINE のトーク画面から**アプリ内ブラウザで**ダッシュボードを開き、ログイン操作なしで
  ユーザーを特定したい
- LINE のトーク画面へメッセージを送り返す（`liff.sendMessages`）など、LINE クライアント固有の
  API を使いたい

これらが要件に含まれる場合は、別途 LIFF アプリの追加登録が必要です。

