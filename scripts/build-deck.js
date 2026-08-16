// 見守り隊 / CareRoute AI - 提出用プレゼン資料
// 実行: node scripts/build-deck.js
const path = require('path');
const fs = require('fs');
const PptxGenJS = require('pptxgenjs');

const REPO = path.resolve(__dirname, '..');
const IMG = (n) => path.join(REPO, 'docs', 'images', n);
const OUT = path.join(REPO, 'docs', 'mimamoritai-deck.pptx');

// 見守り＝安心と温かさ。医療機器的な青ではなく、berry/cream 系でまとめる。
const C = {
  berry: '6D2E46',
  rose: 'A26769',
  cream: 'ECE2D0',
  ink: '2B1F24',
  white: 'FFFFFF',
  mute: '8A7A80',
  // クリーム地の上ではmuteが薄すぎて読めない。補足文はこちらを使う。
  note: '6B5C62',
  alert: 'B23A48',
  ok: '4F7A5B',
};

const HEAD = 'Georgia';
const BODY = 'Calibri';

const pptx = new PptxGenJS();
pptx.layout = 'LAYOUT_16x9'; // 10 x 5.625 inch
const W = 10, H = 5.625;

pptx.author = 'CareRoute AI';
pptx.title = '見守り隊 / CareRoute AI';

// 全スライド共通のモチーフ: 左端の太い縦帯
function spine(slide, color = C.berry) {
  slide.addShape(pptx.ShapeType.rect, { x: 0, y: 0, w: 0.18, h: H, fill: { color } });
}

// ページ番号は追加・並べ替えのたびに手で振り直すとずれるので、スライド数から採る。
function footer(slide) {
  const page = pptx.slides.length;
  slide.addText('見守り隊 / CareRoute AI', {
    x: 0.45, y: H - 0.42, w: 4, h: 0.28,
    fontSize: 10, color: C.mute, fontFace: BODY,
  });
  slide.addText(String(page), {
    x: W - 0.85, y: H - 0.42, w: 0.4, h: 0.28,
    fontSize: 10, color: C.mute, fontFace: BODY, align: 'right',
  });
}

function title(slide, text, sub) {
  slide.addText(text, {
    x: 0.62, y: 0.42, w: W - 1.3, h: 0.72,
    fontSize: 34, bold: true, color: C.ink, fontFace: HEAD,
  });
  if (sub) {
    slide.addText(sub, {
      x: 0.62, y: 1.14, w: W - 1.3, h: 0.36,
      fontSize: 14, color: C.rose, fontFace: BODY,
    });
  }
}

/* ---------------------------------------------------------------- 1. 表紙 */
{
  const s = pptx.addSlide();
  s.background = { color: C.berry };
  s.addShape(pptx.ShapeType.rect, { x: 0, y: 0, w: 0.18, h: H, fill: { color: C.rose } });

  s.addText('見守り隊', {
    x: 0.8, y: 1.45, w: 5.6, h: 1.0,
    fontSize: 54, bold: true, color: C.white, fontFace: HEAD,
  });
  s.addText('CareRoute AI', {
    x: 0.82, y: 2.42, w: 5.6, h: 0.42,
    fontSize: 19, color: C.cream, fontFace: BODY, charSpacing: 2,
  });
  s.addText('AIに家電を任せない、見守りサービス', {
    x: 0.82, y: 3.05, w: 5.6, h: 0.45,
    fontSize: 17, color: C.white, fontFace: BODY, italic: true,
  });
  s.addText('カメラを使わず、家電の電源だけで生活リズムを見る', {
    x: 0.82, y: 3.58, w: 5.8, h: 0.4,
    fontSize: 12.5, color: C.cream, fontFace: BODY,
  });

  if (fs.existsSync(IMG('01-top.png'))) {
    // 画像は白背景。berry地の上で浮かないよう、cream の枠を敷いて写真らしく見せる。
    s.addShape(pptx.ShapeType.rect, { x: 6.47, y: 1.07, w: 3.11, h: 2.13, fill: { color: C.cream } });
    s.addImage({ path: IMG('01-top.png'), x: 6.55, y: 1.15, w: 2.95, h: 1.97 });
  }
  s.addText('Azure · Microsoft Fabric · .NET 10 Blazor · LINE', {
    x: 6.3, y: 3.35, w: 3.45, h: 0.4,
    fontSize: 10, color: C.cream, fontFace: BODY, align: 'center',
  });
}

