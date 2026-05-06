import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

// Pages chosen to cover every distinct layout / template once: post layout,
// tagpage layout, custom default-rendered pages, and the 404. Adding more
// posts/tags here would just retest the same templates.
const PAGES_TO_AUDIT = [
  { path: '/', name: 'home' },
  { path: '/about/', name: 'about' },
  { path: '/archive/', name: 'archive' },
  { path: '/tags/', name: 'tags index' },
  { path: '/tags/hangfire.html', name: 'tag page' },
  {
    path: '/2026/04/13/optipowertools-hangfire-2-0-cms-13-support/',
    name: 'post',
  },
  { path: '/404.html', name: '404' },
];

test.describe('accessibility (axe-core, WCAG 2.1 AA)', () => {
  for (const { path, name } of PAGES_TO_AUDIT) {
    test(`${name} (${path}) has no critical or serious violations`, async ({
      page,
    }) => {
      await page.goto(path);
      await page.waitForLoadState('domcontentloaded');

      const results = await new AxeBuilder({ page })
        .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
        // Third-party embeds we don't control — Credly renders its own badge
        // markup with hard-coded colors, Disqus injects a thread iframe.
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
        nodes: v.nodes.map((n) => ({
          html: n.html,
          target: n.target,
        })),
      }));

      expect(formatted, `axe-core violations on ${path}`).toEqual([]);
    });
  }
});
