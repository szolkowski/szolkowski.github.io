import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  snapshotDir: './tests/__snapshots__',
  snapshotPathTemplate: '{snapshotDir}/{testFileName}/{arg}{ext}',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : undefined,
  // 60s per-test budget covers the slowest page (firefox + Linux headless on
  // /tags/<slug> with the full tag-cloud) while still failing fast on real
  // hangs.
  timeout: 60_000,
  reporter: [
    ['list'],
    ...(process.env.GITHUB_ACTIONS ? [['github'] as const] : []),
    ['html', { outputFolder: 'playwright-report', open: 'never' }],
    ['json', { outputFile: 'playwright-report/results.json' }],
  ],
  use: {
    baseURL: 'http://localhost:4321',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    actionTimeout: 10_000,
    // 30s navigation covers Linux Docker firefox load times under parallel
    // worker contention. macOS chromium typically settles in <2s; this is a
    // headroom value, not a target.
    navigationTimeout: 30_000,
  },
  expect: {
    toHaveScreenshot: {
      maxDiffPixelRatio: 0.01,
      animations: 'disabled',
      caret: 'hide',
      scale: 'css',
    },
    toMatchSnapshot: {
      maxDiffPixelRatio: 0,
    },
  },
  // chromium is the source of truth for visual snapshots — those specs
  // self-skip on firefox/webkit via `test.skip(browserName !== 'chromium')`.
  // firefox/webkit cover structural, a11y, JSON-LD, and link tests so we
  // catch browser-specific CSS/JS regressions cheaply.
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 800 },
        deviceScaleFactor: 1,
      },
    },
    {
      name: 'firefox',
      use: {
        ...devices['Desktop Firefox'],
        viewport: { width: 1280, height: 800 },
      },
      grepInvert: /visual:/,
    },
    {
      name: 'webkit',
      use: {
        ...devices['Desktop Safari'],
        viewport: { width: 1280, height: 800 },
      },
      grepInvert: /visual:/,
    },
  ],
  webServer: {
    command:
      'bundle exec jekyll build --destination _site_test && npx http-server _site_test -p 4321 -s --no-dotfiles -c-1',
    url: 'http://localhost:4321/',
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
    env: { JEKYLL_ENV: 'production' },
    stdout: 'ignore',
    stderr: 'pipe',
  },
});