/* ------------------------------------------------------------ 2. 課題 */
{
  const s = pptx.addSlide();
  s.background = { color: C.white };
  spine(s);
  title(s, '毎日の「元気かな」が、負担になる', '見守る側と、見守られる側の両方に');

  const cards = [
    { t: '見守る側', b: '毎日電話するのは気が引ける。\nかといって、何も知らないのは不安。', c: C.rose },
    { t: '見守られる側', b: 'カメラを付けられるのは抵抗がある。\n監視されている感じがする。', c: C.berry },
  ];
  cards.forEach((cd, i) => {
    const x = 0.62 + i * 4.6;
    s.addShape(pptx.ShapeType.rect, { x, y: 1.78, w: 4.25, h: 1.62, fill: { color: C.cream } });
    s.addShape(pptx.ShapeType.rect, { x, y: 1.78, w: 0.09, h: 1.62, fill: { color: cd.c } });
    s.addText(cd.t, {
      x: x + 0.3, y: 1.95, w: 3.8, h: 0.36, fontSize: 17, bold: true, color: cd.c, fontFace: HEAD,
    });
    s.addText(cd.b, {
      x: x + 0.3, y: 2.35, w: 3.8, h: 0.9, fontSize: 13, color: C.ink, fontFace: BODY, lineSpacing: 20,
    });
  });

  s.addShape(pptx.ShapeType.rect, { x: 0.62, y: 3.72, w: 8.83, h: 1.1, fill: { color: C.ink } });
  s.addText('取れる情報を減らして、受け入れやすさを取る', {
    x: 0.95, y: 3.9, w: 8.2, h: 0.4, fontSize: 19, bold: true, color: C.white, fontFace: HEAD,
  });
  s.addText('映像も音声も取らない。家電の ON/OFF と消費電力だけを見る。', {
    x: 0.95, y: 4.31, w: 8.2, h: 0.36, fontSize: 13, color: C.cream, fontFace: BODY,
  });
  footer(s);
}

/* ------------------------------------------------- 3. 何がわかるか */
{
  const s = pptx.addSlide();
  s.background = { color: C.white };
  spine(s);
  title(s, '電源の履歴だけで、ここまでわかる', '絶対値ではなく「いつもとの差」を見る');

  const rows = [
    ['朝', '6:00-9:00', '照明が点いた', '起きた'],
    ['昼', '9:00-18:00', '何も動かない', 'いつもと違う'],
    ['夜', '0:00-5:00', '何度も点く', '眠れていないかも'],
  ];
  rows.forEach((r, i) => {
    const y = 1.75 + i * 0.72;
    s.addShape(pptx.ShapeType.ellipse, { x: 0.62, y, w: 0.56, h: 0.56, fill: { color: C.berry } });
    s.addText(r[0], { x: 0.62, y: y + 0.11, w: 0.56, h: 0.34, fontSize: 14, bold: true, color: C.white, fontFace: BODY, align: 'center' });
    s.addText(r[1], { x: 1.3, y: y - 0.04, w: 1.1, h: 0.26, fontSize: 9.5, color: C.mute, fontFace: BODY });
    s.addText(r[2], { x: 1.3, y: y + 0.19, w: 2.4, h: 0.4, fontSize: 14.5, color: C.ink, fontFace: BODY });
    s.addText('→', { x: 3.75, y: y + 0.16, w: 0.4, h: 0.4, fontSize: 14, color: C.mute, fontFace: BODY });
    s.addText(r[3], { x: 4.19, y: y + 0.16, w: 2.1, h: 0.4, fontSize: 14.5, bold: true, color: C.berry, fontFace: BODY });
  });

  s.addText('この判定はAIではなく、ルールベースで行う', {
    x: 0.62, y: 4.05, w: 5.7, h: 0.34, fontSize: 12.5, italic: true, color: C.mute, fontFace: BODY,
  });

  if (fs.existsSync(IMG('05-trends.png'))) {
    s.addShape(pptx.ShapeType.rect, { x: 6.36, y: 1.66, w: 3.17, h: 2.15, fill: { color: C.white }, line: { color: C.line, width: 1 } });
    s.addImage({ path: IMG('05-trends.png'), x: 6.42, y: 1.72, w: 3.05, h: 2.03 });
    s.addText('直近14日間の推移（濃い色が今日）', {
      x: 6.42, y: 3.8, w: 3.05, h: 0.3, fontSize: 10, color: C.mute, fontFace: BODY, align: 'center',
    });
  }
  footer(s);
}

