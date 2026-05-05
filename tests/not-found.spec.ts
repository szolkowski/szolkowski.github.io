import { expect, test } from '@playwright/test';
import { asJson, captureStructure } from './fixtures';

test.describe('404 page (/404.html)', () => {
  test('invariants: nav present, title indicates not-found', async ({ page }) => {
    await page.goto('/404.html');

    const structure = await captureStructure(page);
    expect(structure.nav.length).toBeGreaterThan(0);
    const title = structure.meta.title.toLowerCase();
    expect(
      title.includes('404') || title.includes('not found'),
      `expected /404.html title to indicate not-found, got: ${structure.meta.title}`,
    ).toBe(true);
  });

  test('structural snapshot', async ({ page }) => {
    await page.goto('/404.html');
    const structure = await captureStructure(page);
    expect(asJson(structure)).toMatchSnapshot('structure.json');
  });

  test('visual: full page', async ({ page }) => {
    await page.goto('/404.html');
    await page.waitForLoadState('networkidle');
    await expect(page).toHaveScreenshot('not-found.png', { fullPage: true });
  });
});
