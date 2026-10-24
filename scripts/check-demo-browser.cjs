// Optional browser integration checks. Install Playwright outside the application:
// npm install --prefix /tmp/volt-browser playwright
// NODE_PATH=/tmp/volt-browser/node_modules node scripts/check-demo-browser.cjs
// Serve the export on :4173 and the actual Volt application on :5093 first.
const { chromium } = require('playwright');
const staticOrigin = process.env.VOLT_STATIC_TEST_URL || 'http://127.0.0.1:4173';
const serverOrigin = process.env.VOLT_SERVER_TEST_URL || 'http://127.0.0.1:5093';

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    for (const width of [390, 768, 1440]) {
      const page = await browser.newPage({ viewport: { width, height: 900 } });
      const failures = [];
      page.on('pageerror', error => failures.push(error.message));
      page.on('response', response => {
        if (response.status() >= 400) failures.push(`${response.status()} ${response.url()}`);
      });
      for (const route of ['/', '/features/', '/islands/', '/about/']) {
        await page.goto(staticOrigin + route);
        const metrics = await page.evaluate(() => ({
          scroll: document.documentElement.scrollWidth,
          badCode: [...document.querySelectorAll('pre')].some(e => e.textContent.includes('&lt;')),
        }));
        if (metrics.scroll > width || metrics.badCode || failures.length)
          throw Error(JSON.stringify({ width, route, metrics, failures }));
        console.log('PASS static', width, route);
      }
      await page.close();
    }
    const page = await browser.newPage();
    await page.goto(staticOrigin + '/islands/');
    if (!await page.getByRole('button', { name: 'Increment requires' }).isDisabled())
      throw Error('Static Counter must be disabled');
    await page.goto(serverOrigin + '/islands/');
    await page.getByRole('button', { name: '+1', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.counter').textContent.includes('count: 1'));
    console.log('PASS real hydrated C# action');
    await page.goto(serverOrigin + '/signup/');
    await page.getByLabel('Email', { exact: true }).fill('not-an-email');
    await page.getByRole('button', { name: 'Validate on the server' }).click();
    await page.getByRole('alert').waitFor();
    await page.getByLabel('Email', { exact: true }).fill('developer@example.com');
    await page.getByRole('button', { name: 'Validate on the server' }).click();
    await page.getByText('Valid email: developer@example.com', { exact: true }).waitFor();
    console.log('PASS real form validation');
    const nojs = await browser.newContext({ javaScriptEnabled: false });
    const fallback = await nojs.newPage();
    await fallback.goto(serverOrigin + '/islands/');
    await fallback.getByRole('button', { name: '+1', exact: true }).click();
    if (!(await fallback.locator('.counter').textContent()).includes('count: 1'))
      throw Error('No-JS Counter failed');
    console.log('PASS no-JS C# fallback');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
