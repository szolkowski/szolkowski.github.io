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
    // Pre-seed the GA consent banner as already-declined so visual snapshots
    // and a11y scans don't have to dismiss it on every test. Real users still
    // see the banner on first visit.
    storageState: {
      cookies: [],
      origins: [
        {
          origin: 'http://localhost:4321',
          localStorage: [
            { name: 'analytics_consent', value: 'denied' },
          ],
        },
      ],
    },
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
  // chromium is the source of truth for desktop visual snapshots — those
  // specs self-skip on firefox/webkit via grepInvert. firefox/webkit cover
  // structural, a11y, JSON-LD, and link tests so we catch browser-specific
  // CSS/JS regressions cheaply. mobile-chromium runs the whole suite at a
  // Pixel 7 viewport so the @media (max-width: 640px) branch (mobile-search
  // hoist) is regression-tested. tests/mobile.spec.ts is mobile-only and
  // contains the breakpoint-specific assertions + a mobile-only screenshot.
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 800 },
        deviceScaleFactor: 1,
      },
      testIgnore: /mobile\.spec\.ts/,
    },
    {
      name: 'firefox',
      use: {
        ...devices['Desktop Firefox'],
        viewport: { width: 1280, height: 800 },
      },
      grepInvert: /visual:/,
      testIgnore: /mobile\.spec\.ts/,
    },
    {
      name: 'webkit',
      use: {
        ...devices['Desktop Safari'],
        viewport: { width: 1280, height: 800 },
      },
      grepInvert: /visual:/,
      testIgnore: /mobile\.spec\.ts/,
    },
    {
      name: 'mobile-chromium',
      use: {
        ...devices['Pixel 7'],
      },
      // Skip desktop visual snapshots — those expect 1280px viewport and
      // their baselines are platform-keyed without a mobile axis. The
      // `mobile snapshot:` prefix in mobile.spec.ts bypasses this filter.
      grepInvert: /visual:/,
    },
  ],
  webServer: {
    command:
      'bundle exec jekyll build --destination _site_test && npx pagefind --site _site_test && npx http-server _site_test -p 4321 -s --no-dotfiles -c-1',
    url: 'http://localhost:4321/',
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
    env: { JEKYLL_ENV: 'production' },
    stdout: 'ignore',
    stderr: 'pipe',
  },
});