/* ------------------------------- 3.5 AIである必然性（審査基準4） */
{
  const s = pptx.addSlide();
  s.background = { color: C.white };
  spine(s);
  title(s, 'なぜ、AIが要るのか', 'ルールだけでは「異常」は出せても、「様子」は答えられない');

  // 左：ルールでできること / 右：できないこと。対比で必然性を示す。
  const panes = [
    {
      head: 'ルールベースで足りる', color: C.ok,
      items: ['照明が12時間点かない → 異常', '深夜に3回以上点灯 → 注意', '安全判定（ON/OFF の可否）'],
      note: '判定は速く、説明でき、外部APIに依存しない。\nだからここはAIに渡していない。',
    },
    {
      head: 'ルールベースでは足りない', color: C.alert,
      items: ['「今日のお母さん、どう？」', '「最近ちょっと変じゃない？」', '数値ではなく、言葉で返す'],
      note: '家族が知りたいのは閾値超えの有無ではなく、\n「いつもと比べてどうか」という解釈。',
    },
  ];
  panes.forEach((p, i) => {
    const x = 0.62 + i * 4.6;
    s.addShape(pptx.ShapeType.rect, { x, y: 1.72, w: 4.25, h: 2.32, fill: { color: C.cream } });
    s.addShape(pptx.ShapeType.rect, { x, y: 1.72, w: 4.25, h: 0.07, fill: { color: p.color } });
    s.addText(p.head, {
      x: x + 0.26, y: 1.88, w: 3.8, h: 0.32, fontSize: 15, bold: true, color: p.color, fontFace: HEAD,
    });
    p.items.forEach((it, j) => {
      s.addText('・' + it, {
        x: x + 0.26, y: 2.26 + j * 0.31, w: 3.8, h: 0.3, fontSize: 12, color: C.ink, fontFace: BODY,
      });
    });
    s.addText(p.note, {
      x: x + 0.26, y: 3.28, w: 3.8, h: 0.62, fontSize: 10.5, color: C.note, fontFace: BODY, lineSpacing: 14,
    });
  });

  s.addShape(pptx.ShapeType.rect, { x: 0.62, y: 4.24, w: 8.83, h: 0.72, fill: { color: C.ink } });
  s.addText('AIは「言葉」に使い、「判断」には使わない ― 役割を分けたから、両方を捨てずに済んだ', {
    x: 0.62, y: 4.42, w: 8.83, h: 0.4, fontSize: 13.5, bold: true, color: C.white, fontFace: BODY, align: 'center',
  });
  footer(s);
}

/* ------------------------------- 4. 一番時間をかけたところ（主役） */
{
  const s = pptx.addSlide();
  s.background = { color: C.ink };
  s.addShape(pptx.ShapeType.rect, { x: 0, y: 0, w: 0.18, h: H, fill: { color: C.alert } });

  s.addText('AIに、危険な判断をさせない', {
    x: 0.62, y: 0.5, w: 8.8, h: 0.72, fontSize: 34, bold: true, color: C.white, fontFace: HEAD,
  });
  s.addText('このプロジェクトで最も時間をかけた部分', {
    x: 0.62, y: 1.22, w: 8.8, h: 0.36, fontSize: 14, color: C.rose, fontFace: BODY,
  });

  s.addText('「ストーブつけて」を誤解して、真夏に暖房を入れる。\n高齢者の家でそれをやると、便利どころか事故になる。', {
    x: 0.62, y: 1.78, w: 5.35, h: 0.85, fontSize: 14, color: C.cream, fontFace: BODY, lineSpacing: 22,
  });

  const pts = [
    ['1', '機器を安全クラスで分ける', '照明・扇風機は Safe、暖房・調理器具は Guarded'],
    ['2', 'ON と OFF を非対称にする', 'ON は安全確認のあと、OFF はそのまま許可'],
    ['3', '拒否も含めて全部記録する', '成功・失敗・拒否をすべて監査ログへ'],
  ];
  pts.forEach((p, i) => {
    const y = 2.78 + i * 0.63;
    s.addShape(pptx.ShapeType.ellipse, { x: 0.62, y, w: 0.42, h: 0.42, fill: { color: C.alert } });
    s.addText(p[0], { x: 0.62, y: y + 0.06, w: 0.42, h: 0.3, fontSize: 13, bold: true, color: C.white, fontFace: BODY, align: 'center' });
    s.addText(p[1], { x: 1.22, y: y - 0.02, w: 4.8, h: 0.3, fontSize: 14, bold: true, color: C.white, fontFace: BODY });
    s.addText(p[2], { x: 1.22, y: y + 0.26, w: 4.8, h: 0.28, fontSize: 11, color: C.mute, fontFace: BODY });
  });

  if (fs.existsSync(IMG('02-guardrail.png'))) {
    s.addShape(pptx.ShapeType.rect, { x: 6.22, y: 1.7, w: 3.31, h: 2.26, fill: { color: C.cream } });
    s.addImage({ path: IMG('02-guardrail.png'), x: 6.3, y: 1.78, w: 3.15, h: 2.1 });
    s.addText('「ストーブつけて」→ まず安全確認。機器は停止中のまま', {
      x: 6.22, y: 4.0, w: 3.31, h: 0.32, fontSize: 10, color: C.cream, fontFace: BODY, align: 'center',
    });
  }
}

