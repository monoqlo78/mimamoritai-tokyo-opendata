$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$scenes = Get-Content scenes.json -Raw | ConvertFrom-Json
$end = ($scenes | Where-Object { $_.name -eq 'END' }).startMs / 1000
$ff = @('-y', '-loglevel', 'error', '-i', 'base.mp4')
$parts = @()
$labels = @()
$n = 0
foreach ($s in $scenes) {
  if ($s.name -eq 'END') { continue }
  $w = "tts\n$($s.name).wav"
  if (-not (Test-Path $w)) { continue }
  $n++
  $ff += @('-i', $w)
  $d = [int]$s.startMs + 400
  $parts += "[${n}:a]adelay=$d|$d[a$n]"
  $labels += "[a$n]"
}
$srt = (Join-Path $PSScriptRoot 'subtitles.srt') -replace '\\', '/' -replace ':', '\:'
$sub = "subtitles='$srt':force_style='FontName=Yu Gothic UI,FontSize=16,PrimaryColour=&H00FFFFFF,BorderStyle=3,Outline=3,Shadow=0,BackColour=&H30000000,MarginV=18,Alignment=2'"
$fc = ($parts -join ';') + ';' + ($labels -join '') + "amix=inputs=${n}:normalize=0:dropout_transition=0[aout];[0:v]$sub" + '[vout]'
$ff += @('-filter_complex', $fc, '-map', '[vout]', '-map', '[aout]',
  '-c:v', 'libx264', '-preset', 'medium', '-crf', '23', '-pix_fmt', 'yuv420p',
  '-c:a', 'aac', '-b:a', '160k', '-t', "$end", 'mimamoritai-demo.mp4')
Write-Host "inputs=$n end=$end"
& ffmpeg @ff
ffprobe -v error -show_entries format=duration -of csv=p=0 mimamoritai-demo.mp4
