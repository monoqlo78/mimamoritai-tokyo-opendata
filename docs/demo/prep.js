// 収録前の状態リセット。扇風機を停止に戻し、チャット履歴を消す。
//
// 本編の終盤で「扇風機をつけて」を実演するため、収録が終わると扇風機は ON、
// チャットには履歴が残った状態になる。そのまま撮り直すと AI が
// 「すでについています」と返してしまい実演にならないので、record.js の前に必ず実行する。
//
//   node prep.js
const { chromium } = require('playwright');

(async () => {
  const b = await chromium.launch();
  const p = await (await b.newContext({ viewport: { width: 1600, height: 1000 } })).newPage();
  await p.goto('http://localhost:5234', { waitUntil: 'networkidle' });

  const say = async (text, expect) => {
    const box = p.locator('.chat-input input').first();
    await box.click();
    await box.fill('');
    await box.type(text, { delay: 40 });
    await p.getByRole('button', { name: '聞く' }).first().click();
    try {
      await p.waitForFunction((w) => document.body.innerText.includes(w), expect, { timeout: 45000 });
      console.log(`OK  ${text} -> ${expect}`);
    } catch (e) {
      console.log(`NG  ${text} (expected ${expect})`);
    }
    await p.waitForTimeout(1500);
  };

  await p.getByText('気になることを聞いてください').first().scrollIntoViewIfNeeded();
  // 安全側の設計どおり、操作は必ず確認をはさむ。1コマンド＝1確認。
  await say('扇風機を消して', 'よろしいですか');
  await say('はい', '消しました');

  const clear = p.getByRole('button', { name: /履歴|消す|クリア/ }).first();
  if (await clear.count()) {
    await clear.click();
    await p.waitForTimeout(1200);
    console.log('history cleared');
  } else {
    console.log('clear button not found');
  }

  await b.close();
})();
