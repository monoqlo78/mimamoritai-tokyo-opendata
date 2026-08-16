# LINEワンタッチ通報 セットアップ

高齢のご本人が、文字入力なしでリッチメニューの1タップだけで家族に状況を伝えられるようにする機能です。この文書は、実際のLINE公式アカウントを取得したあとに行う設定手順をまとめたものです。

## 1. 専用のLINE公式アカウントが必要

**見守り隊専用の新しいLINE公式アカウント（チャネル）を作成してください。既存のSDSiGNER用アカウントを流用しないでください。** 用途（Botの応答内容、リッチメニュー、Webhook）が異なるため、共用すると通知の誤配信や設定の衝突が起きます。

専用アカウントは作成・接続済みです。

- アカウント名: `見守り隊`
- Basic ID: `@755ykcrx`
- 友だち追加URL: `https://line.me/R/ti/p/@755ykcrx`
- QRコード: `https://qr-official.line.me/gs/M_755ykcrx_GW.png`
- 本番Webhook: `https://<your-app>.azurewebsites.net/webhooks/line`
- LINE Developersプロバイダー: `見守り隊`（ID `2005421841`）
- Messaging APIチャネルID: `2011034584`

アカウント作成手順自体は `docs/LINE_SETUP.md` の「1. LINE Developers でプロバイダーを作成」〜「2. Messaging API チャネルを作成」と同じです（プロバイダー名・チャネル名は「見守り隊」など専用の名称にしてください）。

## 2. 認証情報の設定

チャネルの **Basic settings** から Channel secret を、**Messaging API** タブから Channel access token（長期）を発行し、以下を実行します。

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "Line:ChannelAccessToken" "<新しいチャネルのアクセストークン>"
dotnet user-secrets set "Line:ChannelSecret" "<新しいチャネルのシークレット>"
dotnet user-secrets set "Line:Enabled" "true"
```

`Line:Enabled` が `false`（既定）、またはトークン／シークレットが空の場合、アプリは自動的に `MockLineMessagingClient` にフォールバックし、実際のLINEには何も送信されません。これらの値は `appsettings.json` に書かないでください（User Secrets または本番環境変数のみ）。

## 3. Webhook URLの設定

「Messaging API」タブの **Webhook settings** に以下を設定します。

```
https://<公開ホスト>/webhooks/line
```

- **Use webhook**: ON
- **Verify** で疎通確認（アプリ起動が必要）
- 「LINE Official Account Manager」側の応答メッセージ・自動応答メッセージは OFF にしてください（`docs/LINE_SETUP.md` の「5. 自動応答をオフにする」を参照）。

署名検証（`X-Line-Signature`、HMAC-SHA256）はアプリ側で必須になっており、`Line:ChannelSecret` が正しく設定されていないとWebhookは常に401を返します。

## 4. リッチメニューのセットアップ

> **状態: 適用済み。** 2026-08-12 にミマモ版リッチメニュー（`richmenu-4bddafe272307d5a7e3507ec9610f208`）を全ユーザーの既定として適用し、旧フクロウ版は削除済みです。以下は作り直したいときの手順です。

ご本人のトーク画面下部に、常時表示される6つの大きなボタン（リッチメニュー）を設定します。専用アカウントのアクセストークンが発行できたら、以下のコマンドを実行してください（PowerShell 7 が必要です）。

```powershell
./scripts/setup-line-rich-menu.ps1 -ChannelAccessToken "<チャネルアクセストークン>"
```

- 既定では、自作の3Dキャラクター「ミマモ」のCGを組み込んだ `assets/line-rich-menu.png` を使用します。元画像は `src/MimamoriTai.Web/wwwroot/images/mimamo-robot-opus.png`（Webの3Dビューアと同じミマモ）です。
- 置換は安全な順序で行います。新しいメニューの作成、画像アップロード、デフォルト設定、設定確認がすべて成功したあとにだけ、古い `MimamoriTai-` メニューを削除します。途中で失敗しても、現在動いているメニューは残ります。
- メニュー画像を作り直す場合は `assets/create-line-rich-menu.ps1` を実行すると2500×1686pxの画像が再生成されます（別のキャラクター画像を使いたいときは `-MascotPath` で指定。縦横比は保たれます）。
- `-ImagePath ''` を明示した場合は、従来の簡易画像を `scripts/generated/line-rich-menu.png` に生成できます。
- Windows以外の環境、または画像を差し替えたい場合は `-ImagePath` で独自のPNG（2500×1686px）を指定できます。寸法とファイル形式（PNGシグネチャ）はスクリプト側で検証されます。
- トークンは一切ログや画面に出力されません。実行結果として、作成されたリッチメニューID・デフォルト設定の確認結果のみが表示されます。

```powershell
# 生成画像を確認だけしたい／独自画像を使いたい場合
./scripts/setup-line-rich-menu.ps1 -ChannelAccessToken "<token>" -ImagePath ".\my-menu.png"
```

## 4-1. 公式アカウントのアイコンをミマモに差し替える（実施済み）

> **状態: 完了。** 2026-08-12 に `assets/line/mimamo-line-account-icon.png` をアップロード済みで、公式アカウント「見守り隊（@755ykcrx）」のアイコンは開発初期のフクロウからミマモに置き換わっています。以下は作り直したいときの手順です。

トークの吹き出しに出る送信者名とアイコンは Messaging API 側でコードから上書きしています（後述）。しかし**公式アカウント本体のプロフィール画像**は Messaging API では変更できず、LINE Official Account Manager での操作が必要です。アップロードするファイルは用意済みです。

1. `assets/line/mimamo-line-account-icon.png`（1024×1024・不透明PNG）を手元に用意します。作り直したいときは `./assets/create-line-account-icon.ps1` を実行してください。
2. [LINE Official Account Manager](https://manager.line.biz/) にログインし、見守り隊のアカウントを選びます。
3. **設定** → **アカウント設定** → 基本設定の **プロフィールを編集** を開きます（ビジネスプロフィールページ設定が開きます）。
4. **プロフィール画像** の右下にある丸いボタン → **アップロード** から上記PNGを選び、切り抜き範囲を画像全体に広げて **OK** → **公開** を押します。
5. 反映まで数分かかります。トーク画面を開き直してミマモの顔になっていることを確認してください。

> **一度変更すると1時間は再変更できません。** 切り抜き範囲が画像全体（正方形いっぱい）になっていることを確認してから公開してください。既定の選択範囲は中央の一部だけなので、そのままOKすると顔が拡大されて切れます。

> LINEはアイコンを円形に切り抜いて表示します。上記PNGは透過部分を淡いミントで塗りつぶし、円の内側に顔が収まるよう余白を取ってあります（透過PNGをそのまま上げると背景が黒くなります）。

## 4-2. 吹き出しの送信者名・アイコン（コード側・設定のみ）

`Line:SenderName` と `Line:SenderIconPath`、および `Line:PublicBaseUrl` を設定すると、アプリが送るすべてのメッセージ（reply / push / Flex）に `sender` が付与され、その吹き出しだけ表示名とアイコンが「ミマモ」になります。

```json
"Line": {
  "PublicBaseUrl": "https://<your-app>.azurewebsites.net",
  "SenderName": "ミマモ",
  "SenderIconPath": "/images/mimamo-avatar.png"
}
```

- `PublicBaseUrl` が空、またはHTTPSでない場合は `sender` を**付けずに**送信します（LINEは自サーバーから画像を取得するため、`http://localhost` では成立しないため）。
- `SenderName` は LINE の上限である20文字を超えると `sender` ごと省略されます。メッセージ本体が400で弾かれて家族に届かないことを避けるためです。

