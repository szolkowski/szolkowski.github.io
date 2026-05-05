import { expect, test } from '@playwright/test';
import {
  asJson,
  captureStructure,
  extractJsonLd,
  getBreadcrumbs,
  jsonLdTypes,
} from './fixtures';

test.describe('tags index (/tags/)', () => {
  test('invariants: BreadcrumbList + CollectionPage with ItemList of all tags', async ({
    page,
  }) => {
    await page.goto('/tags/');

    const blocks = await extractJsonLd(page);
    const types = jsonLdTypes(blocks);

    expect(types).toContain('BreadcrumbList');
    expect(types.filter((t) => t === 'CollectionPage').length).toBeGreaterThanOrEqual(1);

    const breadcrumbs = getBreadcrumbs(blocks);
    expect(breadcrumbs).not.toBeNull();
    expect(breadcrumbs!.map((b) => b.name)).toEqual(['Home', 'Tags']);

    const manualCollection = blocks.find(
      (b) =>
        b['@type'] === 'CollectionPage' &&
        typeof b['mainEntity'] === 'object' &&
        b['mainEntity'] !== null,
    );
    expect(manualCollection, 'manual CollectionPage with ItemList expected').toBeDefined();

    const itemList = manualCollection!['mainEntity'] as {
      '@type': string;
      numberOfItems: number;
      itemListElement: unknown[];
    };
    expect(itemList['@type']).toBe('ItemList');
    expect(itemList.numberOfItems).toBe(itemList.itemListElement.length);
    expect(itemList.numberOfItems).toBeGreaterThanOrEqual(20);
  });

  test('structural snapshot', async ({ page }) => {
    await page.goto('/tags/');
    const structure = await captureStructure(page);
    expect(asJson(structure)).toMatchSnapshot('structure.json');
  });

  test('breadcrumb snapshot', async ({ page }) => {
    await page.goto('/tags/');
    const blocks = await extractJsonLd(page);
    const breadcrumbs = getBreadcrumbs(blocks);
    expect(asJson(breadcrumbs)).toMatchSnapshot('breadcrumbs.json');
  });

  test('visual: full page', async ({ page }) => {
    await page.goto('/tags/');
    await page.waitForLoadState('networkidle');
    await expect(page).toHaveScreenshot(`tags-index-${process.platform}.png`, { fullPage: true });
  });
});