/* ------------------------------------------- 5. 判定フロー */
{
  const s = pptx.addSlide();
  s.background = { color: C.white };
  spine(s, C.alert);
  title(s, 'AIの出力は「候補」でしかない', '最終判断は必ずルールベースの層を通る');

  const steps = [
    { t: '「ストーブつけて」', c: C.mute, w: 1.72 },
    { t: 'AI\n意図解析', c: C.rose, w: 1.35 },
    { t: '確信度\n0.85 以上?', c: C.berry, w: 1.5 },
    { t: '機器は\nSafe?', c: C.berry, w: 1.35 },
    { t: '実行', c: C.ok, w: 1.15 },
  ];
  let x = 0.62;
  steps.forEach((st, i) => {
    s.addShape(pptx.ShapeType.roundRect, { x, y: 1.9, w: st.w, h: 0.95, fill: { color: st.c }, rectRadius: 0.08 });
    s.addText(st.t, { x, y: 2.02, w: st.w, h: 0.72, fontSize: 11.5, bold: true, color: C.white, fontFace: BODY, align: 'center', lineSpacing: 15 });
    x += st.w;
    if (i < steps.length - 1) {
      s.addText('▶', { x: x + 0.02, y: 2.16, w: 0.36, h: 0.4, fontSize: 12, color: C.mute, fontFace: BODY, align: 'center' });
      x += 0.4;
    }
  });

  // 判定ボックスから下へ落ちる「No」の経路。線とラベルで分岐であることを示す。
  // ステップ配置: x=0.62 から w + 0.4 ずつ加算。判定ボックスの実中心を steps から算出する。
  const centerOf = (idx) => {
    let cx = 0.62;
    for (let i = 0; i < idx; i += 1) cx += steps[i].w + 0.4;
    return cx + steps[idx].w / 2;
  };
  const branches = [
    { cx: centerOf(2), w: 1.5, label: '聞き返す', fill: C.cream, fg: C.ink, bold: false },
    { cx: centerOf(3), w: 1.5, label: '確認 / 拒否', fill: C.alert, fg: C.white, bold: true },
  ];
  branches.forEach((br) => {
    s.addShape(pptx.ShapeType.rect, { x: br.cx - 0.015, y: 2.85, w: 0.03, h: 0.3, fill: { color: C.mute } });
    s.addText('No', { x: br.cx + 0.06, y: 2.84, w: 0.4, h: 0.28, fontSize: 9.5, color: C.alert, fontFace: BODY });
    s.addShape(pptx.ShapeType.roundRect, { x: br.cx - br.w / 2, y: 3.15, w: br.w, h: 0.55, fill: { color: br.fill }, rectRadius: 0.06 });
    s.addText(br.label, { x: br.cx - br.w / 2, y: 3.27, w: br.w, h: 0.32, fontSize: 11, bold: br.bold, color: br.fg, fontFace: BODY, align: 'center' });
  });

  s.addShape(pptx.ShapeType.rect, { x: 0.62, y: 4.05, w: 8.83, h: 0.78, fill: { color: C.ink } });
  s.addText('すべての経路が監査ログへ集まる ― 成功も、確認も、拒否も', {
    x: 0.62, y: 4.25, w: 8.83, h: 0.4, fontSize: 14, bold: true, color: C.white, fontFace: BODY, align: 'center',
  });
  footer(s);
}