## 4-3. LINEの中でミマモを動かす（LIFF）

> **状態: LIFFアプリ登録済み。** 2026-08-12 に LINEログインチャネル「見守り隊ログイン」（チャネルID `2011065310`）へ LIFF アプリ **見守り隊 今日の様子** を作成しました。LIFF ID は `2011065310-k0R1hHKz`、LIFF URL は `https://liff.line.me/2011065310-k0R1hHKz` です。サイズ Full / Scope `openid` `profile` / 友だち追加オプション Off。これらは公開識別子のため `appsettings.json` に直接入れてあります（環境変数や user-secrets で上書き可能）。**残作業は本番へのデプロイのみ**で、エンドポイント `https://<your-app>.azurewebsites.net/liff` が公開されれば動作します。

`/liff` は LINE のトークから開く縦長ページです。Webと同じ three.js ビューアでミマモが動き、「今日の様子」を表示します。**`Line:LiffId` が未設定のあいだはこの機能は表示されません**（URLを直接開いても案内文だけが出ます）。

新しく作り直す場合の手順:

1. [LINE Developers](https://developers.line.biz/console/) で対象チャネルを開き、**LIFF** タブ → **追加** をクリックします。
2. サイズは **Full**、エンドポイントURLに `https://<your-app>.azurewebsites.net/liff` を入力します。
3. **Scope** で `profile` と `openid` にチェックを入れます（`openid` がないとIDトークンが発行されず、世帯を特定できません）。
4. 発行された **LIFF ID**（`1234567890-AbCdEfGh` 形式）と、同じチャネルの **Channel ID**（数字）を設定に入れます。

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "Line:LiffId" "<発行されたLIFF ID>"
dotnet user-secrets set "Line:LiffChannelId" "<チャネルID（数字）>"
```

- `Line:LiffChannelId` はブラウザーから受け取ったIDトークンを LINE の `/oauth2/v2.1/verify` で検証するために使います。**未設定だと検証できないため、`/liff` は常に「LINEからひらいてください」の状態のまま**になり、世帯データは一切表示されません。ブラウザーが自称するユーザーIDを信用しない、という設計です。
- 世帯の特定は既存の `LineRecipient`（連携コードで紐づけた行）をそのまま使います。未連携のユーザーには連携コードの案内だけが出ます。
- 3Dモデル（GLB）は7.6MBあります。まず静止画（`mimamo-robot-opus.png`）を表示し、そのあと3Dに差し替えます。端末が「視差効果を減らす」設定のときはGLBを取得せず静止画のままにし、タップしたときだけ3Dを読み込みます。
- リッチメニューの「Web版」タイルからLIFFを開きたい場合は、メニュー作成時に `-WebAppUrl` を渡してください。URIアクション先を持っているのは `scripts/setup-line-rich-menu.ps1` です（`assets/create-line-rich-menu.ps1` は画像を描くだけで、リンク先は持っていません）。

  ```powershell
  ./scripts/setup-line-rich-menu.ps1 -ChannelAccessToken "…" -WebAppUrl "https://liff.line.me/<LIFF ID>"
  ```

  **LIFF ID を発行するまでは差し替えないでください。** `Line:LiffId` が未設定のあいだ `/liff` は案内文しか出さないため、タイルを押しても何も起きない状態になります。既定の `/one-touch` は設定なしで動くので、それまではそのままにしておきます。なお指定するのは `https://liff.line.me/<LIFF ID>` であって `https://…/liff` ではありません。後者を直接開くとLINEのログイン文脈が無く、IDトークンを取得できません。

## 5. ボタンの動作


各ボタンはLINEの `postback`（一部は `message`）アクションとして送信され、`WebhookEndpoints` → `LinePostbackActionService`（`src/MimamoriTai.Core/Application/LinePostbackActionService.cs`）が処理します。

| ボタン表示 | 送信データ | 返信内容（本人へ） | 家族への通知 | 記録 |
| --- | --- | --- | --- | --- |
| 助けて | `action=emergency` | 「家族に知らせました」という趣旨のやさしい日本語（他に連絡先がない場合は119番を案内） | 送信者本人を除く、世帯内の他のアクティブなLINE連絡先全員へ高優先度のテキスト（本人の名前・日本時間のタイムスタンプ入り、医療的な断定はしない） | `FamilyMessage` として家族フィードに残る |
| 体調が悪い | `action=unwell` | 「家族に知らせます」という趣旨の返信 | 他の連絡先へ「体調が悪い」旨のやさしい通知 | `FamilyMessage` として記録 |
| 大丈夫 | `action=okay` | 「大丈夫を受け付けました」 | 送信なし（緊急性がないため） | `FamilyMessage` として記録 |
| 今日の様子 | `action=status` | Fabric Data Agentへ最大2秒で問い合わせ、応答できない場合はローカルの当日活動データから生活リズムを返信 | 送信なし | Webhookの安全な構造化ログに記録 |
| 家族に連絡 | `action=contact_family` | 「家族に連絡しました」という趣旨の返信 | 他の連絡先へ連絡依頼の通知 | `FamilyMessage` として記録 |
| メッセージ | メッセージ「相談したいです」 | 通常のテキストメッセージとして `AssistantOrchestrator` が応答 | （通常のチャット経路と同じ） | 既存の会話ログに準拠 |

「他の連絡先」は、実行時点でその世帯に登録されている、送信者自身を除くアクティブな `LineRecipient` 全員です。

## 6. ユーザー種別（本人／家族）に関する制限

現在の `LineRecipient` エンティティには「本人（ご利用者）」と「家族」を区別するロール項目がありません。そのため本機能は、安全に判定できない役割推定を行わず、代わりに「送信者本人以外の、世帯内のアクティブな連絡先すべて」へ通知する設計にしています。

- 世帯にご本人と家族が1人ずつしか登録されていない典型的な運用では、想定どおり「本人→家族へ通知」として機能します。
- 家族が複数人いる世帯では、ボタンを押した人（通常は本人）以外の全員に通知が届きます。家族の誰かがLINEグループ経由でボタンを操作した場合も、その人を除く全員に届く点に注意してください。
- 将来的にロールを追加する場合は、`LineRecipient` にロール項目を追加したうえで `LinePostbackActionService.PushToOthersAsync` の宛先解決を見直してください。

## 7. 動作確認手順

1. 上記の手順1〜4を実施し、`dotnet run --project src/MimamoriTai.Web` でアプリを起動（またはデプロイ先を稼働）させます。
2. 見守り隊専用アカウントを友だち追加し、フォローイベントの歓迎メッセージ（リッチメニューの6ボタンの説明）が届くことを確認します。
3. リッチメニューの各ボタンを実際にタップし、以下を確認します。
   - 「助けて」: 本人へやさしい確認メッセージが返り、他の家族連絡先に緊急通知が届く（家族フィード・ダッシュボードにも表示される）。
   - 「体調が悪い」「家族に連絡」: それぞれの通知が家族側に届く。
   - 「大丈夫」: 本人への返信のみで、家族へのPush通知が発生しないこと。
   - 「今日の様子」: Fabricまたはローカルフォールバックから、当日の活動に基づく様子説明が返る。
4. 家族が1人も登録されていない、または他のアクティブな連絡先がいない世帯で「助けて」を押し、119番を案内する返信になることを確認します。
5. `dotnet test` を実行し、`LinePostbackActionServiceTests` を含む全テストが成功することを確認します（実チャネルがなくてもこのテストはモックで完結します）。
