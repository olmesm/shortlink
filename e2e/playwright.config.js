// @ts-check
const { defineConfig } = require('@playwright/test');

const PORT = process.env.E2E_PORT || '18100';
const BASE_URL = `http://localhost:${PORT}`;

// CI sandboxes can point at a preinstalled Chromium instead of downloading one.
const executablePath = process.env.PLAYWRIGHT_CHROMIUM_PATH || undefined;

module.exports = defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  timeout: 30_000,
  use: {
    baseURL: BASE_URL,
    chromiumSandbox: false,
    launchOptions: { executablePath },
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'setup', testMatch: /auth\.setup\.js/ },
    {
      name: 'chromium',
      dependencies: ['setup'],
      use: { storageState: '.auth/admin.json' },
      testIgnore: /auth\.setup\.js/,
    },
  ],
  webServer: {
    // Note: dotnet run executes the app with the *project* directory as its
    // working directory, so the data dir must be an absolute path.
    command:
      'rm -rf "$PWD/.run" && mkdir -p "$PWD/.run" && ' +
      `SHORTLINK_DATA_DIR="$PWD/.run" SHORTLINK_PORT=${PORT} SHORTLINK_DEFAULT_DOMAIN=localhost:${PORT} ` +
      'SHORTLINK_AUTO_RESOLVE_TITLES=false ' +
      'SHORTLINK_INITIAL_ADMIN_USERNAME=admin SHORTLINK_INITIAL_ADMIN_PASSWORD=e2e-password-123 ' +
      'dotnet run --project ../src/Shortlink.Web',
    url: `${BASE_URL}/rest/health`,
    reuseExistingServer: false,
    timeout: 120_000,
  },
});