/* ------------------------------------------- 6. 非対称設計 */
{
  const s = pptx.addSlide();
  s.background = { color: C.white };
  spine(s, C.alert);
  title(s, '「消す」は通す', '安全性を高める方向の操作まで止めると、使い物にならない');

  const cols = [
    { head: 'ストーブ つけて', res: '確認が先', color: C.alert, note: '火や熱をあつかう機器です。\n周囲の安全を確認してから操作します。' },
    { head: 'ストーブ 消して', res: 'そのまま受理', color: C.ok, note: '確認をはさまずに実行します。' },
  ];
  cols.forEach((cd, i) => {
    const x = 0.62 + i * 4.6;
    s.addShape(pptx.ShapeType.rect, { x, y: 1.75, w: 4.25, h: 1.85, fill: { color: C.cream } });
    s.addShape(pptx.ShapeType.rect, { x, y: 1.75, w: 4.25, h: 0.5, fill: { color: cd.color } });
    s.addText(cd.head, { x: x + 0.22, y: 1.83, w: 3.8, h: 0.34, fontSize: 14, bold: true, color: C.white, fontFace: BODY });
    s.addText(cd.res, { x: x + 0.22, y: 2.4, w: 3.8, h: 0.42, fontSize: 20, bold: true, color: cd.color, fontFace: HEAD });
    s.addText(cd.note, { x: x + 0.22, y: 2.88, w: 3.85, h: 0.62, fontSize: 11.5, color: C.ink, fontFace: BODY, lineSpacing: 16 });
  });

  s.addText('なぜ今すぐ動かないのかが使う人に見えないと、ただの故障に見える。\nそのため機器カードにも「周囲の安全を確認したうえでONにします」と表示している。', {
    x: 0.62, y: 3.85, w: 8.83, h: 0.75, fontSize: 12.5, color: C.ink, fontFace: BODY, lineSpacing: 20,
  });
  footer(s);
}

/* ------------------------------------------- 7. 構成 */
{
  const s = pptx.addSlide();
  s.background = { color: C.white };
  spine(s);
  title(s, '構成', 'APIキーがゼロでも、dotnet run だけで全機能が動く');

  const boxes = [
    ['入力', 'SwitchBot Plug Mini\nLINE Messaging API', C.rose],
    ['アプリ', '.NET 10 Blazor\nApp Service', C.berry],
    ['蓄積', 'Azure SQL\nFabric Eventhouse', C.berry],
    ['分析', 'Fabric Data Agent\nOrcaRouter (LLM)', C.rose],
  ];
  boxes.forEach((b, i) => {
    const x = 0.62 + i * 2.26;
    s.addShape(pptx.ShapeType.rect, { x, y: 1.8, w: 1.86, h: 1.35, fill: { color: C.cream } });
    s.addShape(pptx.ShapeType.rect, { x, y: 1.8, w: 1.86, h: 0.06, fill: { color: b[2] } });
    s.addText(b[0], { x: x + 0.16, y: 1.98, w: 1.6, h: 0.3, fontSize: 14, bold: true, color: b[2], fontFace: HEAD });
    s.addText(b[1], { x: x + 0.16, y: 2.32, w: 1.62, h: 0.72, fontSize: 10.5, color: C.ink, fontFace: BODY, lineSpacing: 15 });
    if (i < 3) {
      s.addText('▶', { x: x + 1.9, y: 2.32, w: 0.32, h: 0.3, fontSize: 11, color: C.mute, fontFace: BODY, align: 'center' });
    }
  });

  s.addShape(pptx.ShapeType.rect, { x: 0.62, y: 3.42, w: 8.83, h: 1.0, fill: { color: C.ink } });
  s.addText('データを2系統に分けた', {
    x: 0.92, y: 3.55, w: 4.0, h: 0.32, fontSize: 15, bold: true, color: C.white, fontFace: HEAD,
  });
  s.addText('DeviceEvents（状態変化）と SwitchBotPlugReadings（電力テレメトリ）は\nコードを共有していない。片方の障害がもう片方を巻き込まないため。', {
    x: 0.92, y: 3.87, w: 8.2, h: 0.5, fontSize: 11.5, color: C.cream, fontFace: BODY, lineSpacing: 16,
  });
  footer(s);
}

