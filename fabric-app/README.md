# 見守り隊 運用コンソール (Fabric App / Rayfin)

Microsoft Fabric Apps (Rayfin) 上で動く、見守り隊の**運用者向け**コンソールです。
既存の Blazor アプリ (`src/MimamoriTai.Web`) の `/admin` と同じ観点を、Fabric 側のデータ基盤に載せて提供します。

- **Blazor 側 `/admin`** … アプリ DB を直接読む、リアルタイムの運用画面
- **本アプリ（Fabric）** … 非個人情報のスナップショットを Fabric の SQL に置き、Fabric SSO で運用者に開放する

## スコープと個人情報の扱い

本アプリは**居住者の個人情報を持ちません**。

- `HouseholdSnapshot` … 世帯ごとの件数・状態のみ（人数、デバイス数、SwitchBot 状態、LINE 受信者数、直近リスクレベル）
- `AlertRecord` … 通知の発生記録のみ。`WatchAlert.Message`（家族向け本文＝居住者名や行動を含みうる）は**意図的に持ち込みません**。機械生成の `reason` のみ保持します。

静的コンテンツは公開 URL から配信されるため、フロントエンドにシークレットを埋め込まないでください。

## 開発

```bash
# Fabric にデプロイしつつローカル開発サーバーを起動
npm run dev

# 初回のみ DB マイグレーションを適用
npm run rayfin:db
```

