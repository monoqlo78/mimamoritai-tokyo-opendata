# 稼働証跡 / Evidence

提出物が「動いている」ことを、開発機以外から確認した記録です。

- 本番URL: <https://<your-app>.azurewebsites.net/>
- 取得日時: 2026-08-12 19:02 UTC（2026-08-13 04:02 JST）

> **この環境は審査後に停止します。** ハッカソン用に立てた Azure リソースなので、審査期間を過ぎるとこの URL は応答しなくなります。そのため本書では、URL を案内するのではなく**その時点で動いていた記録**（応答ヘッダ・TLS・画面キャプチャ）を残すことを目的にしています。上の日時の時点では稼働していました。

---

## 1. 独立した環境からの到達性

開発に使ったPCとは別の Azure 仮想マシンから、公開URLへ実際にアクセスして確認しました。
開発機のキャッシュやローカル設定に依存していないことを示すためです。

| 項目 | 値 |
| --- | --- |
| 検証元ホスト | `blendercodex`（Azure VM / Windows 11 Pro） |
| 検証元の外向きIP | `20.78.x.x`（開発PCとは別ネットワーク） |
| 名前解決 | 公開URLが Azure App Service の共有フロントエンドIPへ解決されることを確認 |

### HTTP応答

| パス | ステータス | 応答時間 | サイズ |
| --- | --- | --- | --- |
| `/` | 200 | 391 ms | 47,766 B |
| `/health` | 200 | 59 ms | 61 B |
| `/one-touch` | 200 | 88 ms | 13,071 B |
| `/admin` | 200 | 155 ms | 8,723 B |
| `/liff` | 200 | 88 ms | 8,770 B |
| `/not-found` | 200 | 85 ms | 7,175 B |

### TLS

| 項目 | 値 |
| --- | --- |
| Subject | `CN=*.azurewebsites.net, O=Microsoft Corporation, L=Redmond, S=WA, C=US` |
| Issuer | `CN=Microsoft TLS G2 RSA CA OCSP 02, O=Microsoft Corporation, C=US` |
| 有効期限 | 2027-01-10 |

### 配信されているHTMLの内容確認

トップページのHTML（46,735バイト）に、以下の文字列が含まれることを確認しました。
静的なプレースホルダではなく、実際のアプリが配信されていることの確認です。

| マーカー | 含まれるか |
| --- | --- |
| `CareRoute` | あり |
| `SwitchBot` | あり |
| `blazor` | あり |
| `_framework`（Blazor のランタイム） | あり |
| 日本語（かな・漢字） | あり |

---

## 2. 画面の証跡

### 2-1. トップ画面

![トップ画面](images/evidence/prod-01-home.png)

家族が最初に見る画面です。「いつもどおり」という一言を最上段に置き、
数字は下に回しています。直近14日の推移は、家電の利用回数・起きた時間・夜間の動きの3本。

なお撮影時刻が午前4時のため、「本日の活動」はまだ0回です。
14日間の推移グラフには過去分が描かれており、当日分だけが空という
実際の運用でも起きる状態がそのまま出ています。

### 2-2. ワンタッチ画面

![ワンタッチ画面](images/evidence/prod-02-one-touch.png)

見守られる側（高齢の家族）が使う画面です。ボタンは3つだけ。
右のキャラクターは Blender で制作し、Three.js でブラウザ上に表示しています。

### 2-3. 運用コンソール（未認証時）

![運用コンソールの拒否画面](images/evidence/prod-03-admin-denied.png)

`/admin` に未認証でアクセスした場合の画面です。
**画面の枠だけ出して中身を空にするのではなく、データを一切読み込まずに拒否**します。
サインインが構成された環境では、許可リスト（`Admin:Subjects`、
`<IdentityProvider>:<ExternalSubject>` 形式）に載っている識別子だけが一致します。
リストが空なら誰も一致しません。同じデータを返すAPI側も、未許可なら 404 を返して
エンドポイントの存在自体を答えない作りです。

---

## 3. Microsoft Fabric 側の証跡

見守り隊は、リアルタイムの見守り（Azure SQL）と、
分析・運用の基盤（Microsoft Fabric）を分けています。
ここでは Fabric 側が実際にデータを受け取り、動いていることを示します。

### 3-1. ワークスペースの構成

![Fabric ワークスペース](images/evidence/fabric-03-workspace.png)

ワークスペース `CareRoute-AI-Mimamori` に、
Eventstream・Eventhouse（KQLデータベース）・Lakehouse・
Fabric SQL Database・データエージェントが並んでいます。
運用コンソール（`mimamoritai-admin`）も Fabric のアプリとして
このワークスペースに載っています。

### 3-2. Eventhouse への取り込み

![Eventhouse](images/evidence/fabric-04-eventhouse.png)

`MimamoriEventhouse` には `DeviceEvents` と `SwitchBotPlugReadings` の
2テーブルがあります。撮影時点で直近の取り込みは 568 行、
`SwitchBotPlugReadings` の最終インジェストは「3分前」です。
デモ用に流し込んだ静的データではなく、現在も動いている経路です。

### 3-3. KQL で実データを確認

![KQL クエリ結果](images/evidence/fabric-05-kql.png)

```kusto
SwitchBotPlugReadings
| summarize Readings=count(), LastIngested=max(ingestion_time()) by DeviceName
| order by Readings desc
```

