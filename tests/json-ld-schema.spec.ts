import { expect, test } from '@playwright/test';
import { extractJsonLd, type JsonLdBlock } from './fixtures';

// Lightweight per-type validation. Existing structural/snapshot tests catch
// "did the JSON-LD change?". This catches "is the JSON-LD still semantically
// valid?" — Google's required fields for each rich-result type, basic
// type-shape checks (ISO 8601 dates, non-empty arrays, etc.). Cheaper than a
// full ajv+schema-dts setup, sufficient to flag regressions.

interface Rule {
  type: string;
  required: string[];
  validators?: Record<string, (value: unknown) => string | null>;
}

const isIsoDate = (v: unknown): boolean =>
  typeof v === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/.test(v);

const isNonEmptyString = (v: unknown): boolean =>
  typeof v === 'string' && v.length > 0;

const isUrl = (v: unknown): boolean =>
  typeof v === 'string' && /^https?:\/\//.test(v);

const RULES: Rule[] = [
  {
    type: 'BlogPosting',
    required: [
      'headline',
      'author',
      'datePublished',
      'dateModified',
      'description',
      'image',
      'mainEntityOfPage',
      'inLanguage',
    ],
    validators: {
      datePublished: (v) => (isIsoDate(v) ? null : 'must be ISO 8601 datetime'),
      dateModified: (v) => (isIsoDate(v) ? null : 'must be ISO 8601 datetime'),
      headline: (v) =>
        isNonEmptyString(v) && (v as string).length <= 110
          ? null
          : 'must be a non-empty string ≤ 110 chars',
      description: (v) =>
        isNonEmptyString(v) ? null : 'must be a non-empty string',
      image: (v) =>
        v && typeof v === 'object' && isUrl((v as { url?: unknown }).url)
          ? null
          : 'must be an ImageObject with a url',
    },
  },
  {
    type: 'Person',
    required: ['name', 'url', 'image', 'sameAs', 'jobTitle', 'knowsAbout'],
    validators: {
      name: (v) => (isNonEmptyString(v) ? null : 'must be a non-empty string'),
      url: (v) => (isUrl(v) ? null : 'must be an absolute URL'),
      sameAs: (v) =>
        Array.isArray(v) && v.length > 0 && v.every(isUrl)
          ? null
          : 'must be a non-empty array of absolute URLs',
      knowsAbout: (v) =>
        Array.isArray(v) && v.length > 0 ? null : 'must be a non-empty array',
    },
  },
  {
    type: 'BreadcrumbList',
    required: ['itemListElement'],
    validators: {
      itemListElement: (v) => {
        if (!Array.isArray(v) || v.length < 2) {
          return 'must be an array of at least 2 ListItems';
        }
        for (const item of v) {
          if (!item || typeof item !== 'object') return 'list items must be objects';
          const li = item as Record<string, unknown>;
          if (li['@type'] !== 'ListItem') return 'list items must be @type=ListItem';
          if (typeof li.position !== 'number') return 'every ListItem needs a numeric position';
          if (!isNonEmptyString(li.name)) return 'every ListItem needs a name';
        }
        return null;
      },
    },
  },
  {
    type: 'CollectionPage',
    required: ['name', 'url', 'inLanguage', 'isPartOf'],
    validators: {
      name: (v) => (isNonEmptyString(v) ? null : 'must be a non-empty string'),
      url: (v) => (isUrl(v) ? null : 'must be an absolute URL'),
    },
  },
];

const blockType = (b: JsonLdBlock): string =>
  Array.isArray(b['@type']) ? b['@type'][0] : (b['@type'] ?? '');

function validateBlock(block: JsonLdBlock, rule: Rule): string[] {
  const errs: string[] = [];
  for (const key of rule.required) {
    if ((block as Record<string, unknown>)[key] === undefined) {
      errs.push(`missing required property: ${key}`);
    }
  }
  if (rule.validators) {
    for (const [key, validator] of Object.entries(rule.validators)) {
      const value = (block as Record<string, unknown>)[key];
      if (value !== undefined) {
        const err = validator(value);
        if (err) errs.push(`${key}: ${err}`);
      }
    }
  }
  return errs;
}

interface Page {
  path: string;
  name: string;
  expectedTypes: string[];
}

const PAGES: Page[] = [
  {
    path: '/',
    name: 'home',
    expectedTypes: ['WebSite', 'Person', 'WebPage'],
  },
  {
    path: '/about/',
    name: 'about',
    expectedTypes: ['WebSite', 'Person', 'ProfilePage', 'BreadcrumbList'],
  },
  {
    path: '/2026/04/13/optipowertools-hangfire-2-0-cms-13-support/',
    name: 'post',
    expectedTypes: ['WebSite', 'Person', 'BlogPosting', 'BreadcrumbList'],
  },
  {
    path: '/tags/',
    name: 'tags index',
    expectedTypes: ['WebSite', 'Person', 'CollectionPage', 'BreadcrumbList'],
  },
  {
    path: '/tags/hangfire.html',
    name: 'tag page',
    expectedTypes: ['WebSite', 'Person', 'CollectionPage', 'BreadcrumbList'],
  },
];

test.describe('JSON-LD schema validation', () => {
  for (const { path, name, expectedTypes } of PAGES) {
    test(`${name} (${path}) JSON-LD passes per-type rules`, async ({ page }) => {
      await page.goto(path);
      await page.waitForLoadState('domcontentloaded');
      const blocks = await extractJsonLd(page);

      // Every expected type must be emitted at least once.
      const presentTypes = new Set(blocks.map(blockType));
      for (const t of expectedTypes) {
        expect(presentTypes.has(t), `${t} block missing on ${path}`).toBe(true);
      }

      // Validate each block against its rule when one exists.
      const errors: string[] = [];
      for (const block of blocks) {
        const rule = RULES.find((r) => r.type === blockType(block));
        if (!rule) continue;
        const blockErrors = validateBlock(block, rule);
        if (blockErrors.length) {
          errors.push(`[${rule.type}] ${blockErrors.join('; ')}`);
        }
      }

      expect(errors, `validation errors on ${path}`).toEqual([]);
    });
  }
});
