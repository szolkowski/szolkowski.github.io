import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

// Mobile-specific regression tests. Only runs in the `mobile-chromium`
// Playwright project (Pixel 7 viewport ≈ 412×915). Desktop projects skip
// this file via `testIgnore` in playwright.config.ts.
//
// Visual tests use the prefix `mobile snapshot:` rather than the desktop
// `visual:` prefix so the desktop projects' grepInvert doesn't interfere.

test.describe('mobile breakpoint', () => {
  test('mobile-search is visible, sidebar-search is hidden', async ({
    page,
  }) => {
    await page.goto('/');
    await expect(page.locator('.mobile-search')).toBeVisible();
    await expect(page.locator('.sidebar-search')).toBeHidden();
  });

  test('home page has no critical or serious a11y violations at mobile width', async ({
    page,
  }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');

    const results = await new AxeBuilder({ page })
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
      .exclude('[id^="embedded-badge-"]')
      .exclude('#disqus_thread')
      .analyze();

    const blocking = results.violations.filter(
      (v) => v.impact === 'critical' || v.impact === 'serious',
    );
    const formatted = blocking.map((v) => ({
      id: v.id,
      impact: v.impact,
      help: v.help,
      helpUrl: v.helpUrl,
      nodes: v.nodes.map((n) => ({ html: n.html, target: n.target })),
    }));
    expect(formatted, `axe-core violations on / at mobile`).toEqual([]);
  });

  test('mobile snapshot: home above the fold', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('domcontentloaded');
    await expect(page).toHaveScreenshot(
      `home-mobile-${process.platform}.png`,
      { fullPage: false },
    );
  });
});
