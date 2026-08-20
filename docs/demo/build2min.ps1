# 5分のデモ本編（base.mp4／字幕なし）から、First Stage 収録用の2分版を組み立てる。
#
#   出力1  mimamoritai-demo-2min.mp4           …… 本番で画面共有するもの。無音・字幕焼き込み
#   出力2  mimamoritai-demo-2min-prompter.mp4  …… 手元で見るカンペ。残り秒とチャプター番号つき
#
# 音声は入れない。収録では本人が話すため、動画は無音のまま流す。
# 字幕はそのまま読み上げ台本になっている（docs/PITCH_2MIN_VIDEO.md と同じ文面）。

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$base = Join-Path $here "base.mp4"
$ev   = Join-Path $repo "images\evidence"
$work = Join-Path $env:TEMP "mimamori2min"

if (-not (Test-Path $base)) { throw "base.mp4 が見つかりません: $base" }
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work | Out-Null

$V = "-c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -r 25 -an"

# ---- 1. 本編から抜き出す区間 ------------------------------------------------
# ss = base.mp4 の開始秒 / d = 使う長さ
$cuts = @(
  @{ n = "c01"; ss =   3.0; d = 12 },  # ダッシュボード全体（少し気になる／暑さ指数24 注意）
  @{ n = "c02"; ss =  24.0; d = 10 },  # 使う機器＝市販スマートプラグ1個の構成図
  @{ n = "c03"; ss = 105.0; d = 13 },  # 電気の使用量と外の気温（東京）の重ね合わせ
  @{ n = "c04"; ss = 127.0; d = 12 },  # オープンデータ連携 5/5 稼働中
  @{ n = "c06"; ss = 191.0; d = 12 },  # 自然言語で聞く → 実測から回答
  @{ n = "c07"; ss = 236.0; d = 14 },  # 扇風機をつけて → よろしいですか → はい
  @{ n = "c08"; ss = 256.0; d = 10 },  # 火や熱をあつかう機器の警告
  @{ n = "c10"; ss = 280.0; d = 11 }   # 締め
)

foreach ($c in $cuts) {
  $out = Join-Path $work "$($c.n).mp4"
  Write-Host "cut  $($c.n)  $($c.ss)s +$($c.d)s"
  $args = @("-y","-v","error","-ss",$c.ss,"-i",$base,"-t",$c.d,
            "-vf","scale=1920:1080:flags=lanczos,setsar=1") + ($V -split " ") + @($out)
  & ffmpeg @args
  if ($LASTEXITCODE -ne 0) { throw "cut 失敗: $($c.n)" }
}

# ---- 2. LINE の実画面を静止画クリップにする --------------------------------
# 5分版には LINE の実UIが1カットも入っていないため、証跡スクショから作る。
# line-04-today.png は上端にエミュレータのタイトルバーが写るので切り落とす。
$stills = @(
  @{ n = "s05"; f = "line-04-today.png";   crop = "crop=536:930:0:44"; box = "1700:860"; dy = -26; d = 10 },
  @{ n = "s09"; f = "line-10-confirm.png"; crop = "crop=1930:788:0:0"; box = "1720:820"; dy =   6; d = 10 }
)

foreach ($s in $stills) {
  $src = Join-Path $ev $s.f
  if (-not (Test-Path $src)) { throw "LINE 画像が見つかりません: $src" }
  $out = Join-Path $work "$($s.n).mp4"
  Write-Host "still $($s.n)  $($s.f)"
  # 背景は同じ画像を引き伸ばしてぼかしたもの。前景は縦横比を保ったまま枠に収める。
  $wh = $s.box -split ":"
  $fc = "[0:v]$($s.crop),split=2[bg][fg];" +
        "[bg]scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080," +
        "boxblur=28:2,eq=brightness=-0.06:saturation=0.6[bgo];" +
        "[fg]scale=w=$($wh[0]):h=$($wh[1]):force_original_aspect_ratio=decrease:flags=lanczos," +
        "pad=iw+16:ih+16:8:8:0xFFFFFF[fgo];" +
        "[bgo][fgo]overlay=(W-w)/2:(H-h)/2+$($s.dy),scale=1920:1080,setsar=1"
  $args = @("-y","-v","error","-loop","1","-i",$src,"-t",$s.d,"-filter_complex",$fc) +
          ($V -split " ") + @($out)
  & ffmpeg @args
  if ($LASTEXITCODE -ne 0) { throw "still 失敗: $($s.n)" }
}

# ---- 3. つなぐ --------------------------------------------------------------
$order = "c01","c02","c03","c04","s05","c06","c07","c08","s09","c10"
$list  = Join-Path $work "concat.txt"
($order | ForEach-Object { "file '" + (Join-Path $work "$_.mp4").Replace("\","/") + "'" }) |
  Set-Content -Path $list -Encoding ASCII   # concat デマルチプレクサは BOM を受け付けない

$joined = Join-Path $work "joined.mp4"
& ffmpeg -y -v error -f concat -safe 0 -i $list -c copy $joined
if ($LASTEXITCODE -ne 0) { throw "concat 失敗" }

# ---- 4. 字幕（＝読み上げ台本）を焼き込む -----------------------------------
$ass = Join-Path $here "pitch2min.ass"
if (-not (Test-Path $ass)) { throw "字幕ファイルがありません: $ass" }

Push-Location $work
Copy-Item $ass (Join-Path $work "sub.ass") -Force
$final = Join-Path $here "mimamoritai-demo-2min.mp4"
& ffmpeg -y -v error -i $joined -vf "subtitles=sub.ass" `
         -c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -movflags +faststart -an $final
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "字幕焼き込み 失敗" }

# ---- 5. カンペ版（残り秒＋チャプター番号） ---------------------------------
$total = 114
$prompt = Join-Path $here "mimamoritai-demo-2min-prompter.mp4"
$font = "C\:/Windows/Fonts/consola.ttf"
$dt = "drawbox=x=0:y=0:w=1920:h=76:color=black@0.72:t=fill," +
      "drawtext=fontfile='$font':text='REMAIN %{eif\:($total-t)\:d}s':" +
      "fontcolor=white:fontsize=46:x=40:y=16," +
      "drawtext=fontfile='$font':text='ELAPSED %{eif\:t\:d}s':" +
      "fontcolor=0xBBBBBB:fontsize=38:x=1520:y=22"
& ffmpeg -y -v error -i $final -vf $dt `
         -c:v libx264 -preset medium -crf 22 -pix_fmt yuv420p -movflags +faststart -an $prompt
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "カンペ版 失敗" }
Pop-Location

foreach ($f in $final, $prompt) {
  $d = & ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 $f
  "{0}  {1:N2}s  {2:N1} MB" -f (Split-Path $f -Leaf), [double]$d, ((Get-Item $f).Length / 1MB)
}
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
