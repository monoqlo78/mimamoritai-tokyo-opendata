# デモ動画

**[mimamoritai-demo.mp4](mimamoritai-demo.mp4)** — 4分2秒 / 1920x1080 / 25fps / 14.3MB / 日本語ナレーション + 日本語字幕入り

家電の消費電力と、屋外のオープンデータ（気温・暑さ指数）だけで、離れて暮らすご家族の一日を見守る画面をひととおり操作しています。冒頭で**家族が見る画面**と、**Microsoft Fabric 上で動く運用コンソール**の両方を見せてから、**気温と電気を重ねて見るグラフ**、**ふつうの言葉での質問と、実際に返ってきた AI の回答**、**家電の状態と火気注意**、**AI / Microsoft Fabric / LINE の接続状況**まで、実際に動いている画面をそのまま収録したものです。

**[mimamoritai-demo-voice-safety.mp4](mimamoritai-demo-voice-safety.mp4)** — 3分11秒 / 音声操作と安全確認の回。「寒いからストーブつけて」に対し、AI が**拒否ではなく周囲の安全を確認する質問を返して止まる**ところを収めてあります。上の本編には入っていない挙動なので、記録として残しています（収録が早いため、画面に映るモデル名は Azure Model Router 移行前のものです）。

**[mascot.mp4](mascot.mp4)** — 3.8秒 / ナレーション入り。LINE のトーク内に出るマスコットの短いクリップです（「お母さんの今日は、いつもどおりです。」）。

ナレーションは **Azure AI Speech の Text to Speech**（`ja-JP-NanamiNeural`）で合成し、読み上げた内容が原稿どおりかを **同じ Azure AI Speech の Speech to Text で聞き直して検証**しています。音を出せない環境でも困らないよう、字幕だけで内容が追えるようにしてあります。

## 収録内容（本編）

| 時間 | 内容 |
| --- | --- |
| 0:00 | カメラを使わない見守り。家電の電気と、外の気温だけ |
| 0:15 | **Microsoft Fabric 上の運用コンソール**。センサーから家族への通知までのデータの流れ |
| 0:28 | Azure Model Router が実際に答えたモデルと、その平均応答時間 |
| 0:49 | 今日のようす。暑さ指数と、お住まいの地域の気温 |
| 1:07 | 動きはじめた時間 / 最後に動いた時間 / 今日の使用量 |
| 1:20 | 今日の電気を、直近13日間の同時刻平均（点線）と重ねる |
| 1:36 | 起床・就寝の推定を2週間ぶん並べて、生活リズムの変化を見る |
| 1:49 | **気温 × 電気**。気象庁アメダスの最高／最低気温を電力に重ねる |
| 2:07 | 同じ内容を数値で確認。暑い日に電気がのびていれば冷房を使えているサイン |
| 2:19 | 使っているオープンデータ5件（環境省 WBGT / 気象庁アメダス・予報・観測所 / 東京都 熱中症統計） |
| 2:34 | ふつうの言葉で質問（「今日のお母さんの様子は？」）。音声入力にも対応 |
| 2:46 | Azure Model Router が、質問に応じて使うモデルを自動で選ぶ |
| 3:01 | **実際に返ってきた回答**と、根拠になる時刻順のできごと一覧 |
| 3:13 | 家電の状態。離れたまま操作でき、火や熱をあつかう機器には注意書きと家族通知 |
| 3:31 | 接続状況。AI・Microsoft Fabric・LINE の実連携と、選ばれたモデル名 |
| 3:51 | まとめ。カメラを置かず、特別な機器も足さずに |

## 補足

- 撮影・編集の方針は [../DEMO_SCENARIO.md](../DEMO_SCENARIO.md) にまとめてあります。
- デモ環境の URL はあえて公開していません。ハッカソン用に立てた Azure リソースなので、審査後は停止する予定です。この動画と [../EVIDENCE.md](../EVIDENCE.md) が、稼働していた証拠になります。
- LINE の友だち追加 QR / ID は、動画には映さないようにしています（公開リポジトリのため）。
- 手元で動かす場合は `dotnet run` だけで済みます。API キーは要りません（すべてモックに切り替わります）。

## 作り方（再現手順）

収録・ナレーション・字幕まで、すべてスクリプトから機械的に作っています。

