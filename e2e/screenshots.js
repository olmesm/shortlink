// Capture dashboard screenshots for the README and docs site.
//
// Usage: with an app instance running and some data in it,
//   BASE_URL=http://localhost:18120 ADMIN_USER=admin ADMIN_PASS=... \
//   PLAYWRIGHT_CHROMIUM_PATH=/opt/pw-browsers/chromium \
//   node screenshots.js ../docs/screenshots
const { chromium } = require('playwright-core');
const path = require('path');
const fs = require('fs');

const BASE_URL = process.env.BASE_URL || 'http://localhost:8080';
const ADMIN_USER = process.env.ADMIN_USER || 'admin';
const ADMIN_PASS = process.env.ADMIN_PASS || '';
const outDir = path.resolve(process.argv[2] || 'screenshots');

(async () => {
  fs.mkdirSync(outDir, { recursive: true });
  const browser = await chromium.launch({
    executablePath: process.env.PLAYWRIGHT_CHROMIUM_PATH || undefined,
    chromiumSandbox: false,
  });
  const context = await browser.newContext({
    baseURL: BASE_URL,
    viewport: { width: 1280, height: 800 },
    deviceScaleFactor: 2,
  });
  const page = await context.newPage();

  const shot = async (name, opts = {}) => {
    await page.screenshot({ path: path.join(outDir, `${name}.png`), ...opts });
    console.log(`captured ${name}.png`);
  };

  // Login page first (while signed out), then sign in.
  await page.goto('/admin/login');
  await shot('login');
  await page.fill('input[name="username"]', ADMIN_USER);
  await page.fill('input[name="password"]', ADMIN_PASS);
  await page.click('button:has-text("Log in")');
  await page.waitForSelector('h1:has-text("Overview")');

  await shot('overview', { fullPage: true });

  await page.goto('/admin/short-urls');
  await page.waitForSelector('#su-table');
  await shot('short-urls');

  // Edit page of the short URL with redirect rules ("app").
  await page.locator('tr', { hasText: 'sho.rt/app' }).locator('a:has-text("Edit")').click();
  await page.waitForSelector('h1:has-text("Edit short URL")');
  await shot('edit-rules', { fullPage: true });

  // Analytics of the busiest link.
  await page.goto('/admin/short-urls');
  await page.locator('tr', { hasText: 'sho.rt/launch' }).locator('td:nth-child(5) a').click();
  await page.waitForSelector('.chart-card svg');
  await shot('visits', { fullPage: true });

  await page.goto('/admin/tags');
  await page.waitForSelector('#tag-table');
  await shot('tags');

  await page.goto('/admin/visits/orphan');
  await page.waitForSelector('h1:has-text("Orphan visits")');
  await shot('orphan-visits');

  await page.goto('/admin/api-keys');
  await page.waitForSelector('h1:has-text("API keys")');
  await shot('api-keys');

  await browser.close();
  console.log(`done → ${outDir}`);
})();
