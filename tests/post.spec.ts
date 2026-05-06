import { expect, test } from '@playwright/test';
import {
  asJson,
  captureStructure,
  extractJsonLd,
  getBreadcrumbs,
  jsonLdTypes,
} from './fixtures';

const POST_PATH =
  '/2026/04/13/optipowertools-hangfire-2-0-cms-13-support.html';

test.describe(`post page (${POST_PATH})`, () => {
  test('invariants: BlogPosting + BreadcrumbList, canonical, og:image, description', async ({
    page,
  }) => {
    await page.goto(POST_PATH);

    const structure = await captureStructure(page);
    expect(structure.jsonLdTypes).toContain('BlogPosting');
    expect(structure.jsonLdTypes).toContain('BreadcrumbList');

    expect(structure.meta.canonical).toBeTruthy();
    expect(structure.meta.ogImage).toBeTruthy();
    expect(structure.meta.description?.length).toBeGreaterThanOrEqual(50);

    const blocks = await extractJsonLd(page);
    const breadcrumbs = getBreadcrumbs(blocks);
    expect(breadcrumbs).not.toBeNull();
    expect(breadcrumbs!.map((b) => b.name)).toEqual([
      'Home',
      expect.any(String),
      expect.any(String),
    ]);

    void jsonLdTypes;
  });

  test('structural snapshot', async ({ page }) => {
    await page.goto(POST_PATH);
    const structure = await captureStructure(page);
    expect(asJson(structure)).toMatchSnapshot('structure.json');
  });

  test('visual: above the fold', async ({ page }) => {
    await page.goto(POST_PATH);
    await page.waitForLoadState('networkidle');
    await expect(page).toHaveScreenshot(`post-above-fold-${process.platform}.png`, {
      fullPage: false,
    });
  });
});
