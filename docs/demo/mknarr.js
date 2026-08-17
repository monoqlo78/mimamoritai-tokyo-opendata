// narration_src.txt (idx|本文) と scenes.json から narration.txt (idx|秒数|本文) を作る
const fs = require('fs');
const D = require('path').join(__dirname, '/');
const scenes = JSON.parse(fs.readFileSync(D + 'scenes.json', 'utf8'));
const src = fs.readFileSync(D + 'narration_src.txt', 'utf8').split(/\r?\n/).filter(Boolean);
const body = {};
for (const l of src) { const i = l.indexOf('|'); body[l.slice(0, i)] = l.slice(i + 1); }

const out = [];
for (let i = 0; i < scenes.length - 1; i++) {
  const idx = scenes[i].name;
  if (!body[idx]) continue;
  const span = (scenes[i + 1].startMs - scenes[i].startMs) / 1000;
  const budget = Math.max(2, Math.round((span - 1.0) * 10) / 10);
  const chars = body[idx].replace(/[、。]/g, '').length;
  out.push(`${idx}|${budget.toFixed(1)}|${body[idx]}`);
  console.log(`${idx} span=${span.toFixed(1)} budget=${budget.toFixed(1)} chars=${chars} need=${(chars / 6.5).toFixed(1)}s ${chars / 6.5 > budget ? '  << OVER' : ''}`);
}
fs.writeFileSync(D + 'narration.txt', out.join('\n') + '\n', 'utf8');
