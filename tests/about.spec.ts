import { expect, test } from '@playwright/test';
import { asJson, captureStructure, extractJsonLd } from './fixtures';

test.describe('about page (/about/)', () => {
  test('invariants: Person JSON-LD with jobTitle, knowsAbout, sameAs', async ({
    page,
  }) => {
    await page.goto('/about/');

    const blocks = await extractJsonLd(page);
    const person = blocks.find((b) => b['@type'] === 'Person');
    expect(person, 'Person JSON-LD must be present on /about/').toBeDefined();

    expect(person!['jobTitle']).toBeTruthy();

    const knowsAbout = person!['knowsAbout'];
    expect(Array.isArray(knowsAbout)).toBe(true);
    expect((knowsAbout as string[]).length).toBeGreaterThanOrEqual(10);

    const sameAs = person!['sameAs'];
    expect(Array.isArray(sameAs)).toBe(true);
    expect((sameAs as string[]).length).toBe(3);
  });

  test('structural snapshot', async ({ page }) => {
    await page.goto('/about/');
    const structure = await captureStructure(page);
    expect(asJson(structure)).toMatchSnapshot('structure.json');
  });

  test('Person JSON-LD block snapshot', async ({ page }) => {
    await page.goto('/about/');
    const blocks = await extractJsonLd(page);
    const person = blocks.find((b) => b['@type'] === 'Person');
    expect(asJson(person)).toMatchSnapshot('jsonld-person.json');
  });

  test('visual: full page', async ({ page }) => {
    await page.goto('/about/');
    await page.waitForLoadState('domcontentloaded');
    await expect(page).toHaveScreenshot(`about-${process.platform}.png`, { fullPage: true });
  });
});