| ファイル | 役割 |
| --- | --- |
| [record.js](record.js) | Playwright で画面を操作しながら 1920x1080 で録画。各シーンの開始時刻を `scenes.json` に、録画先頭の余白を `recording.json` に書き出す |
| [scenes.json](scenes.json) | 収録の実測タイムライン。ナレーション尺と字幕タイミングの基準になる |
| [trim.ps1](trim.ps1) | `recording.json` の余白ぶんだけ録画の先頭を切り落として `base.mp4` を作る |
| [narration_src.txt](narration_src.txt) | 原稿。`通し番号｜読み上げる文` |
| [mknarr.js](mknarr.js) | 原稿 + `scenes.json` → [narration.txt](narration.txt)（`通し番号｜その場面の秒数｜文`）。尺に対する必要秒数も試算する |
| [tts_gen.py](tts_gen.py) | Azure AI Speech で音声を合成。**場面の尺に収まらなければ自動的に話速を上げて作り直す** |
| [mksrt.js](mksrt.js) | `scenes.json` + `narration.txt` → [subtitles.srt](subtitles.srt) |
| [mux.ps1](mux.ps1) | 合成した wav を ffmpeg で映像へ多重化し、字幕を焼き込む |
| [stt_verify.py](stt_verify.py) | できあがった動画から場面ごとに音声を切り出し、Speech to Text で聞き直して原稿と突き合わせる |

収録には2つの画面を同時に立ち上げておきます。

```powershell
dotnet run --project ..\..\src\MimamoriTai.Web --urls http://localhost:5234   # 家族が見る画面
cd ..\..\fabric-app; npx vite --port 5199 --strictPort                        # 運用コンソール
```

運用コンソールは本来 Microsoft Fabric の SSO を通るため、そのままではヘッドレスで録画できません。開発サーバー限定の収録モード（`fabric-app/.env.capture.example` を `.env.development.local` にコピー）を用意して、サインインを省いたうえで **本番 Fabric データベースから抽出済みのスナップショット**を描画しています。この分岐は `import.meta.env.DEV` で囲ってあり、本番ビルドでは丸ごと除去されます（`npm run build` した `dist/` に `CaptureAuthService` の文字列が残っていないことで確認できます）。

```powershell
node record.js                       # 画面録画 + scenes.json / recording.json
.\trim.ps1                           # 録画先頭の余白を落として base.mp4
node mknarr.js                       # 原稿 -> narration.txt（尺の試算つき）
$env:SPEECH_KEY = "<Azure AI Speech のキー>"
python tts_gen.py                    # narration.txt -> tts/*.wav
python tts_gen.py 07                 # 番号を渡すと、その場面だけ作り直す
node mksrt.js                        # -> subtitles.srt
.\mux.ps1                            # 音声多重化 + 字幕焼き込み
python stt_verify.py                 # 動画 -> 文字起こし -> 原稿と比較
```

収録時に気をつけている点をいくつか残しておきます。

- 文字を読みやすくするため `html { zoom: 1.45 }`（運用コンソールは情報密度が高いので `1.30`）を当てています。この状態では `scrollIntoView()` の位置がずれるので、`getBoundingClientRect()` を見ながら補正しています。
- AI の回答は待ち時間が一定しないため、「考えています…」が消えるまで待ってから次の場面へ進みます（本編では約14秒で返ってきています）。
- Playwright の録画はブラウザ起動時から始まるため、先頭に数秒の余白ができます。`record.js` がその長さを `recording.json` に書き出し、`trim.ps1` が同じだけ切り落とすので、`scenes.json` の時刻と映像の時刻が一致します。
- 運用コンソールは定期的に自動更新するため `networkidle` が成立しません。要素の出現を待って進めています。また Vite は最初のリクエストでモジュールをまとめてコンパイルするので、本番の収録が始まる前に一度開いてウォームアップしています。
- SRT には解像度情報がなく、libass は既定の `PlayResY=384` を基準に拡大します。1080p では約2.8倍になるので、`FontSize` などはその前提で小さめに指定しています。

`stt_verify.py` は 16 場面すべてを照合します。`AI` や `Microsoft Fabric` は原稿にひらがなで書いてある（そう読ませたいため）ので、文字列としては一致しませんが、**音としては正しく読めている**ことの確認になります。今回の照合結果は、いちばん低い場面でも一致率 0.75、それ以外は 0.78 以上でした。

> 以前ここに置いていた `dataflow.mp4` は削除しました。AI ルーターを Azure Model Router へ移行する前の画面で、旧名称と当時の接続エラーがそのまま映っていたためです。現在の構成図は [../ARCHITECTURE.md](../ARCHITECTURE.md) を参照してください。
