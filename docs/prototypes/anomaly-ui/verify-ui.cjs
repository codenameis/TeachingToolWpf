const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { pathToFileURL } = require('node:url');
const { chromium } = require('C:/Users/Kim Bohyun/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');

async function main() {
  const output = path.join(__dirname, 'previews');
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    headless: true
  });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' });
  const errors = [];
  const checks = [];
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error' || message.type() === 'warning') errors.push(message.text());
  });
  const check = (name, condition) => { assert.ok(condition, name); checks.push(name); };
  async function load(file) {
    await page.goto(pathToFileURL(path.join(__dirname, file)).href);
    await page.locator('canvas').first().waitFor();
    await page.waitForTimeout(150);
  }
  async function responsive(prefix) {
    for (const width of [320, 768, 1024, 1440]) {
      await page.setViewportSize({ width, height: 1000 });
      const bounds = await page.evaluate(() => ({
        viewport: innerWidth,
        document: document.documentElement.scrollWidth,
        unnamedButtons: [...document.querySelectorAll('button')].filter(button =>
          !button.textContent.trim() && !button.getAttribute('aria-label')).length
      }));
      check(`${prefix}: ${width}px horizontal overflow`, bounds.document <= bounds.viewport);
      check(`${prefix}: ${width}px button accessible names`, bounds.unnamedButtons === 0);
    }
    await page.keyboard.press('Tab');
    const focus = await page.evaluate(() => document.activeElement.tagName);
    check(`${prefix}: keyboard focus`, ['BUTTON', 'A', 'INPUT', 'SUMMARY', 'SELECT'].includes(focus));
    fs.writeFileSync(path.join(output, `${prefix}-accessibility.txt`), await page.locator('body').ariaSnapshot());
  }
  try {
    await load('01-control-room.html');
    check('Professional: 4 camera panels', await page.locator('.feed').count() === 4);
    check('Professional: initial 2 pending examples', await page.locator('#pendingCount').innerText() === '미확인 2');
    await responsive('professional');
    await page.screenshot({ path: path.join(output, '01-control-room.png'), fullPage: true });
    await page.getByRole('button', { name: '감시 일시정지', exact: true }).click();
    check('Professional: pause disables new events', await page.locator('#injectAppearance').isDisabled());
    check('Professional: paused status differs from normal', (await page.locator('#feedStatus1').innerText()).includes('일시정지'));
    await page.getByRole('button', { name: '감시 다시 시작', exact: true }).click();
    await page.locator('#injectMotion').click();
    check('Professional: new event updates count', await page.locator('#pendingCount').innerText() === '미확인 3');
    await page.locator('#ackEvent').click();
    check('Professional: acknowledgement updates count', await page.locator('#pendingCount').innerText() === '미확인 2');
    await page.locator('[data-layout="single"]').click();
    check('Professional: single layout', await page.locator('.feed:visible').count() === 1);
    await page.locator('[data-layout="grid"]').click();
    await page.locator('[data-view="events"]').click();
    check('Professional: history includes injected event', await page.locator('#eventTable tr').count() === 4);
    await page.locator('[data-view="models"]').click();
    check('Professional: model state page', await page.locator('.model:visible').count() === 2);
    await page.locator('[data-view="live"]').click();
    await page.locator('#settingsNav').click();
    await page.locator('#threshold').fill('95');
    await page.getByRole('button', { name: '현재 탭에 적용' }).click();
    await page.locator('#injectAppearance').click();
    check('Professional: applied threshold suppresses lower score', await page.locator('#eventTable tr').count() === 4);
    await page.locator('#settingsNav').click();
    await page.keyboard.press('Escape');
    check('Professional: Escape closes modal', !(await page.locator('#settingsDialog').isVisible()));
    await page.locator('#cameraSearch').fill('없는카메라');
    check('Professional: meaningful search empty state', (await page.locator('#cameraList').innerText()).includes('없습니다'));
    await page.locator('#cameraSearch').fill('');

    await load('02-guided-monitor.html');
    check('Guided: monitoring unavailable before normal reference', await page.locator('[data-step="3"]').isDisabled());
    await responsive('guided');
    await page.screenshot({ path: path.join(output, '02-guided-setup.png'), fullPage: true });
    await page.locator('#continueToLearn').click();
    for (const checkbox of await page.locator('#samples input').all()) await checkbox.uncheck();
    check('Guided: no normal examples prevents preparation', await page.locator('#learnButton').isDisabled());
    await page.locator('#samples input').first().check();
    await page.locator('#learnButton').click();
    check('Guided: preparation shows loading', await page.locator('#learning').isVisible());
    check('Guided: configuration locked during preparation', await page.locator('[data-step="1"]').isDisabled());
    await page.locator('#readyMessage').waitFor({ state: 'visible', timeout: 8000 });
    await page.locator('#startMonitoring').click();
    const normalFrame = await page.locator('#previewCanvas').evaluate(canvas => canvas.toDataURL());
    await page.locator('#appearanceDemo').click();
    check('Guided: anomaly visibly changes the canvas immediately', normalFrame !== await page.locator('#previewCanvas').evaluate(canvas => canvas.toDataURL()));
    check('Guided: brief appearance anomaly creates alarm', (await page.locator('#alarmTitle').innerText()).includes('제품 하나'));
    check('Guided: event recorded', await page.locator('.history-entry').count() === 1);
    await page.screenshot({ path: path.join(output, '02-guided-alarm.png'), fullPage: true });
    await page.locator('#acknowledge').click();
    check('Guided: acknowledgement clears pending card', !(await page.locator('#alarmCard').isVisible()));
    await page.locator('#motionDemo').click();
    check('Guided: temporal anomaly has its own explanation', (await page.locator('#alarmReason').innerText()).includes('3.2초'));
    await page.locator('#markNormal').click();
    check('Guided: false alarm recorded separately', (await page.locator('#historyEntries').innerText()).includes('정상으로 기록'));
    await page.locator('#pauseMonitoring').click();
    check('Guided: pause disables injection', await page.locator('#appearanceDemo').isDisabled());
    await page.locator('#pauseMonitoring').click();
    await page.locator('summary').click();
    await page.locator('#disconnect').click();
    check('Guided: disconnection is not normal', (await page.locator('#viewerState').innerText()).includes('검사 중단'));
    check('Guided: disconnected injection disabled', await page.locator('#appearanceDemo').isDisabled());
    await page.locator('#reconnect').click();
    check('Guided: reconnect restores monitoring', await page.locator('#appearanceDemo').isEnabled());
    await page.locator('#appearanceDemo').click();
    await page.setViewportSize({ width: 320, height: 900 });
    check('Guided: alarm screen fits narrow viewport', await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({ path: path.join(output, '02-guided-mobile.png'), fullPage: true });
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.locator('[data-step="1"]').click();
    await page.locator('[data-source="hallway"]').click();
    check('Guided: changed source invalidates old reference', await page.locator('[data-step="3"]').isDisabled());
    check('Guided: CCTV sample selection updates name', await page.locator('#cameraName').innerText() === '물류 이동 통로');
    await page.locator('#continueToLearn').click();
    await page.locator('#learnButton').click();
    await page.locator('#readyMessage').waitFor({ state: 'visible', timeout: 8000 });
    await page.locator('#startMonitoring').click();
    check('Guided: prior source pending event is preserved', (await page.locator('#alarmTime').innerText()).includes('제품 검사 라인'));
    check('Guided: prior source alarm is distinguished from current video', (await page.locator('#sceneCaption').innerText()).includes('다른 영상'));
    await page.locator('[data-history]').click();
    await page.locator('#acknowledge').click();
    check('Guided: prior source pending event remains reviewable', await page.locator('#alarmCount').innerText() === '없어요');
    await page.locator('#helpButton').click();
    await page.keyboard.press('Escape');
    check('Guided: help dialog keyboard dismissal', !(await page.locator('#helpDialog').isVisible()));
    check('Both prototypes: no console warnings or runtime errors', errors.length === 0);
    const report = { result: 'PASS', checks, browser: await browser.version(), viewports: [320,768,1024,1440], consoleErrors: errors,
      scope: 'HTML prototype UI only; no real inference, network camera or model training.' };
    fs.writeFileSync(path.join(output, 'verification.json'), JSON.stringify(report, null, 2));
    console.log(JSON.stringify(report, null, 2));
  } finally {
    await browser.close();
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