/* ------------------------------- 7.5 LLMコスト（審査基準6） */
{
  const s = pptx.addSlide();
  s.background = { color: C.white };
  spine(s);
  title(s, 'LLMコストは、設計で削る', '呼ぶ回数を減らす。これが一番効く');

  // 左：センサー経路（LLM 0回）／右：会話経路（ここだけ課金）
  const lanes = [
    {
      head: 'センサー経路', sub: '5分ごとのポーリング',
      big: '0', unit: '回', cap: 'LLM呼び出し',
      body: '1世帯あたり 288回/日 の取得と判定。すべてルールベースなので、デバイスが増えても LLM 費用は増えない。',
      color: C.ok,
    },
    {
      head: '会話経路', sub: '家族が話しかけたときだけ',
      big: '1', unit: '回', cap: '発話あたり',
      body: '意図解析・要約・通知文の生成のみ。費用は「世帯数」ではなく「会話した回数」に比例する。',
      color: C.rose,
    },
  ];
  lanes.forEach((l, i) => {
    const x = 0.62 + i * 4.6;
    s.addShape(pptx.ShapeType.rect, { x, y: 1.66, w: 4.25, h: 1.78, fill: { color: C.cream } });
    s.addShape(pptx.ShapeType.rect, { x, y: 1.66, w: 4.25, h: 0.07, fill: { color: l.color } });
    s.addText(l.head, { x: x + 0.24, y: 1.8, w: 2.4, h: 0.3, fontSize: 14, bold: true, color: l.color, fontFace: HEAD });
    s.addText(l.sub, { x: x + 0.24, y: 2.08, w: 2.5, h: 0.26, fontSize: 10, color: C.mute, fontFace: BODY });
    // 数字と単位は1つのテキストにする。別ボックスにすると単位だけ浮いて見える。
    s.addText([
      { text: l.big, options: { fontSize: 40, bold: true, color: l.color, fontFace: HEAD } },
      { text: ' ' + l.unit, options: { fontSize: 13, color: l.color, fontFace: BODY } },
    ], { x: x + 2.55, y: 1.76, w: 1.5, h: 0.62, align: 'right', margin: 0 });
    s.addText(l.cap, { x: x + 2.55, y: 2.36, w: 1.5, h: 0.24, fontSize: 9.5, color: C.mute, fontFace: BODY, align: 'right' });
    s.addText(l.body, {
      x: x + 0.24, y: 2.66, w: 3.8, h: 0.72, fontSize: 10, color: C.ink, fontFace: BODY, lineSpacing: 13, margin: 0,
    });
  });

  // モデル選択も実測に基づく（auto router は速いが遅延の幅が大きい）
  s.addShape(pptx.ShapeType.rect, { x: 0.62, y: 3.52, w: 8.83, h: 0.92, fill: { color: C.white }, line: { color: C.cream, width: 1.5 } });
  s.addText('締切のある経路だけ、安いモデルに固定する', {
    x: 0.86, y: 3.62, w: 5.4, h: 0.28, fontSize: 12.5, bold: true, color: C.ink, fontFace: HEAD,
  });
  s.addText('LINE の webhook は 8 秒で打ち切られる。自動ルータは同じプロンプトで 5.6〜51 秒とばらついたため、\nこの経路だけ gpt-4.1-mini に固定（実測 3〜5 秒）。他の画面は自動ルータのまま＝速さと費用を両取りする。', {
    x: 0.86, y: 3.9, w: 8.35, h: 0.46, fontSize: 10.5, color: C.mute, fontFace: BODY, lineSpacing: 13,
  });

  s.addShape(pptx.ShapeType.rect, { x: 0.62, y: 4.6, w: 8.83, h: 0.6, fill: { color: C.ink } });
  s.addText('APIキー0本でも全機能が動く ― 審査でも運用でも、課金せずに試せる状態を既定にした', {
    x: 0.62, y: 4.73, w: 8.83, h: 0.34, fontSize: 12.5, bold: true, color: C.white, fontFace: BODY, align: 'center',
  });
  footer(s);
}