実機の SwitchBot プラグ「リビングの電気」から 383 件が取り込まれ、
最終取り込みは実行の約2分前でした。

### 3-4. Eventstream（センサー → Fabric の入口）

![Eventstream](images/evidence/fabric-06-eventstream.png)

`MimamoriApp`（見守り隊 Web）から `DeviceStream` を通り、
`ToEventhouse` と `ToLakehouse` の2つの宛先へ分岐しています。
下部のデータプレビューには、実際に流れているイベントが
`eventId` / `householdId` / `deviceId` とともに並んでいます。

なお、この宛先は長らく非アクティブのままでした。
「すべてアクティブ化」で起動し、両方の宛先が**アクティブ**になっています。
アプリ側は Eventstream が止まっていても動き続けられるよう、
Azure SQL を主経路として分けた設計にしているため、
この停止中も見守り自体は継続していました。

### 3-5. 運用コンソール（Fabric App / Rayfin）

![運用コンソール](images/evidence/fabric-02-console-full.png)

Fabric 上でホストしている運用コンソールです。
サインインは Fabric のブローカー認証（SSO）を通ります。
上部の帯にある「データはこう流れています」は、
センサーから家族への通知、そして Fabric への取り込みまでを
1枚で追える図で、数字はすべて下の表と同じ実データです。

「OrcaRouter が使ったモデル」の節では、記録した87回の呼び出しのうち
86回が4種類のモデルで応答し、残り1回はモデルが応答する前に失敗した
呼び出しであることを、末尾の「未応答（失敗）」の棒として明示しています。
**うまくいった分だけを描いて合計を合わせる、ということをしていません。**

なお画面上部のアカウント表記は、公開用に伏せています。

---

## 4. LINE 実機での動作（Android エミュレータ）

見守り隊は LINE 公式アカウントからも使えます。
以下は Android エミュレータ（AndroidDemo）に LINE を入れ、
実際に公式アカウント「見守り隊」と会話した記録です。

### 4-1. 「助けて」

![LINE 助けて](images/evidence/line-01-help.png)

ボタンを押すと、記録した旨と**119番の案内**が即座に返ります。
このサービスは緊急通報の代わりにはならない、という立場を
最初の応答ではっきり示しています。

### 4-2. 「体調が悪い」

![LINE 体調が悪い](images/evidence/line-02-unwell.png)

家族への連絡が走ったことを、本人にも短く伝えます。

### 4-3. 「大丈夫」

![LINE 大丈夫](images/evidence/line-03-ok.png)

何もなかった日も記録できます。**無事の記録**が残ることが、
離れて暮らす家族の安心につながります。

### 4-4. 「今日の様子」

![LINE 今日の様子](images/evidence/line-04-today.png)

その日の家電の使われ方をまとめて返します。
「まだ記録がありません」「登録されている家電は1台（リビングの電気）です」と、
**わからないことはわからないまま**返しています。
※ 公開のため、本人の氏名部分のみ伏せています。

### 4-5. 「家族に連絡」

![LINE 家族に連絡](images/evidence/line-05-contact.png)

### 4-6. 家電の操作は必ず確認を挟む（本題）

![LINE 操作の確認](images/evidence/line-10-confirm.png)

自由文の「リビングの電気を付けれる？」に対しても、
**いきなり実行せず「よろしいですか？」を必ず1回挟みます**。
「はい」を受け取ってはじめて実行し、実行後に結果を返します。

この確認は AI の判断で省略できません。
AI が担うのは「どの家電をどうしたいか」の解釈までで、
実行の可否は人が押した「はい」だけが決めます。

### 4-7. LINE から開くワンタッチ画面（LIFF）

![LIFF トップ](images/evidence/line-06-liff-top.png)
![LIFF キャラクター](images/evidence/line-07-liff-character.png)

LINE のトーク内から、そのままワンタッチ画面が開きます。
Blender で制作した3Dキャラクターも、スマートフォンの画面上で動きます。

![LIFF ボタン](images/evidence/line-08-liff-buttons.png)
![LIFF 詳細](images/evidence/line-09-liff-detail.png)

ボタンは大きく、色と記号で区別しています。
最下部には**「命にかかわる緊急時は119番」**を常に出しています。

なお、これらの操作は検証用の Android エミュレータ上で行っており、
個人の LINE アカウントは使用していません。

---

## 5. 検証方法

### VMからの到達性確認

Azure VM 上で PowerShell を実行し、以下を取得しました。

- `Invoke-WebRequest` による各パスのステータス・応答時間・サイズ
- `HttpWebRequest.ServicePoint.Certificate` によるTLS証明書
- `Resolve-DnsName` による名前解決結果
- 外向きIPの確認

### 画面キャプチャ

公開URLに対して Playwright（Chromium / 1400×1050）でアクセスし取得しました。
本番アプリの3枚（2章）は認証を伴わない状態での表示です。
Fabric 側（3章）は、運用者としてサインインした状態で取得しています。
LINE 側（4章）は、Android エミュレータ上の LINE アプリの画面です。

---

## 6. 補足

- `/admin` `/liff` は認証前提の画面のため、上記のステータス200は
  「拒否画面が正しく返っている」ことを意味します。
- `/health` はヘルスチェック用のエンドポイントで、61バイトの応答を返します。