[http://localhost:5173](http://localhost:5173) を開きます。

Fabric に接続せず型と単体テストだけ確認したい場合:

```bash
npx tsc -b      # 型チェック（rayfin env を経由しない）
npm test        # vitest
npm run lint
```

ローカル実行時 (`isLocalBackend()`) は Fabric を呼ばずサンプルデータを返すため、Fabric 容量が無い状態でも UI を確認できます。

## 前提条件

- Fabric 容量の割り当て
- テナント管理者による **Fabric Apps ワークロードの有効化**

これらが無い場合 `rayfin up` は実行できません。

## データ投入（本番 → Fabric）

`scripts/` に本番 Azure SQL から Fabric へスナップショットを流し込む同期スクリプトがあります。
`rayfin up` でテーブルが作られた後に実行してください。

```bash
# 本番DBを読み取り、Fabric の SQL に MERGE する
pwsh ./scripts/sync-to-fabric.ps1
```

- `scripts/extract-snapshot.sql` … `AdminConsoleService.LoadAsync` と同じ集計を行う **読み取り専用**クエリ。`mimamori` スキーマのみを参照します
- `scripts/extract-snapshot.ps1` … 上記を実行して `snapshot.json` を出力（`.gitignore` 済み）
- `scripts/sync-to-fabric.ps1` … 抽出から Fabric への MERGE までを一括実行
- `scripts/semantic-model-views.sql` … Power BI 用に型を戻したビューを作る（後述）

認証は呼び出し元の Entra トークン（`az account get-access-token`）を使うため、
接続シークレットはリポジトリに保存されません。本番DBへの書き込みは行いません。

行のキーは冪等です（世帯は `householdId` から導出した固定 GUID、通知は元の `WatchAlert.Id`）。
そのため再実行しても重複しません。

## Power BI から見る

Fabric SQL データベースは OneLake へ自動でミラーされるので、Power BI からは
そのまま接続できます。ただし Rayfin のエンティティは数値も日時も `@text()`
（NVARCHAR）で持っているため、テーブルを直接指すと全列がテキストになり、
合計も時系列も組めません。

`scripts/semantic-model-views.sql` を実行すると、`TRY_CONVERT` で型を戻した
6 つのビューが作られます。セマンティックモデルにはテーブルではなくこのビューを
載せてください。

**このスクリプトは 2 か所で実行します。** ビューは OneLake にミラーされない
（運ばれるのはテーブルだけ）ため、SQL データベース側だけに作ると Power BI から
見えません。SQL データベースと SQL 分析エンドポイントの両方に流してください。

| ビュー | 中身 |
| --- | --- |
| `v_Household` | 世帯ごとの現在の運用状況 |
| `v_Alert` | 通知の記録と成否 |
| `v_ActivityHourly` | 機器の1時間ごとの動きと電力量 |
| `v_OutdoorHourly` | 屋外の気温・湿度・暑さ指数 |
| `v_AiRouterCall` | AI 呼び出しの回数と応答時間 |
| `v_Date` | 日付ディメンション |

「屋外の気温」と「家の中の電力」を同じグラフに並べるには `v_Date` を
「日付テーブルとしてマーク」してリレーションを張る必要があります。この構成は
`scripts/gen-semantic-model.py` が TMDL として生成し、
`scripts/deploy-semantic-model.ps1` が Fabric へ配置します（DirectQuery）。
手順の全体は `docs/FABRIC_SETUP.md` の「6. Power BI で可視化する」にあります。

欠測は空文字で保存されており、`TRY_CONVERT` が NULL に落とすので 0 とは
区別されます（0℃ は真冬の正当な観測値なので混ぜられません）。

`SwitchBotConnection.Encrypted*` と `WatchAlert.Message` は抽出クエリで**選択していません**。

## 構成

```text
├── rayfin/
│   ├── rayfin.yml                  # Fabric サービス構成 (auth/data/staticHosting)
│   └── data/
│       ├── HouseholdSnapshot.ts    # 世帯スナップショット（非個人情報）
│       ├── AlertRecord.ts          # 通知履歴（本文は持たない）
│       ├── ActivityBucket.ts       # 機器イベントの時間別ロールアップ
│       └── schema.ts               # 型付きクライアントが参照するスキーマ
├── src/
│   ├── main.tsx                    # エントリポイント + Rayfin クライアント初期化
│   ├── App.tsx                     # ルーティングと認証ゲート
│   ├── hooks/AuthContext.tsx       # 認証ヘルパーの React コンテキスト
│   ├── components/
│   │   ├── AuthPage.tsx            # サインイン UI
│   │   ├── charts.tsx              # 依存ゼロの SVG グラフ群
│   │   └── DataFlowCanvas.tsx      # WebGL2 のデータフロー図
│   ├── pages/HomePage.tsx          # 運用コンソール UI
│   └── services/
│       ├── monitoring.ts           # 世帯・通知の取得と集計（純関数 summarize / sortHouseholds）
│       ├── analytics.ts            # グラフ・図の数値を導出（表と同じ行から計算）
│       ├── snapshotFallback.ts     # 自動生成。バックエンド不達時の退避データ
│       ├── rayfinClient.ts         # 型付き Rayfin クライアント
│       ├── MockAuthService.ts      # ローカル開発用（email/password）
│       └── RayfinAuthService.ts    # 本番用（Fabric ブローカー認証）
```

**新しいエンティティは必ず `rayfin/data/schema.ts` に登録してください。** SQL スキーマと GraphQL API はここから生成されるため、未登録のエンティティは実行時に存在しません。

## デプロイ

```bash
npx rayfin login
npx rayfin up --workspace-id <Fabric ワークスペース GUID>
npx rayfin up status
```

現在のデプロイ先は Fabric ワークスペース `CareRoute-AI-Mimamori`
(`e2a48a60-0b5f-421f-91bb-51a33fe528bc`) で、.NET 本体の `Fabric__WorkspaceId` と同じです。

`rayfin up` は静的コンテンツのビルド・配信とスキーマ適用を一度に行い、
デプロイ情報を `rayfin/.deployments.json`（gitignore 済み）に記録します。

デプロイ後は `scripts/sync-to-fabric.ps1` でデータを投入してください。

### 開くときの注意

静的ホスティング URL を直接開くとポップアップのサインインが
「Loading…」のまま完了しないことがあります。確実なのは Fabric ポータルの
ディープリンク経由です（ポータルの iframe 内で埋め込み認証が走ります）。

```text
https://app.fabric.microsoft.com/groups/<workspace-id>/appbackends/<item-id>?ctid=<tenant-id>
```

## トラブルシューティング

### Fabric SQL がログイン中に切断される → まず容量の state を疑う

**症状。** TCP は繋がるのに、ログインの途中でサーバーから接続を切られます。
クライアントには理由が出ません。

```text
System.ComponentModel.Win32Exception (10054):
既存の接続はリモート ホストに強制的に切断されました。
```

**原因の第一候補は Fabric 容量の一時停止です。** トークン、接続文字列、
暗号化設定、クライアントライブラリはいずれも正常でもこの症状になります。
容量が Inactive だと、SQL エンドポイントは読めるエラーを返さず TDS レベルで
接続をリセットするためです。実際にここで半日溶けました。

**確認の順番。** 上から順に、安いものから試してください。

1. **容量の state を見る**（これを最初にやる）

   ```bash
   az rest --method get \
     --url "https://api.fabric.microsoft.com/v1/capacities" \
     --resource "https://api.fabric.microsoft.com"
   # 該当容量が "state": "Inactive" なら、これが原因です
   ```

   `rayfin up db apply` を流すと、CLI 経由では読める形の
   `This SQL database has been disabled. Please reach out to your Fabric
   Capacity administrator.` が出ることがあります。これも同じ意味です。

2. **容量が Active なら、次は DB のアイドル休止を疑う。**
   容量とデータベースは別々に止まります。下の
   「容量は Active なのに 1 回目のアクセスだけ失敗する」を見てください。

3. **`master` に繋いでみる。** 通常の SQL エラーが返るならトークンは正常です。
   トークンを疑う前にこれで切り分けてください。

4. トークン・接続文字列・暗号化設定を疑うのは、上の 3 つを潰してからです。

**復旧。** 容量を resume します。DB は 30〜60 秒ほどで応答を再開します。

```bash
az resource invoke-action --action resume \
  --ids "/subscriptions/<sub-id>/resourceGroups/fabric/providers/Microsoft.Fabric/capacities/fa3"
```

> **resume は課金が再開します。実行前に必ず確認を取ってください。**
> 容量を止めていることには理由がある場合があります。無人で勝手に叩かないこと。

**止まっていても画面は白くなりません。** バックエンドに到達できないとき、
コンソールはバンドル済みのスナップショット（`src/services/snapshotFallback.ts`）に
自動で切り替わり、「Fabric SQL に接続できていません」「これは現在の状況ではありません」と
抽出時刻つきで明示します。構成図の該当ノードも
`停止中` / `接続不可` / `スナップショット` に変わります。
**古いデータをライブのふりで見せることはしません。**

スナップショットを更新するには、`scripts/extract-snapshot.ps1` を流したあとに
`node scripts/generate-fallback.cjs` を実行します（`snapshotFallback.ts` は自動生成物なので手で編集しない）。

### 容量は Active なのに 1 回目のアクセスだけ失敗する → DB のアイドル休止

**容量の state を確認した次に見るのはここです。** Fabric SQL データベースは、
容量とは別に、それ自体がアイドル休止します。容量が Active でも起こります。

**観測した事実。** 容量 `fa3` が Active の状態で、デプロイ済みコンソールを開いたところ
1 回目はデータ取得に失敗してスナップショット表示に落ちました。その後 SQL に直接
クエリを投げると正常に応答し、以降は 3 回連続で正常系（`origin=fabric`）でした。
つまり **最初のアクセスは失敗しつつ DB を起こすので、2 回目以降は通ります。**

**運用手順：デモや人に見せる直前に、一度アクセスして温めておいてください。**
これをやらないと、最初に開いた人だけがスナップショット表示に当たります。
表示自体は正直に「これは現在の状況ではありません」と出るので嘘にはなりませんが、
見せる相手には最新の状態を見せたいはずです。

### 本番 Azure SQL に繋がらない（Error 47073）

見守り隊本体が使う `sqldb-mngenv` は、Azure Policy
`AzureSQL_PublicNetwork_Modify` により公衆ネットワークアクセスが自動で
無効化されることがあります。**ポリシーの再評価のたびに再発します。**
復旧はリポジトリ直下の `pwsh ./scripts/fix-sql-public-access.ps1` です。
このサーバーは複数案件が同居しているため、**サーバー単位の設定変更は必ず事前に確認を取ってください。**

## 未決事項

- Blazor 側からの**自動**同期は未実装です。現状 `scripts/sync-to-fabric.ps1` を手動実行する運用で、
  定期実行（HostedService または Fabric パイプライン）は未着手です。
- 本アプリの `HouseholdSnapshot` / `AlertRecord` は `@role('authenticated', 'read')` の読み取り専用です。
  書き込みは上記スクリプトが Fabric SQL へ直接 MERGE する経路のみです。

