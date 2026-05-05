import type { Page } from '@playwright/test';

export interface PageMeta {
  title: string;
  description: string | null;
  canonical: string | null;
  ogTitle: string | null;
  ogDescription: string | null;
  ogImage: string | null;
  ogType: string | null;
  twitterCard: string | null;
  twitterTitle: string | null;
}

export async function extractMeta(page: Page): Promise<PageMeta> {
  return page.evaluate(() => {
    const content = (sel: string) =>
      document.querySelector(sel)?.getAttribute('content') ?? null;
    return {
      title: document.title,
      description: content('meta[name="description"]'),
      canonical:
        document.querySelector('link[rel="canonical"]')?.getAttribute('href') ?? null,
      ogTitle: content('meta[property="og:title"]'),
      ogDescription: content('meta[property="og:description"]'),
      ogImage: content('meta[property="og:image"]'),
      ogType: content('meta[property="og:type"]'),
      twitterCard: content('meta[name="twitter:card"]'),
      twitterTitle: content('meta[name="twitter:title"]'),
    };
  });
}

export type JsonLdBlock = Record<string, unknown> & {
  '@type'?: string | string[];
};

export async function extractJsonLd(page: Page): Promise<JsonLdBlock[]> {
  const raw = await page.evaluate(() =>
    Array.from(document.querySelectorAll('script[type="application/ld+json"]')).map(
      (s) => s.textContent ?? '',
    ),
  );
  const parsed: JsonLdBlock[] = [];
  for (const text of raw) {
    if (!text.trim()) continue;
    try {
      const obj = JSON.parse(text);
      if (Array.isArray(obj)) {
        parsed.push(...obj);
      } else if (Array.isArray((obj as { '@graph'?: unknown })['@graph'])) {
        parsed.push(...((obj as { '@graph': JsonLdBlock[] })['@graph']));
      } else {
        parsed.push(obj);
      }
    } catch {
      parsed.push({ '@type': 'INVALID_JSON', raw: text.slice(0, 200) });
    }
  }
  return parsed.sort((a, b) => typeKey(a).localeCompare(typeKey(b)));
}

function typeKey(b: JsonLdBlock): string {
  const t = b['@type'];
  if (Array.isArray(t)) return t.join(',');
  return typeof t === 'string' ? t : '';
}

export function jsonLdTypes(blocks: JsonLdBlock[]): string[] {
  return blocks
    .flatMap((b) => (Array.isArray(b['@type']) ? b['@type'] : [b['@type']]))
    .filter((t): t is string => typeof t === 'string')
    .sort();
}

export interface NavLink {
  label: string;
  href: string;
}

export async function extractNav(page: Page): Promise<NavLink[]> {
  return page.evaluate(() =>
    Array.from(document.querySelectorAll('nav.site-nav a')).map((a) => ({
      label: (a.textContent ?? '').trim(),
      href: new URL((a as HTMLAnchorElement).href).pathname,
    })),
  );
}

export async function extractFooterSocial(page: Page): Promise<string[]> {
  return page.evaluate(() =>
    Array.from(document.querySelectorAll('p.footer-social a[href]')).map(
      (a) => (a as HTMLAnchorElement).href,
    ),
  );
}

export interface Breadcrumb {
  position: number;
  name: string;
  item: string;
}

export function getBreadcrumbs(blocks: JsonLdBlock[]): Breadcrumb[] | null {
  const bc = blocks.find((b) => b['@type'] === 'BreadcrumbList');
  if (!bc) return null;
  const items = bc['itemListElement'];
  if (!Array.isArray(items)) return null;
  return items.map((i) => ({
    position: (i as { position: number }).position,
    name: (i as { name: string }).name,
    item: (i as { item: string }).item,
  }));
}

export interface StructuralSnapshot {
  meta: PageMeta;
  nav: NavLink[];
  footerSocial: string[];
  jsonLdTypes: string[];
}

export async function captureStructure(page: Page): Promise<StructuralSnapshot> {
  const [meta, nav, footerSocial, blocks] = await Promise.all([
    extractMeta(page),
    extractNav(page),
    extractFooterSocial(page),
    extractJsonLd(page),
  ]);
  return {
    meta,
    nav,
    footerSocial,
    jsonLdTypes: jsonLdTypes(blocks),
  };
}

export function asJson(value: unknown): string {
  return JSON.stringify(value, null, 2);
}

const VOLATILE_KEYS = new Set(['dateModified', 'datePublished']);

export function stripVolatileFields<T>(value: T): T {
  if (Array.isArray(value)) return value.map(stripVolatileFields) as unknown as T;
  if (value && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      if (VOLATILE_KEYS.has(k)) continue;
      out[k] = stripVolatileFields(v);
    }
    return out as T;
  }
  return value;
}
