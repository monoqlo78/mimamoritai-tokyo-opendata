# 意図分類の正答率

<!-- IntentEvaluationTests.MeasureAccuracyAgainstLiveRouter が生成。手で書き換えないこと。 -->

- 実行日時 (UTC): 2026-08-17 13:19
- 評価件数: 100 件
- 正答: 97 件 / **正答率 97.0%**
- うち決定的ロジックで確定: 19 件、モデル呼び出し: 81 件
- 危険側の取りこぼし (emergency / expert を取り逃した件数): **0 件**

## クラス別

| クラス | 件数 | 正答 | 正答率 |
| --- | ---: | ---: | ---: |
| control_device | 20 | 18 | 90.0% |
| conversation/emergency | 8 | 8 | 100.0% |
| conversation/expert | 10 | 10 | 100.0% |
| conversation/faq | 10 | 10 | 100.0% |
| conversation/general | 18 | 18 | 100.0% |
| device_status | 12 | 12 | 100.0% |
| query_data/analysis | 10 | 10 | 100.0% |
| query_data/recent | 12 | 11 | 91.7% |

## 誤分類の内訳

| ID | 入力 | 期待 | 実際 | 判定した層 |
| --- | --- | --- | --- | --- |
| ctl-007 | テレビ消しといて | control_device | unparsed | model:error |
| ctl-014 | 寒そうなので暖房を強めにして | control_device | conversation/general | model |
| qry-r11 | 今日はエアコン使ってた？ | query_data/recent | device_status | model |
