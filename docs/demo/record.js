const { chromium } = require('playwright');
const fs = require('fs');

const OUT = require('path').join(__dirname, 'out');
fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });

const scenes = [];
let t0 = 0;
function mark(name) { scenes.push({ name, startMs: Math.round(Date.now() - t0) }); }

(async () => {
  const b = await chromium.launch();
  const ctx = await b.newContext({
    viewport: { width: 1920, height: 1080 },
    recordVideo: { dir: OUT, size: { width: 1920, height: 1080 } },
  });
  const p = await ctx.newPage();
  // Playwright starts the video with the page, not with the first mark. Keep the
  // moment so the leading idle can be trimmed off exactly instead of by eye.
  const videoStart = Date.now();

  const WEB = 'http://localhost:5234';
  const FABRIC = 'http://localhost:5199';

  // Reading zoom. The console is a denser layout than the app, so it gets less.
  const open = async (url, zoom, settle = 4000) => {
    await p.goto(url, { waitUntil: 'networkidle' });
    await p.addStyleTag({ content: `html{zoom:${zoom}}` });
    await p.waitForTimeout(settle);
  };

  // Vite compiles the console's module graph on the first request, which cost
  // 20 seconds of dead air in the middle of the take. Pay it before t0 instead;
  // trim.ps1 cuts everything before scene 01 anyway. The console also refreshes
  // on a timer, so 'networkidle' never settles there — wait for content instead.
  const openFabric = async () => {
    await p.goto(FABRIC, { waitUntil: 'domcontentloaded' });
    await p.getByText('データはこう流れています').first().waitFor({ timeout: 30000 });
    await p.addStyleTag({ content: 'html{zoom:1.30}' });
    await p.waitForTimeout(2000);
  };

  await openFabric();
  await open(WEB, 1.45);

  // zoom 環境では scrollIntoView がずれるため rect 基準で補正する
  const scrollTo = async (loc, offset) => {
    await loc.evaluate((el, o) => {
      window.scrollBy({ top: el.getBoundingClientRect().top + o, behavior: 'smooth' });
    }, offset);
    await p.waitForTimeout(1000);
    for (let i = 0; i < 5; i++) {
      const d = await loc.evaluate((el, o) => {
        const dy = el.getBoundingClientRect().top + o;
        if (Math.abs(dy) >= 3) window.scrollBy({ top: dy, behavior: 'auto' });
        return dy;
      }, offset);
      if (Math.abs(d) < 3) break;
      await p.waitForTimeout(250);
    }
  };
  const go = (text, offset = -110) => scrollTo(p.locator(`text=${text}`).first(), offset);
  const openSection = async (label, offset = -110) => {
    const s = p.getByText(label, { exact: true }).first();
    await scrollTo(s, offset);
    await s.click();
    await p.waitForTimeout(900);
    await scrollTo(s, offset);
  };

  t0 = Date.now();

  mark('01'); await p.waitForTimeout(16000);

  // 家に置く機器。SwitchBot の公式製品写真は権利上使えないので、
  // 自分で描いた概念図（docs/images/hardware.svg）をブラウザで開いて見せる。
  await p.goto('file:///' + require('path').join(__dirname, 'hardware.html').replace(/\\/g, '/'));
  await p.waitForTimeout(1200);
  mark('H1'); await p.waitForTimeout(13500);

  // 運用コンソール（Microsoft Fabric 上でホストしている Rayfin アプリ）。
  // 収録モードでは Fabric のサインインを通さず、本番データベースから抽出した
  // スナップショットを描画する。詳細は fabric-app/src/services/CaptureAuthService.ts。
  await openFabric();
  // 既定は本番世帯だけの集計。全体像を見せる場面なので両方を足した表示にする。
  await p.getByRole('button', { name: /すべて/ }).first().click();
  await p.waitForTimeout(2000);

  // 先頭でコンソールのタイトルと KPI を見せてから、データフロー図へ寄る。
  mark('F1'); await p.waitForTimeout(5000);
  await go('データはこう流れています', -110); await p.waitForTimeout(7000);
  mark('F2'); await go('Azure Model Router が選んだモデル', -110); await p.waitForTimeout(9000);

  await open(WEB, 1.45, 2500);

  mark('02'); await go('お母さんの今日', -130); await p.waitForTimeout(9500);
  mark('03'); await go('電気が動きはじめた', -130); await p.waitForTimeout(9500);
  mark('04'); await go('この24時間の電気の使いかた', -130); await p.waitForTimeout(13500);
  mark('05'); await go('電気が動きはじめた時間', -130); await p.waitForTimeout(12500);
  mark('06'); await go('電気の使用量と外の気温', -130); await p.waitForTimeout(16000);

  mark('07');
  {
    const d = p.locator('summary', { hasText: '数値で見る' }).first();
    await scrollTo(d, -320);
    await p.waitForTimeout(800);
    await d.click();
    await p.waitForTimeout(10500);
  }

  mark('08'); await go('公的データで見守りを補強しています', -140); await p.waitForTimeout(13500);

  mark('09');
  {
    await go('気になることを聞いてください', -150);
    const box = p.locator('.chat-input input').first();
    await box.click();
    await box.type('今日のお母さんの様子は？', { delay: 110 });
    await p.waitForTimeout(1500);
    await p.getByRole('button', { name: '聞く' }).first().click();
    await p.waitForTimeout(6000);
  }

  mark('10');
  {
    const t = Date.now();
    try {
      await p.waitForFunction(() => !document.body.innerText.includes('考えています'), null, { timeout: 45000 });
    } catch (e) { console.log('answer timeout'); }
    const spent = Date.now() - t;
    if (spent < 12000) await p.waitForTimeout(12000 - spent);
    await p.waitForTimeout(2500);
  }

  mark('11'); await go('家の中の様子', -150); await p.waitForTimeout(11500);
  mark('12'); await openSection('家電の状態', -120); await p.waitForTimeout(11000);

  // ふつうの言葉での家電操作。扇風機は DeviceSafetyPolicy で Safe なので
  // ブラウザ標準の confirm() ではなく、会話の中で確認して実行できる。
  mark('D1');
  {
    await go('気になることを聞いてください', -150);
    const box = p.locator('.chat-input input').first();
    await box.click();
    await box.type('扇風機をつけて', { delay: 110 });
    await p.waitForTimeout(1200);
    await p.getByRole('button', { name: '聞く' }).first().click();
    try {
      await p.waitForFunction(() => document.body.innerText.includes('よろしいですか'), null, { timeout: 30000 });
    } catch (e) { console.log('confirm timeout'); }
    await p.waitForTimeout(4500);
  }

  mark('D2');
  {
    const box = p.locator('.chat-input input').first();
    await box.click();
    await box.type('はい', { delay: 140 });
    await p.waitForTimeout(1200);
    await p.getByRole('button', { name: '聞く' }).first().click();
    try {
      await p.waitForFunction(() => document.body.innerText.includes('つけました'), null, { timeout: 30000 });
    } catch (e) { console.log('exec timeout'); }
    await p.waitForTimeout(6000);
    await go('家電の状態', -120);
    await p.waitForTimeout(8000);
  }

  mark('13');
  await openSection('詳しい情報・デモ操作', -120);
  await go('接続状況', -150);
  await p.waitForTimeout(12500);

  mark('14');
  await p.evaluate(() => window.scrollTo({ top: 0, behavior: 'smooth' }));
  await p.waitForTimeout(10000);

  mark('END');

  await ctx.close();
  await b.close();
  const D = require('path');
  fs.writeFileSync(D.join(__dirname, 'scenes.json'), JSON.stringify(scenes, null, 2));
  // The offset the trim step needs: how much of the recording happened before
  // scene 01 started.
  fs.writeFileSync(
    D.join(__dirname, 'recording.json'),
    JSON.stringify({ leadInMs: t0 - videoStart, video: fs.readdirSync(OUT)[0] }, null, 2)
  );
  console.log(scenes.map(s => `${s.name} ${(s.startMs / 1000).toFixed(1)}`).join('\n'));
})();
