// scenes.json（実測タイムライン）と narration.txt から SRT を作る。
// 長い文は「。」で分割して、文字数の比で表示時間を割り当てる。
const fs = require('fs');

const D = require('path').join(__dirname, '/');
const scenes = JSON.parse(fs.readFileSync(D + 'scenes.json', 'utf8'));
const lines = fs.readFileSync(D + 'narration.txt', 'utf8').split(/\r?\n/).filter(Boolean);

const text = {};
for (const l of lines) {
  const [idx, , body] = l.split('|');
  text[idx] = body;
}

const ts = ms => {
  const h = String(Math.floor(ms / 3600000)).padStart(2, '0');
  const m = String(Math.floor(ms / 60000) % 60).padStart(2, '0');
  const s = String(Math.floor(ms / 1000) % 60).padStart(2, '0');
  const x = String(ms % 1000).padStart(3, '0');
  return `${h}:${m}:${s},${x}`;
};

// 1行が長すぎると読みにくいので、なるべく2行で均等に折る
const wrap = (s, w = 32) => {
  if (s.length <= w) return s;
  const lines = Math.ceil(s.length / w);
  const per = Math.ceil(s.length / lines);
  const out = [];
  for (let i = 0; i < s.length; i += per) out.push(s.slice(i, i + per));
  return out.join('\n');
};

let n = 0;
const srt = [];
for (let i = 0; i < scenes.length - 1; i++) {
  const idx = scenes[i].name;
  if (!text[idx]) continue;
  const start = scenes[i].startMs + 300;
  const end = scenes[i + 1].startMs - 400;
  const span = end - start;

  const parts = text[idx].split('。').filter(Boolean).map(s => s + '。');
  const total = parts.reduce((a, b) => a + b.length, 0);
  let cur = start;
  for (const part of parts) {
    const d = Math.round(span * (part.length / total));
    srt.push(`${++n}\n${ts(cur)} --> ${ts(Math.min(cur + d, end))}\n${wrap(part)}\n`);
    cur += d;
  }
}
fs.writeFileSync(D + 'subtitles.srt', srt.join('\n'), 'utf8');
console.log(`cues: ${n}`);