/* ------------------------------------------- 8. 障害の話 */
{
  const s = pptx.addSlide();
  s.background = { color: C.ink };
  s.addShape(pptx.ShapeType.rect, { x: 0, y: 0, w: 0.18, h: H, fill: { color: C.alert } });

  s.addText('沈黙して壊れた', {
    x: 0.62, y: 0.48, w: 8.8, h: 0.7, fontSize: 34, bold: true, color: C.white, fontFace: HEAD,
  });
  s.addText('3件とも、例外は一度も投げていない', {
    x: 0.62, y: 1.2, w: 8.8, h: 0.34, fontSize: 14, color: C.rose, fontFace: BODY,
  });

  const items = [
    ['存在しないテーブルに投げ続けた', 'アプリ側だけ実装し、Eventhouse にテーブルを作っていなかった。\n1日半、400 を返し続けて容量を焼いた。'],
    ['宛先が Paused のまま、送信は成功していた', 'Event Hub は正常に受理し、アプリは「送信済み」を記録。\n3日分のデータが静かに消えた。'],
    ['3Dマスコットが一度も起動していなかった', '「動いている」と報告したが、実際は初期化されていなかった。\n計測方法そのものが間違っていた。'],
  ];
  items.forEach((it, i) => {
    const y = 1.78 + i * 1.02;
    s.addShape(pptx.ShapeType.rect, { x: 0.62, y, w: 8.83, h: 0.88, fill: { color: '3A2C31' } });
    s.addShape(pptx.ShapeType.rect, { x: 0.62, y, w: 0.07, h: 0.88, fill: { color: C.alert } });
    s.addText(it[0], { x: 0.92, y: y + 0.08, w: 8.3, h: 0.3, fontSize: 14, bold: true, color: C.white, fontFace: BODY });
    s.addText(it[1], { x: 0.92, y: y + 0.38, w: 8.3, h: 0.46, fontSize: 11, color: C.cream, fontFace: BODY, lineSpacing: 15 });
  });

  s.addText('「送信成功」は「到達」ではない。届いた側を見ないと気づけない。', {
    x: 0.62, y: 4.88, w: 8.83, h: 0.36, fontSize: 13, bold: true, italic: true, color: C.rose, fontFace: BODY,
  });
}

/* ------------------------------------------- 9. 使う人の画面 */
{
  const s = pptx.addSlide();
  s.background = { color: C.white };
  spine(s);
  title(s, '見る人と、使う人で画面を分ける', '必要な情報がまったく違うため');

  if (fs.existsSync(IMG('06-one-touch.png'))) {
    s.addShape(pptx.ShapeType.rect, { x: 0.54, y: 1.7, w: 4.46, h: 2.83, fill: { color: C.cream } });
    s.addImage({ path: IMG('06-one-touch.png'), x: 0.62, y: 1.78, w: 4.3, h: 2.67 });
    s.addText('利用者本人が使う /one-touch 画面', {
      x: 0.54, y: 4.58, w: 4.46, h: 0.3, fontSize: 10, color: C.mute, fontFace: BODY, align: 'center',
    });
  }
  s.addText([
    { text: '高齢の利用者本人が使う画面\n', options: { fontSize: 16, bold: true, color: C.berry, fontFace: HEAD } },
    { text: '文字とボタンを大きくした専用画面（/one-touch）を用意。\n見守る側のダッシュボードとは分けている。\n\n', options: { fontSize: 12.5, color: C.ink, fontFace: BODY } },
    { text: '家族はLINEで聞くだけ\n', options: { fontSize: 16, bold: true, color: C.berry, fontFace: HEAD } },
    { text: '専用アプリのインストールは不要。\n「今日のお母さんどう？」と聞けば、\n蓄積された生活データから答える', options: { fontSize: 12.5, color: C.ink, fontFace: BODY } },
  ], {
    x: 5.32, y: 1.85, w: 4.15, h: 2.8, lineSpacing: 19,
  });
  footer(s);
}

