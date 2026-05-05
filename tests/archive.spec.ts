import { expect, test } from '@playwright/test';
import { asJson, captureStructure } from './fixtures';

test.describe('archive page (/archive/)', () => {
  test('invariants: nav, canonical, post list non-empty', async ({ page }) => {
    await page.goto('/archive/');

    const structure = await captureStructure(page);
    expect(structure.nav.length).toBeGreaterThan(0);
    expect(structure.meta.canonical).toBeTruthy();

    const postLinks = await page.locator('main a[href*="/20"]').count();
    expect(postLinks, 'archive should list at least one post').toBeGreaterThan(0);
  });

  test('structural snapshot', async ({ page }) => {
    await page.goto('/archive/');
    const structure = await captureStructure(page);
    expect(asJson(structure)).toMatchSnapshot('structure.json');
  });

  test('visual: full page', async ({ page }) => {
    await page.goto('/archive/');
    await page.waitForLoadState('networkidle');
    await expect(page).toHaveScreenshot('archive.png', { fullPage: true });
  });
});
