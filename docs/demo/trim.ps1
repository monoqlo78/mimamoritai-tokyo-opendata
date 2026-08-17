# Playwright の録画（out/*.webm）を base.mp4 に変換する。
#
# 録画はブラウザを開いた時点から始まるので、先頭には場面 01 が始まるまでの
# 待ち時間が入っている。record.js が書いた recording.json の leadInMs だけ
# 切り落とすと、scenes.json の時刻と映像の時刻が一致する。
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$rec = Get-Content recording.json -Raw | ConvertFrom-Json
$src = Join-Path 'out' $rec.video
if (-not (Test-Path $src)) { throw "recording not found: $src" }

$ss = [math]::Round($rec.leadInMs / 1000, 3)
Write-Host "trim $src  lead-in=${ss}s"
ffmpeg -y -loglevel error -ss $ss -i $src -c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -r 25 -an base.mp4
ffprobe -v error -show_entries format=duration -of csv=p=0 base.mp4
