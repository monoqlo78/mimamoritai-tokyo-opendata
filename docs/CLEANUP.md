# ハッカソン後のクリーンアップ手順

審査が終わったあと、このプロジェクトが作った Azure リソースを確実に消し切るための手順です。**課金が続くもの**と、**共有基盤なので消してはいけないもの**を分けて書いています。

## 前提：新規リソースは1つのリソースグループに集約してある

このプロジェクトが新規作成した Azure リソースは、すべて **`rg-mimamoritai-hackathon`（Japan West）** に入れてあります。ここを消せば、当プロジェクト由来の Azure リソースは残りません。

| 種別 | 名前 |
| --- | --- |
| App Service | `<your-app>` |
| App Service プラン | `asp-mimamoritai` |
| Key Vault | `kv-mimamoritai-hack` |
| 仮想ネットワーク | `vnet-mimamoritai`（`snet-appsvc` / `snet-pe`） |
| Private Endpoint | `pe-kv-mimamoritai` / `pe-sql-mimamoritai`（＋自動生成の NIC 2件） |
| Private DNS ゾーン | `privatelink.vaultcore.azure.net` / `privatelink.database.windows.net`（＋VNet リンク各1件） |

## 手順1：リソースグループごと削除する

```bash
az group delete --name rg-mimamoritai-hackathon --yes --no-wait
```

これで上記12リソースが消えます。**共有の SQL サーバー `sqldb-mngenv` 側に残っている承認済みプライベートエンドポイント接続も、`pe-sql-mimamoritai` の削除と同時に消えます**（サーバー本体の設定は最初から一切変更していません）。

削除後の確認:

```bash
az group exists --name rg-mimamoritai-hackathon        # false になること
az sql server show -g sql-group -n sqldb-mngenv \
  --query "privateEndpointConnections[].id" -o tsv     # 当方の接続が消えていること
```

## 手順2：リソースグループの外にあるものを個別に片付ける

削除順は上から順に実施してください。

### 2-1. 共有 SQL サーバー上のデータベース（**サーバーは消さない**）

アプリのデータは共有サーバー `sqldb-mngenv`（リソースグループ `sql-group`）上の **`free-sql-db-5743178`** に入っています。**サーバーは他案件と共有しているため、絶対に削除しないでください。** 消すのはデータベース1件だけです。

```bash
az sql db delete -g sql-group -s sqldb-mngenv -n free-sql-db-5743178 --yes
```

### 2-2. Fabric ワークスペース

ワークスペース **`CareRoute-AI-Mimamori`** を削除します。中の Lakehouse / Eventhouse / Eventstream / Data Agent / Rayfin アプリ（AppBackend）/ SQL Database も同時に消えます。Fabric ポータルの **ワークスペース設定 → 全般 → ワークスペースの削除** から実施してください。

### 2-3. Fabric 容量（**共有なので停止のみ**）

容量 **`fa4`（F4 / リソースグループ `fabric`）は起動中で課金が続きます。** 他案件と共有しているため削除はせず、**一時停止**してください。

```bash
az fabric capacity suspend -g fabric -n fa4
```

> 審査終了後、いちばん最初にこれを実行するのが金額的にはいちばん効きます。

### 2-4. 外部サービス側（Azure 課金には影響しない）

- **LINE Developers**：Messaging API チャネルと LINE Login チャネル。Webhook URL の向き先が消えるので、不要なら削除します。
- **SwitchBot アプリ**：デモ用に発行したトークン/シークレットを失効させます。
- **Microsoft Entra のアプリ登録**：`Auth__ClientId` / `AuthEntra__ClientId` / `Fabric__ClientId` に対応する登録が残ります。他で使っていなければ削除します。
- **YouTube**：限定公開のデモ動画。公開したままで問題なければ残して構いません。

## 手順3：残っていないことの確認

```bash
# 当プロジェクト名を含む Azure リソースが残っていないこと
az resource list --query "[?contains(name,'mimamoritai')].{name:name,rg:resourceGroup}" -o table

# Fabric 容量がすべて停止していること
az fabric capacity list --query "[].{name:name,state:properties.state}" -o table
```

> Windows の PowerShell から `az` を実行する場合、`--query` に `contains(...)` を含めると cmd 側の解釈で失敗することがあります。その場合は `az resource list -o json | ConvertFrom-Json` してから `Where-Object` で絞り込んでください。

## 消してはいけないもの（再掲）

| 対象 | 理由 |
| --- | --- |
| SQL サーバー `sqldb-mngenv`（`sql-group`） | 他案件と共有。DB 1件だけ消す |
| Fabric 容量 `fa1` / `fa2` / `fa3` / `fa4` | 共有。`fa4` は停止のみ |
| リソースグループ `fabric` / `sql-group` | 共有 |
| テナントの Fabric 設定・ネットワーク規則 | 共有テナント全体に影響する |
