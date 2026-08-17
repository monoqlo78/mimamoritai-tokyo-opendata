import os, sys, re, subprocess, pathlib, urllib.request, html

KEY = os.environ["SPEECH_KEY"]
REGION = "japaneast"
URL = f"https://{REGION}.tts.speech.microsoft.com/cognitiveservices/v1"
VOICE = "ja-JP-NanamiNeural"

BASE = pathlib.Path(__file__).parent
OUT = BASE / "tts"
OUT.mkdir(exist_ok=True)


def synth(text, rate, dest):
    ssml = (
        '<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" '
        'xmlns:mstts="https://www.w3.org/2001/mstts" xml:lang="ja-JP">'
        f'<voice name="{VOICE}"><mstts:express-as style="calm">'
        f'<prosody rate="{rate:+d}%" pitch="0%">{html.escape(text)}</prosody>'
        "</mstts:express-as></voice></speak>"
    )
    req = urllib.request.Request(
        URL,
        data=ssml.encode("utf-8"),
        headers={
            "Ocp-Apim-Subscription-Key": KEY,
            "Content-Type": "application/ssml+xml",
            "X-Microsoft-OutputFormat": "riff-24khz-16bit-mono-pcm",
            "User-Agent": "mimamoritai-demo",
        },
    )
    with urllib.request.urlopen(req, timeout=60) as r:
        data = r.read()
    dest.write_bytes(data)
    return dur(dest)


def dur(p):
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-of", "default=nw=1:nk=1", str(p)],
        capture_output=True, text=True,
    ).stdout.strip()
    return float(out)


rows = []
for line in (BASE / "narration.txt").read_text(encoding="utf-8").splitlines():
    line = line.strip()
    if not line:
        continue
    idx, budget, text = line.split("|", 2)
    rows.append((idx, float(budget), text))

# 引数で番号を渡すと、その行だけ作り直す。原稿を一部だけ直したときに使う。
only = set(a.zfill(2) for a in sys.argv[1:])
if only:
    rows = [r for r in rows if r[0] in only]

report = []
for idx, budget, text in rows:
    dest = OUT / f"n{idx}.wav"
    # leave 0.6s of breathing room inside the segment
    target = budget - 0.6
    rate = 0
    d = synth(text, rate, dest)
    tries = 0
    while d > target and tries < 6:
        # SSML の rate は正で速く、負で遅くなる。尺に収めるので正方向へ寄せる。
        rate += max(4, int(round((d / target - 1) * 100 / 2)))
        rate = min(rate, 40)
        d = synth(text, rate, dest)
        tries += 1
        if rate >= 40:
            break
    report.append((idx, budget, round(d, 2), rate, "OK" if d <= budget else "OVER"))

print(f"{'seg':>4} {'budget':>7} {'speech':>7} {'rate':>6}  status")
for r in report:
    print(f"{r[0]:>4} {r[1]:>7.1f} {r[2]:>7.2f} {r[3]:>5}%  {r[4]}")