/* ------------------------------- 9.5 ビジネス成立性（審査基準2） */
{
  const s = pptx.addSlide();
  s.background = { color: C.white };
  spine(s);
  title(s, '誰が、いくら払うのか', 'カメラを置けなかった家庭が、そのまま顧客になる');

  // 桁数が違うので一律のサイズだと折り返して単位に重なる。カードごとに指定する。
  const stats = [
    { n: '約700万', u: '世帯', fs: 27, c: '65歳以上の一人暮らし\n（内閣府 高齢社会白書）' },
    { n: '3,000〜5,000', u: '円 / 月', fs: 19, c: '既存のセンサー型\n見守りサービスの相場' },
    { n: '0', u: '円', fs: 27, c: '新規の見守り機器\n（家電はすでにある）' },
  ];
  stats.forEach((st, i) => {
    const x = 0.62 + i * 2.98;
    s.addShape(pptx.ShapeType.rect, { x, y: 1.68, w: 2.75, h: 1.5, fill: { color: C.cream } });
    s.addText(st.n, {
      x: x + 0.1, y: 1.84, w: 2.55, h: 0.5, fontSize: st.fs, bold: true, color: C.berry,
      fontFace: HEAD, align: 'center', margin: 0, shrinkText: true,
    });
    s.addText(st.u, { x: x + 0.14, y: 2.36, w: 2.47, h: 0.24, fontSize: 11, color: C.rose, fontFace: BODY, align: 'center', margin: 0 });
    s.addText(st.c, { x: x + 0.14, y: 2.64, w: 2.47, h: 0.46, fontSize: 9.5, color: C.mute, fontFace: BODY, align: 'center', lineSpacing: 12, margin: 0 });
  });

  s.addText('成立の条件は「安いこと」ではなく、「置けること」', {
    x: 0.62, y: 3.30, w: 8.83, h: 0.32, fontSize: 15, bold: true, color: C.ink, fontFace: HEAD,
  });

  const points = [
    ['導入の障壁を外した', 'カメラもマットも要らない。スマートプラグを既存の家電に挿すだけ。\n「見張られる」と感じさせないから、本人が拒まない。'],
    ['受け手の負担も外した', '専用アプリを入れさせない。通知も操作も LINE の中で完結する。\nインストール率という最大の離脱要因が、そもそも発生しない。'],
    ['原価が積み上がらない', '判定はルールベース、LLM は会話時のみ。\n1世帯あたりの固定費が小さく、月額数百円台でも粗利が残る。'],
  ];
  points.forEach((p, i) => {
    const y = 3.68 + i * 0.48;
    s.addShape(pptx.ShapeType.rect, { x: 0.62, y: y + 0.06, w: 0.07, h: 0.32, fill: { color: C.rose } });
    s.addText(p[0], { x: 0.84, y, w: 2.1, h: 0.42, fontSize: 11.5, bold: true, color: C.berry, fontFace: BODY, margin: 0 });
    s.addText(p[1], { x: 3.0, y, w: 6.45, h: 0.42, fontSize: 10, color: C.ink, fontFace: BODY, lineSpacing: 12, margin: 0 });
  });
  footer(s);
}

/* ------------------------------------------- 10. まとめ */
{
  const s = pptx.addSlide();
  s.background = { color: C.berry };
  s.addShape(pptx.ShapeType.rect, { x: 0, y: 0, w: 0.18, h: H, fill: { color: C.rose } });

  s.addText('AIは会話に使い、判断には使わない', {
    x: 0.62, y: 0.75, w: 8.8, h: 0.8, fontSize: 32, bold: true, color: C.white, fontFace: HEAD,
  });

  const stats = [
    ['977', 'テスト（失敗ゼロ）'],
    ['0', '必要なAPIキー（デモ実行時）'],
    ['3', '記録した「沈黙した障害」'],
  ];
  stats.forEach((st, i) => {
    const x = 0.62 + i * 3.0;
    s.addText(st[0], { x, y: 1.85, w: 2.7, h: 0.85, fontSize: 54, bold: true, color: C.cream, fontFace: HEAD });
    s.addText(st[1], { x, y: 2.72, w: 2.7, h: 0.35, fontSize: 11.5, color: C.white, fontFace: BODY });
  });

  s.addShape(pptx.ShapeType.rect, { x: 0.62, y: 3.4, w: 8.83, h: 1.35, fill: { color: C.ink } });
  s.addText([
    { text: 'コード  ', options: { fontSize: 11, color: C.rose, fontFace: BODY, bold: true } },
    { text: 'https://github.com/monoqlo78/mimamoritai-careroute-ai\n', options: { fontSize: 11, color: C.white, fontFace: BODY } },
    { text: 'デモ動画  ', options: { fontSize: 11, color: C.rose, fontFace: BODY, bold: true } },
    { text: 'https://youtu.be/TnM-RHFZ_Lc （字幕・ナレーション入り / 3分11秒）\n', options: { fontSize: 11, color: C.white, fontFace: BODY } },
    { text: '解説記事  ', options: { fontSize: 11, color: C.rose, fontFace: BODY, bold: true } },
    { text: 'https://qiita.com/monoqlo78/items/27ea5bfa760bd8e3c3b7', options: { fontSize: 11, color: C.white, fontFace: BODY } },
  ], { x: 0.95, y: 3.52, w: 8.2, h: 0.9, lineSpacing: 17 });

  s.addText('安全判定をLLMの外側に置いたか、内側でお願いしたか。そこが一番の分岐点だった。', {
    x: 0.95, y: 4.32, w: 8.2, h: 0.34, fontSize: 11, italic: true, color: C.rose, fontFace: BODY,
  });
}

pptx.writeFile({ fileName: OUT }).then(() => {
  console.log('wrote:', OUT);
});
