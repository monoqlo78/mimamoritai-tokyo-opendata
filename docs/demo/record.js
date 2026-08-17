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
  await p.goto('http://localhost:5234', { waitUntil: 'networkidle' });
  await p.addStyleTag({ content: 'html{zoom:1.45}' });
  await p.waitForTimeout(4000);

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

  mark('01'); await p.waitForTimeout(10000);
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
  mark('12'); await openSection('家電の状態', -120); await p.waitForTimeout(12500);

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
  fs.writeFileSync(require('path').join(__dirname, 'scenes.json'), JSON.stringify(scenes, null, 2));
  console.log(scenes.map(s => `${s.name} ${(s.startMs / 1000).toFixed(1)}`).join('\n'));
})();
