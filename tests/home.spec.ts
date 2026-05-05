import { expect, test } from '@playwright/test';
import {
  asJson,
  captureStructure,
  extractJsonLd,
  jsonLdTypes,
  stripVolatileFields,
} from './fixtures';

test.describe('home page (/)', () => {
  test('invariants: nav, footer social, JSON-LD types', async ({ page }) => {
    await page.goto('/');

    const structure = await captureStructure(page);

    expect(structure.nav.map((n) => n.label)).toEqual([
      'About',
      'Archive',
      'Tags',
    ]);

    expect(structure.footerSocial).toHaveLength(3);

    expect(structure.jsonLdTypes).toContain('WebSite');

    expect(structure.meta.title).toBeTruthy();
    expect(structure.meta.description).toBeTruthy();
    expect(structure.meta.canonical).toBeTruthy();
    expect(structure.meta.ogImage).toBeTruthy();
    expect(structure.meta.twitterCard).toBeTruthy();
  });

  test('structural snapshot', async ({ page }) => {
    await page.goto('/');
    const structure = await captureStructure(page);
    expect(asJson(structure)).toMatchSnapshot('structure.json');
  });

  test('JSON-LD WebSite block snapshot', async ({ page }) => {
    await page.goto('/');
    const blocks = await extractJsonLd(page);
    const website = blocks.find((b) => b['@type'] === 'WebSite');
    expect(website).toBeDefined();
    expect(asJson(stripVolatileFields(website))).toMatchSnapshot('jsonld-website.json');
    void jsonLdTypes;
  });

  test('visual: full page', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    await expect(page).toHaveScreenshot(`home-${process.platform}.png`, { fullPage: true });
  });
});
