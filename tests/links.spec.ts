import { expect, test } from '@playwright/test';

// Crawl `<a href>` on representative pages and confirm every internal link
// resolves with status < 400 (after redirects). Catches URL-rename mistakes
// like the pretty-URL flip (b319777) before they ship.

const SEED_PAGES = [
  '/',
  '/about/',
  '/archive/',
  '/tags/',
  '/2026/04/13/optipowertools-hangfire-2-0-cms-13-support/',
];

// Sample legacy URLs declared in post frontmatter `redirect_from:` blocks.
// These should 30x → canonical pretty URL → 200. If a post is renamed in a way
// that drops one of these, this test fails.
const REDIRECT_FROM_URLS = [
  '/2026/04/13/OptiPowerTools-Hangfire-2.0-CMS-13-Support.html',
  '/2026/04/13/optipowertools-hangfire-2-0-cms-13-support.html',
  '/2026/03/31/OptiPowerTools.Hangfire-A-Drop-in-Hangfire-Integration-for-Optimizely-CMS-12.html',
  '/2026/03/03/Catalog-Traversal-with-Hangfire-Part-3-Advanced-Job-Management.html',
];

test.describe('internal links', () => {
  test('every <a href> on seed pages returns < 400', async ({
    page,
    request,
    baseURL,
  }) => {
    const internalLinks = new Set<string>();

    for (const seed of SEED_PAGES) {
      await page.goto(seed);
      await page.waitForLoadState('domcontentloaded');
      const hrefs = await page.evaluate(() =>
        Array.from(document.querySelectorAll('a[href]')).map(
          (a) => (a as HTMLAnchorElement).href,
        ),
      );
      for (const href of hrefs) {
        if (!href.startsWith(baseURL!)) continue;
        const url = new URL(href);
        if (!url.pathname) continue;
        internalLinks.add(url.pathname + url.search);
      }
    }

    const broken: string[] = [];
    for (const link of [...internalLinks].sort()) {
      const resp = await request.get(link, { maxRedirects: 5 });
      if (resp.status() >= 400) {
        broken.push(`${link} -> ${resp.status()}`);
      }
    }

    expect(broken, 'broken internal links').toEqual([]);
  });

  test('redirect_from URLs (legacy .html) resolve to canonical pretty URL', async ({
    request,
  }) => {
    const broken: string[] = [];
    for (const url of REDIRECT_FROM_URLS) {
      const resp = await request.get(url, { maxRedirects: 5 });
      if (resp.status() >= 400) {
        broken.push(`${url} -> ${resp.status()}`);
      }
    }
    expect(broken, 'broken redirect_from URLs').toEqual([]);
  });
});
