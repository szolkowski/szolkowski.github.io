import { expect, test } from '@playwright/test';
import {
  asJson,
  captureStructure,
  extractJsonLd,
  getBreadcrumbs,
} from './fixtures';

const TAG_PATH = '/tags/hangfire.html';

test.describe(`tag page (${TAG_PATH})`, () => {
  test('invariants: BreadcrumbList Home > Tags > Tag, description meta', async ({
    page,
  }) => {
    await page.goto(TAG_PATH);

    const structure = await captureStructure(page);
    expect(structure.jsonLdTypes).toContain('BreadcrumbList');
    expect(structure.meta.description).toBeTruthy();

    const blocks = await extractJsonLd(page);
    const breadcrumbs = getBreadcrumbs(blocks);
    expect(breadcrumbs).not.toBeNull();
    expect(breadcrumbs!.map((b) => b.name)).toEqual([
      'Home',
      'Tags',
      expect.any(String),
    ]);
  });

  test('structural snapshot', async ({ page }) => {
    await page.goto(TAG_PATH);
    const structure = await captureStructure(page);
    expect(asJson(structure)).toMatchSnapshot('structure.json');
  });

  test('visual: above the fold', async ({ page }) => {
    await page.goto(TAG_PATH);
    await page.waitForLoadState('domcontentloaded');
    await expect(page).toHaveScreenshot(`tag-page-above-fold-${process.platform}.png`, {
      fullPage: false,
    });
  });
});
