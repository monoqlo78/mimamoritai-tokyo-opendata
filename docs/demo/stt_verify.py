import os, json, pathlib, subprocess, tempfile, urllib.request, difflib, re

KEY = os.environ["SPEECH_KEY"]
URL = ("https://japaneast.stt.speech.microsoft.com/speech/recognition/"
       "conversation/cognitiveservices/v1?language=ja-JP&format=simple")

BASE = pathlib.Path(__file__).parent
VID = BASE / "mimamoritai-demo.mp4"
TMP = pathlib.Path(tempfile.gettempdir()) / "mimamoritai_stt"
TMP.mkdir(exist_ok=True)

scenes = json.loads((BASE / "scenes.json").read_text(encoding="utf-8"))
script = {}
for line in (BASE / "narration.txt").read_text(encoding="utf-8").splitlines():
    if line.strip():
        i, b, txt = line.strip().split("|", 2)
        script[i] = txt


def norm(s):
    return re.sub(r"[、。「」？！\s]", "", s)


def recognize(wav):
    req = urllib.request.Request(
        URL, data=wav.read_bytes(),
        headers={"Ocp-Apim-Subscription-Key": KEY,
                 "Content-Type": "audio/wav; codecs=audio/pcm; samplerate=16000",
                 "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=90) as r:
        return json.loads(r.read().decode("utf-8"))


print(f"{'seg':>3} {'sim':>5}  recognised")
worst = 1.0
for i in range(len(scenes) - 1):
    name = scenes[i]["name"]
    if name not in script:
        continue
    s = scenes[i]["startMs"] / 1000
    d = (scenes[i + 1]["startMs"] - scenes[i]["startMs"]) / 1000
    wav = TMP / f"s{name}.wav"
    subprocess.run(["ffmpeg", "-y", "-v", "error", "-ss", f"{s:.2f}", "-t", f"{d:.2f}",
                    "-i", str(VID), "-vn", "-ac", "1", "-ar", "16000",
                    "-c:a", "pcm_s16le", str(wav)], check=True)
    got = recognize(wav).get("DisplayText", "")
    exp = script[name]
    ratio = difflib.SequenceMatcher(None, norm(exp), norm(got)).ratio() if got else 0.0
    worst = min(worst, ratio)
    print(f"{name:>3} {ratio:>5.2f}  {got}")

print(f"\nworst similarity: {worst:.2f}")


